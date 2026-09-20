using System.Buffers.Binary;
using System.IO;

namespace LBA2LevelEditor.Assets;

// A file of run-length pictures ("graphs"): the bricks of LBA1 (LBA_BRK.HQR) and LBA2 (the brick range of LBA_BKG.HQR), the sprites of LBA1
// (SPRITES.HQR). Reads pictures, replaces them and saves the archive back (a .bak of the original, other entries byte for byte).
internal sealed class GphLibrary
{
    private readonly HqrFile hqr;
    private readonly int firstEntry;
    private int count;
    // sprite entries start with the offset of the picture (8) and its size; bricks are the picture alone
    private readonly bool hasHeader;
    // raw pictures (LBA2 SPRIRAW): the pixels are stored as they are, index 0 is transparent
    public bool Raw { get; }

    public string Path { get; }
    public string Title { get; }
    // The palette the pictures are drawn with (RESS.HQR entry 0 of the same game folder).
    public string PaletteFile { get; }

    private GphLibrary(string path, string title, HqrFile hqr, int firstEntry, int count, bool hasHeader, bool raw = false)
    {
        Raw = raw;
        Path = path; Title = title; this.hqr = hqr; this.firstEntry = firstEntry; this.count = count; this.hasHeader = hasHeader;
        PaletteFile = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", "RESS.HQR");
    }

    public static GphLibrary Lba1Bricks(string directory) => Open(System.IO.Path.Combine(directory, "LBA_BRK.HQR"), "LBA1 bricks", 0, null, false);
    public static GphLibrary Lba1Sprites(string directory) => Open(System.IO.Path.Combine(directory, "SPRITES.HQR"), "LBA1 sprites", 0, null, true);
    public static GphLibrary Lba2Sprites(string directory) => Open(System.IO.Path.Combine(directory, "SPRITES.HQR"), "LBA2 sprites", 0, null, true);
    public static GphLibrary Lba2RawSprites(string directory) { var path = System.IO.Path.Combine(directory, "SPRIRAW.HQR"); return new GphLibrary(path, "LBA2 raw sprites", HqrFile.Parse(File.ReadAllBytes(path)), 0, HqrFile.CountSlots(File.ReadAllBytes(path)), true, true); }

    // LBA2: the header (entry 0 of LBA_BKG.HQR) says where the bricks start: U16 Gri_Start, Grm_Start, Bll_Start, Brk_Start, Max_Brk.
    public static GphLibrary Lba2Bricks(string directory)
    {
        var path = System.IO.Path.Combine(directory, "LBA_BKG.HQR");
        var hqr = HqrFile.Parse(File.ReadAllBytes(path));
        var header = hqr.Read(0);
        int start = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)), max = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
        return new GphLibrary(path, "LBA2 bricks", hqr, start, Math.Min(max, hqr.Count - start), false);
    }

    private static GphLibrary Open(string path, string title, int first, int? count, bool header)
    {
        var hqr = HqrFile.Parse(File.ReadAllBytes(path));
        return new GphLibrary(path, title, hqr, first, count ?? hqr.Count, header);
    }

    public int Count => count;

    // The picture numbers that hold data.
    public IEnumerable<int> Numbers() => Enumerable.Range(0, count).Where(n => !hqr.IsEmpty(firstEntry + n));

    private int PictureStart(byte[] entry) => hasHeader ? (entry.Length >= 8 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(entry) : entry.Length) : 0;

    public GphImage Read(int number)
    {
        var entry = hqr.Read(firstEntry + number);
        return Raw ? GphImage.DecodeRaw(entry, PictureStart(entry)) : GphImage.Decode(entry, PictureStart(entry));
    }

    // Replaces a picture; a sprite keeps its 8-byte header (the offset stays, the size follows the new picture).
    public void Replace(int number, GphImage image)
    {
        var picture = Raw ? image.EncodeRaw() : image.Encode();
        var old = hqr.Read(firstEntry + number);
        byte[] entry;
        if (hasHeader)
        {
            var start = PictureStart(old);
            entry = new byte[start + picture.Length];
            Array.Copy(old, entry, Math.Min(start, old.Length));
            if (start >= 8) BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)picture.Length);
            picture.CopyTo(entry, start);
        }
        else entry = picture;
        hqr.SetStored(firstEntry + number, entry);
    }

    // Adds a new picture and returns its number. LBA1 files just grow; LBA2 bricks are inserted after the last brick (the table of scene -> grid that follows them
    // moves up one slot) and the brick count in the file's header (entry 0) is raised, which is where the engine finds that table again.
    public int Add(GphImage image)
    {
        var picture = Raw ? image.EncodeRaw() : image.Encode();
        byte[] entry;
        if (hasHeader)
        {
            entry = new byte[8 + picture.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)picture.Length);
            picture.CopyTo(entry, 8);
        }
        else entry = picture;

        int number;
        if (Title == "LBA2 bricks")
        {
            var header = hqr.Read(0);
            var max = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
            if (max >= ushort.MaxValue - 1) throw new InvalidOperationException("The file has no room for more bricks.");
            number = max;
            hqr.InsertAt(firstEntry + number, entry);
            var changed = (byte[])header.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(8), (ushort)(max + 1));
            hqr.SetStored(0, changed);
            count = max + 1;
        }
        else
        {
            number = hqr.Add(entry) - firstEntry;
            count = hqr.Count - firstEntry;
        }
        return number;
    }

    public byte[] ToBytes() => hqr.ToBytes();

    public void Save()
    {
        var bytes = hqr.ToBytes();
        new FileTransaction().Write(Path, bytes, VerifySaved).Commit();
    }

    private string? VerifySaved(byte[] bytes)
    {
        try
        {
            var back = HqrFile.Parse(bytes);
            foreach (var n in Numbers())
            {
                var mine = Read(n);
                var entry = back.Read(firstEntry + n);
                var theirs = Raw ? GphImage.DecodeRaw(entry, PictureStart(entry)) : GphImage.Decode(entry, PictureStart(entry));
                if (theirs.Width != mine.Width || theirs.Height != mine.Height || !theirs.Pixels.AsSpan().SequenceEqual(mine.Pixels) || !theirs.Opaque.AsSpan().SequenceEqual(mine.Opaque))
                    return $"picture {n} differs after the save";
            }
            return null;
        }
        catch (Exception e) { return "the saved file does not parse: " + e.Message; }
    }

    // The 768-byte palette of the game the library belongs to.
    public byte[] LoadPalette() => HqrFile.Parse(File.ReadAllBytes(PaletteFile)).Read(0);
}
