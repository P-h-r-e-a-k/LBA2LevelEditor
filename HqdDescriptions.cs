using System.IO;

namespace LBA2LevelEditor;

// Loads the human-readable body/animation name lists LBAPackageManager
// ships as plain-text .HQD files (E:\LBA\LBAPackageManager\FileDesc\*.HQD):
// one description per line, first line is a header describing the file
// ("This file contains the LBA2 characters 3D models"), and line N+2
// describes HQR entry N (line 2 -> entry 0). Separate files exist per game
// (BODY1/ANIM1.HQD for LBA1, BODY2/ANIM2.HQD for LBA2) -- this editor only
// ever works with LBA2 data, so it always loads the "2" variant, but still
// cross-checks the description count against the real HQR's entry count (as
// requested) since silently trusting a hand-authored text file next to a
// binary archive it could drift out of sync with is exactly the kind of
// thing worth a sanity check rather than an assumption.
internal static class HqdDescriptions
{
    public sealed record LoadResult(IReadOnlyList<string?> Names, string? ValidationWarning);

    private const string FileDescDirectory = @"E:\LBA\LBAPackageManager\FileDesc";

    public static LoadResult Load(string hqdFileName, int hqrEntryCount)
    {
        // A LBAPackageManager install, when there is one, wins; otherwise the copy shipped inside the exe.
        var path = Path.Combine(FileDescDirectory, hqdFileName);
        var embedded = typeof(HqdDescriptions).Assembly.GetManifestResourceStream("FileDesc." + hqdFileName);
        if (!File.Exists(path) && embedded is null) return new LoadResult(Array.Empty<string?>(), $"{hqdFileName} not found at {FileDescDirectory}.");

        string[] lines;
        // Windows-1252/Latin-1, not UTF-8: these files predate UTF-8 tooling
        // and encode accented letters (Zoé, é, à, ç, ...) as single bytes in
        // 0xA0-0xFF (confirmed against the raw bytes -- "Zo\xE9" for "Zoé").
        // Encoding.Latin1 (built in since .NET 5, no extra package needed)
        // decodes that range identically to Windows-1252; the two only
        // differ in 0x80-0x9F, which neither file uses. Reading as UTF-8
        // (File.ReadAllLines' default) mangled every accented name into a
        // replacement character instead.
        try
        {
            if (File.Exists(path)) lines = File.ReadAllLines(path, System.Text.Encoding.Latin1);
            else
            {
                using var reader = new StreamReader(embedded!, System.Text.Encoding.Latin1);
                lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
                if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
            }
        }
        catch (Exception ex) { return new LoadResult(Array.Empty<string?>(), $"Couldn't read {hqdFileName}: {ex.Message}"); }
        finally { embedded?.Dispose(); }

        // Line 0 is the file's own header line ("This file contains..."),
        // not a description -- skip it. Line 1 describes entry 0.
        var described = lines.Length > 0 ? lines[1..] : Array.Empty<string>();

        var names = new string?[Math.Max(described.Length, hqrEntryCount)];
        for (var i = 0; i < names.Length; i++)
            names[i] = i < described.Length && described[i].Length > 0 ? described[i] : null;

        // Described count and the HQR's own entry count should match once
        // the header line is accounted for -- they're independent files
        // that can drift (a body added to the archive after the
        // description file was last updated, or -- the actual failure mode
        // this guards against -- accidentally loading the LBA1 variant
        // against LBA2 data, where the counts are wildly different rather
        // than off by a handful).
        string? warning = null;
        var diff = Math.Abs(described.Length - hqrEntryCount);
        if (hqrEntryCount > 0 && diff > Math.Max(5, hqrEntryCount / 10))
        {
            warning = $"{hqdFileName} describes {described.Length} entries but the HQR has {hqrEntryCount} -- " +
                      "names past whichever count is smaller may not line up with the right index.";
        }

        return new LoadResult(names, warning);
    }
}
