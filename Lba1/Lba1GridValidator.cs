using System.Buffers.Binary;
using LBAAssembler.Scenes;

namespace LBAAssembler.Lba1;

// Checks an LBA1 grid (LBA_GRI entry) against its block library (LBA_BLL entry) the way the engine will read them
// (GRILLE.C: InitGrille, LoadUsedBrick, CopyMapToCube / DecompColonne).
internal static class Lba1GridValidator
{
    public const int MaxBrickBytes = 361472;   // MAX_SIZE_BRICK_CUBE
    public const int MaxBrickNumber = 10000;   // MAX_BRICK_GAME

    public sealed record Report(List<SceneIssue> Issues, int BlocksUsed, int Bricks, long BrickBytes);

    // `brickSize` gives a brick's decompressed size in LBA_BRK.HQR (bricks are numbered from 0), or -1 if unknown;
    // without it the brick budget isn't checked.
    public static Report Validate(byte[] grid, byte[] library, Func<int, int>? brickSize = null)
    {
        var issues = new List<SceneIssue>();
        void Error(string where, string message) => issues.Add(new(SceneIssueSeverity.Error, where, message));
        void Warn(string where, string message) => issues.Add(new(SceneIssueSeverity.Warning, where, message));

        const int table = 64 * 64 * 2, tail = 32;
        if (grid.Length < table + tail) { Error("grid", "the entry is shorter than its column table and used-blocks bitmap."); return new Report(issues, 0, 0, 0); }
        var columnsEnd = grid.Length - tail;

        // ---- library ----
        var blockCount = library.Length >= 4 ? (int)(BinaryPrimitives.ReadUInt32LittleEndian(library) / 4) : 0;
        if (blockCount == 0) Error("library", "the block library is empty or missing.");
        if (blockCount > 255) Warn("library", $"{blockCount} blocks; the used-blocks bitmap only has bits for blocks 1..255.");
        (int Dx, int Dy, int Dz, int At)? Block(int number)
        {
            if (number < 1 || number > blockCount) return null;
            var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(library.AsSpan((number - 1) * 4));
            if (at < 0 || at + 3 > library.Length) return null;
            return (library[at], library[at + 1], library[at + 2], at);
        }

        // ---- columns ----
        var used = new SortedSet<int>();
        for (var z = 0; z < 64; z++)
        for (var x = 0; x < 64; x++)
        {
            var where = $"column ({x}, {z})";
            int src = BinaryPrimitives.ReadUInt16LittleEndian(grid.AsSpan((x + z * 64) * 2));
            if (src < table || src >= columnsEnd) { Error(where, $"starts at byte {src}, outside the column data ({table}..{columnsEnd - 1})."); continue; }
            int runs = grid[src++];
            if (runs == 0) { Error(where, "has no runs (the engine would read 256 of them)."); continue; }
            var cells = 0;
            var ok = true;
            while (runs-- > 0)
            {
                if (src >= columnsEnd) { Error(where, "runs off the end of the column data."); ok = false; break; }
                var op = grid[src++];
                var count = (op & 0x3F) + 1;
                cells += count;
                switch (op >> 6)
                {
                    case 0: break;
                    case 1:
                        for (var k = 0; k < count && src + 1 < columnsEnd; k++) { Use(grid[src], grid[src + 1], where); src += 2; }
                        break;
                    default:
                        if (src + 1 >= columnsEnd) { Error(where, "runs off the end of the column data."); ok = false; }
                        else { Use(grid[src], grid[src + 1], where); src += 2; }
                        break;
                }
                if (!ok) break;
            }
            if (ok && cells != 25) Error(where, $"holds {cells} cells; the engine needs exactly 25 (it never clears the buffer, so a short column leaves cells of the previous scene behind).");
        }

        void Use(int block, int pos, string where)
        {
            if (block == 0) return;
            used.Add(block);
            var b = Block(block);
            if (b is null) { Error(where, $"uses block {block}, but the library has {blockCount}."); return; }
            if (pos >= b.Value.Dx * b.Value.Dy * b.Value.Dz) Error(where, $"cell {pos} of block {block}, which is only {b.Value.Dx}x{b.Value.Dy}x{b.Value.Dz}.");
        }

        // ---- used-blocks bitmap ----
        var flagged = new SortedSet<int>();
        for (var b = 1; b < 256; b++)
            if ((grid[columnsEnd + (b >> 3)] & (0x80 >> (b & 7))) != 0) flagged.Add(b);
        foreach (var b in used.Except(flagged)) Error("used-blocks bitmap", $"block {b} is used by the grid but isn't listed, so the engine won't load its bricks.");
        foreach (var b in flagged.Except(used)) Warn("used-blocks bitmap", $"block {b} is listed but no cell uses it (its bricks are loaded for nothing).");

        // ---- bricks the flagged blocks need ----
        var bricks = new SortedSet<int>();
        foreach (var number in flagged)
        {
            var b = Block(number);
            if (b is null) continue;
            var extent = b.Value.Dx * b.Value.Dy * b.Value.Dz;
            for (var k = 0; k < extent && b.Value.At + 3 + k * 4 + 4 <= library.Length; k++)
            {
                var brick = BinaryPrimitives.ReadUInt16LittleEndian(library.AsSpan(b.Value.At + 3 + k * 4 + 2));
                if (brick != 0) bricks.Add(brick - 1);
            }
        }
        long bytes = 0;
        if (bricks.Count > 0 && bricks.Max >= MaxBrickNumber) Error("bricks", $"brick {bricks.Max} is above the engine's limit of {MaxBrickNumber}.");
        if (brickSize is not null)
        {
            bytes = (bricks.Count + 1) * 4L;
            foreach (var brick in bricks)
            {
                var size = brickSize(brick);
                if (size < 0) { Error("bricks", $"brick {brick} isn't in LBA_BRK.HQR."); continue; }
                bytes += size;
            }
            if (bytes > MaxBrickBytes) Error("bricks", $"the scene's {bricks.Count} bricks take {bytes} bytes; the engine's brick buffer is {MaxBrickBytes}.");
        }
        return new Report(issues, used.Count, bricks.Count, bytes);
    }
}
