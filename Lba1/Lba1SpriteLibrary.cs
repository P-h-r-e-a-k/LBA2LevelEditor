using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LBA2LevelEditor.Lba1;

// The sprites of SPRITES.HQR (doors, keys, boxes, bonus items ...), drawn in the same run-length format as bricks:
// width, height, hot-spot x and y (signed, relative to the sprite's anchor), then the lines.
internal sealed class Lba1SpriteLibrary
{
    public sealed record Sprite(BitmapSource Image);

    private readonly HqrArchive? archive;
    private readonly byte[] palette;
    private readonly Dictionary<int, Sprite?> cache = new();

    public Lba1SpriteLibrary(string directory, byte[] palette)
    {
        this.palette = palette;
        var path = Path.Combine(directory, "SPRITES.HQR");
        if (File.Exists(path)) archive = HqrArchive.Open(path);
    }

    // The shadow pictures of RESS.HQR entry 4: a table of offsets, then graphs like sprites (width, height, hot spot, lines).
    // They are a dither of one dark colour, which is how the game shades the ground under actors.
    private readonly Dictionary<int, Sprite?> shadows = new();
    private byte[]? shadowEntry;

    public Sprite? Shadow(int number, string directory)
    {
        if (shadows.TryGetValue(number, out var cached)) return cached;
        Sprite? shadow = null;
        try
        {
            shadowEntry ??= HqrArchive.Open(Path.Combine(directory, "RESS.HQR")).Read(4);
            var table = shadowEntry;
            var count = BitConverter.ToUInt32(table, 0) / 4;
            if (number >= 0 && number < count)
            {
                int start = (int)BitConverter.ToUInt32(table, number * 4);
                int end = number + 1 < count ? (int)BitConverter.ToUInt32(table, (number + 1) * 4) : table.Length;
                var data = table[start..end];
                if (data.Length >= 4 && data[0] > 0 && data[1] > 0)
                {
                    var bgra = new byte[data[0] * data[1] * 4];
                    Lba1GridRenderer.Blit(bgra, data[0], data[1], data, 0, 0, palette);
                    var bitmap = BitmapSource.Create(data[0], data[1], 96, 96, PixelFormats.Bgra32, null, bgra, data[0] * 4);
                    bitmap.Freeze();
                    shadow = new Sprite(bitmap);
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException)
        {
            DebugLog.Log($"Lba1SpriteLibrary: shadow {number} unreadable: {error.Message}");
        }
        return shadows[number] = shadow;
    }

    public Sprite? Get(int index)
    {
        if (cache.TryGetValue(index, out var cached)) return cached;
        Sprite? sprite = null;
        try
        {
            if (archive is not null && archive.IsValid(index))
            {
                // the entry starts with the offset of the picture (8) and its size, then: width, height, 0, 0 and the lines
                var entry = archive.Read(index);
                var start = entry.Length >= 8 ? (int)BitConverter.ToUInt32(entry, 0) : entry.Length;
                var data = start + 4 <= entry.Length ? entry[start..] : Array.Empty<byte>();
                if (data.Length >= 4 && data[0] > 0 && data[1] > 0)
                {
                    var bgra = new byte[data[0] * data[1] * 4];
                    Lba1GridRenderer.Blit(bgra, data[0], data[1], data, 0, 0, palette);
                    var bitmap = BitmapSource.Create(data[0], data[1], 96, 96, PixelFormats.Bgra32, null, bgra, data[0] * 4);
                    bitmap.Freeze();
                    sprite = new Sprite(bitmap);
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException)
        {
            DebugLog.Log($"Lba1SpriteLibrary: sprite {index} unreadable: {error.Message}");
        }
        return cache[index] = sprite;
    }
}
