using System.IO;
using System.Text.Json;

namespace LBA2LevelEditor;

// Persists user-configurable paths (currently just the LBA2 game install
// directory, previously hardcoded to one developer's machine) to a JSON file
// under %AppData%, so each install can point at wherever the game actually
// lives.
internal sealed class EditorSettings
{
    private static readonly string DefaultSettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LBA2LevelEditor");

#if DEBUG
    // Debug builds only: LBA2_EDITOR_SETTINGS_DIR, when set, replaces the %AppData% folder --
    // for running a second instance (or an automated test) against a copy of the game files
    // without touching the real settings. Compiled out of Release builds entirely, so a
    // shipped editor cannot be redirected this way.
    private static readonly string SettingsDirectory =
        Environment.GetEnvironmentVariable("LBA2_EDITOR_SETTINGS_DIR") is { Length: > 0 } overrideDirectory
            ? overrideDirectory
            : DefaultSettingsDirectory;
#else
    private static readonly string SettingsDirectory = DefaultSettingsDirectory;
#endif
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public string GameDirectory { get; set; } = @"E:\GOG Games\Little Big Adventure 2 - Level viewer";

    // Script text prints function names in lowercase (set_track(...)) instead of the
    // engine's uppercase (SET_TRACK(...)). The compiler accepts either.
    public bool LowercaseScriptNames { get; set; } = true;

    private static EditorSettings? cached;
    public static EditorSettings Current => cached ??= Load();

    private static EditorSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null) { LbaScript.ScriptStyle.LowercaseNames = loaded.LowercaseScriptNames; return loaded; }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file: fall back to defaults
            // rather than failing to start.
        }
        return new EditorSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        cached = this;
        LbaScript.ScriptStyle.LowercaseNames = LowercaseScriptNames;
    }
}
