#include "C_EXTERN.H"
#include "COMMON.H"
#include "SCRIPT_BREAKPOINT.H"

#include <SYSTEM/LOG.H>

#define MAX_BREAKPOINTS 64

typedef struct {
    S32 actor;
    S32 kind;
    S32 offset;
} T_BREAKPOINT;

static T_BREAKPOINT s_points[MAX_BREAKPOINTS];
static int s_count = 0;

static S32 s_paused = 0;
static S32 s_pausedActor = -1;
static S32 s_pausedKind = 0;
static S32 s_pausedOffset = 0;

/* See ScriptBreakpoints.cs (the LBA1 C# port) for the reasoning behind this exact shape --
   ported here unchanged after that version was live-debugged through three real bugs (Step
   re-pausing before letting the instruction run, the per-frame loop not stopping at the pause,
   a stale check). skipArmed: let the currently-paused instruction execute once without
   re-triggering. forcePauseNext: Step only, forces a pause at the very next Hit() call after
   the skip is consumed, regardless of whether it's a real breakpoint. */
static S32 s_skipArmed = 0;
static S32 s_forcePauseNext = 0;

void ScriptBreakpoint_Add(S32 actor, S32 kind, S32 offset) {
    for (int i = 0; i < s_count; i++)
        if (s_points[i].actor == actor && s_points[i].kind == kind && s_points[i].offset == offset)
            return;
    if (s_count >= MAX_BREAKPOINTS) {
        Log_Warn("[breakpoint] table full (%d), ignoring actor=%d kind=%d offset=%d", MAX_BREAKPOINTS, (int)actor, (int)kind, (int)offset);
        return;
    }
    s_points[s_count].actor = actor;
    s_points[s_count].kind = kind;
    s_points[s_count].offset = offset;
    s_count++;
}

void ScriptBreakpoint_Remove(S32 actor, S32 kind, S32 offset) {
    for (int i = 0; i < s_count; i++) {
        if (s_points[i].actor == actor && s_points[i].kind == kind && s_points[i].offset == offset) {
            s_points[i] = s_points[s_count - 1];
            s_count--;
            return;
        }
    }
}

void ScriptBreakpoint_Clear(void) {
    s_count = 0;
}

S32 ScriptBreakpoint_IsPaused(void) { return s_paused; }
S32 ScriptBreakpoint_PausedActor(void) { return s_paused ? s_pausedActor : -1; }
S32 ScriptBreakpoint_PausedKind(void) { return s_pausedKind; }
S32 ScriptBreakpoint_PausedOffset(void) { return s_pausedOffset; }

static S32 DoPause(S32 actor, S32 kind, S32 offset) {
    s_paused = 1;
    s_pausedActor = actor;
    s_pausedKind = kind;
    s_pausedOffset = offset;
    /* "! "-prefixed to a `stream on` client (CONTROL_SERVER.CPP's log fan-out) -- the editor's
       control-socket client watches for this exact line shape to learn a breakpoint fired. */
    Log_Info("[breakpoint] paused actor=%d kind=%d offset=%d", (int)actor, (int)kind, (int)offset);
    return 1;
}

void ScriptBreakpoint_Resume(S32 singleStep) {
    if (!s_paused)
        return;
    s_skipArmed = 1;
    s_forcePauseNext = singleStep;
}

void ScriptBreakpoint_SettleAfterResume(void) {
    s_forcePauseNext = 0;
}

S32 ScriptBreakpoint_Hit(S32 actor, S32 kind, S32 offset) {
    if (!s_paused) {
        for (int i = 0; i < s_count; i++) {
            if (s_points[i].actor == actor && s_points[i].kind == kind && s_points[i].offset == offset)
                return DoPause(actor, kind, offset);
        }
        return 0;
    }
    /* Reachable with s_paused set only from the resume-driven direct re-entry into DoLife/DoTrack
       for exactly the paused actor -- PERSO.CPP's own per-frame loop stops calling either while
       paused, so no other actor's script reaches here in the meantime. */
    if (s_skipArmed) {
        s_skipArmed = 0;
        if (!s_forcePauseNext) {
            s_paused = 0;
            return 0;
        }
        return 0; /* Step: let this one instruction run; the *next* Hit() call re-pauses below. */
    }
    s_forcePauseNext = 0;
    return DoPause(actor, kind, offset);
}
