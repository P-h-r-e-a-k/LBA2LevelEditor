namespace LBAAssembler.LbaScript;

// Boolean condition expressions of the life language.
//
// The engine has no boolean operators: a condition is a chain of single
// comparisons (AND_IF / OR_IF ... IF) that jump on the result. In C they read
// as ordinary && / || expressions of comparison leaves:
//
//     if (0 < nb_little_keys() && (2 == zone_obj(0) || 300 > distance(0))) ...
//
// Each leaf is one LF_* condition function (with its operand, if it has one),
// a comparison operator and a constant.

internal abstract record Expr;

internal sealed record Leaf(CondDef Cond, int FuncArg, byte Test, int Value) : Expr
{
    public Leaf Negated() => this with { Test = InvertTest(Test) };

    // LT_EQUAL/SUP/LESS/SUP_EQUAL/LESS_EQUAL/DIFFERENT -> its logical opposite.
    public static byte InvertTest(byte t) => t switch { 0 => 5, 5 => 0, 1 => 4, 4 => 1, 2 => 3, 3 => 2, _ => t };
}

internal sealed record AndExpr(Expr L, Expr R) : Expr;
internal sealed record OrExpr(Expr L, Expr R) : Expr;

internal static class ExprText
{
    public static string FuncCall(CondDef cd, int funcArg)
    {
        var name = ScriptStyle.Func(cd.Name);
        return cd.OperandName is null ? $"{name}()" : $"{name}({funcArg})";
    }

    // Literals always print on the left: `500 > DISTANCE(0)`, never
    // `DISTANCE(0) < 500`. The operator is mirrored to keep the meaning.
    public static string PrintLeaf(Leaf l) => $"{l.Value} {Opcodes.TestSymbols[MirrorTest(l.Test)]} {FuncCall(l.Cond, l.FuncArg)}";

    // `a < b` is `b > a`: swap the sides of a comparison.
    public static byte MirrorTest(byte t) => t switch { 1 => 2, 2 => 1, 3 => 4, 4 => 3, _ => t };

    // Precedence: || = 1, && = 2, leaf = 3. A child is parenthesised when it
    // binds looser than its parent, or (for readability) when an && sits
    // directly under an || / an || sits under an &&.
    public static string Print(Expr e, int parentPrec = 0)
    {
        switch (e)
        {
            case Leaf l: return PrintLeaf(l);
            case AndExpr a:
            {
                var s = $"{Print(a.L, 2)} && {Print(a.R, 2)}";
                return parentPrec == 1 ? $"({s})" : s;   // parenthesised inside an || for readability
            }
            case OrExpr o:
            {
                var s = $"{Print(o.L, 1)} || {Print(o.R, 1)}";
                return parentPrec == 2 ? $"({s})" : s;   // required inside an &&
            }
            default: throw new InvalidOperationException();
        }
    }

    // De Morgan: !(a && b) = !a || !b, etc.; leaves flip their comparison.
    public static Expr Negate(Expr e) => e switch
    {
        Leaf l => l.Negated(),
        AndExpr a => new OrExpr(Negate(a.L), Negate(a.R)),
        OrExpr o => new AndExpr(Negate(o.L), Negate(o.R)),
        _ => throw new InvalidOperationException(),
    };

    // ---- parsing ----------------------------------------------------------

    public static Expr Parse(TokenStream ts) => ParseOr(ts);

    private static Expr ParseOr(TokenStream ts)
    {
        var e = ParseAnd(ts);
        while (ts.Accept("||")) e = new OrExpr(e, ParseAnd(ts));
        return e;
    }

    private static Expr ParseAnd(TokenStream ts)
    {
        var e = ParseUnary(ts);
        while (ts.Accept("&&")) e = new AndExpr(e, ParseUnary(ts));
        return e;
    }

    private static Expr ParseUnary(TokenStream ts)
    {
        if (ts.Accept("!")) return Negate(ParseUnary(ts));
        if (ts.Peek().Is("("))
        {
            ts.Next();
            var e = ParseOr(ts);
            ts.Expect(")");
            return e;
        }
        return ParseLeaf(ts);
    }

    // value <op> FUNC(operand)      (the canonical form: literals on the left)
    // FUNC(operand) <op> value      (accepted, with a warning)
    // Nullary functions: FUNC() <op> value, or bare FUNC.
    public static Leaf ParseLeaf(TokenStream ts)
    {
        var first = ts.Peek();
        var literalFirst = first.Kind == TokKind.Number || first.Is("-")
            || (first.Kind == TokKind.Ident && Opcodes.Cond(first.Text) is null && Operands.TryConstant(first.Text, out _));
        if (literalFirst)
        {
            var vt = ts.Peek();
            var value = (int)ParseValue(ts);
            var test = MirrorTest(ParseTestOp(ts));
            var (cd, arg, _) = ParseCondCall(ts);
            Operands.CheckValueRange(cd.Value, value, vt);
            return new Leaf(cd, arg, test, value);
        }
        else
        {
            var (cd, arg, at) = ParseCondCall(ts);
            var test = ParseTestOp(ts);
            var vt = ts.Peek();
            var value = (int)ParseValue(ts);
            Operands.CheckValueRange(cd.Value, value, vt);
            var leaf = new Leaf(cd, arg, test, value);
            ts.Warn(vt, $"literal on the right of a comparison; write '{PrintLeaf(leaf)}' (literals go on the left)");
            return leaf;
        }
    }

    // FUNC / FUNC() / FUNC(n)
    public static (CondDef Cond, int Arg, Token At) ParseCondCall(TokenStream ts)
    {
        var name = ts.ExpectIdent("a condition such as DISTANCE(0)");
        var cd = Opcodes.Cond(name.Text) ?? throw TokenStream.Error($"Unknown condition '{name.Text}'", name);
        var arg = -1;
        if (ts.Peek().Is("("))
        {
            ts.Next();
            if (cd.OperandName is null)
            {
                ts.Expect(")");
            }
            else
            {
                var at = ts.Peek();
                if (at.Is(")")) throw TokenStream.Error($"{cd.Name} needs an operand: {cd.Name}({cd.OperandName})", name);
                var v = ts.ExpectInteger();
                if (v is < 0 or > 255) throw TokenStream.Error($"Operand '{cd.OperandName}' must be 0..255", at);
                arg = (int)v;
                ts.Expect(")");
            }
        }
        else if (cd.OperandName is not null)
            throw TokenStream.Error($"{cd.Name} needs an operand: {cd.Name}({cd.OperandName})", name);
        return (cd, arg, name);
    }

    public static byte ParseTestOp(TokenStream ts)
    {
        var t = ts.Peek();
        var ix = t.Kind == TokKind.Punct ? Array.IndexOf(Opcodes.TestSymbols, t.Text) : -1;
        if (ix < 0) throw TokenStream.Error($"Expected a comparison (==, !=, <, >, <=, >=) but found {t}", t);
        ts.Next();
        return (byte)ix;
    }

    public static long ParseValue(TokenStream ts)
    {
        var t = ts.Peek();
        if (t.Kind == TokKind.Ident && Operands.TryConstant(t.Text, out var c)) { ts.Next(); return c; }
        return ts.ExpectInteger();
    }
}
