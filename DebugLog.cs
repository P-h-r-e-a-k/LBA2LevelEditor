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
//
// TEMP FOR TESTING (2026-09-22): on, in both Debug and Release, so every build under test leaves a trail
// without anyone having to set an environment variable first. Before a formal release, either delete this
// file's always-on behaviour (restore the old "Debug only, else opt in" #if) or gate it behind a settings
// flag the user can turn off -- a released build logging actor positions and file paths to its own folder
// by default is not what a shipped product should do silently forever.
internal static class DebugLog
{
    // 5 MB before the log is rotated (moved to .log.old, overwriting any previous one) rather than left to
    // grow for the whole lifetime of a long test session.
    private const long MaxBytes = 5 * 1024 * 1024;

    // Where the log goes: the LBA2_EDITOR_DEBUG_LOG environment variable if set (a sandboxed UI test's own
    // scratch folder uses this), else a fixed name next to the running executable -- AppContext.BaseDirectory
    // is the exe's own folder for an ordinary build and for a single-file publish alike (the same resolution
    // EditorSettings' own portable settings.json uses), so this works the same way wherever the app runs
    // instead of a path that only existed on the machine it was written on.
    public static readonly string LogFile = ResolvePath();

    private static string ResolvePath()
    {
        if (Environment.GetEnvironmentVariable("LBA2_EDITOR_DEBUG_LOG") is { Length: > 0 } configured) return configured;
        return Path.Combine(AppContext.BaseDirectory, "editor.log");
    }

    public static void Log(string message)
    {
        try
        {
            RotateIfHuge();
            File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] [managed] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never be the reason the app crashes -- if the file
            // is locked or the path is briefly unavailable, drop the line.
        }
    }

    private static void RotateIfHuge()
    {
        try
        {
            var info = new FileInfo(LogFile);
            if (!info.Exists || info.Length < MaxBytes) return;
            var old = LogFile + ".old";
            File.Delete(old);
            File.Move(LogFile, old);
        }
        catch
        {
            // Same policy as Log() itself: a failed rotation (the file locked by another process, say)
            // just means this session's log keeps growing a little longer, never a crash.
        }
    }
}
