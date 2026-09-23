using System.Buffers.Binary;
using System.IO;

namespace LBAAssembler;

// Writes HQR archives (the read side is HqrArchive). An HQR is a table of
// little-endian u32 offsets -- its first word is the table's own size in bytes,
// so entry count = size/4 - 1 -- followed by the entries, each being
// [u32 uncompressedSize][u32 storedSize][u16 method][payload]. A zero offset
// marks an unused slot.
//
// Replacing one entry means every entry stored after it moves, so the table is
// rewritten; entries themselves are copied byte-for-byte (including any padding
// that follows them) so nothing else in the file changes.
internal static class HqrWriter
{
    // A stored (method 0, uncompressed) entry holding `data`. The engine's HQR
    // loader accepts method 0, and HqrArchive.Read does too.
    public static byte[] StoredEntry(byte[] data)
    {
        var entry = new byte[10 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), 0);
        data.CopyTo(entry.AsSpan(10));
        return entry;
    }

    // A compressed entry (LZ method 1 or 2) when that is smaller AND the engine can decompress it in place;
    // otherwise a stored entry. Game files written by the editor use stored entries (the engine loads both), so
    // this is for callers that want the file kept small.
    public static byte[] CompressedEntry(byte[] data, int method = 1)
    {
        var packed = HqrLz.Compress(data, method);
        if (packed.Length >= data.Length || !HqrLz.IsInPlaceSafe(packed, data.Length, method)) return StoredEntry(data);
        var entry = new byte[10 + packed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)packed.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), (ushort)method);
        packed.CopyTo(entry.AsSpan(10));
        return entry;
    }

    // Returns a copy of `hqr` with one new entry appended past the end (index = the old entry count), growing the
    // offset table by one slot. Only the table itself (4 bytes) and the trailing sentinel move; every existing
    // entry's own bytes stay exactly where they are relative to each other, just shifted down by those 4 bytes.
    public static byte[] AppendEntry(byte[] hqr, byte[] newEntry)
    {
        if (hqr.Length < 4) throw new InvalidDataException("The HQR file is too small.");
        var oldTableBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(hqr);
        var oldSlots = oldTableBytes / 4;
        if (oldTableBytes < 4 || oldTableBytes > hqr.Length) throw new InvalidDataException("Invalid HQR directory.");

        // The old table's own last slot was the sentinel (== hqr.Length); after the table grows by 4 bytes, that
        // same position becomes the new entry's real offset, and a fresh sentinel goes after it.
        var newEntryOffset = hqr.Length + 4;
        var result = new byte[newEntryOffset + newEntry.Length];
        for (var i = 0; i < oldSlots - 1; i++)
        {
            var o = BinaryPrimitives.ReadUInt32LittleEndian(hqr.AsSpan(i * 4));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), o == 0 ? 0 : o + 4);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan((oldSlots - 1) * 4), (uint)newEntryOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(oldSlots * 4), (uint)result.Length);
        hqr.AsSpan(oldTableBytes).CopyTo(result.AsSpan(oldTableBytes + 4));
        newEntry.CopyTo(result.AsSpan(newEntryOffset));
        return result;
    }

    // Returns a copy of `hqr` with entry `index` replaced by `newEntry` (a
    // complete entry including its 10-byte header, e.g. from StoredEntry).
    public static byte[] ReplaceEntry(byte[] hqr, int index, byte[] newEntry)
    {
        if (hqr.Length < 4) throw new InvalidDataException("The HQR file is too small.");
        var tableBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(hqr);
        var slots = tableBytes / 4;
        if (tableBytes < 8 || tableBytes > hqr.Length || (uint)index >= (uint)(slots - 1))
            throw new ArgumentOutOfRangeException(nameof(index), "No such HQR entry.");

        var offsets = new uint[slots];
        for (var i = 0; i < slots; i++) offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(hqr.AsSpan(i * 4));

        var start = (int)offsets[index];
        if (start == 0) throw new InvalidDataException($"HQR entry {index} is an empty slot; nothing to replace.");
        for (var i = 0; i < slots - 1; i++)
            if (i != index && offsets[i] == start)
                throw new InvalidDataException($"HQR entries {i} and {index} share the same data; refusing to replace one of them.");

        // The entry's extent runs to the next entry start (or end of file).
        var end = hqr.Length;
        for (var i = 0; i < slots - 1; i++)
            if (offsets[i] != 0 && offsets[i] > start && offsets[i] < end) end = (int)offsets[i];

        var delta = newEntry.Length - (end - start);
        var result = new byte[hqr.Length + delta];
        hqr.AsSpan(0, start).CopyTo(result);
        newEntry.CopyTo(result.AsSpan(start));
        hqr.AsSpan(end).CopyTo(result.AsSpan(start + newEntry.Length));

        // Everything located after the replaced entry moves by delta (this
        // includes the trailing slot when it records the file size).
        for (var i = 0; i < slots; i++)
        {
            var o = offsets[i];
            if (o > start) o = (uint)(o + delta);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), o);
        }
        return result;
    }
}
