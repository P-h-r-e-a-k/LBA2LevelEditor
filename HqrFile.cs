using System.Buffers.Binary;
using System.IO;

namespace LBA2LevelEditor;

// An HQR archive held as a table of slots, so entries can be replaced, added, cleared and removed and the file
// written back. HqrArchive stays the read-only view; HqrWriter.ReplaceEntry is the older single-entry helper.
//
// Layout: a table of little-endian u32 offsets (one per slot, then one trailing word holding the file size), then
// the entries, each [u32 size][u32 stored size][u16 method][payload]. A zero offset is an empty slot. The table is
// as long as the first entry's offset says, which normally equals the first non-zero offset.
//
// Parse keeps each entry's whole extent (everything up to the next entry's start, padding included) and slots that
// share one offset stay shared, so Parse -> ToBytes reproduces a file exactly when its entries are stored in
// ascending slot order with nothing but the table before the first entry (the retail files).
internal sealed class HqrFile
{
    public sealed class Slot
    {
        // The entry's bytes from its offset to the next entry (or the end of the file); null = empty slot.
        public byte[]? Extent;
        // >= 0: this slot shares another slot's data (same offset in the table).
        public int AliasOf = -1;
    }

    public List<Slot> Slots { get; } = new();

    // Bytes between the table and the first entry, kept so unusual files survive a round trip.
    public byte[] Gap { get; private set; } = Array.Empty<byte>();

    // The slot count of the table of `hqr` (how many entries it can address).
    public static int CountSlots(byte[] hqr) => ReadTable(hqr).Offsets.Length - 1;

    public static HqrFile Parse(byte[] hqr)
    {
        var (offsets, tableBytes) = ReadTable(hqr);
        var slots = offsets.Length - 1;
        var file = new HqrFile();

        var distinct = offsets.Take(slots).Where(o => o != 0).Distinct().OrderBy(o => o).ToList();
        foreach (var o in distinct)
            if (o < tableBytes || o > hqr.Length) throw new InvalidDataException($"HQR offset {o} lies outside the file.");
        if (distinct.Count > 0 && distinct[0] > tableBytes) file.Gap = hqr.AsSpan(tableBytes, (int)distinct[0] - tableBytes).ToArray();

        var firstSlotOf = new Dictionary<uint, int>();
        for (var i = 0; i < slots; i++)
        {
            var o = offsets[i];
            var slot = new Slot();
            if (o != 0)
            {
                if (firstSlotOf.TryGetValue(o, out var first)) slot.AliasOf = first;
                else
                {
                    firstSlotOf[o] = i;
                    var next = distinct.FirstOrDefault(d => d > o);
                    var end = next == 0 ? hqr.Length : (int)next;
                    slot.Extent = hqr.AsSpan((int)o, end - (int)o).ToArray();
                }
            }
            file.Slots.Add(slot);
        }
        return file;
    }

    // The table: offsets up to where the first entry starts. Returns the words (including the trailing size word)
    // and the table's length in bytes.
    private static (uint[] Offsets, int TableBytes) ReadTable(byte[] hqr)
    {
        if (hqr.Length < 8) throw new InvalidDataException("The HQR file is too small.");
        var words = new List<uint>();
        uint smallestOffset = uint.MaxValue;
        for (var i = 0; i * 4 + 4 <= hqr.Length; i++)
        {
            if ((uint)(i * 4) >= smallestOffset) break;
            var w = BinaryPrimitives.ReadUInt32LittleEndian(hqr.AsSpan(i * 4));
            words.Add(w);
            if (w != 0 && w < smallestOffset) smallestOffset = w;
            if (i > 100_000) throw new InvalidDataException("The HQR table is implausibly long.");
        }
        if (smallestOffset == uint.MaxValue) throw new InvalidDataException("The HQR file has no entries.");
        // The last word of the table is the file size, not an entry offset; it is >= smallestOffset, so the loop
        // above stops at the first entry, which is where the table ends.
        return (words.ToArray(), words.Count * 4);
    }

    public int Count => Slots.Count;

    public bool IsEmpty(int index) => Slots[index].Extent is null && Slots[index].AliasOf < 0;

    private byte[] ExtentOf(int index)
    {
        var slot = Slots[index];
        if (slot.AliasOf >= 0) slot = Slots[slot.AliasOf];
        return slot.Extent ?? throw new InvalidDataException($"HQR entry {index} is an empty slot.");
    }

    // The decoded payload of an entry.
    public byte[] Read(int index) => HqrArchive.DecodeEntry(ExtentOf(index));

    // Replaces an entry with a complete entry (header included, e.g. from HqrWriter.StoredEntry). A slot that shared
    // its data with another slot gets its own copy.
    public void SetEntry(int index, byte[] entry)
    {
        var slot = Slots[index];
        if (slot.AliasOf < 0 && slot.Extent is not null)
        {
            // slots that shared this entry keep the old data: the first of them takes it over
            var dependents = Enumerable.Range(0, Slots.Count).Where(i => Slots[i].AliasOf == index).ToList();
            if (dependents.Count > 0)
            {
                Slots[dependents[0]].Extent = slot.Extent;
                Slots[dependents[0]].AliasOf = -1;
                foreach (var d in dependents.Skip(1)) Slots[d].AliasOf = dependents[0];
            }
        }
        slot.AliasOf = -1;
        slot.Extent = entry;
    }

    public void SetStored(int index, byte[] payload) => SetEntry(index, HqrWriter.StoredEntry(payload));

    // Appends a stored entry; returns its slot index.
    public int Add(byte[] payload)
    {
        Slots.Add(new Slot { Extent = HqrWriter.StoredEntry(payload) });
        return Slots.Count - 1;
    }

    // Makes a slot empty (its offset becomes 0); other slots keep their numbers.
    public void Clear(int index)
    {
        if (Slots.Any(s => s.AliasOf == index)) throw new InvalidOperationException($"Other slots share entry {index}'s data.");
        Slots[index].Extent = null;
        Slots[index].AliasOf = -1;
    }

    // Removes a slot; every later slot's number goes down by one.
    public void RemoveAt(int index)
    {
        if (Slots.Any(s => s.AliasOf == index)) throw new InvalidOperationException($"Other slots share entry {index}'s data.");
        Slots.RemoveAt(index);
        foreach (var s in Slots)
            if (s.AliasOf > index) s.AliasOf--;
    }

    public byte[] ToBytes()
    {
        var tableBytes = (Slots.Count + 1) * 4;
        var offsets = new uint[Slots.Count + 1];
        var position = tableBytes + Gap.Length;
        for (var i = 0; i < Slots.Count; i++)
        {
            var slot = Slots[i];
            if (slot.AliasOf >= 0) continue;
            if (slot.Extent is null) continue;
            offsets[i] = (uint)position;
            position += slot.Extent.Length;
        }
        for (var i = 0; i < Slots.Count; i++)
            if (Slots[i].AliasOf >= 0) offsets[i] = offsets[Slots[i].AliasOf];
        offsets[^1] = (uint)position;

        var result = new byte[position];
        for (var i = 0; i < offsets.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), offsets[i]);
        Gap.CopyTo(result, tableBytes);
        for (var i = 0; i < Slots.Count; i++)
        {
            var slot = Slots[i];
            if (slot.AliasOf >= 0 || slot.Extent is null) continue;
            slot.Extent.CopyTo(result, (int)offsets[i]);
        }
        return result;
    }
}
