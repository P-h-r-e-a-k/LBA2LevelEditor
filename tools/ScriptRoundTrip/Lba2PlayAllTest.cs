using System.Diagnostics;
using LBAAssembler;

namespace ScriptRoundTrip;

// A full play test: every LBA2 scene (cubes 0-222, see native/lba2-classic-community/docs/SCENES.md), started headless the
// same way "LBA2: play scene" does, checked for the engine actually landing in that scene with no crash. Unlike
// Lba2PlayTests.Run (a handful of hand-picked scenes exercised more thoroughly), this sweeps every scene once, for the
// narrower question "does it load".
//   lba2playall [first] [last]
internal static class Lba2PlayAllTest
{
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
    private const int SceneCount = 223;

    public static int Run(string[] args)
    {
        var first = args.Length > 1 ? int.Parse(args[1]) : 0;
        var last = args.Length > 2 ? int.Parse(args[2]) : SceneCount - 1;
        var engine = Lba2Engine.Find();
        if (engine is null) { Console.WriteLine("no lba2cc.exe found"); return 1; }
        if (!Lba2Engine.IsGameFolder(Lba2Dir)) { Console.WriteLine($"not a game folder: {Lba2Dir}"); return 1; }

        var user = Path.Combine(Path.GetTempPath(), "lba2playall_" + Guid.NewGuid().ToString("N")[..8]);
        var failures = new List<string>();
        try
        {
            for (var scene = first; scene <= last; scene++)
            {
                var options = new Lba2PlayOptions { Scene = scene, Sound = false };
                var start = new ProcessStartInfo(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in options.Arguments(Lba2Dir, user)) start.ArgumentList.Add(a);
                foreach (var a in new[] { "--headless", "--exec-at", "60", "status", "--tick", "80", "--exit" }) start.ArgumentList.Add(a);
                using var process = Process.Start(start)!;
                // Reading synchronously (ReadToEnd) before WaitForExit deadlocks forever if the engine hangs without closing its
                // output pipe: WaitForExit's own timeout never gets a chance to run. Collect output via the async events instead
                // (same pattern as Lba2Play.PrepareSceneSave), so a hang is bounded by the WaitForExit timeout below and gets killed.
                var outputBuilder = new System.Text.StringBuilder();
                void OnData(object? _, DataReceivedEventArgs e) { if (e.Data is not null) lock (outputBuilder) outputBuilder.AppendLine(e.Data); }
                process.OutputDataReceived += OnData;
                process.ErrorDataReceived += OnData;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var exited = process.WaitForExit(30000);
                if (!exited) { try { process.Kill(true); } catch (InvalidOperationException) { } process.WaitForExit(5000); }
                string output;
                lock (outputBuilder) output = outputBuilder.ToString();

                var crashed = output.Contains("CRASH", StringComparison.Ordinal);
                var entered = output.Contains($"Cube: {scene}", StringComparison.Ordinal) && output.Contains("Obj:", StringComparison.Ordinal);
                var ok = exited && !crashed && entered;
                if (!ok)
                {
                    var reason = !exited ? "timed out / hung" : crashed ? "CRASHED" : "never reported entering the scene";
                    var detail = output.Split('\n').FirstOrDefault(l => l.Contains("Cube:") || l.Contains("CRASH"))?.Trim();
                    failures.Add($"scene {scene}: {reason}{(detail is null ? "" : "  " + detail)}");
                    Console.WriteLine($"  scene {scene}: FAILED - {reason}{(detail is null ? "" : "  " + detail)}");
                }
                else if (scene % 10 == 0) Console.WriteLine($"  scene {scene}: ok (checkpoint)");
            }
        }
        finally { try { Directory.Delete(user, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

        Console.WriteLine();
        Console.WriteLine(failures.Count == 0
            ? $"lba2 play-all: all {last - first + 1} scenes ({first}-{last}) loaded cleanly"
            : $"lba2 play-all: {failures.Count}/{last - first + 1} scenes FAILED");
        foreach (var f in failures) Console.WriteLine("  " + f);
        return failures.Count == 0 ? 0 : 1;
    }
}
