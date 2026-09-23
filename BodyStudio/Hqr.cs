using System.IO;
using System.Linq;

namespace LbaBodyStudio;

// HQR offset directory followed by 10-byte record headers and LZ payloads.
public sealed class Hqr
{
    readonly byte[] data;
    readonly int[] offsets;
    public int Count => offsets.Length;
    public Hqr(string path) : this(File.ReadAllBytes(path)) { }
    public Hqr(byte[] bytes)
    {
        data = bytes;
        if (bytes.Length < 4) throw new InvalidDataException("Truncated HQR directory.");
        int size = BitConverter.ToInt32(bytes, 0);
        if (size < 4 || size % 4 != 0 || size > bytes.Length) throw new InvalidDataException("Invalid HQR directory.");
        offsets = new int[size / 4];
        for (int i = 0; i < Count; i++)
        {
            offsets[i] = BitConverter.ToInt32(bytes, i * 4);
            if (offsets[i] != 0 && (offsets[i] < size || offsets[i] > bytes.Length))
                throw new InvalidDataException("HQR offset out of bounds.");
        }
    }
    public byte[] Read(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        int p = offsets[index];
        if (p == 0 || p == data.Length) throw new InvalidDataException("Empty HQR entry.");
        if (p + 10 > data.Length) throw new InvalidDataException("Truncated HQR header.");
        int size = BitConverter.ToInt32(data, p), packed = BitConverter.ToInt32(data, p + 4);
        int method = BitConverter.ToUInt16(data, p + 8);
        p += 10;
        if (size < 0 || size > 32 * 1024 * 1024 || packed < 0 || packed > data.Length - p || method > 2)
            throw new InvalidDataException("Unsupported or malformed HQR entry.");
        int end = p + packed;
        if (method == 0)
        {
            if (size != packed) throw new InvalidDataException("Invalid uncompressed HQR size.");
            return data.AsSpan(p, size).ToArray();
        }
        byte[] result = new byte[size];
        int dst = 0;
        byte Next() => p < end ? data[p++] : throw new InvalidDataException("Truncated LZ stream.");
        while (dst < size)
        {
            int flags = Next();
            for (int bit = 0; bit < 8 && dst < size; bit++, flags >>= 1)
            {
                if ((flags & 1) != 0) result[dst++] = Next();
                else
                {
                    int lo = Next(), hi = Next();
                    int length = (lo & 15) + method + 1;
                    int distance = ((hi << 4) | (lo >> 4)) + 1;
                    if (distance > dst) throw new InvalidDataException("Invalid LZ back reference.");
                    for (int j = 0; j < length && dst < size; j++, dst++) result[dst] = result[dst - distance];
                }
            }
        }
        return result;
    }
    // Copy raw entry spans: preserve other records, their compression, and hidden subentries.
    public byte[] Replace(int index, byte[] replacement)
    {
        if (index < 0 || index >= Count || offsets[index] == 0 || offsets[index] == data.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        int start = offsets[index];
        if(offsets.Count(o=>o==start)>1)throw new InvalidDataException("This entry shares storage with another HQR index; replacement would affect both.");
        if (start + 10 > data.Length) throw new InvalidDataException("Truncated record.");
        int oldEnd = checked(start + 10 + BitConverter.ToInt32(data, start + 4));
        int next = offsets.Where(x => x > start).DefaultIfEmpty(data.Length).Min();
        if (oldEnd > next || oldEnd < start) throw new InvalidDataException("Overlapping HQR records.");
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(data, 0, start);
        byte[] packed = Compress(replacement);
        w.Write(replacement.Length); w.Write(packed.Length); w.Write((ushort)1); w.Write(packed);
        w.Write(data, oldEnd, data.Length - oldEnd);
        byte[] output = stream.ToArray();
        int delta = packed.Length + 10 - (oldEnd - start);
        for (int i = 0; i < Count; i++)
            if (offsets[i] >= oldEnd) BitConverter.GetBytes(checked(offsets[i] + delta)).CopyTo(output, i * 4);
        return output;
    }

    // A brand new archive holding exactly these entries, stored uncompressed (method 0): the offset table
    // (one u32 per entry plus a trailing sentinel equal to the file's own length, matching how Read/Replace
    // above already expect a real HQR to end), then each entry as [u32 size][u32 size][u16 0][payload].
    // Used for small debug/test archives (see mario.hqr) where simplicity matters more than file size.
    public static byte[] Build(IReadOnlyList<byte[]> entries)
    {
        int pos = (entries.Count + 1) * 4;
        var offsets = new int[entries.Count + 1];
        for (int i = 0; i < entries.Count; i++) { offsets[i] = pos; pos += 10 + entries[i].Length; }
        offsets[entries.Count] = pos;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        foreach (var o in offsets) w.Write(o);
        foreach (var e in entries) { w.Write(e.Length); w.Write(e.Length); w.Write((ushort)0); w.Write(e); }
        return stream.ToArray();
    }

    // Classic method 1: 4096-byte window, 2..17-byte matches, LSB-first flags.
    public static byte[] Compress(byte[] input)
    {
        using var output = new MemoryStream();
        int at = 0;
        while (at < input.Length)
        {
            long flagAt = output.Position; output.WriteByte(0); byte flags = 0;
            for (int bit = 0; bit < 8 && at < input.Length; bit++)
            {
                int best = 0, distance = 0, maximum = Math.Min(17, input.Length - at);
                for (int candidate = at - 1; candidate >= Math.Max(0, at - 4096); candidate--)
                {
                    int length = 0;
                    while (length < maximum && input[candidate + length] == input[at + length]) length++;
                    if (length > best) { best = length; distance = at - candidate; }
                    if (best == maximum) break;
                }
                if (best >= 2)
                {
                    int token = ((distance - 1) << 4) | (best - 2);
                    output.WriteByte((byte)token); output.WriteByte((byte)(token >> 8)); at += best;
                }
                else { flags |= (byte)(1 << bit); output.WriteByte(input[at++]); }
            }
            long end = output.Position; output.Position = flagAt; output.WriteByte(flags); output.Position = end;
        }
        byte[] packed = output.ToArray();
        // HQRM_Load expands in place with only 500 extra bytes. Check every unread
        // byte before allowing this stream into an archive, including literal-heavy input.
        int src = 0, dst = 0, sourceStart = input.Length - packed.Length + 500;
        if (sourceStart < 0) throw new InvalidDataException("Compressed body exceeds the classic loader's recovery space.");
        while (dst < input.Length)
        {
            int flags = packed[src++];
            for (int bit = 0; bit < 8 && dst < input.Length; bit++, flags >>= 1)
            {
                int count;
                if ((flags & 1) != 0) { src++; count = 1; }
                else { count = (packed[src] & 15) + 2; src += 2; }
                dst += count;
                if (src < packed.Length && dst > sourceStart + src)
                    throw new InvalidDataException("Compressed body would overwrite unread data in the classic loader.");
            }
        }
        return packed;
    }
}
