using LBA2LevelEditor.Terrain;

namespace ScriptRoundTrip;

// How the retail islands' baked light relates to their terrain: a Lambert light from the cube's AlphaLight (elevation)
// and BetaLight (azimuth), regressed against the stored brightness.
internal static class IslandLightFit
{
    public static int Run(string[] args)
    {
        var dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
        var only = args.Length > 2 ? args[2].ToUpperInvariant() : "DESERT";
        var island = IslandFile.Load(Path.Combine(dir, only + ".ILE"));
        foreach (var cube in island.Cubes.Values.Take(8))
        {
            var normals = IslandLight.Normals(cube);
            var light = cube.Intensity.Select(v => (double)(v & 15)).ToArray();
            var best = (Rmse: 1e9, Az: 0.0, El: 0.0, Gain: 0.0, Offset: 0.0);
            for (var az = 0.0; az < 360; az += 2.5)
            for (var el = 10.0; el <= 90; el += 2.5)
            {
                var lam = normals.Select(n => IslandLight.Lambert(n, az, el)).ToArray();
                var (gain, offset, rmse) = Regress(lam, light);
                if (rmse < best.Rmse) best = (rmse, az, el, gain, offset);
            }
            var alpha = cube.AlphaLight * 360.0 / 4096; var beta = cube.BetaLight * 360.0 / 4096;
            Console.WriteLine($"cube {cube.Id}: best az {best.Az} el {best.El} gain {best.Gain:F2} offset {best.Offset:F2} rmse {best.Rmse:F2} | alpha {alpha:F1} beta {beta:F1} (360-beta {(360 - beta) % 360:F1})");
        }
        return 0;
    }

    private static (double Gain, double Offset, double Rmse) Regress(double[] x, double[] y)
    {
        var mx = x.Average(); var my = y.Average();
        double sxy = 0, sxx = 0;
        for (var i = 0; i < x.Length; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
        var gain = sxx < 1e-12 ? 0 : sxy / sxx;
        var offset = my - gain * mx;
        double se = 0;
        for (var i = 0; i < x.Length; i++) { var e = y[i] - (offset + gain * x[i]); se += e * e; }
        return (gain, offset, Math.Sqrt(se / x.Length));
    }
}

// island render <ISLAND> <view> <out.png> [scale]: the map renderer's output, to look at.
internal static class IslandRenderCommand
{
    public static int Run(string[] args)
    {
        var dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
        var name = args.Length > 2 ? args[2].ToUpperInvariant() : "DESERT";
        var view = args.Length > 3 ? Enum.Parse<MapView>(args[3], true) : MapView.Terrain;
        var output = args.Length > 4 ? args[4] : Path.Combine(Path.GetTempPath(), name + "_" + view + ".png");
        var scale = args.Length > 5 ? int.Parse(args[5]) : 3;
        var island = IslandFile.Load(Path.Combine(dir, name + ".ILE"));
        var renderer = new IslandMapRenderer(island, IslandMapRenderer.LoadPalette(dir, name), scale);
        renderer.RenderAll(view);
        PngWriter.Write(output, renderer.Pixels, renderer.PixelWidth, renderer.PixelHeight);
        Console.WriteLine($"{output}: {renderer.PixelWidth}x{renderer.PixelHeight}");
        return 0;
    }
}

// island footprints <ISLAND>: do the retail baked shadows sit under the decors' bounding boxes?
internal static class IslandFootprintStudy
{
    public static int Run(string[] args)
    {
        var dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
        foreach (var name in args.Length > 2 ? new[] { args[2].ToUpperInvariant() } : new[] { "DESERT", "CITABAU", "OTRINGAL", "KNARTAS" })
        {
            var island = IslandFile.Load(Path.Combine(dir, name + ".ILE"));
            var options = BakeOptions.For(island.Cubes.Values.First());
            var field = new IslandHeightField(island);
            double insideSum = 0, outsideSum = 0; int inside = 0, outside = 0, dark = 0, boxes = 0;
            var inBox = new HashSet<(int, int)>();
            foreach (var (cx, cz, cube) in IslandOps.CubeCells(island))
                foreach (var d in cube.Decors)
                {
                    if ((d.XMax - d.XMin) < 1024 || (d.ZMax - d.ZMin) < 1024) continue;
                    boxes++;
                    int x0 = (cx * 32768 + d.XMin) / 512, x1 = (cx * 32768 + d.XMax) / 512, z0 = (cz * 32768 + d.ZMin) / 512, z1 = (cz * 32768 + d.ZMax) / 512;
                    for (var z = z0; z <= z1; z++) for (var x = x0; x <= x1; x++) inBox.Add((x, z));
                }
            foreach (var (cx, cz, cube) in IslandOps.CubeCells(island))
                for (var z = 0; z < 65; z++) for (var x = 0; x < 65; x++)
                {
                    var gx = cx * 64 + x; var gz = cz * 64 + z;
                    var diff = cube.Light(x, z) - IslandBake.Lit(field, gx, gz, options);
                    if (inBox.Contains((gx, gz))) { insideSum += diff; inside++; if (diff < -3) dark++; } else { outsideSum += diff; outside++; }
                }
            Console.WriteLine($"{name}: {boxes} large boxes, {inside} vertices inside (mean light minus plain light {insideSum / Math.Max(1, inside):F2}, {dark} darker than -3), {outside} outside (mean {outsideSum / Math.Max(1, outside):F2})");
        }
        return 0;
    }
}
