using System.Buffers.Binary;
using LBAAssembler;
using LBAAssembler.Lba1;

namespace ScriptRoundTrip;

// How a block's cells are numbered: for every block of a scene that spans several cells, which formula for `pos` of the cell (i, j, k) inside the block
// (dx, dy, dz) matches the grids of the retail game?
//   gridlayout
internal static class GridLayoutStudy
{
    public static int Run()
    {
        var dir = Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure";
        var grids = HqrArchive.Open(Path.Combine(dir, "LBA_GRI.HQR"));
        var libs = HqrArchive.Open(Path.Combine(dir, "LBA_BLL.HQR"));
        var formulas = new (string Name, Func<int, int, int, int, int, int, int> F)[]
        {
            ("y + dy*(x + dx*z)", (i, j, k, dx, dy, dz) => j + dy * (i + dx * k)),
            ("y + dy*(z + dz*x)", (i, j, k, dx, dy, dz) => j + dy * (k + dz * i)),
            ("x + dx*(y + dy*z)", (i, j, k, dx, dy, dz) => i + dx * (j + dy * k)),
            ("x + dx*(z + dz*y)", (i, j, k, dx, dy, dz) => i + dx * (k + dz * j)),
            ("z + dz*(x + dx*y)", (i, j, k, dx, dy, dz) => k + dz * (i + dx * j)),
            ("z + dz*(y + dy*x)", (i, j, k, dx, dy, dz) => k + dz * (j + dy * i)),
        };
        var ok = new int[formulas.Length]; var tested = 0; var partial = 0;
        var perBlock = new Dictionary<(int, int, int, int, int), int[]>();
        foreach (var scene in Enumerable.Range(0, 120))
        {
            if (!grids.IsValid(scene) || !libs.IsValid(scene)) continue;
            var cells = Lba1GridCodec.Decode(grids.Read(scene));
            var lib = libs.Read(scene);
            var count = (int)(BinaryPrimitives.ReadUInt32LittleEndian(lib) / 4);
            int At(int x, int y, int z) => ((z * 64 + x) * 25 + y) * 2;
            for (var z = 0; z < 64; z++) for (var x = 0; x < 64; x++) for (var y = 0; y < 25; y++)
            {
                int block = cells[At(x, y, z)], pos = cells[At(x, y, z) + 1];
                if (block == 0 || block > count || pos != 0) continue;
                var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(lib.AsSpan((block - 1) * 4));
                int dx = lib[at], dy = lib[at + 1], dz = lib[at + 2];
                if (dx * dy * dz <= 1 || x + dx > 64 || y + dy > 25 || z + dz > 64) continue;
                var intact = true;
                for (var i = 0; i < dx && intact; i++) for (var j = 0; j < dy && intact; j++) for (var k = 0; k < dz && intact; k++) intact = cells[At(x + i, y + j, z + k)] == block;
                if (!intact) { partial++; continue; }
                tested++;
                var key = (scene, block, dx, dy, dz);
                if (!perBlock.TryGetValue(key, out var counts)) perBlock[key] = counts = new int[formulas.Length + 1];
                counts[formulas.Length]++;
                for (var f = 0; f < formulas.Length; f++)
                {
                    var good = true;
                    for (var i = 0; i < dx && good; i++) for (var j = 0; j < dy && good; j++) for (var k = 0; k < dz && good; k++)
                        good = cells[At(x + i, y + j, z + k)] == block && cells[At(x + i, y + j, z + k) + 1] == formulas[f].F(i, j, k, dx, dy, dz);
                    if (good) ok[f]++;
                    if (good) perBlock[key][f]++;
                }
            }
        }
        Console.WriteLine($"{tested} multi-cell block placements at pos 0 with all their cells still the same block ({partial} more are overlapped by other blocks)");
        for (var f = 0; f < formulas.Length; f++) Console.WriteLine($"  {formulas[f].Name}: {ok[f]} match");
        // per distinct block (of a scene): does one formula explain all its placements?
        var consistent = 0; var mixed = new List<string>();
        foreach (var (key, counts) in perBlock)
        {
            var best = Enumerable.Range(0, formulas.Length).OrderByDescending(f => counts[f]).First();
            if (counts[best] == counts[formulas.Length]) consistent++; else if (mixed.Count < 8) mixed.Add($"scene {key.Item1} block {key.Item2} dims {key.Item3}x{key.Item4}x{key.Item5}: best {formulas[best].Name} {counts[best]} of {counts[formulas.Length]}");
        }
        Console.WriteLine($"{perBlock.Count} distinct blocks, {consistent} explained entirely by one formula");
        foreach (var m in mixed) Console.WriteLine("  " + m);
        return 0;
    }
}
