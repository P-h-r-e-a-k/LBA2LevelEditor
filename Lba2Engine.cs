using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace LBAAssembler;

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
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LBAAssembler", "native"),
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

// What to start the game with. A scene is entered by having the engine load a save that was made in that scene (see
// Lba2Play.PrepareSceneSave): the engine's own start is a new game whose opening dialogue blocks the tick the `cube` console
// command would run on, so `cube N` on its own left the game in scene 0. Anything else the tester wants (items, quest
// variables, behaviour ...) goes through the engine's console once the scene is running.
internal sealed class Lba2PlayOptions
{
    public int Scene;
    public int Width = 1280, Height = 960;
    public bool Sound = true;
    public bool KeepFocus;                              // keep running when the window isn't in front
    public string Commands = "";                        // console commands, separated by ';', run once the scene has loaded
    public int ZoneMask;                                // the zone types drawn over the game (bit n = type n)
    public bool Paths;                                  // the actors' routes drawn over the game
    public (int X, int Y, int Z)? Spawn;                // where the hero starts (in the scene's own coordinates); null = where the scene puts him
    public AudioLevels? Audio;                          // the sound balance (default: the settings' LBA2 balance)
    public string? LoadSave;                            // a save (in the user folder's save\) that the game loads instead of starting a new game
    public int? ListenPort;                             // --listen <port>: the script-breakpoints control socket (Lba2ControlClient), bound to 127.0.0.1 only

    public Lba2PlayOptions WithScene(int scene)
    {
        var copy = (Lba2PlayOptions)MemberwiseClone();
        copy.Scene = scene;
        copy.LoadSave = null;
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
        };
        // the save puts the game in the scene; without one (it couldn't be made) the console command is the fallback
        if (LoadSave is not null) { args.Add("--load"); args.Add(LoadSave); }
        else { args.Add("--exec-at"); args.Add("5"); args.Add($"cube {Scene}"); }
        // where the player put the hero: moved there once the scene is running
        if (Spawn is { } spawn) { args.Add("--exec-at"); args.Add("40"); args.Add($"teleport {spawn.X} {spawn.Y} {spawn.Z}"); }
        if (!Sound) args.Add("--no-audio");
        if (KeepFocus) args.Add("--ignore-focus");
        if (ListenPort is { } port) { args.Add("--listen"); args.Add(port.ToString()); }
        var extra = Commands.Replace("\r", "").Replace("\n", ";").Trim(' ', ';');
        if (extra.Length > 0) { args.Add("--exec-at"); args.Add("180"); args.Add(extra); }
        // The engine's command harness disarms itself after the first tick unless it was given a tick budget (its default budget is
        // zero), which silently drops every --exec-at later than tick 0: the hero was never moved and the console commands never ran.
        // A budget this long is only a way to keep it armed; the run has no --exit, so nothing ends at the budget.
        if (args.Contains("--exec-at")) { args.Add("--tick"); args.Add("100000000"); }
        return args;
    }
}

internal static class Lba2Play
{
    private const string SaveName = "EDITORPLAY";

    // The options of the last start, so a "Play scene" button can repeat them for another scene.
    public static Lba2PlayOptions? LastOptions { get; set; }

    // The folder for the engine's own saves, settings and log, beside the editor's settings.
    public static string? UserDirectory(out string? problem)
    {
        problem = null;
        var root = Environment.GetEnvironmentVariable("LBA2_EDITOR_SETTINGS_DIR") is { Length: > 0 } configured ? configured : AppContext.BaseDirectory;
        var user = Path.Combine(root, "lba2-play");
        try { Directory.CreateDirectory(user); return user; }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LBAAssembler", "lba2-play");
            try { Directory.CreateDirectory(user); return user; }
            catch (Exception second) when (second is UnauthorizedAccessException or IOException) { problem = $"Couldn't create {user}: {second.Message}"; return null; }
        }
    }

    // The file the engine reads (through LBA2_OVERLAY_FILE) for the zone boxes and actor paths to draw over the game; the editor rewrites it while the game runs.
    public static string OverlayFile(string user) => Path.Combine(user, "overlay.txt");

    public static void WriteOverlay(string user, int zoneMask, bool paths)
    {
        try { File.WriteAllText(OverlayFile(user), $"zones={zoneMask} paths={(paths ? 1 : 0)}\n"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { DebugLog.Log($"Lba2Play: couldn't write the overlay file: {error.Message}"); }
    }

    // Sets the volumes in the engine's cfg (WaveVolume = effects, VoiceVolume = speech, MusicVolume / CDVolume = music; each 0..127).
    public static void WriteAudioConfig(string user, AudioLevels audio)
    {
        var path = Path.Combine(user, "lba2.cfg");
        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            void Set(string key, int value)
            {
                var line = $"{key}: {value}";
                var at = lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
                if (at >= 0) lines[at] = line; else lines.Add(line);
            }
            Set("WaveVolume", AudioLevels.ToEngine(audio.Effects));
            Set("VoiceVolume", AudioLevels.ToEngine(audio.Voices));
            Set("MusicVolume", AudioLevels.ToEngine(audio.Music));
            Set("CDVolume", AudioLevels.ToEngine(audio.Music));
            Set("MasterVolume", 127);
            File.WriteAllLines(path, lines);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DebugLog.Log($"Lba2Play: couldn't write the audio settings to {path}: {error.Message}");
        }
    }

    // Enters `scene` in a short run of the engine that has no window (a few seconds) and saves the game there, as a save the visible
    // run can load. Returns the save's name, or null when it couldn't be made.
    public static string? PrepareSceneSave(string engine, string gameDirectory, string user, int scene)
    {
        var saves = Path.Combine(user, "save");
        var made = Path.Combine(saves, "bugs", "editorplay.lba");
        var target = Path.Combine(saves, SaveName + ".LBA");
        try
        {
            Directory.CreateDirectory(saves);
            if (File.Exists(made)) File.Delete(made);
            if (File.Exists(target)) File.Delete(target);
            var start = new ProcessStartInfo(engine) { WorkingDirectory = gameDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--headless", "--game-dir", gameDirectory, "--user-dir", user, "--no-autosave", "--resolution", "640x480",
                                        "--exec-at", "5", $"cube {scene}", "--exec-at", "39", "status", "--exec-at", "40", "savebug editorplay", "--tick", "80", "--exit" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return null;
            // Drained via events (not ReadToEnd, which would deadlock the caller if the engine ever hangs without exiting) but
            // kept, not discarded: a `status` line confirms the `cube` command actually landed before trusting the save it made.
            // Some scenes (the LBA2 "demo reel" duplicates, e.g. cube 195 -- see docs/SCENES.md "standalone vignettes") sit on
            // their own return-to-cube-0 zone and bounce straight back out when entered cold, so the resulting save would
            // silently open in the wrong place if this weren't checked.
            var landed = new System.Text.StringBuilder();
            void OnLine(object? _, DataReceivedEventArgs e) { if (e.Data is not null) lock (landed) landed.AppendLine(e.Data); }
            process.OutputDataReceived += OnLine;
            process.ErrorDataReceived += OnLine;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(40000)) { try { process.Kill(true); } catch (InvalidOperationException) { } return null; }
            if (!File.Exists(made)) return null;
            string statusOutput;
            lock (landed) statusOutput = landed.ToString();
            if (!statusOutput.Contains($"Cube: {scene}", StringComparison.Ordinal))
            {
                DebugLog.Log($"Lba2Play: scene {scene} didn't hold when entered directly (probably only reachable from another scene); not using this save");
                return null;
            }
            File.Copy(made, target, overwrite: true);
            return SaveName;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DebugLog.Log($"Lba2Play: couldn't prepare a save for scene {scene}: {error.Message}");
            return null;
        }
    }

    // Starts the game. Returns the process, or null with the reason. Blocks for a few seconds while the scene's save is made
    // (call it from a background thread when the UI shouldn't wait).
    public static Process? Launch(string gameDirectory, Lba2PlayOptions options, out string? problem, bool embedded = false)
    {
        problem = null;
        LastOptions = options;
        var engine = Lba2Engine.Find();
        if (engine is null) { problem = "The LBA2 engine (lba2cc.exe) isn't part of this build."; return null; }
        if (!Lba2Engine.IsGameFolder(gameDirectory)) { problem = "The LBA2 game folder isn't set. Choose it under File > Settings."; return null; }
        var user = UserDirectory(out problem);
        if (user is null) return null;

        // the sound balance: the engine reads its volumes from its cfg at start, and sound is off altogether when muted
        var audio = options.Audio ?? EditorSettings.Current.Lba2Audio;
        options.Sound = !audio.Mute;
        WriteAudioConfig(user, audio);
        WriteOverlay(user, options.ZoneMask, options.Paths);

        options.LoadSave = PrepareSceneSave(engine, gameDirectory, user, options.Scene);
        if (options.LoadSave is null) DebugLog.Log($"Lba2Play: no save for scene {options.Scene}; falling back to the cube command");

        // The engine is a console program: without CreateNoWindow Windows opens a console (a terminal window) beside the game.
        // Embedded, its window starts hidden as well, so the editor can take it over before anything is seen of it.
        var start = new ProcessStartInfo(engine) { WorkingDirectory = gameDirectory, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["LBA2_OVERLAY_FILE"] = OverlayFile(user);      // zone boxes and actor paths drawn by the engine (EDITOR_OVERLAY.CPP); an all-zero file draws nothing
        if (embedded)
        {
            start.WindowStyle = ProcessWindowStyle.Hidden;
            // the engine creates its window where this says (see WINDOW.CPP); off screen it is never seen before the editor has taken it over
            start.Environment["LBA2_WINDOW_POS"] = "-32000,-32000";
        }
        foreach (var arg in options.Arguments(gameDirectory, user)) start.ArgumentList.Add(arg);
        try { return Process.Start(start); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            problem = $"Couldn't start the engine: {error.Message}";
            return null;
        }
    }
}
