using System.Text;

namespace LBA2LevelEditor.LbaScript;

// Comments in scripts.
//
// The bytecode has nowhere to keep comments, and the C text shown to the user is
// regenerated from the bytes every time a script is loaded. So comments live in
// a separate file (CommentStore) and are put back by matching them against the
// script that was loaded:
//
//   * A comment is anchored to an *instruction* (its index in the script), not to
//     a text position -- text is reformatted freely, instructions are not.
//   * Each stored set also carries a fingerprint per instruction of the script it
//     was written against. The fingerprint ignores everything that legitimately
//     shifts when other scripts change (jump targets, SET_TRACK / SET_COMPORTEMENT
//     offsets) and keeps everything else (opcode, operands, comparison...).
//   * On load the stored fingerprints are aligned against the loaded script's
//     (a diff). If they are identical every comment lands exactly; if the script
//     changed since, comments follow their statements through the alignment, and
//     any whose statement no longer exists are kept, visibly, at the end of the
//     script rather than dropped.

// Where a comment sits relative to the statement its anchor instruction starts.
public enum CommentPlace
{
    Before,         // own line(s) above the statement (above its C label, if it has one)
    BeforeCode,     // own line(s) between a C label and the statement it labels
    Trailing,       // same line, after the statement
    After,          // own line(s) below the whole statement (below its closing brace for if/while/switch)
    Inside,         // own line(s) just inside the block the statement opens (empty block bodies)
    BeforeBlock,    // own line(s) above a comportement function's header
    BlockTrailing,  // on a function header line
    End,            // after the last statement of the script
}

// One comment group. Lines are the text after "//" of each comment line, verbatim
// (so "// note" is stored as " note").
public sealed record ScriptComment(int Instr, CommentPlace Place, IReadOnlyList<string> Lines);

public sealed class CommentSet
{
    // One fingerprint per instruction of the script the comments were written against.
    public List<string> Fingerprints { get; init; } = new();
    public List<ScriptComment> Comments { get; init; } = new();
    public bool IsEmpty => Comments.Count == 0;
}

// Offset-independent fingerprint of one instruction: "OO" (opcode, hex) + 8 hex
// digits of an FNV-1a hash over the operands that carry meaning.
internal static class Fingerprint
{
    public static string Of(Instr i, ScriptKind kind)
    {
        var sb = new StringBuilder();
        if (kind == ScriptKind.Life)
        {
            var def = Opcodes.Life(i.Op)!;
            switch (def.Form)
            {
                case LifeForm.Cond: sb.Append(i.Func).Append(',').Append(i.FuncArg).Append(',').Append(i.Test).Append(',').Append(i.Value); break;
                case LifeForm.Switch: sb.Append(i.Func).Append(',').Append(i.FuncArg); break;
                case LifeForm.Case: sb.Append(i.Test).Append(',').Append(i.Value); break;
                default: AppendArgs(sb, def.Args, i); break;
            }
        }
        else AppendArgs(sb, Opcodes.Track(i.Op)!.Args, i);
        return i.Op.ToString("X2") + Fnv(sb.ToString()).ToString("X8");
    }

    // Jump targets and offset operands are byte offsets that move when a script (or
    // the script they point into) changes; hidden operands are runtime scratch.
    private static void AppendArgs(StringBuilder sb, ArgDef[] defs, Instr i)
    {
        for (var k = 0; k < i.A.Length; k++)
        {
            if (k < defs.Length)
            {
                var d = defs[k];
                if (d.Type == ArgType.Jump || d.Role is ArgRole.LifeOffset or ArgRole.TrackOffset or ArgRole.Hidden) continue;
            }
            sb.Append(i.A[k]).Append(',');
        }
        if (i.Str is not null) sb.Append('"').Append(i.Str);
    }

    private static uint Fnv(string s)
    {
        var h = 2166136261u;
        foreach (var c in s) { h ^= c; h *= 16777619u; }
        return h;
    }

    public static List<string> Sequence(IEnumerable<Instr> code, ScriptKind kind) => code.Select(i => Of(i, kind)).ToList();
}

// ---------------------------------------------------------------------------
// Reading comments out of user text
// ---------------------------------------------------------------------------

// One physical line of source split into code and comment. Comment is the text
// after "//" (block comments are normalised to that form: "/* x */" -> " x"), or
// null when the line has none.
internal sealed record SrcLine(int Number, string Code, string? Comment);

internal static class CommentScanner
{
    public static List<SrcLine> Scan(string text)
    {
        var result = new List<SrcLine>();
        var rawLines = text.Replace("\r\n", "\n").Split('\n');
        var inBlock = false;

        for (var n = 0; n < rawLines.Length; n++)
        {
            var raw = rawLines[n];
            var code = new StringBuilder();
            var pieces = new List<string>();
            var block = new StringBuilder();
            var inString = false;
            var continued = inBlock;   // this line starts inside a block comment

            for (var c = 0; c < raw.Length; c++)
            {
                var ch = raw[c];
                if (inBlock)
                {
                    if (ch == '*' && c + 1 < raw.Length && raw[c + 1] == '/') { inBlock = false; c++; pieces.Add(Block(block, continued)); block.Clear(); continued = false; }
                    else block.Append(ch);
                    continue;
                }
                if (inString)
                {
                    code.Append(ch);
                    if (ch == '\\' && c + 1 < raw.Length) code.Append(raw[++c]);
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; code.Append(ch); continue; }
                if (ch == '/' && c + 1 < raw.Length && raw[c + 1] == '/') { pieces.Add(raw[(c + 2)..].TrimEnd()); break; }
                if (ch == '/' && c + 1 < raw.Length && raw[c + 1] == '*') { inBlock = true; c++; continue; }
                code.Append(ch);
            }
            if (inBlock) pieces.Add(Block(block, continued));

            result.Add(new SrcLine(n + 1, code.ToString().Trim(), pieces.Count == 0 ? null : string.Join(" ", pieces)));
        }
        return result;
    }

    private static string Block(StringBuilder b, bool continuation)
    {
        var s = b.ToString().Trim();
        if (continuation && s.StartsWith('*')) s = s[1..].Trim();   // " * text" continuation style
        return s.Length == 0 ? "" : " " + s;
    }

    // Removes string literal contents so braces inside strings are not counted.
    public static string StripStrings(string code)
    {
        var sb = new StringBuilder(code.Length);
        var inString = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (inString)
            {
                if (ch == '\\') i++;
                else if (ch == '"') inString = false;
            }
            else if (ch == '"') inString = true;
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}

internal static class CommentExtractor
{
    // Turns the comments of `text` (the script as the user wrote it) into anchored
    // comments. `code` is what that text compiled to (each instruction carrying the
    // source line it came from); `blocks` the function headers it defined. `header`
    // is the generated first-line comment, which is regenerated on load and so
    // never stored.
    public static CommentSet Extract(string text, string? header, IReadOnlyList<Instr> code,
        IReadOnlyList<(int HeaderLine, int FirstInstr)> blocks, ScriptKind kind)
    {
        var set = new CommentSet { Fingerprints = Fingerprint.Sequence(code, kind) };
        var src = CommentScanner.Scan(text);
        var byNumber = src.ToDictionary(l => l.Number);

        var firstInstr = new Dictionary<int, int>();          // source line -> its first instruction
        for (var i = 0; i < code.Count; i++)
            if (code[i].Line > 0) firstInstr.TryAdd(code[i].Line, i);
        var instrLines = firstInstr.Keys.OrderBy(k => k).ToArray();

        var blockAt = new Dictionary<int, int>();             // function header line -> first instruction
        foreach (var (headerLine, first) in blocks) blockAt.TryAdd(headerLine, first);

        int NextInstrAfter(int line)
        {
            foreach (var l in instrLines) if (l > line) return firstInstr[l];
            return -1;
        }

        // Brace structure: for a line that closes a statement, the line that statement started on.
        // Handles both `if (c) {` and braces on their own line: a lone `{` belongs to the header line
        // just above it, and `}` + `else` + `{` continue the same if statement.
        var endsStatement = new Dictionary<int, int>();
        var openLineHeader = new Dictionary<int, int>();      // a line that is just "{" -> the header line above it
        var codeLines = src.Where(l => l.Code.Length > 0).ToList();
        var stack = new Stack<int>();
        var pendingBlockStart = -1;                           // start of an if whose block closed and whose `else` follows
        var lastHeader = -1;                                  // last code line that is not just a brace
        for (var ix = 0; ix < codeLines.Count; ix++)
        {
            var l = codeLines[ix];
            var stripped = CommentScanner.StripStrings(l.Code);
            var trimmed = stripped.Trim();
            var braceOnly = trimmed == "{";
            var inherit = -1;
            foreach (var ch in stripped)
            {
                if (ch == '}') { if (stack.Count > 0) inherit = stack.Pop(); }
                else if (ch == '{')
                {
                    var start = inherit >= 0 ? inherit : pendingBlockStart >= 0 ? pendingBlockStart : braceOnly && lastHeader >= 0 ? lastHeader : l.Number;
                    if (braceOnly && lastHeader >= 0) openLineHeader[l.Number] = lastHeader;
                    stack.Push(start);
                    inherit = -1;
                    pendingBlockStart = -1;
                }
            }
            if (inherit >= 0)
            {
                var nextIsElse = ix + 1 < codeLines.Count && StartsWithWord(codeLines[ix + 1].Code, "else");
                if (nextIsElse) pendingBlockStart = inherit; else endsStatement[l.Number] = inherit;
            }
            if (trimmed != "{" && trimmed != "}") lastHeader = l.Number;
        }

        static bool StartsWithWord(string code, string word) =>
            code.StartsWith(word, StringComparison.Ordinal) && (code.Length == word.Length || !(char.IsLetterOrDigit(code[word.Length]) || code[word.Length] == '_'));

        void Add(int instr, CommentPlace place, params string[] lines)
        {
            // Consecutive groups at the same place keep their order as separate entries.
            set.Comments.Add(new ScriptComment(instr, place, lines));
        }

        void AddEnd(params string[] lines) => set.Comments.Add(new ScriptComment(-1, CommentPlace.End, lines));

        void AddBefore(int line, string[] lines)
        {
            var i = firstInstr.TryGetValue(line, out var f) ? f : NextInstrAfter(line);
            if (i >= 0) Add(i, CommentPlace.Before, lines); else AddEnd(lines);
        }

        void AnchorGroup(List<string> lines, SrcLine next, int prevCode)
        {
            var arr = lines.ToArray();
            if (blockAt.TryGetValue(next.Number, out var blockFirst)) { Add(blockFirst, CommentPlace.BeforeBlock, arr); return; }

            if (next.Code.StartsWith('}') && prevCode > 0 && !blockAt.ContainsKey(prevCode))
            {
                // A comment just before a closing brace belongs to the end of the block it closes.
                var prev = byNumber[prevCode];
                if (endsStatement.TryGetValue(prevCode, out var start) && firstInstr.TryGetValue(start, out var si)) { Add(si, CommentPlace.After, arr); return; }
                // Right after a lone `{` (an empty body): the comment goes inside the block that header opens.
                if (openLineHeader.TryGetValue(prevCode, out var hdrLine) && firstInstr.TryGetValue(hdrLine, out var hi)) { Add(hi, CommentPlace.Inside, arr); return; }
                if (firstInstr.TryGetValue(prevCode, out var pi))
                {
                    // Right after a line that opens a block (an empty body) the comment goes inside it.
                    var opensBlock = CommentScanner.StripStrings(prev.Code).TrimEnd().EndsWith('{');
                    Add(pi, opensBlock ? CommentPlace.Inside : CommentPlace.After, arr);
                    return;
                }
            }
            // Right after a label line, above the statement it labels: keep it below the label.
            if (prevCode > 0 && !firstInstr.ContainsKey(prevCode) && !blockAt.ContainsKey(prevCode) && byNumber[prevCode].Code.EndsWith(':')
                && firstInstr.TryGetValue(next.Number, out var labelled))
            {
                Add(labelled, CommentPlace.BeforeCode, arr);
                return;
            }
            AddBefore(next.Number, arr);
        }

        var headerPending = header is not null;
        var pending = new List<string>();
        var prevCodeLine = -1;

        foreach (var l in src)
        {
            if (l.Code.Length == 0)
            {
                if (l.Comment is null) continue;
                if (headerPending && l.Comment.Trim() == header) { headerPending = false; continue; }
                pending.Add(l.Comment);
                continue;
            }

            headerPending = false;
            if (pending.Count > 0) { AnchorGroup(pending, l, prevCodeLine); pending = new List<string>(); }

            if (l.Comment is not null)
            {
                if (firstInstr.TryGetValue(l.Number, out var i)) Add(i, CommentPlace.Trailing, l.Comment);
                else if (blockAt.TryGetValue(l.Number, out var b)) Add(b, CommentPlace.BlockTrailing, l.Comment);
                else if (endsStatement.TryGetValue(l.Number, out var start) && firstInstr.TryGetValue(start, out var si)) Add(si, CommentPlace.After, l.Comment);
                else AddBefore(l.Number, new[] { l.Comment });
            }
            prevCodeLine = l.Number;
        }
        if (pending.Count > 0) AddEnd(pending.ToArray());

        return set;
    }
}

// ---------------------------------------------------------------------------
// Matching stored comments to the script that was loaded
// ---------------------------------------------------------------------------

internal static class CommentAligner
{
    // For each old instruction index the index of the corresponding instruction in
    // the new sequence, or -1. Common prefix/suffix first, a longest-common-
    // subsequence over what is left, then instructions changed *in place* (same
    // number of instructions between two matches, or same opcode in order) are
    // paired up too so a comment on an edited statement follows it.
    public static int[] Align(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count, m = b.Count;
        var map = new int[n];
        Array.Fill(map, -1);

        var p = 0;
        while (p < n && p < m && a[p] == b[p]) { map[p] = p; p++; }
        var s = 0;
        while (s < n - p && s < m - p && a[n - 1 - s] == b[m - 1 - s]) { map[n - 1 - s] = m - 1 - s; s++; }

        int an = n - p - s, bn = m - p - s;
        if (an <= 0 || bn <= 0) return map;

        var pairs = new List<(int I, int J)>();
        if ((long)an * bn <= 6_000_000)
        {
            var w = bn + 1;
            var dp = new int[(an + 1) * w];
            for (var i = 1; i <= an; i++)
                for (var j = 1; j <= bn; j++)
                    dp[i * w + j] = a[p + i - 1] == b[p + j - 1] ? dp[(i - 1) * w + j - 1] + 1 : Math.Max(dp[(i - 1) * w + j], dp[i * w + j - 1]);
            for (int i = an, j = bn; i > 0 && j > 0;)
            {
                if (a[p + i - 1] == b[p + j - 1]) { pairs.Add((p + i - 1, p + j - 1)); i--; j--; }
                else if (dp[(i - 1) * w + j] >= dp[i * w + j - 1]) i--;
                else j--;
            }
            pairs.Reverse();
            foreach (var (i, j) in pairs) map[i] = j;
        }

        // Gaps between consecutive matches.
        int prevI = p - 1, prevJ = p - 1;
        pairs.Add((p + an, p + bn));
        foreach (var (i, j) in pairs)
        {
            var oldCount = i - prevI - 1;
            var newCount = j - prevJ - 1;
            if (oldCount > 0 && newCount > 0)
            {
                if (oldCount == newCount)
                    for (var k = 0; k < oldCount; k++) map[prevI + 1 + k] = prevJ + 1 + k;
                else
                {
                    var jj = prevJ + 1;
                    for (var ii = prevI + 1; ii < i; ii++)
                        for (var k = jj; k < j; k++)
                            if (string.CompareOrdinal(a[ii], 0, b[k], 0, 2) == 0) { map[ii] = k; jj = k + 1; break; }
                }
            }
            prevI = i; prevJ = j;
        }
        return map;
    }
}

internal sealed record WovenScript(string Text, int Detached);

internal static class CommentWeaver
{
    public const string DetachedMarker = " (the comments below lost their statement: it was changed or removed since they were saved)";

    // Puts `stored` comments into the freshly decompiled `d`.
    public static WovenScript Apply(DecompiledScript d, CommentSet? stored, ScriptKind kind)
    {
        if (stored is null || stored.Comments.Count == 0) return new WovenScript(d.Text, 0);

        var current = Fingerprint.Sequence(d.Code, kind);
        var map = stored.Fingerprints.SequenceEqual(current)
            ? Enumerable.Range(0, current.Count).ToArray()
            : CommentAligner.Align(stored.Fingerprints, current);

        var lines = d.Lines;
        var above = new Dictionary<int, List<string>>();
        var below = new Dictionary<int, List<string>>();
        var trailing = new Dictionary<int, List<string>>();
        var endLines = new List<string>();
        var detached = new List<string>();
        var detachedCount = 0;

        // Files comment lines under `fileLine`, indented like `indentLine` (plus extra).
        void Put(Dictionary<int, List<string>> where, int fileLine, int indentLine, IEnumerable<string> text, int extraIndent = 0)
        {
            if (!where.TryGetValue(fileLine, out var list)) where[fileLine] = list = new List<string>();
            var indent = new string(' ', Indent(lines[indentLine]) + extraIndent);
            foreach (var t in text) list.Add(indent + "//" + t);
        }

        foreach (var c in stored.Comments)
        {
            if (c.Place == CommentPlace.End) { endLines.AddRange(c.Lines.Select(t => "//" + t)); continue; }

            var idx = c.Instr >= 0 && c.Instr < map.Length ? map[c.Instr] : -1;
            var line = idx < 0 ? -1 : c.Place switch
            {
                CommentPlace.Before => First(d.BeforeLine[idx], d.CodeLine[idx]),
                CommentPlace.BeforeCode => First(d.CodeLine[idx], d.BeforeLine[idx]),
                CommentPlace.Trailing => d.CodeLine[idx],
                CommentPlace.After => First(d.EndLine[idx], d.CodeLine[idx]),
                CommentPlace.Inside => d.CodeLine[idx],
                // A function comment follows the function that now contains its instruction, so
                // it survives statements being inserted in front of the old first one.
                CommentPlace.BeforeBlock or CommentPlace.BlockTrailing => EnclosingHeader(idx) is var hl && hl >= 0 ? hl : First(d.BeforeLine[idx], d.CodeLine[idx]),
                _ => -1,
            };
            if (line < 0 || line >= lines.Length)
            {
                detachedCount++;
                detached.AddRange(c.Lines.Select(t => "//" + t));
                continue;
            }

            switch (c.Place)
            {
                case CommentPlace.Before: case CommentPlace.BeforeCode:
                    // Above a closing brace means "at the end of the block being closed": indent as its contents.
                    Put(above, line, line, c.Lines, lines[line].TrimStart().StartsWith('}') ? 4 : 0);
                    break;
                case CommentPlace.BeforeBlock: Put(above, line, line, c.Lines); break;
                case CommentPlace.After:
                {
                    // Filed below the statement's last line, indented like its first line
                    // (one level deeper when that first line is a `case x:` / `default:`).
                    var first = d.CodeLine[idx] >= 0 && d.CodeLine[idx] < lines.Length ? d.CodeLine[idx] : line;
                    Put(below, line, first, c.Lines, lines[first].TrimEnd().EndsWith(':') ? 4 : 0);
                    break;
                }
                case CommentPlace.Inside:
                {
                    // The block's `{` is on its own line just below the header: file the comment under it.
                    var open = line + 1 < lines.Length && lines[line + 1].Trim() == "{" ? line + 1 : line;
                    Put(below, open, line, c.Lines, 4);
                    break;
                }
                case CommentPlace.Trailing: case CommentPlace.BlockTrailing:
                    if (!trailing.TryGetValue(line, out var tl)) trailing[line] = tl = new List<string>();
                    tl.AddRange(c.Lines.Select(t => "//" + t));
                    break;
            }
        }

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (above.TryGetValue(i, out var a)) foreach (var t in a) sb.Append(t).Append('\n');
            sb.Append(lines[i]);
            if (trailing.TryGetValue(i, out var tr)) foreach (var t in tr) sb.Append("  ").Append(t);
            sb.Append('\n');
            if (below.TryGetValue(i, out var b)) foreach (var t in b) sb.Append(t).Append('\n');
        }
        if (endLines.Count > 0 || detached.Count > 0)
        {
            if (lines.Length > 0) sb.Append('\n');
            foreach (var t in endLines) sb.Append(t).Append('\n');
            if (detached.Count > 0)
            {
                sb.Append("//").Append(DetachedMarker).Append('\n');
                foreach (var t in detached) sb.Append(t).Append('\n');
            }
        }
        return new WovenScript(sb.ToString(), detachedCount);

        static int First(int preferred, int fallback) => preferred >= 0 ? preferred : fallback;

        // Header line of the function whose first instruction is the last one at or before idx.
        int EnclosingHeader(int idx)
        {
            var bestFirst = -1;
            var header = -1;
            foreach (var (first, hdr) in d.BlockHeaderLine)
                if (first <= idx && first > bestFirst) { bestFirst = first; header = hdr; }
            return header;
        }
        static int Indent(string line) { var n = 0; while (n < line.Length && line[n] == ' ') n++; return n; }
    }
}
