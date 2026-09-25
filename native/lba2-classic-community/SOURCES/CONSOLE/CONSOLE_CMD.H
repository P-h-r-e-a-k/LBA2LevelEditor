#pragma once

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Applies a resolution switch that res_do_switch (the `resolution` console
 * verb's handler) had to defer because it was requested while the renderer's
 * streaming texture was locked (Window_IsRendererTextureLocked, WINDOW.H) --
 * see res_do_switch's own comment in CONSOLE_CMD.CPP for the crash this
 * avoids. No-op if nothing is pending.
 *
 * Registered as the engine's pre-frame callback (SetPreFrameCallback,
 * SVGA/VIDEO.H) in main(), so it runs once per actual present, strictly
 * before PresentFrame() ever takes that lock -- the same "reached every
 * frame, including inside a modal's own inner loop" guarantee
 * ControlPrePresent relies on to service the control socket at all, just on
 * the other side of the lock.
 */
void Console_ApplyPendingResSwitch(void);

#ifdef __cplusplus
}
#endif
