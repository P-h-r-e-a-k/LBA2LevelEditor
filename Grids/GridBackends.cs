using System.Buffers.Binary;
using System.IO;
using LBA2LevelEditor.Lba1;
using LBA2LevelEditor.Scenes;

namespace LBA2LevelEditor.Grids;

// The isometric maps of the interiors: a grid of 64 x 25 x 64 cells, each a (block, position in block) pair, over a block library and the bricks.
// LBA1 keeps a grid per scene in LBA_GRI.HQR with its library in LBA_BLL.HQR; LBA2 keeps them all in LBA_BKG.HQR (a 34-byte header of style, fragment set and
// used-block bitmap, then the same columns). Both are handled in the LBA1 shape: offsets table, columns, then the 32-byte used-block bitmap at the end.
internal interface IGridBackend
{
    string Title { get; }
    string Folder { get; }
    IReadOnlyList<(int Id, string Label)> Grids { get; }
    // the grid in the LBA1 shape
    byte[] LoadGrid(int id);
    // the block library the grid uses
    byte[] LoadLibrary(int id);
    // which grids share the grid's library (editing it changes them all)
    IReadOnlyList<int> LibraryUsers(int id);
    // a brick's picture (0-based number) or null
    byte[]? Brick(int number);
    byte[] Palette { get; }
    void SaveGrid(int id, byte[] grid);
    void SaveLibrary(int id, byte[] library);
}

internal sealed class Lba1GridBackend : IGridBackend
{
    private readonly string directory;
    private readonly HqrFile grids, libraries;
    private readonly HqrArchive bricks;

    public Lba1GridBackend(string directory, Func<int, string?>? describe = null)
    {
        this.directory = directory;
        grids = HqrFile.Parse(File.ReadAllBytes(System.IO.Path.Combine(directory, "LBA_GRI.HQR")));
        libraries = HqrFile.Parse(File.ReadAllBytes(System.IO.Path.Combine(directory, "LBA_BLL.HQR")));
        bricks = HqrArchive.Open(System.IO.Path.Combine(directory, "LBA_BRK.HQR"));
        Palette = HqrFile.Parse(File.ReadAllBytes(System.IO.Path.Combine(directory, "RESS.HQR"))).Read(0);
        // entries 120 and up of LBA_GRI.HQR are the grid fragments (GRMs), not scene grids
        Grids = Enumerable.Range(0, Math.Min(120, grids.Count)).Where(i => !grids.IsEmpty(i) && !libraries.IsEmpty(i)).Select(i => (i, describe?.Invoke(i) is { } d ? $"{i}: {d}" : $"{i}")).ToList();
    }

    public string Title => "LBA1";
    public string Folder => directory;
    public IReadOnlyList<(int Id, string Label)> Grids { get; }
    public byte[] Palette { get; }
    public byte[] LoadGrid(int id) => grids.Read(id);
    public byte[] LoadLibrary(int id) => libraries.Read(id);
    public IReadOnlyList<int> LibraryUsers(int id) => new[] { id };
    public byte[]? Brick(int number) => number >= 0 && bricks.IsValid(number) ? bricks.Read(number) : null;

    // Through the scene store, so the grid is checked against its library and bricks (Lba1GridValidator) before it is written, like every other scene save.
    public void SaveGrid(int id, byte[] grid)
    {
        var store = new SceneStore(SceneGame.Lba1, directory);
        store.Save(id, store.Load(id), grid);
        grids.SetStored(id, grid);
    }

    public void SaveLibrary(int id, byte[] library)
    {
        libraries.SetStored(id, library);
        new FileTransaction().Write(System.IO.Path.Combine(directory, "LBA_BLL.HQR"), libraries.ToBytes(), saved => HqrFile.Parse(saved).Read(id).AsSpan().SequenceEqual(library) ? null : "the library differs after the save").Commit();
    }
}

// LBA2: entry 0 of LBA_BKG.HQR says where things start (U16 Gri_Start, Grm_Start, Bll_Start, Brk_Start, Max_Brk). Grid n is entry Gri_Start + n:
// [My_Bll, My_Grm, used-block bitmap (32)] then the offsets table (relative to the end of that header) and the columns. Its library is entry Bll_Start + My_Bll.
internal sealed class Lba2GridBackend : IGridBackend
{
    public const int HeaderSize = 34;
    private readonly string path;
    private readonly HqrFile file;
    private readonly int griStart, grmStart, bllStart, brkStart, maxBrk;

    public Lba2GridBackend(string directory)
    {
        path = System.IO.Path.Combine(directory, "LBA_BKG.HQR");
        file = HqrFile.Parse(File.ReadAllBytes(path));
        var header = file.Read(0);
        griStart = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0)); grmStart = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        bllStart = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)); brkStart = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        maxBrk = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
        Folder = directory;
        Palette = HqrFile.Parse(File.ReadAllBytes(System.IO.Path.Combine(directory, "RESS.HQR"))).Read(0);
        Grids = Enumerable.Range(0, Math.Max(0, grmStart - griStart)).Where(i => !file.IsEmpty(griStart + i)).Select(i => (i, $"grid {i} (library {StyleOf(i)})")).ToList();
    }

    public string Title => "LBA2";
    public string Folder { get; }

    // The interior grid a scene loads: the table after the bricks (two bytes per scene: type, grid), or null.
    public int? GridOfScene(int scene)
    {
        var table = file.Read(brkStart + maxBrk);
        if (scene < 0 || scene * 2 + 1 >= table.Length) return null;
        var grid = table[scene * 2 + 1];
        return Grids.Any(g => g.Id == grid) ? grid : null;
    }
    public IReadOnlyList<(int Id, string Label)> Grids { get; }
    public byte[] Palette { get; }

    private int StyleOf(int id) => file.Read(griStart + id)[0];

    public byte[] LoadGrid(int id) => ToLba1Shape(file.Read(griStart + id));
    public byte[] LoadLibrary(int id) => file.Read(bllStart + StyleOf(id));
    public IReadOnlyList<int> LibraryUsers(int id) { var style = StyleOf(id); return Grids.Where(g => StyleOf(g.Id) == style).Select(g => g.Id).ToList(); }
    public byte[]? Brick(int number) => number >= 0 && number < maxBrk && !file.IsEmpty(brkStart + number) ? file.Read(brkStart + number) : null;

    // header (2 + 32) + body  ->  body + the used-block bitmap
    public static byte[] ToLba1Shape(byte[] entry)
    {
        var shape = new byte[entry.Length - HeaderSize + 32];
        Array.Copy(entry, HeaderSize, shape, 0, entry.Length - HeaderSize);
        Array.Copy(entry, 2, shape, shape.Length - 32, 32);
        return shape;
    }

    public static byte[] FromLba1Shape(byte[] original, byte[] shape)
    {
        var entry = new byte[shape.Length - 32 + HeaderSize];
        entry[0] = original[0]; entry[1] = original[1];
        Array.Copy(shape, shape.Length - 32, entry, 2, 32);
        Array.Copy(shape, 0, entry, HeaderSize, shape.Length - 32);
        return entry;
    }

    public void SaveGrid(int id, byte[] grid)
    {
        var entry = FromLba1Shape(file.Read(griStart + id), grid);
        file.SetStored(griStart + id, entry);
        new FileTransaction().Write(path, file.ToBytes(), saved => HqrFile.Parse(saved).Read(griStart + id).AsSpan().SequenceEqual(entry) ? null : "the grid differs after the save").Commit();
    }

    public void SaveLibrary(int id, byte[] library)
    {
        var index = bllStart + StyleOf(id);
        file.SetStored(index, library);
        new FileTransaction().Write(path, file.ToBytes(), saved => HqrFile.Parse(saved).Read(index).AsSpan().SequenceEqual(library) ? null : "the library differs after the save").Commit();
    }
}

// Painting blocks into a grid. A block spans dx x dy x dz cells; the cell at offset (i, j, k) from the block's origin is (block, i + dx * (j + dy * k))
// (measured on the retail grids of LBA1: every block whose placements are not overlapped by another placement of itself follows it).
internal static class GridPaint
{
    public const int Size = 64, Height = 25;

    public readonly record struct BlockInfo(int Number, int Dx, int Dy, int Dz, int FirstBrick);

    public static int BlockCount(byte[] library) => (int)(BinaryPrimitives.ReadUInt32LittleEndian(library) / 4);

    public static BlockInfo? Info(byte[] library, int block)
    {
        if (block < 1 || block > BlockCount(library)) return null;
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(library.AsSpan((block - 1) * 4));
        if (at + 3 > library.Length) return null;
        int dx = library[at], dy = library[at + 1], dz = library[at + 2];
        var first = at + 3 + 2 < library.Length ? BinaryPrimitives.ReadUInt16LittleEndian(library.AsSpan(at + 3 + 2)) - 1 : -1;
        return new BlockInfo(block, dx, dy, dz, first);
    }

    // The 4-byte entries of a block: shape, sound, brick + 1.
    public static IEnumerable<(int Pos, int Shape, int Sound, int Brick)> Entries(byte[] library, int block)
    {
        if (Info(library, block) is not { } info) yield break;
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(library.AsSpan((block - 1) * 4)) + 3;
        for (var p = 0; p < info.Dx * info.Dy * info.Dz && at + p * 4 + 4 <= library.Length; p++)
            yield return (p, library[at + p * 4], library[at + p * 4 + 1], BinaryPrimitives.ReadUInt16LittleEndian(library.AsSpan(at + p * 4 + 2)) - 1);
    }

    // The cells that placing `block` at (x, y, z) writes; null if it would not fit inside the grid.
    public static List<Lba1GridCell>? PlaceCells(byte[] library, int block, int x, int y, int z)
    {
        if (Info(library, block) is not { } info || info.Dx * info.Dy * info.Dz == 0) return null;
        if (x < 0 || y < 0 || z < 0 || x + info.Dx > Size || y + info.Dy > Height || z + info.Dz > Size) return null;
        var cells = new List<Lba1GridCell>();
        for (var k = 0; k < info.Dz; k++) for (var j = 0; j < info.Dy; j++) for (var i = 0; i < info.Dx; i++)
            cells.Add(new Lba1GridCell(x + i, y + j, z + k, block, i + info.Dx * (j + info.Dy * k)));
        return cells;
    }

    public static byte[] Place(byte[] grid, byte[] library, int block, int x, int y, int z)
    {
        var cells = PlaceCells(library, block, x, y, z) ?? throw new ArgumentOutOfRangeException(nameof(block), "The block does not fit there.");
        return Lba1GridEdit.SetCells(grid, cells);
    }

    // The origin of the block placement a cell belongs to, or null for an empty cell.
    public static (int Block, int X, int Y, int Z)? OriginOf(byte[] grid, byte[] library, int x, int y, int z)
    {
        var cell = Lba1GridEdit.Get(grid, x, y, z);
        if (cell.Block == 0 || Info(library, cell.Block) is not { } info) return null;
        int i = cell.Pos % Math.Max(1, info.Dx), j = cell.Pos / Math.Max(1, info.Dx) % Math.Max(1, info.Dy), k = cell.Pos / Math.Max(1, info.Dx * info.Dy);
        return (cell.Block, x - i, y - j, z - k);
    }

    // Removes the whole block placement that the cell belongs to (only the cells that still carry that placement's numbering); keeps the cells' second
    // byte (an empty cell's second byte is its collision code, not part of the picture) when `keepCollision`.
    public static byte[] Erase(byte[] grid, byte[] library, int x, int y, int z)
    {
        if (OriginOf(grid, library, x, y, z) is not { } origin) return grid;
        var info = Info(library, origin.Block)!.Value;
        var cells = Lba1GridEdit.Get(grid, x, y, z) is var c ? new List<Lba1GridCell>() : new List<Lba1GridCell>();
        var all = Lba1GridCodec.Decode(grid);
        for (var k = 0; k < info.Dz; k++) for (var j = 0; j < info.Dy; j++) for (var i = 0; i < info.Dx; i++)
        {
            int cx = origin.X + i, cy = origin.Y + j, cz = origin.Z + k;
            if ((uint)cx >= Size || (uint)cz >= Size || (uint)cy >= Height) continue;
            var at = ((cz * Size + cx) * Height + cy) * 2;
            if (all[at] == origin.Block && all[at + 1] == i + info.Dx * (j + info.Dy * k)) cells.Add(new Lba1GridCell(cx, cy, cz, 0, 0));
        }
        return cells.Count == 0 ? grid : Lba1GridEdit.SetCells(grid, cells);
    }

    // Fills the x-z rectangle at layer y with a block, stepping by the block's size.
    public static byte[] FillRectangle(byte[] grid, byte[] library, int block, int x0, int z0, int x1, int z1, int y)
    {
        var info = Info(library, block) ?? throw new ArgumentOutOfRangeException(nameof(block));
        var cells = new List<Lba1GridCell>();
        for (var z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z += Math.Max(1, info.Dz))
            for (var x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x += Math.Max(1, info.Dx))
                if (PlaceCells(library, block, x, y, z) is { } placed) cells.AddRange(placed);
        return cells.Count == 0 ? grid : Lba1GridEdit.SetCells(grid, cells);
    }

    // The same block, its brick numbers replaced: a new library entry (used by "new block").
    public static byte[] AppendBlock(byte[] library, int dx, int dy, int dz, int brick, int shape = 1, int sound = 0)
    {
        var count = BlockCount(library);
        var table = new List<uint>();
        for (var i = 0; i < count; i++) table.Add(BinaryPrimitives.ReadUInt32LittleEndian(library.AsSpan(i * 4)));
        // the offsets table grows by one entry, so every block moves 4 bytes
        var body = library.AsSpan(count * 4).ToArray();
        var entry = new List<byte> { (byte)dx, (byte)dy, (byte)dz };
        for (var p = 0; p < dx * dy * dz; p++) { entry.Add((byte)shape); entry.Add((byte)sound); entry.Add((byte)((brick + 1) & 255)); entry.Add((byte)((brick + 1) >> 8)); }
        var result = new byte[(count + 1) * 4 + body.Length + entry.Count];
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), table[i] + 4);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(count * 4), (uint)((count + 1) * 4 + body.Length));
        body.CopyTo(result, (count + 1) * 4);
        entry.CopyTo(result, (count + 1) * 4 + body.Length);
        return result;
    }

    // Sets the brick (and optionally shape / sound) of one entry of a block: a new library, the same size.
    public static byte[] SetEntry(byte[] library, int block, int pos, int brick, int? shape = null, int? sound = null)
    {
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(library.AsSpan((block - 1) * 4)) + 3 + pos * 4;
        var copy = (byte[])library.Clone();
        if (shape is { } s) copy[at] = (byte)s;
        if (sound is { } o) copy[at + 1] = (byte)o;
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(at + 2), (ushort)(brick + 1));
        return copy;
    }
}
