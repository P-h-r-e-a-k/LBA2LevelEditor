using System.Buffers.Binary;
using System.IO;

namespace LBAAssembler;

// The LZ scheme of compressed HQR entries (methods 1 and 2), mirroring HqrArchive's decoder: groups of eight
// items led by a flag byte (bit set = literal byte, clear = back-reference), a back-reference being a u16 with
// (distance - 1) in bits 4..15 and (length - minBlock) in bits 0..3, minBlock = method + 1.
internal static class HqrLz
{
    private const int Window = 4096, MaxLengthExtra = 15;

    // The engine decompresses in place: the compressed bytes sit at the end of the destination buffer,
    // `size - stored + RecoverArea` bytes in, and the output must never overtake what is still unread.
    public const int RecoverArea = 500; // LBA1 500, LBA2 512; the smaller one is safe for both

    public static byte[] Compress(byte[] data, int method)
    {
        if (method is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(method));
        var minBlock = method + 1;
        var maxLength = minBlock + MaxLengthExtra;

        var output = new List<byte>(data.Length / 2 + 16);
        // position tables: the most recent occurrence of each 2-byte prefix, chained backwards
        var head = new int[65536];
        Array.Fill(head, -1);
        var previous = new int[data.Length];
        void Index(int at)
        {
            if (at + 1 >= data.Length) return;
            var key = data[at] | (data[at + 1] << 8);
            previous[at] = head[key];
            head[key] = at;
        }

        var flagsAt = -1;
        var bit = 8;
        void StartItem(bool literal)
        {
            if (bit == 8) { flagsAt = output.Count; output.Add(0); bit = 0; }
            if (literal) output[flagsAt] |= (byte)(1 << bit);
            bit++;
        }

        var i = 0;
        while (i < data.Length)
        {
            var bestLength = 0;
            var bestDistance = 0;
            if (i + 1 < data.Length)
            {
                var candidate = head[data[i] | (data[i + 1] << 8)];
                var tries = 64;
                while (candidate >= 0 && i - candidate <= Window && tries-- > 0)
                {
                    var length = 0;
                    while (length < maxLength && i + length < data.Length && data[candidate + length] == data[i + length]) length++;
                    if (length > bestLength) { bestLength = length; bestDistance = i - candidate; if (length == maxLength) break; }
                    candidate = previous[candidate];
                }
            }

            if (bestLength >= minBlock)
            {
                StartItem(literal: false);
                var token = (ushort)(((bestDistance - 1) << 4) | (bestLength - minBlock));
                output.Add((byte)token);
                output.Add((byte)(token >> 8));
                for (var k = 0; k < bestLength; k++) Index(i + k);
                i += bestLength;
            }
            else
            {
                StartItem(literal: true);
                output.Add(data[i]);
                Index(i);
                i++;
            }
        }
        return output.ToArray();
    }

    // True if decompressing `stored` (of `size` bytes decompressed) in place, the way the engine does it, never lets
    // the output run into compressed bytes it hasn't read yet.
    public static bool IsInPlaceSafe(byte[] stored, int size, int method)
    {
        var minBlock = method + 1;
        long start = size - stored.Length + RecoverArea;
        var read = 0;
        long written = 0;
        while (written < size && read < stored.Length)
        {
            var flags = stored[read++];
            for (var bit = 0; bit < 8 && written < size; bit++)
            {
                if ((flags & (1 << bit)) != 0)
                {
                    read++;
                    if (written >= start + read) return false;
                    written++;
                    continue;
                }
                var token = BinaryPrimitives.ReadUInt16LittleEndian(stored.AsSpan(read));
                read += 2;
                var length = (token & 0x0F) + minBlock;
                for (var k = 0; k < length && written < size; k++)
                {
                    if (written >= start + read) return false;
                    written++;
                }
            }
        }
        return true;
    }
}
