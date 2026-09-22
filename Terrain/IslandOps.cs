namespace LBAAssembler.Terrain;

// Which vertices an operation touches and how strongly (0..1): a round brush, a rectangle or the whole island.
// Vertices are island-wide grid coordinates (0..1024, one per 512 world units); only vertices of present cubes count.
internal abstract class IslandRegion
{
    public abstract IEnumerable<(int Gx, int Gz, double Weight)> Vertices(IslandFile island);
    public abstract (double Gx, double Gz) Center { get; }

    // A brush profile: full weight inside `hardness * radius`, a cosine ramp to zero at the radius.
    public static double Falloff(double distance, double radius, double hardness)
    {
        if (distance >= radius) return 0;
        var inner = radius * Math.Clamp(hardness, 0, 0.999);
        if (distance <= inner) return 1;
        var t = (distance - inner) / (radius - inner);
        return 0.5 + 0.5 * Math.Cos(t * Math.PI);
    }
}

internal sealed class BrushRegion : IslandRegion
{
    public double Gx { get; }
    public double Gz { get; }
    public double Radius { get; }
    public double Hardness { get; }
    public BrushRegion(double gx, double gz, double radius, double hardness = 0.35) { Gx = gx; Gz = gz; Radius = Math.Max(0.5, radius); Hardness = hardness; }
    public override (double Gx, double Gz) Center => (Gx, Gz);

    public override IEnumerable<(int Gx, int Gz, double Weight)> Vertices(IslandFile island)
    {
        var x0 = (int)Math.Floor(Gx - Radius); var x1 = (int)Math.Ceiling(Gx + Radius);
        var z0 = (int)Math.Floor(Gz - Radius); var z1 = (int)Math.Ceiling(Gz + Radius);
        for (var z = z0; z <= z1; z++)
        for (var x = x0; x <= x1; x++)
        {
            var w = Falloff(Math.Sqrt((x - Gx) * (x - Gx) + (z - Gz) * (z - Gz)), Radius, Hardness);
            if (w > 0 && island.HasVertex(x, z)) yield return (x, z, w);
        }
    }
}

internal sealed class RectRegion : IslandRegion
{
    public int X0 { get; }
    public int Z0 { get; }
    public int X1 { get; }
    public int Z1 { get; }
    public double Feather { get; }
    public RectRegion(int x0, int z0, int x1, int z1, double feather = 0) { X0 = Math.Min(x0, x1); X1 = Math.Max(x0, x1); Z0 = Math.Min(z0, z1); Z1 = Math.Max(z0, z1); Feather = feather; }
    public override (double Gx, double Gz) Center => ((X0 + X1) / 2.0, (Z0 + Z1) / 2.0);

    public override IEnumerable<(int Gx, int Gz, double Weight)> Vertices(IslandFile island)
    {
        var f = (int)Math.Ceiling(Feather);
        for (var z = Z0 - f; z <= Z1 + f; z++)
        for (var x = X0 - f; x <= X1 + f; x++)
        {
            var d = Math.Max(Math.Max(X0 - x, x - X1), Math.Max(Math.Max(Z0 - z, z - Z1), 0));
            var w = d == 0 ? 1 : Feather <= 0 ? 0 : Math.Max(0, 1 - d / (Feather + 1));
            if (w > 0 && island.HasVertex(x, z)) yield return (x, z, w);
        }
    }
}

internal sealed class WholeIslandRegion : IslandRegion
{
    public override (double Gx, double Gz) Center => (IslandFile.GridSize / 2.0, IslandFile.GridSize / 2.0);

    public override IEnumerable<(int Gx, int Gz, double Weight)> Vertices(IslandFile island)
    {
        // each vertex once: a shared border vertex is written to all its cubes by the setters
        for (var z = 0; z <= IslandFile.GridSize; z++)
        for (var x = 0; x <= IslandFile.GridSize; x++)
            if (island.HasVertex(x, z)) yield return (x, z, 1);
    }
}

// Terrain editing: height brushes, the levelling tools and ground queries. Everything goes through the island-wide
// vertex accessors, so vertices on a cube's border stay equal in all the cubes that hold them.
internal static class IslandOps
{
    // ---- ground queries --------------------------------------------------------------------------------------------

    // The height Twinsen stands at, world x / z in the island frame (the engine's CalculAltitudeObjet: the cell is cut along
    // the diagonal its first triangle's flag says). Null off the island.
    public static double? Altitude(IslandFile island, double worldX, double worldZ)
    {
        var gx = (int)Math.Floor(worldX / IslandFile.CellSize); var gz = (int)Math.Floor(worldZ / IslandFile.CellSize);
        if (island.HeightAt(gx, gz) is not { } h0 || island.HeightAt(gx, gz + 1) is not { } h1 || island.HeightAt(gx + 1, gz + 1) is not { } h2 || island.HeightAt(gx + 1, gz) is not { } h3)
            return null;
        var x = worldX - gx * IslandFile.CellSize; var z = worldZ - gz * IslandFile.CellSize;
        var sens = false;
        var cx = Math.Min(gx / IslandCube.Cells, IslandFile.MapSize - 1); var cz = Math.Min(gz / IslandCube.Cells, IslandFile.MapSize - 1);
        if (island.CubeAt(cx, cz) is { HasPolygons: true } cube) sens = ((cube.Polygon(gx % IslandCube.Cells, gz % IslandCube.Cells, 0) >> 16) & 1) != 0;
        double y0 = h0, y1 = h1, y2 = h2, y3 = h3;
        if (!sens)
            return x < z ? y0 + ((y1 - y0) * z + (y2 - y1) * x) / 512 : y0 + ((y3 - y0) * x + (y2 - y3) * z) / 512;
        return 511 - x > z ? y0 + ((y3 - y0) * x + (y1 - y0) * z) / 512 : y1 + ((y2 - y1) * x + (y3 - y2) * (511 - z)) / 512;
    }

    public static (short Min, short Max) HeightRange(IslandFile island)
    {
        short min = short.MaxValue, max = short.MinValue;
        foreach (var cube in island.Cubes.Values)
            foreach (var h in cube.Heights) { if (h < min) min = h; if (h > max) max = h; }
        return island.Cubes.Count == 0 ? ((short)0, (short)0) : (min, max);
    }

    // ---- height brushes --------------------------------------------------------------------------------------------

    // Adds `amount` world units, scaled by the weight of each vertex.
    public static int Raise(IslandFile island, IslandRegion region, double amount)
    {
        var n = 0;
        foreach (var (gx, gz, w) in region.Vertices(island))
        {
            island.SetHeight(gx, gz, (int)Math.Round(island.HeightAt(gx, gz)!.Value + amount * w));
            n++;
        }
        return n;
    }

    // Moves each vertex towards the average of its eight neighbours by `strength` (0..1) x weight.
    public static int Smooth(IslandFile island, IslandRegion region, double strength)
    {
        var updates = new List<(int, int, double)>();
        foreach (var (gx, gz, w) in region.Vertices(island))
        {
            double sum = 0; var count = 0;
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
                if (island.HeightAt(gx + dx, gz + dz) is { } h) { sum += h; count++; }
            var h0 = island.HeightAt(gx, gz)!.Value;
            updates.Add((gx, gz, h0 + (sum / count - h0) * Math.Clamp(strength, 0, 1) * w));
        }
        foreach (var (gx, gz, h) in updates) island.SetHeight(gx, gz, (int)Math.Round(h));
        return updates.Count;
    }

    // Moves each vertex towards `level` by `strength` x weight; strength 1 with the brush inside the hard core levels flat.
    public static int FlattenTo(IslandFile island, IslandRegion region, double level, double strength = 1)
    {
        var n = 0;
        foreach (var (gx, gz, w) in region.Vertices(island))
        {
            var h = island.HeightAt(gx, gz)!.Value;
            island.SetHeight(gx, gz, (int)Math.Round(h + (level - h) * Math.Clamp(strength, 0, 1) * w));
            n++;
        }
        return n;
    }

    // The weighted mean height of a region (what "level to average" flattens to).
    public static double MeanHeight(IslandFile island, IslandRegion region)
    {
        double sum = 0, weights = 0;
        foreach (var (gx, gz, w) in region.Vertices(island)) { sum += island.HeightAt(gx, gz)!.Value * w; weights += w; }
        return weights == 0 ? 0 : sum / weights;
    }

    // ---- levelling for uneven islands ---------------------------------------------------------------------------------

    // The plane h = A * gx + B * gz + C that best fits (least squares, weighted) the heights of a region.
    public static (double A, double B, double C) FitPlane(IslandFile island, IslandRegion region)
    {
        var pts = region.Vertices(island).ToList();
        if (pts.Count < 3)
        {
            var only = pts.Count == 1 ? island.HeightAt(pts[0].Gx, pts[0].Gz)!.Value : 0;
            return (0, 0, only);
        }
        var (cx, cz) = region.Center;
        // solve the 3x3 normal equations about the centre for a well-conditioned system
        double sw = 0, sx = 0, sz = 0, sxx = 0, sxz = 0, szz = 0, sh = 0, sxh = 0, szh = 0;
        foreach (var (gx, gz, w) in pts)
        {
            var x = gx - cx; var z = gz - cz; double h = island.HeightAt(gx, gz)!.Value;
            sw += w; sx += w * x; sz += w * z; sxx += w * x * x; sxz += w * x * z; szz += w * z * z; sh += w * h; sxh += w * x * h; szh += w * z * h;
        }
        var m = new[,] { { sxx, sxz, sx, sxh }, { sxz, szz, sz, szh }, { sx, sz, sw, sh } };
        for (var col = 0; col < 3; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 3; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-9) return (0, 0, sw == 0 ? 0 : sh / sw);
            if (pivot != col) for (var c = 0; c < 4; c++) (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            for (var r = 0; r < 3; r++)
            {
                if (r == col) continue;
                var f = m[r, col] / m[col, col];
                for (var c = col; c < 4; c++) m[r, c] -= f * m[col, c];
            }
        }
        double a = m[0, 3] / m[0, 0], b = m[1, 3] / m[1, 1], c0 = m[2, 3] / m[2, 2];
        // back to absolute grid coordinates: h = a * (gx - cx) + b * (gz - cz) + c0
        return (a, b, c0 - a * cx - b * cz);
    }

    // Pulls the region onto a plane (blending by weight and strength). Passing the fitted plane with `flatten` = 0 keeps
    // the slope of the ground and removes only the bumps; `flatten` = 1 makes it horizontal at the plane's mean height.
    public static int LevelToPlane(IslandFile island, IslandRegion region, (double A, double B, double C) plane, double strength = 1, double flatten = 0)
    {
        var (cx, cz) = region.Center;
        var meanLevel = plane.A * cx + plane.B * cz + plane.C;
        var n = 0;
        foreach (var (gx, gz, w) in region.Vertices(island).ToList())
        {
            var slope = plane.A * gx + plane.B * gz + plane.C;
            var target = slope + (meanLevel - slope) * Math.Clamp(flatten, 0, 1);
            var h = island.HeightAt(gx, gz)!.Value;
            island.SetHeight(gx, gz, (int)Math.Round(h + (target - h) * Math.Clamp(strength, 0, 1) * w));
            n++;
        }
        return n;
    }

    // A ramp from (gx0, gz0) at height h0 to (gx1, gz1) at h1, `halfWidth` vertices either side of the line (full weight) with a
    // `feather` fall-off beyond. Vertices past the ends are not touched.
    public static int Ramp(IslandFile island, (double Gx, double Gz, double H) from, (double Gx, double Gz, double H) to, double halfWidth, double feather = 3, double strength = 1)
    {
        var dx = to.Gx - from.Gx; var dz = to.Gz - from.Gz;
        var length2 = dx * dx + dz * dz;
        if (length2 < 1e-9) return 0;
        var reach = halfWidth + feather;
        var n = 0;
        var x0 = (int)Math.Floor(Math.Min(from.Gx, to.Gx) - reach); var x1 = (int)Math.Ceiling(Math.Max(from.Gx, to.Gx) + reach);
        var z0 = (int)Math.Floor(Math.Min(from.Gz, to.Gz) - reach); var z1 = (int)Math.Ceiling(Math.Max(from.Gz, to.Gz) + reach);
        for (var z = z0; z <= z1; z++)
        for (var x = x0; x <= x1; x++)
        {
            if (!island.HasVertex(x, z)) continue;
            var t = ((x - from.Gx) * dx + (z - from.Gz) * dz) / length2;
            if (t < 0 || t > 1) continue;
            var px = from.Gx + dx * t; var pz = from.Gz + dz * t;
            var d = Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
            var w = d <= halfWidth ? 1 : feather <= 0 ? 0 : IslandRegion.Falloff(d - halfWidth, feather, 0);
            if (w <= 0) continue;
            var target = from.H + (to.H - from.H) * t;
            var h = island.HeightAt(x, z)!.Value;
            island.SetHeight(x, z, (int)Math.Round(h + (target - h) * Math.Clamp(strength, 0, 1) * w));
            n++;
        }
        return n;
    }

    // Snaps heights to multiples of `step` (terraces), blended by weight.
    public static int Terrace(IslandFile island, IslandRegion region, double step, double strength = 1)
    {
        if (step < 1) return 0;
        var n = 0;
        foreach (var (gx, gz, w) in region.Vertices(island))
        {
            var h = island.HeightAt(gx, gz)!.Value;
            var target = Math.Round(h / step) * step;
            island.SetHeight(gx, gz, (int)Math.Round(h + (target - h) * Math.Clamp(strength, 0, 1) * w));
            n++;
        }
        return n;
    }

    // Scales the relief about the region's mean height (factor < 1 flattens, > 1 exaggerates).
    public static int ScaleRelief(IslandFile island, IslandRegion region, double factor)
    {
        var mean = MeanHeight(island, region);
        var n = 0;
        foreach (var (gx, gz, w) in region.Vertices(island).ToList())
        {
            var h = island.HeightAt(gx, gz)!.Value;
            island.SetHeight(gx, gz, (int)Math.Round(h + ((mean + (h - mean) * factor) - h) * w));
            n++;
        }
        return n;
    }

    // Clamps heights into [min, max].
    public static int Clamp(IslandFile island, IslandRegion region, int min, int max)
    {
        var n = 0;
        foreach (var (gx, gz, w) in region.Vertices(island).ToList())
        {
            var h = island.HeightAt(gx, gz)!.Value;
            var target = Math.Clamp(h, min, max);
            if (target == h) continue;
            island.SetHeight(gx, gz, (int)Math.Round(h + (target - h) * w));
            n++;
        }
        return n;
    }

    // Makes the vertices along the shared border of two cubes agree (heights and light take the first owner's value) -- for
    // islands whose neighbouring cubes were edited by hand.
    public static int WeldBorders(IslandFile island)
    {
        var fixedCount = 0;
        for (var gz = 0; gz <= IslandFile.GridSize; gz++)
        for (var gx = 0; gx <= IslandFile.GridSize; gx++)
        {
            var owners = island.Owners(gx, gz).ToList();
            if (owners.Count < 2) continue;
            var (first, fx, fz) = owners[0];
            var h = first.Height(fx, fz);
            var l = first.Intensity[fz * IslandCube.Vertices + fx];
            var changed = false;
            foreach (var (cube, x, z) in owners.Skip(1))
            {
                if (cube.Height(x, z) != h) { cube.Heights[z * IslandCube.Vertices + x] = h; changed = true; }
                if (cube.HasIntensity && cube.Intensity[z * IslandCube.Vertices + x] != l) { cube.Intensity[z * IslandCube.Vertices + x] = l; changed = true; }
            }
            if (changed) fixedCount++;
        }
        return fixedCount;
    }

    // ---- objects follow the ground ----------------------------------------------------------------------------------

    // Decors keep their height above the ground when the ground under them moves: call with the altitude of each decor taken
    // before the edit (BeforeEdit) and after it (AfterEdit).
    public sealed class DecorFollow
    {
        private readonly IslandFile island;
        private readonly List<(IslandCube Cube, IslandDecor Decor, double[] Before)> tracked = new();

        public DecorFollow(IslandFile island)
        {
            this.island = island;
            foreach (var (cx, cz, cube) in CubeCells(island))
                foreach (var decor in cube.Decors)
                    if (Altitude(island, cx * IslandFile.CubeSize + decor.X, cz * IslandFile.CubeSize + decor.Z) is { } y) tracked.Add((cube, decor, new[] { y }));
        }

        // Moves the tracked decors by the change in ground height under them; returns how many moved.
        public int Apply()
        {
            var moved = 0;
            foreach (var (cube, decor, before) in tracked)
            {
                var cell = island.CellsOf(cube.Id).FirstOrDefault();
                if (Altitude(island, cell.X * IslandFile.CubeSize + decor.X, cell.Z * IslandFile.CubeSize + decor.Z) is not { } after) continue;
                var delta = (int)Math.Round(after - before[0]);
                if (delta == 0) continue;
                before[0] += delta;      // the baseline moves with the object, so Apply can be called again during a stroke
                decor.MoveTo(decor.X, decor.Y + delta, decor.Z);
                moved++;
            }
            return moved;
        }
    }

    // Each distinct cube once, with the map cell it is first shown at.
    public static IEnumerable<(int X, int Z, IslandCube Cube)> CubeCells(IslandFile island)
    {
        var seen = new HashSet<int>();
        for (var z = 0; z < IslandFile.MapSize; z++)
        for (var x = 0; x < IslandFile.MapSize; x++)
            if (island.CubeAt(x, z) is { } cube && seen.Add(cube.Id)) yield return (x, z, cube);
    }
}
