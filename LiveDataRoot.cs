using System.IO;
using System.Runtime.InteropServices;

namespace LBAAssembler;

// A stand-in for the game folder that the native renderer reads while terrain edits haven't been saved: every file of the
// game folder hard-linked (or copied, where a link can't be made), except the island being edited, which is a real copy that
// the editor rewrites as the edits are made. The renderer's data root is pointed here, so the 3D view shows the unsaved island
// and the real game folder is untouched until Save. The folder lives inside the game folder (hard links need the same volume)
// under a name that starts with an underscore, and is removed again afterwards (a leftover from a crash is removed at the
// next start).
internal sealed class LiveDataRoot : IDisposable
{
    public const string FolderName = "_LIVE_PREVIEW";
    // A second, distinct folder for the body-debug preview (ActorAttributesWindow's "Load Debug Body"), so it never
    // collides with a terrain edit's own live folder above -- the two are independent single-file overrides, and
    // the native renderer only tracks one data-root override at a time, so callers still can't run both at once
    // (MainWindow guards this), but at least their on-disk folders don't fight over the same name mid-edit.
    public const string BodyPreviewFolderName = "_LIVE_BODY_PREVIEW";
    // A third, distinct folder for a whole-session "test these edits before they touch the real game
    // folder" mirror (MainWindow.TestEditsSession) -- same reasoning as BodyPreviewFolderName above, so it
    // can't collide with either of the other two even though nothing currently runs more than one at once.
    public const string TestEditsFolderName = "_TEST_EDITS";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr reserved);

    private readonly string gameDirectory;
    private readonly string? islandFile;

    public string Directory { get; }

    private LiveDataRoot(string gameDirectory, string? islandFile, string folderName)
    {
        this.gameDirectory = gameDirectory;
        this.islandFile = islandFile;
        Directory = Path.Combine(gameDirectory, folderName);
    }

    // Removes a live folder a crashed session left behind.
    public static void CleanStale(string gameDirectory, string folderName = FolderName)
    {
        try { var path = Path.Combine(gameDirectory, folderName); if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DebugLog.Log($"LiveDataRoot: couldn't remove a stale folder: {e.Message}"); }
    }

    // islandFile null: a full mirror with no file pre-copied for real -- every file starts as a hard link,
    // including whichever ones end up edited. Safe because of how a save actually writes a file: every save
    // path in this app (FileTransaction, HqrEntryStore, WriteIsland below) writes to a *new* temp file and
    // renames it over the target, never opens the target itself for writing -- a rename-over-target detaches
    // that one hard link (the two paths stop sharing data, confirmed empirically before relying on it here)
    // without ever touching the bytes the real game-folder file still points at. So there's nothing to
    // pre-copy: whichever files a test-edits session actually touches become real, independent copies the
    // moment they're first saved, and everything else stays a hard link (instant, no wasted disk space) for
    // as long as it goes untouched.
    public static LiveDataRoot? Create(string gameDirectory, string? islandFile, string folderName = FolderName)
    {
        var root = new LiveDataRoot(gameDirectory, islandFile, folderName);
        try
        {
            if (System.IO.Directory.Exists(root.Directory)) System.IO.Directory.Delete(root.Directory, true);
            System.IO.Directory.CreateDirectory(root.Directory);
            root.Sync();
            return root;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DebugLog.Log($"LiveDataRoot: couldn't create {root.Directory}: {e.Message}");
            root.Dispose();
            return null;
        }
    }

    // Brings the links up to date with the game folder (a file replaced there since is linked again).
    public void Sync()
    {
        foreach (var source in System.IO.Directory.EnumerateFiles(gameDirectory))
        {
            var name = Path.GetFileName(source);
            if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(Directory, name);
            var isIsland = string.Equals(name, islandFile, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(target))
            {
                if (isIsland) continue;
                var a = new FileInfo(source); var b = new FileInfo(target);
                if (a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc) continue;
                File.Delete(target);
            }
            if (isIsland) { File.Copy(source, target); continue; }
            if (!CreateHardLinkW(target, source, IntPtr.Zero)) File.Copy(source, target);
        }
    }

    public void WriteIsland(byte[] bytes)
    {
        if (islandFile is null) throw new InvalidOperationException("This LiveDataRoot has no single island file -- it's a full mirror (see ChangedFiles instead).");
        var target = Path.Combine(Directory, islandFile);
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, target, overwrite: true);
    }

    // Which of the mirror's own files a test-edits session actually touched (relative names): whatever a
    // save detached from its hard link now differs from the real game folder's own copy in length and/or
    // write time -- the same pair Sync() above already uses to notice an outside change, just compared the
    // other way around. Committing copies exactly these back over the real files; nothing else in the
    // mirror (everything still a plain hard link to the real file) needs touching at all.
    public IReadOnlyList<string> ChangedFiles()
    {
        var changed = new List<string>();
        foreach (var target in System.IO.Directory.EnumerateFiles(Directory))
        {
            var name = Path.GetFileName(target);
            if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var source = Path.Combine(gameDirectory, name);
            var b = new FileInfo(target);
            if (!File.Exists(source)) { changed.Add(name); continue; }     // a file the session created that the real folder never had
            var a = new FileInfo(source);
            if (a.Length != b.Length || a.LastWriteTimeUtc != b.LastWriteTimeUtc) changed.Add(name);
        }
        return changed;
    }

    public void Dispose()
    {
        try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DebugLog.Log($"LiveDataRoot: couldn't remove {Directory}: {e.Message}"); }
    }
}
