using System.IO;

namespace LBA2LevelEditor.Assets;

// The game's run-length picture ("graph"): bricks of both games and the sprites of LBA1. Header: width, height, hot-spot x, hot-spot y
// (signed bytes); then one record per line: a run count and the runs. A run's first byte holds its type in the top two bits (0 skip
// transparent pixels, 1 copy the next pixels, 2 / 3 repeat the next byte) and the length - 1 in the low six. Pixels are palette indices;
// a pixel not covered by a run is transparent.
internal sealed class GphImage
{
    public int Width { get; }
    public int Height { get; }
    public sbyte OffsetX { get; set; }
    public sbyte OffsetY { get; set; }
    // one palette index per pixel; Opaque says whether the pixel is drawn (index 0 is a real colour in these pictures)
    public byte[] Pixels { get; }
    public bool[] Opaque { get; }

    public GphImage(int width, int height)
    {
        if (width < 1 || width > 255 || height < 1 || height > 255) throw new ArgumentOutOfRangeException(nameof(width), "A game picture is 1..255 pixels each way.");
        Width = width; Height = height;
        Pixels = new byte[width * height]; Opaque = new bool[width * height];
    }

    public static GphImage Decode(byte[] data, int start = 0)
    {
        if (data.Length < start + 4) throw new InvalidDataException("The picture is too short.");
        var image = new GphImage(Math.Max(1, (int)data[start]), Math.Max(1, (int)data[start + 1])) { OffsetX = (sbyte)data[start + 2], OffsetY = (sbyte)data[start + 3] };
        var src = start + 4;
        for (var line = 0; line < image.Height; line++)
        {
            if (src >= data.Length) throw new InvalidDataException("The picture ends in the middle of a line.");
            int runs = data[src++];
            var x = 0;
            for (var r = 0; r < runs; r++)
            {
                if (src >= data.Length) throw new InvalidDataException("The picture ends in the middle of a run.");
                var control = data[src++];
                var count = (control & 0x3F) + 1;
                switch (control >> 6)
                {
                    case 0: x += count; break;
                    case 1:
                        for (var k = 0; k < count; k++)
                        {
                            if (src >= data.Length) throw new InvalidDataException("The picture ends in the middle of a run.");
                            image.Put(x++, line, data[src++]);
                        }
                        break;
                    default:
                        if (src >= data.Length) throw new InvalidDataException("The picture ends in the middle of a run.");
                        var colour = data[src++];
                        for (var k = 0; k < count; k++) image.Put(x++, line, colour);
                        break;
                }
            }
        }
        image.Length = src - start;
        return image;
    }

    // The raw sprites of LBA2 (SPRIRAW.HQR): width, height, hot spot x, y, then width x height palette indices, row by row; index 0 is transparent.
    public static GphImage DecodeRaw(byte[] data, int start = 0)
    {
        if (data.Length < start + 4) throw new InvalidDataException("The picture is too short.");
        int width = data[start], height = data[start + 1];
        if (width < 1 || height < 1 || data.Length < start + 4 + width * height) throw new InvalidDataException("The raw picture is shorter than its size says.");
        var image = new GphImage(width, height) { OffsetX = (sbyte)data[start + 2], OffsetY = (sbyte)data[start + 3] };
        for (var i = 0; i < width * height; i++) { image.Pixels[i] = data[start + 4 + i]; image.Opaque[i] = image.Pixels[i] != 0; }
        image.Length = 4 + width * height;
        return image;
    }

    public byte[] EncodeRaw()
    {
        var output = new byte[4 + Width * Height];
        output[0] = (byte)Width; output[1] = (byte)Height; output[2] = (byte)OffsetX; output[3] = (byte)OffsetY;
        for (var i = 0; i < Width * Height; i++) output[4 + i] = Opaque[i] ? Pixels[i] : (byte)0;
        return output;
    }

    // Bytes the picture occupied when it was decoded (so a caller can find what follows it).
    public int Length { get; private set; }

    private void Put(int x, int y, byte index)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        Pixels[y * Width + x] = index; Opaque[y * Width + x] = true;
    }

    public void Set(int x, int y, byte index, bool opaque = true) { Pixels[y * Width + x] = index; Opaque[y * Width + x] = opaque; }

    public byte[] Encode()
    {
        var output = new List<byte> { (byte)Width, (byte)Height, (byte)OffsetX, (byte)OffsetY };
        for (var y = 0; y < Height; y++)
        {
            // the runs of this line; trailing transparent pixels are not written
            var runs = new List<byte[]>();
            var x = 0;
            var end = Width;
            while (end > 0 && !Opaque[y * Width + end - 1]) end--;
            while (x < end)
            {
                var i = y * Width + x;
                if (!Opaque[i])
                {
                    var n = 0;
                    while (x + n < end && !Opaque[y * Width + x + n] && n < 64) n++;
                    runs.Add(new[] { (byte)(n - 1) });
                    x += n;
                    continue;
                }
                // a repeat of at least three identical pixels is cheaper as a repeat run
                var same = 1;
                while (x + same < end && Opaque[y * Width + x + same] && Pixels[y * Width + x + same] == Pixels[i] && same < 64) same++;
                if (same >= 3) { runs.Add(new[] { (byte)(0x80 | (same - 1)), Pixels[i] }); x += same; continue; }
                // otherwise copy pixels until the next repeat of three or the end of the opaque stretch
                var copy = new List<byte>();
                while (x < end && Opaque[y * Width + x] && copy.Count < 64)
                {
                    var here = y * Width + x;
                    var ahead = 1;
                    while (x + ahead < end && Opaque[y * Width + x + ahead] && Pixels[y * Width + x + ahead] == Pixels[here] && ahead < 3) ahead++;
                    if (ahead >= 3 && copy.Count > 0) break;
                    copy.Add(Pixels[here]); x++;
                }
                var run = new byte[copy.Count + 1];
                run[0] = (byte)(0x40 | (copy.Count - 1));
                copy.CopyTo(run, 1);
                runs.Add(run);
            }
            if (runs.Count > 255) throw new InvalidDataException("A line needs more than 255 runs.");
            output.Add((byte)runs.Count);
            foreach (var run in runs) output.AddRange(run);
        }
        return output.ToArray();
    }

    // BGRA pixels through a palette (768 bytes of RGB, 8-bit or the DOS 6-bit scale).
    public byte[] ToBgra(byte[] palette)
    {
        var sixBit = palette.Take(768).Max() <= 63;
        var bgra = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            if (!Opaque[i]) continue;
            var p = Pixels[i] * 3;
            bgra[i * 4] = Scale(palette[p + 2]); bgra[i * 4 + 1] = Scale(palette[p + 1]); bgra[i * 4 + 2] = Scale(palette[p]); bgra[i * 4 + 3] = 255;
            byte Scale(byte v) => sixBit ? (byte)Math.Min(255, v * 4) : v;
        }
        return bgra;
    }

    // Builds a picture from BGRA pixels: every pixel becomes the nearest palette colour, pixels with alpha under 128 stay transparent.
    public static GphImage FromBgra(byte[] bgra, int width, int height, byte[] palette, sbyte offsetX = 0, sbyte offsetY = 0, bool avoidZero = false)
    {
        var image = new GphImage(width, height) { OffsetX = offsetX, OffsetY = offsetY };
        var sixBit = palette.Take(768).Max() <= 63;
        var colours = new (int R, int G, int B)[256];
        for (var i = 0; i < 256; i++) colours[i] = (palette[i * 3] * (sixBit ? 4 : 1), palette[i * 3 + 1] * (sixBit ? 4 : 1), palette[i * 3 + 2] * (sixBit ? 4 : 1));
        for (var i = 0; i < width * height; i++)
        {
            if (bgra[i * 4 + 3] < 128) continue;
            int b = bgra[i * 4], g = bgra[i * 4 + 1], r = bgra[i * 4 + 2];
            var best = 0; var bestDistance = int.MaxValue;
            for (var c = avoidZero ? 1 : 0; c < 256; c++)
            {
                var d = (colours[c].R - r) * (colours[c].R - r) + (colours[c].G - g) * (colours[c].G - g) + (colours[c].B - b) * (colours[c].B - b);
                if (d < bestDistance) { bestDistance = d; best = c; if (d == 0) break; }
            }
            image.Pixels[i] = (byte)best; image.Opaque[i] = true;
        }
        return image;
    }
}
