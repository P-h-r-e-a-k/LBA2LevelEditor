using System.IO;
using System.Text.Json;

namespace LBA2LevelEditor;

// Persists user-configurable paths (currently just the LBA2 game install
// directory, previously hardcoded to one developer's machine) to a JSON file
// under %AppData%, so each install can point at wherever the game actually
// lives.
internal sealed class EditorSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LBA2LevelEditor");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public string GameDirectory { get; set; } = @"E:\GOG Games\Little Big Adventure 2 - Level viewer";

    private static EditorSettings? cached;
    public static EditorSettings Current => cached ??= Load();

    private static EditorSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(SettingsPath));
                if (loaded is not null) return loaded;
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
    }
}
