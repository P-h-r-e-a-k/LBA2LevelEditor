#include "RENDERER_API.H"
#include "DEFINES.H"
#include "EXTERN.H"
#include "LOADISLE.H"
#include "DIRECTORIES.H"
#include "HQR_NAMES.H"
#include "COMMON.H"
#include <SYSTEM/EVENTS.H>
#include "MEM.H"
#include <SDL3/SDL.h>

extern void AdjustHQRMem(void);
#include <string.h>
#include <direct.h>
#include <stdlib.h>

extern void AffGrilleExt(void);
extern void Init3DExtView(void);
extern void Init3DExtGame(void);
extern int RendererBootAssets(void);

int lba2_renderer_version(void)
{
    return 1;
}

int lba2_renderer_initialize(void)
{
    IsleMapIndex = (U8 *)malloc(256);
    GroundTexture = (U8 *)malloc(256 * 256 + RECOVER_AREA);
    ObjTexture = (U8 *)malloc(256 * 256 + RECOVER_AREA);
    ListTriExt = (T_LIST_TRI *)malloc(sizeof(T_LIST_TRI) * MAX_OBJ_DECORS);
    if (!IsleMapIndex || !GroundTexture || !ObjTexture || !ListTriExt) return 0;
    if (!SDL_WasInit(SDL_INIT_VIDEO) && !SDL_InitSubSystem(SDL_INIT_VIDEO)) return 0;
    Mem_ConfigureScreenBuffers(640, 480);
    AdjustHQRMem();
    InitMainBuffer();
    ModeDesiredX = 640;
    ModeDesiredY = 480;
    Log = malloc(640 * 480 + 512);
    if (!Log) return 0;
    InitMemory();
    if (!RendererBootAssets()) return 0;
    if (!InitEvents()) return 0;
    return InitExternalView();
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
    free(Log); Log = NULL;
    free(IsleMapIndex); IsleMapIndex = NULL;
    free(GroundTexture); GroundTexture = NULL;
    free(ObjTexture); ObjTexture = NULL;
    free(ListTriExt); ListTriExt = NULL;
}

int lba2_renderer_load_island(const char *name)
{
    if (!name || !name[0]) return 0;
    strncpy(IslandName, name, ADELINE_MAX_PATH - 1);
    IslandName[ADELINE_MAX_PATH - 1] = '\0';
    LoadIsland();

    char path[ADELINE_MAX_PATH];
    GetResPath(path, ADELINE_MAX_PATH, RESS_HQR_NAME);
    S32 skyResource = RESS_SKYSEA0;
    if (!strcasecmp(name, "desert")) skyResource = RESS_SKYSEA2;
    else if (!strcasecmp(name, "emeraude")) skyResource = RESS_SKYSEA3;
    else if (!strcasecmp(name, "otringal")) skyResource = RESS_SKYSEA4;
    else if (!strcasecmp(name, "celebrat") || !strcasecmp(name, "celebra2")) skyResource = RESS_SKYSEA5;
    else if (!strcasecmp(name, "platform")) skyResource = RESS_SKYSEA6;
    else if (!strcasecmp(name, "mosquibe")) skyResource = RESS_SKYSEA7;
    else if (!strcasecmp(name, "knartas")) skyResource = RESS_SKYSEA8;
    else if (!strcasecmp(name, "ilotcx")) skyResource = RESS_SKYSEA9;
    else if (!strcasecmp(name, "ascence")) skyResource = RESS_SKYSEA10;
    else if (!strcasecmp(name, "citabau")) skyResource = RESS_SKYSEA00;
    if (!Load_HQR(path, SkySeaTexture, skyResource)) return 0;
    return 1;
}

int lba2_renderer_load_cube(int x, int y)
{
    CurrentCubeX = x;
    CurrentCubeY = y;
    S32 result = LoadCube(x, y);
    if (result) Init3DExtGame();
    return result;
}

int lba2_renderer_render_frame(void)
{
    AffGrilleExt();
    return 1;
}

void lba2_renderer_set_camera(int alpha, int beta, int gamma, int distance)
{
    AlphaCam = alpha;
    BetaCam = beta;
    GammaCam = gamma;
    VueDistance = distance;
    Init3DExtView();
}

const unsigned char *lba2_renderer_framebuffer(int *width, int *height, int *pitch)
{
    if (width) *width = ModeDesiredX;
    if (height) *height = ModeDesiredY;
    if (pitch) *pitch = ModeDesiredX;
    return (const unsigned char *)Screen;
}

