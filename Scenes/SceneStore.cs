using System.Buffers.Binary;
using System.IO;
using LBA2LevelEditor.Lba1;

namespace LBA2LevelEditor.Scenes;

// Problems the validators found that stop a save. An IOException (via SceneEditException) so the editor's existing
// "Not saved: ..." handling shows it.
internal sealed class SceneValidationException : SceneEditException
{
    public IReadOnlyList<SceneIssue> Issues { get; }
    public SceneValidationException(IReadOnlyList<SceneIssue> issues)
        : base("The scene has problems the game would trip over:\n" + string.Join("\n", issues.Where(i => i.Severity == SceneIssueSeverity.Error).Take(12).Select(i => "  " + i)))
    {
        Issues = issues;
    }
}

internal sealed record SceneSaveResult(IReadOnlyList<SceneIssue> Issues, int RecordBytes);

// One scene to write: its model and, LBA1, its grid when that changes too.
internal sealed record SceneChange(int Scene, SceneModel Model, byte[]? Grid = null);

// A scene exactly as it is in the game files.
internal sealed record RawScene(int Scene, byte[] Record, byte[]? Grid);

// Another game file that has to change together with the scenes (a body added to BODY.HQR for an actor a scene now uses):
// it joins the save's transaction, so either everything is written or nothing is. SceneHistory doesn't undo it.
internal sealed record ExtraFile(string Path, byte[] Content, Func<byte[], string?>? Verify = null);

// Loads and saves scenes of one game folder as SceneModel (+ the grid, for LBA1). Saving validates first, writes the
// scene record with SceneSerializer, keeps LBA2's "largest scene" record (SCENE.HQR entry 0) true, and writes every
// touched file as one FileTransaction (one-time .bak copies, written beside and verified, then swapped in).
//
// Entry numbers: LBA1 stores scene N in entry N of SCENE.HQR, LBA_GRI.HQR and LBA_BLL.HQR. LBA2 stores scene N in
// entry N + 1; entry 0 is a 4-byte record holding the size of the largest scene, from which the engine sizes the buffer
// every scene is decompressed into (MEM.CPP: PtrSceneMem), so a bigger scene than that would overrun it.
internal sealed class SceneStore
{
    public SceneGame Game { get; }
    public string Directory { get; }

    public SceneStore(SceneGame game, string directory)
    {
        Game = game;
        Directory = directory;
    }

    public string ScenePath => Path.Combine(Directory, "SCENE.HQR");
    public string GridPath => Path.Combine(Directory, "LBA_GRI.HQR");
    public string LibraryPath => Path.Combine(Directory, "LBA_BLL.HQR");
    public string BrickPath => Path.Combine(Directory, "LBA_BRK.HQR");

    public int Entry(int scene) => Game == SceneGame.Lba1 ? scene : scene + 1;

    // Scenes the game has (LBA1 0..119, LBA2 0..221): the table's slots, less LBA2's size record.
    public int SceneCount => HqrFile.CountSlots(File.ReadAllBytes(ScenePath)) - (Game == SceneGame.Lba2 ? 1 : 0);

    public bool SceneExists(int scene) => scene >= 0 && HqrArchive.Open(ScenePath).IsValid(Entry(scene));

    public byte[] LoadRecord(int scene) => HqrArchive.Open(ScenePath).Read(Entry(scene));

    public SceneModel Load(int scene) => SceneSerializer.Parse(Game, LoadRecord(scene));

    public byte[] LoadGrid(int scene) => HqrArchive.Open(GridPath).Read(scene);

    public byte[] LoadLibrary(int scene) => HqrArchive.Open(LibraryPath).Read(scene);

    public SceneValidationOptions ValidationOptions()
    {
        var archive = HqrArchive.Open(ScenePath);
        var count = SceneCount;
        return new SceneValidationOptions { SceneCount = count, SceneExists = s => s >= 0 && s < count && archive.IsValid(Entry(s)) };
    }

    // Everything the validators say about the scene (and, LBA1, its grid) as it would be saved.
    public List<SceneIssue> Validate(int scene, SceneModel model, byte[]? grid = null)
    {
        var issues = SceneValidator.Validate(model, ValidationOptions());
        if (grid is not null && Game == SceneGame.Lba1)
        {
            var bricks = HqrArchive.Open(BrickPath);
            var sizes = new Dictionary<int, int>();
            int SizeOf(int b) => sizes.TryGetValue(b, out var s) ? s : sizes[b] = bricks.DecodedSize(b);
            issues.AddRange(Lba1GridValidator.Validate(grid, LoadLibrary(scene), SizeOf).Issues);
        }
        return issues;
    }

    // Saves the scene (and its grid). Errors from the validators stop the save unless `allowErrors`. With a
    // description the save goes on SceneHistory's undo log.
    public SceneSaveResult Save(int scene, SceneModel model, byte[]? grid = null, bool allowErrors = false, string? description = null)
        => SaveMany(new[] { new SceneChange(scene, model, grid) }, allowErrors, description);

    // Saves several scenes in one transaction (all or none), as one undo step. `extraFiles` are written in the same
    // transaction (see ExtraFile); an undo puts the scenes and grids back but leaves them as they are.
    public SceneSaveResult SaveMany(IReadOnlyList<SceneChange> changes, bool allowErrors = false, string? description = null, IReadOnlyList<ExtraFile>? extraFiles = null)
    {
        if (changes.Count == 0) return new SceneSaveResult(Array.Empty<SceneIssue>(), 0);
        var allIssues = new List<SceneIssue>();
        var records = new List<RawScene>();
        foreach (var change in changes)
        {
            if (change.Model.Game != Game) throw new InvalidOperationException("The scene belongs to the other game.");
            if (change.Grid is not null && Game != SceneGame.Lba1) throw new InvalidOperationException("Only LBA1 scenes have a grid in LBA_GRI.HQR.");
            var issues = Validate(change.Scene, change.Model, change.Grid);
            allIssues.AddRange(issues.Select(i => i with { Where = $"scene {change.Scene}: {i.Where}" }));
            byte[] record;
            try { record = SceneSerializer.Write(change.Model); }
            catch (SceneFormatException e) { throw new InvalidDataException($"Scene {change.Scene}: {e.Message}", e); }
            records.Add(new RawScene(change.Scene, record, change.Grid));
        }
        if (!allowErrors && allIssues.Any(i => i.Severity == SceneIssueSeverity.Error)) throw new SceneValidationException(allIssues);

        var before = description is null ? null : Snapshot(records.Select(r => r.Scene));
        Write(records, extraFiles);
        if (description is not null) SceneHistory.Record(new SceneHistoryEntry(description, Game, Directory, before!, records));
        return new SceneSaveResult(allIssues, records.Sum(r => r.Record.Length));
    }

    // Puts scenes back exactly as given (undo / redo): no validation, the bytes are taken as they are.
    internal void WriteRaw(IReadOnlyList<RawScene> scenes) => Write(scenes);

    // What the game files hold for these scenes now (their grids too, for LBA1).
    internal List<RawScene> Snapshot(IEnumerable<int> scenes)
        => scenes.Select(s => new RawScene(s, LoadRecord(s), Game == SceneGame.Lba1 ? LoadGrid(s) : null)).ToList();

    private void Write(IReadOnlyList<RawScene> scenes, IReadOnlyList<ExtraFile>? extraFiles = null)
    {
        var hqr = HqrFile.Parse(File.ReadAllBytes(ScenePath));
        foreach (var scene in scenes)
        {
            var entry = Entry(scene.Scene);
            if (entry < 0 || entry >= hqr.Count || hqr.IsEmpty(entry)) throw new InvalidDataException($"SCENE.HQR has no scene {scene.Scene} to replace.");
            hqr.SetStored(entry, scene.Record);
        }
        if (Game == SceneGame.Lba2) UpdateLargestSceneRecord(hqr);

        var transaction = new FileTransaction().Write(ScenePath, hqr.ToBytes(), written =>
        {
            var read = HqrFile.Parse(written);
            foreach (var scene in scenes)
                if (!read.Read(Entry(scene.Scene)).AsSpan().SequenceEqual(scene.Record)) return $"scene {scene.Scene} read back differently.";
            return null;
        });

        var grids = scenes.Where(s => s.Grid is not null).ToList();
        if (grids.Count > 0)
        {
            var gridFile = HqrFile.Parse(File.ReadAllBytes(GridPath));
            foreach (var scene in grids)
            {
                if (scene.Scene >= gridFile.Count || gridFile.IsEmpty(scene.Scene)) throw new InvalidDataException($"LBA_GRI.HQR has no grid {scene.Scene} to replace.");
                gridFile.SetStored(scene.Scene, scene.Grid!);
            }
            transaction.Write(GridPath, gridFile.ToBytes(), written =>
            {
                var read = HqrFile.Parse(written);
                foreach (var scene in grids)
                    if (!read.Read(scene.Scene).AsSpan().SequenceEqual(scene.Grid!)) return $"grid {scene.Scene} read back differently.";
                return null;
            });
        }
        foreach (var extra in extraFiles ?? Array.Empty<ExtraFile>()) transaction.Write(extra.Path, extra.Content, extra.Verify);
        transaction.Commit();
    }

    // Entry 0 of LBA2's SCENE.HQR: the size of the biggest scene record. Only ever raised: a larger buffer is harmless.
    private static void UpdateLargestSceneRecord(HqrFile hqr)
    {
        var current = hqr.IsEmpty(0) ? 0 : hqr.Read(0) is { Length: >= 4 } payload ? BinaryPrimitives.ReadInt32LittleEndian(payload) : 0;
        var largest = 0;
        for (var i = 1; i < hqr.Count; i++)
        {
            if (hqr.IsEmpty(i)) continue;
            var length = hqr.Read(i).Length;
            if (length > largest) largest = length;
        }
        if (largest <= current) return;
        var value = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(value, largest);
        hqr.SetStored(0, value);
    }
}

// One undoable step of the application-wide log: the scenes as they were before and as they are after a save.
internal sealed record SceneHistoryEntry(string Description, SceneGame Game, string Directory, IReadOnlyList<RawScene> Before, IReadOnlyList<RawScene> After);

// The editor's undo log for changes saved to the game files: every save that names itself lands here (zone and actor
// edits, script saves, the door tool), and Undo / Redo write the earlier or later scenes back.
internal static class SceneHistory
{
    private const int Limit = 100;
    private static readonly List<SceneHistoryEntry> undo = new();
    private static readonly List<SceneHistoryEntry> redo = new();

    public static event EventHandler? Changed;

    public static SceneHistoryEntry? NextUndo => undo.Count > 0 ? undo[^1] : null;
    public static SceneHistoryEntry? NextRedo => redo.Count > 0 ? redo[^1] : null;
    public static string? UndoDescription => NextUndo?.Description;
    public static string? RedoDescription => NextRedo?.Description;

    internal static void Record(SceneHistoryEntry entry)
    {
        undo.Add(entry);
        if (undo.Count > Limit) undo.RemoveAt(0);
        redo.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Clear()
    {
        undo.Clear();
        redo.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // Writes the previous state back. Returns the step, or null when there is nothing to undo. On a failed write the
    // step stays on the log.
    public static SceneHistoryEntry? Undo()
    {
        if (undo.Count == 0) return null;
        var entry = undo[^1];
        new SceneStore(entry.Game, entry.Directory).WriteRaw(entry.Before);
        undo.RemoveAt(undo.Count - 1);
        redo.Add(entry);
        Changed?.Invoke(null, EventArgs.Empty);
        return entry;
    }

    public static SceneHistoryEntry? Redo()
    {
        if (redo.Count == 0) return null;
        var entry = redo[^1];
        new SceneStore(entry.Game, entry.Directory).WriteRaw(entry.After);
        redo.RemoveAt(redo.Count - 1);
        undo.Add(entry);
        Changed?.Invoke(null, EventArgs.Empty);
        return entry;
    }
}
