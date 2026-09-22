using System.IO;

namespace LBAAssembler;

// Writes/updates a per-game-folder .HQD sidecar (LBAPackageManager's own plain-text entry-description format --
// see HqdDescriptions) whenever the editor adds a new HQR entry of its own, so that entry has a name too, not
// just the game's own original ones. A first write seeds the file from the embedded reference descriptions
// (HqdDescriptions.LoadLines) when one exists for this archive, so retail entries keep their known names;
// otherwise it starts from a plain header line. Entries between the header and the one being named, if any, are
// padded with blank lines, never skipped, so every line's number keeps meaning "entry (line - 1)".
internal static class HqdWriter
{
    // The reference/embedded .HQD this HQR corresponds to, if the project ships one (Assets/FileDesc) -- used
    // only to seed a fresh sidecar so retail entries aren't left nameless the first time this game folder gets
    // one of its own.
    private static string? ReferenceFileName(string hqrFileName, Scenes.SceneGame game) => Path.GetFileNameWithoutExtension(hqrFileName).ToUpperInvariant() switch
    {
        "BODY" => game == Scenes.SceneGame.Lba1 ? "BODY1.HQD" : "BODY2.HQD",
        "ANIM" => game == Scenes.SceneGame.Lba1 ? "ANIM1.HQD" : "ANIM2.HQD",
        "FILE3D" => "FILE3D.HQD",
        "SCENE" => game == Scenes.SceneGame.Lba1 ? "SCENE1.HQD" : "SCENE2.HQD",
        _ => null,
    };

    // The sidecar's own name in the game folder: the HQR's name with a .HQD extension (BODY.HQR -> BODY.HQD),
    // regardless of which numbered reference file (if any) seeded it -- the folder is already one game's own.
    public static string SidecarName(string hqrFileName) => Path.ChangeExtension(hqrFileName, ".HQD");

    // The sidecar's new whole content with `description` set as entry `entry`'s line, built on top of whatever
    // the game folder already has (or the embedded reference, or a bare header) -- ready to hand to
    // HqrEntryStore as a TextEdit alongside the archive edit that added the entry, so both land in the same
    // undo step and the same all-or-nothing transaction. `pendingContent`: another entry of the same save has
    // already planned this same sidecar (not yet written to disk) -- this one builds on that instead of what's
    // on disk, so both descriptions end up in the one file (mirrors HqrEntryStore.LoadWithPending, for text).
    public static string Describe(string directory, string hqrFileName, Scenes.SceneGame game, int entry, string description, string? pendingContent = null)
    {
        List<string> lines;
        if (pendingContent is not null) lines = pendingContent.Replace("\r\n", "\n").Split('\n').ToList();
        else
        {
            var sidecarPath = Path.Combine(directory, SidecarName(hqrFileName));
            if (File.Exists(sidecarPath)) lines = File.ReadAllLines(sidecarPath, System.Text.Encoding.Latin1).ToList();
            else if (ReferenceFileName(hqrFileName, game) is { } reference && HqdDescriptions.LoadLines(reference) is { Count: > 0 } seed) lines = seed.ToList();
            else lines = new List<string> { $"Entry descriptions added by the level editor ({hqrFileName})." };
        }

        var lineIndex = entry + 1; // line 0 is the header; line N+1 describes entry N
        while (lines.Count <= lineIndex) lines.Add("");
        lines[lineIndex] = description;
        return string.Join("\r\n", lines) + "\r\n";
    }
}
