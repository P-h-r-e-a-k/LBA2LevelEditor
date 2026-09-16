#include "RENDERER_API.H"
#include "DEFINES.H"
#include "EXTERN.H"
#include "LOADISLE.H"
#include "DIRECTORIES.H"
#include "HQR_NAMES.H"
#include "COMMON.H"
#include <SYSTEM/EVENTS.H>
#include <POLYGON/POLY.H>
#include <SVGA/CLIP.H>
#include <SVGA/INITMODE.H>
#include <SVGA/DIRTYBOX.H>
#include <SYSTEM/WINDOW.H>
#include "MEM.H"
#include "AMBIANCE.H"
#include <SDL3/SDL.h>

extern void AdjustHQRMem(void);
#include <string.h>
#include <direct.h>
#include <stdlib.h>

extern void AffGrilleExt(void);
extern void Init3DExtView(void);
extern void Init3DExtGame(void);
extern int RendererBootAssets(void);

// Declared directly instead of pulling in C_EXTERN.H: that header (via
// MEM.H) redeclares several enums/typedefs this file's own "MEM.H" include
// already brought in, and MEM.H has no include guard against being reached
// twice by two different paths.
extern S32 Island;
extern U8 RendererDrawSky;
extern int RendererScanIslandActors(int islandIndex);
extern int RendererGetActorCount(void);
extern int RendererGetActor(int index, int *x, int *y, int *z, int *waypointCount);
extern int RendererGetActorWaypoint(int actorIndex, int waypointIndex, int *x, int *y, int *z);

// LoadCube() (LOADISLE.CPP) overwrites ClipZFar/StartZFog from each cube's
// own data on every load; lba2_renderer_load_cube() re-applies these on top
// afterwards so every cube keeps the same generous editor view distance
// regardless of what it shipped with. lba2_renderer_set_view_distance()
// updates both the live values and this pair, so a later cube load doesn't
// silently revert to the original defaults.
static S32 RendererStartFog = 200000;
static S32 RendererClipFar = 250000;

int lba2_renderer_version(void)
{
    return 1;
}

int lba2_renderer_initialize(void)
{
    // IsleMapIndex/GroundTexture/ObjTexture are reassigned via the engine's
    // own Malloc (NormMalloc) inside InitMemory() and LoadIsland(), and
    // ListTriExt via Init3DExt(); allocate with the same allocator here so
    // whichever pointer survives to lba2_renderer_shutdown() can be freed
    // with the matching Free() instead of crashing ucrtbase's free() on a
    // pointer NormMalloc actually owns.
    IsleMapIndex = (U8 *)Malloc(256);
    GroundTexture = (U8 *)Malloc(256 * 256 + RECOVER_AREA);
    ObjTexture = (U8 *)Malloc(256 * 256 + RECOVER_AREA);
    ListTriExt = (T_LIST_TRI *)Malloc(sizeof(T_LIST_TRI) * MAX_OBJ_DECORS);
    if (!IsleMapIndex || !GroundTexture || !ObjTexture || !ListTriExt) return 0;

    // InitWindow() is the prerequisite CreateWindowSurface() (called by
    // InitGraphics() below) asserts on -- it brings up the SDL video
    // subsystem and records windowSystemInitialized.
    if (!IsWindowInitialized() && !InitWindow("LBA2 Level Workshop")) return 0;

    Mem_ConfigureScreenBuffers(640, 480);
    AdjustHQRMem();
    InitMainBuffer();
    InitMemory();

    // InitGraphics() is PERSO.CPP's single entry point for bringing up the
    // window/video surface and the Log/Screen buffers, and -- critically --
    // for resetting ClipXMin/ClipYMin/ClipXMax/ClipYMax to the full mode
    // resolution via SetClipWindow()/SetClip(). Without it ModeResX/ModeResY
    // and the clip bounds stay at their zero-initialized defaults, so
    // MaskVisible() flags every projected vertex as out-of-bounds and
    // AffichageTerrainZBuf() never reaches DrawFeuillePolyZBuf(). This
    // supersedes the separate CreateScreenMemory() call this function used
    // to make (InitGraphics calls it internally).
    if (!InitGraphics(640, 480, false)) return 0;

    // This renderer only ever reads pixels back via lba2_renderer_framebuffer()
    // (the raw Log buffer); it has no use for a visible OS window. Without this,
    // the SDL window InitGraphics() just created stays on screen as a second,
    // real window alongside the WPF host window.
    Window_SetVisible(false);

    // BoxInit() sets up the dirty-box clip rect (BoxClipXMin/Min/Max) that the
    // Cls() macro clears against every frame; it's normally called from
    // PERSO.CPP's boot (excluded from this renderer-only build) with
    // COMMON.H's MAX_BOXES (a sum of gameplay-system limits -- objects,
    // flows, darts, rain -- this renderer never boots). Without it
    // BoxClipXMin/Max stay at their zero-initialized default, so
    // ClsTerrainZBuf()'s Cls() clears a degenerate zero-area box and does
    // nothing -- each new frame's Log buffer keeps whatever the previous
    // frame left behind wherever the new frame doesn't fully redraw over it,
    // visible as ghost trails of stale terrain while panning. This renderer
    // only ever clears the whole screen, so the box pool size just needs to
    // be non-trivial; it doesn't need to match the full game's limits.
    if (!BoxInit(256)) return 0;

    if (!RendererBootAssets()) return 0;
    if (!InitEvents()) return 0;
    if (!InitExternalView()) return 0;

    // InitExternalView() just set ClipZFar/StartZFog to the original game's
    // tuned values (48000/12000); widen them for editing so panning/zooming
    // around a cube doesn't fade into the fog/sky colour well short of its
    // far edge. See lba2_renderer_set_view_distance()'s doc comment.
    lba2_renderer_set_view_distance(200000, 250000);
    return 1;
}

int lba2_renderer_set_data_root(const char *path)
{
    if (!path || _chdir(path) != 0) return 0;
    char root[ADELINE_MAX_PATH];
    strncpy(root, path, ADELINE_MAX_PATH - 2);
    root[ADELINE_MAX_PATH - 2] = '\0';
    if (root[0] && root[strlen(root) - 1] != '\\' && root[strlen(root) - 1] != '/') strcat(root, "\\");
    InitDirectories(root, root, root, "", 0);
    return 1;
}

void lba2_renderer_shutdown(void)
{
    ClearExternalView();
    EndEvents();
    Mem_TeardownMainBuffer();
    Free(IsleMapIndex); IsleMapIndex = NULL;
    Free(GroundTexture); GroundTexture = NULL;
    Free(ObjTexture); ObjTexture = NULL;
    Free(ListTriExt); ListTriExt = NULL;
}

// Mirrors EXTFUNC.CPP's IleLst[] order, which the retail engine uses both to
// pick the per-island sky texture (RESS_SKYSEA0 + index) and, via the
// numeric Island global, the per-island ambience palette in ChoicePalette()
// (RESS_XPL0 + Island -- OffsetFog/FogCoul/OffsetPalette all come from it).
static const char *kIslandNames[] = {
    "citadel", "sendell", "desert", "emeraude", "otringal",
    "celebrat", "platform", "mosquibe", "knartas", "ilotcx",
    "ascence", "souscelb",
};

// Bumped on every successful lba2_renderer_load_island() call so
// lba2_renderer_set_view_target() can tell an island switch apart from a
// same-island reposition, even when the new island happens to have data at
// the same cube coordinates as the old one.
static S32 islandGeneration = 0;

int lba2_renderer_load_island(const char *name)
{
    if (!name || !name[0]) return 0;
    strncpy(IslandName, name, ADELINE_MAX_PATH - 1);
    IslandName[ADELINE_MAX_PATH - 1] = '\0';
    LoadIsland();

    char path[ADELINE_MAX_PATH];
    GetResPath(path, ADELINE_MAX_PATH, RESS_HQR_NAME);

    S32 islandIndex = -1;
    for (S32 i = 0; i < (S32)(sizeof(kIslandNames) / sizeof(kIslandNames[0])); i++) {
        if (!strcasecmp(name, kIslandNames[i])) { islandIndex = i; break; }
    }
    if (islandIndex < 0 && !strcasecmp(name, "celebra2")) islandIndex = 5; // shares celebrat's sky/ambience

    S32 skyResource = islandIndex >= 0 ? RESS_SKYSEA0 + islandIndex : RESS_SKYSEA0;
    if (!strcasecmp(name, "citabau")) skyResource = RESS_SKYSEA00; // interior-only ambience, no Island slot
    if (!Load_HQR(path, SkySeaTexture, skyResource)) return 0;

    // ChoicePalette() reads the Island global to pick this island's ambience
    // palette. Without refreshing it here, every island loaded after the
    // first kept whatever FogCoul/PtrNuances/PtrPalCurrent the boot-time
    // default (RendererBootAssets()'s one-time ChoicePalette() call) left
    // behind, so the horizon/screen-clear colour and shading didn't match
    // the actually-loaded island.
    if (islandIndex >= 0) {
        Island = islandIndex;
        ChoicePalette();
    }
    islandGeneration++;
    // Scans every exterior scene on this island for actors/tracks (see
    // RENDERER_ACTORS.CPP); islandIndex < 0 (e.g. citabau, which has no
    // ordinary Island slot -- see ChoicePalette()'s special case) just
    // leaves the actor list empty rather than scanning.
    RendererScanIslandActors(islandIndex);
    return 1;
}

int lba2_renderer_load_cube(int x, int y)
{
    CurrentCubeX = x;
    CurrentCubeY = y;
    S32 result = LoadCube(x, y);
    if (result) {
        Init3DExtGame();
        // LoadCube() just overwrote ClipZFar/StartZFog from this cube's own
        // data (LOADISLE.CPP) and re-ran SetFog() with them; re-apply the
        // wider editor defaults on top so every cube gets the same generous
        // view distance regardless of what it shipped with.
        lba2_renderer_set_view_distance(RendererStartFog, RendererClipFar);
    }
    return result;
}

// Matches HOLO.H's SCE ("SizeXZ Cube Exterieur"): each exterior cube spans
// 32768 world units on X/Z.
#define WORLD_CUBE_SIZE 32768

int lba2_renderer_set_view_target(int worldX, int worldY, int worldZ)
{
    // Tracked independently of CurrentCubeX/CurrentCubeY: lba2_renderer_
    // load_cube() sets those before knowing whether LoadCube() succeeds, so
    // they can point at a cube that was never actually loaded.
    static S32 lastViewCubeX = -1;
    static S32 lastViewCubeY = -1;
    // Also tracked so a same-coordinate cube on a *different* island still
    // forces a reload -- otherwise switching islands while landing on the
    // same (cubeX, cubeY) silently kept the old island's cube data (its
    // decor list, in particular) paired with the new island's much smaller
    // HQR_Isle_Obj object bank, and a decor body index valid for the old
    // island but absent from the new one crashed AffichageObjetDecorsZBuf's
    // Return_BodyDisplay on the resulting null pointer.
    static S32 lastViewIslandGeneration = -1;

    S32 cubeX = worldX / WORLD_CUBE_SIZE;
    S32 cubeY = worldZ / WORLD_CUBE_SIZE;
    if (cubeX < 0) cubeX = 0; else if (cubeX > 15) cubeX = 15;
    if (cubeY < 0) cubeY = 0; else if (cubeY > 15) cubeY = 15;

    if (cubeX != lastViewCubeX || cubeY != lastViewCubeY || islandGeneration != lastViewIslandGeneration) {
        if (!lba2_renderer_load_cube(cubeX, cubeY)) return 0;
        lastViewCubeX = cubeX;
        lastViewCubeY = cubeY;
        lastViewIslandGeneration = islandGeneration;
    }

    VueOffsetX = worldX - cubeX * WORLD_CUBE_SIZE;
    VueOffsetY = worldY;
    VueOffsetZ = worldZ - cubeY * WORLD_CUBE_SIZE;
    return 1;
}

int lba2_renderer_render_frame(void)
{
    AffGrilleExt();
    return 1;
}

void lba2_renderer_set_camera(int alpha, int beta, int gamma, int distance)
{
    // Camera angles are 0..4095 units per full turn everywhere else in the
    // engine (see e.g. FOLLOWCAM.CPP, INTEXT.CPP); callers here may pass an
    // unwrapped/negative value, and DrawHorizon2ZBuf's quadrant selection on
    // BetaCam only checks BetaCam >= 0, so an unwrapped negative angle left
    // TerrainTri uninitialized and crashed on first dereference.
    AlphaCam = alpha & 4095;
    BetaCam = beta & 4095;
    GammaCam = gamma & 4095;
    VueDistance = distance;
    Init3DExtView();
}

void lba2_renderer_set_draw_sky(int enabled)
{
    RendererDrawSky = enabled ? 1 : 0;
}

void lba2_renderer_set_view_distance(int startFog, int clipFar)
{
    RendererStartFog = startFog;
    RendererClipFar = clipFar;
    StartZFog = startFog;
    ClipZFar = clipFar;
    // SetFog() recomputes Fill_ZBuffer_Factor (POLY.CPP) from the far value;
    // without this the Z-buffer stays scaled for whatever distance was
    // current before, and depth comparisons past that range are unreliable.
    SetFog(startFog, clipFar);
}

const unsigned char *lba2_renderer_framebuffer(int *width, int *height, int *pitch)
{
    if (width) *width = ModeDesiredX;
    if (height) *height = ModeDesiredY;
    if (pitch) *pitch = ModeDesiredX;
    return (const unsigned char *)Log;
}

int lba2_renderer_get_actor_count(void)
{
    return RendererGetActorCount();
}

int lba2_renderer_get_actor(int index, int *x, int *y, int *z, int *waypointCount)
{
    return RendererGetActor(index, x, y, z, waypointCount);
}

int lba2_renderer_get_actor_waypoint(int actorIndex, int waypointIndex, int *x, int *y, int *z)
{
    return RendererGetActorWaypoint(actorIndex, waypointIndex, x, y, z);
}

