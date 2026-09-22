using System.IO;
using System.Text.Json;

namespace LBA2LevelEditor;

// What an LBA2 game folder must contain for the editor to use it.
internal static class Lba2Folder
{
    public static bool IsValid(string? path)
        => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
           && File.Exists(Path.Combine(path, "RESS.HQR")) && File.Exists(Path.Combine(path, "SCENE.HQR"))
           && Directory.EnumerateFiles(path, "*.ILE").Any();
}

// Persists the user's settings (game folders and a few options) as settings.json next to the
// executable, so the whole app is portable: copy the folder and the settings come along. If that
// folder can't be written to (say, the exe sits under Program Files) it falls back to
// %AppData%\LBA2LevelEditor. A settings file left in %AppData% by an earlier version is read once
// and copied to the new location straight away.
internal sealed class EditorSettings
{
    private static readonly string LegacySettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LBA2LevelEditor");

#if DEBUG
    // Debug builds only: LBA2_EDITOR_SETTINGS_DIR, when set, replaces the executable's folder --
    // for running a second instance (or an automated test) against a copy of the game files
    // without touching the real settings. Compiled out of Release builds entirely, so a
    // shipped editor cannot be redirected this way.
    private static readonly string PortableSettingsDirectory =
        Environment.GetEnvironmentVariable("LBA2_EDITOR_SETTINGS_DIR") is { Length: > 0 } overrideDirectory
            ? overrideDirectory
            : AppContext.BaseDirectory;
#else
    // AppContext.BaseDirectory is the exe's own folder, including for a single-file publish.
    private static readonly string PortableSettingsDirectory = AppContext.BaseDirectory;
#endif
    private const string SettingsFileName = "settings.json";

    // Both folders start empty; a user may own only one of the games, so either can stay unset.
    public string GameDirectory { get; set; } = "";

    public string Lba1Directory { get; set; } = "";

    // LBA1: show scenes that join into one map as that map (see Lba1Areas). On until the user turns it off; kept between runs. (The setting this replaces,
    // Lba1JoinAreas, started off, so its saved value says nothing about what was chosen.)
    public bool Lba1JoinConnectedAreas { get; set; } = true;

    // Draw the yellow ring around the selected actor and the thick white outline on the selected zone.
    public bool HighlightSelection { get; set; } = true;

    // The sound balance when a scene is played, per game (mute and music / speech / effects levels).
    public AudioLevels Lba1Audio { get; set; } = new();
    public AudioLevels Lba2Audio { get; set; } = new();

    // Where each kind of window last was (see WindowPlacement), keyed by a name for that kind ("MainWindow",
    // "ActorAttributesWindow", ...) rather than per window instance: several windows of the same kind opened
    // at once (one actor attributes window per actor, say) all restore to this one remembered spot and then
    // cascade off each other and off it, so there is one remembered place per kind, not an ever-growing list.
    public Dictionary<string, WindowBounds> WindowPositions { get; set; } = new();

    // Script text prints function names in lowercase (set_track(...)) instead of the
    // engine's uppercase (SET_TRACK(...)). The compiler accepts either.
    public bool LowercaseScriptNames { get; set; } = true;

    // Undo history caps (see Scenes.SceneHistory): whichever of these two is reached first drops the oldest
    // step. Applied to the live SceneHistory below whenever settings are loaded or saved, same as
    // LowercaseScriptNames is applied to LbaScript.ScriptStyle, so Settings > Save takes effect immediately.
    public int UndoMaxSteps { get; set; } = 100;
    public int UndoMaxMegabytes { get; set; } = 50;

    private static EditorSettings? cached;
    public static EditorSettings Current => cached ??= Load();

    private static EditorSettings Load()
    {
        foreach (var directory in new[] { PortableSettingsDirectory, LegacySettingsDirectory })
        {
            try
            {
                var path = Path.Combine(directory, SettingsFileName);
                if (!File.Exists(path)) continue;
                var loaded = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(path));
                if (loaded is null) continue;
                LbaScript.ScriptStyle.LowercaseNames = loaded.LowercaseScriptNames;
                ApplyUndoLimits(loaded);
                // Settings found only in the old %AppData% location are copied next to the exe now.
                if (directory == LegacySettingsDirectory)
                {
                    try { Write(PortableSettingsDirectory, JsonSerializer.Serialize(loaded, new JsonSerializerOptions { WriteIndented = true })); }
                    catch { /* read-only folder: keep using the old copy */ }
                }
                return loaded;
            }
            catch
            {
                // Corrupt or unreadable settings file: try the next location, then the defaults,
                // rather than failing to start.
            }
        }
        return new EditorSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            Write(PortableSettingsDirectory, json);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            Write(LegacySettingsDirectory, json);
        }
        cached = this;
        LbaScript.ScriptStyle.LowercaseNames = LowercaseScriptNames;
        ApplyUndoLimits(this);
    }

    private static void ApplyUndoLimits(EditorSettings settings)
    {
        Scenes.SceneHistory.MaxSteps = Math.Max(1, settings.UndoMaxSteps);
        Scenes.SceneHistory.MaxBytes = Math.Max(1, settings.UndoMaxMegabytes) * 1024L * 1024L;
    }

    private static void Write(string directory, string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SettingsFileName), json);
    }
}

// A window's last-known position and size (WPF device-independent units, i.e. Window.Left/Top/Width/Height
// as WPF itself reports them). Plain get/set properties, like the rest of this file's settings, so
// System.Text.Json's default reflection-based (de)serializer needs nothing extra to round-trip it.
public sealed class WindowBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
