namespace LBA2LevelEditor.Terrain;

// How a bake lights the terrain. The retail islands' stored brightness (LUM, low nibble of each vertex) is a Lambert
// light of the terrain: azimuth = 360 degrees - BetaLight, elevation about AlphaLight (both /4096 turns), roughly
// nibble = 1 + 11 * (N . L), with cast shadows (cliffs, buildings) darkening some vertices further.
internal sealed class BakeOptions
{
    public double Azimuth { get; set; }
    public double Elevation { get; set; } = 45;
    // brightness = Offset + Gain * lambert, in nibble units (0..15)
    public double Gain { get; set; } = 11;
    public double Offset { get; set; } = 1.2;
    public bool TerrainShadows { get; set; }
    public bool DecorShadows { get; set; }
    // what a fully shadowed vertex is lit to at most
    public int ShadowLevel { get; set; } = 3;
    // shadow rays per vertex (1 = hard edges, 5 = a soft edge)
    public int Softness { get; set; } = 5;
    public double MaxDistance { get; set; } = 24000;

    // The light of a cube as its own INF says it.
    public static BakeOptions For(IslandCube cube) => new()
    {
        Azimuth = (360 - cube.BetaLight * 360.0 / 4096 % 360 + 360) % 360,
        Elevation = Math.Clamp(cube.AlphaLight * 360.0 / 4096 + 5, 5, 85),
    };
}

// The whole island's heights as one array (for shadow rays and normals across cube seams).
internal sealed class IslandHeightField
{
    public const int Size = IslandFile.GridSize + 1;
    private readonly short[] heights = new short[Size * Size];
    private readonly bool[] has = new bool[Size * Size];
    private readonly IslandFile island;

    public IslandHeightField(IslandFile island)
    {
        this.island = island;
        foreach (var (cx, cz, cube) in AllCells(island))
            for (var z = 0; z < IslandCube.Vertices; z++)
            for (var x = 0; x < IslandCube.Vertices; x++)
            {
                var i = (cz * IslandCube.Cells + z) * Size + cx * IslandCube.Cells + x;
                heights[i] = cube.Height(x, z); has[i] = true;
            }
    }

    private static IEnumerable<(int X, int Z, IslandCube Cube)> AllCells(IslandFile island)
    {
        for (var z = 0; z < IslandFile.MapSize; z++)
        for (var x = 0; x < IslandFile.MapSize; x++)
            if (island.CubeAt(x, z) is { } cube) yield return (x, z, cube);
    }

    public bool Has(int gx, int gz) => (uint)gx < Size && (uint)gz < Size && has[gz * Size + gx];
    public double At(int gx, int gz) => heights[Math.Clamp(gz, 0, Size - 1) * Size + Math.Clamp(gx, 0, Size - 1)];

    // Height at a world position (bilinear); null off the island.
    public double? Sample(double worldX, double worldZ)
    {
        var fx = worldX / IslandFile.CellSize; var fz = worldZ / IslandFile.CellSize;
        var gx = (int)Math.Floor(fx); var gz = (int)Math.Floor(fz);
        if (!Has(gx, gz) || !Has(gx + 1, gz) || !Has(gx, gz + 1) || !Has(gx + 1, gz + 1)) return null;
        var tx = fx - gx; var tz = fz - gz;
        var top = At(gx, gz) * (1 - tx) + At(gx + 1, gz) * tx;
        var bottom = At(gx, gz + 1) * (1 - tx) + At(gx + 1, gz + 1) * tx;
        return top * (1 - tz) + bottom * tz;
    }

    // Normal from the eight-neighbourhood, falling back on the vertex itself where a neighbour is missing.
    public (double X, double Y, double Z) Normal(int gx, int gz)
    {
        double H(int x, int z) => Has(x, z) ? At(x, z) : At(gx, gz);
        var dhx = (H(gx + 1, gz) - H(gx - 1, gz)) / (2.0 * IslandFile.CellSize);
        var dhz = (H(gx, gz + 1) - H(gx, gz - 1)) / (2.0 * IslandFile.CellSize);
        var len = Math.Sqrt(dhx * dhx + dhz * dhz + 1);
        return (-dhx / len, 1 / len, -dhz / len);
    }
}

internal static class IslandBake
{
    // Bakes brightness for the vertices of `region`, blending with what is stored by the region's weight. Returns the number of
    // vertices whose light changed.
    public static int Bake(IslandFile island, IslandRegion region, BakeOptions options, IProgress<double>? progress = null)
    {
        var field = new IslandHeightField(island);
        var boxes = options.DecorShadows ? DecorBoxes(island) : new List<(double, double, double, double, double, double)>();
        var vertices = region.Vertices(island).ToList();
        var changed = 0;
        var done = 0;
        foreach (var (gx, gz, w) in vertices)
        {
            var target = Compute(field, boxes, gx, gz, options);
            var current = island.LightAt(gx, gz) ?? 15;
            var next = (int)Math.Round(current + (target - current) * w);
            if (next != current) { island.SetLight(gx, gz, next); changed++; }
            if (progress is not null && ++done % 4096 == 0) progress.Report(done / (double)vertices.Count);
        }
        return changed;
    }

    // The plain lambert brightness (no cast shadows) of a vertex.
    public static double Lit(IslandHeightField field, int gx, int gz, BakeOptions options) =>
        Math.Clamp(options.Offset + options.Gain * IslandLight.Lambert(field.Normal(gx, gz), options.Azimuth, options.Elevation), 0, 15);

    // Adds cast shadows on top of the light that is stored: only darkens, by the fraction of the light each vertex loses (down
    // to ShadowLevel at most), so hand-made shading stays. Returns the number of vertices changed.
    public static int CastShadows(IslandFile island, IslandRegion region, BakeOptions options)
    {
        var field = new IslandHeightField(island);
        var boxes = options.DecorShadows ? DecorBoxes(island) : new List<(double, double, double, double, double, double)>();
        var changed = 0;
        foreach (var (gx, gz, w) in region.Vertices(island).ToList())
        {
            if (island.LightAt(gx, gz) is not { } current) continue;
            var fraction = ShadowFraction(field, boxes, gx, gz, options);
            if (fraction <= 0) continue;
            var target = Math.Min(current, current + (options.ShadowLevel - current) * fraction);
            var next = (int)Math.Round(current + (target - current) * w);
            if (next == current) continue;
            island.SetLight(gx, gz, next); changed++;
        }
        return changed;
    }

    // Removes baked shadows: vertices darker than the plain lambert light by more than `threshold` levels are lifted back to it
    // (blended by the region's weight); brighter and matching vertices are left alone. Returns the number changed.
    public static int LiftShadows(IslandFile island, IslandRegion region, BakeOptions options, double threshold = 1.5)
    {
        var field = new IslandHeightField(island);
        var changed = 0;
        foreach (var (gx, gz, w) in region.Vertices(island).ToList())
        {
            if (island.LightAt(gx, gz) is not { } current) continue;
            var baseline = Lit(field, gx, gz, options);
            if (current >= baseline - threshold) continue;
            var next = (int)Math.Round(current + (baseline - current) * w);
            if (next == current) continue;
            island.SetLight(gx, gz, next); changed++;
        }
        return changed;
    }

    // The retail islands' baked object shadows are footprints: the vertices under a building's (or tree's) bounding box are 4-6
    // levels darker than the plain lighting there. The footprint of a decor as a vertex rectangle, in island grid coordinates.
    public static (int X0, int Z0, int X1, int Z1) Footprint(int cubeCellX, int cubeCellZ, IslandDecor d) =>
        ((cubeCellX * IslandFile.CubeSize + d.XMin) / IslandFile.CellSize, (cubeCellZ * IslandFile.CubeSize + d.ZMin) / IslandFile.CellSize,
         (cubeCellX * IslandFile.CubeSize + d.XMax) / IslandFile.CellSize, (cubeCellZ * IslandFile.CubeSize + d.ZMax) / IslandFile.CellSize);

    // Darkens (or, with `remove`, lifts back to plain lighting) the vertices under one decor's footprint: light = plain lighting - depth.
    // Idempotent: doing it twice gives the same light. `margin` widens the rectangle by that many vertices.
    public static int FootprintShadow(IslandFile island, int cubeCellX, int cubeCellZ, IslandDecor decor, BakeOptions options, int depth, bool remove, int margin = 0, IslandHeightField? field = null)
    {
        field ??= new IslandHeightField(island);
        var (x0, z0, x1, z1) = Footprint(cubeCellX, cubeCellZ, decor);
        var changed = 0;
        for (var gz = z0 - margin; gz <= z1 + margin; gz++)
        for (var gx = x0 - margin; gx <= x1 + margin; gx++)
        {
            if (island.LightAt(gx, gz) is not { } current) continue;
            var plain = Lit(field, gx, gz, options);
            var next = remove ? (int)Math.Round(Math.Max(current, plain)) : (int)Math.Round(Math.Min(current, Math.Max(0, plain - depth)));
            if (next == current) continue;
            island.SetLight(gx, gz, next); changed++;
        }
        return changed;
    }

    // Footprint shadows under every decor at least `minSize` world units across (small props like bushes stay unshadowed).
    public static int FootprintShadowAll(IslandFile island, BakeOptions options, int depth, bool remove, int minSize = 1024)
    {
        var changed = 0;
        var field = new IslandHeightField(island);
        foreach (var (cx, cz, cube) in IslandOps.CubeCells(island))
            foreach (var d in cube.Decors)
                if (d.XMax - d.XMin >= minSize && d.ZMax - d.ZMin >= minSize) changed += FootprintShadow(island, cx, cz, d, options, depth, remove, 0, field);
        return changed;
    }

    // The brightness (0..15, fractional) a vertex gets under `options`.
    public static double Compute(IslandHeightField field, List<(double X0, double X1, double Y0, double Y1, double Z0, double Z1)> boxes, int gx, int gz, BakeOptions options)
    {
        var lit = Lit(field, gx, gz, options);
        if (!options.TerrainShadows && boxes.Count == 0) return lit;
        var fraction = ShadowFraction(field, boxes, gx, gz, options);
        var floor = Math.Min(lit, options.ShadowLevel);
        return Math.Clamp(lit + (floor - lit) * fraction, 0, 15);
    }

    // 0..1: how much of the light a vertex loses to cast shadows (terrain and, when asked, decors).
    public static double ShadowFraction(IslandHeightField field, List<(double X0, double X1, double Y0, double Y1, double Z0, double Z1)> boxes, int gx, int gz, BakeOptions options)
    {
        var shadowed = 0.0;
        var rays = Math.Max(1, options.Softness);
        for (var r = 0; r < rays; r++)
        {
            // a soft edge: the extra rays leave the light direction by up to 4 degrees
            var (da, de) = r == 0 ? (0.0, 0.0) : r switch { 1 => (3.0, 0.0), 2 => (-3.0, 0.0), 3 => (0.0, 3.0), _ => (0.0, -3.0) };
            if (Occluded(field, boxes, gx, gz, options.Azimuth + da, Math.Clamp(options.Elevation + de, 1, 89), options)) shadowed++;
        }
        return shadowed / rays;
    }

    private static bool Occluded(IslandHeightField field, List<(double X0, double X1, double Y0, double Y1, double Z0, double Z1)> boxes, int gx, int gz, double azimuth, double elevation, BakeOptions o)
    {
        var l = IslandLight.Direction(azimuth, elevation);
        double px = gx * (double)IslandFile.CellSize, pz = gz * (double)IslandFile.CellSize, py = field.At(gx, gz) + 40;
        // march towards the light in steps of a quarter cell
        var step = IslandFile.CellSize / 4.0;
        for (var d = step; d < o.MaxDistance; d += step)
        {
            var x = px + l.X * d; var y = py + l.Y * d; var z = pz + l.Z * d;
            if (o.TerrainShadows && field.Sample(x, z) is { } ground && ground > y) return true;
            if (y > 40000) break;
        }
        if (boxes.Count > 0)
            foreach (var b in boxes)
                if (RayHitsBox(px, py, pz, l.X, l.Y, l.Z, b, o.MaxDistance)) return true;
        return false;
    }

    private static bool RayHitsBox(double ox, double oy, double oz, double dx, double dy, double dz, (double X0, double X1, double Y0, double Y1, double Z0, double Z1) b, double max)
    {
        double tMin = 0, tMax = max;
        bool Slab(double o, double d, double lo, double hi)
        {
            if (Math.Abs(d) < 1e-9) return o >= lo && o <= hi;
            var t0 = (lo - o) / d; var t1 = (hi - o) / d;
            if (t0 > t1) (t0, t1) = (t1, t0);
            tMin = Math.Max(tMin, t0); tMax = Math.Min(tMax, t1);
            return tMin <= tMax;
        }
        return Slab(ox, dx, b.X0, b.X1) && Slab(oy, dy, b.Y0, b.Y1) && Slab(oz, dz, b.Z0, b.Z1) && tMax > 60;
    }

    // The decors' ZVs in island world coordinates.
    public static List<(double X0, double X1, double Y0, double Y1, double Z0, double Z1)> DecorBoxes(IslandFile island)
    {
        var boxes = new List<(double, double, double, double, double, double)>();
        foreach (var (cx, cz, cube) in IslandOps.CubeCells(island))
            foreach (var d in cube.Decors)
            {
                if (d.XMax <= d.XMin || d.YMax <= d.YMin || d.ZMax <= d.ZMin) continue;
                double ox = cx * (double)IslandFile.CubeSize, oz = cz * (double)IslandFile.CubeSize;
                boxes.Add((ox + d.XMin, ox + d.XMax, d.YMin, d.YMax, oz + d.ZMin, oz + d.ZMax));
            }
        return boxes;
    }
}

// Painting light and water depth by hand.
internal static class IslandLightOps
{
    public enum Mode { Set, Lighten, Darken, Smooth }

    // Set: pulls the light to `value` (0..15) by weight; Lighten / Darken: adds / subtracts `value` levels x weight; Smooth: averages.
    public static int Paint(IslandFile island, IslandRegion region, Mode mode, double value)
    {
        var n = 0;
        var vertices = region.Vertices(island).ToList();
        var smoothed = mode == Mode.Smooth ? vertices.Select(v => Average(island, v.Gx, v.Gz)).ToList() : null;
        for (var i = 0; i < vertices.Count; i++)
        {
            var (gx, gz, w) = vertices[i];
            if (island.LightAt(gx, gz) is not { } current) continue;
            var next = mode switch
            {
                Mode.Set => current + (value - current) * w,
                Mode.Lighten => current + value * w,
                Mode.Darken => current - value * w,
                _ => current + (smoothed![i] - current) * Math.Clamp(value, 0, 1) * w,
            };
            var rounded = (int)Math.Round(Math.Clamp(next, 0, 15));
            if (rounded == current) continue;
            island.SetLight(gx, gz, rounded);
            n++;
        }
        return n;
    }

    private static double Average(IslandFile island, int gx, int gz)
    {
        double sum = 0; var count = 0;
        for (var dz = -1; dz <= 1; dz++)
        for (var dx = -1; dx <= 1; dx++)
            if (island.LightAt(gx + dx, gz + dz) is { } l) { sum += l; count++; }
        return count == 0 ? 15 : sum / count;
    }

    // A round blob shadow: darkens by up to `depth` levels at the centre (the shadow under a tree or a house).
    public static int BlobShadow(IslandFile island, double gx, double gz, double radius, double depth) =>
        Paint(island, new BrushRegion(gx, gz, radius, 0.2), Mode.Darken, depth);

    // The water depth: the high nibble of each vertex, lowering the ground Twinsen walks on by 200 units per step on water polygons.
    public static int PaintWaterDepth(IslandFile island, IslandRegion region, int depth)
    {
        var n = 0;
        foreach (var (gx, gz, w) in region.Vertices(island))
        {
            if (w < 0.5) continue;
            foreach (var (cube, x, z) in island.Owners(gx, gz))
            {
                if (!cube.HasIntensity) continue;
                var i = z * IslandCube.Vertices + x;
                cube.Intensity[i] = (byte)((cube.Intensity[i] & 15) | (Math.Clamp(depth, 0, 15) << 4));
            }
            n++;
        }
        return n;
    }

    public static int WaterDepthAt(IslandFile island, int gx, int gz)
    {
        foreach (var (cube, x, z) in island.Owners(gx, gz)) return cube.HasIntensity ? cube.Intensity[z * IslandCube.Vertices + x] >> 4 : 0;
        return 0;
    }
}
