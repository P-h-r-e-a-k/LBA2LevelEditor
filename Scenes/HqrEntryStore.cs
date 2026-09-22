using System.IO;
using System.Text;

namespace LBAAssembler.Scenes;

// Direct entry-level edits to an HQR archive (or a whole small text sidecar), for anything that isn't a scene:
// grid and block libraries, brick and sprite pictures, the object browser's Replace, and the extra files a scene
// save bundles alongside it (Lba1SurpriseChanges/Lba1RoomDoorMod: a new body, a new FILE3D entity record, new
// dialogue text). One call here is one all-or-nothing FileTransaction and one step on SceneHistory, exactly like
// SceneStore's own saves -- so Undo/Redo cover these the same way, and only ever keep the entries that actually
// changed, not a copy of the archive they live in.
internal static class HqrEntryStore
{
    // One entry to write: `Entry` must be an existing slot's index (replaced) or exactly the archive's current
    // slot count (appended) -- anything else throws, since inserting into or shrinking the middle of a shared
    // slot table isn't something any of today's callers need (the one exception, LBA2 bricks, keeps the whole
    // file instead -- see GphLibrary.Save).
    public readonly record struct Edit(string RelativePath, int Entry, byte[] Payload);

    // One small text sidecar file (an .HQD description list, see HqdWriter) to write in full.
    public readonly record struct TextEdit(string RelativePath, string Content);

    // Reads `relativePath` (relative to `directory`) as an HqrFile and applies any `pending` edits already
    // targeting it, so a second plan can see what an earlier one (not yet written to disk) is about to do --
    // e.g. Lba1SecretRoomExtras building its mushroom body on top of Lba1PinkElf's not-yet-saved BODY.HQR entry.
    public static HqrFile LoadWithPending(string directory, string relativePath, IReadOnlyList<Edit>? pending)
    {
        var file = HqrFile.Parse(File.ReadAllBytes(Path.Combine(directory, relativePath)));
        foreach (var edit in pending ?? Array.Empty<Edit>())
            if (string.Equals(edit.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                Apply(file, edit.Entry, edit.Payload);
        return file;
    }

    private static void Apply(HqrFile file, int entry, byte[] payload)
    {
        if (entry == file.Count) file.Add(payload);
        else if (entry >= 0 && entry < file.Count) file.SetStored(entry, payload);
        else throw new InvalidDataException($"Entry {entry} isn't the next free slot ({file.Count}) or an existing one.");
    }

    // Adds each touched file's write to `transaction` (does not commit) and returns what changed, entry by
    // entry, for the undo log. Grouped and parsed once per distinct file even when several edits target it.
    internal static List<HqrEntryChange> Compose(string directory, IReadOnlyList<Edit> edits, FileTransaction transaction)
    {
        var changes = new List<HqrEntryChange>();
        foreach (var group in edits.GroupBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(directory, group.Key);
            var file = HqrFile.Parse(File.ReadAllBytes(path));
            var groupChanges = new List<HqrEntryChange>();
            foreach (var edit in group)
            {
                var before = edit.Entry >= 0 && edit.Entry < file.Count && !file.IsEmpty(edit.Entry) ? file.Read(edit.Entry) : null;
                Apply(file, edit.Entry, edit.Payload);
                groupChanges.Add(new HqrEntryChange(group.Key, edit.Entry, before, edit.Payload));
            }
            var expect = groupChanges.ToDictionary(c => c.Entry, c => c.After!);
            transaction.Write(path, file.ToBytes(), written =>
            {
                var read = HqrFile.Parse(written);
                foreach (var (entryIndex, after) in expect)
                    if (!read.Read(entryIndex).AsSpan().SequenceEqual(after)) return $"{group.Key} entry {entryIndex} read back differently.";
                return null;
            });
            changes.AddRange(groupChanges);
        }
        return changes;
    }

    internal static List<TextFileChange> ComposeText(string directory, IReadOnlyList<TextEdit> edits, FileTransaction transaction)
    {
        var changes = new List<TextFileChange>();
        foreach (var edit in edits)
        {
            var path = Path.Combine(directory, edit.RelativePath);
            // Latin-1, matching HqdDescriptions' own read: these sidecars carry the game's own accented names
            // (Zoé, ...) as single bytes in 0xA0-0xFF, and UTF-8 would mangle them on the next read.
            string? before = File.Exists(path) ? File.ReadAllText(path, Encoding.Latin1) : null;
            transaction.Write(path, Encoding.Latin1.GetBytes(edit.Content));
            changes.Add(new TextFileChange(edit.RelativePath, before, edit.Content));
        }
        return changes;
    }

    // Writes `edits`/`textEdits` alone (no scene change) as one transaction and one named step on SceneHistory --
    // used directly by grid/library editors, the brick and sprite editor, and the object browser's Replace;
    // SceneStore.SaveMany folds the same Compose/ComposeText calls into its own transaction instead, for a save
    // that touches both a scene and one of these files together.
    public static void Save(SceneGame game, string directory, string description, IReadOnlyList<Edit> edits, IReadOnlyList<TextEdit>? textEdits = null)
    {
        var texts = textEdits ?? Array.Empty<TextEdit>();
        if (edits.Count == 0 && texts.Count == 0) return;
        var transaction = new FileTransaction();
        var entries = Compose(directory, edits, transaction);
        var textChanges = ComposeText(directory, texts, transaction);
        transaction.Commit();
        SceneHistory.Record(new SceneHistoryEntry(description, game, directory, Array.Empty<RawScene>(), Array.Empty<RawScene>(), entries, textChanges, Array.Empty<WholeFileChange>()));
    }

    // The rare fallback for a change that reshapes an archive's own slot table (see WholeFileChange) rather than
    // one entry's bytes: `after` is the whole new file, already built by the caller; `verify` (given the bytes
    // read back) works the same as FileTransaction.Write's own.
    public static void SaveWholeFile(SceneGame game, string directory, string description, string relativePath, byte[] after, Func<byte[], string?>? verify = null)
    {
        var path = Path.Combine(directory, relativePath);
        byte[]? before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        new FileTransaction().Write(path, after, verify).Commit();
        SceneHistory.Record(new SceneHistoryEntry(description, game, directory, Array.Empty<RawScene>(), Array.Empty<RawScene>(), Array.Empty<HqrEntryChange>(), Array.Empty<TextFileChange>(),
            new[] { new WholeFileChange(relativePath, before, after) }));
    }

    // Applies Entries/Texts/WholeFiles back (Undo: Before, Redo: After) -- called from SceneHistory only, in its
    // own transaction (separate from any scene/grid write the same step also has, which SceneHistory writes
    // first -- see its own Undo/Redo).
    internal static void WriteRaw(string directory, IReadOnlyList<HqrEntryChange> entries, IReadOnlyList<TextFileChange> texts, IReadOnlyList<WholeFileChange> wholeFiles, bool after)
    {
        if (entries.Count == 0 && texts.Count == 0 && wholeFiles.Count == 0) return;
        var transaction = new FileTransaction();
        foreach (var group in entries.GroupBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(directory, group.Key);
            var file = HqrFile.Parse(File.ReadAllBytes(path));
            foreach (var change in group)
            {
                var payload = after ? change.After : change.Before;
                if (payload is null) { if (change.Entry < file.Count) file.Clear(change.Entry); }
                else Apply(file, change.Entry, payload);
            }
            transaction.Write(path, file.ToBytes());
        }
        foreach (var text in texts)
        {
            var content = after ? text.After : text.Before;
            if (content is not null) transaction.Write(Path.Combine(directory, text.RelativePath), Encoding.Latin1.GetBytes(content));
            // content == null means the sidecar didn't exist at this end of the step (Before, for a newly-created
            // one): deleted just after Commit below, not written here -- FileTransaction has no delete of its
            // own, and MatchesCurrent already confirmed the file holds exactly the other side's content, so this
            // is never a surprise.
        }
        foreach (var whole in wholeFiles)
        {
            var content = after ? whole.After : whole.Before;
            if (content is not null) transaction.Write(Path.Combine(directory, whole.RelativePath), content);
        }
        transaction.Commit();
        foreach (var text in texts)
            if ((after ? text.After : text.Before) is null)
                try { File.Delete(Path.Combine(directory, text.RelativePath)); } catch (IOException) { }
        foreach (var whole in wholeFiles)
            if ((after ? whole.After : whole.Before) is null)
                try { File.Delete(Path.Combine(directory, whole.RelativePath)); } catch (IOException) { }
    }
}
