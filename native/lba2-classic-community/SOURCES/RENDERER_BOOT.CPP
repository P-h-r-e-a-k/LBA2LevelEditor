#include "C_EXTERN.H"
#include "DIRECTORIES.H"
#include "HQR_NAMES.H"
#include "OBJECT.H"
#include "AMBIANCE.H"

#include <SYSTEM/HQRLOAD.H>
#include <SYSTEM/HQRRESS.H>
#include <stdlib.h>

extern void LoadFicPerso(void);
extern void InitObjects(void *buffer, U32 size, Func_TransNumPtr *giveBody, Func_TransNumPtr *giveAnim);
extern void *GivePtrBody(S32 index);
extern void *GivePtrAnim(S32 index);
extern void ScanTextureAnimation(U8 *texture, S32 size);
extern void InitTextureAnimation(void);

static U8 *RendererBufferAnim;
static U8 *RendererBufferFile3D;
static U8 *RendererBufferTexture;
static U8 *RendererPtrZvExtra;
static U8 *RendererPtrZvExtraRaw;
static U8 *RendererPtrZvAnim3DS;

int RendererBootAssets(void)
{
    char path[ADELINE_MAX_PATH];

    GetResPath(path, ADELINE_MAX_PATH, RESS_HQR_NAME);
    PtrPalNormal = (U8 *)LoadMalloc_HQR(path, RESS_PAL);
    if (!PtrPalNormal) return 0;
    ChoicePalette();

    RendererBufferAnim = (U8 *)malloc(SIZE_BUFFER_ANIM);
    if (!RendererBufferAnim) return 0;

    RendererBufferFile3D = (U8 *)malloc(HQF_ResSize(path, RESS_FILE3D) + RECOVER_AREA);
    if (!RendererBufferFile3D) return 0;
    if (!Load_HQR(path, RendererBufferFile3D, RESS_FILE3D)) return 0;

    LoadFicPerso();
    InitObjects(RendererBufferAnim, SIZE_BUFFER_ANIM, GivePtrBody, GivePtrAnim);

    RendererBufferTexture = (U8 *)LoadMalloc_HQR(path, RESS_TEXTURES);
    if (!RendererBufferTexture) return 0;
    ScanTextureAnimation(RendererBufferTexture, 256);
    InitTextureAnimation();

    RendererPtrZvExtra = (U8 *)LoadMalloc_HQR(path, RESS_GOODIES_GPC);
    RendererPtrZvExtraRaw = (U8 *)LoadMalloc_HQR(path, RESS_GOODRAW_GPC);
    if (!RendererPtrZvExtra || !RendererPtrZvExtraRaw) return 0;
#ifndef DEMO
    RendererPtrZvAnim3DS = (U8 *)LoadMalloc_HQR(path, RESS_ANIM3DS_GPC);
#endif

    GetResPath(path, ADELINE_MAX_PATH, ANIM3DS_HQR_NAME);
    HQRPtrAnim3DS = HQR_Init_Ressource(path, Anim3DSMem, 100);
    if (!HQRPtrAnim3DS) return 0;

    // PERSO.CPP normally owns this allocation; it is excluded from the
    // renderer-only build, so TERRAIN.CPP's animated water path would
    // otherwise write through a null PtrPolySea/PtrAniPoly whenever a
    // loaded cube has an animated water polygon (CodeJeu & 12 == 12).
    PtrPolySea = (S_ANI_POLY *)Malloc(SIZE_BUFFER_POLY_SEA);
    if (!PtrPolySea)
        MaxPolySea = 0;

    return 1;
}
