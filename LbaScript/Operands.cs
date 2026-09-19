using System.Text;

namespace LBA2LevelEditor.LbaScript;

// A parsed call argument: number, bare identifier (constant or symbol),
// "@offset" raw byte offset, or string literal.
internal sealed record ArgVal(Token At, long Num, string? Ident, string? Str, bool Raw)
{
    public bool IsNumber => Ident is null && Str is null;
}

// Shared operand text handling for plain opcodes of both languages: how the
// decompilers print an instruction's arguments and how the compilers read
// them back.
internal static class Operands
{
    // ---- printing ---------------------------------------------------------

    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    // Joins the visible (non-hidden) operands, then any hidden operand whose
    // value differs from `hiddenDefault` (so a script with non-canonical
    // runtime scratch bytes still round-trips). `symbol` renders operands that
    // refer to positions/other scripts; return null to fall back to a number.
    public static string Format(ArgDef[] defs, Instr ins, Func<int, ArgDef, string?> symbol, Func<int, long>? hiddenDefault = null, byte? moveOp = null)
    {
        var parts = new List<string>();
        var extraHidden = new List<string>();
        var anyHiddenNonDefault = false;

        for (var i = 0; i < defs.Length; i++)
        {
            var d = defs[i];
            if (d.Role == ArgRole.Hidden)
            {
                var def = hiddenDefault?.Invoke(i) ?? 0;
                if (ins.A[i] != def) anyHiddenNonDefault = true;
                extraHidden.Add(ins.A[i].ToString());
                continue;
            }
            if (d.Type == ArgType.CStr) { parts.Add(Quote(ins.Str ?? "")); continue; }
            var sym = symbol(i, d);
            if (sym is not null) { parts.Add(sym); continue; }
            parts.Add(FormatNumber(d.Type, ins.A[i]));
        }

        // Optional variable-length tail (SET_DIR's mode parameter).
        for (var i = defs.Length; i < ins.A.Length; i++) parts.Add(ins.A[i].ToString());

        if (anyHiddenNonDefault) parts.AddRange(extraHidden);
        return string.Join(", ", parts);
    }

    public static string FormatNumber(ArgType t, long v) => v.ToString();

    // ---- parsing ----------------------------------------------------------

    // Parses "( a, b, c )" (the '(' already consumed by the caller is NOT
    // assumed: this expects to be positioned at '(').
    public static List<ArgVal> ParseArgList(TokenStream ts)
    {
        var args = new List<ArgVal>();
        ts.Expect("(");
        if (ts.Accept(")")) return args;
        do
        {
            args.Add(ParseArg(ts));
        } while (ts.Accept(","));
        ts.Expect(")");
        return args;
    }

    public static ArgVal ParseArg(TokenStream ts)
    {
        var t = ts.Peek();
        if (t.Kind == TokKind.String) { ts.Next(); return new ArgVal(t, 0, null, t.Text, false); }
        if (t.Kind == TokKind.Ident) { ts.Next(); return new ArgVal(t, 0, t.Text, null, false); }
        if (t.Is("@")) { ts.Next(); return new ArgVal(t, ts.ExpectInteger(), null, null, true); }
        return new ArgVal(t, ts.ExpectInteger(), null, null, false);
    }

    // Integer range for each operand type. U8 also accepts what a signed
    // byte would print as (-128..-1) is *not* allowed: authors must pick the
    // representation the engine reads.
    public static void CheckRange(ArgType t, long v, Token at, string argName)
    {
        var (lo, hi) = t switch
        {
            ArgType.U8 => (0L, 255L),
            ArgType.S8 => (-128L, 127L),
            ArgType.U16 => (0L, 65535L),
            ArgType.S16 => (-32768L, 32767L),
            ArgType.U32 => (0L, uint.MaxValue),
            ArgType.Jump => (-32768L, 32767L),
            _ => (long.MinValue, long.MaxValue),
        };
        if (v < lo || v > hi) throw TokenStream.Error($"Value {v} for '{argName}' is out of range ({lo}..{hi})", at);
    }

    public static void CheckValueRange(ValueKind k, long v, Token at)
    {
        var (lo, hi) = k switch { ValueKind.S8 => (-128L, 127L), ValueKind.U8 => (0L, 255L), _ => (-32768L, 32767L) };
        if (v < lo || v > hi) throw TokenStream.Error($"Comparison value {v} is out of range for this condition ({lo}..{hi})", at);
    }

    // Numeric constants usable wherever a number is expected.
    public static bool TryConstant(string name, out long value)
    {
        var ix = Array.IndexOf(Opcodes.MoveNames, name);
        if (ix >= 0) { value = ix; return true; }
        value = 0;
        return false;
    }

    public static long RequireNumber(ArgVal a, string argName)
    {
        if (a.IsNumber && !a.Raw) return a.Num;
        if (a.Ident is not null && TryConstant(a.Ident, out var v)) return v;
        throw TokenStream.Error($"'{argName}' must be a number, found {(a.Ident ?? a.Str ?? "@offset")}", a.At);
    }
}
