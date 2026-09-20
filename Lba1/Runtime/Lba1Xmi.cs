using System.IO;

namespace LBA2LevelEditor.Lba1.Runtime;

// LBA1's music (MIDI_MI.HQR) is XMIDI, Miles Design's variant of a MIDI file: chunks inside IFF "FORM" wrappers, delays
// as plain bytes between events (120 ticks per second), and note-ons that carry their own length instead of a note-off.
// This turns the first sequence of one into a Standard MIDI File that Windows can play.
internal static class Lba1Xmi
{
    public static byte[]? ToMidi(byte[] xmi)
    {
        var events = FindEvents(xmi);
        if (events is null) return null;
        var (start, length) = events.Value;

        var list = new List<(long Time, int Order, byte[] Data)>();
        long time = 0;
        var p = start;
        var end = Math.Min(xmi.Length, start + length);
        var order = 0;
        long lastTime = 0;

        int Vlq()
        {
            var v = 0;
            for (var i = 0; i < 4 && p < end; i++)
            {
                var b = xmi[p++];
                v = v << 7 | (b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            return v;
        }

        while (p < end)
        {
            var b = xmi[p];
            if (b < 0x80) { time += b; p++; continue; }       // a delay
            p++;
            if (b == 0xFF)
            {
                var type = xmi[p++];
                var len = Vlq();
                var data = new byte[len];
                Array.Copy(xmi, p, data, 0, Math.Min(len, end - p));
                p += len;
                if (type == 0x2F) { lastTime = Math.Max(lastTime, time); break; }
                var meta = new List<byte> { 0xFF, type };
                meta.AddRange(WriteVlq(len));
                meta.AddRange(data);
                list.Add((time, order++, meta.ToArray()));
            }
            else if (b is 0xF0 or 0xF7)
            {
                var len = Vlq();
                var sysex = new List<byte> { b };
                sysex.AddRange(WriteVlq(len));
                for (var i = 0; i < len && p < end; i++) sysex.Add(xmi[p++]);
                list.Add((time, order++, sysex.ToArray()));
            }
            else
            {
                var kind = b & 0xF0;
                if (kind == 0x90)
                {
                    var note = xmi[p++]; var velocity = xmi[p++]; var duration = Vlq();
                    list.Add((time, order++, new[] { b, note, velocity }));
                    list.Add((time + duration, order++ + 1_000_000, new[] { (byte)(0x80 | (b & 0x0F)), note, (byte)0 }));   // note-offs sort after the events of their tick
                }
                else if (kind is 0xC0 or 0xD0) list.Add((time, order++, new[] { b, xmi[p++] }));
                else { var d1 = xmi[p++]; var d2 = xmi[p++]; list.Add((time, order++, new[] { b, d1, d2 })); }
            }
            lastTime = Math.Max(lastTime, time);
        }

        list.Sort((a, c) => a.Time != c.Time ? a.Time.CompareTo(c.Time) : a.Order.CompareTo(c.Order));

        var track = new MemoryStream();
        // one quarter note = 60 ticks at 500000 microseconds: 120 ticks a second, XMIDI's clock
        track.Write(new byte[] { 0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20 });
        long previous = 0;
        foreach (var (t, _, data) in list)
        {
            track.Write(WriteVlq((int)(t - previous)));
            track.Write(data);
            previous = t;
        }
        track.Write(WriteVlq((int)Math.Max(0, lastTime - previous)));
        track.Write(new byte[] { 0xFF, 0x2F, 0x00 });

        var body = track.ToArray();
        var smf = new MemoryStream();
        void U32(int v) { smf.WriteByte((byte)(v >> 24)); smf.WriteByte((byte)(v >> 16)); smf.WriteByte((byte)(v >> 8)); smf.WriteByte((byte)v); }
        void U16(int v) { smf.WriteByte((byte)(v >> 8)); smf.WriteByte((byte)v); }
        smf.Write("MThd"u8); U32(6); U16(0); U16(1); U16(60);
        smf.Write("MTrk"u8); U32(body.Length); smf.Write(body);
        return smf.ToArray();
    }

    private static byte[] WriteVlq(int value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        for (value >>= 7; value > 0; value >>= 7) bytes.Insert(0, (byte)(0x80 | (value & 0x7F)));
        return bytes.ToArray();
    }

    // The EVNT chunk of the first XMID form: IFF chunks are a 4-byte name and a big-endian length, padded to even.
    private static (int Start, int Length)? FindEvents(byte[] d)
    {
        int At(int p) => p + 8 <= d.Length ? d[p + 4] << 24 | d[p + 5] << 16 | d[p + 6] << 8 | d[p + 7] : 0;
        bool Is(int p, string name) => p + 4 <= d.Length && System.Text.Encoding.ASCII.GetString(d, p, 4) == name;

        // find the sequence form ("FORM" length "XMID"), then walk its chunks (TIMB, EVNT ...)
        for (var p = 0; p + 12 <= d.Length; p++)
        {
            if (!Is(p, "FORM") || !Is(p + 8, "XMID")) continue;
            var q = p + 12;
            var formEnd = Math.Min(d.Length, p + 8 + At(p));
            while (q + 8 <= formEnd)
            {
                var length = At(q);
                if (Is(q, "EVNT")) return (q + 8, length);
                q += 8 + length + (length & 1);
            }
        }
        return null;
    }
}
