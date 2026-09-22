using System.IO;

namespace LBAAssembler.Lba1;

// One cell of an LBA1 grid: the block used there (1-based index into the scene's block library, 0 = empty)
// and which cell of that block's template this is (block extent dx*dy*dz; see Lba1GridRenderer).
internal readonly record struct Lba1GridCell(int X, int Y, int Z, int Block, int Pos);

// Edits to an LBA_GRI entry. The columns that change are re-encoded and appended to the columns; every other
// column keeps its original bytes, so an edit can't disturb what it doesn't touch. (Columns are 25 cells tall,
// stored as runs: skip empties / literal (block, pos) pairs / one pair repeated -- see Lba1GridRenderer.DecodeGrid.)
//
// The entry ends with a 32-byte bitmap of the blocks the scene uses (bit 7-(i&7) of byte i>>3 for block i). The
// engine reads it from the END of the entry (BufMap + size - 32) to decide which bricks to load, so it has to stay
// the last 32 bytes and has to list every block the grid uses.
internal static class Lba1GridEdit
{
    private const int Size = 64, Height = 25, TableSize = 64 * 64 * 2, UsedBlocksSize = 32;

    public static byte[] SetCells(byte[] grid, IReadOnlyList<Lba1GridCell> edits)
    {
        if (grid.Length < TableSize + UsedBlocksSize) throw new InvalidDataException("The grid entry is too short.");
        var cells = Lba1GridCodec.Decode(grid);
        var columns = new SortedSet<(int Z, int X)>();
        var usedBlocks = grid.AsSpan(grid.Length - UsedBlocksSize).ToArray();
        foreach (var e in edits)
        {
            if ((uint)e.X >= Size || (uint)e.Z >= Size || (uint)e.Y >= Height) throw new ArgumentOutOfRangeException(nameof(edits), $"cell ({e.X}, {e.Y}, {e.Z}) is outside the grid");
            if ((uint)e.Block > 255 || (uint)e.Pos > 255) throw new ArgumentOutOfRangeException(nameof(edits), "block and position are single bytes");
            var i = ((e.Z * Size + e.X) * Height + e.Y) * 2;
            cells[i] = (byte)e.Block;
            // for an empty cell the second byte is not a block position but the cell's collision code (the engine's
            // WorldColBrick returns it as it is): 1 = solid, 2..13 = ramps; retail has ~57,000 such invisible walls
            cells[i + 1] = (byte)e.Pos;
            if (e.Block != 0) usedBlocks[e.Block >> 3] |= (byte)(0x80 >> (e.Block & 7));
            columns.Add((e.Z, e.X));
        }

        var result = new List<byte>(grid.AsSpan(0, grid.Length - UsedBlocksSize).ToArray());
        foreach (var (z, x) in columns)
        {
            var offset = result.Count;
            if (offset > ushort.MaxValue) throw new InvalidOperationException("The grid entry outgrew its 16-bit column offsets.");
            result.AddRange(EncodeColumn(cells, x, z));
            result[(x + z * Size) * 2] = (byte)offset;
            result[(x + z * Size) * 2 + 1] = (byte)(offset >> 8);
        }
        result.AddRange(usedBlocks);
        return result.ToArray();
    }

    public static Lba1GridCell Get(byte[] grid, int x, int y, int z)
    {
        var cells = Lba1GridCodec.Decode(grid);
        var i = ((z * Size + x) * Height + y) * 2;
        return new Lba1GridCell(x, y, z, cells[i], cells[i + 1]);
    }

    // True if the entry's trailing bitmap lists every block its cells use (what the engine needs to load their bricks).
    public static bool UsedBlocksListed(byte[] grid)
    {
        if (grid.Length < TableSize + UsedBlocksSize) return false;
        var cells = Lba1GridCodec.Decode(grid);
        var tail = grid.AsSpan(grid.Length - UsedBlocksSize);
        for (var i = 0; i < cells.Length; i += 2)
        {
            var block = cells[i];
            if (block != 0 && (tail[block >> 3] & (0x80 >> (block & 7))) == 0) return false;
        }
        return true;
    }

    // The column's cells as runs. The engine writes the runs into a buffer it never clears, so a column must always
    // account for all 25 cells: trailing empties are written as a skip run, like the game's own grids.
    internal static byte[] EncodeColumn(byte[] cells, int x, int z)
    {
        var start = (z * Size + x) * Height * 2;
        var pairs = new (byte Block, byte Pos)[Height];
        for (var y = 0; y < Height; y++) pairs[y] = (cells[start + y * 2], cells[start + y * 2 + 1]);
        var last = Array.FindLastIndex(pairs, p => p.Block != 0 || p.Pos != 0);
        if (last < 0) return new byte[] { 1, Height - 1 };

        var runs = new List<byte[]>();
        var at = 0;
        while (at <= last)
        {
            if (pairs[at] == (0, 0))
            {
                var n = 0;
                while (at + n <= last && pairs[at + n] == (0, 0) && n < 64) n++;
                runs.Add(new[] { (byte)(n - 1) });
                at += n;
                continue;
            }
            var same = 1;
            while (at + same <= last && pairs[at + same] == pairs[at] && same < 64) same++;
            if (same >= 2)
            {
                runs.Add(new[] { (byte)(0x80 | (same - 1)), pairs[at].Block, pairs[at].Pos });
                at += same;
                continue;
            }
            var literal = 1;
            while (at + literal <= last && pairs[at + literal] != (0, 0) && literal < 64
                   && !(at + literal + 1 <= last && pairs[at + literal + 1] == pairs[at + literal])) literal++;
            var run = new List<byte> { (byte)(0x40 | (literal - 1)) };
            for (var k = 0; k < literal; k++) { run.Add(pairs[at + k].Block); run.Add(pairs[at + k].Pos); }
            runs.Add(run.ToArray());
            at += literal;
        }
        if (last < Height - 1) runs.Add(new[] { (byte)(Height - 1 - last - 1) });
        var column = new List<byte> { (byte)runs.Count };
        foreach (var r in runs) column.AddRange(r);
        return column.ToArray();
    }
}
