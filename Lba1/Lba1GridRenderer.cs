using System.Buffers.Binary;
using System.Windows;

namespace LBA2LevelEditor.Lba1;

// A scene's whole isometric map as one BGRA bitmap plus the mapping from world coordinates.
// Canvas position of a world point (X, Y, Z): see Project.
internal sealed class Lba1SceneImage
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int OriginX { get; init; }
    public required int OriginY { get; init; }

    // Same isometric projection as the LBA2 interior view: a brick is 48 px wide (24 per
    // cell step in X or Z), a floor cell 12 px deep, and a layer 15 px tall.
    public Point Project(double x, double y, double z)
        => new((x - z) * 24 / 512 + OriginX, (x + z) * 12 / 512 - y * 15 / 256 + OriginY);
}

// One brick of a grid: cell (X, Y, Z) shows brick number Brick (an index into LBA_BRK).
internal readonly record struct Lba1Placement(int X, int Y, int Z, int Brick);

// A scene grid placed in a shared map: its cell (0, 0, 0) sits at cell (OffsetX, OffsetY, OffsetZ).
internal sealed record Lba1Tile(IReadOnlyList<Lba1Placement> Cells, int OffsetX, int OffsetY, int OffsetZ);

// Draws LBA1 grids the way GRILLE.CPP's AffGrille does: cells far to near
// (z, then x, then y), each cell's brick blitted at its isometric anchor.
//   grid  (LBA_GRI): 64*64 U16 column offsets (index x + z*64), then run-length columns
//   block (LBA_BLL): U32 offsets; per block 3 size bytes then dx*dy*dz 4-byte entries
//                    (shape, sound, U16 brick+1)
//   brick (LBA_BRK): deltaX, lines, hotX, hotY, then per line a run list
internal static class Lba1GridRenderer
{
    public const int SizeX = 64, SizeY = 25, SizeZ = 64;

    public static Lba1SceneImage Render(byte[] grid, byte[] blocks, Func<int, byte[]?> brick, byte[] palette)
        => Render(new[] { new Lba1Tile(Placements(grid, blocks), 0, 0, 0) }, brick, palette);

    // Every drawn brick of a grid, in painter order.
    public static List<Lba1Placement> Placements(byte[] grid, byte[] blocks) => PlacementsOfCells(DecodeGrid(grid), blocks);

    // The same from cells already decoded (a map a run has changed with grid fragments).
    public static List<Lba1Placement> PlacementsOfCells(byte[] cells, byte[] blocks)
    {
        var placements = new List<Lba1Placement>(32768);
        var blockCount = (int)(BinaryPrimitives.ReadUInt32LittleEndian(blocks) / 4);

        for (var z = 0; z < SizeZ; z++)
        for (var x = 0; x < SizeX; x++)
        for (var y = 0; y < SizeY; y++)
        {
            var i = ((z * SizeX + x) * SizeY + y) * 2;
            int block = cells[i], pos = cells[i + 1];
            if (block == 0 || block > blockCount) continue;
            var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(blocks.AsSpan((block - 1) * 4));
            if (at + 3 > blocks.Length) continue;
            var extent = blocks[at] * blocks[at + 1] * blocks[at + 2];
            if (pos >= extent) continue;
            var entry = at + 3 + pos * 4;
            if (entry + 4 > blocks.Length) continue;
            var number = BinaryPrimitives.ReadUInt16LittleEndian(blocks.AsSpan(entry + 2));
            if (number == 0) continue;
            placements.Add(new Lba1Placement(x, y, z, number - 1));
        }
        return placements;
    }

    // Draws several grids into one image, sorting every brick together so bricks of one tile
    // correctly cover those of a neighbour.
    public static Lba1SceneImage Render(IReadOnlyList<Lba1Tile> tiles, Func<int, byte[]?> brick, byte[] palette)
    {
        var all = new List<(int X, int Y, int Z, int Brick)>();
        foreach (var tile in tiles)
            foreach (var c in tile.Cells) all.Add((c.X + tile.OffsetX, c.Y + tile.OffsetY, c.Z + tile.OffsetZ, c.Brick));
        // Stable: where two tiles share a cell the later tile stays on top.
        var ordered = all.OrderBy(c => c.Z).ThenBy(c => c.X).ThenBy(c => c.Y).ToList();

        var bricks = new Dictionary<int, byte[]?>();
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var p in ordered)
        {
            if (!bricks.TryGetValue(p.Brick, out var data)) bricks[p.Brick] = data = brick(p.Brick);
            if (data is null || data.Length < 4) continue;
            int left = 24 * (p.X - p.Z) + (sbyte)data[2], top = 12 * (p.X + p.Z) - 15 * p.Y + (sbyte)data[3];
            minX = Math.Min(minX, left); minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, left + data[0]); maxY = Math.Max(maxY, top + data[1]);
        }
        if (minX > maxX) { minX = minY = 0; maxX = maxY = 1; }

        const int margin = 16;
        var width = maxX - minX + margin * 2;
        var height = maxY - minY + margin * 2;
        var originX = margin - minX;
        var originY = margin - minY;
        var bgra = new byte[width * height * 4];

        foreach (var p in ordered)
        {
            var data = bricks[p.Brick];
            if (data is null || data.Length < 4) continue;
            Blit(bgra, width, height, data, 24 * (p.X - p.Z) + originX + (sbyte)data[2], 12 * (p.X + p.Z) - 15 * p.Y + originY + (sbyte)data[3], palette);
        }

        return new Lba1SceneImage { Bgra = bgra, Width = width, Height = height, OriginX = originX, OriginY = originY };
    }

    // Column data is stored per (x, z); the result is [z][x][y] pairs of (block, position in block).
    internal static byte[] DecodeGrid(byte[] grid) => Lba1GridCodec.Decode(grid);

    internal static void Blit(byte[] bgra, int width, int height, byte[] data, int x0, int y0, byte[] palette)
    {
        int lines = data[1];
        var src = 4;
        for (var line = 0; line < lines && src < data.Length; line++)
        {
            int runs = data[src++];
            var x = x0;
            var y = y0 + line;
            for (var r = 0; r < runs && src < data.Length; r++)
            {
                var control = data[src++];
                var count = (control & 0x3F) + 1;
                switch (control >> 6)
                {
                    case 0:
                        x += count;
                        break;
                    case 1:
                        for (var k = 0; k < count && src < data.Length; k++) Put(data[src++]);
                        break;
                    default:
                        if (src >= data.Length) return;
                        var color = data[src++];
                        for (var k = 0; k < count; k++) Put(color);
                        break;
                }

                void Put(byte index)
                {
                    if ((uint)x < (uint)width && (uint)y < (uint)height)
                    {
                        var o = (y * width + x) * 4;
                        bgra[o] = palette[index * 3 + 2];
                        bgra[o + 1] = palette[index * 3 + 1];
                        bgra[o + 2] = palette[index * 3];
                        bgra[o + 3] = 255;
                    }
                    x++;
                }
            }
        }
    }
}
