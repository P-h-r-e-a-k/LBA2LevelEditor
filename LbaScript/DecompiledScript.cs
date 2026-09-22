namespace LBAAssembler.LbaScript;

// A decompiled script: its C text plus, for every instruction of the script, the
// lines of that text it was written on. The comment layer uses this to put a
// comment back next to the statement it was attached to.
//
// All line numbers are 0-based indices into Lines. Per instruction (-1 = not
// printed anywhere, e.g. the implicit closing END):
//   BeforeLine  first line belonging to the instruction's statement, including a
//               C label that precedes it -- where an "above this statement"
//               comment goes;
//   CodeLine    the line the instruction itself is printed on -- where a
//               trailing comment goes (all instructions of an `if (a && b)`
//               chain share one line);
//   EndLine     the last line of the statement that starts at this instruction
//               (its closing brace for an if / while / switch) -- where a
//               "below this statement" comment goes.
internal sealed class DecompiledScript
{
    public required string Text { get; init; }
    public required IReadOnlyList<Instr> Code { get; init; }
    public required int[] BeforeLine { get; init; }
    public required int[] CodeLine { get; init; }
    public required int[] EndLine { get; init; }

    // Life scripts: index of a function's first instruction -> the line of its
    // `void comportement_N() {` header.
    public required IReadOnlyDictionary<int, int> BlockHeaderLine { get; init; }

    public string[] Lines => Split(Text);

    public static string[] Split(string text)
    {
        var lines = text.Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
