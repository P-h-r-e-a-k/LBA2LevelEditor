using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LBAAssembler.Lba1;

// Plays a Standard MIDI File through Windows' own MIDI sequencer (MCI), looping until told to stop, as the game's
// music does. One piece at a time; a piece asked for again while it plays is left alone.
internal sealed class Lba1MusicPlayer : IDisposable
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? result, int length, IntPtr callback);

    private const string Alias = "lba1music";
    private readonly string file = Path.Combine(Path.GetTempPath(), $"lba1_music_{Environment.ProcessId}.mid");
    private bool open;
    private int current = -1;

    public int Current => current;

    private byte[]? lastData;
    private bool lastIsCd;
    private double volume = 1;

    // 0..1. The piece playing is started again at the new level (the volume is part of the data it plays).
    public double Volume
    {
        get => volume;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(volume - value) < 0.005) return;
            volume = value;
            if (lastData is null || current < 0 || !(open || cdPlaying)) return;
            var number = current; var data = lastData; var cd = lastIsCd;
            Stop();
            if (cd) PlayCd(number, data); else Play(number, data);
        }
    }

    // The CD's own recording of a tune (an audio track as a WAV), looped.
    private readonly System.Media.SoundPlayer cdPlayer = new();
    private bool cdPlaying;

    public void PlayCd(int number, byte[] wav)
    {
        if (cdPlaying && number == current) return;
        Stop();
        try
        {
            lastData = wav; lastIsCd = true;
            cdPlayer.Stream = new MemoryStream(PcmVolume.Scale(wav, volume));
            cdPlayer.PlayLooping();
            cdPlaying = true;
            current = number;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            DebugLog.Log($"Lba1MusicPlayer: CD track for music {number} didn't start: {error.Message}");
        }
    }

    public void Play(int number, byte[] midi)
    {
        if (open && number == current) return;
        Stop();
        try
        {
            lastData = midi; lastIsCd = false;
            File.WriteAllBytes(file, MidiVolume.Scale(midi, volume));
            if (mciSendString($"open \"{file}\" type sequencer alias {Alias}", null, 0, IntPtr.Zero) != 0) return;
            open = true;
            current = number;
            mciSendString($"play {Alias} from 0", null, 0, IntPtr.Zero);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DebugLog.Log($"Lba1MusicPlayer: music {number} didn't start: {error.Message}");
        }
    }

    public void Stop()
    {
        if (open)
        {
            mciSendString($"stop {Alias}", null, 0, IntPtr.Zero);
            mciSendString($"close {Alias}", null, 0, IntPtr.Zero);
        }
        open = false;
        if (cdPlaying) { try { cdPlayer.Stop(); } catch (InvalidOperationException) { } }
        cdPlaying = false;
        current = -1;
    }

    // Called now and then: starts the piece over when it has finished.
    public void Tick()
    {
        if (!open) return;
        var mode = new StringBuilder(64);
        if (mciSendString($"status {Alias} mode", mode, mode.Capacity, IntPtr.Zero) == 0 && mode.ToString() == "stopped")
            mciSendString($"play {Alias} from 0", null, 0, IntPtr.Zero);
    }

    public void Dispose()
    {
        Stop();
        try { File.Delete(file); } catch (IOException) { }
    }
}
