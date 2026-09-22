using System.IO;
using System.Text;
using LBAAssembler.Scenes;

namespace LBAAssembler.Lba1;

// New texts for LBA1's TEXT.HQR, added as the last texts of Principal Island's dialogue file (scene 61, the bedroom, is on that island; so are the
// fishermen of scene 24) in all five languages.
//
// TEXT.HQR is 5 languages x 14 files x (order, text) entries, language 0 = English, then French, German, Spanish, Italian. Only the bank of the scene's
// island is loaded, into fixed engine buffers (25,000 bytes of text, 512 ids). A text is found by id and its voice by its position, and every voice
// table has exactly one slot per retail text, so a text appended at the end is silent and moves no voice. The game's text is in the DOS code page
// (CP 850: an e-acute is 0x82), not Latin-1.
internal static class Lba1DialogueText
{
    public const int PrincipalTextFile = 4, Languages = 5, FilesPerLanguage = 14;
    private const int MaxTextBytes = 25000, MaxTextIds = 512;   // the engine's dialogue buffers (BufText, BufOrder)

    // One text to add. Wanted gives the bytes (with the closing NUL) for a language, or null for the English text as it is.
    // Own: an existing text under the id may be replaced when it is still what an earlier version wrote (the English text standing in for a translation).
    public sealed record AddedText(int Id, string What, string English, Func<HqrFile, int, byte[]?>? Localised = null, bool Own = false);

    // The DOS code page of the game's dialogue. (Not part of .NET's default set.)
    public static Encoding DosEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(850);
        }
    }

    public static byte[] Bytes(string text) => DosEncoding.GetBytes(text + "\0");

    // Returns the TEXT.HQR entries to write, or nothing when it already has every text. A bank that has an id
    // with other text, or no room left, is refused.
    public static IReadOnlyList<HqrEntryStore.Edit> Plan(string directory, IReadOnlyList<AddedText> added)
    {
        var path = Path.Combine(directory, "TEXT.HQR");
        if (!File.Exists(path)) throw new InvalidDataException("TEXT.HQR must be in the game folder.");
        var texts = HqrFile.Parse(File.ReadAllBytes(path));
        if (texts.Count < Languages * FilesPerLanguage * 2) throw new InvalidDataException("TEXT.HQR isn't the game's dialogue file (too few entries).");

        var edited = new Dictionary<int, byte[]>();
        for (var language = 0; language < Languages; language++)
        {
            var at = (language * FilesPerLanguage + PrincipalTextFile) * 2;
            var (ids, strings) = Split(texts.Read(at), texts.Read(at + 1));
            var changed = false;
            foreach (var text in added)
            {
                var english = Bytes(text.English);
                var wanted = language == 0 ? english : text.Localised?.Invoke(texts, language) ?? english;
                var existing = ids.IndexOf((ushort)text.Id);
                if (existing >= 0)
                {
                    if (strings[existing].AsSpan().SequenceEqual(wanted)) continue;
                    if (!text.Own || !strings[existing].AsSpan().SequenceEqual(english))
                        throw new InvalidDataException($"Principal Island's dialogue (language {language}) already has a text with id {text.Id} that isn't {text.What}. Restore TEXT.HQR from the .bak copy and run this again.");
                    strings[existing] = wanted;
                }
                else
                {
                    ids.Add((ushort)text.Id);
                    strings.Add(wanted);
                }
                changed = true;
            }
            if (!changed) continue;
            var (order, data) = Join(ids, strings);
            if (ids.Count > MaxTextIds || data.Length > MaxTextBytes) throw new InvalidDataException($"Principal Island's dialogue (language {language}) has no room for another text ({ids.Count} ids of {MaxTextIds}, {data.Length} bytes of {MaxTextBytes}).");
            edited[at] = order;
            edited[at + 1] = data;
        }
        if (edited.Count == 0) return Array.Empty<HqrEntryStore.Edit>();
        return edited.Select(e => new HqrEntryStore.Edit("TEXT.HQR", e.Key, e.Value)).ToList();
    }

    // A dialogue file is two entries: the text ids in storage order, and (count + 1) U16 start offsets followed by the NUL-terminated strings.
    public static (List<ushort> Ids, List<byte[]> Strings) Split(byte[] order, byte[] text)
    {
        var ids = new List<ushort>();
        var strings = new List<byte[]>();
        for (var i = 0; i < order.Length / 2; i++)
        {
            ids.Add(BitConverter.ToUInt16(order, i * 2));
            int start = BitConverter.ToUInt16(text, i * 2), end = BitConverter.ToUInt16(text, i * 2 + 2);
            strings.Add(text[start..end]);
        }
        return (ids, strings);
    }

    public static (byte[] Order, byte[] Text) Join(List<ushort> ids, List<byte[]> strings)
    {
        var order = new byte[ids.Count * 2];
        for (var i = 0; i < ids.Count; i++) BitConverter.TryWriteBytes(order.AsSpan(i * 2), ids[i]);
        var text = new List<byte>();
        var offset = (strings.Count + 1) * 2;
        var table = new byte[offset];
        for (var i = 0; i < strings.Count; i++)
        {
            BitConverter.TryWriteBytes(table.AsSpan(i * 2), (ushort)offset);
            offset += strings[i].Length;
        }
        BitConverter.TryWriteBytes(table.AsSpan(strings.Count * 2), (ushort)offset);
        text.AddRange(table);
        foreach (var s in strings) text.AddRange(s);
        return (order, text.ToArray());
    }
}
