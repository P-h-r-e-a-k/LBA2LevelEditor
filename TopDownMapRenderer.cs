using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LBA2LevelEditor;

// Renders a full-resolution orthographic top-down map of an island, sampling
// the same per-corner texture/light data the 3D renderer uses (see
// SoftwareTerrainRenderer) instead of the coarse one-pixel-per-cube presence
// grid IslandDocument.CreatePreview() draws. Meant to be shown scaled down
// (e.g. in the minimap) or at full size, similar to the game's holomap art.
internal static class TopDownMapRenderer
{
    private const int CubesPerSide = 16;
    private const int CellsPerCube = 64;

    // 2 pixels per terrain cell -> 2048x2048 for a full 16x16-cube island.
    // High enough detail to hold up scaled down or viewed large; low enough
    // to regenerate in well under a second even on a dense island.
    public static WriteableBitmap Render(IslandDocument island, int pixelsPerCell = 2)
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
                var color = BlendCellColor(island, cubeId, x, z, first, second);
                FillCell(pixels, size, cubeX, cubeY, x, z, pixelsPerCell, color);
            }
        }

        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void FillCell(byte[] pixels, int size, int cubeX, int cubeY, int cellX, int cellZ, int pixelsPerCell, Color color)
    {
        var startX = (cubeX * CellsPerCube + cellX) * pixelsPerCell;
        var startZ = (cubeY * CellsPerCube + cellZ) * pixelsPerCell;
        for (var dz = 0; dz < pixelsPerCell; dz++)
        {
            var rowOffset = ((startZ + dz) * size + startX) * 4;
            for (var dx = 0; dx < pixelsPerCell; dx++)
            {
                var offset = rowOffset + dx * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 255;
            }
        }
    }

    // A cell is a 512x512-unit quad split into two triangles (matches the
    // ground polygon layout SoftwareTerrainRenderer walks); average both
    // triangles' corner colors into one flat fill for the cell.
    private static Color BlendCellColor(IslandDocument island, int cubeId, int cellX, int cellZ, uint first, uint second)
    {
        var firstCorners = ((first >> 16) & 1) == 0 ? Corners0 : Corners1;
        var secondCorners = ((second >> 16) & 1) == 0 ? Corners2 : Corners3;
        var a = TriangleColor(island, cubeId, cellX, cellZ, first, firstCorners);
        var b = TriangleColor(island, cubeId, cellX, cellZ, second, secondCorners);
        return Color.FromRgb((byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));
    }

    private static readonly int[] Corners0 = { 0, 1, 2 };
    private static readonly int[] Corners1 = { 3, 0, 1 };
    private static readonly int[] Corners2 = { 2, 3, 0 };
    private static readonly int[] Corners3 = { 1, 2, 3 };
    private static readonly (int X, int Z)[] LocalOffsets = { (0, 0), (0, 1), (1, 1), (1, 0) };

    private static Color TriangleColor(IslandDocument island, int cubeId, int cellX, int cellZ, uint polygon, int[] corners)
    {
        var texture = island.TextureAt(cubeId, (int)((polygon >> 19) & 0x1FFF));
        var textured = ((polygon >> 4) & 3) != 0 && texture is not null;
        int rSum = 0, gSum = 0, bSum = 0;
        for (var i = 0; i < 3; i++)
        {
            var corner = corners[i];
            var x = cellX + LocalOffsets[corner].X;
            var z = cellZ + LocalOffsets[corner].Z;
            var light = island.IntensityAt(cubeId, x, z);
            var color = textured
                ? island.ColorAt(texture![i * 2] / 256.0, texture[i * 2 + 1] / 256.0, light)
                : FlatColor((int)(polygon & 15), light);
            rSum += color.R;
            gSum += color.G;
            bSum += color.B;
        }
        return Color.FromRgb((byte)(rSum / 3), (byte)(gSum / 3), (byte)(bSum / 3));
    }

    private static Color FlatColor(int bank, int light) => Color.FromRgb(
        (byte)Math.Clamp(70 + bank * 10 + light * 5, 0, 255),
        (byte)Math.Clamp(95 + bank * 7 + light * 6, 0, 255),
        (byte)Math.Clamp(55 + bank * 4 + light * 3, 0, 255));
}
