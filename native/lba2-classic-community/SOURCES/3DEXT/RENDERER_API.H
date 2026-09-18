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
/* Like lba2_renderer_render_frame(), but also loads and draws radiusCubes
   rings of neighboring cubes around the one lba2_renderer_set_view_target()
   last landed on, compositing them into the same frame via the engine's own
   per-cube Z-buffer (see AffGrilleExtWide in EXTFUNC.CPP for exactly how and
   why this doesn't need the terrain/decor/actor data structures -- all
   fixed-size and single-cube -- to be resized). radiusCubes=0 behaves like
   lba2_renderer_render_frame(). Costs roughly (2*radiusCubes+1)^2 cube loads
   per call, so reserve it for camera distances where a single cube visibly
   runs out before the horizon does. Restores the primary cube's own
   terrain/actors before returning, so a later lba2_renderer_render_frame()
   or lba2_renderer_project_point() call still sees the cube the camera
   nominally targets. */
LBA2_RENDERER_API int lba2_renderer_render_frame_wide(int radiusCubes);
LBA2_RENDERER_API void lba2_renderer_set_camera(int alpha, int beta, int gamma, int distance);
/* Toggles the sky/horizon layer (DrawHorizon2ZBuf + DrawSkySeaZBuf) for
   every subsequent lba2_renderer_render_frame() call. Off leaves the
   screen-clear colour (the island's ambience fog colour) as the background
   instead, useful when a high overhead camera would otherwise have the sky
   filling most of the frame. Does not affect the full game. */
LBA2_RENDERER_API void lba2_renderer_set_draw_sky(int enabled);
/* Toggles DrawOneSea() (TERRAIN.CPP), the flat sea plane drawn across a
   whole cube at a fixed world height. From a near-vertical (top-down)
   camera the classic engine's depth math can sort this plane as nearer than
   elevated terrain it should sit well behind, painting flat sea color over
   real terrain -- off for the minimap's top-down snapshots, on (the
   default) for the main 3D view's ordinary viewing angles which don't hit
   this. Does not affect the full game. */
LBA2_RENDERER_API void lba2_renderer_set_draw_sea(int enabled);
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

/* Actor (NPC) positions and patrol-route waypoints for the island loaded by
   the most recent lba2_renderer_load_island() call, for a minimap overlay.
   This is loading-side data only (RENDERER_ACTORS.CPP scans every exterior
   scene belonging to the island via the real LoadScene(), then walks each
   actor's track bytecode structurally) -- no life-script or track VM runs,
   so this reflects each actor's initial position and the full set of
   waypoints its track(s) reference, not where it "currently" is or which
   track it starts on. Populated once per lba2_renderer_load_island() call;
   O(scene count) so callers should not call it more than that. */
LBA2_RENDERER_API int lba2_renderer_get_actor_count(void);
/* Returns 0 if index is out of range, 1 on success. waypointCount may be 0. */
LBA2_RENDERER_API int lba2_renderer_get_actor(int index, int *x, int *y, int *z, int *waypointCount);
/* Returns 0 if actorIndex/waypointIndex is out of range, 1 on success. */
LBA2_RENDERER_API int lba2_renderer_get_actor_waypoint(int actorIndex, int waypointIndex, int *x, int *y, int *z);

/* Attribute snapshot for the editor's actor inspector: facing angle, body/anim
   indices, remaining life points, armor, hit force, and movement type -- all
   read directly off the loaded T_OBJET at scan time. Returns 0 if index is
   out of range, 1 on success; any out pointer may be NULL. */
LBA2_RENDERER_API int lba2_renderer_get_actor_attributes(int index, int *beta, int *body, int *anim, int *lifePoint, int *armor, int *hitForce, int *move);

/* Read-only decoded life+track script text for one actor (see RENDERER_ACTORS.CPP's
   DecodeLifeScript/DecodeTrackScript for exactly which opcodes are covered --
   decoding stops and says so at the first opcode it doesn't recognize, rather
   than risk misreading the rest of the script). Copies up to bufferSize-1 bytes
   plus a NUL terminator into buffer (pass NULL/0 to just query the required
   size). Returns the number of bytes the full text needs, or 0 if index is out
   of range. */
LBA2_RENDERER_API int lba2_renderer_get_actor_script(int index, char *buffer, int bufferSize);

/* Session-only edits: none of these three write anything to disk. They
   update the in-memory actor snapshot (so the next lba2_renderer_get_actor*
   call reflects the change immediately) and, if the affected scene is the
   one currently loaded for rendering, patch it live into the renderer's
   working actor list too -- and re-patch it again every time that scene
   next reloads (which happens often; see AffGrilleExtWide), so the edit
   doesn't appear to "revert" while panning around. All three refuse to
   touch actor slot 0 (the hero), which has special-cased behavior
   elsewhere in the engine this generic editor has no business overriding.
   Persisting these edits to the game's SCENE.HQR is a separate operation
   (not yet exposed here).

   body/anim are raw BODY.HQR/ANIM.HQR archive indices, matching
   lba2_renderer_render_body_preview's own body/anim below and what
   lba2_renderer_get_actor_attributes reads back -- NOT the small
   per-object "generic" index space a life script's own BODY/ANIM opcodes
   use (SearchBody()/SearchAnim(), FICHE.CPP), which only resolves
   correctly against that specific object's own PtrFile3D conversion
   table. Applied via ObjectInitBody()/ObjectInitAnim() directly for
   exactly that reason -- see RENDERER_ACTORS.CPP's ApplyRawBodyAnim. */

/* Returns 0 if index is out of range or refers to the hero, 1 on success. */
LBA2_RENDERER_API int lba2_renderer_set_actor_attributes(int index, int beta, int body, int anim, int lifePoint, int armor, int hitForce, int move);
/* Returns 0 if index is out of range or refers to the hero, 1 on success. */
LBA2_RENDERER_API int lba2_renderer_set_actor_position(int index, int worldX, int worldY, int worldZ);
/* Adds a new actor to whichever scene covers (worldX, worldZ)'s cube, with a
   trivial one-opcode (END) life and track script. body/anim are raw archive
   indices, same as lba2_renderer_set_actor_attributes. Returns the new
   actor's index (usable with every other actor API immediately), or -1 if
   that world position isn't on any loaded scene or the scene is already at
   its 100-actor limit. */
LBA2_RENDERER_API int lba2_renderer_add_actor(int worldX, int worldY, int worldZ, int beta, int body, int anim, int lifePoint, int armor, int hitForce, int move);

/* Full designer-authored T_OBJET.Flags word (collision/gravity/visibility/
   water-lava-reaction toggles) -- lba2_renderer_get_actor_attributes only
   covers the handful of scalar fields the original inspector panel showed.
   Same session-only semantics and hero exclusion as the Set* calls above. */
LBA2_RENDERER_API int lba2_renderer_get_actor_flags(int index, unsigned int *flags);
LBA2_RENDERER_API int lba2_renderer_set_actor_flags(int index, unsigned int flags);

/* The actor's own local bounding box (its body model's real extent), for a
   screen-space hit-test box sized to match the actual rendered body instead
   of a fixed guess. Only succeeds for an actor whose scene is the one
   currently loaded and rendered, and that has a body assigned -- callers
   should fall back to a fixed-size hit target when this returns 0. */
LBA2_RENDERER_API int lba2_renderer_get_actor_bounds(int index, int *xMin, int *xMax, int *yMin, int *yMax, int *zMin, int *zMax);

/* Raw ANIM.HQR archive indices this actor's own character-type table (its
   PtrFile3D "fiche", FICHE.CPP -- the same table SearchAnim() walks to
   resolve a script's GetAnim() call) actually lists, i.e. the real moveset
   this specific character is designed to use -- not a guess from body/anim
   group counts (an earlier, disproven approach; see EXTFUNC.CPP's
   AnimFitsBody comment). Writes up to maxCount entries into outAnims and
   returns the total found (which may exceed maxCount -- call once with
   maxCount 0 to size, as usual), or -1 if this actor's scene isn't the one
   currently loaded/rendered (the scan-time actor list never runs the load
   that populates PtrFile3D) or it has none. */
LBA2_RENDERER_API int lba2_renderer_get_actor_native_anims(int index, int *outAnims, int maxCount);

/* Renders one body model, alone against a cleared background, into the same
   shared framebuffer lba2_renderer_framebuffer() reads -- for the actor-
   attributes editor's rotating preview. Call with a slowly increasing
   cameraBeta (the same 0-4096-per-turn angle unit as lba2_renderer_
   set_camera's alpha/beta/gamma) across successive calls to animate an
   orbit around the body, and the animation itself plays across successive
   calls the same way (ObjectSetInterFrame steps it each call). body/anim
   are raw BODY.HQR/ANIM.HQR archive indices -- the same space
   lba2_renderer_set_actor_attributes' body/anim and lba2_renderer_get_
   actor_attributes' body/anim use, and what the editor's own body/anim
   pickers list (built from those archives directly). This does not touch
   terrain/sky/decor or any real actor, so it's safe to call between
   ordinary lba2_renderer_render_frame*() calls without disturbing the main
   view (whatever real render happens next resets the camera and clears the
   screen fully anyway). cameraDistance has no per-body auto-fit built in
   (there's no reliable per-body bounding box available for a synthetic
   preview object -- see AffichageBodyPreview's own comment); callers that
   want the body to fill the frame should measure the rendered silhouette
   at a probe distance and solve for the right one themselves (see
   CommunityRendererBackend.CalibrateBodyPreviewDistance in the C# editor
   for the approach this API was designed around). Returns 0 if body or
   anim is out of range, 1 on a successful draw -- callers should show a
   fallback message rather than a stale/blank frame on 0. */
LBA2_RENDERER_API int lba2_renderer_render_body_preview(int body, int anim, int cameraBeta, int cameraDistance);

/* Projects a world point through the camera set up by the most recent
   lba2_renderer_render_frame() call (see RENDERER_API.CPP for exactly which
   engine functions this reuses). Returns 0 if the point is beyond the far
   clip plane -- screenX/screenY are still written in that case, but to a
   sentinel value, not a usable screen position. A nonzero return only means
   "in front of the camera and within depth range," not "on screen": callers
   still need their own bounds check against the viewport size. */
LBA2_RENDERER_API int lba2_renderer_project_point(int worldX, int worldY, int worldZ, int *screenX, int *screenY);

#ifdef __cplusplus
}
#endif

#endif
