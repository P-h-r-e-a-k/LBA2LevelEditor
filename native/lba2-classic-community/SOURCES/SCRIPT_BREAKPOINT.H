#ifndef SCRIPT_BREAKPOINT_H
#define SCRIPT_BREAKPOINT_H

/* Script breakpoints for the level editor's embedded Play session, driven over the --listen
   control socket (CONTROL_SERVER.CPP / CONSOLE_CMD.CPP's "breakpoint"/"continue"/"step"
   commands). Mirrors the LBA1 C# port's ScriptBreakpoints class: same one-shot skip/re-pause
   state machine, same "check right before executing an instruction, yield cleanly through the
   engine's own resume-next-frame fields on a hit" hook shape. kind: 0 = Life, 1 = Track. */

extern void ScriptBreakpoint_Add(S32 actor, S32 kind, S32 offset);
extern void ScriptBreakpoint_Remove(S32 actor, S32 kind, S32 offset);
extern void ScriptBreakpoint_Clear(void);

/* Called by DoLife/DoTrack right before executing the instruction at `offset`. Returns TRUE if
   the caller must yield without executing it (the caller is responsible for saving `offset` back
   as its own resumable position first). */
extern S32 ScriptBreakpoint_Hit(S32 actor, S32 kind, S32 offset);

/* Whether ANY actor is currently paused -- gates PERSO.CPP's per-actor loop so a breakpoint that
   fires mid-frame stops every actor not yet processed this frame from running its own script too,
   not just the one that hit it. */
extern S32 ScriptBreakpoint_IsPaused(void);
extern S32 ScriptBreakpoint_PausedActor(void);
extern S32 ScriptBreakpoint_PausedKind(void);
extern S32 ScriptBreakpoint_PausedOffset(void);

/* `continue`/`step` console commands: arm the one-shot resume, then the caller re-enters
   DoLife/DoTrack directly for the paused actor (bypassing the per-frame loop's own gate, which
   only skips actors it iterates to itself). singleStep: FALSE = run freely from here, TRUE =
   execute exactly the next instruction, then re-pause unconditionally. */
extern void ScriptBreakpoint_Resume(S32 singleStep);

/* Call once the DoLife/DoTrack re-entry above returns. If Step's forced re-pause was never
   reached -- the stepped instruction itself yielded the script (e.g. it started waiting on an
   animation) instead of falling through to another Hit() check -- there is nothing left to
   force-pause; this drops the stale arming so it can't ambush some unrelated later frame. A
   no-op once the re-pause already happened (ScriptBreakpoint_Hit already cleared it itself). */
extern void ScriptBreakpoint_SettleAfterResume(void);

#endif // SCRIPT_BREAKPOINT_H
