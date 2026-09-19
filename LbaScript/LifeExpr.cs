namespace LBA2LevelEditor.LbaScript;

// Boolean condition expressions of the life language.
//
// The engine has no boolean operators: a condition is a chain of single
// comparisons (AND_IF / OR_IF ... IF) that jump on the result. In C they read
// as ordinary && / || expressions of comparison leaves:
//
//     if (NB_LITTLE_KEYS() > 0 && (ZONE_OBJ(0) == 2 || DISTANCE(0) < 300)) ...
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
    public static string FuncCall(CondDef cd, int funcArg) => cd.OperandName is null ? $"{cd.Name}()" : $"{cd.Name}({funcArg})";

    public static string PrintLeaf(Leaf l) => $"{FuncCall(l.Cond, l.FuncArg)} {Opcodes.TestSymbols[l.Test]} {l.Value}";

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

    // FUNC(operand) <op> value      (nullary functions: FUNC() <op> value, or bare FUNC)
    public static Leaf ParseLeaf(TokenStream ts)
    {
        var (cd, arg, at) = ParseCondCall(ts);
        var test = ParseTestOp(ts);
        var vt = ts.Peek();
        var value = (int)ParseValue(ts);
        Operands.CheckValueRange(cd.Value, value, vt);
        return new Leaf(cd, arg, test, value);
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
