using System.Buffers.Binary;

namespace LBA2LevelEditor.Lba1;

// Reads an LBA_GRI scene entry into cells (no drawing, so tools without WPF can use it).
//   64*64 U16 column offsets (index x + z*64), then the columns, then a 32-byte bitmap of the blocks in use.
internal static class Lba1GridCodec
{
    public const int SizeX = 64, SizeY = 25, SizeZ = 64;

    // Column data is stored per (x, z); the result is [z][x][y] pairs of (block, position in block).
    public static byte[] Decode(byte[] grid)
    {
        var cells = new byte[SizeZ * SizeX * SizeY * 2];
        for (var z = 0; z < SizeZ; z++)
        for (var x = 0; x < SizeX; x++)
        {
            var src = BinaryPrimitives.ReadUInt16LittleEndian(grid.AsSpan((x + z * SizeZ) * 2));
            var dst = (z * SizeX + x) * SizeY * 2;
            var end = dst + SizeY * 2;
            if (src >= grid.Length) continue;
            int runs = grid[src++];
            while (runs-- > 0 && src < grid.Length && dst < end)
            {
                var op = grid[src++];
                var count = (op & 0x3F) + 1;
                switch (op >> 6)
                {
                    case 0:
                        dst += count * 2;
                        break;
                    case 1:
                        for (var k = 0; k < count && dst < end && src + 1 < grid.Length; k++)
                        {
                            cells[dst++] = grid[src++];
                            cells[dst++] = grid[src++];
                        }
                        break;
                    default:
                        if (src + 1 >= grid.Length) return cells;
                        var block = grid[src++];
                        var pos = grid[src++];
                        for (var k = 0; k < count && dst < end; k++)
                        {
                            cells[dst++] = block;
                            cells[dst++] = pos;
                        }
                        break;
                }
            }
        }
        return cells;
    }

    // How many cells a column's runs add up to (the engine needs exactly SizeY), or -1 if the runs run off the entry.
    public static int ColumnCells(byte[] grid, int x, int z)
    {
        int src = BinaryPrimitives.ReadUInt16LittleEndian(grid.AsSpan((x + z * SizeZ) * 2));
        if (src >= grid.Length) return -1;
        int runs = grid[src++];
        var cells = 0;
        while (runs-- > 0)
        {
            if (src >= grid.Length) return -1;
            var op = grid[src++];
            var count = (op & 0x3F) + 1;
            cells += count;
            switch (op >> 6)
            {
                case 0: break;
                case 1: src += 2 * count; break;
                default: src += 2; break;
            }
            if (src > grid.Length) return -1;
        }
        return cells;
    }
}
