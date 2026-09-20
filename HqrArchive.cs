using System.Buffers.Binary;
using System.IO;

namespace LBA2LevelEditor;

internal sealed class HqrArchive
{
    private readonly byte[] data;
    private readonly uint[] offsets;

    private HqrArchive(byte[] data, uint[] offsets)
    {
        this.data = data;
        this.offsets = offsets;
    }

    public int Count => offsets.Length;
    public IEnumerable<int> ValidIndices => Enumerable.Range(0, offsets.Length).Where(IsValid);

    // Count is deliberately not used for this: it treats the raw offset-
    // table-length header value as the slot count directly instead of
    // dividing by 4 first, so it runs ~4x too high (confirmed against
    // BODY.HQR while building the actor attributes editor's Body picker).
    // This reads the same header and applies the real formula (matching
    // the native engine's own HQF_NbRes()) instead.
    public static int CountEntries(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4) return 0;
        var tableBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var slots = (int)(tableBytes / 4);
        return Math.Max(0, slots - 1);
    }

    public static HqrArchive Open(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 4) throw new InvalidDataException("The HQR file is too small.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count < 1 || count > data.Length / 4) throw new InvalidDataException("The HQR index is invalid.");
        var offsets = new uint[count];
        for (var index = 0; index < offsets.Length; index++)
        {
            offsets[index] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index * 4));
        }
        return new HqrArchive(data, offsets);
    }

    // The decompressed size an entry's header announces (no decoding), or -1 for an empty or invalid slot.
    public int DecodedSize(int index)
        => IsValid(index) ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)offsets[index]))) : -1;

    public bool IsValid(int index)
    {
        if ((uint)index >= offsets.Length || offsets[index] == 0 || offsets[index] > data.Length - 10) return false;
        var offset = (int)offsets[index];
        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
        return compressedSize <= data.Length - offset - 10;
    }

    public byte[] Read(int index)
    {
        if (!IsValid(index)) throw new ArgumentOutOfRangeException(nameof(index));
        var offset = checked((int)offsets[index]);
        var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)));
        var compressedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4)));
        var method = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 8));
        if (compressedSize < 0 || compressedSize > data.Length - offset - 10)
            throw new InvalidDataException($"HQR record {index} has an invalid payload size.");
        var source = data.AsSpan(offset + 10, compressedSize);
        if (method == 0) return source.ToArray();
        if (method is not (1 or 2)) throw new InvalidDataException($"Unsupported HQR compression method {method}.");
        // minBlockLength matches the native decoder's own MinBloc parameter
        // (LIB386/SYSTEM/LZ.CPP's ExpandLZ, called from HQFILE.CPP's
        // HQF_LoadClose as "ExpandLZ(ptr, ptrdecomp, SizeFile,
        // CompressMethod + 1)") -- a back-reference's block length is
        // (nibble + MinBloc), not always (nibble + 2); method 2 uses
        // MinBloc 3. Hardcoding +2 here previously decoded method-1 records
        // fine but desynced method-2 ones a nibble at a time, eventually
        // producing a distance bigger than what had been decoded so far.
        return DecodeLz(source, size, method + 1);
    }

    // Decodes one complete entry ([u32 size][u32 stored size][u16 method][payload], padding after it is ignored).
    internal static byte[] DecodeEntry(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < 10) throw new InvalidDataException("An HQR entry needs its 10-byte header.");
        var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry));
        var storedSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]));
        var method = BinaryPrimitives.ReadInt16LittleEndian(entry[8..]);
        if (storedSize < 0 || storedSize > entry.Length - 10) throw new InvalidDataException("The HQR entry has an invalid payload size.");
        var payload = entry.Slice(10, storedSize);
        if (method == 0) return payload.ToArray();
        if (method is not (1 or 2)) throw new InvalidDataException($"Unsupported HQR compression method {method}.");
        return DecodeLz(payload, size, method + 1);
    }

    private static byte[] DecodeLz(ReadOnlySpan<byte> source, int expectedSize, int minBlockLength)
    {
        var output = new byte[expectedSize];
        var sourceIndex = 0;
        var outputIndex = 0;
        while (outputIndex < output.Length && sourceIndex < source.Length)
        {
            var flags = source[sourceIndex++];
            for (var bit = 0; bit < 8 && outputIndex < output.Length; bit++)
            {
                if ((flags & (1 << bit)) != 0)
                {
                    if (sourceIndex >= source.Length) throw new InvalidDataException("Truncated LZ literal.");
                    output[outputIndex++] = source[sourceIndex++];
                    continue;
                }
                if (sourceIndex + 1 >= source.Length) throw new InvalidDataException("Truncated LZ back-reference.");
                var token = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(sourceIndex, 2));
                sourceIndex += 2;
                var distance = ((token >> 4) & 0x0FFF) + 1;
                var length = (token & 0x0F) + minBlockLength;
                if (distance > outputIndex) throw new InvalidDataException("Invalid LZ back-reference.");
                for (var copy = 0; copy < length && outputIndex < output.Length; copy++)
                    output[outputIndex] = output[outputIndex++ - distance];
            }
        }
        if (outputIndex != output.Length) throw new InvalidDataException("The LZ record decompressed to an unexpected size.");
        return output;
    }
}
