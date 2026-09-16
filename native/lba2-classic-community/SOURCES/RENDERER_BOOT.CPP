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

int RendererBootAssets(void)
{
    char path[ADELINE_MAX_PATH];

    GetResPath(path, ADELINE_MAX_PATH, RESS_HQR_NAME);
    PtrPalNormal = (U8 *)LoadMalloc_HQR(path, RESS_PAL);
    if (!PtrPalNormal) return 0;
    ChoicePalette();

    // These populate the same engine globals (C_EXTERN.H / GLOBAL.CPP) that
    // PERSO.CPP normally fills during boot. Earlier versions of this function
    // loaded into separate local buffers instead, leaving BufferAnim,
    // BufferFile3D, BufferTexture, PtrZvExtra, PtrZvExtraRaw, and PtrZvAnim3DS
    // allocated (via InitMemory/AdjustLba2Mem) but never written -- readers
    // such as SearchAnim() then walked off into uninitialized heap memory.
    BufferAnim = (U8 *)SmartMalloc(SIZE_BUFFER_ANIM);
    if (!BufferAnim) return 0;

    if (!Load_HQR(path, BufferFile3D, RESS_FILE3D)) return 0;

    LoadFicPerso();

    // GivePtrBody()/GivePtrAnim() (OBJECT.CPP) read through the HQR_Bodys/
    // HQR_Anims cache handles PERSO.CPP normally opens at boot; left NULL,
    // HQR_Get() hands back a garbage pointer the first time a decor object
    // needs an actual body model (e.g. an animated actor/prop -- CELEBRA2's
    // celebration decor set has some, plain terrain decors on most other
    // islands don't), and Return_BodyDisplay() crashes dereferencing it.
    // AnimMem/BodyMem/ObjFixMem are sized by AdjustHQRMem() above.
    char hqrPath[ADELINE_MAX_PATH];
    GetResPath(hqrPath, ADELINE_MAX_PATH, ANIM_HQR_NAME);
    HQR_Anims = HQR_Init_Ressource(hqrPath, AnimMem, AnimMem / 800);
    if (!HQR_Anims) return 0;

    GetResPath(hqrPath, ADELINE_MAX_PATH, BODY_HQR_NAME);
    HQR_Bodys = HQR_Init_Ressource(hqrPath, BodyMem, BodyMem / 1000);
    if (!HQR_Bodys) return 0;

    GetResPath(hqrPath, ADELINE_MAX_PATH, OBJFIX_HQR_NAME);
    HQR_ObjFix = HQR_Init_Ressource(hqrPath, ObjFixMem, ObjFixMem / 1000);
    if (!HQR_ObjFix) return 0;

    InitObjects(BufferAnim, SIZE_BUFFER_ANIM, GivePtrBody, GivePtrAnim);

    BufferTexture = (U8 *)LoadMalloc_HQR(path, RESS_TEXTURES);
    if (!BufferTexture) return 0;
    ScanTextureAnimation(BufferTexture, 256);
    InitTextureAnimation();

    PtrZvExtra = (S16 *)LoadMalloc_HQR(path, RESS_GOODIES_GPC);
    PtrZvExtraRaw = (S16 *)LoadMalloc_HQR(path, RESS_GOODRAW_GPC);
    if (!PtrZvExtra || !PtrZvExtraRaw) return 0;
#ifndef DEMO
    PtrZvAnim3DS = (S16 *)LoadMalloc_HQR(path, RESS_ANIM3DS_GPC);
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
