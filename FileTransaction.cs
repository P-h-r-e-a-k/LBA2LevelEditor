using System.IO;

namespace LBA2LevelEditor;

// Writes several files as one unit: each gets a one-time `.bak` copy of what it held before, its new content is
// written beside it and checked, and only then are the files swapped in. If a swap fails part-way the files
// already swapped are put back, so a save never leaves the game folder half-edited.
internal sealed class FileTransaction
{
    private sealed record Change(string Path, byte[] Content, Func<byte[], string?>? Verify);

    private readonly List<Change> changes = new();

    // `verify` receives the file's bytes as read back from the temporary file and returns an error message or null.
    // Two writes to the same path in one transaction is always a caller bug (their two temp files, both named
    // `path + ".tmp"`, would collide during Commit -- one Move would silently consume the other's file, and the
    // second Move would then fail with a confusing "file not found" deep inside Commit): callers that touch the
    // same file more than once (HqrEntryStore.Compose, several entries of one archive) merge into one Write
    // instead, so this throws early and clearly rather than committing half a transaction.
    public FileTransaction Write(string path, byte[] content, Func<byte[], string?>? verify = null)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (changes.Any(c => string.Equals(c.Path, full, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{path} is already queued in this transaction; merge the two writes into one instead.");
        changes.Add(new Change(full, content, verify));
        return this;
    }

    public void Commit()
    {
        var before = new Dictionary<string, byte[]?>();
        var temps = new List<(string Temp, string Path)>();
        try
        {
            foreach (var change in changes)
            {
                before[change.Path] = File.Exists(change.Path) ? File.ReadAllBytes(change.Path) : null;
                var temp = change.Path + ".tmp";
                File.WriteAllBytes(temp, change.Content);
                temps.Add((temp, change.Path));
                var readBack = File.ReadAllBytes(temp);
                if (!readBack.AsSpan().SequenceEqual(change.Content)) throw new InvalidDataException($"Verification failed after writing {System.IO.Path.GetFileName(change.Path)}; nothing was modified.");
                var problem = change.Verify?.Invoke(readBack);
                if (problem is not null) throw new InvalidDataException($"{System.IO.Path.GetFileName(change.Path)}: {problem} Nothing was modified.");
            }

            foreach (var change in changes)
            {
                var backup = change.Path + ".bak";
                if (before[change.Path] is not null && !File.Exists(backup)) File.WriteAllBytes(backup, before[change.Path]!);
            }

            var swapped = new List<string>();
            try
            {
                foreach (var (temp, path) in temps)
                {
                    File.Move(temp, path, overwrite: true);
                    swapped.Add(path);
                }
            }
            catch
            {
                foreach (var path in swapped)
                {
                    var original = before[path];
                    try { if (original is not null) File.WriteAllBytes(path, original); else File.Delete(path); } catch (IOException) { }
                }
                throw;
            }
        }
        finally
        {
            foreach (var (temp, _) in temps)
                if (File.Exists(temp)) { try { File.Delete(temp); } catch (IOException) { } }
        }
    }
}
