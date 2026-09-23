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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr reserved);

    private readonly string gameDirectory;
    private readonly string islandFile;

    public string Directory { get; }

    private LiveDataRoot(string gameDirectory, string islandFile, string folderName)
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

    public static LiveDataRoot? Create(string gameDirectory, string islandFile, string folderName = FolderName)
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
        var target = Path.Combine(Directory, islandFile);
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, target, overwrite: true);
    }

    public void Dispose()
    {
        try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DebugLog.Log($"LiveDataRoot: couldn't remove {Directory}: {e.Message}"); }
    }
}
