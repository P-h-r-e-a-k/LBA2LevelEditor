using System.Diagnostics;
using LBA2LevelEditor;
using LBA2LevelEditor.Grids;
using LBA2LevelEditor.Lba1;

namespace ScriptRoundTrip;

// The engine plays what the grid editor saves: an interior scene of LBA2 is rendered headless before and after blocks are painted next to the hero in a sandbox
// copy of LBA_BKG.HQR, and the frame has to change.
//   gridengine        (env GRID_E2E_KEEP=<folder> keeps the frames)
internal static class GridEngineTest
{
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr reserved);

    public static int Run(string[] args)
    {
        var engine = Lba2Engine.Find();
        if (engine is null) { Console.WriteLine("no lba2cc.exe found"); return 1; }
        var sandbox = @"E:\dump\_lba2grid_e2e";
        var user = Path.Combine(Path.GetTempPath(), "grid_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox); Directory.CreateDirectory(user);
        try
        {
            foreach (var file in Directory.GetFiles(Lba2Dir))
            {
                var target = Path.Combine(sandbox, Path.GetFileName(file));
                if (File.Exists(target)) File.Delete(target);
                if (Path.GetFileName(file).Equals("LBA_BKG.HQR", StringComparison.OrdinalIgnoreCase)) File.Copy(file, target, true);
                else CreateHardLinkW(target, file, IntPtr.Zero);
            }
            // scene -> grid: the table after the bricks in LBA_BKG.HQR is two bytes per scene (type, grid)
            var bkg = HqrFile.Parse(File.ReadAllBytes(Path.Combine(sandbox, "LBA_BKG.HQR")));
            var header = bkg.Read(0);
            int brkStart = BitConverter.ToUInt16(header, 6), maxBrk = BitConverter.ToUInt16(header, 8);
            var table = bkg.Read(brkStart + maxBrk);
            var backend = new Lba2GridBackend(sandbox);
            var scenes = HqrArchive.Open(Path.Combine(Lba2Dir, "SCENE.HQR"));
            var failures = 0;
            var tried = 0;
            // an interior scene (cube mode 0) whose grid we can paint on
            for (var scene = 0; scene < table.Length / 2 && tried < 1; scene++)
            {
                var entry = scene + 1;
                if (!scenes.IsValid(entry)) continue;
                var record = scenes.Read(entry);
                if (record.Length < 6 || record[5] != 0) continue;         // interior only
                var gridId = table[scene * 2 + 1];
                if (backend.Grids.All(g => g.Id != gridId)) continue;
                var model = new LBA2LevelEditor.Scenes.SceneStore(LBA2LevelEditor.Scenes.SceneGame.Lba2, Lba2Dir).Load(scene);
                int hx = model.Hero.X / 512, hy = model.Hero.Y / 256, hz = model.Hero.Z / 512;
                if (hx < 4 || hz < 4 || hx > 58 || hz > 58 || hy > 20) continue;
                tried++;
                Console.WriteLine($"scene {scene}: grid {gridId}, hero at cell ({hx}, {hy}, {hz})");
                var baseFrame = Shoot(engine, sandbox, user, scene, Path.Combine(user, "base.png"));
                Check(ref failures, "the engine renders the interior", baseFrame is not null);
                if (baseFrame is null) return 1;
                var noise = Diff(Shoot(engine, sandbox, user, scene, Path.Combine(user, "base2.png")) ?? baseFrame, baseFrame);
                Console.WriteLine($"  the same run twice differs by {noise:F3} (animation / ambient noise)");

                var grid = backend.LoadGrid(gridId);
                var library = backend.LoadLibrary(gridId);
                // a block of at least 2 x 1 x 2 to put next to the hero
                var wide = Enumerable.Range(1, GridPaint.BlockCount(library)).Select(b => GridPaint.Info(library, b)!.Value).First(i => i.Dx >= 2 && i.Dz >= 2 && i.Dy >= 2);
                var edited = grid;
                foreach (var (ox, oz) in new[] { (2, 0), (2, 2), (0, 2), (-3, 0) })
                    if (GridPaint.PlaceCells(library, wide.Number, hx + ox, hy, hz + oz) is { } cells) edited = Lba1GridEdit.SetCells(edited, cells);
                backend.SaveGrid(gridId, edited);
                var frame = Shoot(engine, sandbox, user, scene, Path.Combine(user, "edited.png"));
                var diff = frame is null ? 0 : Diff(frame, baseFrame);
                Console.WriteLine($"  painted block {wide.Number} ({wide.Dx}x{wide.Dy}x{wide.Dz}) next to the hero; mean difference from the base frame {diff:F2}");
                Check(ref failures, "painted blocks change the frame", diff > Math.Max(0.5, noise * 4));

                // erase them again: the frame comes back
                var restored = edited;
                foreach (var (ox, oz) in new[] { (2, 0), (2, 2), (0, 2), (-3, 0) }) restored = GridPaint.Erase(restored, library, hx + ox, hy, hz + oz);
                backend = new Lba2GridBackend(sandbox);
                backend.SaveGrid(gridId, restored);
                var back = Shoot(engine, sandbox, user, scene, Path.Combine(user, "restored.png"));
                Check(ref failures, "erasing them brings the original frame back", back is not null && Diff(back, baseFrame) < diff / 4, back is null ? "" : $"difference {Diff(back, baseFrame):F2}");
                var keep = Environment.GetEnvironmentVariable("GRID_E2E_KEEP");
                if (keep is not null) foreach (var f in Directory.GetFiles(user, "*.png")) File.Copy(f, Path.Combine(keep, Path.GetFileName(f)), true);
            }
            if (tried == 0) { Console.WriteLine("no suitable interior scene found"); return 1; }
            Console.WriteLine(failures == 0 ? "grid engine test: passed" : $"grid engine test: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(user, true); } catch (IOException) { }
            try { Directory.Delete(sandbox, true); } catch (IOException) { }
        }
    }

    // blankengine: a blank interior made by Lba2BlankScene runs in the engine with the hero alone on a floor.
    public static int Blank()
    {
        var engine = Lba2Engine.Find();
        if (engine is null) { Console.WriteLine("no lba2cc.exe found"); return 1; }
        var sandbox = @"E:dump_lba2blank_e2e";
        var user = Path.Combine(Path.GetTempPath(), "blank_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox); Directory.CreateDirectory(user);
        try
        {
            foreach (var file in Directory.GetFiles(Lba2Dir))
            {
                var target = Path.Combine(sandbox, Path.GetFileName(file));
                if (File.Exists(target)) File.Delete(target);
                if (Path.GetFileName(file).Equals("LBA_BKG.HQR", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).Equals("SCENE.HQR", StringComparison.OrdinalIgnoreCase)) File.Copy(file, target, true);
                else CreateHardLinkW(target, file, IntPtr.Zero);
            }
            var store = new LBA2LevelEditor.Scenes.SceneStore(LBA2LevelEditor.Scenes.SceneGame.Lba2, sandbox);
            var backend = new Lba2GridBackend(sandbox);
            var failures = 0;
            var scene = Enumerable.Range(0, 200).First(s => { try { return store.Load(s).CubeMode == 0 && backend.GridOfScene(s) is not null && s >= 5; } catch (Exception) { return false; } });
            var gridId = backend.GridOfScene(scene)!.Value;
            var (model, grid) = Lba2BlankScene.Create(store, backend, scene);
            Check(ref failures, "a blank interior has just the hero, no zones", model.Actors.Count == 1 && model.Zones.Count == 0 && model.TrackPoints.Count == 0);
            var floorCells = Lba1GridCodec.Decode(grid).Where((_, i) => i % 2 == 0).Count(b => b != 0);
            Check(ref failures, "and a 32 x 32 floor", floorCells == 32 * 32, $"{floorCells} cells");
            store.Save(scene, model);
            backend.SaveGrid(gridId, grid);
            var start = new ProcessStartInfo(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new Lba2PlayOptions { Scene = scene, Sound = false, Width = 640, Height = 480 }.Arguments(sandbox, user)) start.ArgumentList.Add(a);
            foreach (var a in new[] { "--headless", "--exec-at", "600", "status", "--tick", "700", "--screenshot", Path.Combine(user, "blank.png"), "--exit" }) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var text = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { try { process.Kill(true); } catch (InvalidOperationException) { } }
            var output = text.Result + errors.Result;
            var line = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault(l => l.Contains("Obj:"));
            Console.WriteLine($"  engine: {line?.Trim()}");
            Check(ref failures, "the engine enters the blank scene", output.Contains($"Cube: {scene}") && line is not null);
            Check(ref failures, "with one object (the hero)", line is not null && line.Split("Obj:")[1].Trim().Split(' ')[0] == "1");
            var keep = Environment.GetEnvironmentVariable("GRID_E2E_KEEP");
            if (keep is not null && File.Exists(Path.Combine(user, "blank.png"))) File.Copy(Path.Combine(user, "blank.png"), Path.Combine(keep, "blank_interior.png"), true);
            Console.WriteLine(failures == 0 ? "blank interior test: passed" : $"blank interior test: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(user, true); } catch (IOException) { }
            try { Directory.Delete(sandbox, true); } catch (IOException) { }
        }
    }

    private static void Check(ref int failures, string what, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "ok    " : "FAILED")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        if (!ok) failures++;
    }

    internal static byte[]? Shoot(string engine, string gameDir, string user, int scene, string png)
    {
        var start = new ProcessStartInfo(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new Lba2PlayOptions { Scene = scene, Sound = false, Width = 640, Height = 480 }.Arguments(gameDir, user)) start.ArgumentList.Add(a);
        foreach (var a in new[] { "--headless", "--tick", "700", "--screenshot", png, "--exit" }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { try { process.Kill(true); } catch (InvalidOperationException) { } Console.WriteLine("    (the engine did not exit in 60 s and was stopped)"); }
        return File.Exists(png) ? Decode(File.ReadAllBytes(png)) : null;
    }

    // Raw RGB of a PNG (8-bit RGB / RGBA), enough to compare two frames.
    private static byte[]? Decode(byte[] png)
    {
        int pos = 8, w = 0, h = 0, colour = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            var len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IHDR") { w = (png[pos + 8] << 24) | (png[pos + 9] << 16) | (png[pos + 10] << 8) | png[pos + 11]; h = (png[pos + 12] << 24) | (png[pos + 13] << 16) | (png[pos + 14] << 8) | png[pos + 15]; colour = png[pos + 17]; }
            else if (type == "IDAT") idat.Write(png, pos + 8, len);
            pos += 12 + len;
        }
        if (colour is not (2 or 6)) return null;
        var bpp = colour == 6 ? 4 : 3;
        idat.Position = 0;
        using var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        var raw = new MemoryStream(); z.CopyTo(raw);
        var data = raw.ToArray(); var stride = w * bpp; var rows = new byte[h * stride];
        for (var y = 0; y < h; y++)
        {
            var filter = data[y * (stride + 1)];
            for (var x = 0; x < stride; x++)
            {
                int cur = data[y * (stride + 1) + 1 + x];
                int a = x >= bpp ? rows[y * stride + x - bpp] : 0, b = y > 0 ? rows[(y - 1) * stride + x] : 0, c = x >= bpp && y > 0 ? rows[(y - 1) * stride + x - bpp] : 0;
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                var v = filter switch { 0 => cur, 1 => cur + a, 2 => cur + b, 3 => cur + ((a + b) >> 1), _ => cur + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c) };
                rows[y * stride + x] = (byte)v;
            }
        }
        return rows;
    }

    private static double Diff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return 999;
        double s = 0; for (var i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / a.Length;
    }
}
