#ifndef LBA2_RENDERER_API_H
#define LBA2_RENDERER_API_H

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define LBA2_RENDERER_API __declspec(dllexport)
#else
#define LBA2_RENDERER_API
#endif

/* Stable editor-facing boundary. The implementation is intentionally small;
   engine initialization and resource ownership remain on the native side. */
LBA2_RENDERER_API int lba2_renderer_version(void);
LBA2_RENDERER_API int lba2_renderer_initialize(void);
LBA2_RENDERER_API int lba2_renderer_set_data_root(const char *path);
LBA2_RENDERER_API void lba2_renderer_shutdown(void);
LBA2_RENDERER_API int lba2_renderer_load_island(const char *name);
LBA2_RENDERER_API int lba2_renderer_load_cube(int x, int y);
/* Moves the camera's look-at point to an absolute world position spanning
   the whole 16x16 cube grid (each cube is 32768 world units square).
   Loads whichever cube contains that position if it differs from the one
   currently loaded, so callers can pan continuously across an island
   instead of being pinned to a single cube. Returns 0 (leaving the
   previously loaded cube and view target unchanged) if the target cube
   has no data. */
LBA2_RENDERER_API int lba2_renderer_set_view_target(int worldX, int worldY, int worldZ);
LBA2_RENDERER_API int lba2_renderer_render_frame(void);
LBA2_RENDERER_API void lba2_renderer_set_camera(int alpha, int beta, int gamma, int distance);
/* Toggles the sky/horizon layer (DrawHorizon2ZBuf + DrawSkySeaZBuf) for
   every subsequent lba2_renderer_render_frame() call. Off leaves the
   screen-clear colour (the island's ambience fog colour) as the background
   instead, useful when a high overhead camera would otherwise have the sky
   filling most of the frame. Does not affect the full game. */
LBA2_RENDERER_API void lba2_renderer_set_draw_sky(int enabled);
/* Overrides the distance-fog start and far-clip distances (world units) used
   by the exterior renderer. LOADISLE.CPP's InitExternalView() sets these to
   values tuned for the original 320x200 game (StartZFog 12000, ClipZFar
   48000) which, at editor zoom-out distances and steep overhead angles,
   fades most of a 32768-unit-wide cube into the fog/sky colour well before
   its far edge. Called once at boot with generous defaults; exposed
   separately in case a caller wants different values later. Does not affect
   the full game (LOADISLE.CPP's own values are untouched; this only
   overwrites them after the fact, and only this renderer ever calls it). */
LBA2_RENDERER_API void lba2_renderer_set_view_distance(int startFog, int clipFar);
LBA2_RENDERER_API const unsigned char *lba2_renderer_framebuffer(int *width, int *height, int *pitch);

#ifdef __cplusplus
}
#endif

#endif
