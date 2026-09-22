using LBAAssembler;
using LBAAssembler.Assets;
using LBAAssembler.Grids;
using LBAAssembler.Lba1;

namespace ScriptRoundTrip;

// A brick drawn by hand, a block made from it, painted into an interior of LBA2 (sandbox copy of LBA_BKG.HQR): the engine has to draw it. Towers of blocks are
// built well to the side of Twinsen, and the frames are compared away from him (he moves between runs).
//   newbrick        (env GRID_E2E_KEEP=<folder> keeps the frames)
internal static class NewBrickTest
{
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
    private static int failures;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr reserved);

    private static void Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "ok    " : "FAILED")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        if (!ok) failures++;
    }

    // pixels that differ clearly, ignoring a box around Twinsen
    private static int Changed(byte[] a, byte[] b)
    {
        var stride = a.Length / (640 * 480); var n = 0;
        for (var y = 0; y < 480; y++)
            for (var x = 0; x < 640; x++)
            {
                if (x > 250 && x < 390 && y > 190 && y < 340) continue;
                var i = (y * 640 + x) * stride;
                if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 60) n++;
            }
        return n;
    }

    private static byte[] Towers(byte[] grid, byte[] library, int block, int hx, int hy, int hz)
    {
        foreach (var ox in new[] { 4, 5, 6 })
            foreach (var oz in new[] { -1, 0, 1 })
                for (var layer = hy - 1; layer <= hy + 2; layer++)
                    if (GridPaint.PlaceCells(library, block, hx + ox, layer, hz + oz) is { } cells) grid = Lba1GridEdit.SetCells(grid, cells);
        return grid;
    }

    public static int Run()
    {
        failures = 0;
        var engine = Lba2Engine.Find();
        if (engine is null) { Console.WriteLine("no lba2cc.exe found"); return 1; }
        var sandbox = @"E:\dump\_lba2brick_e2e";
        var user = Path.Combine(Path.GetTempPath(), "brick_e2e_" + Guid.NewGuid().ToString("N")[..8]);
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
            var backend = new Lba2GridBackend(sandbox);
            const int scene = 0;
            var gridId = backend.GridOfScene(scene)!.Value;
            var model = new LBAAssembler.Scenes.SceneStore(LBAAssembler.Scenes.SceneGame.Lba2, Lba2Dir).Load(scene);
            int hx = model.Hero.X / 512, hy = model.Hero.Y / 256, hz = model.Hero.Z / 512;
            var keep = Environment.GetEnvironmentVariable("GRID_E2E_KEEP");
            void Keep(string name) { if (keep is not null && File.Exists(Path.Combine(user, name))) File.Copy(Path.Combine(user, name), Path.Combine(keep, "newbrick_" + name), true); }

            var baseFrame = GridEngineTest.Shoot(engine, sandbox, user, scene, Path.Combine(user, "base.png"));
            var again = GridEngineTest.Shoot(engine, sandbox, user, scene, Path.Combine(user, "base2.png"));
            Check("the engine renders the interior", baseFrame is not null && again is not null);
            if (baseFrame is null || again is null) return 1;
            var noise = Changed(baseFrame, again);
            Console.WriteLine($"  noise between two runs, away from Twinsen: {noise} pixels");

            // 1. an existing block as towers: the engine shows them
            var library = backend.LoadLibrary(gridId);
            var solid = Enumerable.Range(1, GridPaint.BlockCount(library)).First(b => GridPaint.Info(library, b) is { Dx: 1, Dy: 1, Dz: 1 });
            backend.SaveGrid(gridId, Towers(backend.LoadGrid(gridId), library, solid, hx, hy, hz));
            var control = GridEngineTest.Shoot(engine, sandbox, user, scene, Path.Combine(user, "control.png"))!;
            var controlChange = Changed(control, baseFrame);
            Check("towers of an existing block are drawn", controlChange > noise + 400, $"{controlChange} pixels changed");
            Keep("control.png");

            // 2. a hand-drawn brick in a new block: fresh files, the same towers
            File.Copy(Path.Combine(Lba2Dir, "LBA_BKG.HQR"), Path.Combine(sandbox, "LBA_BKG.HQR"), true);
            var bricks = GphLibrary.Lba2Bricks(sandbox);
            var palette = bricks.LoadPalette();
            // a strong, saturated colour that is neither the floor's nor the wall's
            var index = Enumerable.Range(1, 255).OrderByDescending(i => Math.Max(palette[i * 3], Math.Max(palette[i * 3 + 1], palette[i * 3 + 2])) - Math.Min(palette[i * 3], Math.Min(palette[i * 3 + 1], palette[i * 3 + 2]))).First();
            var image = new GphImage(48, 38);
            for (var y = 0; y < 25; y++) { var half = 24 - Math.Abs(12 - y) * 2; for (var x = 24 - half; x < 24 + half; x++) image.Set(x, y + 6, (byte)index); }
            var number = bricks.Add(image);
            bricks.Save(new[] { number }, "Add a new brick");
            backend = new Lba2GridBackend(sandbox);
            var lib2 = backend.LoadLibrary(gridId);
            var grown = GridPaint.AppendBlock(lib2, 1, 1, 1, number);
            var block = GridPaint.BlockCount(grown);
            backend.SaveLibrary(gridId, grown);
            backend.SaveGrid(gridId, Towers(backend.LoadGrid(gridId), grown, block, hx, hy, hz));
            var frame = GridEngineTest.Shoot(engine, sandbox, user, scene, Path.Combine(user, "newbrick.png"))!;
            var change = Changed(frame, baseFrame);
            Console.WriteLine($"  new brick {number} (palette colour {index}) in block {block}: {change} pixels changed");
            Check("towers of the new block are drawn (the LBA2 file grew a brick, a block and the header's brick count)", change > noise + 400);
            Keep("newbrick.png");
            Console.WriteLine(failures == 0 ? "new brick test: passed" : $"new brick test: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(user, true); } catch (IOException) { }
            try { Directory.Delete(sandbox, true); } catch (IOException) { }
        }
    }
}
