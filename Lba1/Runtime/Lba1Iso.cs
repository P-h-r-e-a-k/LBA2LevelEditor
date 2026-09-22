using System.IO;

namespace LBAAssembler.Lba1.Runtime;

// The game's CD image (LBA.iso / LBA.GOG): a raw MODE1 disc, 2352-byte sectors with 16 bytes of header before each
// 2048-byte block, an ISO 9660 file system on track 1 (the films, the game data), and the CD's music as audio tracks after
// it (LBA.DAT is the cue sheet giving where each begins).
internal sealed class Lba1Iso : IDisposable
{
    public sealed record Entry(string Path, long Sector, long Size);

    private const int RawSector = 2352, DataOffset = 16, DataSize = 2048;
    private readonly FileStream stream;
    public IReadOnlyList<Entry> Files { get; }

    public static string? Find(string gameDirectory)
    {
        foreach (var name in new[] { "LBA.iso", "LBA.GOG", "LBA.bin" })
        {
            var path = Path.Combine(gameDirectory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public Lba1Iso(string path)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var pvd = ReadBlock(16);
        if (pvd.Length < DataSize || System.Text.Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") throw new InvalidDataException("This isn't a raw MODE1 CD image.");
        var files = new List<Entry>();
        Walk(pvd[156..], "", files, 0);
        Files = files;
    }

    private byte[] ReadBlock(long sector)
    {
        var block = new byte[DataSize];
        stream.Position = sector * RawSector + DataOffset;
        stream.ReadExactly(block, 0, DataSize);
        return block;
    }

    private void Walk(byte[] record, string prefix, List<Entry> files, int depth)
    {
        var sector = BitConverter.ToUInt32(record, 2);
        var size = BitConverter.ToUInt32(record, 10);
        var data = new byte[((size + DataSize - 1) / DataSize) * DataSize];
        for (var i = 0; i < data.Length / DataSize; i++) ReadBlock(sector + i).CopyTo(data, i * DataSize);

        var at = 0;
        while (at < size)
        {
            int length = data[at];
            if (length == 0) { at = (at / DataSize + 1) * DataSize; continue; }
            var flags = data[at + 25];
            int nameLength = data[at + 32];
            var name = System.Text.Encoding.ASCII.GetString(data, at + 33, nameLength);
            if (!(nameLength == 1 && data[at + 33] is 0 or 1))
            {
                var semicolon = name.IndexOf(';');
                if (semicolon >= 0) name = name[..semicolon];
                var full = prefix + "/" + name;
                if ((flags & 2) != 0)
                {
                    if (depth < 8) Walk(data[at..(at + length)], full, files, depth + 1);
                }
                else files.Add(new Entry(full, BitConverter.ToUInt32(data, at + 2), BitConverter.ToUInt32(data, at + 10)));
            }
            at += length;
        }
    }

    public byte[] Read(Entry file)
    {
        var bytes = new byte[file.Size];
        var read = 0;
        for (var i = 0L; read < bytes.Length; i++)
        {
            var block = ReadBlock(file.Sector + i);
            var take = (int)Math.Min(DataSize, bytes.Length - read);
            Array.Copy(block, 0, bytes, read, take);
            read += take;
        }
        return bytes;
    }

    // Raw CD audio (44.1 kHz, 16-bit, stereo) between two sectors of the image.
    public byte[] ReadAudio(long firstSector, long sectorCount)
    {
        var pcm = new byte[sectorCount * RawSector];
        stream.Position = firstSector * RawSector;
        stream.ReadExactly(pcm, 0, pcm.Length);
        return pcm;
    }

    public long SectorCount => stream.Length / RawSector;

    public void Dispose() => stream.Dispose();
}
