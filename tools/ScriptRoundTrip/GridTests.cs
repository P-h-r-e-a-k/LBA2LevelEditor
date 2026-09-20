using LBA2LevelEditor;
using LBA2LevelEditor.Grids;
using LBA2LevelEditor.Lba1;

namespace ScriptRoundTrip;

// The interior grids of both games and the block painting on them.
//   grids                 every LBA2 grid round-trips through the LBA1 shape, painting / erasing blocks works on both games
//   grids png <lba1|lba2> <grid> <out.png>   an interior drawn with the game's bricks
internal static class GridTests
{
    private static readonly string Lba1Dir = Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure";
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
    private static int failures;

    private static void Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "ok    " : "FAILED")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        if (!ok) failures++;
    }

    public static int Run(string[] args)
    {
        if (args.Length > 4 && args[1] == "png") return Png(args);
        failures = 0;
        Backend(new Lba1GridBackend(Lba1Dir), "LBA1");
        Backend(new Lba2GridBackend(Lba2Dir), "LBA2");
        SaveThroughStores();
        Console.WriteLine(failures == 0 ? "grid tests: all passed" : $"grid tests: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void Backend(IGridBackend backend, string name)
    {
        Console.WriteLine($"{name}: {backend.Grids.Count} grids");
        var listed = 0;
        foreach (var (id, _) in backend.Grids)
        {
            var grid = backend.LoadGrid(id);
            if (Lba1GridEdit.UsedBlocksListed(grid)) listed++; else if (backend is Lba1GridBackend) Console.WriteLine($"    LBA1 entry {id} does not list its blocks");
        }
        Check($"{name} grids list the blocks they use", listed == backend.Grids.Count, $"{listed} of {backend.Grids.Count}");

        if (backend is Lba2GridBackend)
        {
            // header and body survive the LBA1 shape exactly
            var file = HqrFile.Parse(File.ReadAllBytes(Path.Combine(Lba2Dir, "LBA_BKG.HQR")));
            var header = file.Read(0);
            int griStart = BitConverter.ToUInt16(header, 0);
            var same = backend.Grids.All(g => { var entry = file.Read(griStart + g.Id); return Lba2GridBackend.FromLba1Shape(entry, Lba2GridBackend.ToLba1Shape(entry)).AsSpan().SequenceEqual(entry); });
            Check("LBA2 grids convert to the LBA1 shape and back unchanged", same);
        }

        // paint and erase on the grid with the most blocks
        var (id0, _) = backend.Grids.OrderByDescending(g => backend.LoadLibrary(g.Id).Length).First();
        var grid0 = backend.LoadGrid(id0);
        var library = backend.LoadLibrary(id0);
        var big = Enumerable.Range(1, GridPaint.BlockCount(library)).Select(b => GridPaint.Info(library, b)!.Value).First(i => i.Dx > 1 && i.Dz > 1 && i.Dy >= 1);
        // a free spot: the top layer, where nothing is drawn
        var before = Lba1GridCodec.Decode(grid0);
        (int X, int Z)? spot = null;
        for (var z = 0; z + big.Dz <= 64 && spot is null; z++)
            for (var x = 0; x + big.Dx <= 64 && spot is null; x++)
            {
                var free = true;
                for (var k = 0; k < big.Dz && free; k++) for (var j = 0; j < big.Dy && free; j++) for (var i = 0; i < big.Dx && free; i++)
                {
                    var at = (((z + k) * 64 + x + i) * 25 + (24 - big.Dy + 1 + j)) * 2;
                    free = before[at] == 0 && before[at + 1] == 0;
                }
                if (free) spot = (x, z);
            }
        if (spot is not { } s) { Check($"{name}: a free spot to paint on", false); return; }
        var y = 25 - big.Dy;
        var placed = GridPaint.Place(grid0, library, big.Number, s.X, y, s.Z);
        var after = Lba1GridCodec.Decode(placed);
        var good = true;
        for (var k = 0; k < big.Dz; k++) for (var j = 0; j < big.Dy; j++) for (var i = 0; i < big.Dx; i++)
        {
            var at = (((s.Z + k) * 64 + s.X + i) * 25 + y + j) * 2;
            good &= after[at] == big.Number && after[at + 1] == i + big.Dx * (j + big.Dy * k);
        }
        Check($"{name}: placing a {big.Dx}x{big.Dy}x{big.Dz} block writes its cells", good);
        var others = true;
        for (var i = 0; i < before.Length; i += 2)
        {
            var cell = i / 2; int cy = cell % 25, cx = cell / 25 % 64, cz = cell / 25 / 64;
            var inside = cx >= s.X && cx < s.X + big.Dx && cz >= s.Z && cz < s.Z + big.Dz && cy >= y && cy < y + big.Dy;
            if (!inside && (before[i] != after[i] || before[i + 1] != after[i + 1])) { others = false; break; }
        }
        Check($"{name}: placing leaves every other cell alone", others);
        Check($"{name}: the placed grid still lists its blocks", Lba1GridEdit.UsedBlocksListed(placed));
        var erased = GridPaint.Erase(placed, library, s.X + big.Dx - 1, y + big.Dy - 1, s.Z + big.Dz - 1);
        Check($"{name}: erasing any cell of a block removes the whole block", Lba1GridCodec.Decode(erased).AsSpan().SequenceEqual(before));
        var origin = GridPaint.OriginOf(placed, library, s.X + big.Dx - 1, y + big.Dy - 1, s.Z + big.Dz - 1);
        Check($"{name}: a cell knows its block's origin", origin is { } o && o.X == s.X && o.Y == y && o.Z == s.Z && o.Block == big.Number);
        var filled = GridPaint.FillRectangle(grid0, library, big.Number, s.X, s.Z, Math.Min(63, s.X + 5 * big.Dx), Math.Min(63, s.Z + 3 * big.Dz), y);
        Check($"{name}: filling a rectangle places several blocks", Lba1GridCodec.Decode(filled).Where((_, i) => i % 2 == 0).Count(b => b == big.Number) > big.Dx * big.Dy * big.Dz);

        // the library: a new block, an edited entry
        var grown = GridPaint.AppendBlock(library, 2, 1, 2, 5);
        var newBlock = GridPaint.BlockCount(grown);
        Check($"{name}: a new library block reads back", GridPaint.BlockCount(grown) == GridPaint.BlockCount(library) + 1 && GridPaint.Info(grown, newBlock) is { Dx: 2, Dy: 1, Dz: 2, FirstBrick: 5 }
            && Enumerable.Range(1, GridPaint.BlockCount(library)).All(b => GridPaint.Entries(grown, b).SequenceEqual(GridPaint.Entries(library, b))));
        var edited = GridPaint.SetEntry(library, big.Number, 1, 9);
        Check($"{name}: editing one entry changes only that entry", GridPaint.Entries(edited, big.Number).Count(e => e.Brick == 9) == 1 && GridPaint.Entries(edited, big.Number).Zip(GridPaint.Entries(library, big.Number)).Count(p => !p.First.Equals(p.Second)) <= 1);
    }

    // Saving a painted grid into copies of the game files: LBA1 goes through the scene store (validated), LBA2 rewrites LBA_BKG.HQR; both read back what was written.
    private static void SaveThroughStores()
    {
        var dir1 = Path.Combine(Path.GetTempPath(), "grid_save1_" + Guid.NewGuid().ToString("N")[..6]);
        var dir2 = Path.Combine(Path.GetTempPath(), "grid_save2_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir1); Directory.CreateDirectory(dir2);
        try
        {
            foreach (var f in new[] { "SCENE.HQR", "LBA_GRI.HQR", "LBA_BLL.HQR", "LBA_BRK.HQR", "RESS.HQR" }) File.Copy(Path.Combine(Lba1Dir, f), Path.Combine(dir1, f));
            foreach (var f in new[] { "LBA_BKG.HQR", "RESS.HQR" }) File.Copy(Path.Combine(Lba2Dir, f), Path.Combine(dir2, f));
            foreach (var backend in new IGridBackend[] { new Lba1GridBackend(dir1), new Lba2GridBackend(dir2) })
            {
                var id = backend.Grids.First(g => g.Id == 28 || backend is Lba2GridBackend).Id;
                var grid = backend.LoadGrid(id); var library = backend.LoadLibrary(id);
                var block = Enumerable.Range(1, GridPaint.BlockCount(library)).First(b => GridPaint.Info(library, b) is { Dx: 1, Dy: 1, Dz: 1 });
                var painted = GridPaint.Place(grid, library, block, 62, 24, 62);
                backend.SaveGrid(id, painted);
                var reread = backend is Lba1GridBackend ? new Lba1GridBackend(dir1) : (IGridBackend)new Lba2GridBackend(dir2);
                Check($"{backend.Title}: a painted grid saves and reads back", reread.LoadGrid(id).AsSpan().SequenceEqual(painted) && File.Exists(Path.Combine(backend.Folder, backend is Lba1GridBackend ? "LBA_GRI.HQR.bak" : "LBA_BKG.HQR.bak")));
            }
        }
        finally { try { Directory.Delete(dir1, true); Directory.Delete(dir2, true); } catch (IOException) { } }
    }

    private static int Png(string[] args)
    {
        IGridBackend backend = args[2] == "lba2" ? new Lba2GridBackend(Lba2Dir) : new Lba1GridBackend(Lba1Dir);
        var id = int.Parse(args[3]);
        var grid = backend.LoadGrid(id);
        var library = backend.LoadLibrary(id);
        var image = Lba1GridRenderer.Render(grid, library, backend.Brick, backend.Palette);
        PngWriter.Write(args[4], image.Bgra, image.Width, image.Height);
        Console.WriteLine($"{args[4]}: {image.Width}x{image.Height}, {GridPaint.BlockCount(library)} blocks in the library");
        return 0;
    }
}
