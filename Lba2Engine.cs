using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace LBA2LevelEditor;

// The LBA2 community engine (native/lba2-classic-community) built as the full playable game, lba2cc.exe. It is a complete
// port of the game (all of its assembly is C++), so "playing a scene" in the editor means running it against the game
// folder the editor edits: what was saved is what plays. The exe is statically linked (no DLLs), and is embedded in the
// editor's exe like the renderer library, extracted to a "native" folder beside it on first use.
internal static class Lba2Engine
{
    private const string ResourceName = "lba2cc.exe";
    private const string RelativeBuild = @"native\lba2-classic-community\out\build\windows_ucrt64_static\SOURCES\lba2cc.exe";

    public static string? Find()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "lba2cc.exe");
        if (File.Exists(beside)) return beside;

        var assembly = Assembly.GetExecutingAssembly();
        using (var resource = assembly.GetManifestResourceStream(ResourceName))
        {
            if (resource is not null)
            {
                var info = new FileInfo(Environment.ProcessPath ?? "");
                var key = info.Exists ? $"{info.Length}-{info.LastWriteTimeUtc.Ticks}" : resource.Length.ToString();
                foreach (var dir in new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "native"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LBA2LevelEditor", "native"),
                })
                {
                    var path = Path.Combine(dir, $"lba2cc.{key}.exe");
                    try
                    {
                        if (!File.Exists(path))
                        {
                            Directory.CreateDirectory(dir);
                            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            using (var file = File.Create(temp)) resource.CopyTo(file);
                            File.Move(temp, path, overwrite: true);
                            foreach (var old in Directory.EnumerateFiles(dir, "lba2cc.*.exe"))
                                if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(old); } catch (IOException) { } }
                        }
                        return path;
                    }
                    catch (Exception error) when (error is UnauthorizedAccessException or IOException) { resource.Position = 0; }
                }
            }
        }

        // a development checkout: walk up from the exe to the repository's build output
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, RelativeBuild);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static bool IsGameFolder(string directory)
        => Directory.Exists(directory) && new[] { "SCENE.HQR", "BODY.HQR", "ANIM.HQR" }.All(f => File.Exists(Path.Combine(directory, f)));
}

// What to start the game with. The engine opens at its menu; the scene is entered with the console command it offers
// (`cube N`), and anything else the tester wants (items, quest variables, behaviour ...) goes through the same console.
internal sealed class Lba2PlayOptions
{
    public int Scene;
    public int Width = 1280, Height = 960;
    public bool Sound = true;
    public bool KeepFocus;                              // keep running when the window isn't in front
    public string Commands = "";                        // console commands, separated by ';', run once the scene has loaded

    public Lba2PlayOptions WithScene(int scene)
    {
        var copy = (Lba2PlayOptions)MemberwiseClone();
        copy.Scene = scene;
        return copy;
    }

    public List<string> Arguments(string gameDirectory, string userDirectory)
    {
        var args = new List<string>
        {
            "--game-dir", gameDirectory,
            "--user-dir", userDirectory,
            "--no-autosave",
            "--resolution", $"{Width}x{Height}",
            "--exec-at", "5", $"cube {Scene}",
        };
        if (!Sound) args.Add("--no-audio");
        if (KeepFocus) args.Add("--ignore-focus");
        var extra = Commands.Replace("\r", "").Replace("\n", ";").Trim(' ', ';');
        if (extra.Length > 0) { args.Add("--exec-at"); args.Add("180"); args.Add(extra); }
        return args;
    }
}

internal static class Lba2Play
{
    // The options of the last start, so a "Play scene" button can repeat them for another scene.
    public static Lba2PlayOptions? LastOptions { get; set; }

    // Starts the game. Returns the process, or null with the reason.
    public static Process? Launch(string gameDirectory, Lba2PlayOptions options, out string? problem)
    {
        problem = null;
        LastOptions = options;
        var engine = Lba2Engine.Find();
        if (engine is null) { problem = "The LBA2 engine (lba2cc.exe) isn't part of this build."; return null; }
        if (!Lba2Engine.IsGameFolder(gameDirectory)) { problem = "The LBA2 game folder isn't set. Choose it under File > Settings."; return null; }

        // the engine's own saves, settings and log go in a folder of their own, beside the editor's settings
        var root = Environment.GetEnvironmentVariable("LBA2_EDITOR_SETTINGS_DIR") is { Length: > 0 } configured ? configured : AppContext.BaseDirectory;
        var user = Path.Combine(root, "lba2-play");
        try { Directory.CreateDirectory(user); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LBA2LevelEditor", "lba2-play");
            try { Directory.CreateDirectory(user); }
            catch (Exception second) when (second is UnauthorizedAccessException or IOException) { problem = $"Couldn't create {user}: {second.Message}"; return null; }
        }

        var start = new ProcessStartInfo(engine) { WorkingDirectory = gameDirectory, UseShellExecute = false };
        foreach (var arg in options.Arguments(gameDirectory, user)) start.ArgumentList.Add(arg);
        try { return Process.Start(start); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            problem = $"Couldn't start the engine: {error.Message}";
            return null;
        }
    }
}
