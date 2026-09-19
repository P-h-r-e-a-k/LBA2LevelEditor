namespace LBA2LevelEditor.LbaScript;

internal static partial class LifeText
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "while", "switch", "case", "default", "break", "return", "goto", "void", "swif", "oneif", "snif", "neverif",
    };

    public static CompiledScript Compile(string text, int actor, ISymbolSource syms) => new Parser(text, actor).Run();

    // Resolves one cross-script operand against `syms` (or, for this script's
    // own labels, against its freshly compiled symbol table).
    internal static int? ResolveExternal(ExternalRef r, int actor, ISymbolSource syms, CompiledScript self)
    {
        if (r.TargetKind == ScriptKind.Track) return syms.TrackOffset(r.TargetActor, r.Symbol);
        if (r.TargetActor == actor) return self.Symbols.TryGetValue(r.Symbol, out var off) ? off : null;
        return syms.LifeOffset(r.TargetActor, r.Symbol);
    }

    private sealed class Parser
    {
        private readonly TokenStream ts;
        private readonly Assembler asm = new();
        private readonly int actor;
        private readonly Stack<Label> breakTargets = new();
        private readonly List<ExternalRef> pending = new();
        private readonly Dictionary<Label, Token> refTokens = new();
        private ValueKind switchKind = ValueKind.S8;
        private readonly List<(int HeaderLine, int FirstInstr)> blocks = new();

        public Parser(string text, int actor)
        {
            ts = new TokenStream(Lexer.Lex(text));
            this.actor = actor;
        }

        public CompiledScript Run()
        {
            while (!ts.AtEnd)
            {
                if (ts.Peek().Is("void")) ParseFunction();
                else ParseStatement();
            }

            asm.CurrentLine = 0;   // the implicit closing END belongs to no source line
            if (asm.Code.Count == 0 || asm.Code[^1].Op != OpEnd) asm.Emit(new Instr { Op = OpEnd });

            var bytes = asm.Encode(ScriptKind.Life, l => refTokens.TryGetValue(l, out var tok) ? tok : null);
            var symbols = asm.Symbols.Where(p => p.Value.Bound).ToDictionary(p => p.Key, p => asm.OffsetOf(p.Value), StringComparer.Ordinal);
            var script = new CompiledScript { Kind = ScriptKind.Life, Bytes = bytes, Symbols = symbols, Asm = asm };
            script.Pending.AddRange(pending);
            script.Blocks.AddRange(blocks);
            return script;
        }

        // ---- structure ----------------------------------------------------

        private void ParseFunction()
        {
            ts.Expect("void");
            var name = ts.ExpectIdent("a function name");
            if (Keywords.Contains(name.Text)) throw TokenStream.Error($"'{name.Text}' is a keyword", name);
            ts.Expect("("); ts.Expect(")");
            asm.Define(name.Text, name);
            blocks.Add((name.Line, asm.Code.Count));
            ts.Expect("{");
            while (!ts.Peek().Is("}"))
            {
                if (ts.AtEnd) throw TokenStream.Error($"Function '{name.Text}' is missing its closing '}}'", ts.Peek());
                ParseStatement();
            }
            ts.Expect("}");
            asm.CurrentLine = ts.LastLine;
            asm.Emit(new Instr { Op = OpEndComportement });
        }

        private void ParseBody()
        {
            if (ts.Peek().Is("{"))
            {
                ts.Next();
                while (!ts.Peek().Is("}"))
                {
                    if (ts.AtEnd) throw TokenStream.Error("Missing closing '}'", ts.Peek());
                    ParseStatement();
                }
                ts.Expect("}");
            }
            else ParseStatement();
        }

        private void ParseStatement()
        {
            var t = ts.Peek();
            asm.CurrentLine = t.Line;
            if (t.Is("{")) { ParseBody(); return; }
            if (t.Is(";")) { ts.Next(); return; }
            if (t.Kind != TokKind.Ident) throw TokenStream.Error($"Expected a statement but found {t}", t);

            if (ts.Peek(1).Is(":") && !Keywords.Contains(t.Text))
            {
                ts.Next(); ts.Next();
                asm.Define(t.Text, t);
                return;
            }

            switch (t.Text)
            {
                case "if": case "swif": case "oneif": case "snif": case "neverif": ParseIf(); return;
                case "while": ParseWhile(); return;
                case "switch": ParseSwitch(); return;
                case "break":
                {
                    ts.Next(); ts.Expect(";");
                    if (breakTargets.Count == 0) throw TokenStream.Error("'break' outside a switch", t);
                    var ins = asm.Emit(new Instr { Op = OpBreak, A = new long[1] });
                    asm.Jump(ins, breakTargets.Peek());
                    return;
                }
                case "return": ts.Next(); ts.Expect(";"); asm.Emit(new Instr { Op = OpReturn }); return;
                case "goto":
                {
                    ts.Next();
                    var target = ts.ExpectIdent("a label");
                    ts.Expect(";");
                    var ins = asm.Emit(new Instr { Op = OpOffset, A = new long[1] });
                    JumpTo(ins, target);
                    return;
                }
                case "else": throw TokenStream.Error("'else' without a matching 'if'", t);
                case "case": case "default": throw TokenStream.Error($"'{t.Text}' outside a switch", t);
            }

            ParseOpcodeCall();
        }

        private void ParseIf()
        {
            var kw = ts.Next();
            ts.Expect("(");
            var expr = ExprText.Parse(ts);
            ts.Expect(")");

            var falseLabel = asm.NewLabel("$false");
            GenFalse(expr, falseLabel);
            asm.Code[^1].Op = IfOpcode(kw.Text);

            ParseBody();
            if (ts.Peek().Is("else"))
            {
                var elseTok = ts.Next();
                var endLabel = asm.NewLabel("$end");
                asm.CurrentLine = elseTok.Line;
                var jump = asm.Emit(new Instr { Op = OpElse, A = new long[1] });
                asm.Jump(jump, endLabel);
                asm.Bind(falseLabel);
                ParseBody();
                asm.Bind(endLabel);
            }
            else asm.Bind(falseLabel);
        }

        private void ParseWhile()
        {
            ts.Next();
            var top = asm.NewLabel("$top");
            asm.Bind(top);
            ts.Expect("(");
            var expr = ExprText.Parse(ts);
            ts.Expect(")");

            var end = asm.NewLabel("$wend");
            GenFalse(expr, end);
            asm.Code[^1].Op = OpIf;
            ParseBody();
            asm.CurrentLine = ts.LastLine;
            var jump = asm.Emit(new Instr { Op = OpOffset, A = new long[1] });
            asm.Jump(jump, top);
            asm.Bind(end);
        }

        private void ParseSwitch()
        {
            ts.Next();
            ts.Expect("(");
            var (cd, arg, _) = ExprText.ParseCondCall(ts);
            ts.Expect(")");
            ts.Expect("{");
            asm.Emit(new Instr { Op = OpSwitch, Func = cd.Id, FuncArg = arg });
            switchKind = cd.Value;

            var end = asm.NewLabel("$switchend");
            breakTargets.Push(end);

            while (!ts.Peek().Is("}"))
            {
                var t = ts.Peek();
                if (t.Is("case"))
                {
                    var labels = new List<(byte Test, int Value, int Line)>();
                    while (ts.Peek().Is("case"))
                    {
                        var caseTok = ts.Next();
                        var test = (byte)0;
                        if (ts.Peek().Kind == TokKind.Punct && Array.IndexOf(Opcodes.TestSymbols, ts.Peek().Text) >= 0) test = ExprText.ParseTestOp(ts);
                        var vt = ts.Peek();
                        var value = (int)ExprText.ParseValue(ts);
                        Operands.CheckValueRange(switchKind, value, vt);
                        ts.Expect(":");
                        labels.Add((test, value, caseTok.Line));
                    }

                    var body = asm.NewLabel("$casebody");
                    var next = asm.NewLabel("$casenext");
                    for (var k = 0; k < labels.Count; k++)
                    {
                        var last = k == labels.Count - 1;
                        asm.CurrentLine = labels[k].Line;
                        var ins = asm.Emit(new Instr { Op = last ? OpCase : OpOrCase, Test = labels[k].Test, Value = labels[k].Value });
                        asm.Jump(ins, last ? next : body);
                    }
                    asm.Bind(body);
                    ParseCaseBody();
                    asm.Bind(next);
                }
                else if (t.Is("default"))
                {
                    var defTok = ts.Next();
                    ts.Expect(":");
                    asm.CurrentLine = defTok.Line;
                    asm.Emit(new Instr { Op = OpDefault });
                    ParseCaseBody();
                }
                else throw TokenStream.Error($"Expected 'case', 'default' or '}}' inside switch but found {t}", t);
            }
            ts.Expect("}");
            asm.CurrentLine = ts.LastLine;
            asm.Bind(end);
            asm.Emit(new Instr { Op = OpEndSwitch });
            breakTargets.Pop();
        }

        private void ParseCaseBody()
        {
            while (!ts.Peek().Is("case") && !ts.Peek().Is("default") && !ts.Peek().Is("}"))
            {
                if (ts.AtEnd) throw TokenStream.Error("Missing closing '}' of switch", ts.Peek());
                ParseStatement();
            }
        }

        // ---- conditions ---------------------------------------------------

        // Emits code that falls through when `e` is true and jumps to `f` when false.
        private void GenFalse(Expr e, Label f)
        {
            switch (e)
            {
                case Leaf l: EmitCond(OpAndIf, l, f); break;
                case AndExpr a: GenFalse(a.L, f); GenFalse(a.R, f); break;
                case OrExpr o:
                {
                    var t = asm.NewLabel("$or");
                    GenTrue(o.L, t);
                    GenFalse(o.R, f);
                    asm.Bind(t);
                    break;
                }
            }
        }

        // Emits code that falls through when `e` is false and jumps to `t` when true.
        private void GenTrue(Expr e, Label t)
        {
            switch (e)
            {
                case Leaf l: EmitCond(OpOrIf, l, t); break;
                case OrExpr o: GenTrue(o.L, t); GenTrue(o.R, t); break;
                case AndExpr a:
                {
                    var skip = asm.NewLabel("$and");
                    GenFalse(a.L, skip);
                    GenTrue(a.R, t);
                    asm.Bind(skip);
                    break;
                }
            }
        }

        private void EmitCond(byte op, Leaf l, Label target)
        {
            var ins = asm.Emit(new Instr { Op = op, Func = l.Cond.Id, FuncArg = l.FuncArg, Test = l.Test, Value = l.Value });
            asm.Jump(ins, target);
        }

        // ---- opcode calls -------------------------------------------------

        private void JumpTo(Instr ins, Token labelToken)
        {
            var l = asm.Reference(labelToken.Text);
            refTokens.TryAdd(l, labelToken);
            asm.Jump(ins, l);
        }

        private LifeOpDef LookupOp(Token name)
        {
            var def = Opcodes.Life(name.Text);
            if (def is null && name.Text.All(c => char.IsLower(c) || c == '_' || char.IsDigit(c)) && !Keywords.Contains(name.Text))
                def = Opcodes.Life(name.Text.ToUpperInvariant());
            return def ?? throw TokenStream.Error($"Unknown life opcode '{name.Text}'", name);
        }

        private void ParseOpcodeCall()
        {
            var nameTok = ts.Next();
            var def = LookupOp(nameTok);

            switch (def.Form)
            {
                case LifeForm.Cond: ParseFlatCond(def); return;
                case LifeForm.Switch: ParseFlatSwitch(def); return;
                case LifeForm.Case: ParseFlatCase(def); return;
            }

            var args = Operands.ParseArgList(ts);
            ts.Expect(";");
            asm.Emit(BuildPlain(def, args, nameTok));
        }

        // IF(leaf, label);
        private void ParseFlatCond(LifeOpDef def)
        {
            ts.Expect("(");
            var leaf = ExprText.ParseLeaf(ts);
            ts.Expect(",");
            var target = ParseTarget();
            ts.Expect(")"); ts.Expect(";");
            var ins = asm.Emit(new Instr { Op = def.Id, Func = leaf.Cond.Id, FuncArg = leaf.FuncArg, Test = leaf.Test, Value = leaf.Value });
            ApplyTarget(ins, target);
        }

        // SWITCH(FUNC(x));
        private void ParseFlatSwitch(LifeOpDef def)
        {
            ts.Expect("(");
            var (cd, arg, _) = ExprText.ParseCondCall(ts);
            ts.Expect(")"); ts.Expect(";");
            asm.Emit(new Instr { Op = def.Id, Func = cd.Id, FuncArg = arg });
            switchKind = cd.Value;
        }

        // CASE([op] value, label);
        private void ParseFlatCase(LifeOpDef def)
        {
            ts.Expect("(");
            var test = (byte)0;
            if (ts.Peek().Kind == TokKind.Punct && Array.IndexOf(Opcodes.TestSymbols, ts.Peek().Text) >= 0) test = ExprText.ParseTestOp(ts);
            var vt = ts.Peek();
            var value = (int)ExprText.ParseValue(ts);
            Operands.CheckValueRange(switchKind, value, vt);
            ts.Expect(",");
            var target = ParseTarget();
            ts.Expect(")"); ts.Expect(";");
            var ins = asm.Emit(new Instr { Op = def.Id, Test = test, Value = value });
            ApplyTarget(ins, target);
        }

        private ArgVal ParseTarget()
        {
            var a = Operands.ParseArg(ts);
            if (a.Ident is null && !a.Raw) throw TokenStream.Error("Expected a label name or @offset", a.At);
            return a;
        }

        private void ApplyTarget(Instr ins, ArgVal target)
        {
            if (target.Ident is not null) JumpTo(ins, target.At);
            else
            {
                Operands.CheckRange(ArgType.Jump, target.Num, target.At, "target");
                if (Bytecode.TryGetTarget(ScriptKind.Life, ins, out _)) Bytecode.SetTarget(ScriptKind.Life, ins, (int)target.Num);
            }
        }

        private Instr BuildPlain(LifeOpDef def, List<ArgVal> args, Token at)
        {
            var declared = def.Args.Length;
            var expected = declared;
            if (def.Form == LifeForm.Dir)
            {
                if (args.Count < declared) throw TokenStream.Error($"{def.Name} takes at least {declared} argument(s)", at);
                var moveVal = Operands.RequireNumber(args[declared - 1], def.Args[declared - 1].Name);
                expected = declared + (Opcodes.MoveTakesParam(moveVal) ? 1 : 0);
                if (args.Count != expected)
                    throw TokenStream.Error(Opcodes.MoveTakesParam(moveVal)
                        ? $"{def.Name} with this move mode needs a parameter (the object or point to use)"
                        : $"{def.Name} with this move mode takes no parameter", at);
            }
            else if (args.Count != declared)
                throw TokenStream.Error($"{def.Name} takes {declared} argument(s) but {args.Count} were given", at);

            var ins = new Instr { Op = def.Id, A = new long[expected] };
            for (var ix = 0; ix < expected; ix++)
            {
                var a = args[ix];
                var d = ix < declared ? def.Args[ix] : new ArgDef("param", ArgType.U8);

                if (d.Type == ArgType.CStr)
                {
                    ins.Str = a.Str ?? throw TokenStream.Error($"'{d.Name}' must be a string", a.At);
                    if (ins.Str.Contains('\0')) throw TokenStream.Error("String may not contain NUL", a.At);
                    continue;
                }

                if (d.Type == ArgType.Jump)
                {
                    if (a.Ident is not null) JumpTo(ins, a.At);
                    else if (a.Raw) { Operands.CheckRange(ArgType.Jump, a.Num, a.At, d.Name); ins.A[ix] = a.Num; }
                    else throw TokenStream.Error($"'{d.Name}' must be a label name (or @offset)", a.At);
                    continue;
                }

                switch (d.Role)
                {
                    case ArgRole.LifeOffset:
                    case ArgRole.TrackOffset:
                        BindOffsetOperand(def, ins, ix, d, a);
                        break;

                    default:
                    {
                        var v = Operands.RequireNumber(a, d.Name);
                        Operands.CheckRange(d.Type, v, a.At, d.Name);
                        ins.A[ix] = v;
                        break;
                    }
                }
            }
            return ins;
        }

        // SET_COMPORTEMENT(name) / SET_COMPORTEMENT_OBJ(obj, name) /
        // SET_TRACK(name) / SET_TRACK_OBJ(obj, name)
        private void BindOffsetOperand(LifeOpDef def, Instr ins, int ix, ArgDef d, ArgVal a)
        {
            if (a.Raw)
            {
                Operands.CheckRange(ArgType.S16, a.Num, a.At, d.Name);
                ins.A[ix] = a.Num;
                return;
            }
            if (a.Ident is null)
                throw TokenStream.Error($"'{d.Name}' must be a {(d.Role == ArgRole.LifeOffset ? "comportement" : "track label")} name (or @offset)", a.At);

            var isObjVariant = def.Id is OpSetComportementObj or OpSetTrackObj;
            var targetActor = isObjVariant ? (int)ins.A[0] : actor;

            if (d.Role == ArgRole.LifeOffset && targetActor == actor)
            {
                var l = asm.Reference(a.Ident);
                refTokens.TryAdd(l, a.At);
                asm.Ref(ins, ix, l);
                return;
            }

            pending.Add(new ExternalRef(ins, ix, d.Role == ArgRole.LifeOffset ? ScriptKind.Life : ScriptKind.Track, targetActor, a.Ident, a.At.Line, a.At.Col));
        }
    }
}
