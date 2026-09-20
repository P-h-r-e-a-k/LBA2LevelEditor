using System.IO;

namespace LBA2LevelEditor.Terrain;

internal enum MapView { Terrain, Height, Light, Shadows, GameCode, WaterDepth }

// The island seen from above as a BGRA byte buffer (WPF-free): the textured terrain as the engine would light it, or one of
// the analysis views the editing tools work with (height, baked light, where the baked light departs from a plain lambert
// bake = the shadows, the game codes, the water depth). Renders any rectangle of cells, so a brush stroke redraws only what
// it touched.
internal sealed class IslandMapRenderer
{
    private readonly IslandFile island;
    private readonly byte[] palette;
    private readonly bool sixBit;

    public int Scale { get; }
    // The map covers cells [OriginX, OriginX + CellsX) x [OriginZ, OriginZ + CellsZ) of the island.
    public int OriginX { get; }
    public int OriginZ { get; }
    public int CellsX { get; }
    public int CellsZ { get; }
    public int PixelWidth => CellsX * Scale;
    public int PixelHeight => CellsZ * Scale;
    public byte[] Pixels { get; }
    public double HeightMin { get; set; }
    public double HeightMax { get; set; } = 1;
    // Where the baked light is compared with (Shadows view).
    public BakeOptions? Baseline { get; set; }

    public IslandMapRenderer(IslandFile island, byte[] palette, int scale)
    {
        this.island = island;
        this.palette = palette.Length >= 768 ? palette : new byte[768];
        sixBit = this.palette.Take(768).Max() <= 63;
        Scale = Math.Max(1, scale);
        var (minX, minZ, maxX, maxZ) = island.PresentBounds();
        OriginX = minX * IslandCube.Cells; OriginZ = minZ * IslandCube.Cells;
        CellsX = (maxX - minX + 1) * IslandCube.Cells; CellsZ = (maxZ - minZ + 1) * IslandCube.Cells;
        Pixels = new byte[PixelWidth * PixelHeight * 4];
        var (lo, hi) = IslandOps.HeightRange(island);
        HeightMin = lo; HeightMax = Math.Max(lo + 1, hi);
    }

    // The palette of an island as the game picks it: RESS.HQR entry per island, 768 bytes at the offset its header gives.
    public static byte[] LoadPalette(string gameDirectory, string islandName)
    {
        var index = islandName.ToUpperInvariant() switch
        {
            "CITABAU" => 42, "DESERT" => 29, "EMERAUDE" => 30, "OTRINGAL" => 31, "CELEBRAT" or "CELEBRA2" => 32, "PLATFORM" => 33,
            "MOSQUIBE" => 34, "KNARTAS" => 35, "ILOTCX" => 36, "ASCENCE" => 37, _ => 27,
        };
        try
        {
            var xpl = HqrArchive.Open(Path.Combine(gameDirectory, "RESS.HQR")).Read(index);
            var offset = BitConverter.ToInt32(xpl, 4);
            if (offset >= 0 && offset <= xpl.Length - 768) return xpl[offset..(offset + 768)];
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { }
        return new byte[768];
    }

    private static readonly int[][] Corners = { new[] { 0, 1, 2 }, new[] { 2, 3, 0 }, new[] { 3, 0, 1 }, new[] { 1, 2, 3 } };
    // corner k of a cell: (x, z) offsets
    private static readonly (int X, int Z)[] Offsets = { (0, 0), (0, 1), (1, 1), (1, 0) };

    public void Render(MapView view, int cellX0, int cellZ0, int cellX1, int cellZ1)
    {
        cellX0 = Math.Max(cellX0, OriginX); cellZ0 = Math.Max(cellZ0, OriginZ);
        cellX1 = Math.Min(cellX1, OriginX + CellsX - 1); cellZ1 = Math.Min(cellZ1, OriginZ + CellsZ - 1);
        for (var cz = cellZ0; cz <= cellZ1; cz++)
        for (var cx = cellX0; cx <= cellX1; cx++)
            RenderCell(view, cx, cz);
    }

    public void RenderAll(MapView view) => Render(view, OriginX, OriginZ, OriginX + CellsX - 1, OriginZ + CellsZ - 1);

    private void Put(int px, int pz, byte r, byte g, byte b)
    {
        var o = (pz * PixelWidth + px) * 4;
        Pixels[o] = b; Pixels[o + 1] = g; Pixels[o + 2] = r; Pixels[o + 3] = 255;
    }

    private void RenderCell(MapView view, int cx, int cz)
    {
        var px0 = (cx - OriginX) * Scale; var pz0 = (cz - OriginZ) * Scale;
        var cube = island.CubeAt(cx / IslandCube.Cells, cz / IslandCube.Cells);
        if (cube is null || cx >= IslandFile.GridSize || cz >= IslandFile.GridSize)
        {
            for (var z = 0; z < Scale; z++) for (var x = 0; x < Scale; x++) Put(px0 + x, pz0 + z, 22, 30, 34);
            return;
        }
        var lx = cx % IslandCube.Cells; var lz = cz % IslandCube.Cells;
        var height = new double[4]; var light = new double[4]; var aux = new double[4];
        for (var k = 0; k < 4; k++)
        {
            var (ox, oz) = Offsets[k];
            var gx = cx + ox; var gz = cz + oz;
            height[k] = island.HeightAt(gx, gz) ?? 0;
            light[k] = island.LightAt(gx, gz) ?? 15;
            aux[k] = view switch
            {
                MapView.Height => Hillshade(gx, gz),
                MapView.Shadows => light[k] - BaselineLight(gx, gz),
                MapView.WaterDepth => IslandLightOps.WaterDepthAt(island, gx, gz),
                _ => 0,
            };
        }
        var poly0 = new IslandPolygon(cube.HasPolygons ? cube.Polygon(lx, lz, 0) : 0);
        var poly1 = new IslandPolygon(cube.HasPolygons ? cube.Polygon(lx, lz, 1) : 0);
        var diagonal = poly0.Diagonal;

        for (var z = 0; z < Scale; z++)
        for (var x = 0; x < Scale; x++)
        {
            double u = (x + 0.5) / Scale, v = (z + 0.5) / Scale;   // position in the cell, x right, z down
            switch (view)
            {
                case MapView.Terrain:
                {
                    var half = diagonal ? (u + v < 1 ? 0 : 1) : (u < v ? 0 : 1);
                    var poly = half == 0 ? poly0 : poly1;
                    var corners = Corners[(diagonal ? 2 : 0) + half];
                    var (r, g, b) = TerrainColor(cube, poly, corners, light, u, v);
                    Put(px0 + x, pz0 + z, r, g, b);
                    break;
                }
                case MapView.Height:
                {
                    var h = Bilinear(height, u, v); var s = Bilinear(aux, u, v);
                    var (r, g, b) = HeightColor((h - HeightMin) / (HeightMax - HeightMin));
                    var shade = 0.55 + 0.75 * s;
                    Put(px0 + x, pz0 + z, Clamp8(r * shade), Clamp8(g * shade), Clamp8(b * shade));
                    break;
                }
                case MapView.Light:
                {
                    var l = Bilinear(light, u, v);
                    var c = Clamp8(l * 17);
                    Put(px0 + x, pz0 + z, c, c, c);
                    break;
                }
                case MapView.Shadows:
                {
                    var d = Bilinear(aux, u, v);
                    var l = Bilinear(light, u, v);
                    var baseGray = 40 + l * 6;
                    if (d <= -0.75) { var t = Math.Clamp(-d / 6, 0, 1); Put(px0 + x, pz0 + z, Clamp8(baseGray * (1 - t) + 150 * t), Clamp8(baseGray * (1 - t) + 40 * t), Clamp8(baseGray * (1 - t) + 210 * t)); }
                    else if (d >= 0.75) { var t = Math.Clamp(d / 6, 0, 1); Put(px0 + x, pz0 + z, Clamp8(baseGray * (1 - t) + 240 * t), Clamp8(baseGray * (1 - t) + 210 * t), Clamp8(baseGray * (1 - t) + 70 * t)); }
                    else Put(px0 + x, pz0 + z, Clamp8(baseGray), Clamp8(baseGray), Clamp8(baseGray));
                    break;
                }
                case MapView.GameCode:
                {
                    var half = diagonal ? (u + v < 1 ? 0 : 1) : (u < v ? 0 : 1);
                    var code = (half == 0 ? poly0 : poly1).CodeJeu;
                    var (r, g, b) = GameCodeColor(code);
                    var shade = 0.7 + 0.3 * Bilinear(light, u, v) / 15;
                    Put(px0 + x, pz0 + z, Clamp8(r * shade), Clamp8(g * shade), Clamp8(b * shade));
                    break;
                }
                default:
                {
                    var d = Bilinear(aux, u, v);
                    var t = Math.Clamp(d / 15, 0, 1);
                    Put(px0 + x, pz0 + z, Clamp8(30 + 20 * (1 - t)), Clamp8(50 + 60 * (1 - t)), Clamp8(70 + 185 * t));
                    break;
                }
            }
        }
    }

    private double BaselineLight(int gx, int gz)
    {
        var options = Baseline ?? BakeOptions.For(island.Cubes.Values.First());
        var lambert = IslandLight.Lambert(Normal(gx, gz), options.Azimuth, options.Elevation);
        return Math.Clamp(options.Offset + options.Gain * lambert, 0, 15);
    }

    private double Hillshade(int gx, int gz)
    {
        var options = Baseline ?? BakeOptions.For(island.Cubes.Values.First());
        return IslandLight.Lambert(Normal(gx, gz), options.Azimuth, options.Elevation);
    }

    private (double X, double Y, double Z) Normal(int gx, int gz)
    {
        double H(int x, int z) => island.HeightAt(x, z) ?? island.HeightAt(gx, gz) ?? 0;
        var dhx = (H(gx + 1, gz) - H(gx - 1, gz)) / (2.0 * IslandFile.CellSize);
        var dhz = (H(gx, gz + 1) - H(gx, gz - 1)) / (2.0 * IslandFile.CellSize);
        var len = Math.Sqrt(dhx * dhx + dhz * dhz + 1);
        return (-dhx / len, 1 / len, -dhz / len);
    }

    private static double Bilinear(double[] c, double u, double v)
    {
        // corners: 0 (0,0), 1 (0,1), 2 (1,1), 3 (1,0) as (x, z)
        var top = c[0] * (1 - u) + c[3] * u;
        var bottom = c[1] * (1 - u) + c[2] * u;
        return top * (1 - v) + bottom * v;
    }

    private (byte R, byte G, byte B) TerrainColor(IslandCube cube, IslandPolygon poly, int[] corners, double[] light, double u, double v)
    {
        // barycentric position inside the triangle
        var (x0, z0) = Offsets[corners[0]]; var (x1, z1) = Offsets[corners[1]]; var (x2, z2) = Offsets[corners[2]];
        var det = (double)((z1 - z2) * (x0 - x2) + (x2 - x1) * (z0 - z2));
        var w0 = det == 0 ? 1 : ((z1 - z2) * (u - x2) + (x2 - x1) * (v - z2)) / det;
        var w1 = det == 0 ? 0 : ((z2 - z0) * (u - x2) + (x0 - x2) * (v - z2)) / det;
        var w2 = 1 - w0 - w1;
        var l = Math.Clamp(light[corners[0]] * w0 + light[corners[1]] * w1 + light[corners[2]] * w2, 0, 15);
        var factor = 0.48 + l / 15.0 * 0.72;
        var index = poly.TextureIndex;
        if (poly.TexFlag != 0 && index * 6 + 6 <= cube.TextureDefs.Length)
        {
            var t = cube.TextureDefs.AsSpan(index * 6, 6);
            var tu = (t[0] * w0 + t[2] * w1 + t[4] * w2) / 256.0; var tv = (t[1] * w0 + t[3] * w1 + t[5] * w2) / 256.0;
            var tx = Math.Clamp((int)Math.Round(tu), 0, 255); var ty = Math.Clamp((int)Math.Round(tv), 0, 255);
            return Shade(island.GroundTexture[ty * 256 + tx], factor);
        }
        if (poly.PolyFlag == 0) return ((byte)(28 * factor + 10), (byte)(70 * factor + 12), (byte)(120 * factor + 20));   // nothing drawn: the sea shows through
        return Shade((poly.Bank << 4) + 11, factor);
    }

    private (byte, byte, byte) Shade(int paletteIndex, double factor)
    {
        var i = paletteIndex * 3;
        byte S(byte value) => Clamp8((sixBit ? value * 4 : value) * factor);
        return (S(palette[i]), S(palette[i + 1]), S(palette[i + 2]));
    }

    private static byte Clamp8(double value) => (byte)Math.Clamp(value, 0, 255);

    private static (double R, double G, double B) HeightColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        (double R, double G, double B)[] stops = { (20, 50, 130), (40, 130, 90), (170, 190, 80), (170, 120, 60), (245, 245, 245) };
        var scaled = t * (stops.Length - 1);
        var i = Math.Min((int)scaled, stops.Length - 2);
        var f = scaled - i;
        return (stops[i].R + (stops[i + 1].R - stops[i].R) * f, stops[i].G + (stops[i + 1].G - stops[i].G) * f, stops[i].B + (stops[i + 1].B - stops[i].B) * f);
    }

    public static (double R, double G, double B) GameCodeColor(int code) => code switch
    {
        0 => (70, 78, 70),
        1 => (40, 90, 200),
        2 => (230, 230, 60),
        3 or 4 or 5 or 6 => (200, 130, 60),
        8 => (200, 40, 160),
        9 or 13 => (230, 70, 30),
        11 or 14 => (150, 200, 90),
        12 => (90, 170, 220),
        _ => (150, 150, 150),
    };
}
