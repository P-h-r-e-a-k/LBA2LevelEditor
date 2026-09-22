namespace LBAAssembler.Terrain;

// Undo / redo for island edits. An edit is bracketed by Begin() and Commit(): Begin copies the island's editable data,
// Commit compares it with the island as it is now and keeps only the cubes that changed (before and after), so a long
// session of brush strokes stays small.
internal sealed class IslandHistory
{
    private sealed class CubeState
    {
        public short[] Heights = Array.Empty<short>();
        public byte[] Intensity = Array.Empty<byte>();
        public uint[] Polygons = Array.Empty<uint>();
        public ushort[] TextureDefs = Array.Empty<ushort>();
        public int[] Info = Array.Empty<int>();
        public List<byte[]> Decors = new();

        public static CubeState Of(IslandCube cube) => new()
        {
            Heights = (short[])cube.Heights.Clone(), Intensity = (byte[])cube.Intensity.Clone(), Polygons = (uint[])cube.Polygons.Clone(),
            TextureDefs = (ushort[])cube.TextureDefs.Clone(), Info = (int[])cube.Info.Clone(), Decors = cube.Decors.Select(d => (byte[])d.Raw.Clone()).ToList(),
        };

        public bool SameAs(CubeState other) =>
            Heights.AsSpan().SequenceEqual(other.Heights) && Intensity.AsSpan().SequenceEqual(other.Intensity) && Polygons.AsSpan().SequenceEqual(other.Polygons)
            && TextureDefs.AsSpan().SequenceEqual(other.TextureDefs) && Info.AsSpan().SequenceEqual(other.Info)
            && Decors.Count == other.Decors.Count && Decors.Zip(other.Decors).All(p => p.First.AsSpan().SequenceEqual(p.Second));

        public void ApplyTo(IslandCube cube)
        {
            cube.Heights = (short[])Heights.Clone(); cube.Intensity = (byte[])Intensity.Clone(); cube.Polygons = (uint[])Polygons.Clone();
            cube.TextureDefs = (ushort[])TextureDefs.Clone(); cube.Info = (int[])Info.Clone();
            cube.Decors = Decors.Select(raw => new IslandDecor((byte[])raw.Clone())).ToList();
        }
    }

    private sealed record Entry(string Label, Dictionary<int, (CubeState Before, CubeState After)> Cubes);

    private readonly IslandFile island;
    private readonly List<Entry> undo = new();
    private readonly List<Entry> redo = new();
    private Dictionary<int, CubeState>? pending;

    public IslandHistory(IslandFile island) => this.island = island;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string? UndoLabel => undo.Count > 0 ? undo[^1].Label : null;
    public string? RedoLabel => redo.Count > 0 ? redo[^1].Label : null;
    // Raised after an undo or redo so the view can redraw.
    public event Action? Changed;

    // True when something was committed since the island was loaded or last marked saved.
    public bool Dirty { get; private set; }
    public void MarkSaved() => Dirty = false;

    public void Begin() => pending = island.Cubes.ToDictionary(kv => kv.Key, kv => CubeState.Of(kv.Value));

    // Returns false (and records nothing) when the edit changed nothing.
    public bool Commit(string label)
    {
        if (pending is null) return false;
        var changed = new Dictionary<int, (CubeState, CubeState)>();
        foreach (var (id, before) in pending)
        {
            var now = CubeState.Of(island.Cubes[id]);
            if (!before.SameAs(now)) changed[id] = (before, now);
        }
        pending = null;
        if (changed.Count == 0) return false;
        undo.Add(new Entry(label, changed));
        redo.Clear();
        Dirty = true;
        return true;
    }

    public void Cancel() => pending = null;

    public bool Undo()
    {
        if (undo.Count == 0) return false;
        var entry = undo[^1]; undo.RemoveAt(undo.Count - 1);
        foreach (var (id, (before, _)) in entry.Cubes) before.ApplyTo(island.Cubes[id]);
        redo.Add(entry);
        Dirty = true;
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (redo.Count == 0) return false;
        var entry = redo[^1]; redo.RemoveAt(redo.Count - 1);
        foreach (var (id, (_, after)) in entry.Cubes) after.ApplyTo(island.Cubes[id]);
        undo.Add(entry);
        Dirty = true;
        Changed?.Invoke();
        return true;
    }
}
