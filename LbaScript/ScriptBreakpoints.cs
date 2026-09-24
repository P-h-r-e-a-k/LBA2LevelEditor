namespace LBAAssembler.LbaScript;

// One script instruction where execution should pause: LBA1 scene, in-scene actor slot (matching
// Lba1Runtime's own "numObj"), Life or Track, byte offset. LBA2 windows use this same shape (scene =
// island/interior index, actor = slot) purely for a consistent set/toggle UI -- see the remarks on
// ResumeRequested below for why only LBA1 can actually honour a hit.
public readonly record struct ScriptBreakpoint(int Scene, int Actor, ScriptKind Kind, int Offset);

public readonly record struct PausedScript(int Scene, int Actor, ScriptKind Kind, int Offset);

// App-wide breakpoint set and pause state, shared by every open ActorScriptWindow and by
// Lba1Runtime's own interpreter (Lba1Runtime.Script.cs). A breakpoint set from one script window is
// honoured the moment Play reaches it, and every open window (for any actor) sees the same live
// Current/Changed state at once, so "multiple script windows open together" all agree on what's
// paused and can all drive Continue/Step.
//
// LBA2 has no live pause: its Play runs as a separate native process with no script-level hook back
// into this editor (unlike LBA1, which runs as a C# interpreter this process already drives frame by
// frame). Breakpoints can still be set/toggled for LBA2 actors (e.g. from the C pane, which is the
// same compiler/decompiler for both games) so the UI is consistent, but ResumeRequested/Hit are only
// ever driven by the LBA1 runtime -- an LBA2 breakpoint just never triggers.
public static class ScriptBreakpoints
{
    private static readonly HashSet<ScriptBreakpoint> points = new();
    private static bool skipArmed;
    private static bool forcePauseNext;

    // Raised whenever the breakpoint set or the paused/resumed state changes. Carries no data --
    // subscribers re-read Current/IsSet, since several things can change in one call.
    public static event Action? Changed;

    public static PausedScript? Current { get; private set; }

    // Set by whichever Lba1PlayView is currently running (cleared when it stops or loads a new
    // scene): lets any script window's Continue/Step act on the live runtime without holding a
    // direct reference to it.
    public static Action<bool>? ResumeRequested;

    public static bool IsSet(int scene, int actor, ScriptKind kind, int offset) =>
        points.Contains(new ScriptBreakpoint(scene, actor, kind, offset));

    public static bool AnySet(int scene, int actor, ScriptKind kind) =>
        points.Any(p => p.Scene == scene && p.Actor == actor && p.Kind == kind);

    public static IEnumerable<ScriptBreakpoint> For(int scene, int actor, ScriptKind kind) =>
        points.Where(p => p.Scene == scene && p.Actor == actor && p.Kind == kind);

    public static void Toggle(int scene, int actor, ScriptKind kind, int offset)
    {
        var bp = new ScriptBreakpoint(scene, actor, kind, offset);
        if (!points.Remove(bp)) points.Add(bp);
        Changed?.Invoke();
    }

    // A fresh scene/runtime invalidates any in-flight pause (the offsets it refers to belong to a
    // script that no longer has a live interpreter position), but breakpoints themselves persist --
    // like a normal debugger, they survive a restart.
    public static void ClearPause()
    {
        if (Current is null && !skipArmed) return;
        Current = null;
        skipArmed = false;
        forcePauseNext = false;
        Changed?.Invoke();
    }

    // Let the currently-paused script run again: freely (Continue) or for exactly one more
    // instruction before re-pausing (Step). Does not itself advance anything -- it arms the one-shot
    // skip that Hit() below consumes the next time the interpreter reaches the paused instruction, so
    // the same breakpoint doesn't instantly re-trigger, and (for Step) also arms a forced pause at
    // whichever instruction follows it.
    public static void Resume(bool singleStep)
    {
        if (Current is null) return;
        skipArmed = true;
        forcePauseNext = singleStep;
        ResumeRequested?.Invoke(singleStep);
        // The resumed call (Lba1Runtime.ResumePausedScript, synchronous) is done by the time control
        // returns here. If Step's forced pause was never reached -- the stepped instruction itself
        // yielded the script (e.g. it started waiting on an animation) rather than falling through to
        // another Hit() check -- there's nothing left to force-pause; drop the stale arming rather than
        // let it ambush some unrelated later frame.
        forcePauseNext = false;
    }

    // Called by Lba1Runtime right before executing the instruction at `offset` for `actor`'s `kind`
    // script. Returns true if it should pause there instead (the caller must yield without executing
    // it, saving `offset` as the resumable position).
    //
    // Reachable with Current != null only from within a Resume() call: Lba1PlayView never advances the
    // simulation at all while paused, so ResumePausedScript's own direct DoLife/DoTrack call -- always
    // for exactly the paused (actor, kind) -- is the only way execution re-enters here before Current
    // is cleared.
    public static bool Hit(int scene, int actor, ScriptKind kind, int offset)
    {
        if (Current is null)
        {
            if (!points.Contains(new ScriptBreakpoint(scene, actor, kind, offset))) return false;
            return Pause(scene, actor, kind, offset);
        }
        if (skipArmed)
        {
            // Let the previously-paused instruction actually execute now. Continue (forcePauseNext
            // false) is done arming anything and resumes checking real breakpoints from here on;
            // Step keeps Current set so the very next Hit() call (below) pauses unconditionally.
            skipArmed = false;
            if (!forcePauseNext) { Current = null; Changed?.Invoke(); }
            return false;
        }
        forcePauseNext = false;
        return Pause(scene, actor, kind, offset);
    }

    private static bool Pause(int scene, int actor, ScriptKind kind, int offset)
    {
        Current = new PausedScript(scene, actor, kind, offset);
        Changed?.Invoke();
        return true;
    }
}
