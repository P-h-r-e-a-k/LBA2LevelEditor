using System.Buffers.Binary;
using LBA2LevelEditor;

namespace ScriptRoundTrip;

// The sound balance helpers: PCM samples and MIDI channel volumes scale, and everything else stays as it was.
//   audio
internal static class AudioTests
{
    public static int Run(string[] args)
    {
        var failures = 0;
        void Check(string what, bool ok) { Console.WriteLine($"  {(ok ? "ok    " : "FAILED")} {what}"); if (!ok) failures++; }

        Console.WriteLine("PCM");
        byte[] Wav(int bits, byte[] samples)
        {
            var header = new byte[44];
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + samples.Length);
            System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), 11025);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 11025 * bits / 8);
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), (short)(bits / 8));
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), (short)bits);
            System.Text.Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), samples.Length);
            return header.Concat(samples).ToArray();
        }
        var w8 = PcmVolume.Scale(Wav(8, new byte[] { 128, 255, 0, 192 }), 0.5);
        Check("8-bit samples are scaled around the middle", w8[44] == 128 && w8[45] == 192 && w8[46] == 64 && w8[47] == 160);
        var s16 = new byte[4]; BinaryPrimitives.WriteInt16LittleEndian(s16, 20000); BinaryPrimitives.WriteInt16LittleEndian(s16.AsSpan(2), -10000);
        var w16 = PcmVolume.Scale(Wav(16, s16), 0.25);
        Check("16-bit samples are scaled", BinaryPrimitives.ReadInt16LittleEndian(w16.AsSpan(44)) == 5000 && BinaryPrimitives.ReadInt16LittleEndian(w16.AsSpan(46)) == -2500);
        Check("the header is untouched", w16.AsSpan(0, 44).SequenceEqual(Wav(16, s16).AsSpan(0, 44)));
        Check("full volume returns the same bytes", ReferenceEquals(PcmVolume.Scale(w16, 1.0), w16));

        Console.WriteLine("MIDI");
        // format 0, one track: note on, CC7 = 100 on channel 1, note off, end of track
        var track = new byte[] { 0x00, 0x90, 60, 100, 0x00, 0xB1, 7, 100, 0x10, 0x80, 60, 0, 0x00, 0xFF, 0x2F, 0x00 };
        var midi = new List<byte>();
        midi.AddRange("MThd"u8.ToArray()); midi.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 0, 96 });
        midi.AddRange("MTrk"u8.ToArray()); midi.AddRange(new byte[] { 0, 0, 0, (byte)track.Length }); midi.AddRange(track);
        var scaled = MidiVolume.Scale(midi.ToArray(), 0.5);
        var length = BinaryPrimitives.ReadInt32BigEndian(scaled.AsSpan(18));
        Check("the track length still matches the file", 22 + length == scaled.Length);
        Check("16 starting channel volumes were added (64 bytes)", length == track.Length + 64);
        var body = scaled.AsSpan(22, length).ToArray();
        var found = false;
        for (var i = 64; i + 3 < body.Length; i++) if (body[i] == 0xB1 && body[i + 1] == 7) { found = body[i + 2] == 50; break; }
        Check("the file's own channel volume (100) is halved", found);
        Check("the notes are untouched", body.AsSpan(64, 4).SequenceEqual(new byte[] { 0x00, 0x90, 60, 100 }) && body[^4..].SequenceEqual(new byte[] { 0x00, 0xFF, 0x2F, 0x00 }));

        Console.WriteLine("real LBA1 music");
        var lba1 = Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure";
        if (Directory.Exists(lba1))
        {
            var runtimeData = new LBA2LevelEditor.Lba1.Runtime.Lba1RuntimeData(lba1);
            var tunes = 0; var bad = 0;
            for (var number = 0; number < 30; number++)
            {
                if (runtimeData.MidiFile(number) is not { } file) continue;
                tunes++;
                var half = MidiVolume.Scale(file, 0.5);
                var at = 14; var tracks = 0; var whole = half.Length > 14;
                while (whole && at + 8 <= half.Length && half[at] == 'M') { at += 8 + BinaryPrimitives.ReadInt32BigEndian(half.AsSpan(at + 4)); tracks++; }
                if (!whole || at != half.Length || tracks == 0) bad++;
            }
            Check($"{tunes} of the game's MIDI tunes scale into files whose track chunks still add up", tunes > 0 && bad == 0);
        }
        else Console.WriteLine("  (no LBA1 folder here)");

        Console.WriteLine("levels");
        Check("percent to the engine's 0..127", AudioLevels.ToEngine(0) == 0 && AudioLevels.ToEngine(100) == 127 && AudioLevels.ToEngine(50) == 64);
        var user = Path.Combine(Path.GetTempPath(), "audiocfg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(user);
        try
        {
            File.WriteAllText(Path.Combine(user, "lba2.cfg"), "WinMode: 1\nWaveVolume: 97\nMusicVolume: 127\n");
            Lba2Play.WriteAudioConfig(user, new AudioLevels { Effects = 50, Voices = 100, Music = 20 });
            var cfg = File.ReadAllLines(Path.Combine(user, "lba2.cfg"));
            Check("existing keys are replaced, others kept, missing ones added",
                cfg.Contains("WinMode: 1") && cfg.Contains("WaveVolume: 64") && cfg.Contains("MusicVolume: 25") && cfg.Contains("VoiceVolume: 127") && cfg.Contains("CDVolume: 25") && cfg.Contains("MasterVolume: 127") && cfg.Count(l => l.StartsWith("WaveVolume")) == 1);
        }
        finally { try { Directory.Delete(user, true); } catch (IOException) { } }

        Console.WriteLine(failures == 0 ? "audio tests: all passed" : $"audio tests: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
