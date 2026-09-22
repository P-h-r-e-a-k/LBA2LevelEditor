using System.Buffers.Binary;
using System.IO;
using LBAAssembler.Lba1;

namespace LBAAssembler.Scenes;

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

// One entry of one HQR archive a save changed, addressed by its path relative to the game folder (portable, and
// small to keep): Before/After are that entry's own decoded payload (HqrFile.Read's shape), never the whole
// archive -- a save that only touches one entry of a multi-megabyte file (BODY.HQR, LBA_BKG.HQR, SPRITES.HQR)
// only ever keeps that one entry's bytes on the undo log, not a copy of the file it lives in. Null means the slot
// was empty / didn't exist at that point (a brand new entry has Before = null).
internal sealed record HqrEntryChange(string RelativePath, int Entry, byte[]? Before, byte[]? After);

// A small sidecar text file (an .HQD entry-description list, see HqdWriter) a save wrote, kept whole: unlike an
// HQR archive these are only ever a few KB even for a big file, so entry-level tracking isn't worth the bother.
internal sealed record TextFileChange(string RelativePath, string? Before, string? After);

// The rare fallback for a change that reshapes an archive's own slot table (inserts, not just replaces or
// appends an entry) rather than touching one entry's bytes -- HqrEntryStore's Edit model doesn't support that,
// so this one case keeps the whole file, before and after. See GphLibrary.Save's own comment for the one place
// this is actually used (LBA2 bricks: a new brick's slot is inserted before the table that follows the brick
// range, shifting every later entry's number by one).
internal sealed record WholeFileChange(string RelativePath, byte[]? Before, byte[] After);

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

    // Saves several scenes in one transaction (all or none), as one undo step. `extraEdits`/`extraTexts` join the
    // same transaction (see HqrEntryStore) -- Lba1SurpriseChanges adds its elf's body this way -- and are on the
    // same undo step too: Undo/Redo cover them exactly as they cover the scenes, and only ever keep the entries
    // that actually changed, not a copy of the archive they live in.
    public SceneSaveResult SaveMany(IReadOnlyList<SceneChange> changes, bool allowErrors = false, string? description = null,
        IReadOnlyList<HqrEntryStore.Edit>? extraEdits = null, IReadOnlyList<HqrEntryStore.TextEdit>? extraTexts = null)
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
        var (entries, texts) = Write(records, extraEdits, extraTexts);
        if (description is not null) SceneHistory.Record(new SceneHistoryEntry(description, Game, Directory, before!, records, entries, texts, Array.Empty<WholeFileChange>()));
        return new SceneSaveResult(allIssues, records.Sum(r => r.Record.Length));
    }

    // Puts scenes back exactly as given (undo / redo): no validation, the bytes are taken as they are.
    internal void WriteRaw(IReadOnlyList<RawScene> scenes) => Write(scenes, null, null);

    // What the game files hold for these scenes now (their grids too, for LBA1).
    internal List<RawScene> Snapshot(IEnumerable<int> scenes)
        => scenes.Select(s => new RawScene(s, LoadRecord(s), Game == SceneGame.Lba1 ? LoadGrid(s) : null)).ToList();

    private (List<HqrEntryChange> Entries, List<TextFileChange> Texts) Write(IReadOnlyList<RawScene> scenes, IReadOnlyList<HqrEntryStore.Edit>? extraEdits, IReadOnlyList<HqrEntryStore.TextEdit>? extraTexts)
    {
        var transaction = new FileTransaction();
        if (scenes.Count > 0)
        {
            var hqr = HqrFile.Parse(File.ReadAllBytes(ScenePath));
            foreach (var scene in scenes)
            {
                var entry = Entry(scene.Scene);
                if (entry < 0 || entry >= hqr.Count || hqr.IsEmpty(entry)) throw new InvalidDataException($"SCENE.HQR has no scene {scene.Scene} to replace.");
                hqr.SetStored(entry, scene.Record);
            }
            if (Game == SceneGame.Lba2) UpdateLargestSceneRecord(hqr);

            transaction.Write(ScenePath, hqr.ToBytes(), written =>
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
        }
        var entries = HqrEntryStore.Compose(Directory, extraEdits ?? Array.Empty<HqrEntryStore.Edit>(), transaction);
        var texts = HqrEntryStore.ComposeText(Directory, extraTexts ?? Array.Empty<HqrEntryStore.TextEdit>(), transaction);
        transaction.Commit();
        return (entries, texts);
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

// One undoable step of the application-wide log: the scenes as they were before and as they are after a save,
// plus (see HqrEntryStore) any raw HQR entries, text sidecars, or -- rarely -- whole files the same save touched.
internal sealed record SceneHistoryEntry(string Description, SceneGame Game, string Directory,
    IReadOnlyList<RawScene> Before, IReadOnlyList<RawScene> After,
    IReadOnlyList<HqrEntryChange> Entries, IReadOnlyList<TextFileChange> Texts, IReadOnlyList<WholeFileChange> WholeFiles);

// The editor's undo log for changes saved to the game files: every save that names itself lands here (zone and actor
// edits, script saves, the door tool), and Undo / Redo write the earlier or later scenes back.
//
// Steps are kept on disk (Load/PersistPath) so they carry over between sessions, capped by two independently
// configurable limits (MaxSteps, MaxBytes -- both settable from Tools > Settings): once either is reached, adding a
// new step drops the oldest one first, same as it always silently did at a fixed 100-step limit. The one case that
// isn't silent: reaching the BYTE limit before the step limit asks first (ConfirmClearWhenFull, a hook the caller
// wires up once, so this file stays free of any UI dependency) whether to empty the whole log instead of trimming
// it one step at a time -- declining just falls back to trimming.
internal static class SceneHistory
{
    private static readonly List<SceneHistoryEntry> undo = new();
    private static readonly List<SceneHistoryEntry> redo = new();

    public static event EventHandler? Changed;

    // Tools > Settings' own fields; kept here (not read from EditorSettings directly) so this file has no
    // dependency on that one either -- EditorSettings copies them across whenever settings are loaded or saved.
    // Lowering either one trims the log immediately, so the Settings dialog's own "N steps stored" readout
    // (and the log actually kept on disk) never sits above a limit the user just tightened.
    private static int maxSteps = 100;
    public static int MaxSteps { get => maxSteps; set { maxSteps = value; TrimToLimits(); } }

    private static long maxBytes = 50 * 1024 * 1024;
    public static long MaxBytes { get => maxBytes; set { maxBytes = value; TrimToLimits(); } }

    // Drops the oldest step(s) -- undo first, then redo -- until both limits hold. A no-op call (nothing to
    // trim) never persists or fires Changed, so setting these at startup before Load() populates the log costs
    // nothing.
    private static void TrimToLimits()
    {
        var trimmed = false;
        while (undo.Count > maxSteps) { undo.RemoveAt(0); trimmed = true; }
        while (CurrentBytes > maxBytes && (undo.Count > 0 || redo.Count > 0))
        {
            if (undo.Count > 0) undo.RemoveAt(0); else redo.RemoveAt(0);
            trimmed = true;
        }
        if (!trimmed) return;
        Persist();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // Where the log is kept between sessions, or null (the default) to keep it in memory only for this process --
    // tools/ScriptRoundTrip's own tests call Record/Clear thousands of times over a run and never set this, so
    // they never touch disk for it. MainWindow sets this once, to a file next to the exe, and calls Load()
    // straight after.
    public static string? PersistPath { get; set; }

    // Asked once, only when a new step would push the log over MaxBytes with room still left under MaxSteps:
    // (bytes currently used, MaxBytes) -> true to empty the whole log and make room that way, false to fall back
    // to trimming the oldest steps one at a time instead (same as running into MaxSteps does, just silently).
    // Never asked when nothing is being dropped either way (nothing recorded yet, or the new step fits as is).
    public static Func<long, long, bool>? ConfirmClearWhenFull { get; set; }

    public static SceneHistoryEntry? NextUndo => undo.Count > 0 ? undo[^1] : null;
    public static SceneHistoryEntry? NextRedo => redo.Count > 0 ? redo[^1] : null;
    public static string? UndoDescription => NextUndo?.Description;
    public static string? RedoDescription => NextRedo?.Description;
    public static int UndoCount => undo.Count;
    public static int RedoCount => redo.Count;

    // How much of the configured MaxBytes is currently used (both stacks: a step sitting on Redo, waiting for a
    // possible re-do, still occupies its own share of the budget).
    public static long CurrentBytes => undo.Sum(SizeOf) + redo.Sum(SizeOf);

    // The save that fills entry has already happened by the time this runs (see SceneStore.Save), so this never
    // refuses to record it: at worst -- one step alone bigger than the whole configured MaxBytes -- eviction
    // empties the log and that one step is kept anyway, putting CurrentBytes over budget until the next step
    // that isn't oversized trims back down. That's the only case where this ever exceeds MaxBytes.
    internal static void Record(SceneHistoryEntry entry)
    {
        var entrySize = SizeOf(entry);
        if (undo.Count > 0 && CurrentBytes + entrySize > MaxBytes)
        {
            if (ConfirmClearWhenFull?.Invoke(CurrentBytes, MaxBytes) == true)
            {
                undo.Clear();
                redo.Clear();
            }
            else
            {
                while (undo.Count > 0 && CurrentBytes + entrySize > MaxBytes) undo.RemoveAt(0);
            }
        }
        undo.Add(entry);
        while (undo.Count > MaxSteps) undo.RemoveAt(0);
        redo.Clear();
        Persist();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Clear()
    {
        undo.Clear();
        redo.Clear();
        Persist();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // Whether the game files a step touches are still exactly what that step expects (After for an undo step,
    // Before for a redo step -- useAfter picks which) -- false means something else changed them since (another
    // tool, a step from a persisted log whose game files were edited outside the app while it wasn't running,
    // ...), so writing the step's own recorded bytes over them now would silently throw that other change away.
    private static bool MatchesCurrent(SceneHistoryEntry entry, bool useAfter)
    {
        var store = new SceneStore(entry.Game, entry.Directory);
        foreach (var s in useAfter ? entry.After : entry.Before)
        {
            byte[] current;
            try { current = store.LoadRecord(s.Scene); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { return false; }
            if (!current.AsSpan().SequenceEqual(s.Record)) return false;
            if (s.Grid is null) continue;
            byte[] currentGrid;
            try { currentGrid = store.LoadGrid(s.Scene); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { return false; }
            if (!currentGrid.AsSpan().SequenceEqual(s.Grid)) return false;
        }
        foreach (var group in entry.Entries.GroupBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            HqrFile file;
            try { file = HqrFile.Parse(File.ReadAllBytes(Path.Combine(entry.Directory, group.Key))); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { return false; }
            foreach (var change in group)
            {
                var expected = useAfter ? change.After : change.Before;
                var current = change.Entry >= 0 && change.Entry < file.Count && !file.IsEmpty(change.Entry) ? file.Read(change.Entry) : null;
                if (expected is null ? current is not null : current is null || !current.AsSpan().SequenceEqual(expected)) return false;
            }
        }
        foreach (var text in entry.Texts)
        {
            var path = Path.Combine(entry.Directory, text.RelativePath);
            var expected = useAfter ? text.After : text.Before;
            string? current;
            try { current = File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.Latin1) : null; }
            catch (IOException) { return false; }
            if (expected != current) return false;
        }
        foreach (var whole in entry.WholeFiles)
        {
            var path = Path.Combine(entry.Directory, whole.RelativePath);
            var expected = useAfter ? whole.After : whole.Before;
            byte[]? current;
            try { current = File.Exists(path) ? File.ReadAllBytes(path) : null; }
            catch (IOException) { return false; }
            if (expected is null ? current is not null : current is null || !current.AsSpan().SequenceEqual(expected)) return false;
        }
        return true;
    }

    // Writes the previous state back. Returns the step, or null when there is nothing to undo. On a failed write
    // the step stays on the log; so does a stale one (MatchesCurrent false), reported the same way (an
    // InvalidDataException the caller already shows as "Nothing was changed: ...").
    public static SceneHistoryEntry? Undo()
    {
        if (undo.Count == 0) return null;
        var entry = undo[^1];
        if (!MatchesCurrent(entry, useAfter: true))
            throw new InvalidDataException("the game files have changed since this step, so undoing it now would overwrite that change.");
        if (entry.Before.Count > 0) new SceneStore(entry.Game, entry.Directory).WriteRaw(entry.Before);
        HqrEntryStore.WriteRaw(entry.Directory, entry.Entries, entry.Texts, entry.WholeFiles, after: false);
        undo.RemoveAt(undo.Count - 1);
        redo.Add(entry);
        Persist();
        Changed?.Invoke(null, EventArgs.Empty);
        return entry;
    }

    public static SceneHistoryEntry? Redo()
    {
        if (redo.Count == 0) return null;
        var entry = redo[^1];
        if (!MatchesCurrent(entry, useAfter: false))
            throw new InvalidDataException("the game files have changed since this step, so redoing it now would overwrite that change.");
        if (entry.After.Count > 0) new SceneStore(entry.Game, entry.Directory).WriteRaw(entry.After);
        HqrEntryStore.WriteRaw(entry.Directory, entry.Entries, entry.Texts, entry.WholeFiles, after: true);
        redo.RemoveAt(redo.Count - 1);
        undo.Add(entry);
        Persist();
        Changed?.Invoke(null, EventArgs.Empty);
        return entry;
    }

    // ---- disk persistence -------------------------------------------------------------------------------------
    // A small hand-rolled binary format (length-prefixed strings and byte arrays) rather than JSON: these entries
    // are whole scene records, tens of KB apiece, and base64-in-JSON would cost a third more disk and CPU for data
    // nobody ever reads by hand. Best-effort throughout (a save or a load failing here never stops an edit from
    // saving to the game files, which matters far more): a failed Load leaves the log empty, a failed Persist
    // leaves the previous file on disk rather than a half-written one.
    private const int FormatVersion = 2;

    public static void Load()
    {
        if (PersistPath is not { } path || !File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream);
            if (r.ReadInt32() != FormatVersion) return;
            undo.Clear(); redo.Clear();
            undo.AddRange(ReadEntries(r));
            redo.AddRange(ReadEntries(r));
            // The limits may have been tightened since this file was written (a lower Settings value from a
            // previous session, or a different MaxSteps/MaxBytes in a test tool); bring the loaded log back
            // within them rather than silently exceeding the configured cap until the next Record().
            TrimToLimits();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or EndOfStreamException)
        {
            undo.Clear(); redo.Clear();
        }
    }

    private static void Persist()
    {
        if (PersistPath is not { } path) return;
        try
        {
            var tmp = path + ".tmp";
            using (var stream = File.Create(tmp))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(FormatVersion);
                WriteEntries(w, undo);
                WriteEntries(w, redo);
            }
            File.Copy(tmp, path, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The undo log is a convenience: losing it (this session's steps stay undo-able, they just won't
            // greet the next session) is never a reason to fail the edit that triggered this.
        }
    }

    private static void WriteEntries(BinaryWriter w, List<SceneHistoryEntry> entries)
    {
        w.Write(entries.Count);
        foreach (var e in entries)
        {
            w.Write(e.Description);
            w.Write((int)e.Game);
            w.Write(e.Directory);
            WriteScenes(w, e.Before);
            WriteScenes(w, e.After);
            WriteHqrChanges(w, e.Entries);
            WriteTextChanges(w, e.Texts);
            WriteWholeFiles(w, e.WholeFiles);
        }
    }

    private static List<SceneHistoryEntry> ReadEntries(BinaryReader r)
    {
        var count = r.ReadInt32();
        var result = new List<SceneHistoryEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var description = r.ReadString();
            var game = (SceneGame)r.ReadInt32();
            var directory = r.ReadString();
            var before = ReadScenes(r);
            var after = ReadScenes(r);
            var entries = ReadHqrChanges(r);
            var texts = ReadTextChanges(r);
            var wholeFiles = ReadWholeFiles(r);
            result.Add(new SceneHistoryEntry(description, game, directory, before, after, entries, texts, wholeFiles));
        }
        return result;
    }

    private static void WriteHqrChanges(BinaryWriter w, IReadOnlyList<HqrEntryChange> changes)
    {
        w.Write(changes.Count);
        foreach (var c in changes)
        {
            w.Write(c.RelativePath);
            w.Write(c.Entry);
            WriteOptionalBytes(w, c.Before);
            WriteOptionalBytes(w, c.After);
        }
    }

    private static List<HqrEntryChange> ReadHqrChanges(BinaryReader r)
    {
        var count = r.ReadInt32();
        var result = new List<HqrEntryChange>(count);
        for (var i = 0; i < count; i++) result.Add(new HqrEntryChange(r.ReadString(), r.ReadInt32(), ReadOptionalBytes(r), ReadOptionalBytes(r)));
        return result;
    }

    private static void WriteTextChanges(BinaryWriter w, IReadOnlyList<TextFileChange> texts)
    {
        w.Write(texts.Count);
        foreach (var t in texts)
        {
            w.Write(t.RelativePath);
            WriteOptionalString(w, t.Before);
            WriteOptionalString(w, t.After);
        }
    }

    private static List<TextFileChange> ReadTextChanges(BinaryReader r)
    {
        var count = r.ReadInt32();
        var result = new List<TextFileChange>(count);
        for (var i = 0; i < count; i++) result.Add(new TextFileChange(r.ReadString(), ReadOptionalString(r), ReadOptionalString(r)));
        return result;
    }

    private static void WriteWholeFiles(BinaryWriter w, IReadOnlyList<WholeFileChange> files)
    {
        w.Write(files.Count);
        foreach (var f in files)
        {
            w.Write(f.RelativePath);
            WriteOptionalBytes(w, f.Before);
            WriteBytes(w, f.After);
        }
    }

    private static List<WholeFileChange> ReadWholeFiles(BinaryReader r)
    {
        var count = r.ReadInt32();
        var result = new List<WholeFileChange>(count);
        for (var i = 0; i < count; i++) result.Add(new WholeFileChange(r.ReadString(), ReadOptionalBytes(r), ReadBytes(r)));
        return result;
    }

    private static void WriteOptionalBytes(BinaryWriter w, byte[]? bytes) { w.Write(bytes is not null); if (bytes is not null) WriteBytes(w, bytes); }
    private static byte[]? ReadOptionalBytes(BinaryReader r) => r.ReadBoolean() ? ReadBytes(r) : null;
    private static void WriteOptionalString(BinaryWriter w, string? s) { w.Write(s is not null); if (s is not null) w.Write(s); }
    private static string? ReadOptionalString(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    private static void WriteScenes(BinaryWriter w, IReadOnlyList<RawScene> scenes)
    {
        w.Write(scenes.Count);
        foreach (var s in scenes)
        {
            w.Write(s.Scene);
            WriteBytes(w, s.Record);
            w.Write(s.Grid is not null);
            if (s.Grid is not null) WriteBytes(w, s.Grid);
        }
    }

    private static List<RawScene> ReadScenes(BinaryReader r)
    {
        var count = r.ReadInt32();
        var result = new List<RawScene>(count);
        for (var i = 0; i < count; i++)
        {
            var scene = r.ReadInt32();
            var record = ReadBytes(r);
            var hasGrid = r.ReadBoolean();
            var grid = hasGrid ? ReadBytes(r) : null;
            result.Add(new RawScene(scene, record, grid));
        }
        return result;
    }

    private static void WriteBytes(BinaryWriter w, byte[] bytes) { w.Write(bytes.Length); w.Write(bytes); }
    private static byte[] ReadBytes(BinaryReader r) => r.ReadBytes(r.ReadInt32());

    private static long SizeOf(SceneHistoryEntry e) => SizeOf(e.Before) + SizeOf(e.After) + e.Description.Length + e.Directory.Length
        + e.Entries.Sum(c => (long)c.RelativePath.Length + (c.Before?.Length ?? 0) + (c.After?.Length ?? 0))
        + e.Texts.Sum(t => (long)t.RelativePath.Length + (t.Before?.Length ?? 0) + (t.After?.Length ?? 0))
        + e.WholeFiles.Sum(f => (long)f.RelativePath.Length + (f.Before?.Length ?? 0) + f.After.Length);
    private static long SizeOf(IReadOnlyList<RawScene> scenes) => scenes.Sum(s => (long)s.Record.Length + (s.Grid?.Length ?? 0));
}
