using System.Text;

namespace LBAAssembler.Lba1.Runtime;

// One dialogue file of TEXT.HQR (MESSAGE.C InitDial): the entry pair "order" (the U16 text ids in the order they are stored)
// and "text" (U16 start offsets, then the strings). Text in the game is plain 8-bit characters, '@' starts a new line and
// "@P" a new page.
internal sealed class Lba1TextBank
{
    private readonly ushort[] order;
    private readonly byte[] data;

    public Lba1TextBank(byte[] orderEntry, byte[] textEntry)
    {
        order = new ushort[orderEntry.Length / 2];
        for (var i = 0; i < order.Length; i++) order[i] = BitConverter.ToUInt16(orderEntry, i * 2);
        data = textEntry;
    }

    public int Count => order.Length;

    // Where a text sits in the file (FindText): also its number in the matching voice file.
    public int IndexOf(int id) => Array.IndexOf(order, (ushort)id);
    public IReadOnlyList<int> Ids => order.Select(i => (int)i).ToList();

    public string? Get(int id)
    {
        var index = Array.IndexOf(order, (ushort)id);
        if (index < 0 || (index + 1) * 2 + 2 > data.Length) return null;
        int start = BitConverter.ToUInt16(data, index * 2);
        int end = BitConverter.ToUInt16(data, (index + 1) * 2);
        if (start >= data.Length) return null;
        end = Math.Min(end, data.Length);
        var length = 0;
        while (start + length < end && data[start + length] != 0) length++;
        var text = Encoding.Latin1.GetString(data, start, length);
        return text.Replace("@P", "\n\n").Replace('@', '\n').Replace('', '\n');
    }

    // TEXT.HQR holds 14 files (7 pairs) for every language: general, ..., one per island from file 3 on.
    public const int FilesPerLanguage = 14;

    public static Lba1TextBank? Load(HqrArchive texts, int language, int file)
    {
        var order = language * FilesPerLanguage * 2 + file * 2;
        return texts.IsValid(order) && texts.IsValid(order + 1) ? new Lba1TextBank(texts.Read(order), texts.Read(order + 1)) : null;
    }
}
