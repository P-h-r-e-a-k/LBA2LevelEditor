using System.Diagnostics;
using LBA2LevelEditor;

namespace ScriptRoundTrip;

// LBA2 play mode: the editor starts the full engine (lba2cc.exe) the way "LBA2: play scene" does, here headless, and checks
// that it lands in the scene asked for. Read-only against the game folder.
//   lba2play
internal static class Lba2PlayTests
{
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";

    public static int Run(string[] args)
    {
        var engine = Lba2Engine.Find();
        if (engine is null) { Console.WriteLine("no lba2cc.exe found"); return 1; }
        var user = Path.Combine(Path.GetTempPath(), "lba2play_test_" + Guid.NewGuid().ToString("N")[..8]);
        var failures = 0;
        try
        {
            foreach (var scene in new[] { 0, 5, 20, 40, 100, 150 })   // (some scenes, like 200, run a script that moves on at once)
            {
                var options = new Lba2PlayOptions { Scene = scene, Sound = false, Commands = "behaviour 2\nteleport actor 1" };
                var start = new ProcessStartInfo(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in options.Arguments(Lba2Dir, user)) start.ArgumentList.Add(a);
                foreach (var a in new[] { "--headless", "--exec-at", "600", "status", "--tick", "700", "--exit" }) start.ArgumentList.Add(a);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit(60000);
                var ok = output.Contains($"Cube: {scene}") && output.Contains("Obj:");
                Console.WriteLine($"  scene {scene}: {(ok ? "entered" : "FAILED")}  {output.Split('\n').FirstOrDefault(l => l.Contains("Cube:"))?.Trim()}");
                if (!ok) failures++;
            }
        }
        finally { try { Directory.Delete(user, true); } catch (IOException) { } }
        failures += EditThenPlay(engine, user);
        Console.WriteLine(failures == 0 ? "lba2 play tests: all passed" : $"lba2 play tests: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr reserved);

    private static int ObjectsIn(string engine, string gameDir, string user, int scene)
    {
        var start = new ProcessStartInfo(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new Lba2PlayOptions { Scene = scene, Sound = false }.Arguments(gameDir, user)) start.ArgumentList.Add(a);
        foreach (var a in new[] { "--headless", "--exec-at", "600", "status", "--tick", "700", "--exit" }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        var line = output.Split('\n').FirstOrDefault(l => l.Contains("Obj:"));
        return line is null ? -1 : int.Parse(line.Split("Obj:")[1].Trim().Split(' ')[0]);
    }

    // What the scene editor saves is what the engine plays: an actor added through SceneOps and saved through SceneStore
    // (record, patch table, the scene buffer size in entry 0) shows up in the running game.
    private static int EditThenPlay(string engine, string user)
    {
        var dir = @"E:\dump\_lba2e2e";
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var file in Directory.GetFiles(Lba2Dir))
            {
                var target = Path.Combine(dir, Path.GetFileName(file));
                if (string.Equals(Path.GetFileName(file), "SCENE.HQR", StringComparison.OrdinalIgnoreCase)) File.Copy(file, target, true);
                else if (!File.Exists(target)) CreateHardLinkW(target, file, IntPtr.Zero);
            }
            var before = ObjectsIn(engine, dir, user, 5);
            var store = new LBA2LevelEditor.Scenes.SceneStore(LBA2LevelEditor.Scenes.SceneGame.Lba2, dir);
            var scene = store.Load(5);
            var (x, y, z) = (scene.Hero.X + 512, scene.Hero.Y, scene.Hero.Z);
            LBA2LevelEditor.Scenes.SceneOps.AddActor(scene, LBA2LevelEditor.Scenes.SceneOps.BlankActor(LBA2LevelEditor.Scenes.SceneGame.Lba2, x, y, z));
            store.Save(5, scene);
            var after = ObjectsIn(engine, dir, user, 5);
            var ok = before > 0 && after == before + 1;
            Console.WriteLine($"  edit then play: scene 5 has {before} objects, after adding one and saving the engine runs {after}: {(ok ? "ok" : "FAILED")}");
            return ok ? 0 : 1;
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
