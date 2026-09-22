using LBAAssembler.Terrain;

namespace ScriptRoundTrip;

// The terrain operations on real islands (DESERT, the uneven one), on in-memory copies.
internal static class IslandOpsTests
{
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
    private static int failures;

    private static void Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"  {(ok ? "ok    " : "FAILED")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        if (!ok) failures++;
    }

    private static IslandFile Desert() => IslandFile.Load(Path.Combine(Lba2Dir, "DESERT.ILE"));

    public static int Run()
    {
        failures = 0;
        Heights(); Levelling(); Light(); Ground(); Decors(); History(); SaveAndReload();
        return failures;
    }

    // a vertex inside the island, away from the edges, with the cube borders near
    private static (int Gx, int Gz) Middle(IslandFile island)
    {
        var (minX, minZ, maxX, maxZ) = island.PresentBounds();
        return ((minX + maxX + 1) * IslandCube.Cells / 2, (minZ + maxZ + 1) * IslandCube.Cells / 2);
    }

    // a well-lit vertex with all its neighbours near the middle, so light edits have something to change
    private static (int Gx, int Gz) Bright(IslandFile island)
    {
        var (cx, cz) = Middle(island);
        for (var r = 0; r < 200; r++)
        for (var dz = -r; dz <= r; dz++)
        for (var dx = -r; dx <= r; dx++)
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) == r && island.LightAt(cx + dx, cz + dz) >= 9 && island.HasVertex(cx + dx + 6, cz + dz + 6) && island.HasVertex(cx + dx - 6, cz + dz - 6)) return (cx + dx, cz + dz);
        return (cx, cz);
    }

    private static bool EdgesAgree(IslandFile island)
    {
        for (var gz = 0; gz <= IslandFile.GridSize; gz++)
        for (var gx = 0; gx <= IslandFile.GridSize; gx += 1)
        {
            if (gx % 64 != 0 && gz % 64 != 0) continue;
            var owners = island.Owners(gx, gz).ToList();
            if (owners.Count < 2) continue;
            if (owners.Select(o => o.Cube.Height(o.X, o.Z)).Distinct().Count() > 1) return false;
        }
        return true;
    }

    private static void Heights()
    {
        Console.WriteLine("heights");
        var island = Desert();
        var (mx, mz) = Middle(island);
        // straddle a cube corner: the vertex is shared by up to four cubes
        var before = island.HeightAt(mx, mz)!.Value;
        IslandOps.Raise(island, new BrushRegion(mx, mz, 6, 0.5), 300);
        Check("raise moves the centre", island.HeightAt(mx, mz) == before + 300, $"{before} -> {island.HeightAt(mx, mz)}");
        Check("raise keeps shared cube borders equal", EdgesAgree(island));
        var at = IslandOps.Altitude(island, mx * 512.0, mz * 512.0);
        Check("altitude at a vertex is that vertex's height", at is { } a && Math.Abs(a - island.HeightAt(mx, mz)!.Value) < 1, $"{at}");
        var (min, max) = IslandOps.HeightRange(island);
        Check("height range is sane", min < max, $"{min}..{max}");
        // smoothing reduces a spike
        island.SetHeight(mx, mz, island.HeightAt(mx, mz)!.Value + 800);
        var spike = island.HeightAt(mx, mz)!.Value;
        IslandOps.Smooth(island, new BrushRegion(mx, mz, 5, 0.9), 1);
        Check("smooth lowers the spike", island.HeightAt(mx, mz) < spike);
    }

    private static void Levelling()
    {
        Console.WriteLine("levelling");
        var island = Desert();
        var (mx, mz) = Middle(island);
        var brush = new BrushRegion(mx, mz, 10, 0.9);
        IslandOps.FlattenTo(island, brush, 1000, 1);
        var core = brush.Vertices(island).Where(v => v.Weight >= 0.999).ToList();
        Check("flatten to a level puts the core exactly there", core.Count > 0 && core.All(v => island.HeightAt(v.Gx, v.Gz) == 1000), $"{core.Count} vertices");
        Check("flatten keeps borders equal", EdgesAgree(island));

        // a synthetic plane: fit recovers it, levelling to it with flatten 0 changes nothing, flatten 1 makes it horizontal
        var rect = new RectRegion(mx - 8, mz - 8, mx + 8, mz + 8);
        foreach (var (gx, gz, _) in rect.Vertices(island).ToList()) island.SetHeight(gx, gz, (int)Math.Round(2000.0 + 10 * (gx - mx) + 4 * (gz - mz)));
        var (a, b, c) = IslandOps.FitPlane(island, rect);
        Check("plane fit recovers slope and offset", Math.Abs(a - 10) < 0.5 && Math.Abs(b - 4) < 0.5 && Math.Abs(a * mx + b * mz + c - 2000) < 2, $"a {a:F2} b {b:F2} h(mid) {a * mx + b * mz + c:F1}");
        var noisy = island.HeightAt(mx + 3, mz + 2)!.Value + 250;
        island.SetHeight(mx + 3, mz + 2, noisy);
        var plane = IslandOps.FitPlane(island, rect);
        IslandOps.LevelToPlane(island, rect, plane, 1, 0);
        var residual = Math.Abs(island.HeightAt(mx + 3, mz + 2)!.Value - (2000 + 10 * 3 + 4 * 2));
        Check("levelling to the fitted plane removes a bump", residual < 40, $"residual {residual}");
        IslandOps.LevelToPlane(island, rect, plane, 1, 1);
        var flat = rect.Vertices(island).Select(v => island.HeightAt(v.Gx, v.Gz)!.Value).Distinct().Count();
        Check("flatten = 1 makes the region horizontal", flat == 1, $"{flat} distinct heights");

        // ramp: linear along the line
        IslandOps.Ramp(island, (mx - 12, mz + 12, 500), (mx + 12, mz + 12, 2500), 1, 2);
        var mid = island.HeightAt(mx, mz + 12)!.Value;
        Check("ramp interpolates between the ends", Math.Abs(mid - 1500) < 60, $"mid {mid}");
        Check("ramp keeps borders equal", EdgesAgree(island));

        IslandOps.Terrace(island, new RectRegion(mx - 5, mz - 5, mx + 5, mz + 5), 400);
        Check("terrace snaps to multiples of the step", new RectRegion(mx - 5, mz - 5, mx + 5, mz + 5).Vertices(island).All(v => island.HeightAt(v.Gx, v.Gz)!.Value % 400 == 0));

        // objects follow the ground when it is levelled
        var island2 = Desert();
        var (cx, cz, cube) = IslandOps.CubeCells(island2).First(c => c.Cube.Decors.Count > 0);
        var decor = cube.Decors[0];
        var gxv = (cx * IslandFile.CubeSize + decor.X) / 512; var gzv = (cz * IslandFile.CubeSize + decor.Z) / 512;
        var follow = new IslandOps.DecorFollow(island2);
        var oldY = decor.Y;
        IslandOps.Raise(island2, new BrushRegion(gxv, gzv, 4, 0.9), 400);
        var moved = follow.Apply();
        Check("decors follow the ground", moved > 0 && decor.Y > oldY, $"{moved} moved, y {oldY} -> {decor.Y}");
    }

    private static void Light()
    {
        Console.WriteLine("light and shadows");
        var island = Desert();
        // how close does a bake of the terrain alone get to the retail light? (retail has hand-placed shadows on top)
        var field = new IslandHeightField(island);
        var boxes = new List<(double, double, double, double, double, double)>();
        double se = 0, seShadow = 0; var n = 0;
        var options = BakeOptions.For(island.Cubes.Values.First());
        options.TerrainShadows = false;
        var shadowed = BakeOptions.For(island.Cubes.Values.First());
        shadowed.TerrainShadows = true;
        foreach (var (cx, cz, cube) in IslandOps.CubeCells(island).Take(6))
            for (var z = 2; z < 63; z += 3)
            for (var x = 2; x < 63; x += 3)
            {
                var gx = cx * 64 + x; var gz = cz * 64 + z;
                var stored = cube.Light(x, z);
                var a = IslandBake.Compute(field, boxes, gx, gz, options) - stored;
                var b = IslandBake.Compute(field, boxes, gx, gz, shadowed) - stored;
                se += a * a; seShadow += b * b; n++;
            }
        var rmse = Math.Sqrt(se / n); var rmseShadow = Math.Sqrt(seShadow / n);
        Check("a lambert bake lands near the retail light", rmse < 3.2, $"rmse {rmse:F2} levels of 16 (with cast shadows {rmseShadow:F2})");

        var (mx, mz) = Bright(island);
        var waterBefore = IslandLightOps.WaterDepthAt(island, mx, mz);
        var lightBefore = island.LightAt(mx, mz)!.Value;
        IslandLightOps.BlobShadow(island, mx, mz, 5, 6);
        var lightAfter = island.LightAt(mx, mz)!.Value;
        Check("a blob shadow darkens the centre", lightAfter < lightBefore || lightBefore == 0, $"{lightBefore} -> {lightAfter}");
        Check("painting light leaves the water depth alone", IslandLightOps.WaterDepthAt(island, mx, mz) == waterBefore);
        IslandLightOps.Paint(island, new BrushRegion(mx, mz, 4, 0.9), IslandLightOps.Mode.Set, 15);
        Check("set 15 lights the core fully", island.LightAt(mx, mz) == 15);
        IslandLightOps.PaintWaterDepth(island, new BrushRegion(mx, mz, 3, 0.9), 5);
        Check("water depth is the high nibble", IslandLightOps.WaterDepthAt(island, mx, mz) == 5 && island.LightAt(mx, mz) == 15);

        // a removed shadow: bake without shadows over the region brings the darkened vertex back up
        IslandLightOps.Paint(island, new BrushRegion(mx, mz, 4, 0.9), IslandLightOps.Mode.Set, 0);
        var opts = BakeOptions.For(island.Cubes.Values.First()); opts.TerrainShadows = false;
        IslandBake.Bake(island, new BrushRegion(mx, mz, 4, 0.9), opts);
        Check("baking over a painted shadow removes it", island.LightAt(mx, mz) > 2, $"{island.LightAt(mx, mz)}");

        // cast shadows: a spike next to a vertex, with a low sun, darkens it
        var flat = Desert();
        var (fx, fz) = Middle(flat);
        IslandOps.FlattenTo(flat, new BrushRegion(fx, fz, 20, 0.95), 1000, 1);
        var low = new BakeOptions { Azimuth = 90, Elevation = 20, TerrainShadows = true };
        IslandOps.FlattenTo(flat, new BrushRegion(fx + 6, fz, 1.5, 0.99), 4000, 1);
        var testField = new IslandHeightField(flat);
        var lit = IslandBake.Compute(testField, new List<(double, double, double, double, double, double)>(), fx - 6, fz, new BakeOptions { Azimuth = 90, Elevation = 20, TerrainShadows = false });
        var inShadow = IslandBake.Compute(testField, new List<(double, double, double, double, double, double)>(), fx - 6, fz, low);
        Check("a tall spike casts a shadow on the far side from the sun", inShadow < lit - 1, $"{lit:F1} -> {inShadow:F1}");

        // object footprints: the retail baked shadows under buildings; add and remove them
        var fp = Desert();
        var fpOptions = BakeOptions.For(fp.Cubes.Values.First());
        var (fcx, fcz, fcube) = IslandOps.CubeCells(fp).First(c => c.Cube.Decors.Any(d => d.XMax - d.XMin >= 1536 && d.ZMax - d.ZMin >= 1536));
        var big = fcube.Decors.First(d => d.XMax - d.XMin >= 1536 && d.ZMax - d.ZMin >= 1536);
        var (fx0, fz0, fx1, fz1) = IslandBake.Footprint(fcx, fcz, big);
        var fpField = new IslandHeightField(fp);
        double Mean(Func<int, int, double> f) { double s = 0; int n = 0; for (var z = fz0; z <= fz1; z++) for (var x = fx0; x <= fx1; x++) if (fp.HasVertex(x, z)) { s += f(x, z); n++; } return s / Math.Max(1, n); }
        IslandBake.FootprintShadow(fp, fcx, fcz, big, fpOptions, 6, false);
        var darker = Mean((x, z) => fp.LightAt(x, z)!.Value - IslandBake.Lit(fpField, x, z, fpOptions));
        Check("a footprint shadow darkens the vertices under an object", darker < -3, $"mean {darker:F1} levels against plain light");
        var again = fp.ToBytes();
        IslandBake.FootprintShadow(fp, fcx, fcz, big, fpOptions, 6, false);
        Check("adding it twice changes nothing more", fp.ToBytes().AsSpan().SequenceEqual(again));
        IslandBake.FootprintShadow(fp, fcx, fcz, big, fpOptions, 6, true);
        var lifted = Mean((x, z) => fp.LightAt(x, z)!.Value - IslandBake.Lit(fpField, x, z, fpOptions));
        Check("removing it lifts them back to the plain light", lifted > -0.75, $"mean {lifted:F1}");
    }

    private static void Ground()
    {
        Console.WriteLine("ground polygons");
        var island = Desert();
        var (mx, mz) = Middle(island);
        var (cx, cz, source) = IslandOps.CubeCells(island).First();
        // pick a textured triangle from the first cube and paint it into the cube at the middle
        IslandGround.Sample? sample = null;
        for (var z = 0; z < 64 && sample is null; z++)
        for (var x = 0; x < 64 && sample is null; x++)
            if (IslandGround.Pick(island, cx * 64 + x, cz * 64 + z, 0) is { Texture: not null } s && s.Polygon.TexFlag != 0) sample = s;
        if (sample is null) { Check("found a textured triangle", false); return; }
        var target = island.CubeAt(mx / 64, mz / 64)!;
        var beforeDefs = target.TextureDefs.Length;
        IslandGround.Paint(island, new BrushRegion(mx, mz, 3, 0.9), sample, PolygonFields.Texture);
        var painted = IslandGround.Pick(island, mx, mz, 0)!;
        Check("painting copies the texture", painted.Texture is not null && painted.Texture.AsSpan().SequenceEqual(sample.Texture) && painted.Polygon.TexFlag == sample.Polygon.TexFlag,
            $"defs {beforeDefs / 6} -> {target.TextureDefs.Length / 6}");
        IslandGround.PaintGameCode(island, new BrushRegion(mx, mz, 3, 0.9), 1);
        Check("painting the game code makes water", IslandGround.Pick(island, mx, mz, 1)!.Polygon.CodeJeu == 1 && IslandGround.Pick(island, mx, mz, 0)!.Polygon.TextureIndex == painted.Polygon.TextureIndex);
        var p = new IslandPolygon(0).With(bank: 5, texFlag: 3, polyFlag: 2, sampleStep: 7, codeJeu: 9, diagonal: true, col: true, textureIndex: 4321);
        Check("polygon fields pack and unpack", p is { Bank: 5, TexFlag: 3, PolyFlag: 2, SampleStep: 7, CodeJeu: 9, Diagonal: true, Col: true, TextureIndex: 4321 });
        IslandOps.Raise(island, new BrushRegion(mx, mz, 4, 0.9), 700);
        IslandGround.OptimiseDiagonals(island, new BrushRegion(mx, mz, 6, 0.9));
        Check("diagonals can be re-optimised", true);
    }

    private static void Decors()
    {
        Console.WriteLine("decors");
        var island = Desert();
        var (mx, mz) = Middle(island);
        var added = IslandDecors.Add(island, 3, mx * 512.0, mz * 512.0);
        Check("a decor can be added on the ground", added is not null && Math.Abs(added.Value.Decor.Y - (IslandOps.Altitude(island, mx * 512.0, mz * 512.0) ?? 0)) < 1);
        if (added is not { } ad) return;
        var count = ad.Cube.Decors.Count;
        var (ox, oz) = (ad.Decor.X, ad.Decor.Z);
        IslandDecors.Move(island, ad.Cube, ad.Decor, mx * 512.0 + 2000, mz * 512.0 + 1000);
        Check("moving translates the ZV with it", ad.Decor.XMin < ad.Decor.X && ad.Decor.XMax > ad.Decor.X && ad.Decor.X != ox);
        var bytes = island.ToBytes();
        var back = IslandFile.Parse(bytes);
        Check("the saved decor count follows the list", back.Cubes.Values.Sum(c => c.Decors.Count) == island.Cubes.Values.Sum(c => c.Decors.Count) && back.Cubes.All(kv => kv.Value.Info[IslandCube.InfoNbDecors] == kv.Value.Decors.Count));
        IslandDecors.Remove(island.Cubes.Values.First(c => c.Decors.Contains(ad.Decor)), ad.Decor);
        Check("a decor can be removed", island.Cubes.Values.Sum(c => c.Decors.Count) == back.Cubes.Values.Sum(c => c.Decors.Count) - 1);
        _ = count;
    }

    private static void History()
    {
        Console.WriteLine("undo / redo");
        var island = Desert();
        var original = island.ToBytes();
        var history = new IslandHistory(island);
        var (mx, mz) = Middle(island);
        history.Begin();
        IslandOps.Raise(island, new BrushRegion(mx, mz, 8, 0.5), 500);
        IslandLightOps.BlobShadow(island, mx, mz, 6, 5);
        Check("commit records the edit", history.Commit("raise"));
        var edited = island.ToBytes();
        Check("the edit changed the file", !edited.AsSpan().SequenceEqual(original));
        history.Undo();
        Check("undo restores the original byte for byte", island.ToBytes().AsSpan().SequenceEqual(original));
        history.Redo();
        Check("redo re-applies the edit", island.ToBytes().AsSpan().SequenceEqual(edited));
        history.Begin();
        Check("an edit that changes nothing records nothing", !history.Commit("nothing"));
    }

    private static void SaveAndReload()
    {
        Console.WriteLine("saving");
        var dir = Path.Combine(Path.GetTempPath(), "island_save_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "DESERT.ILE");
            File.Copy(Path.Combine(Lba2Dir, "DESERT.ILE"), path);
            var island = IslandFile.Load(path);
            var (mx, mz) = Middle(island);
            IslandOps.Raise(island, new BrushRegion(mx, mz, 6, 0.5), 250);
            IslandLightOps.BlobShadow(island, mx, mz, 5, 4);
            var expectedHeight = island.HeightAt(mx, mz);
            var expectedLight = island.LightAt(mx, mz);
            island.Save();
            Check("the .bak keeps the original", File.Exists(path + ".bak") && File.ReadAllBytes(path + ".bak").AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Lba2Dir, "DESERT.ILE"))));
            var back = IslandFile.Load(path);
            Check("the saved island has the edit", back.HeightAt(mx, mz) == expectedHeight && back.LightAt(mx, mz) == expectedLight);
            // only the touched records were rewritten: the file is not much bigger than the original
            var grew = new FileInfo(path).Length - new FileInfo(path + ".bak").Length;
            Check("unchanged records were left alone", grew < 100_000, $"{grew} bytes bigger");
            back.Save();
            Check("saving again is stable", File.ReadAllBytes(path).AsSpan().SequenceEqual(back.ToBytes()));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
