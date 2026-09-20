using System.Buffers.Binary;

namespace LBA2LevelEditor;

// The sound balance used when a scene is played, kept per game (the games' own default balance is off: the music drowns the
// speech). Levels are percentages of full volume.
public sealed class AudioLevels
{
    public bool Mute { get; set; }
    public int Music { get; set; } = 40;
    public int Voices { get; set; } = 90;
    public int Effects { get; set; } = 70;

    public AudioLevels Clone() => (AudioLevels)MemberwiseClone();

    public static int Clamp(int percent) => Math.Clamp(percent, 0, 100);

    // A percentage as the LBA2 engine's 0..127 volume.
    public static int ToEngine(int percent) => (int)Math.Round(Clamp(percent) * 127 / 100.0);
}

// Volume for LBA1's PCM sounds (effects, speech, the CD tracks): a WAV with its samples scaled.
internal static class PcmVolume
{
    public static byte[] Scale(byte[] wav, double factor)
    {
        if (factor >= 0.995 || wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return wav;
        int bits = 0, format = 0, dataAt = -1, dataLength = 0;
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, at, 4);
            var length = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(at + 4));
            if (id == "fmt " && at + 8 + 16 <= wav.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(at + 8));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(at + 22));
            }
            else if (id == "data") { dataAt = at + 8; dataLength = Math.Min(length < 0 ? int.MaxValue : length, wav.Length - dataAt); break; }
            at += 8 + length + (length & 1);
        }
        if (format != 1 || dataAt < 0 || bits is not (8 or 16)) return wav;
        var scaled = (byte[])wav.Clone();
        if (bits == 8)
            for (var i = dataAt; i < dataAt + dataLength; i++) scaled[i] = (byte)Math.Clamp((int)Math.Round((wav[i] - 128) * factor) + 128, 0, 255);
        else
            for (var i = dataAt; i + 1 < dataAt + dataLength; i += 2)
                BinaryPrimitives.WriteInt16LittleEndian(scaled.AsSpan(i), (short)Math.Clamp((int)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i)) * factor), short.MinValue, short.MaxValue));
        return scaled;
    }
}

// Volume for LBA1's MIDI music: every channel-volume controller (7) in the file is scaled, and each channel is given a scaled
// starting volume for music that never sets one.
internal static class MidiVolume
{
    public static byte[] Scale(byte[] midi, double factor)
    {
        if (factor >= 0.995 || midi.Length < 22 || midi[0] != 'M' || midi[1] != 'T' || midi[2] != 'h' || midi[3] != 'd') return midi;
        var result = new List<byte>(midi.Length + 64);
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(midi.AsSpan(4));
        var at = 8 + headerLength;
        result.AddRange(midi.AsSpan(0, Math.Min(at, midi.Length)).ToArray());
        var firstTrack = true;
        while (at + 8 <= midi.Length && midi[at] == 'M' && midi[at + 1] == 'T' && midi[at + 2] == 'r' && midi[at + 3] == 'k')
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(midi.AsSpan(at + 4));
            var end = Math.Min(at + 8 + length, midi.Length);
            var body = ScaleTrack(midi.AsSpan(at + 8, end - at - 8).ToArray(), factor, firstTrack);
            result.AddRange(midi.AsSpan(at, 4).ToArray());
            var lengthBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lengthBytes, body.Length);
            result.AddRange(lengthBytes);
            result.AddRange(body);
            firstTrack = false;
            at = end;
        }
        if (at < midi.Length) result.AddRange(midi.AsSpan(at).ToArray());
        return result.ToArray();
    }

    private static byte[] ScaleTrack(byte[] track, double factor, bool addStart)
    {
        var body = new List<byte>(track.Length + 48);
        if (addStart)
            for (var channel = 0; channel < 16; channel++) body.AddRange(new byte[] { 0, (byte)(0xB0 | channel), 7, (byte)Math.Clamp((int)Math.Round(100 * factor), 0, 127) });
        var i = 0;
        byte running = 0;
        while (i < track.Length)
        {
            // delta time
            while (i < track.Length) { var b = track[i]; body.Add(b); i++; if ((b & 0x80) == 0) break; }
            if (i >= track.Length) break;
            var status = track[i];
            if (status == 0xFF)                       // meta: type, length, data
            {
                body.Add(status); i++;
                if (i >= track.Length) break;
                body.Add(track[i]); i++;
                var (length, used) = ReadVlq(track, i);
                for (var k = 0; k < used; k++) body.Add(track[i + k]);
                i += used;
                for (var k = 0; k < length && i < track.Length; k++) body.Add(track[i++]);
                continue;
            }
            if (status is 0xF0 or 0xF7)               // sysex: length, data
            {
                body.Add(status); i++;
                var (length, used) = ReadVlq(track, i);
                for (var k = 0; k < used; k++) body.Add(track[i + k]);
                i += used;
                for (var k = 0; k < length && i < track.Length; k++) body.Add(track[i++]);
                continue;
            }
            if ((status & 0x80) != 0) { running = status; body.Add(status); i++; }
            else if (running == 0) { body.Add(track[i++]); continue; }                       // damaged: copy on
            var kind = running & 0xF0;
            var dataBytes = kind is 0xC0 or 0xD0 ? 1 : 2;
            for (var k = 0; k < dataBytes && i < track.Length; k++)
            {
                var value = track[i++];
                if (kind == 0xB0 && k == 1 && body.Count >= 1 && body[^1] == 7) value = (byte)Math.Clamp((int)Math.Round(value * factor), 0, 127);
                body.Add(value);
            }
        }
        return body.ToArray();
    }

    private static (int Length, int Used) ReadVlq(byte[] data, int at)
    {
        var value = 0; var used = 0;
        while (at + used < data.Length)
        {
            var b = data[at + used++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return (value, used);
    }
}
