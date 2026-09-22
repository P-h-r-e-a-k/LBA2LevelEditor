using LBAAssembler.LbaScript;

namespace ScriptRoundTrip;

// ISymbolSource over one *unedited* scene record: every actor's names derive
// from its original bytes (the scene-level editor will later swap in the
// freshly compiled tables for actors the user has changed).
internal sealed class SceneSymbols : ISymbolSource
{
    private readonly List<Dictionary<int, string>> trackNames = new();
    private readonly List<Dictionary<int, string>> lifeNames = new();

    public SceneSymbols(SceneRecord rec)
    {
        foreach (var a in rec.Actors)
        {
            trackNames.Add(TrackText.LabelNames(Bytecode.DecodeTrack(rec.Track(a))));
            lifeNames.Add(LifeText.FunctionNames(Bytecode.DecodeLife(rec.Life(a))));
        }
    }

    public string? TrackName(int actor, int offset) => Lookup(trackNames, actor, offset);
    public string? LifeName(int actor, int offset) => Lookup(lifeNames, actor, offset);
    public int? TrackOffset(int actor, string name) => Reverse(trackNames, actor, name);
    public int? LifeOffset(int actor, string name) => Reverse(lifeNames, actor, name);

    private static string? Lookup(List<Dictionary<int, string>> tables, int actor, int offset) =>
        (uint)actor < (uint)tables.Count && tables[actor].TryGetValue(offset, out var n) ? n : null;

    private static int? Reverse(List<Dictionary<int, string>> tables, int actor, string name)
    {
        if ((uint)actor >= (uint)tables.Count) return null;
        foreach (var (off, n) in tables[actor]) if (n == name) return off;
        return null;
    }
}
