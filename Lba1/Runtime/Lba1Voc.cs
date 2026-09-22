using System.IO;

namespace LBAAssembler.Lba1.Runtime;

// LBA1's sound effects (SAMPLES.HQR) are Creative Voice Files. This turns one into a plain PCM WAV that Windows can play:
// 8-bit unsigned mono blocks joined together, at the rate of the first data block.
internal static class Lba1Voc
{
    private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("Creative Voice File\x1A");

    public static byte[]? ToWav(byte[] voc)
    {
        // the first byte of a game sample is a flag (0 or the 'C' of "Creative"), so only the rest is compared
        if (voc.Length < 26 || !voc.AsSpan(1, Magic.Length - 1).SequenceEqual(Magic.AsSpan(1))) return null;
        int at = BitConverter.ToUInt16(voc, 20);
        var pcm = new MemoryStream();
        var rate = 0;
        var pendingRate = 0;

        while (at + 4 <= voc.Length)
        {
            var type = voc[at];
            if (type == 0) break;
            var size = voc[at + 1] | voc[at + 2] << 8 | voc[at + 3] << 16;
            var body = at + 4;
            if (body + size > voc.Length) size = voc.Length - body;
            switch (type)
            {
                case 1 when size >= 2:   // sound data: frequency divisor, codec (0 = 8-bit unsigned), samples
                    if (voc[body + 1] != 0) return null;
                    pendingRate = 1_000_000 / (256 - voc[body]);
                    if (rate == 0) rate = pendingRate;
                    pcm.Write(voc, body + 2, size - 2);
                    break;
                case 2:                  // continuation of the last data block
                    pcm.Write(voc, body, size);
                    break;
                case 3 when size >= 3:   // silence
                    {
                        int length = (voc[body] | voc[body + 1] << 8) + 1;
                        for (var i = 0; i < length; i++) pcm.WriteByte(128);
                    }
                    break;
            }
            at = body + size;
        }
        if (rate == 0 || pcm.Length == 0) return null;

        var data = pcm.ToArray();
        var wav = new MemoryStream(44 + data.Length);
        var w = new BinaryWriter(wav);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate); w.Write((short)1); w.Write((short)8);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(data.Length);
        w.Write(data);
        return wav.ToArray();
    }
}
