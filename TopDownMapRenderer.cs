using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LBA2LevelEditor;

// Renders a full-resolution orthographic top-down map of an island by
// rasterizing every ground triangle with the same per-pixel barycentric
// texture/light interpolation SoftwareTerrainRenderer uses for the 3D view
// (just without a camera/perspective divide -- X,Z map straight to pixel
// coordinates), instead of the coarse one-pixel-per-cube presence grid
// IslandDocument.CreatePreview() draws, or a single flat-averaged color per
// terrain cell. Meant to be shown scaled down (e.g. in the minimap) or at
// full size, similar to the game's holomap art.
internal static class TopDownMapRenderer
{
    private const int CubesPerSide = 16;
    public const int CellsPerCube = 64;

    // 4 pixels per terrain cell -> 4096x4096 for a full 16x16-cube island.
    // Each cell's two triangles still only cover a handful of pixels each at
    // this scale, so per-pixel rasterization stays cheap even at this size.
    public static WriteableBitmap Render(IslandDocument island, int pixelsPerCell = 4)
    {
        pixelsPerCell = Math.Max(1, pixelsPerCell);
        var size = CubesPerSide * CellsPerCube * pixelsPerCell;
        var pixels = new byte[size * size * 4];

        // Background for cubes with no data (open sea / unmapped).
        for (var i = 0; i < size * size; i++)
        {
            pixels[i * 4] = 40;
            pixels[i * 4 + 1] = 34;
            pixels[i * 4 + 2] = 26;
            pixels[i * 4 + 3] = 255;
        }

        for (var cubeY = 0; cubeY < CubesPerSide; cubeY++)
        for (var cubeX = 0; cubeX < CubesPerSide; cubeX++)
        {
            var cubeId = island.CubeAt(cubeX, cubeY) & 0x7F;
            if (cubeId == 0) continue;
            for (var z = 0; z < CellsPerCube; z++)
            for (var x = 0; x < CellsPerCube; x++)
            {
                var first = island.PolygonAt(cubeId, x, z);
                var second = island.PolygonAt(cubeId, x + CellsPerCube, z);
                var firstCorners = ((first >> 16) & 1) == 0 ? Corners0 : Corners1;
                var secondCorners = ((second >> 16) & 1) == 0 ? Corners2 : Corners3;
                RasterCellTriangle(pixels, size, island, cubeId, cubeX, cubeY, x, z, pixelsPerCell, first, firstCorners);
                RasterCellTriangle(pixels, size, island, cubeId, cubeX, cubeY, x, z, pixelsPerCell, second, secondCorners);
            }
        }

        Smooth(pixels, size);

        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static readonly int[] Corners0 = { 0, 1, 2 };
    private static readonly int[] Corners1 = { 3, 0, 1 };
    private static readonly int[] Corners2 = { 2, 3, 0 };
    private static readonly int[] Corners3 = { 1, 2, 3 };
    private static readonly (int X, int Z)[] LocalOffsets = { (0, 0), (0, 1), (1, 1), (1, 0) };

    private readonly record struct Vertex(double X, double Z, double U, double V, byte Light);

    private static void RasterCellTriangle(byte[] pixels, int size, IslandDocument island, int cubeId, int cubeX, int cubeY, int cellX, int cellZ, int pixelsPerCell, uint polygon, int[] corners)
    {
        var texture = island.TextureAt(cubeId, (int)((polygon >> 19) & 0x1FFF));
        var textured = ((polygon >> 4) & 3) != 0 && texture is not null;

        Span<Vertex> verts = stackalloc Vertex[3];
        for (var i = 0; i < 3; i++)
        {
            var corner = corners[i];
            var lx = cellX + LocalOffsets[corner].X;
            var lz = cellZ + LocalOffsets[corner].Z;
            var px = (cubeX * CellsPerCube + lx) * (double)pixelsPerCell;
            var pz = (cubeY * CellsPerCube + lz) * (double)pixelsPerCell;
            var light = island.IntensityAt(cubeId, lx, lz);
            double u = 0, v = 0;
            if (textured) { u = texture![i * 2] / 256.0; v = texture[i * 2 + 1] / 256.0; }
            verts[i] = new Vertex(px, pz, u, v, light);
        }

        var minX = Math.Max(0, (int)Math.Floor(Math.Min(verts[0].X, Math.Min(verts[1].X, verts[2].X))));
        var maxX = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(verts[0].X, Math.Max(verts[1].X, verts[2].X))));
        var minZ = Math.Max(0, (int)Math.Floor(Math.Min(verts[0].Z, Math.Min(verts[1].Z, verts[2].Z))));
        var maxZ = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(verts[0].Z, Math.Max(verts[1].Z, verts[2].Z))));
        var area = Edge(verts[0], verts[1], verts[2].X, verts[2].Z);
        if (Math.Abs(area) < 1e-6) return;

        for (var pz = minZ; pz <= maxZ; pz++)
        for (var px = minX; px <= maxX; px++)
        {
            var sampleX = px + 0.5;
            var sampleZ = pz + 0.5;
            var w0 = Edge(verts[1], verts[2], sampleX, sampleZ) / area;
            var w1 = Edge(verts[2], verts[0], sampleX, sampleZ) / area;
            var w2 = 1 - w0 - w1;
            if (w0 < 0 || w1 < 0 || w2 < 0) continue;

            var light = (int)Math.Clamp(Math.Round(verts[0].Light * w0 + verts[1].Light * w1 + verts[2].Light * w2), 0, 15);
            Color color;
            if (textured)
            {
                var u = verts[0].U * w0 + verts[1].U * w1 + verts[2].U * w2;
                var v = verts[0].V * w0 + verts[1].V * w1 + verts[2].V * w2;
                color = island.ColorAtSmooth(u, v, light);
            }
            else
            {
                color = FlatColor((int)(polygon & 15), light);
            }

            var offset = (pz * size + px) * 4;
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
            pixels[offset + 3] = 255;
        }
    }

    private static double Edge(Vertex a, Vertex b, double x, double z) => (x - a.X) * (b.Z - a.Z) - (z - a.Z) * (b.X - a.X);

    // The ground texture atlas is genuinely grainy 1997-era art (rock/gravel
    // in particular has real per-texel light/dark variance, not just
    // dithering) -- sampled at this resolution, exact per-pixel colors read
    // as visual noise rather than a recognizable texture the way it does at
    // the 3D view's native scale where distance and motion blur it. A mild
    // 3x3 box blur (no bigger; it would start eating cube/decor edges)
    // trades a little sharpness for something that actually reads as
    // terrain from minimap distance, the same tradeoff a satellite-image
    // renderer makes by mip-filtering instead of point-sampling.
    private static void Smooth(byte[] pixels, int size)
    {
        var source = (byte[])pixels.Clone();
        for (var y = 0; y < size; y++)
        {
            var y0 = Math.Max(0, y - 1);
            var y1 = Math.Min(size - 1, y + 1);
            for (var x = 0; x < size; x++)
            {
                var x0 = Math.Max(0, x - 1);
                var x1 = Math.Min(size - 1, x + 1);
                int b = 0, g = 0, r = 0;
                for (var sy = y0; sy <= y1; sy++)
                for (var sx = x0; sx <= x1; sx++)
                {
                    var o = (sy * size + sx) * 4;
                    b += source[o]; g += source[o + 1]; r += source[o + 2];
                }
                var count = (y1 - y0 + 1) * (x1 - x0 + 1);
                var offset = (y * size + x) * 4;
                pixels[offset] = (byte)(b / count);
                pixels[offset + 1] = (byte)(g / count);
                pixels[offset + 2] = (byte)(r / count);
            }
        }
    }

    private static Color FlatColor(int bank, int light) => Color.FromRgb(
        (byte)Math.Clamp(70 + bank * 10 + light * 5, 0, 255),
        (byte)Math.Clamp(95 + bank * 7 + light * 6, 0, 255),
        (byte)Math.Clamp(55 + bank * 4 + light * 3, 0, 255));
}
