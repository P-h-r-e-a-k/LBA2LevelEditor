using System.IO.Compression;

namespace ScriptRoundTrip;

// A minimal PNG encoder (BGRA in, RGBA out) so the renderers can be looked at without WPF.
internal static class PngWriter
{
    public static void Write(string path, byte[] bgra, int width, int height)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        WriteBE(header, 0, width); WriteBE(header, 4, height);
        header[8] = 8; header[9] = 6;
        Chunk(file, "IHDR", header);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
        {
            var row = new byte[width * 4 + 1];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0;
                for (var x = 0; x < width; x++)
                {
                    var s = (y * width + x) * 4;
                    row[1 + x * 4] = bgra[s + 2]; row[2 + x * 4] = bgra[s + 1]; row[3 + x * 4] = bgra[s]; row[4 + x * 4] = bgra[s + 3];
                }
                z.Write(row);
            }
        }
        Chunk(file, "IDAT", raw.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    private static void WriteBE(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes); s.Write(data);
        var crc = Crc(typeBytes, data);
        var c = new byte[4]; WriteBE(c, 0, (int)crc);
        s.Write(c);
    }

    private static uint Crc(byte[] a, byte[] b)
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; table[n] = c; }
        var crc = 0xFFFFFFFFu;
        foreach (var x in a) crc = table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in b) crc = table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
