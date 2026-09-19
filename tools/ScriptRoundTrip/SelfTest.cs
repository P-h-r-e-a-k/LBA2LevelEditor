using LBA2LevelEditor.LbaScript;

namespace ScriptRoundTrip;

// Checks the *compiler* on hand-written C -- constructs the shipped scripts never
// use (while, (a||b)&&c, !, else-if chains, error reporting) -- by compiling
// snippets and comparing the resulting instruction listing to a known-good one.
internal static class SelfTest
{
    private static int failures;
    private static int passed;

    public static int Run()
    {
        var syms = NoSymbols.Instance;

        // if / else lowers to IF -> else-start; ELSE -> end
        Expect("if-else", "void f() { if (ZONE() == 1) { BODY(2); } else { BODY(3); } }",
            "IF ZONE == 1 -> 11", "BODY 2", "ELSE -> 13", "BODY 3", "END_COMPORTEMENT", "END");

        // plain if: IF jumps past the body
        Expect("plain-if", "void f() { if (ACTION() == 1) MESSAGE(5); }",
            "IF ACTION == 1 -> 9", "MESSAGE 5", "END_COMPORTEMENT", "END");

        // && : AND_IF ... IF, all to the same false target
        Expect("and-chain", "void f() { if (ZONE() == 1 && COL() == 0 && ACTION() == 1) { RETURN(); } }",
            "AND_IF ZONE == 1 -> 19", "AND_IF COL == 0 -> 19", "IF ACTION == 1 -> 19", "RETURN", "END_COMPORTEMENT", "END");

        // || : OR_IF jumps to the body start
        Expect("or-chain", "void f() { if (ZONE() == 1 || COL() == 0) { RETURN(); } }",
            "OR_IF ZONE == 1 -> 12", "IF COL == 0 -> 13", "RETURN", "END_COMPORTEMENT", "END");

        // (a || b) && c : the OR clause's true target is the next clause
        Expect("cnf", "void f() { if ((ZONE() == 1 || COL() == 0) && ACTION() == 1) { RETURN(); } }",
            "OR_IF ZONE == 1 -> 12", "AND_IF COL == 0 -> 19", "IF ACTION == 1 -> 19", "RETURN", "END_COMPORTEMENT", "END");

        // ! flips the comparison (De Morgan for groups)
        Expect("negate", "void f() { if (!(ZONE() == 1 && COL() < 3)) { RETURN(); } }",
            "OR_IF ZONE != 1 -> 12", "IF COL >= 3 -> 13", "RETURN", "END_COMPORTEMENT", "END");

        // while: condition at the top, unconditional OFFSET back to it
        Expect("while", "void f() { while (VAR_CUBE(3) < 5) { ADD_VAR_CUBE(3, 1); } }",
            "IF VAR_CUBE(3) < 5 -> 13", "ADD_VAR_CUBE 3 1", "OFFSET -> 0", "END_COMPORTEMENT", "END");

        // else-if chain
        Expect("else-if", "void f() { if (ZONE() == 1) { BODY(1); } else if (ZONE() == 2) { BODY(2); } else { BODY(3); } }",
            "IF ZONE == 1 -> 11", "BODY 1", "ELSE -> 24", "IF ZONE == 2 -> 22", "BODY 2", "ELSE -> 24", "BODY 3", "END_COMPORTEMENT", "END");

        // swif / oneif choose the terminal opcode
        Expect("swif", "void f() { swif (ACTION() == 1) { RETURN(); } oneif (ZONE() == 1) { RETURN(); } }",
            "SWIF ACTION == 1 -> 7", "RETURN", "ONEIF ZONE == 1 -> 14", "RETURN", "END_COMPORTEMENT", "END");

        // switch with stacked (OR) cases, break, default
        Expect("switch", "void f() { switch (VAR_CUBE(1)) { case 1: case 2: BODY(1); break; case > 5: BODY(2); break; default: BODY(9); } }",
            "SWITCH VAR_CUBE(1)", "OR_CASE == 1 -> 13", "CASE == 2 -> 18", "BODY 1", "BREAK -> 31", "CASE > 5 -> 28", "BODY 2", "BREAK -> 31", "DEFAULT", "BODY 9", "END_SWITCH", "END_COMPORTEMENT", "END");

        // forward labels + goto (OFFSET) and SET_COMPORTEMENT to a function / a mid-block label
        Expect("labels", "void a() { goto skip; BODY(1); skip: BODY(2); SET_COMPORTEMENT(b); SET_COMPORTEMENT(skip); } void b() { NOP(); }",
            "OFFSET -> 5", "BODY 1", "BODY 2", "SET_COMPORTEMENT 14", "SET_COMPORTEMENT 5", "END_COMPORTEMENT", "NOP", "END_COMPORTEMENT", "END");

        // tail statements outside functions + lower-case opcode spelling
        Expect("tail", "void a() { RETURN(); } suicide();", "RETURN", "END_COMPORTEMENT", "SUICIDE", "END");

        // SET_DIR takes a parameter only for the modes that read one
        Expect("set_dir", "void a() { SET_DIR(MOVE_FOLLOW, 4); SET_DIR(MOVE_MANUAL); }", "SET_DIR 2 4", "SET_DIR 1", "END_COMPORTEMENT", "END");

        // errors carry line/column and a useful message
        ExpectError("unknown-op", "void a() {\n  FROBNICATE(1);\n}", "Unknown life opcode", 2);
        ExpectError("range", "void a() { BODY(300); }", "out of range", 1);
        ExpectError("arg-count", "void a() { ANIM(1, 2); }", "takes 1 argument", 1);
        ExpectError("undefined-label", "void a() { goto nowhere; }", "Undefined label", 1);
        ExpectError("missing-brace", "void a() { BODY(1);", "missing its closing", 1);
        ExpectError("dup-label", "void a() { x: NOP(); x: NOP(); }", "already defined", 1);
        ExpectError("break-outside", "void a() { break; }", "outside a switch", 1);
        ExpectError("bad-dir-param", "void a() { SET_DIR(MOVE_FOLLOW); }", "needs a parameter", 1);
        ExpectError("need-operand", "void a() { if (DISTANCE() < 5) { NOP(); } }", "needs an operand", 1);

        // track language: labels, goto, hidden bytes synthesized
        var track = TrackText.Compile("LABEL(3);\nSAMPLE(9);\nWAIT_NB_SECOND(2);\nGOTO(label_3);");
        Check("track-bytes", Hex(track.Bytes) == "09 03 0E 09 00 12 02 00 00 00 00 0A 00 00 00", $"got {Hex(track.Bytes)}");
        var dupTrack = TrackText.Compile("LABEL(1); NOP(); LABEL(1); GOTO(label_1_2);");
        Check("track-dup-label", Hex(dupTrack.Bytes) == "09 01 01 09 01 0A 03 00 00", $"got {Hex(dupTrack.Bytes)}");

        Console.WriteLine($"selftest: {passed} passed, {failures} failed");
        return failures == 0 ? 0 : 1;

        // -- helpers ------------------------------------------------------
        void Expect(string name, string src, params string[] expected)
        {
            try
            {
                var c = LifeText.Compile(src, 0, syms);
                c.ResolveExternals(_ => 0);
                var listing = Bytecode.DecodeLife(c.Bytes).Select(Describe).ToArray();
                Check(name, listing.SequenceEqual(expected), $"\n      expected: {string.Join(" | ", expected)}\n      actual:   {string.Join(" | ", listing)}");
            }
            catch (Exception e) { Check(name, false, $"threw {e.GetType().Name}: {e.Message}"); }
        }

        void ExpectError(string name, string src, string contains, int line)
        {
            try
            {
                LifeText.Compile(src, 0, syms);
                Check(name, false, "expected a compile error but it compiled");
            }
            catch (ScriptCompileException e)
            {
                Check(name, e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase) && e.Line == line, $"got '{e.Message}'");
            }
        }
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { passed++; return; }
        failures++;
        Console.WriteLine($"  FAIL {name}: {detail}");
    }

    private static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

    private static string Describe(Instr i)
    {
        var def = Opcodes.Life(i.Op)!;
        switch (def.Form)
        {
            case LifeForm.Cond:
            {
                var cd = Opcodes.Cond(i.Func)!;
                return $"{def.Name} {(cd.OperandName is null ? cd.Name : $"{cd.Name}({i.FuncArg})")} {Opcodes.TestSymbols[i.Test]} {i.Value} -> {i.Target}";
            }
            case LifeForm.Switch:
            {
                var cd = Opcodes.Cond(i.Func)!;
                return $"SWITCH {(cd.OperandName is null ? cd.Name : $"{cd.Name}({i.FuncArg})")}";
            }
            case LifeForm.Case:
                return $"{def.Name} {Opcodes.TestSymbols[i.Test]} {i.Value} -> {i.Target}";
        }
        var jump = def.Args.Select((a, k) => (a, k)).FirstOrDefault(p => p.a.Type == ArgType.Jump);
        if (jump.a.Name is not null && def.Args.Length == 1) return $"{def.Name} -> {i.A[jump.k]}";
        return def.Args.Length == 0 ? def.Name : $"{def.Name} {string.Join(" ", i.A)}";
    }
}
