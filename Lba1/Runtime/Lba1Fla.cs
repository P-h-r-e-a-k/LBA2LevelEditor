using System.IO;

namespace LBAAssembler.Lba1.Runtime;

// LBA1's films (.FLA, on the CD): a 16-byte header (version "V1.3", frame count, frames a second, size), a list of the
// sound effects used, then frames. A frame is a list of "passes": a palette, a sound to start or stop, an information code,
// or a picture change (a whole picture run-length coded, a delta of changed lines, a raw copy, or black). PLAYFLA.C and
// ADFLI_A.C are the reference. The picture is 320 pixels wide, 8 bits per pixel, drawn into `Pixels`.
internal sealed class Lba1Fla
{
    public sealed record Sound(int Sample, int Shift, int Repeat, int LeftVolume, int RightVolume);

    public int FrameCount { get; }
    public int FramesPerSecond { get; }
    public int Width => 320;
    public int Height { get; }
    public byte[] Pixels { get; }
    public byte[] Palette { get; } = new byte[768];
    public int FrameIndex { get; private set; }

    // What the last frame decoded asked for besides drawing.
    public List<Sound> Sounds { get; } = new();
    public List<int> StoppedSamples { get; } = new();   // -1 = all
    public List<int> Infos { get; } = new();            // 1 flute music, 2 fade to black, 3 fade in, 4 fade the music out
    public bool PaletteChanged { get; private set; }

    private readonly byte[] data;
    private int position;

    public Lba1Fla(byte[] data)
    {
        this.data = data;
        if (data.Length < 32 || data[0] != 'V' || data[1] != '1' || data[2] != '.' || data[3] != '3') throw new InvalidDataException("This isn't an LBA1 film (V1.3).");
        FrameCount = (int)BitConverter.ToUInt32(data, 6);
        FramesPerSecond = Math.Max(1, (int)data[10]);
        Height = Math.Clamp((int)BitConverter.ToUInt16(data, 14), 1, 480);
        Pixels = new byte[Width * Height];
        var samples = BitConverter.ToUInt16(data, 16);
        SampleNumbers = Enumerable.Range(0, samples).Select(i => (int)BitConverter.ToUInt16(data, 20 + i * 4)).ToList();
        position = 20 + samples * 4;
    }

    // The sound effects (FLASAMP.HQR entries) the film uses.
    public List<int> SampleNumbers { get; }

    public bool AtEnd => FrameIndex >= FrameCount || position + 8 > data.Length;

    // Decodes the next frame into Pixels / Palette. False when the film is over.
    public bool NextFrame()
    {
        if (AtEnd) return false;
        Sounds.Clear(); StoppedSamples.Clear(); Infos.Clear();
        PaletteChanged = false;

        int passes = data[position];
        int size = (int)BitConverter.ToUInt32(data, position + 2);
        var frame = position + 6;      // the pass header is packed to 6 bytes: count, then a 32-bit size at offset 2
        position = frame + size;
        var p = frame;

        for (var i = 0; i < passes && p + 4 <= data.Length; i++)
        {
            int type = data[p];
            int next = BitConverter.ToUInt16(data, p + 2);
            var d = p + 4;
            switch (type)
            {
                case 1:   // palette: count, first colour, then RGB triples
                    {
                        int count = BitConverter.ToUInt16(data, d), first = BitConverter.ToUInt16(data, d + 2);
                        if (count == 0) count = 256;
                        Array.Copy(data, d + 4, Palette, first * 3, Math.Min(count * 3, Palette.Length - first * 3));
                        PaletteChanged = true;
                    }
                    break;
                case 3:   // a sound effect
                    Sounds.Add(new Sound(BitConverter.ToUInt16(data, d), BitConverter.ToUInt16(data, d + 2), BitConverter.ToUInt16(data, d + 4), data[d + 7], data[d + 8]));
                    break;
                case 5:   // stop a sound
                    StoppedSamples.Add((short)BitConverter.ToUInt16(data, d));
                    break;
                case 2:   // information
                    Infos.Add(BitConverter.ToUInt16(data, d));
                    break;
                case 7: Array.Clear(Pixels); break;                                     // black
                case 8: RunLengthFrame(d); break;                                       // a whole picture, run-length coded
                case 6: DeltaFrame(d); break;                                           // only the lines that changed
                case 9:
                case 16: Array.Copy(data, d, Pixels, 0, Math.Min(Pixels.Length, data.Length - d)); break;   // raw
            }
            p = d + next;
        }
        FrameIndex++;
        return true;
    }

    // ADFLI_A.C DrawFrame: each line a run count, then runs: a negative count copies that many bytes, a positive one repeats a byte.
    private void RunLengthFrame(int p)
    {
        var dst = 0;
        for (var line = 0; line < Height && p < data.Length; line++)
        {
            int blocks = data[p++];
            while (blocks-- > 0 && p < data.Length)
            {
                var count = (sbyte)data[p++];
                if (count < 0)
                {
                    var n = -count;
                    for (var k = 0; k < n && dst < Pixels.Length && p < data.Length; k++) Pixels[dst++] = data[p++];
                }
                else
                {
                    var value = data[p++];
                    for (var k = 0; k < count && dst < Pixels.Length; k++) Pixels[dst++] = value;
                }
            }
            dst = (line + 1) * Width;
        }
    }

    // ADFLI_A.C UpdateFrame: first line and line count, then per line blocks of (skip, count) with a literal run or, for a
    // negative count, a repeated byte.
    private void DeltaFrame(int p)
    {
        int line = data[p] | data[p + 1] << 8, lines = data[p + 2] | data[p + 3] << 8;
        p += 4;
        while (lines-- > 0 && p < data.Length)
        {
            var dst = line * Width;
            int blocks = data[p++];
            while (blocks-- > 0 && p + 1 < data.Length)
            {
                dst += data[p++];
                var count = (sbyte)data[p++];
                if (count < 0)
                {
                    var value = data[p++];
                    for (var k = 0; k < -count && dst < Pixels.Length; k++) Pixels[dst++] = value;
                }
                else
                {
                    for (var k = 0; k < count && dst < Pixels.Length && p < data.Length; k++) Pixels[dst++] = data[p++];
                }
            }
            line++;
        }
    }
}
