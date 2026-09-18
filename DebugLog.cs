using System;
using System.IO;

namespace LBA2LevelEditor;

// Best-effort breadcrumb trail for tracing intermittent crashes -- the kind
// that show up once or twice then stop reproducing (confirmed: adding an
// actor and clicking its Body/Animation dropdown crashed the app the first
// two times it was tried, then never again across many later attempts), so
// there's nothing to attach a debugger to after the fact. Appends one line
// per call (open/write/close, never a held-open handle) rather than
// buffering, since the crash this exists to catch is a native access
// violation (see NativeDebugLog's own comment in EXTFUNC.CPP) that gives
// managed code no chance to run a finally block or flush anything -- a
// buffered writer would lose exactly the last lines that matter most.
// Writes to the project folder rather than AppData so the log sits next to
// the rest of the project instead of somewhere that needs hunting for, and
// to the SAME file the native side writes to, so a crash's last few lines
// show what the UI and the engine were each doing, interleaved by time.
internal static class DebugLog
{
    private static readonly string LogPath = @"E:\dump\LBA2LevelEditor\debug.log";

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [managed] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never be the reason the app crashes -- if the file
            // is locked or the path is briefly unavailable, drop the line.
        }
    }
}
