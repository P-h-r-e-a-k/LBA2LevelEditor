namespace LBA2LevelEditor;

// Scans every actor script already resident in the native renderer (it scans
// the whole island's SCENE.HQR into memory once per island load anyway --
// see RendererScanIslandActors -- so re-reading GetActorScript(i) for all of
// them here is just string work, not a fresh decode) and counts how often
// each distinct line appears across all of them. The most frequent lines are
// offered in the script editor's autocomplete as ready-made snippets, on the
// idea that a line five different NPCs already use is a reasonable thing to
// suggest for a sixth -- this is what the user meant by "scan the inbuilt
// actor scripts and look for common code structures".
internal sealed class ScriptSuggestionIndex
{
    public sealed record LineSuggestion(string Text, int Occurrences);

    public IReadOnlyList<LineSuggestion> CommonLines { get; }

    private ScriptSuggestionIndex(IReadOnlyList<LineSuggestion> commonLines) => CommonLines = commonLines;

    public static ScriptSuggestionIndex Empty { get; } = new(Array.Empty<LineSuggestion>());

    // Lines that are pure boilerplate (section headers, "no script" markers,
    // decode-failure fallbacks) or that only ever make sense once per script
    // (END/RETURN/ENDIF and friends already have their own opcode-list
    // entries) are excluded so the snippet list highlights actually
    // reusable, parameterized lines instead of being dominated by them.
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "END", "RETURN", "END_LIFE", "SUICIDE", "NOP", "ENDIF", "END_COMPORTEMENT",
        "(no life script)", "(no track script)",
    };

    public static ScriptSuggestionIndex Build(RendererLibraryApi? library)
    {
        if (library is null) return Empty;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var count = library.GetActorCount();
        for (var i = 0; i < count; i++)
        {
            var script = library.GetActorScript(i);
            if (string.IsNullOrEmpty(script)) continue;
            foreach (var rawLine in script.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("--", StringComparison.Ordinal)) continue; // "-- Life script --" etc.
                if (line.StartsWith("[", StringComparison.Ordinal)) continue; // decode-failure fallback lines
                if (Excluded.Contains(line)) continue;
                counts[line] = counts.GetValueOrDefault(line) + 1;
            }
        }

        var ranked = counts
            .Where(pair => pair.Value > 1) // "common" implies seen more than once
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(60)
            .Select(pair => new LineSuggestion(pair.Key, pair.Value))
            .ToList();
        return ranked.Count == 0 ? Empty : new ScriptSuggestionIndex(ranked);
    }
}
