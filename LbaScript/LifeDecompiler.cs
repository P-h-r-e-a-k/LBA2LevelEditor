using System.Text;

namespace LBA2LevelEditor.LbaScript;

// Cross-script names for operands that point into another actor's script (or
// this actor's *track* from its life script): SET_TRACK / SET_TRACK_OBJ take a
// byte offset into a track script, SET_COMPORTEMENT_OBJ one into another
// life script. Both directions are needed: decompile turns offsets into names
// (offset -> name), compile turns names back into the target's current layout.
internal interface ISymbolSource
{
    string? TrackName(int actor, int offset);
    string? LifeName(int actor, int offset);
    int? TrackOffset(int actor, string name);
    int? LifeOffset(int actor, string name);
}

internal sealed class NoSymbols : ISymbolSource
{
    public static readonly NoSymbols Instance = new();
    public string? TrackName(int actor, int offset) => null;
    public string? LifeName(int actor, int offset) => null;
    public int? TrackOffset(int actor, string name) => null;
    public int? LifeOffset(int actor, string name) => null;
}

// Thrown while structuring a segment when its control flow doesn't fit the
// if/else / while / switch templates; the segment is then written in the flat
// goto/label form instead, which represents any script exactly.
internal sealed class Unstructured : Exception
{
    public Unstructured(string why) : base(why) { }
}

// The C-style life-script language.
//
//   void comportement_0()
//   {
//       if (2 == zone_obj(0))
//       {
//           set_door_down(1024);
//           set_comportement(comportement_1);
//       }
//       else
//       {
//           set_track(label_1);
//       }
//   }
//
// Style: braces on their own lines (Allman), `else` aligned with its `if`,
// comparisons with the literal on the left (a literal on the right compiles
// with a warning), function names lowercase (ScriptStyle.LowercaseNames).
//
// A life script is a sequence of comportement blocks, each ended by
// END_COMPORTEMENT (implicit at the closing brace), followed by an optional
// tail of statements outside any function, and a final END (implicit). There is
// no COMPORTEMENT header opcode: a block is identified only by its byte
// offset, which SET_COMPORTEMENT stores. Functions here are those blocks, and
// SET_COMPORTEMENT(comportement_N) names one -- it selects what runs on the
// actor's *next* tick, it is not a call.
internal static partial class LifeText
{
    internal const byte OpEnd = 0, OpSnif = 2, OpOffset = 3, OpNeverIf = 4, OpReturn = 11, OpIf = 12, OpSwIf = 13, OpOneIf = 14,
        OpElse = 15, OpEndComportement = 35, OpOrIf = 55, OpAndIf = 112, OpSwitch = 113, OpOrCase = 114, OpCase = 115,
        OpDefault = 116, OpBreak = 117, OpEndSwitch = 118, OpSetComportement = 33, OpSetComportementObj = 34,
        OpSetTrack = 23, OpSetTrackObj = 24;

    internal static bool IsChainPrefix(byte op) => op is OpOrIf or OpAndIf;
    internal static bool IsTerminal(byte op) => op is OpIf or OpSwIf or OpOneIf or OpSnif or OpNeverIf;

    internal static string IfKeyword(byte op) => op switch
    {
        OpIf => "if", OpSwIf => "swif", OpOneIf => "oneif", OpSnif => "snif", OpNeverIf => "neverif",
        _ => throw new InvalidOperationException(),
    };

    internal static byte IfOpcode(string kw) => kw switch
    {
        "if" => OpIf, "swif" => OpSwIf, "oneif" => OpOneIf, "snif" => OpSnif, "neverif" => OpNeverIf,
        _ => throw new InvalidOperationException(),
    };

    internal static string ComportementName(int ordinal) => $"comportement_{ordinal}";

    // [From, To) are instruction indices of the statements; if Terminated,
    // code[To] is the END_COMPORTEMENT that closes the function.
    internal sealed record Segment(int From, int To, bool Terminated, int Ordinal);

    internal static List<Segment> Segments(IReadOnlyList<Instr> code, out int scriptEnd)
    {
        scriptEnd = code.Count > 0 && code[^1].Op == OpEnd ? code.Count - 1 : code.Count;
        var list = new List<Segment>();
        var from = 0;
        var ordinal = 0;
        for (var i = 0; i < scriptEnd; i++)
        {
            if (code[i].Op != OpEndComportement) continue;
            list.Add(new Segment(from, i, true, ordinal++));
            from = i + 1;
        }
        if (from < scriptEnd) list.Add(new Segment(from, scriptEnd, false, -1));
        return list;
    }

    // offset of each terminated block's first instruction -> its function name.
    // Used both here and by other actors' scripts that reference this one.
    internal static Dictionary<int, string> FunctionNames(IReadOnlyList<Instr> code)
    {
        var names = new Dictionary<int, string>();
        foreach (var s in Segments(code, out _))
            if (s.Terminated) names[code[s.From].Offset] = ComportementName(s.Ordinal);
        return names;
    }

    // ---------------------------------------------------------------------
    public static string Decompile(byte[] bytes, int actor, ISymbolSource syms, string? header = null, Action<string>? diag = null) =>
        DecompileMapped(bytes, actor, syms, header, diag).Text;

    // Same, but also reports which line each instruction was printed on.
    internal static DecompiledScript DecompileMapped(byte[] bytes, int actor, ISymbolSource syms, string? header = null, Action<string>? diag = null)
    {
        var code = Bytecode.DecodeLife(bytes);
        var segs = Segments(code, out var scriptEnd);
        var flat = new bool[segs.Count];

        for (var attempt = 0; attempt <= segs.Count + 2; attempt++)
        {
            DecompiledScript result;
            try
            {
                result = new Renderer(code, bytes.Length, segs, flat, actor, syms).Render(header);
            }
            catch (UnstructuredSegment u)
            {
                diag?.Invoke($"segment {u.Segment} written flat: {u.Message}");
                flat[u.Segment] = true;
                continue;
            }

            // Self-check: the text must compile back to exactly these bytes.
            int mismatchAt;
            try
            {
                var compiled = Compile(result.Text, actor, syms);
                compiled.ResolveExternals(r => ResolveExternal(r, actor, syms, compiled));
                var back = compiled.Bytes;
                mismatchAt = FirstDifference(back, bytes);
                if (mismatchAt < 0) return result;
            }
            catch (ScriptCompileException)
            {
                mismatchAt = 0;
            }

            // Flatten the segment containing the first differing byte; if it is
            // already flat (or unlocatable) flatten everything.
            var seg = SegmentAt(code, segs, mismatchAt);
            diag?.Invoke($"recompiled bytes differ at {mismatchAt} (segment {seg}); flattening");
            if (seg >= 0 && !flat[seg]) flat[seg] = true;
            else Array.Fill(flat, true);
        }
        throw new InvalidOperationException("Life decompiler could not produce round-trippable text (this is a bug).");
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }

    private static int SegmentAt(List<Instr> code, List<Segment> segs, int offset)
    {
        for (var k = 0; k < segs.Count; k++)
        {
            var s = segs[k];
            var start = s.From < code.Count ? code[s.From].Offset : int.MaxValue;
            var endIns = s.Terminated ? s.To : s.To - 1;
            var end = code[Math.Min(endIns, code.Count - 1)].Offset + code[Math.Min(endIns, code.Count - 1)].Length;
            if (offset >= start && offset < end) return k;
        }
        return -1;
    }

    private sealed class UnstructuredSegment : Exception
    {
        public int Segment { get; }
        public UnstructuredSegment(int segment, string why) : base(why) => Segment = segment;
    }

    // ---------------------------------------------------------------------
    private sealed class Renderer
    {
        private readonly List<Instr> code;
        private readonly int totalLength;
        private readonly List<Segment> segs;
        private readonly bool[] flat;
        private readonly int actor;
        private readonly ISymbolSource syms;
        private readonly Dictionary<int, int> indexByOffset = new();
        private readonly Dictionary<int, string> blockNames = new();
        private readonly Dictionary<int, string> labelNames = new();
        private readonly HashSet<int> placed = new();
        private readonly StringBuilder sb = new();
        private int indent;

        // Line bookkeeping for DecompiledScript (see there).
        private int lineNo;
        private readonly int[] beforeLine;
        private readonly int[] codeLine;
        private readonly int[] endLine;
        private readonly Dictionary<int, int> blockHeaderLine = new();

        public Renderer(List<Instr> code, int totalLength, List<Segment> segs, bool[] flat, int actor, ISymbolSource syms)
        {
            this.code = code; this.totalLength = totalLength; this.segs = segs; this.flat = flat; this.actor = actor; this.syms = syms;
            beforeLine = new int[code.Count]; codeLine = new int[code.Count]; endLine = new int[code.Count];
            Array.Fill(beforeLine, -1); Array.Fill(codeLine, -1); Array.Fill(endLine, -1);
            for (var i = 0; i < code.Count; i++) indexByOffset[code[i].Offset] = i;

            foreach (var s in segs)
                if (s.Terminated) blockNames[OffsetAt(s.From)] = ComportementName(s.Ordinal);

            // Labels: every resume target that is not a block start, plus (for
            // segments written flat) every jump target inside them.
            // (Only offsets that really are instruction boundaries can carry a
            // label; anything else stays a raw @offset.)
            var needed = new SortedSet<int>();
            void Need(int off)
            {
                if (!blockNames.ContainsKey(off) && indexByOffset.ContainsKey(off)) needed.Add(off);
            }
            foreach (var i in code)
            {
                if (i.Op == OpSetComportement) Need((int)i.A[0]);
                if (i.Op == OpSetComportementObj && (int)i.A[0] == actor) Need((int)i.A[1]);
            }
            for (var k = 0; k < segs.Count; k++)
            {
                if (!flat[k]) continue;
                var s = segs[k];
                for (var ix = s.From; ix < s.To; ix++)
                    if (Bytecode.TryGetTarget(ScriptKind.Life, code[ix], out var t)) Need(t);
            }
            var n = 0;
            foreach (var off in needed) labelNames[off] = $"L{++n}";
        }

        private int OffsetAt(int index) => index < code.Count ? code[index].Offset : totalLength;

        private int IndexOf(int offset)
        {
            if (indexByOffset.TryGetValue(offset, out var ix)) return ix;
            if (offset == totalLength) return code.Count;
            throw new Unstructured($"jump target {offset} is not an instruction boundary");
        }

        private void Line(string s, int extraIndent = 0)
        {
            sb.Append(' ', Math.Max(0, indent + extraIndent) * 4).Append(s).Append('\n');
            lineNo++;
        }

        // Instruction i is printed on the line about to be written, by itself.
        private void Mark(int i)
        {
            beforeLine[i] = codeLine[i] = endLine[i] = lineNo;
        }

        private bool EndsWithBlankLine() => sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '\n';

        public DecompiledScript Render(string? header)
        {
            if (header is not null) { sb.Append("// ").Append(header).Append('\n'); lineNo++; }

            for (var k = 0; k < segs.Count; k++)
            {
                var s = segs[k];
                var startOffset = OffsetAt(s.From);
                var endOffset = OffsetAt(s.To);

                try
                {
                    if (sb.Length > 0 && !EndsWithBlankLine()) { sb.Append('\n'); lineNo++; }

                    if (s.Terminated)
                    {
                        blockHeaderLine[s.From] = lineNo;
                        Line($"void {ComportementName(s.Ordinal)}()");
                        Line("{");
                        indent++;
                    }

                    if (flat[k]) EmitFlat(s.From, s.To);
                    else EmitRange(s.From, s.To, null);

                    // A label sitting exactly at the segment's end (before the
                    // closing brace / end of the tail).
                    if (s.Terminated) beforeLine[s.To] = lineNo;
                    PlaceLabel(endOffset);

                    if (s.Terminated)
                    {
                        indent--;
                        codeLine[s.To] = endLine[s.To] = lineNo;
                        Line("}");
                    }

                    // Every label inside this segment must have been written.
                    foreach (var (off, name) in labelNames)
                        if (off >= startOffset && off <= endOffset && !placed.Contains(off))
                            throw new Unstructured($"label {name} at {off} could not be placed");
                }
                catch (Unstructured u)
                {
                    if (flat[k]) throw new InvalidOperationException($"flat segment failed: {u.Message}");
                    throw new UnstructuredSegment(k, u.Message);
                }
            }
            return new DecompiledScript
            {
                Text = sb.ToString(),
                Code = code,
                BeforeLine = beforeLine,
                CodeLine = codeLine,
                EndLine = endLine,
                BlockHeaderLine = blockHeaderLine,
            };
        }

        private void PlaceLabel(int offset)
        {
            if (labelNames.TryGetValue(offset, out var name) && placed.Add(offset))
                Line($"{name}:", -1);   // labels sit one level left of the statements they precede
        }

        // ---- flat ----------------------------------------------------------
        private void EmitFlat(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                beforeLine[i] = lineNo;
                PlaceLabel(code[i].Offset);
                codeLine[i] = lineNo;
                Line(FlatText(code[i]));
                endLine[i] = lineNo - 1;
            }
        }

        private string TargetName(int offset) =>
            blockNames.TryGetValue(offset, out var b) ? b :
            labelNames.TryGetValue(offset, out var l) ? l :
            $"@{offset}";

        private string FlatText(Instr ins)
        {
            var def = Opcodes.Life(ins.Op)!;
            switch (def.Form)
            {
                case LifeForm.Cond:
                    return $"{def.Name}({ExprText.PrintLeaf(LeafOf(ins))}, {TargetName(ins.Target)});";
                case LifeForm.Switch:
                    return $"SWITCH({ExprText.FuncCall(Opcodes.Cond(ins.Func)!, ins.FuncArg)});";
                case LifeForm.Case:
                    return $"{def.Name}({CaseLabel(ins)}, {TargetName(ins.Target)});";
            }
            if (ins.Op == OpReturn) return "return;";
            if (ins.Op == OpOffset) return $"goto {TargetName((int)ins.A[0])};";
            return PlainText(ins, def);
        }

        private static Leaf LeafOf(Instr c) => new(Opcodes.Cond(c.Func)!, c.FuncArg, c.Test, c.Value);

        private static string CaseLabel(Instr c) => (c.Test == 0 ? "" : Opcodes.TestSymbols[c.Test] + " ") + c.Value;

        // "NAME(args);" for any Plain / Dir instruction.
        private string PlainText(Instr ins, LifeOpDef def)
        {
            var args = Operands.Format(def.Args, ins, (ix, d) => SymbolFor(ins, def, ix, d), null);
            // SET_DIR / SET_DIR_OBJ: name the move mode.
            if (def.Form == LifeForm.Dir) args = FormatDir(ins, def);
            return $"{ScriptStyle.Func(def.Name)}({args});";
        }

        private static string FormatDir(Instr ins, LifeOpDef def)
        {
            var parts = new List<string>();
            var moveIx = def.Args.Length - 1;
            for (var i = 0; i < ins.A.Length; i++)
            {
                if (i == moveIx) parts.Add(ins.A[i] >= 0 && ins.A[i] < Opcodes.MoveNames.Length ? Opcodes.MoveNames[ins.A[i]] : ins.A[i].ToString());
                else parts.Add(ins.A[i].ToString());
            }
            return string.Join(", ", parts);
        }

        private string? SymbolFor(Instr ins, LifeOpDef def, int ix, ArgDef d)
        {
            if (d.Type == ArgType.Jump) return TargetName((int)ins.A[ix]);
            switch (d.Role)
            {
                case ArgRole.LifeOffset:
                {
                    var off = (int)ins.A[ix];
                    var target = def.Id == OpSetComportementObj ? (int)ins.A[0] : actor;
                    if (target == actor) return blockNames.TryGetValue(off, out var b) ? b : labelNames.TryGetValue(off, out var l) ? l : $"@{off}";
                    return syms.LifeName(target, off) ?? $"@{off}";
                }
                case ArgRole.TrackOffset:
                {
                    var off = (int)ins.A[ix];
                    var target = def.Id == OpSetTrackObj ? (int)ins.A[0] : actor;
                    return syms.TrackName(target, off) ?? $"@{off}";
                }
                default:
                    return null;
            }
        }

        // ---- structured ---------------------------------------------------
        // Emits code[from..to) as C statements. breakTarget is the byte offset
        // a BREAK must jump to for it to read as `break;` (the enclosing switch's END_SWITCH).
        private void EmitRange(int from, int to, int? breakTarget)
        {
            var i = from;
            while (i < to)
            {
                var ins = code[i];
                beforeLine[i] = lineNo;
                PlaceLabel(ins.Offset);
                codeLine[i] = lineNo;

                if (IsChainPrefix(ins.Op) || IsTerminal(ins.Op)) { var last = EmitIf(i, to, breakTarget); endLine[i] = lineNo - 1; i = last + 1; continue; }
                if (ins.Op == OpSwitch) { var last = EmitSwitch(i, to); endLine[i] = lineNo - 1; i = last + 1; continue; }

                switch (ins.Op)
                {
                    case OpReturn: Line("return;"); break;
                    case OpBreak:
                        if (breakTarget is null || (int)ins.A[0] != breakTarget) throw new Unstructured("BREAK outside its switch");
                        Line("break;");
                        break;
                    case OpElse: case OpOffset: case OpCase: case OpOrCase: case OpDefault: case OpEndSwitch: case OpEndComportement: case OpEnd:
                        throw new Unstructured($"unexpected {Opcodes.Life(ins.Op)!.Name} in statement position");
                    default:
                        Line(PlainText(ins, Opcodes.Life(ins.Op)!));
                        break;
                }
                endLine[i] = lineNo - 1;
                i++;
            }
        }

        // Returns the index of the last instruction consumed.
        // The recognised shape of one if / if-else / while statement starting
        // at code[I]: the condition chain is code[I..J] (J = the terminating
        // IF), the body is [J+1, ThenEnd), and the else body (if any) is
        // [ElseFrom, ElseEnd). Last is the final instruction the statement consumes.
        private sealed record IfShape(int I, int J, Instr Term, Expr Cond, int ThenEnd, int ElseFrom, int ElseEnd, bool IsWhile, int Last)
        {
            public bool HasElse => ElseEnd >= 0;
        }

        private IfShape AnalyzeIf(int i, int to)
        {
            var j = i;
            while (j < to && IsChainPrefix(code[j].Op)) j++;
            if (j >= to || !IsTerminal(code[j].Op)) throw new Unstructured("condition chain without a terminating IF");
            var term = code[j];
            var chainStart = code[i].Offset;

            for (var k = i + 1; k <= j; k++)
                if (labelNames.ContainsKey(code[k].Offset)) throw new Unstructured("label inside a condition chain");

            var falseOff = term.Target;
            if (falseOff <= term.Offset) throw new Unstructured("backward IF target");
            var fi = IndexOf(falseOff);
            if (fi <= j || fi > to) throw new Unstructured("IF target outside the enclosing block");
            var bodyStart = term.Offset + term.Length;

            var chain = code.GetRange(i, j - i + 1);
            var expr = BuildExpr(chain, 0, falseOff, bodyStart);

            var thenEnd = fi;
            var elseEnd = -1;
            var isWhile = false;
            if (fi - 1 > j)
            {
                var prev = code[fi - 1];
                if (prev.Op == OpElse)
                {
                    var ei = IndexOf((int)prev.A[0]);
                    if (ei < fi || ei > to) throw new Unstructured("ELSE target outside its block");
                    thenEnd = fi - 1;
                    elseEnd = ei;
                }
                else if (prev.Op == OpOffset && (int)prev.A[0] == chainStart && term.Op == OpIf)
                {
                    thenEnd = fi - 1;
                    isWhile = true;
                }
            }
            return new IfShape(i, j, term, expr, thenEnd, fi, elseEnd, isWhile, elseEnd >= 0 ? elseEnd - 1 : fi - 1);
        }

        // Returns the index of the last instruction consumed. An else-branch
        // that is exactly one if-statement is written `else if (...)`.
        private int EmitIf(int i, int to, int? breakTarget)
        {
            var shape = AnalyzeIf(i, to);
            var result = shape.Last;
            var prefix = "";
            var pendingElse = -1;
            var visited = new List<IfShape>();

            // Every instruction of the condition chain is printed on the statement's first line.
            for (var k = i + 1; k <= shape.J; k++) { beforeLine[k] = beforeLine[i]; codeLine[k] = codeLine[i]; }

            void Finish()
            {
                foreach (var v in visited)
                    for (var k = v.I; k <= v.J; k++) endLine[k] = lineNo - 1;
            }

            while (true)
            {
                visited.Add(shape);
                if (pendingElse >= 0)
                {
                    // "else if (...)": one line carries the ELSE and the inner chain.
                    Mark(pendingElse);
                    for (var k = shape.I; k <= shape.J; k++) beforeLine[k] = codeLine[k] = lineNo;
                    pendingElse = -1;
                }
                var kw = shape.IsWhile ? "while" : IfKeyword(shape.Term.Op);
                Line($"{prefix}{kw} ({ExprText.Print(shape.Cond)})");
                Line("{");
                indent++;
                EmitRange(shape.J + 1, shape.ThenEnd, breakTarget);
                indent--;

                if (!shape.HasElse)
                {
                    if (shape.IsWhile) Mark(shape.ThenEnd);   // the OFFSET back to the test sits on the closing brace
                    Line("}");
                    Finish();
                    return result;
                }

                Line("}");
                if (TryElseIf(shape, out var inner))
                {
                    // "else if (...)" is printed by the next iteration of this loop.
                    prefix = "else ";
                    pendingElse = shape.ThenEnd;
                    shape = inner;
                    continue;
                }

                Mark(shape.ThenEnd);
                Line("else");
                Line("{");
                indent++;
                EmitRange(shape.ElseFrom, shape.ElseEnd, breakTarget);
                indent--;
                Line("}");
                Finish();
                return result;
            }
        }

        // True when the else body [ElseFrom, ElseEnd) is a single if / swif /
        // oneif statement (not a while) with nothing else in it.
        private bool TryElseIf(IfShape outer, out IfShape inner)
        {
            inner = outer;
            var f = outer.ElseFrom;
            if (f >= outer.ElseEnd) return false;
            if (!IsChainPrefix(code[f].Op) && !IsTerminal(code[f].Op)) return false;
            if (labelNames.ContainsKey(code[f].Offset)) return false;
            try
            {
                var shape = AnalyzeIf(f, outer.ElseEnd);
                if (shape.IsWhile || shape.Last != outer.ElseEnd - 1) return false;
                inner = shape;
                return true;
            }
            catch (Unstructured)
            {
                return false;
            }
        }

        private static Leaf MakeLeaf(Instr c) => LeafOf(c);

        // Right-nested chains (OR_IF -> body start) and clause chains
        // (OR_IF ... AND_IF -> next clause) -> && / || expression.
        private static Expr BuildExpr(List<Instr> chain, int n, int falseOff, int bodyStart)
        {
            var c = chain[n];
            var lit = MakeLeaf(c);
            var last = n == chain.Count - 1;

            if (c.Op != OpOrIf)
            {
                if (c.Target != falseOff) throw new Unstructured("AND_IF target differs from the IF's false target");
                return last ? lit : new AndExpr(lit, BuildExpr(chain, n + 1, falseOff, bodyStart));
            }

            if (last) throw new Unstructured("OR_IF cannot end a chain");
            if (c.Target == bodyStart) return new OrExpr(lit, BuildExpr(chain, n + 1, falseOff, bodyStart));

            var m = chain.FindIndex(n + 1, x => x.Offset == c.Target);
            if (m < 0) throw new Unstructured("OR_IF target is not inside its chain");
            for (var k = n; k <= m - 2; k++)
                if (chain[k].Op != OpOrIf || chain[k].Target != c.Target) throw new Unstructured("irregular OR clause");
            var closer = chain[m - 1];
            if (closer.Op != OpAndIf || closer.Target != falseOff) throw new Unstructured("OR clause is not closed by an AND_IF");

            Expr clause = lit;
            for (var k = n + 1; k <= m - 1; k++) clause = new OrExpr(clause, MakeLeaf(chain[k]));
            return new AndExpr(clause, BuildExpr(chain, m, falseOff, bodyStart));
        }

        private int EmitSwitch(int i, int to)
        {
            var s = code[i];
            var depth = 0;
            var k = -1;
            for (var m = i + 1; m < to; m++)
            {
                if (code[m].Op == OpSwitch) depth++;
                else if (code[m].Op == OpEndSwitch)
                {
                    if (depth == 0) { k = m; break; }
                    depth--;
                }
            }
            if (k < 0) throw new Unstructured("SWITCH without END_SWITCH");
            var endOff = code[k].Offset;

            Line($"switch ({ExprText.FuncCall(Opcodes.Cond(s.Func)!, s.FuncArg)})");
            Line("{");
            indent++;   // case labels sit inside the braces
            var m2 = i + 1;
            while (m2 < k)
            {
                var ins = code[m2];
                if (ins.Op is OpOrCase or OpCase)
                {
                    var g = m2;
                    while (g < k && code[g].Op == OpOrCase) g++;
                    if (g >= k || code[g].Op != OpCase) throw new Unstructured("OR_CASE group without a CASE");
                    var caseIns = code[g];
                    var bodyStart = caseIns.Offset + caseIns.Length;
                    for (var q = m2; q < g; q++)
                        if (code[q].Target != bodyStart) throw new Unstructured("OR_CASE does not target its body");
                    for (var q = m2 + 1; q <= g; q++)
                        if (labelNames.ContainsKey(code[q].Offset)) throw new Unstructured("label inside a case group");
                    if (labelNames.ContainsKey(code[m2].Offset)) throw new Unstructured("label on a case");

                    var ni = IndexOf(caseIns.Target);
                    if (ni <= g || ni > k) throw new Unstructured("CASE target outside its switch");

                    for (var q = m2; q <= g; q++) { Mark(q); Line($"case {CaseLabel(code[q])}:"); }
                    indent++;
                    if (ni == g + 1) Line(";");
                    EmitRange(g + 1, ni, endOff);
                    indent--;
                    m2 = ni;
                }
                else if (ins.Op == OpDefault)
                {
                    if (labelNames.ContainsKey(ins.Offset)) throw new Unstructured("label on default");
                    Mark(m2);
                    Line("default:");
                    indent++;
                    EmitRange(m2 + 1, k, endOff);
                    indent--;
                    m2 = k;
                }
                else throw new Unstructured($"unexpected {Opcodes.Life(ins.Op)!.Name} directly inside a switch");
            }
            indent--;
            Mark(k);
            Line("}");
            return k;
        }
    }
}
