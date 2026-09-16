// Renderer-only: extracts actor (NPC) initial positions and patrol-route
// waypoints for every exterior scene on a given island, for the editor's
// minimap. This is a *loading-side* extraction -- it calls the real
// LoadScene() (DISKFUNC.CPP) to get real actor/track data, but never runs
// the life-script VM (DoLife) or the track VM (DoTrack); it walks each
// actor's track bytecode structurally (mirroring GERETRAK.CPP's
// SearchOffsetTrack opcode-length table) to collect the waypoints it visits,
// without simulating movement.
#include "C_EXTERN.H"
#include "DIRECTORIES.H"
#include "HQR_NAMES.H"
#include "COMMON.H"

#include <SYSTEM/HQRLOAD.H>
#include <vector>

extern S32 LoadScene(S32 numscene);

// Matches HOLO.H's SCE ("SizeXZ Cube Exterieur"): each exterior cube spans
// 32768 world units on X/Z. Duplicated from RENDERER_API.CPP's own
// WORLD_CUBE_SIZE rather than shared, to keep this file's only dependency on
// the renderer API boundary at zero.
#define ACTORS_WORLD_CUBE_SIZE 32768

struct RendererActor {
    S32 X, Y, Z;
    S32 WaypointStart;
    S32 WaypointCount;
};

static std::vector<RendererActor> g_actors;
static std::vector<S32> g_waypointX;
static std::vector<S32> g_waypointY;
static std::vector<S32> g_waypointZ;

// Peeks a scene's header (island, cube X/Y, interior/exterior). DISKFUNC.CPP's
// own LoadSceneCubeXY() does this into the scratch ScreenAux buffer, but
// ScreenAux is a fixed 640x480-ish budget (MEM.CPP) sized for its original
// purpose as a spare screen buffer, not for scene data -- PtrScene is the
// buffer actually sized for the largest possible scene (MEM.CPP reads that
// size from SCENE.HQR entry 0, "SizeCube.MAX"). Load_HQR decompresses the
// *entire* entry regardless of how little of the result the caller reads
// back, so peeking a large late-game scene into ScreenAux silently overflows
// it -- this doesn't crash immediately (see PtrScene's own comment on why
// LoadScene doesn't have this problem), only later, on whatever heap
// operation next notices the corruption. Peeking into PtrScene instead is
// always safe by construction, and fine to reuse here since a non-matching
// peek's content is never read again and a matching one is immediately
// overwritten by the real LoadScene() call below anyway.
static bool PeekSceneCube(S32 numscene, S32 targetIsland, S32 *outCubeX, S32 *outCubeY)
{
    char tmpFilePath[ADELINE_MAX_PATH];
    GetResPath(tmpFilePath, ADELINE_MAX_PATH, SCENE_HQR_NAME);
    if (!Load_HQR(tmpFilePath, PtrScene, numscene + 1)) return false;

    U8 *p = PtrScene;
    U8 island = p[0];
    U8 cubeX = p[1];
    U8 cubeY = p[2];
    U8 cubeMode = p[5];
    if (cubeMode != CUBE_EXTERIEUR || island != (U8)targetIsland) return false;

    *outCubeX = cubeX;
    *outCubeY = cubeY;
    return true;
}

// Structural walk of one actor's track bytecode (GERETRAK.CPP's TM_*
// opcodes), collecting every waypoint index the TM_GOTO_POINT family
// references (resolved via the scene's ListBrickTrack pool) in program
// order. This is the same opcode-length table SearchOffsetTrack() uses to
// skip to a label; unlike that (trusted, shipped) code this only ever reads
// data supplied by whatever island/scene the editor has open, so it bails
// out on a byte budget or an unrecognized opcode instead of trusting TM_END
// to appear.
static void ExtractTrackWaypoints(T_OBJET *ptrobj, S32 baseX, S32 baseZ)
{
    if (!ptrobj->PtrTrack) return;
    S32 offset = 0;
    const S32 maxBytes = 8192;
    while (offset < maxBytes) {
        U8 *ptr = ptrobj->PtrTrack + offset;
        U8 macro = *ptr++;
        offset++;
        switch (macro) {
        case TM_GOTO_POINT:
        case TM_GOTO_POINT_3D:
        case TM_GOTO_SYM_POINT:
        case TM_POS_POINT: {
            U8 index = *ptr;
            if (index < NbBrickTrack) {
                g_waypointX.push_back(baseX + ListBrickTrack[index].X);
                g_waypointY.push_back(ListBrickTrack[index].Y);
                g_waypointZ.push_back(baseZ + ListBrickTrack[index].Z);
            }
            offset++;
            break;
        }
        case TM_LABEL:
        case TM_BODY:
        case TM_BACKGROUND:
        case TM_SET_FRAME:
        case TM_SET_FRAME_3DS:
        case TM_SET_START_3DS:
        case TM_SET_END_3DS:
        case TM_START_ANIM_3DS:
        case TM_WAIT_FRAME_3DS:
        case TM_VOLUME:
            offset++;
            break;
        case TM_ANIM:
        case TM_SAMPLE:
        case TM_SAMPLE_RND:
        case TM_SAMPLE_ALWAYS:
        case TM_SAMPLE_STOP:
        case TM_REPEAT_SAMPLE:
        case TM_SIMPLE_SAMPLE:
        case TM_WAIT_NB_ANIM:
        case TM_ANGLE:
        case TM_FACE_TWINSEN:
        case TM_OPEN_LEFT:
        case TM_OPEN_RIGHT:
        case TM_OPEN_UP:
        case TM_OPEN_DOWN:
        case TM_BETA:
        case TM_GOTO:
        case TM_SPEED:
        case TM_SPRITE:
        case TM_DECALAGE:
        case TM_FREQUENCE:
            offset += 2;
            break;
        case TM_ANGLE_RND:
        case TM_LOOP:
            offset += 4;
            break;
        case TM_WAIT_NB_SECOND:
        case TM_WAIT_NB_SECOND_RND:
        case TM_WAIT_NB_DIZIEME:
        case TM_WAIT_NB_DIZIEME_RND:
            offset += 5;
            break;
        case TM_PLAY_ACF: {
            U8 c;
            do { c = *ptr++; offset++; } while (c != 0 && offset < maxBytes);
            break;
        }
        case TM_END:
            return;
        default:
            return; // unrecognized opcode: stop rather than risk misreading
        }
    }
}

int RendererScanIslandActors(int islandIndex)
{
    g_actors.clear();
    g_waypointX.clear();
    g_waypointY.clear();
    g_waypointZ.clear();
    if (islandIndex < 0) return 0;

    S32 savedFlagLoadGame = FlagLoadGame;
    S32 savedLastCubeMode = LastCubeMode;
    // LoadScene() (DISKFUNC.CPP) fades to black and stops samples unless
    // CubeMode+LastCubeMode == 2 or FlagLoadGame is set; it also skips
    // LoadFile3D() (whose target BufferFile3D this renderer-only build never
    // populates -- see RENDERER_BOOT.CPP) when FlagLoadGame is set. Both are
    // exactly what a position/track-only scan wants.
    FlagLoadGame = TRUE;
    LastCubeMode = CUBE_EXTERIEUR;

    // Scene indices run 0..221 (222 real scenes; docs/SCENES.md's own table
    // ends at 221 despite its "223 game scenes" header line -- SCENE.HQR
    // entry numscene+1 tops out at 222, with entry 0 the "SizeCube.MAX"
    // metadata record PeekSceneCube/LoadScene never read as a scene).
    // Scanning through 222 requests entry 223, past the end of the archive;
    // Load_HQR doesn't reject it, and ExpandLZ segfaults decompressing
    // whatever garbage offset/size it finds past the real entry table.
    for (S32 scene = 0; scene <= 221; scene++) {
        S32 cubeX, cubeY;
        if (!PeekSceneCube(scene, islandIndex, &cubeX, &cubeY)) continue;
        if (!LoadScene(scene)) continue;

        S32 baseX = cubeX * ACTORS_WORLD_CUBE_SIZE;
        S32 baseZ = cubeY * ACTORS_WORLD_CUBE_SIZE;
        // Object 0 is always the hero (Twinsen); scene actors start at 1.
        for (S32 n = 1; n < NbObjets; n++) {
            T_OBJET *ptrobj = &ListObjet[n];
            RendererActor actor;
            actor.X = baseX + ptrobj->Obj.X;
            actor.Y = ptrobj->Obj.Y;
            actor.Z = baseZ + ptrobj->Obj.Z;
            actor.WaypointStart = (S32)g_waypointX.size();
            ExtractTrackWaypoints(ptrobj, baseX, baseZ);
            actor.WaypointCount = (S32)g_waypointX.size() - actor.WaypointStart;
            g_actors.push_back(actor);
        }
    }

    FlagLoadGame = savedFlagLoadGame;
    LastCubeMode = savedLastCubeMode;
    return (int)g_actors.size();
}

int RendererGetActorCount()
{
    return (int)g_actors.size();
}

int RendererGetActor(int index, int *x, int *y, int *z, int *waypointCount)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    const RendererActor &a = g_actors[(size_t)index];
    if (x) *x = a.X;
    if (y) *y = a.Y;
    if (z) *z = a.Z;
    if (waypointCount) *waypointCount = a.WaypointCount;
    return 1;
}

int RendererGetActorWaypoint(int actorIndex, int waypointIndex, int *x, int *y, int *z)
{
    if (actorIndex < 0 || actorIndex >= (int)g_actors.size()) return 0;
    const RendererActor &a = g_actors[(size_t)actorIndex];
    if (waypointIndex < 0 || waypointIndex >= a.WaypointCount) return 0;
    size_t i = (size_t)(a.WaypointStart + waypointIndex);
    if (x) *x = g_waypointX[i];
    if (y) *y = g_waypointY[i];
    if (z) *z = g_waypointZ[i];
    return 1;
}
