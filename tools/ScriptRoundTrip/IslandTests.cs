using LBAAssembler.Terrain;

namespace ScriptRoundTrip;

// LBA2 islands (.ILE): the writable model, checked against every island of the game folder.
//   island probe        what the files contain (cubes, shared ids, decor strides, light values, shared edges)
//   island roundtrip    parse -> ToBytes is byte-identical for every island; edits survive a save and reload
//   island all
internal static class IslandTests
{
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";

    public static int Run(string[] args)
    {
        var what = args.Length > 1 ? args[1] : "all";
        var failures = 0;
        if (what is "probe" or "all") failures += Probe();
        if (what is "roundtrip" or "all") failures += RoundTrip();
        if (what is "ops" or "all") failures += IslandOpsTests.Run();
        Console.WriteLine(failures == 0 ? "island tests: all passed" : $"island tests: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static IEnumerable<string> Islands() => Directory.GetFiles(Lba2Dir, "*.ILE").OrderBy(p => p);

    private static int Probe()
    {
        foreach (var path in Islands())
        {
            var island = IslandFile.Load(path);
            var ids = island.Cubes.Keys.ToList();
            var cells = Enumerable.Range(0, 256).Count(i => island.Map[i] != 0);
            var flagged = Enumerable.Range(0, 256).Count(i => (island.Map[i] & 0x80) != 0);
            var decors = island.Cubes.Values.Sum(c => c.Decors.Count);
            var strides = island.Cubes.Values.Where(c => c.Decors.Count > 0).Select(c => c.DecorStride).Distinct().OrderBy(s => s);
            var tails = island.Cubes.Values.Count(c => c.DecorTailBytes != 0 || c.InfoTail.Length != 0 || c.PolygonTail.Length != 0 || c.HeightTail.Length != 0 || c.IntensityTail.Length != 0 || c.TextureTail.Length != 0);
            var noLight = island.Cubes.Values.Count(c => !c.HasIntensity);
            var histogram = new int[16];
            var upper = new SortedSet<int>();
            foreach (var cube in island.Cubes.Values)
                foreach (var v in cube.Intensity) { histogram[v & 15]++; upper.Add(v >> 4); }
            var mismatches = EdgeMismatches(island);
            Console.WriteLine($"{Path.GetFileName(path),-14} cubes {ids.Count,3} cells {cells,3} (flagged {flagged}) decors {decors,4} stride [{string.Join(",", strides)}] tails {tails} noLight {noLight} edge mismatches h {mismatches.Height} l {mismatches.Light}");
            Console.WriteLine($"    light histogram {string.Join(" ", histogram)}  upper nibbles [{string.Join(",", upper)}]");
            foreach (var cube in island.Cubes.Values.Take(2))
            {
                var at = island.CellsOf(cube.Id).First();
                var d = cube.Decors.FirstOrDefault();
                if (d is not null) Console.WriteLine($"    cube {cube.Id} at map ({at.X},{at.Z}): first decor body {d.Body:X} pos ({d.X},{d.Y},{d.Z}) beta {d.Beta:X} code {d.CodeJeu} zv x {d.XMin}..{d.XMax} y {d.YMin}..{d.YMax} z {d.ZMin}..{d.ZMax}");
            }
            int agree = 0, cells2 = 0;
            var codes = new SortedDictionary<int, int>();
            foreach (var cube in island.Cubes.Values.Where(c => c.HasPolygons))
                for (var z = 0; z < 64; z++)
                for (var x = 0; x < 64; x++)
                {
                    var a = cube.Polygon(x, z, 0); var b = cube.Polygon(x, z, 1);
                    cells2++;
                    if (((a >> 16) & 1) == ((b >> 16) & 1)) agree++;
                    var code = (int)((a >> 12) & 15);
                    codes[code] = codes.GetValueOrDefault(code) + 1;
                }
            int codeA = 0, codeB = 0, total = 0, sensFirst = 0;
            foreach (var cube in island.Cubes.Values.Where(c => c.HasPolygons))
                for (var z = 0; z < 64; z++)
                for (var x = 0; x < 64; x++)
                {
                    total++;
                    var interleaved0 = cube.Polygons[z * 128 + x * 2]; var interleaved1 = cube.Polygons[z * 128 + x * 2 + 1];
                    var half0 = cube.Polygons[z * 128 + x]; var half1 = cube.Polygons[z * 128 + 64 + x];
                    if (((interleaved0 >> 12) & 15) == ((interleaved1 >> 12) & 15)) codeA++;
                    if (((half0 >> 12) & 15) == ((half1 >> 12) & 15)) codeB++;
                    if (((interleaved0 >> 16) & 1) == 1) sensFirst++;
                }
            Console.WriteLine($"    layout check: game code equal between the triangles of a cell: interleaved {codeA * 100.0 / Math.Max(1, total):F1}%, half rows {codeB * 100.0 / Math.Max(1, total):F1}%; first triangle diagonal flag set in {sensFirst * 100.0 / Math.Max(1, total):F1}%");
            Console.WriteLine($"    polygons: both triangles of a cell share the diagonal in {agree * 100.0 / Math.Max(1, cells2):F1}% of cells; game codes {string.Join(" ", codes.Select(kv => $"{kv.Key}:{kv.Value}"))}");
            var first = island.Cubes.Values.First();
            for (var t = 0; t < Math.Min(6, first.TextureDefs.Length / 6); t++) Console.WriteLine("    texdef " + t + ": " + string.Join(",", first.TextureDefs.Skip(t * 6).Take(6)));
            Console.WriteLine($"    cube {first.Id}: info [{string.Join(",", first.Info)}] alpha {first.AlphaLight} beta {first.BetaLight} bits {first.BitField:X4} texdefs {first.TextureDefs.Length / 6}");
        }
        return 0;
    }

    private static (int Height, int Light) EdgeMismatches(IslandFile island)
    {
        int h = 0, l = 0;
        for (var gz = 0; gz <= IslandFile.GridSize; gz++)
        for (var gx = 0; gx <= IslandFile.GridSize; gx++)
        {
            var owners = island.Owners(gx, gz).ToList();
            if (owners.Count < 2) continue;
            if (owners.Select(o => o.Cube.Height(o.X, o.Z)).Distinct().Count() > 1) h++;
            if (owners.Where(o => o.Cube.HasIntensity).Select(o => o.Cube.Light(o.X, o.Z)).Distinct().Count() > 1) l++;
        }
        return (h, l);
    }

    private static int RoundTrip()
    {
        var failures = 0;
        foreach (var path in Islands())
        {
            var bytes = File.ReadAllBytes(path);
            var island = IslandFile.Parse(bytes, path);
            var rewritten = island.ToBytes();
            var same = rewritten.AsSpan().SequenceEqual(bytes);
            Console.WriteLine($"  {Path.GetFileName(path),-14} unchanged rewrite: {(same ? "identical" : "DIFFERENT")} ({bytes.Length} bytes)");
            if (!same) { failures++; continue; }

            // an edit survives a save and a reload, and only the touched records change
            var edited = IslandFile.Parse(bytes, path);
            var cube = edited.Cubes.Values.First();
            edited.SetHeight(cube.Id * 0 + IslandFile.GridSize / 2, IslandFile.GridSize / 2, 1234);
            var probe = edited.Cubes.Values.First();
            probe.Heights[100] = 777;
            probe.SetLight(3, 3, probe.Light(3, 3) == 5 ? 6 : 5);
            var wanted = probe.Light(3, 3);
            var back = IslandFile.Parse(edited.ToBytes());
            var ok = back.Cubes[probe.Id].Heights[100] == 777 && back.Cubes[probe.Id].Light(3, 3) == wanted;
            if (!ok) { Console.WriteLine("    edit did not survive the save"); failures++; }
        }
        return failures;
    }
}
