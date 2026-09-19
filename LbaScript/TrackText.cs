using System.Text;

namespace LBA2LevelEditor.LbaScript;

// A compiled script plus the named positions it defines (function names, C
// labels, LABEL(n) symbols) as byte offsets -- what other actors' scripts
// need to resolve cross-actor references like SET_TRACK_OBJ(5, label_1).
internal sealed class CompiledScript
{
    public required ScriptKind Kind { get; init; }
    public required byte[] Bytes { get; set; }
    public required IReadOnlyDictionary<string, int> Symbols { get; init; }
    public required Assembler Asm { get; init; }
    public List<ExternalRef> Pending { get; } = new();

    // Non-fatal problems found while compiling (e.g. a literal on the right of a comparison).
    public List<ScriptWarning> Warnings { get; } = new();

    // Life scripts: each function's header source line and the index of its first instruction.
    public List<(int HeaderLine, int FirstInstr)> Blocks { get; } = new();

    // Fills in operands that point into other scripts, then re-encodes.
    public void ResolveExternals(Func<ExternalRef, int?> resolve)
    {
        foreach (var r in Pending)
        {
            var v = resolve(r) ?? throw new ScriptCompileException($"Cannot resolve '{r.Symbol}' in actor {r.TargetActor}'s {r.TargetKind.ToString().ToLowerInvariant()} script", r.Line, r.Col);
            r.Ins.A[r.ArgIndex] = v;
        }
        Pending.Clear();
        Bytes = Asm.Reencode(Kind);
    }
}

// The C-style track-script language.
//
//   REM();
//   LABEL(0);            // defines the jump symbol label_0
//   SAMPLE(349);
//   OPEN_DOWN(1024);
//   WAIT_DOOR();
//   GOTO(label_0);       // jumps to the LABEL(0) statement
//   ...                  // (the closing END is implicit)
//
// Every statement is an opcode call, exactly one instruction each. LABEL(id)
// statements double as the jump targets of GOTO/LOOP and of SET_TRACK in life
// scripts. Label ids are not unique in shipped scripts, so a repeated id gets
// an occurrence suffix: the second LABEL(3) in a script is label_3_2.
internal static class TrackText
{
    public static string LabelSymbol(int id, int occurrence) => occurrence <= 1 ? $"label_{id}" : $"label_{id}_{occurrence}";

    // offset -> symbol for every LABEL instruction, in the naming scheme above.
    public static Dictionary<int, string> LabelNames(IReadOnlyList<Instr> code)
    {
        var seen = new Dictionary<long, int>();
        var names = new Dictionary<int, string>();
        foreach (var i in code)
        {
            if (i.Op != 9) continue;
            var id = i.A[0];
            seen[id] = seen.GetValueOrDefault(id) + 1;
            names[i.Offset] = LabelSymbol((int)id, seen[id]);
        }
        return names;
    }

    // Hidden (runtime scratch) operand values a freshly authored script has.
    // GERETRAK.CPP / GERELIFE.CPP's CleanTrack() resets exactly these.
    private static long HiddenDefault(byte op, long[] a, int argIndex) => op switch
    {
        6 => a[0],      // LOOP: counter starts at the loop count
        34 => -1,       // ANGLE_RND: state
        _ => 0,         // WAIT_NB_ANIM counter, WAIT_NB_* timers
    };

    // ---- decompile --------------------------------------------------------

    public static string Decompile(byte[] bytes, string? header = null) => DecompileMapped(bytes, header).Text;

    // Same, but also reports which line each instruction was printed on.
    internal static DecompiledScript DecompileMapped(byte[] bytes, string? header = null)
    {
        var code = Bytecode.DecodeTrack(bytes);
        var labelNames = LabelNames(code);
        var length = bytes.Length;

        // Jump targets that are not LABEL instructions get a C label.
        var extraLabels = new Dictionary<int, string>();
        foreach (var i in code)
            if (Bytecode.TryGetTarget(ScriptKind.Track, i, out var t) && !labelNames.ContainsKey(t) && t < length && !extraLabels.ContainsKey(t))
                extraLabels[t] = $"L{extraLabels.Count + 1}";

        string? Target(int t) =>
            labelNames.TryGetValue(t, out var n) ? n :
            extraLabels.TryGetValue(t, out n) ? n :
            $"@{t}";

        var end = code.Count > 0 && code[^1].Op == 0 ? code.Count - 1 : code.Count;
        var sb = new StringBuilder();
        var lineNo = 0;
        var before = new int[code.Count];
        var codeLine = new int[code.Count];
        var endLine = new int[code.Count];
        Array.Fill(before, -1); Array.Fill(codeLine, -1); Array.Fill(endLine, -1);
        if (header is not null) { sb.Append("// ").Append(header).Append('\n'); lineNo++; }

        for (var k = 0; k < end; k++)
        {
            var i = code[k];
            var def = Opcodes.Track(i.Op)!;
            if (i.Op == 9 && k > 0 && sb.Length > 0) { sb.Append('\n'); lineNo++; }
            before[k] = lineNo;
            if (extraLabels.TryGetValue(i.Offset, out var lbl)) { sb.Append(lbl).Append(":\n"); lineNo++; }
            codeLine[k] = endLine[k] = lineNo;

            var args = Operands.Format(def.Args, i,
                (ix, d) => d.Type == ArgType.Jump ? Target((int)i.A[ix]) : null,
                ix => HiddenDefault(i.Op, i.A, ix));
            sb.Append(ScriptStyle.Func(def.Name, life: false)).Append('(').Append(args).Append(");\n");
            lineNo++;
        }
        return new DecompiledScript
        {
            Text = sb.ToString(),
            Code = code,
            BeforeLine = before,
            CodeLine = codeLine,
            EndLine = endLine,
            BlockHeaderLine = new Dictionary<int, int>(),
        };
    }

    // ---- compile ----------------------------------------------------------

    public static CompiledScript Compile(string text)
    {
        var ts = new TokenStream(Lexer.Lex(text));
        var asm = new Assembler();
        var labelCount = new Dictionary<long, int>();
        var labelTokens = new Dictionary<Label, Token>();
        var refTokens = new Dictionary<Label, Token>();

        while (!ts.AtEnd)
        {
            var t = ts.Peek();
            if (t.Kind != TokKind.Ident) throw TokenStream.Error($"Expected a statement but found {t}", t);

            // "name:" -- a C label on the next instruction
            if (ts.Peek(1).Is(":"))
            {
                ts.Next(); ts.Next();
                asm.Define(t.Text, t);
                continue;
            }

            ts.Next();
            var def = Opcodes.Track(t.Text) ?? throw TokenStream.Error($"Unknown track opcode '{t.Text}'", t);
            var args = Operands.ParseArgList(ts);
            ts.Expect(";");
            var ins = BuildInstr(def, args, t, asm, refTokens);
            asm.CurrentLine = t.Line;
            asm.Emit(ins);

            if (def.Id == 9) // LABEL(id) also defines label_<id>
            {
                var id = ins.A[0];
                labelCount[id] = labelCount.GetValueOrDefault(id) + 1;
                var name = LabelSymbol((int)id, labelCount[id]);
                // bind *at* this LABEL instruction, i.e. before it was added
                var l = asm.Reference(name);
                if (l.Bound) throw TokenStream.Error($"'{name}' is already defined", t);
                l.Index = asm.Code.Count - 1;
                labelTokens[l] = t;
            }
        }

        asm.CurrentLine = 0;   // the implicit closing END belongs to no source line
        if (asm.Code.Count == 0 || asm.Code[^1].Op != 0)
            asm.Emit(new Instr { Op = 0 });

        var bytes = asm.Encode(ScriptKind.Track, l => refTokens.TryGetValue(l, out var tok) ? tok : null);
        var symbols = asm.Symbols.Where(p => p.Value.Bound).ToDictionary(p => p.Key, p => asm.OffsetOf(p.Value), StringComparer.Ordinal);
        return new CompiledScript { Kind = ScriptKind.Track, Bytes = bytes, Symbols = symbols, Asm = asm };
    }

    private static Instr BuildInstr(TrackOpDef def, List<ArgVal> args, Token at, Assembler asm, Dictionary<Label, Token> refTokens)
    {
        var visible = def.Args.Select((d, ix) => (d, ix)).Where(p => p.d.Role != ArgRole.Hidden).ToList();
        var hidden = def.Args.Select((d, ix) => (d, ix)).Where(p => p.d.Role == ArgRole.Hidden).ToList();

        if (args.Count != visible.Count && args.Count != visible.Count + hidden.Count)
            throw TokenStream.Error($"{def.Name} takes {visible.Count} argument(s) but {args.Count} were given", at);

        var ins = new Instr { Op = def.Id, A = new long[def.Args.Length] };
        for (var k = 0; k < visible.Count; k++)
        {
            var (d, ix) = visible[k];
            var a = args[k];
            if (d.Type == ArgType.CStr)
            {
                ins.Str = a.Str ?? throw TokenStream.Error($"'{d.Name}' must be a string", a.At);
                if (ins.Str.Contains('\0')) throw TokenStream.Error("String may not contain NUL", a.At);
            }
            else if (d.Type == ArgType.Jump)
            {
                if (a.Ident is not null)
                {
                    var l = asm.Reference(a.Ident);
                    refTokens.TryAdd(l, a.At);
                    asm.Jump(ins, l);
                }
                else if (a.Raw) { Operands.CheckRange(ArgType.Jump, a.Num, a.At, d.Name); ins.A[ix] = a.Num; }
                else throw TokenStream.Error($"'{d.Name}' must be a label name (or @offset)", a.At);
            }
            else
            {
                var v = Operands.RequireNumber(a, d.Name);
                Operands.CheckRange(d.Type, v, a.At, d.Name);
                ins.A[ix] = v;
            }
        }

        for (var k = 0; k < hidden.Count; k++)
        {
            var (d, ix) = hidden[k];
            if (args.Count == visible.Count + hidden.Count)
            {
                var a = args[visible.Count + k];
                ins.A[ix] = Operands.RequireNumber(a, d.Name);
            }
            else ins.A[ix] = HiddenDefault(def.Id, ins.A, ix);
        }
        return ins;
    }
}
