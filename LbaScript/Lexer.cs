using System.Globalization;
using System.Text;

namespace LBAAssembler.LbaScript;

public sealed class ScriptCompileException : Exception
{
    public int Line { get; }
    public int Column { get; }
    public ScriptCompileException(string message, int line, int column) : base($"line {line}, col {column}: {message}")
    {
        Line = line;
        Column = column;
    }
}

// A problem that does not stop the script compiling.
public sealed record ScriptWarning(int Line, int Column, string Message);

internal enum TokKind : byte { Ident, Number, String, Punct, End }

internal readonly record struct Token(TokKind Kind, string Text, long Value, int Line, int Col)
{
    public bool Is(string punctOrKeyword) => (Kind == TokKind.Punct || Kind == TokKind.Ident) && Text == punctOrKeyword;
    public override string ToString() => Kind == TokKind.End ? "end of file" : Kind == TokKind.String ? $"\"{Text}\"" : $"'{Text}'";
}

// Tokenizer for the C-style script dialect: identifiers, decimal/hex
// integers, double-quoted strings, // and /* */ comments, and the handful of
// punctuators the grammar uses. Anything else is a hard error with position.
internal static class Lexer
{
    private static readonly string[] TwoChar = { "==", "!=", "<=", ">=", "&&", "||" };
    private const string OneChar = "(){};,:<>!@-";

    public static List<Token> Lex(string src)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, col = 1;

        void Advance(int n = 1)
        {
            for (var k = 0; k < n; k++, i++)
            {
                if (src[i] == '\n') { line++; col = 1; } else col++;
            }
        }

        while (i < src.Length)
        {
            var c = src[i];
            if (char.IsWhiteSpace(c)) { Advance(); continue; }

            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') Advance();
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                var sl = line; var sc = col;
                Advance(2);
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) Advance();
                if (i + 1 >= src.Length) throw new ScriptCompileException("Unterminated /* comment", sl, sc);
                Advance(2);
                continue;
            }

            var tl = line; var tc = col;

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) Advance();
                tokens.Add(new Token(TokKind.Ident, src[start..i], 0, tl, tc));
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                if (c == '0' && i + 1 < src.Length && (src[i + 1] == 'x' || src[i + 1] == 'X'))
                {
                    Advance(2);
                    var hs = i;
                    while (i < src.Length && Uri.IsHexDigit(src[i])) Advance();
                    if (hs == i) throw new ScriptCompileException("Malformed hex number", tl, tc);
                    tokens.Add(new Token(TokKind.Number, src[start..i], long.Parse(src[hs..i], NumberStyles.HexNumber, CultureInfo.InvariantCulture), tl, tc));
                }
                else
                {
                    while (i < src.Length && char.IsDigit(src[i])) Advance();
                    tokens.Add(new Token(TokKind.Number, src[start..i], long.Parse(src[start..i], CultureInfo.InvariantCulture), tl, tc));
                }
                if (i < src.Length && (char.IsLetter(src[i]) || src[i] == '_'))
                    throw new ScriptCompileException($"Malformed number '{src[start..(i + 1)]}'", tl, tc);
                continue;
            }

            if (c == '"')
            {
                Advance();
                var sb = new StringBuilder();
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\n') throw new ScriptCompileException("Unterminated string", tl, tc);
                    if (src[i] == '\\' && i + 1 < src.Length)
                    {
                        Advance();
                        sb.Append(src[i] switch { 'n' => '\n', 't' => '\t', '0' => '\0', var e => e });
                    }
                    else sb.Append(src[i]);
                    Advance();
                }
                if (i >= src.Length) throw new ScriptCompileException("Unterminated string", tl, tc);
                Advance();
                tokens.Add(new Token(TokKind.String, sb.ToString(), 0, tl, tc));
                continue;
            }

            var two = i + 1 < src.Length ? src.Substring(i, 2) : "";
            if (Array.IndexOf(TwoChar, two) >= 0)
            {
                tokens.Add(new Token(TokKind.Punct, two, 0, tl, tc));
                Advance(2);
                continue;
            }
            if (OneChar.Contains(c))
            {
                tokens.Add(new Token(TokKind.Punct, c.ToString(), 0, tl, tc));
                Advance();
                continue;
            }

            throw new ScriptCompileException($"Unexpected character '{c}'", tl, tc);
        }

        tokens.Add(new Token(TokKind.End, "", 0, line, col));
        return tokens;
    }
}

// Cursor over the token list with the error helpers every parser needs.
internal sealed class TokenStream
{
    private readonly List<Token> tokens;
    private int pos;
    private int lastLine = 1;

    public TokenStream(List<Token> tokens) => this.tokens = tokens;

    // Warnings raised while parsing (compilation still succeeds).
    public List<ScriptWarning> Warnings { get; } = new();
    public void Warn(Token at, string message) => Warnings.Add(new ScriptWarning(at.Line, at.Col, message));

    public Token Peek(int ahead = 0) => tokens[Math.Min(pos + ahead, tokens.Count - 1)];
    public Token Next() { var t = Peek(); if (t.Kind != TokKind.End) { pos++; lastLine = t.Line; } return t; }

    // Line of the most recently consumed token.
    public int LastLine => lastLine;
    public bool AtEnd => Peek().Kind == TokKind.End;
    public int Position { get => pos; set => pos = value; }

    public bool Accept(string text)
    {
        if (!Peek().Is(text)) return false;
        Next();
        return true;
    }

    public Token Expect(string text)
    {
        var t = Peek();
        if (!t.Is(text)) throw Error($"Expected '{text}' but found {t}", t);
        Next();
        return t;
    }

    public Token ExpectIdent(string what = "an identifier")
    {
        var t = Peek();
        if (t.Kind != TokKind.Ident) throw Error($"Expected {what} but found {t}", t);
        Next();
        return t;
    }

    // A possibly negative integer literal.
    public long ExpectInteger()
    {
        var neg = Accept("-");
        var t = Peek();
        if (t.Kind != TokKind.Number) throw Error($"Expected a number but found {t}", t);
        Next();
        return neg ? -t.Value : t.Value;
    }

    public static ScriptCompileException Error(string message, Token at) => new(message, at.Line, at.Col);
}
