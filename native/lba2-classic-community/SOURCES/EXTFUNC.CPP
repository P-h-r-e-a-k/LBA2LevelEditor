/*══════════════════════════════════════════════════════════════════════════*
		 ████┐  █████┐          ██████┐ ██┐ ██┐ ██████┐
		██┌─██┐ ██┌─██┐         ██┌───┘ ██│ ██│ └─██┌─┘
		└─┘██┌┘ ██│ ██│         █████┐  └████┌┘   ██│
		██┐└██┐ ██│ ██│         ██┌──┘  ██┌─██┐   ██│
		└████┌┘ █████┌┘ ██████┐ ██████┐ ██│ ██│   ██│
		 └───┘  └────┘  └─────┘ └─────┘ └─┘ └─┘   └─┘
 *══════════════════════════════════════════════════════════════════════════*/

#include "C_EXTERN.H"
#include "DIRECTORIES.H"
#include "INPUT.H"

#include <POLYGON/POLY_JMP.H>

#include <stdio.h>
#include <stdarg.h>
#include <time.h>
#include <algorithm>
#include <cmath>

// Always-on, best-effort debug trail for tracing intermittent crashes --
// the ones that only show up occasionally (confirmed: the very first
// couple of times an actor was added and its Body/Animation dropdown
// clicked, never once reproduced after) are exactly the kind a debugger
// attached after the fact can't catch, so this exists to have already
// written down what was happening right before one happens. Opens,
// writes one line, and closes the file on every single call rather than
// keeping a handle open -- a native crash (an access violation, not a
// catchable C++ exception) never gets a chance to flush a buffered
// write, so leaving anything buffered risks losing exactly the last
// lines that would matter most. In the project folder rather than a
// per-user AppData path so it's easy to find without hunting for it.
// Logs to the SAME file the C# side's own DebugLog writes to
// (E:\dump\LBA2LevelEditor\debug.log, RendererLibraryApi.cs/App.xaml.cs),
// interleaved by wall-clock time, so a crash's last few lines show both
// what the UI was doing and what the native engine was doing at once.
// The file is named by the LBA2_EDITOR_DEBUG_LOG environment variable (the editor's
// debug builds set it to the shared log); with it unset nothing is written.
#ifdef _WIN32
extern "C" __declspec(dllimport) unsigned long __stdcall GetEnvironmentVariableA(const char *name, char *buffer, unsigned long size);
#endif

static const char *NativeDebugLogPath()
{
    static char path[512];
    static bool resolved = false;
    if (!resolved) {
        path[0] = 0;
#ifdef _WIN32
        unsigned long n = GetEnvironmentVariableA("LBA2_EDITOR_DEBUG_LOG", path, sizeof(path));
        if (n == 0 || n >= sizeof(path)) path[0] = 0;
#else
        const char *env = getenv("LBA2_EDITOR_DEBUG_LOG");
        if (env && strlen(env) < sizeof(path)) strcpy(path, env);
#endif
        resolved = true;
    }
    return path[0] ? path : NULL;
}

void NativeDebugLog(const char *fmt, ...)
{
    const char *logPath = NativeDebugLogPath();
    if (!logPath) return;
    FILE *f = fopen(logPath, "a");
    if (!f) return;
    time_t now = time(NULL);
    struct tm *lt = localtime(&now);
    char stamp[16];
    strftime(stamp, sizeof(stamp), "%H:%M:%S", lt);
    fprintf(f, "[%s] [native] ", stamp);
    va_list args;
    va_start(args, fmt);
    vfprintf(f, fmt, args);
    va_end(args);
    fprintf(f, "\n");
    fclose(f);
}

#include <SVGA/SCREEN.H>
#include <SYSTEM/KEYBOARD_KEYS.H>
#include <SYSTEM/MOUSE.H>
#include <SYSTEM/WINDOW.H>
#include "FOLLOWCAM.H"
#include "FOLLOWCAM_CFG.H"
#include "FOLLOWCAM_MATH.H"
#include "JOYSTICK.H"
#include "BOOT_EXIT.H"

extern S32 Nxw, Nyw, Nzw;
extern S32 SaveNxw, SaveNzw;
extern S32 OldX, OldY, OldZ;

extern S32 NbTriObj; // OBJECT.CPP

extern T_OBJET *APtObj;

extern U8 AnimNumObj;
extern S32 YMap;
extern S32 Col1;

S32 FlagHeroOutX = FALSE; // Twinsen sort du cube en X
S32 FlagHeroOutZ = FALSE; // Twinsen sort du cube en Z

T_DECORS *PtrDecorsCol = 0; // Objet du decors en collision
S16 NumDecorsCol = -1;      // avec l'objet courant

U8 *PtrNuances; // Nuances gouraud

S32 LastIsland = -1;

#ifdef LBA_EDITOR
char *GenAnimSkate; /* identificateur anim en cours */
#else
U16 GenAnimSkate; /* identificateur anim en cours */
#endif

#define SKATING_TIME_OUT (25 * 20) // 1/2s
S32 TimerSkating;

/*──────────────────────────────────────────────────────────────────────────*/
/*──────────────────────────────────────────────────────────────────────────*/

/*══════════════════════════════════════════════════════════════════════════*
			    █▀▀▀▀ █▀▀▀█ █▄ ▄█ █▀▀▀▀
			    ██ ▀█ ██▀▀█ ██▀ █ ██▀▀
			    ▀▀▀▀▀ ▀▀  ▀ ▀▀  ▀ ▀▀▀▀▀
 *══════════════════════════════════════════════════════════════════════════*/

// Initialisation des buffers nécessaires à la 3D Extérieur: a appeler une
// seule fois en début de programme (PERSO.C)
void Init3DExt() {
    InitExternalView(); // mallocs

    /*#ifdef	LBA_EDITOR
	ScreenAux		= (U8 *)Malloc( 640*480+RECOVER_AREA ) ;
	if( !ScreenAux )	TheEnd( NOT_ENOUGH_MEM, "ScreenAux (Init3DExt)" ) ;
#endif
*/
    // Faire pointer sur un autre buffer
    ListTriExt = (T_LIST_TRI *)Malloc(sizeof(T_LIST_TRI) * MAX_OBJ_DECORS);
    if (!ListTriExt)
        TheEnd(NOT_ENOUGH_MEM, "ListTriExt (Init3DExt)");

#ifdef LBA_EDITOR
    strcpy(IslandName, GetRessName("citadele"));
#else
    strcpy(IslandName, "citadele");
#endif
}

/*──────────────────────────────────────────────────────────────────────────*/
// Restaure buffers alloués pour la 3D Extérieur: appelé en quittant le
// programme (PERSO.C)
void Clear3dExt() {
    // libere memoire
    ClearExternalView();
}

/*──────────────────────────────────────────────────────────────────────────*/
// Positionne la caméra pour la 3d Extérieure
void Init3DExtView(void) {
    // Derive the projection origin from the framebuffer dimensions so the 3D
    // world centres on the rendered area for any render-space width/height.
    // At the default 640x480 this is the original (320, 240) — the hardcoded
    // literal Phase 0 of the widescreen plan flagged; see docs/WIDESCREEN.md
    // and docs/WIDESCREEN_PROJECTION_AUDIT.md.
    // Near clip scales down as the framebuffer grows taller. The focal is fixed
    // (ChampX/ChampZ), so a taller frame reveals more vertical FOV, which pulls
    // the bottom of the frustum's ground intersection closer to the camera. The
    // terrain trivial-rejects whole cells whose depth is within the near plane
    // (zr <= NearClip in TERRAIN.CPP MaskVisible), so a fixed plane drops near
    // terrain/buildings that are plainly in view at HD ("clipped on the right").
    // Scale it with height to keep the near plane at the original screen-space
    // position. Exact CLIP_NEAR at the original 480, so 480 is unchanged.
    S32 nearClip = CLIP_NEAR;
    if (ModeDesiredY > 480)
        nearClip = CLIP_NEAR * 480 / (S32)ModeDesiredY;
    SetProjection((S32)(ModeDesiredX / 2), (S32)(ModeDesiredY / 2),
                  nearClip, ChampX, ChampZ);
    SetFollowCamera(VueOffsetX, VueOffsetY, VueOffsetZ,
                    AlphaCam, BetaCam, GammaCam,
                    VueDistance);

    SetLightVector(AlphaLight, BetaLight, 0);
}

/*──────────────────────────────────────────────────────────────────────────*/
// a appeler a chaque fois que l'on change de cube en 3d exterieur
// pour réinitialiser la caméra et les pointeurs de fonctions
void Init3DExtGame() {
    Init3DExtView();

    // Initialise les pointeurs de fonctions
#ifndef COMPILATOR
    PtrReajustPos = ReajustPosExt;
    PtrWorldColBrick = WorldColBrickExt;
    PtrWorldColBrickVisible = WorldColBrickVisibleExt;
    PtrProjectShadow = ProjectShadowExt;
    PtrReajustPosExtra = ReajustPosExtraExt;
#ifndef LBA_EDITOR
    PtrFullWorldColBrick = FullWorldColBrickExt;
#endif
#endif

    PtrGetShadow = GetShadowExt;
    PtrDoAnim = DoAnimExt;
    PtrInitGrille = InitGrilleExt;
    PtrInit3DView = Init3DExtView;
}

/*══════════════════════════════════════════════════════════════════════════*
			       █▄ ▄█ █▀▀▀█ █▀▀▀█
			       ██▀ █ ██▀▀█ ██▀▀▀
			       ▀▀  ▀ ▀▀  ▀ ▀▀
 *══════════════════════════════════════════════════════════════════════════*/

// Charge grille lors d'un changement de cube
#ifdef LBA_EDITOR
void InitGrilleExt(char *defpathname) {
    S32 numskysea;
    char tmpFilePath[ADELINE_MAX_PATH];

    InitDefFile(defpathname);
    CubeMode = DefFileBufferReadValue("Mode");

    if (CubeMode != CUBE_EXTERIEUR) {
        MessageF(TRUE, "Erreur: cube %s non exterieur !", defpathname);
        return;
    }

    if (NumCube != NUM_CUBE_PHANTOM) {
        strcpy(IslandName, DefFileBufferReadStringDefault("Island", "citadel"));
        if (strchr(IslandName, ';'))
            *(char *)strchr(IslandName, ';') = '\0';
    } else
        Island = PhantomIsland;

    numskysea = RESS_SKYSEA0 + Island;

    // GROSSE Rustine pour charger la citadelle quand il fait beau ou
    // quand il pleut
    if (Island == 0 AND TEMPETE_FINIE
                      AND strstr(IslandName, "citadel")) {
        strcpy(IslandName, GetRessName("citabau"));
        LastIsland = 1;
        numskysea = RESS_SKYSEA00;
    } else {
        if (Island == 5 AND ListVarGame[FLAG_CELEBRATION].Value) {
            strcpy(IslandName, GetRessName("celebra2"));
            LastIsland = 1;
        }
    }

    if (NumCube != NUM_CUBE_PHANTOM) {
        CurrentCubeX = DefFileBufferReadValue("CubeX");
        CurrentCubeY = DefFileBufferReadValue("CubeY");

        if (LastIsland != Island) {
            GetResPath(tmpFilePath, ADELINE_MAX_PATH, RESS_HQR_NAME);
            Load_HQR(tmpFilePath, SkySeaTexture, numskysea);
            LoadIsland();

            LastIsland = Island;
        }

        LoadCube(CurrentCubeX, CurrentCubeY);
        FixeObjetsDecorsInvisibles();
    } else {
        CurrentCubeX += PhantomX;
        CurrentCubeY += PhantomY;

        InitSeaCube();
    }
}
#else

const char *IleLst[] = {
    "citadel",
    "sendell",
    "desert",
    "emeraude",
    "otringal",
    "celebrat",
    "platform",
    "mosquibe",
    "knartas",
    "ilotcx",
    "ascence",
    "souscelb",
};

void InitGrilleExt(U32 numcube) {
    S32 numskysea;
    char tmpFilePath[ADELINE_MAX_PATH];

    // Pour virer Warning
    numcube = numcube;

    if (NumCube == NUM_CUBE_PHANTOM) {
        Island = PhantomIsland;
    }

    // GROSSE Rustine pour charger la citadelle quand il fait beau ou
    // quand il pleut
    switch (Island) {
    case 0:
        if (TEMPETE_FINIE) {
            if (!strstr(IslandName, "citabeau"))
                LastIsland = 1; // pour que le chargement s'effectue

            strcpy(IslandName, "citabau");
            numskysea = RESS_SKYSEA00;
        } else {
            if (!strstr(IslandName, "citadel"))
                LastIsland = 1; // pour que le chargement s'effectue

            strcpy(IslandName, "citadel");

            numskysea = RESS_SKYSEA0;
        }
        break;

    case 5:
        if (ListVarGame[FLAG_CELEBRATION]) {
            if (!strstr(IslandName, "celebra2"))
                LastIsland = 1; // pour que le chargement s'effectue

            strcpy(IslandName, "celebra2");
        } else {
            if (!strstr(IslandName, "celebrat"))
                LastIsland = 1; // pour que le chargement s'effectue

            strcpy(IslandName, "celebrat");
        }
        numskysea = RESS_SKYSEA5;
        break;

    default:
        //			DefFileBufferInit( "ile.lst", Screen, 640*480 ) ;

        strcpy(IslandName, "");
        //			strcat( IslandName, DefFileBufferReadStringDefault( Itoa( Island ), "citadel" ) ) ;
        strcat(IslandName, IleLst[Island]);

        if (strchr(IslandName, ';'))
            *(char *)strchr(IslandName, ';') = '\0';

        numskysea = RESS_SKYSEA0 + Island;
    }

    if (NumCube != NUM_CUBE_PHANTOM) {
        if (LastIsland != Island) {
            GetResPath(tmpFilePath, ADELINE_MAX_PATH, RESS_HQR_NAME);
            Load_HQR(tmpFilePath, SkySeaTexture, numskysea);

            LoadIsland();
            LastIsland = Island;
        }

        ManageSystem(); // for streaming

        LoadCube(CurrentCubeX, CurrentCubeY);

        ManageSystem(); // for streaming

        FixeObjetsDecorsInvisibles();
    } else {
#ifdef DEMO
        // Grosse rustine pour ne pas voir les plaques d'égouts
        ListVarGame[164] = 1;
        ListVarGame[165] = 1;
#endif
        InitSeaCube();
    }
}
#endif

/*──────────────────────────────────────────────────────────────────────────*/
// Affiche la grille complète à la position actuelle de la caméra
// Draws every non-hero object's real 3D body/anim (ObjectDisplay --
// OBJECT/AFF_OBJ.H) into the same Z-buffered pass terrain and static decor
// already draw into (AffichageObjetDecorsZBuf(), called from inside
// AffichageTerrainZBuf() itself), so actors correctly occlude and get
// occluded by both. This is the same rotate/project/set-interframe/display
// sequence AffOneObject() (OBJECT.CPP) uses for the TYPE_OBJ_3D case and
// CREDITS.CPP's minimal actor-drawing loop uses directly, without
// AffOneObject's T_SORT/shadow/dirty-rect/background-copy bookkeeping --
// none of that is needed for a single full redraw with no previous frame to
// diff against.
//
// Requires ListObjet/NbObjets to already reflect the currently-loaded
// cube's scene with each object's body initialized (see
// RendererLoadCubeScene() in RENDERER_ACTORS.CPP, called from
// lba2_renderer_load_cube()); if that never ran (e.g. NbObjets is still 0
// or 1 from boot), this is a no-op.
extern U8 Fill_Logical_Palette[256]; // POLY.H -- SetCLUT's own final "raw colour index -> displayed index" remap table

// Tracks, per ListObjet slot, whether AffichageActorsZBuf should render
// that slot as a flat black placeholder silhouette instead of its real
// body/anim -- see ObjectDisplayForcedBlack's own comment for why, and
// RENDERER_ACTORS.CPP's own SetPlaceholderBodySlot/ClearAllPlaceholderBodySlots
// (RendererAddActor/RendererSetActorAttributes/ReapplyPendingEditsToListObjet)
// for who actually sets it. Owned here, in the shared engine core, rather
// than exposing a getter FROM the 3DEXT/editor-only layer for the engine
// to call INTO -- EXTFUNC.CPP and RENDERER_ACTORS.CPP link into separate
// static libraries for this build (confirmed the hard way: a call the
// other way around linked cleanly from RENDERER_API.CPP, in the same
// library as RENDERER_ACTORS.CPP, but the identical call from here failed
// with "undefined reference" even after a full clean rebuild), so 3DEXT
// pushing into a plain array the engine owns is the direction that
// actually links, matching how every other engine/3DEXT boundary in this
// file already works (3DEXT calls into the engine's own ObjectInitBody/
// ListObjet/etc., never the other way around).
static bool g_placeholderBodySlot[MAX_OBJETS];

void SetPlaceholderBodySlot(S32 slot, int isPlaceholder)
{
    if (slot >= 0 && slot < MAX_OBJETS) g_placeholderBodySlot[slot] = isPlaceholder != 0;
}

void ClearAllPlaceholderBodySlots(void)
{
    memset(g_placeholderBodySlot, 0, sizeof(g_placeholderBodySlot));
}

// Renders obj with its polygons forced toward a single flat colour (BLACK,
// DEFINES.H -- palette index 0, the conventional black entry) by
// overriding Fill_Logical_Palette -- the table every *flat/gouraud* fill
// routine reads as its own last step before writing a pixel (POLY.CPP/
// POLYGOUR.ASM/POLYDISC.ASM) -- for the duration of this one object's own
// ObjectDisplay() call, then restoring it immediately after. Confirmed by
// testing: this correctly blacks out flat-shaded areas (skin, limbs), but
// a textured area (e.g. Twinsen's own sweater) keeps its own colours --
// its fill path resolves texture pixels through something this override
// doesn't reach, not yet tracked down. Good enough for a placeholder that
// only needs to read as "obviously not a real body yet" rather than a
// pixel-perfect silhouette; see RendererAddActor's own IsPlaceholderBody
// comment for the full intent. Being a pure render-time override, it never
// touches BODY.HQR or this object's own actual body/anim data regardless.
static void ObjectDisplayForcedBlack(T_OBJ_3D *obj)
{
    U8 saved[256];
    memcpy(saved, Fill_Logical_Palette, sizeof(saved));
    memset(Fill_Logical_Palette, 0, sizeof(Fill_Logical_Palette)); // 0 == BLACK, DEFINES.H
    ObjectDisplay(obj);
    memcpy(Fill_Logical_Palette, saved, sizeof(saved));
}

void AffichageActorsZBuf(void) {
    for (S32 n = 1; n < NbObjets; n++) {
        T_OBJET *ptrobj = &ListObjet[n];

        // Sprite-based objects (pickups, flat decor) need a different draw
        // call (InitSprite's path, not InitBody's) -- see StartInitObj's own
        // SPRITE_3D branch. Left for later; this covers real 3D-bodied NPCs.
        if (ptrobj->Flags & SPRITE_3D) continue;
        if (ptrobj->Obj.Body.Num < 0) continue; // no body assigned/loaded

        LongWorldRotatePoint(ptrobj->Obj.X, ptrobj->Obj.Y, ptrobj->Obj.Z);
        if (!LongProjectPoint(X0, Y0, Z0)) continue;

        S32 zr = CameraZr - Z0;
        if (zr > ClipZFar) continue;

        SetCLUT(PalLevel + 16 * BoundRegleTrois(0, 15, ClipZFar - StartZFog, zr - StartZFog));
        Switch_Fillers(FILL_POLY_FOG_ZBUFFER);

        if (!(ptrobj->WorkFlags & MANUAL_INTER_FRAME)) {
            ObjectSetInterFrame(&ptrobj->Obj);
        }

        UnsetClip();
        if (g_placeholderBodySlot[n]) {
            ObjectDisplayForcedBlack(&ptrobj->Obj);
        } else {
            ObjectDisplay(&ptrobj->Obj);
        }
    }
}

// Scratch object slot for AffichageBodyPreview() below -- beyond any real
// actor (MAX_OBJETS-1), so it never collides with NbObjets/ListObjet[0..
// NbObjets) or disturbs the real scene.
#define BODY_PREVIEW_OBJ (MAX_OBJETS - 1)

extern void InitObject(U8 numobj);
extern void ObjectInitBody(T_OBJ_3D *obj, void *body);
extern void ObjectInitAnim(T_OBJ_3D *obj, void *anim);
extern S32 ObjectSetInterDep(T_OBJ_3D *obj);
extern void *GivePtrBody(S32 index);
extern void *GivePtrAnim(S32 index);
extern U8 *BufferTexture; // GLOBAL.CPP -- the character/body texture page (RESS_TEXTURES, loaded by RendererBootAssets); see AffichageBodyPreview's own call site for why this needs handing to obj->Texture explicitly
extern bool g_disableBackfaceCulling;
extern bool g_previewFlattenTransparent; // see AFF_OBJ.CPP's own comment
extern volatile S32 TimerLock; // TIMERWIN.CPP -- see AffichageBodyPreview's own comment on why this gets force-cleared there
extern void ChoicePalette(void); // AMBIANCE.CPP -- see AffichageBodyPreview's own call site for why this runs every call

// Renders one body, alone against a cleared background, for the actor-
// attributes editor's preview panel -- reuses AffichageActorsZBuf's own
// per-object draw sequence (immediately above) for the scratch slot, with
// the *camera* orbiting around it (cameraBeta) rather than the object
// itself. (Rotating the object's own Beta instead was tried -- expecting
// it to be an equivalent way to spin the view -- but composing that with
// this camera's own alpha/gamma setup tipped the whole body onto a
// diagonal instead of a level turntable spin; camera-orbit doesn't have
// that composition to go wrong, so it stayed.) Deliberately touches no
// terrain/sky/decor/real-actor state, so it's safe
// to call between ordinary frames -- whatever real render happens next
// clears the screen and resets the camera fully anyway, exactly like
// RenderIslandTopDown's own minimap snapshots already do to this same
// shared framebuffer. Returns 0 if the body/anim index doesn't resolve to
// anything loadable, 1 on a successful draw.
//
// body/anim are raw HQR archive indices (BODY.HQR/ANIM.HQR), not the
// "generic" per-actor indices GenBody/GenAnim normally hold everywhere
// else in this file. Those generic indices are only meaningful relative
// to one specific object's own PtrFile3D conversion table (SearchBody/
// SearchAnim, FICHE.CPP, walk that table to translate a small per-object
// generic number into the real archive index) -- a scratch preview object
// has no such table of its own (it was borrowing actor slot 1's, which
// happens to have a completely different generic numbering than whatever
// the editor's own body/anim pickers list, since those are built from the
// archives directly). Bypassing InitBody()/InitAnim()'s generic lookup
// entirely and calling ObjectInitBody()/ObjectInitAnim() (BODY.CPP/
// ANIM.CPP) straight with the archive index sidesteps that mismatch --
// same primitives InitBody/InitAnim themselves call after resolving a
// generic index down to one of these.
// True if animPtr's own group count fits the fixed-size buffer
// ObjectInitAnim/ObjectSetInterFrame interpolate into (T_OBJ_3D::
// CurrentFrame[30], AFF_OBJ.H -- 30 T_GROUP_INFO entries of 4 S16 each, so
// 60 U32-equivalents; ObjectInitAnim copies animGroups*2-2 of those, capping
// animGroups at 31). Originally this compared against the *body's* own
// group count instead (the theory being that INTFRAME.CPP's interpolation
// loop, driven by the animation's group count with no clamp of its own,
// could walk past a smaller body's own group data) -- but that loop only
// ever reads the animation's own keyframe arrays and writes into this fixed
// CurrentFrame buffer, never touching the body's point/group data directly,
// so a body with *fewer* groups than the animation is fine (the extra
// interpolated groups simply have no body geometry to apply to). Comparing
// against the body was actively wrong: it rejected ordinary pairings like a
// body's own default idle animation (confirmed by logging body 0 at 19
// groups against animation 0 at 24, both perfectly normal, silently
// treated as "incompatible" and left rendering nothing). Duplicated in
// RENDERER_ACTORS.CPP's ApplyRawBodyAnim for real actors, same reasoning.
static bool AnimFitsBody(const void *bodyPtr, const void *animPtr)
{
    // ObjectInitAnim's own read (LIB386/ANIM/ANIM.CPP): animPtr[1], a U16.
    U16 animGroups = ((const U16 *)animPtr)[1];
    if (animGroups > 31) return false; // exceeds TabMat's own hard limit (MAX_NB_GROUPES, AFF_OBJ.CPP)
    // Real crash, confirmed via a Windows Event Log report and reproduced: this check used to stop here,
    // only guarding the upper bound -- but ObjectDisplay's own transform loop (AFF_OBJ.CPP) walks the
    // ANIMATION's own per-group frame data (ListGroupAnim) once for every group the BODY itself declares
    // (NbGroupes), not the animation's own. An animation with FEWER groups than the body needs gets walked
    // straight past the end of its own data -- an out-of-bounds read that can (and did) access violation.
    // "Twinsen and Zoé with the umbrella down" (BODY.HQR entry 175) has NbGroupes=30, BODY.HQR's own
    // effective maximum -- paired with any ordinary animation (Twinsen's own standard rig is 19 groups,
    // comfortably "<= 31" and so previously accepted here) it crashed every time. Requiring the animation to
    // have AT LEAST as many groups as the body needs is the actual contract this function's own name promises.
    const T_BODY_HEADER *body = (const T_BODY_HEADER *)bodyPtr;
    return animGroups >= body->NbGroupes;
}

// ListObjet is a fixed global array (DEFINES.H), zero-initialized by C++
// itself before any code runs -- so ptrobj->Obj.Body.Num/Anim.Num both
// start at exactly 0, not -1. Whenever the very first preview request of
// the process happened to be for body 0 anim 0 (the ordinary default for a
// freshly-added actor), the change check below saw Body.Num(0)==body(0)
// and Anim.Num(0)==anim(0), skipped initialization entirely, and rendered
// a scratch object that had never been through ObjectInitBody/
// ObjectInitAnim at all -- explaining reports of the preview loading
// "inconsistently" (it depended entirely on which body happened to be
// requested first) and rendering with body parts missing or adrift
// (confirmed by temporary logging showing NbGroups stuck at 0, its
// zero-initialized value, meaning no group ever actually got animated).
// Tracked here instead of trusting Body.Num/Anim.Num, which start at a
// value indistinguishable from a real "already initialized to body 0" state.
static S32 g_previewInitBody = -1;
static S32 g_previewInitAnim = -1;
static bool g_previewInitialized = false;

// A conservative bounding-sphere radius (centred on the body's own local
// origin) computed once per body change from BODY.HQR's own header bounds
// (T_BODY_HEADER::XMin/XMax/YMin/YMax/ZMin/ZMax, AFF_OBJ.H). Used to keep
// the turntable's camera distance far enough out that no part of the body
// -- not just its silhouette centre -- crosses NearClip as the camera
// orbits. Confirmed by testing: the auto-fit calibration (C#, 2D-silhouette
// based) picks a distance tight enough for a *thin* profile silhouette that
// individual vertices (an outstretched arm, the far side of the torso) were
// landing closer to the camera than NearClip and failing to project at all
// (LongProjectPoint3D, LPROJ3DF.CPP) -- indistinguishable on screen from a
// missing/culled polygon, which is what an earlier round misdiagnosed this
// as (see g_disableBackfaceCulling's own comment for that fix, which is
// still independently needed for genuine backface visibility).
static S32 g_previewBodyRadius = 0;

// Model-space Y of the body's bounding-box centre. The preview camera looks
// here rather than at the object's origin (usually its feet), so a tall
// body is centred in the frame instead of filling only the half above it.
static S32 g_previewBodyCenterY = 0;

// Set via lba2_renderer_set_body_preview_animation_paused (RENDERER_API.CPP)
// for the actor-attributes editor's own "pause animation" toggle -- lets the
// turntable keep rotating (cameraBeta is entirely the C# caller's own doing,
// untouched by this) while holding the animated pose still, e.g. to inspect
// a specific frame from a chosen angle. See its own use below for how
// skipping just the ObjectSetInterDep call (and nothing else) achieves that.
static bool g_previewAnimationPaused = false;

// Tracks how long TimerRefHR has sat completely still despite real
// wall-clock time genuinely advancing, to distinguish a truly stuck
// TimerLock (TIMERWIN.CPP) from a brief, legitimate one -- see this trio's
// own use below for the full story. g_previewFreezeArmed is false whenever
// TimerRefHR last moved on its own, so a fresh freeze always gets the
// full grace window again rather than inheriting an old one.
static U32 g_previewLastTimerRefHR = 0;
static U32 g_previewFreezeSinceSystemHR = 0;
static bool g_previewFreezeArmed = false;

// Set when a call's own ObjectSetInterDep() just wrapped past an
// animation's last frame (FLAG_LAST_FRAME) -- see its own use below for
// why the actual restart is deferred to the *next* call's very start
// instead of happening immediately, right where it's noticed.
static bool g_previewNeedsAnimRestart = false;

static S32 ComputeBodyBoundingRadius(const void *bodyPtr)
{
    const T_BODY_HEADER *header = (const T_BODY_HEADER *)bodyPtr;
    S32 rx = std::max(std::abs(header->XMin), std::abs(header->XMax));
    S32 ry = std::max(std::abs(header->YMin), std::abs(header->YMax));
    S32 rz = std::max(std::abs(header->ZMin), std::abs(header->ZMax));
    double radius = std::sqrt((double)rx * rx + (double)ry * ry + (double)rz * rz);
    return (S32)radius;
}

void SetBodyPreviewAnimationPaused(int paused)
{
    g_previewAnimationPaused = paused != 0;
}

int AffichageBodyPreview(int body, int anim, int cameraBeta, int cameraDistance)
{
    // ChoicePalette() (AMBIANCE.CPP) picks the shading/palette state (PtrPalCurrent/PtrNuances/FogCoul) this
    // preview's own SetLightVector/lighting math below reads from -- it is otherwise only refreshed by whichever
    // scene-loading path (lba2_renderer_load_island, RendererLoadInteriorScene) last ran, which may not be *this*
    // actor's own scene (e.g. an interior actor previewed without that interior's own load path having run first
    // in this process, or simply time having passed and something else's rendering having changed CubeMode/Island
    // in between). ChoicePalette() itself is cheap to call every frame when nothing changed (it compares against
    // LastIdxPalette and only re-reads RESS.HQR on an actual change), so calling it defensively here -- the one
    // rendering path every actor/body preview in the attributes window goes through, regardless of which scene the
    // actor is actually in -- keeps this panel's own colours always matching the CubeMode/Island state whatever
    // *did* most recently load, rather than something possibly stale.
    ChoicePalette();

    T_OBJET *ptrobj = &ListObjet[BODY_PREVIEW_OBJ];
    if (!g_previewInitialized || g_previewInitBody != body || g_previewInitAnim != anim) {
        // ObjectInitAnim() (and, via InitBodyZV()/ObjectDisplay() on later
        // frames, the body side too) dereferences GivePtrBody()/
        // GivePtrAnim()'s return value with no null check -- bail out
        // before touching the scratch object at all if either index is
        // out of range, rather than crashing on the next render. The
        // AnimFitsBody() check guards the *other* out-of-bounds risk: a
        // valid but mismatched anim/body pair (see its own comment).
        void *bodyPtr = GivePtrBody(body);
        void *animPtr = GivePtrAnim(anim);
        bool fits = bodyPtr && animPtr && AnimFitsBody(bodyPtr, animPtr);
        if (!bodyPtr || !animPtr || !fits) {
            NativeDebugLog("AffichageBodyPreview: rejecting body=%d anim=%d (bodyPtr=%p animPtr=%p fits=%d)",
                body, anim, bodyPtr, animPtr, (int)fits);
            g_previewInitialized = false; return 0;
        }
        NativeDebugLog("AffichageBodyPreview: init body=%d anim=%d (was body=%d anim=%d initialized=%d)",
            body, anim, g_previewInitBody, g_previewInitAnim, g_previewInitialized);
        InitObject(BODY_PREVIEW_OBJ);
        ptrobj->Obj.Body.Num = -1; // see ObjectInitBody's own Body.Num==-1 check
        ObjectInitBody(&ptrobj->Obj, (void *)(uintptr_t)(U32)body);
        // Real root cause of a body with heavily-textured polygons (a robe, patterned cloth, etc.) rendering
        // with wrong/unrelated colours in this preview: ObjectDisplay (AFF_OBJ.CPP) only updates ObjPtrMap
        // (the actual texture-sampling source pointer, POLYGTEX.CPP's own GTEX_PtrMapPatch=PtrMap=ObjPtrMap+
        // offset) when obj->Texture is a valid non-null/non-(-1) pointer -- ObjectInitBody never sets it, so it
        // stayed NULL and ObjectDisplay silently left ObjPtrMap pointed at whatever the MAIN SCENE's own
        // terrain/decor rendering last set it to (DECORS.CPP: ObjPtrMap=ObjTexture, the *current exterior
        // island's own scenery texture page*, loaded per-island from its own .ILE, nothing to do with
        // characters at all) -- confirmed exactly this: Wizard Twinsen's own textured robe polygons (over half
        // his body, versus a few percent for a plain-tunic Twinsen, which is why this was easy to miss) sampled
        // Desert Island's own sandy/rocky texture pixels while previewed with Desert loaded in the main view.
        // The real character/body texture page (RESS.HQR's own RESS_TEXTURES entry, loaded once at boot by
        // SOURCES/RENDERER_BOOT.CPP -- NOT the second, same-named SOURCES/3DEXT/RENDERER_BOOT.CPP, which
        // turned out to be unused by this build -- into GLOBAL.CPP's own BufferTexture) was sitting in memory
        // the whole time, just never handed to any object's own Texture field anywhere in this codebase.
        // Assigning it here is correct for every body this preview ever shows (it's not per-body data, no
        // island- or scene-load-order dependency, and it can't regress any already-correct body: this simply
        // makes ObjectDisplay's own existing conditional finally take its "yes" branch instead of never
        // running at all). Both Texture AND NextTexture: ObjectClear (CLEAR.CPP, run via InitObject above)
        // resets both to the (void*)-1 sentinel, and ObjectInitAnim's own very first line unconditionally
        // does "obj->Texture = obj->NextTexture" (a commit-pending-value pattern also used for Body/NextBody)
        // -- setting Texture alone here got silently wiped back to -1 the instant ObjectInitAnim ran below,
        // which is why the very first attempt at this fix still showed the wrong texture despite BufferTexture
        // itself being confirmed valid right up until that call.
        ptrobj->Obj.Texture = BufferTexture;
        ptrobj->Obj.NextTexture = BufferTexture;
        ptrobj->Obj.Anim.Num = -1; // see ObjectInitAnim's own Anim.Ptr==anim check
        ObjectInitAnim(&ptrobj->Obj, (void *)(uintptr_t)(U32)anim);
        g_previewBodyRadius = ComputeBodyBoundingRadius(bodyPtr);
        {
            const T_BODY_HEADER *bh = (const T_BODY_HEADER *)bodyPtr;
            g_previewBodyCenterY = ((S32)bh->YMin + (S32)bh->YMax) / 2;
        }
        g_previewInitBody = body;
        g_previewInitAnim = anim;
        g_previewInitialized = true;
        // See ObjectSetInterDep's own call site below for what this forces
        // and why it must NOT also be set on every later call: it's read
        // once by ObjectSetInterDep (INTERDEP.CPP) then folded back into a
        // freshly-computed Status, never re-armed by that function itself,
        // so setting it here (right after a real body/anim change, when
        // TimerRefHR happens to still equal the object's freshly-reset Time)
        // forces exactly the one call that needs forcing.
        ptrobj->Obj.Status |= FLAG_BODY;
        g_previewNeedsAnimRestart = false; // a real body/anim change already starts fresh at frame 0
    } else if (g_previewNeedsAnimRestart) {
        // Deferred from the *previous* call's own ObjectSetInterDep hitting
        // FLAG_LAST_FRAME (see that call site's own comment for why a
        // non-looping animation needs restarting at all) -- doing the
        // actual ObjectInitAnim() reinit here, at this same top-of-function
        // spot the real body/anim-change path above already uses safely,
        // rather than immediately in the middle of the call that noticed
        // it, crashed the app (confirmed by testing: an access violation
        // inside coreclr.dll, reproducible via nothing more than opening
        // the Body/Animation dropdown shortly after, which happens to be
        // exactly when ObjectDisplay next processes whatever partially-
        // reinitialized state that mid-stream call left behind). This path
        // runs at the very start of a call, before anything else has
        // touched the object this call, identical in shape to the
        // body/anim-change branch above -- the one call shape already
        // proven safe by every other test this session.
        void *animPtr = GivePtrAnim(anim); // resolved only to null-check below; NOT what gets passed to ObjectInitAnim -- see its own comment just below
        NativeDebugLog("AffichageBodyPreview: deferred anim restart body=%d anim=%d (animPtr=%p)", body, anim, animPtr);
        if (animPtr) {
            ptrobj->Obj.Anim.Num = -1; // force re-init -- see ObjectInitAnim's own Anim.Ptr==anim check
            // The actual crash (confirmed by bisecting via NativeDebugLog
            // checkpoints across two different reproductions: the process
            // died inside ObjectInitAnim itself, never reaching the log line
            // right after it, both before and after an earlier attempted fix
            // here that addressed a real-but-secondary issue without ever
            // being the operative cause): this call was passing `animPtr`,
            // the already-resolved real pointer GivePtrAnim(anim) returned
            // above -- but that's not what ObjectInitAnim (LIB386/ANIM/
            // ANIM.CPP) actually wants. Every other caller in this codebase,
            // including the real body/anim-change branch just above this one
            // (which passes `(void *)(uintptr_t)(U32)anim`), hands it the
            // raw small integer archive index disguised as a pointer, zero-
            // extended so reading it back low-32-bits-only recovers the
            // original index exactly. ObjectInitAnim stores whatever it's
            // given verbatim into obj->Anim.Ptr (a union with obj->Anim.Num,
            // AFF_OBJ.H's own T_PTR_NUM), then -- because RENDERER_BOOT.CPP
            // wires TransFctAnim to GivePtrAnim itself -- immediately calls
            // TransFctAnim(obj->Anim.Num) to resolve that stored index into
            // the real pointer it actually uses for the rest of the
            // function. Handing it an already-resolved real pointer instead
            // of the small-int form meant obj->Anim.Num (the SAME union
            // storage as Ptr) read back as the low 32 bits of a real 64-bit
            // heap address on this next call -- a huge garbage "archive
            // index" -- so GivePtrAnim(garbage) returned NULL or a wild
            // pointer, crashing the moment ObjectInitAnim dereferenced it.
            // That garbage index is whatever GivePtrAnim(anim) happens to
            // resolve to for THIS process's own heap layout, which ASLR
            // changes on every relaunch -- some runs it happened to decode
            // to something that didn't crash immediately, others (like this
            // one, and the very first reports of this bug) it didn't. Fixed
            // by passing the exact same small-int-as-pointer form the real-
            // change branch above already uses correctly.
            ptrobj->Obj.LastOfsFrame = 0; // loop restart wants a hard cut to frame 0, not ObjectInitAnim's cross-fade-from-current-pose path -- see LastOfsFrame's own note in ANIM.CPP if this ever needs revisiting
            ObjectInitAnim(&ptrobj->Obj, (void *)(uintptr_t)(U32)anim);
            NativeDebugLog("AffichageBodyPreview: deferred restart -- ObjectInitAnim returned");
            ptrobj->Obj.Status |= FLAG_BODY; // matches the real-change branch: force ObjectSetInterDep to run even though TimerRefHR still equals the freshly-reset Time
        }
        g_previewNeedsAnimRestart = false;
    }
    if (ptrobj->Obj.Body.Num < 0) return 0;

    // The preview object sits exactly at the camera's look-at target (world
    // origin, below), so its camera-space Z after LongWorldRotatePoint is
    // always exactly 0 -- LongProjectPoint3D (LPROJ3DF.CPP) rejects any
    // point whose z exceeds CameraZrClip, and CameraZrClip works out to
    // (camDistance - NearClip) here (SetFollowCamera pushes CameraZr back
    // by camDistance from the target's own, also-zero, rotated Z). Once
    // camDistance drops below NearClip that goes negative, so 0 > a
    // negative clip and the object silently fails to project at all --
    // confirmed by temporary logging showing exactly this (X0=Y0=Z0=0,
    // CameraZrClip negative) whenever the auto-fit calibration picked a
    // distance under NearClip for a small body. Clamping here (rather than
    // in the C# calibrator, which has no visibility into NearClip) keeps
    // the fix correct regardless of what NearClip is configured to.
    S32 camDistance = cameraDistance > 0 ? cameraDistance : 5000;
    S32 minSafeDistance = NearClip + g_previewBodyRadius + 200;
    if (camDistance < minSafeDistance) camDistance = minSafeDistance;

    ptrobj->Obj.X = 0;
    ptrobj->Obj.Y = 0;
    ptrobj->Obj.Z = 0;
    ptrobj->Obj.Beta = 0;

    ClearAniPoly();
    ClsTerrainZBuf();
    Switch_Fillers(FILL_POLY_FOG_ZBUFFER);
    // Interior scenes leave the fixed iso projection installed (Init3DView);
    // the preview needs the exterior perspective one.
    Init3DExtView();
    SetLightVector(600, -600, 0);
    SetFollowCamera(0, g_previewBodyCenterY, 0, 200, cameraBeta, 0, camDistance);

    ManageSystem();

    // TimerLock (TIMERWIN.CPP) is a counting lock -- ManageTime() (just
    // called above) only advances the shared TimerRefHR clock while it's
    // exactly zero, though it always updates TimerSystemHR (real wall-clock
    // time) regardless. Confirmed by logging (TimerLock=1, TimerSystemHR
    // genuinely advancing, TimerRefHR/obj->Time frozen solid) that something
    // on the editor's own island/actor-scan path (RendererScanIslandActors's
    // repeated LoadScene() calls, most likely -- see its own FlagLoadGame
    // comment) sometimes leaves this elevated well past whatever briefly
    // needed it, permanently freezing the preview's animation (ObjectSetInterDep's
    // whole state machine is driven by TimerRefHR actually moving).
    //
    // Force-clearing it the *instant* a freeze was seen was tried first and
    // crashed the app -- but only when triggered within about a second of a
    // fresh launch, confirmed by repeated testing (crashed twice fresh,
    // survived every time once delayed or once the same fix was disabled
    // entirely): something in the app's own startup sequence legitimately
    // holds this lock briefly, and clearing it out from under that
    // in-progress lock corrupted whatever it assumed about time standing
    // still meanwhile. Waiting for a genuinely *sustained* freeze instead
    // (TimerRefHR unchanged while TimerSystemHR has still moved by more
    // than half a second) distinguishes the two: any legitimate short-lived
    // lock has long since released by then, so only a truly stuck one ever
    // gets force-cleared, and startup's own brief hold is never touched.
    if (TimerRefHR != g_previewLastTimerRefHR) {
        g_previewLastTimerRefHR = TimerRefHR;
        g_previewFreezeArmed = false;
    } else if (!g_previewFreezeArmed) {
        g_previewFreezeArmed = true;
        g_previewFreezeSinceSystemHR = TimerSystemHR;
    } else if (TimerSystemHR - g_previewFreezeSinceSystemHR > 500) {
        NativeDebugLog("AffichageBodyPreview: force-clearing stuck TimerLock (was %ld, frozen for %ldms)",
            (long)TimerLock, (long)(TimerSystemHR - g_previewFreezeSinceSystemHR));
        TimerLock = 0;
        ManageSystem(); // let TimerRefHR actually move this same call, not just from the next one
        g_previewLastTimerRefHR = TimerRefHR;
        g_previewFreezeArmed = false;
    }

    // ObjectSetInterDep() (INTERDEP.CPP) is what actually advances the
    // animation timer state machine and sets Status to FLAG_CHANGE (and,
    // once a frame boundary is crossed, copies the new frame's keyframe
    // data into CurrentFrame) -- ObjectSetInterFrame() just interpolates
    // between whatever LastOfsFrame/NextOfsFrame ObjectSetInterDep last set
    // up, and bails out immediately if FLAG_CHANGE isn't set. Real actors
    // get this every tick from the main per-object update loop (OBJECT.CPP,
    // e.g. line ~1112); our scratch object never goes through that, so
    // without calling it here Status stayed 0 forever, ObjectSetInterFrame
    // never did anything beyond ObjectInitAnim's own one-time frame-0 copy,
    // and only whichever body groups that copy happened to leave sensibly
    // posed rendered -- the rest (missing torso/arms in testing) stayed at
    // effectively garbage transforms.
    //
    // FLAG_BODY (see the init block above) forces this to run on the very
    // first call after a body/anim change, when TimerRefHR still exactly
    // equals obj->Time -- ObjectSetInterDep's own "nothing changed" fast
    // path (TimerRefHR == obj->Time, INTERDEP.CPP) otherwise skips it
    // entirely. Setting that flag *here instead*, unconditionally on every
    // call, was the bug behind animation playback freezing after the first
    // frame: CalibrateBodyPreviewDistance (C#) fires nine of these calls
    // back to back with no real time between them for every single body/
    // anim commit (a probe render plus eight angle samples) and again on
    // every window resize, and forcing this fast path open on every one of
    // those zero-elapsed-time calls made ObjectSetInterDep re-process the
    // *same* instant repeatedly instead of correctly no-op'ing -- confirmed
    // by testing: switching animations still showed the new anim's first
    // frame (that path runs once, correctly), but it then never advanced
    // again on later real per-tick calls, because the forced zero-delta
    // reprocessing during calibration corrupted LastTimer/NextTimer against
    // a TimerRefHR value that had no relation to when real time would next
    // catch up to it. Saving/restoring X/Y/Z matches OBJECT.CPP's own call
    // site: ObjectSetInterDep applies the animation's own root motion to the
    // object's position, which would otherwise walk our preview object away
    // from the world origin the camera is centred on.
    // When paused, Status/CurrentFrame are simply left exactly as the last
    // real (non-paused) call computed them -- ObjectSetInterFrame below
    // still runs and converges to a fully-idempotent no-op within at most
    // one more call (INTFRAME.CPP clears Status to 0 the first time through
    // after the freeze, then bails immediately on FLAG_CHANGE being clear
    // every time after that), so the pose simply stops changing rather than
    // needing any special-cased "frozen" rendering path of its own.
    if (!g_previewAnimationPaused) {
        S32 savedX = ptrobj->Obj.X, savedY = ptrobj->Obj.Y, savedZ = ptrobj->Obj.Z;
        NativeDebugLog("AffichageBodyPreview: calling ObjectSetInterDep");
        ObjectSetInterDep(&ptrobj->Obj);
        NativeDebugLog("AffichageBodyPreview: ObjectSetInterDep returned, Status=0x%x", (unsigned)ptrobj->Obj.Status);
        ptrobj->Obj.X = savedX;
        ptrobj->Obj.Y = savedY;
        ptrobj->Obj.Z = savedZ;

        // INTERDEP.CPP's own end-of-animation branch (nextFrame >=
        // NbFrames) already wraps to LoopFrame/LoopOfsFrame and sets
        // FLAG_LAST_FRAME -- but LoopFrame is just whatever the anim's own
        // data says, and for something authored as a one-shot (a death,
        // a pickup gesture, an attack swing) that's never meant to repeat
        // in real gameplay (a script swaps to a different animation once
        // it finishes, rather than ever letting it loop), so nothing about
        // that value was ever exercised or necessarily sensible as a
        // restart point. Our preview has no such script and just keeps
        // playing the one selected animation indefinitely, so a one-shot
        // anim would otherwise sit wherever LoopFrame landed it looking
        // frozen. Restarting from frame 0 whenever the last frame is
        // reached makes every animation loop continuously, which is what a
        // preview -- unlike real gameplay -- actually wants. Only *noticed*
        // here, though -- see g_previewNeedsAnimRestart's own comment at
        // the top of this function for why the actual reinit happens on
        // the next call instead of immediately, right where it's detected.
        if (ptrobj->Obj.Status & FLAG_LAST_FRAME) {
            g_previewNeedsAnimRestart = true;
        }
    }

    LongWorldRotatePoint(ptrobj->Obj.X, ptrobj->Obj.Y, ptrobj->Obj.Z);
    if (LongProjectPoint(X0, Y0, Z0)) {
        S32 zr = CameraZr - Z0;
        if (zr <= ClipZFar) {
            SetCLUT(PalLevel + 16 * BoundRegleTrois(0, 15, ClipZFar - StartZFog, zr - StartZFog));
            if (!(ptrobj->WorkFlags & MANUAL_INTER_FRAME)) {
                NativeDebugLog("AffichageBodyPreview: calling ObjectSetInterFrame");
                ObjectSetInterFrame(&ptrobj->Obj);
                NativeDebugLog("AffichageBodyPreview: ObjectSetInterFrame returned");
            }
            UnsetClip();
            // Real bug (confirmed via temporary logging, since removed): the preview is a single isolated
            // object, but Fill_Saut_Normal (POLY.CPP) is a process-wide filler-jump-table pointer shared with
            // the main scene's own terrain/interior rendering -- if the main scene last rendered with fog/
            // z-buffer active (Switch_Fillers(FILL_POLY_FOG_ZBUFFER), used elsewhere in this file for the real
            // 3D view), that table stays selected for every subsequent caller, including this one, until
            // something explicitly switches it back. This function already resets it to FILL_POLY_TEXTURES
            // (the plain table) AFTER its own render, as a courtesy to the NEXT normal-mode caller -- but
            // never BEFORE its own, so it can inherit the wrong table for its own render too. For most polygon
            // types every table's own entry behaves the same either way, but texture-chromakey types (8-15)
            // do not: the fog/z-buffered chromakey fillers (Filler_TextureChromaKeyFogZBuf and friends) are
            // genuinely different code paths from their plain-table counterparts. Confirmed via temporary
            // pointer logging: Fill_Saut_Normal really was Fill_FogZBuf_Table_Jumps (left behind by the main
            // scene's own terrain/interior rendering) during a body preview, not Fill_N_Table_Jumps -- a real,
            // general correctness bug regardless of any specific body. Forcing the plain table right here,
            // unconditionally, before every preview render (not just once at startup) is the fix -- a preview
            // of one isolated body has no business inheriting the main scene's own fog/z-buffer mode.
            Switch_Fillers(FILL_POLY_TEXTURES);
            // See TestVisible's own comment (AFF_OBJ.CPP): a full rotation
            // crosses viewing angles a real camera never does, where a
            // body modeled single-sided for its habitual viewing side
            // would otherwise render with entire groups missing.
            g_disableBackfaceCulling = true;
            g_previewFlattenTransparent = true;
            NativeDebugLog("AffichageBodyPreview: calling ObjectDisplay");
            ObjectDisplay(&ptrobj->Obj);
            NativeDebugLog("AffichageBodyPreview: ObjectDisplay returned");
            g_disableBackfaceCulling = false;
            g_previewFlattenTransparent = false;
        }
    }

    FiltreAnimatedPolys();
    Switch_Fillers(FILL_POLY_TEXTURES);
    CopyScreen(Log, Screen);
    return 1;
}

// Matches RENDERER_API.CPP's own WORLD_CUBE_SIZE (HOLO.H's SCE: 32768 world
// units per exterior cube), duplicated rather than shared to keep this
// file's only dependency on the renderer API boundary at zero.
#define WIDE_WORLD_CUBE_SIZE 32768

extern int RendererLoadCubeScene(int cubeX, int cubeY);
extern int RendererLoadNeighborCubeData(int cubeX, int cubeY);
extern void DrawOneSea(S32 y);

// Renders `radiusCubes` rings of neighboring cubes around the currently-
// loaded one (CurrentCubeX/CurrentCubeY) into the same frame, instead of
// just the one cube AffGrilleExt() draws. The classic exterior renderer's
// terrain grid, decor list and actor list (MapSommetY, ListDecors,
// ListObjet, ...) are all fixed-size, single-cube buffers -- there's no
// making them hold several cubes' data at once without resizing and
// rebasing coordinate math throughout terrain/decor/object rendering, a
// much larger and riskier change. Instead, this reuses those buffers
// exactly as they already work: load one cube's data, draw it, load the
// next cube's data in its place, draw that too -- into the *same*
// Z-buffered color/depth buffer, cleared only once at the very start of
// the frame rather than per cube (see ClsTerrainZBuf()). As long as every
// cube's camera is positioned at the same real-world stance (just expressed
// in that cube's own local 0-32768 frame, the same way lba2_renderer_set_
// view_target() already re-expresses a world position for whichever cube
// contains it), depth values computed independently per cube are still
// directly comparable, so ordinary Z-buffer occlusion composites them
// correctly with no awareness on either side that it's happening -- nearer
// terrain from one cube's pass correctly hides farther terrain from
// another's, actors occlude neighboring cubes' terrain and vice versa, etc.
//
// This does make each wide frame cost roughly (2*radiusCubes+1)^2 cube
// loads instead of one, so it trades per-frame cost for coverage; callers
// should reserve it for when the camera is pulled back enough that a
// single cube's worth of terrain visibly runs out before the horizon does,
// not use it for every frame regardless of zoom.
void AffGrilleExtWide(int radiusCubes) {
    S32 centerCubeX = CurrentCubeX, centerCubeY = CurrentCubeY;
    S32 centerWorldX = centerCubeX * WIDE_WORLD_CUBE_SIZE + VueOffsetX;
    S32 centerWorldY = VueOffsetY;
    S32 centerWorldZ = centerCubeY * WIDE_WORLD_CUBE_SIZE + VueOffsetZ;

    SetFollowCamera(VueOffsetX, VueOffsetY, VueOffsetZ, AlphaCam, BetaCam, GammaCam, VueDistance);
    SetLightVector(AlphaLight, BetaLight, 0);

    Switch_Fillers(FILL_POLY_FOG_ZBUFFER);
    ClearAniPoly();
    ClsTerrainZBuf();

    ManageSystem();

#ifdef LBA_EDITOR
    if ((FlagInfos & INFO_HORIZON) && RendererDrawSky)
#else
    if (FlagDrawHorizon && RendererDrawSky)
#endif
    {
        DrawHorizon2ZBuf();
    }

    ManageSystem();

    for (S32 dy = -radiusCubes; dy <= radiusCubes; dy++) {
        for (S32 dx = -radiusCubes; dx <= radiusCubes; dx++) {
            S32 cx = centerCubeX + dx, cy = centerCubeY + dy;
            if (cx < 0 || cx > 15 || cy < 0 || cy > 15) continue;

            S32 localOffsetX = centerWorldX - cx * WIDE_WORLD_CUBE_SIZE;
            S32 localOffsetZ = centerWorldZ - cy * WIDE_WORLD_CUBE_SIZE;
            // Every cube in the loop -- including (dx,dy)==(0,0), the
            // primary/camera-target cube -- reloads its own terrain/scene
            // here, with no special case that skips it. An earlier version
            // treated (0,0) as "already loaded, just draw it" and skipped
            // the reload, on the assumption that its data would still be
            // sitting in the (shared, single-cube-sized) terrain grid by the
            // time the loop reached it. That's only true if (0,0) happens to
            // be the *first* iteration; for any radius >= 1 the loop order
            // (dy outer, dx inner, both from -radius to +radius) visits
            // several neighbors first, each overwriting that same shared
            // grid with its own data -- so the primary cube silently drew
            // whichever neighbor's terrain was loaded last, at the primary
            // cube's own camera position. That mismatch (right camera,
            // wrong terrain/fog-depth for what's actually being drawn) is
            // what showed up as a patch of visibly wrong/washed-out terrain
            // exactly in the primary cube's own area -- and only at radius
            // >= 1, since radius 0 never entered this loop.
            //
            // RendererLoadNeighborCubeData (not a raw LoadCube() call)
            // re-applies the editor's widened fog/clip distance on top of
            // each cube's own, much narrower, shipped defaults -- skipping
            // that left every reloaded cube's terrain fogged as if the
            // camera were only its own cube's usual viewing distance away,
            // which is short enough that farther cubes washed out to the
            // fog color almost entirely.
            if (!RendererLoadNeighborCubeData(cx, cy)) {
                // No terrain data at all for this cube (open sea/void). The
                // engine's own convention for this -- see the LBA_EDITOR-
                // only phantom-cube preview code elsewhere in this file,
                // which does the same LoadCube-fails-so-draw-sea-instead
                // fallback -- is a flat sea plane, not nothing; leaving this
                // cube undrawn showed as a solid black hole (ClsTerrainZBuf's
                // clear color) in the middle of an otherwise-normal wide view.
                SetFollowCamera(localOffsetX, centerWorldY, localOffsetZ, AlphaCam, BetaCam, GammaCam, VueDistance);
                if (RendererDrawSea) DrawOneSea(Y_SEA);
                continue;
            }
            SetFollowCamera(localOffsetX, centerWorldY, localOffsetZ, AlphaCam, BetaCam, GammaCam, VueDistance);

            AffichageTerrainZBuf();
            AffichageActorsZBuf();
        }
    }

    // Restore the primary cube's own terrain/actors/camera so callers that
    // run after this (lba2_renderer_project_point, another ordinary
    // lba2_renderer_render_frame, etc.) keep seeing the cube the camera
    // nominally targets, not whichever neighbor was drawn last.
    RendererLoadNeighborCubeData(centerCubeX, centerCubeY);
    SetFollowCamera(VueOffsetX, VueOffsetY, VueOffsetZ, AlphaCam, BetaCam, GammaCam, VueDistance);

    ManageSystem();

    if (RendererDrawSky)
        DrawSkySeaZBuf();

    ManageSystem();

    FiltreAnimatedPolys();

    Switch_Fillers(FILL_POLY_TEXTURES);

    CopyScreen(Log, Screen);
}

void AffGrilleExt() {
    SetFollowCamera(VueOffsetX, VueOffsetY, VueOffsetZ,
                    AlphaCam, BetaCam, GammaCam,
                    VueDistance);

    SetLightVector(AlphaLight, BetaLight, 0);

    Switch_Fillers(FILL_POLY_FOG_ZBUFFER);

    ClearAniPoly();

    ClsTerrainZBuf();

    ManageSystem(); // for streaming

#ifdef LBA_EDITOR
    if ((FlagInfos & INFO_HORIZON) && RendererDrawSky)
#else
    if (FlagDrawHorizon && RendererDrawSky)
#endif
    {
        DrawHorizon2ZBuf();
    }

    ManageSystem(); // for streaming

    AffichageTerrainZBuf();

    ManageSystem(); // for streaming

    AffichageActorsZBuf();

    ManageSystem(); // for streaming

    if (RendererDrawSky)
        DrawSkySeaZBuf();

    ManageSystem(); // for streaming

    FiltreAnimatedPolys();

    Switch_Fillers(FILL_POLY_TEXTURES);

    CopyScreen(Log, Screen);

#ifdef LBA_EDITOR
    if (FlagInfos & INFO_CAMERA) {
        DrawCameraInfos();
    }
#endif
}

/*══════════════════════════════════════════════════════════════════════════*
			 █▀▀▀▀ █▀▀▀█ █▀▀▀█ █▀▀▀█ █   █
			 ██ ▀█ ██▀█▀ ██▀▀█ ██▀▀▀ ██▀▀█
			 ▀▀▀▀▀ ▀▀  ▀ ▀▀  ▀ ▀▀    ▀▀  ▀
 *══════════════════════════════════════════════════════════════════════════*/

//▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀
// Calcule le facteur de zoom d'un sprite pour l'afficher: a revoir si c'est
// bon ! En intérieur, on le fixe a la valeur par défaut (afficher en taille
// normale). D'ailleurs, cette fonction devrait se trouver dans INTEXT.C avec
// pourquoi pas un pointeur sur fonction
void CalculeScaleFactorSprite(S32 x, S32 y, S32 z, S32 scaleorg) {
    if (CubeMode == CUBE_INTERIEUR AND scaleorg == -1) {
        ScaleFactorSprite = DEF_SCALE_FACTOR;
    } else {
        S32 yp0;

        LongWorldRotatePoint(x, y, z);
        if (!LongProjectPoint(X0, Y0 + 1000, Z0))
            return;

        yp0 = Yp;

        if (!LongProjectPoint(X0, Y0, Z0))
            return;

        if (scaleorg <= 0)
            scaleorg = 70;

        ScaleFactorSprite = ((Yp - yp0) * DEF_SCALE_FACTOR) / scaleorg;
    }
}

/*══════════════════════════════════════════════════════════════════════════*
		   █▀▀▀█ █▀▀█      █ █▀▀▀▀ █▀▀▀▀ ▀▀█▀▀ ██▀▀▀
		   ██  █ ██▀▀█ ▄▄  █ ██▀▀  ██      ██  ▀▀▀▀█
		   ▀▀▀▀▀ ▀▀▀▀▀ ▀▀▀▀▀ ▀▀▀▀▀ ▀▀▀▀▀   ▀▀  ▀▀▀▀▀
 *══════════════════════════════════════════════════════════════════════════*/

// Calcule coordonnees de l'ombre
void GetShadowExt(S32 xw, S32 yw, S32 zw) {
    S32 cj = -1; // tient compte de la profondeur de l'eau

    if (yw >= 0) // l'objet est au-dessus de l'eau (enfin presque car l'eau n'est pas tjs en 0!)
    {
        cj = 0; // ne tient pas compte de la profondeur de l'eau
    }

    ShadowX = xw;
    ShadowY = CalculAltitudeObjet(xw, zw, cj);
    ShadowZ = zw;

    // peut-etre faut-il rajouter ces deux lignes pour etre plus exact dans le tri
    if (ShadowY != yw) {
        ShadowY = AdjustShadowDecors(xw, zw, ShadowY, yw);
        ShadowY = AdjustShadowObjects(xw, zw, ShadowY, yw);
    } else {
        ShadowTestObject = -1;
    }
}

#ifndef COMPILATOR
/*──────────────────────────────────────────────────────────────────────────*/
// Pour recentrage de la camera: teste si la camera se trouve à l'intérieur
// d'un objet scénarique. si oui, on lève la que.. euh.. la caméra
// (voir 3DEXT\MAPTOOLS.CPP --> SearchCameraPos() )

void TestCameraPosObjets(void) {
    S32 xmin, ymin, zmin;
    S32 xmax, ymax, zmax;
    S32 i;
    T_OBJET *ptrobj = ListObjet;
    T_OBJ_3D *pt3d;

    if (CameraZone OR CinemaMode)
        return;

    for (i = 0; i < NbObjets; i++, ptrobj++) {
        pt3d = &ptrobj->Obj;

        if ((pt3d->Body.Num != -1)
                AND !(ptrobj->Flags & INVISIBLE)) {
            xmin = pt3d->X + ptrobj->XMin;
            ymin = pt3d->Y + ptrobj->YMin;
            zmin = pt3d->Z + ptrobj->ZMin;
            xmax = pt3d->X + ptrobj->XMax;
            ymax = pt3d->Y + ptrobj->YMax;
            zmax = pt3d->Z + ptrobj->ZMax;

            if (CamPosX >= xmin AND CamPosX <= xmax
                                                   AND CamPosY >= ymin AND CamPosY <= ymax
                                                                                          AND CamPosZ >= zmin AND CamPosZ <= zmax) {
                // cam dans objet
                CamPosY = ymax + 200;
            }
        }
    }
}

/*──────────────────────────────────────────────────────────────────────────*/
// Detecte collision avec le decors en un point (terrain + objets du decors)
U8 WorldColBrickExt(S32 xw, S32 yw, S32 zw) {
    U32 i;
    S32 y = CalculAltitudeObjet(xw, zw, APtObj->CodeJeu >> 4);

    PtrDecorsCol = ListDecors;

    // decors
    for (i = 0; i < NbObjDecors; i++, PtrDecorsCol++) {
        if (TestZVDecors(xw, yw, zw, PtrDecorsCol)) {
            // verifie qu'il n'y ait pas de terrain au-dessus
            // de l'objet
            if (y <= PtrDecorsCol->YMax) {
                return 1;
            }
        }
    }

    PtrDecorsCol = NULL;

    // terrain
    if (y > yw) {
        if (GiveTerrainCol(xw, zw))
            return 1;
        else
            return 3;
    }

    return 0;
}

/*──────────────────────────────────────────────────────────────────────────*/
U8 WorldColBrickVisibleExt(S32 oldx, S32 oldy, S32 oldz,
                           S32 xw, S32 yw, S32 zw) {
    U8 col = WorldColBrickExt(xw, yw, zw);

    if (col)
        return (col);

    if (abs(xw - oldx) >= SIZE_BRICK_XZ
                              OR abs(yw - oldy) >= SIZE_BRICK_Y
                                                       OR abs(zw - oldz) >= SIZE_BRICK_XZ) {
        return (WorldColBrickVisible(oldx, oldy, oldz,
                                     oldx + (xw - oldx) / 2,
                                     oldy + (yw - oldy) / 2,
                                     oldz + (zw - oldz) / 2));
    }

    return (0); // Ok, cette fois, on est sûr !:)
}

/*──────────────────────────────────────────────────────────────────────────*/
void ReajustPosExtraExt(T_EXTRA *ptrextra, U8 col, S16 *ptrymin) {
    S32 y;

    if (!col)
        return;

    y = CalculAltitudeObjet(ptrextra->PosX, ptrextra->PosZ, -1) - *ptrymin;

    if (y > ptrextra->PosY)
        ptrextra->PosY = y;
}

/*──────────────────────────────────────────────────────────────────────────*/
// Detecte collision des avec le decors entre deux positions
// (uniquement en version jeu)
U8 FullWorldColBrickExt(S32 oldx, S32 oldy, S32 oldz,
                        S32 newx, S32 newy, S32 newz) {
    S32 x0, y0, z0;

    if (WorldColBrickExt(newx, newy, newz) == 1)
        return 1;

    x0 = (newx + oldx) / 2;
    y0 = (newy + oldy) / 2;
    z0 = (newz + oldz) / 2;

    if (WorldColBrickExt(x0, y0, z0) == 1)
        return 1;

    if (WorldColBrickExt((newx + x0) / 2, (newy + y0) / 2, (newz + z0) / 2) == 1)
        return 1;

    if (WorldColBrickExt((x0 + oldx) / 2, (y0 + oldy) / 2, (z0 + oldz) / 2) == 1)
        return 1;

    return 0;
}

/*──────────────────────────────────────────────────────────────────────────*/
// Détecte collision avec un objet du décors en tenant compte de la ZV
// de l'objet scénarique testé.
// La ZV passée de l'objet scénarique est précalculée en absolu

// Version ZVs absolues précalculées des Decors
S32 TestZVDecorsZV(T_DECORS *objet,
                   S32 xpmin, S32 ypmin, S32 zpmin,
                   S32 xpmax, S32 ypmax, S32 zpmax) {
    if (objet->Body & DEC_INVISIBLE)
        return FALSE;

    if (objet->XMin > xpmax)
        return FALSE;
    if (objet->XMax < xpmin)
        return FALSE;

    if (objet->ZMin > zpmax)
        return FALSE;
    if (objet->ZMax < zpmin)
        return FALSE;

    if (objet->YMin > ypmax)
        return FALSE;
    if (objet->YMax < ypmin - 1)
        return FALSE;

    return TRUE;
}

/*──────────────────────────────────────────────────────────────────────────*/
// Détecte collision de l'objet scénarique courant avec les objets du décors
U8 WorldColBrickDecors(S32 xpmin, S32 ypmin, S32 zpmin,
                       S32 xpmax, S32 ypmax, S32 zpmax) {
    U16 i;

    PtrDecorsCol = ListDecors;

    // decors
    for (i = 0; i < NbObjDecors; i++, PtrDecorsCol++) {
        if (i == NumDecorsCol)
            continue;

        if (TestZVDecorsZV(PtrDecorsCol,
                           xpmin, ypmin, zpmin,
                           xpmax, ypmax, zpmax)) {
            NumDecorsCol = i;
            return 1;
        }
    }

    PtrDecorsCol = NULL; // No Collision

    return 0;
}

//▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀
// Version ZVs absolues précalculées
S32 TestZVDecorsXZFullY(S32 xmin, S32 zmin, S32 xmax, S32 zmax, S32 y0, S32 y1, T_DECORS *objet) {
    if (objet->Body & DEC_INVISIBLE)
        return FALSE;

    if (objet->XMin > xmax)
        return FALSE;
    if (objet->XMax < xmin)
        return FALSE;

    if (objet->ZMin > zmax)
        return FALSE;
    if (objet->ZMax < zmin)
        return FALSE;

    if (objet->YMin > y1)
        return FALSE;
    else if (objet->YMax > y1)
        return FALSE;

    if (objet->YMax < y0)
        return FALSE;

    return TRUE;
}

/*──────────────────────────────────────────────────────────────────────────*/
S32 ReajustPosDecors(S32 xmin, S32 zmin,
                     S32 xmax, S32 zmax,
                     S32 y0, S32 y1) {
    U16 i;

    PtrDecorsCol = ListDecors;

    // decors
    for (i = 0; i < NbObjDecors; i++, PtrDecorsCol++) {
        if (TestZVDecorsXZFullY(xmin, zmin, xmax, zmax,
                                y0, y1, PtrDecorsCol)) {
            y0 = PtrDecorsCol->YMax;
            NumDecorsCol = i;
        }
    }

    if (NumDecorsCol == -1)
        PtrDecorsCol = NULL; // No Collision
    else
        PtrDecorsCol = &ListDecors[NumDecorsCol];

    return y0;
}

/*──────────────────────────────────────────────────────────────────────────*/
// Détecte collision de l'objet scénarique courant avec le terrain
U8 WorldColBrickFullExt(S32 xw, S32 yw, S32 zw) {
    // terrain
    if (GiveTerrainCol(xw, zw))
        return 1;

    if (CalculAltitudeObjet(xw, zw, APtObj->CodeJeu >> 4) >= yw)
        return 3;

    return 0;
}

/*──────────────────────────────────────────────────────────────────────────*/
// Initialise la glissage dans les penchés interdits
void InitSkating(void) {
    S32 angle;
    S32 beta;

    if (APtObj->WorkFlags & SKATING)
        return;

    beta = GetSkateBeta(OldX, OldZ);

    angle = (APtObj->Obj.Beta - beta) & (MAX_ANGLE - 1);
    if (angle > 2048)
        angle = 4096 - angle;

    if (angle > 1024) {
        GenAnimSkate = GEN_ANIM_SKATEG;
        beta = (beta + 2048) & 4095;
    } else {
        GenAnimSkate = GEN_ANIM_SKATE;
    }

    // passée en anim_all_then ici pour forcee l'anim
    InitAnim(GenAnimSkate, ANIM_ALL_THEN, NUM_PERSO);
    APtObj->WorkFlags |= SKATING;
    StartYFalling = 0;
    TimerSkating = TimerRefHR + SKATING_TIME_OUT;

    InitBoundAngleMove(&APtObj->BoundAngle, APtObj->SRot, APtObj->Obj.Beta, beta);
}

/*──────────────────────────────────────────────────────────────────────────*/
void AnnuleSkating(void) {
    InitAnim(GEN_ANIM_RIEN, ANIM_REPEAT, NUM_PERSO);
    APtObj->WorkFlags &= ~(SKATING);
}

/*──────────────────────────────────────────────────────────────────────────*/
// Réajuste hauteur (Nyw) de l'objet scénarique courant en fonction du terrain
// et des objets du décors

void ReajustPosExt(U8 col) {
    S32 coly;

    if (!col)
        return;

    coly = CalculAltitudeObjet(Nxw, Nzw, APtObj->CodeJeu >> 4);

    if (PtrDecorsCol) {
        if (coly > PtrDecorsCol->YMax) {
            Nyw = coly;
            PtrDecorsCol = NULL;
            APtObj->CarryBy = -1; // attention si porté par obj
                                  // scenarique ?
        } else {
            Nyw = PtrDecorsCol->YMax + 1;
        }
    } else {
        if (Nyw >= coly) // on se trouve au-dessus de la pente du polygone
        {
            if (APtObj->CarryBy == -1) {
                S32 ny;

                /*				if( !(APtObj->WorkFlags & ANIM_MASTER_GRAVITY) )
				{
					Nyw = coly ;

					if( AnimNumObj==NUM_PERSO
					AND GiveTerrainCol(OldX,OldZ) )
					{
						InitSkating() ;
					}
				}
				else
				{
					if( AnimNumObj==NUM_PERSO
					AND GiveTerrainCol(Nxw,Nzw) )
					{
						InitSkating() ;
					}
				}
*/
                // Blindage buggy
                if ((APtObj->Move == MOVE_BUGGY_MANUAL OR APtObj->Move == MOVE_BUGGY)
                        AND GiveTerrainCol(Nxw, Nzw)) {
                    InitSkating();
                }

                // Vérifie qu'il n'y ait pas d'objet scenarique ou du decors
                // entre le sol et nous
                FirstLoop = TRUE; // Magouille pour bien passer par les tests
                ny = AdjustShadowObjects(Nxw, Nzw, coly, Nyw);
                FirstLoop = FALSE;

                Nyw = ReajustPosDecors(Nxw + APtObj->XMin, Nzw + APtObj->ZMin,
                                       Nxw + APtObj->XMax, Nzw + APtObj->ZMax,
                                       coly, Nyw);

                Nyw = ade_max(Nyw, ny);

                if (ShadowTestObject != AnimNumObj) {
                    APtObj->CarryBy = (S16)ShadowTestObject;
                } else if (NumDecorsCol != -1) {
                    //					APtObj->CarryBy = (S16)(NumDecorsCol|CARRY_BY_DECORS) ;
                    Nxw = OldX;
                    Nyw = OldY;
                    Nzw = OldZ;
                } else {
                    Nyw = coly;
                }
            }
        } else {
            if (AnimNumObj == NUM_PERSO AND APtObj->CarryBy == -1) {
                if (APtObj->WorkFlags & ANIM_MASTER_GRAVITY) {
                    if (GiveTerrainCol(Nxw, Nzw)) {
                        InitSkating();
                    }
                } else {
                    if (GiveTerrainCol(OldX, OldZ)) {
                        InitSkating();
                    }
                }
            }

            Nyw = coly;
        }

        // Teste si Twinsen est sur une pente interdite
        /*		if( AnimNumObj==NUM_PERSO
		AND GiveTerrainCol(Nxw,Nzw) )
		{
			if( (APtObj->WorkFlags & ANIM_MASTER_GRAVITY) )
			{
//				InitAnim( GEN_ANIM_RIEN, ANIM_REPEAT, NUM_PERSO ) ;
			}
			else
			{
				InitSkating() ;
			}
		}
*/
    }
}

/*──────────────────────────────────────────────────────────────────────────*/
// Recale Twinsen contre un objet du décors en X

#define RecaleXExt(ptrd)                         \
    {                                            \
        if (OldX > Nxw) {                        \
            Nxw = ptrd->XMax - APtObj->XMin + 1; \
        } else {                                 \
            Nxw = ptrd->XMin - APtObj->XMax - 1; \
        }                                        \
    }

/*──────────────────────────────────────────────────────────────────────────*/
// Recale Twinsen contre un objet du décors en Z

#define RecaleZExt(ptrd)                         \
    {                                            \
        if (OldZ > Nzw) {                        \
            Nzw = ptrd->ZMax - APtObj->ZMin + 1; \
        } else {                                 \
            Nzw = ptrd->ZMin - APtObj->ZMax - 1; \
        }                                        \
    }

/*──────────────────────────────────────────────────────────────────────────*/
// Reajuste l'objet scénarique courant contre les objets du décors
// probablement optimisable !

S32 DoCornerReajustDecors() {
    S32 xpmin, ypmin, zpmin, xpmax, ypmax, zpmax;

    if (Nxw < 0)
        return 0;
    if (Nzw < 0)
        return 0;
    if (Nxw > 64 * SIZE_BRICK_XZ - 1)
        return 0;
    if (Nzw > 64 * SIZE_BRICK_XZ - 1)
        return 0;

    xpmin = Nxw + APtObj->XMin;
    ypmin = Nyw + APtObj->YMin;
    zpmin = Nzw + APtObj->ZMin;
    xpmax = Nxw + APtObj->XMax;
    ypmax = Nyw + APtObj->YMax;
    zpmax = Nzw + APtObj->ZMax;

    // OPTIMISATION: Calculer ici la zv de l'objet pour optimiser les
    // appels a TestZVDecorsZV

    if (WorldColBrickDecors(xpmin, ypmin, zpmin, xpmax, ypmax, zpmax) == 1) {
        // On est en collision avec un objet du decors
        if (OldY >= PtrDecorsCol->YMax - SIZE_BRICK_Y / 2) {
            Nyw = CalculAltitudeObjet(Nxw, Nzw, APtObj->CodeJeu >> 4);

            if (Nyw <= PtrDecorsCol->YMax) {
                // on est au dessus de l'objet
                Nyw = PtrDecorsCol->YMax + 1;
                APtObj->CarryBy = (S16)(NumDecorsCol | CARRY_BY_DECORS);
            }
        } else if (!TestZVDecorsZV(PtrDecorsCol,
                                   xpmin, OldY + APtObj->YMin, zpmin,
                                   xpmax, OldY + APtObj->YMax, zpmax))

        {
            // on rentre dans l'objet par dessous
            Nyw = PtrDecorsCol->YMin - APtObj->YMax - 1;
            if (Nyw < OldY - DEMI_BRICK_Y)
                Nyw = OldY - DEMI_BRICK_Y;
        } else {
            Col1 = TRUE;

            // On est en collision avec un objet du decors
            if (TestZVDecorsZV(PtrDecorsCol,
                               xpmin, ypmin + 1, OldZ + APtObj->ZMin,
                               xpmax, ypmax, OldZ + APtObj->ZMax)) {
                if (!TestZVDecorsZV(PtrDecorsCol,
                                    OldX + APtObj->XMin, ypmin, zpmin,
                                    OldX + APtObj->XMax, ypmax, zpmax)) {
                    RecaleXExt(PtrDecorsCol);
                } else {
                    if (!TestZVDecorsZV(PtrDecorsCol,
                                        OldX + APtObj->XMin, ypmin, OldZ + APtObj->ZMin,
                                        OldX + APtObj->XMax, ypmax, OldZ + APtObj->ZMax)) {
                        RecaleXExt(PtrDecorsCol);
                        RecaleZExt(PtrDecorsCol);
                    }
                }
            } else {
                if (TestZVDecorsZV(PtrDecorsCol,
                                   OldX + APtObj->XMin, ypmin, zpmin,
                                   OldX + APtObj->XMax, ypmax, zpmax)) {
                    RecaleZExt(PtrDecorsCol);
                }
            }
        }

        return TRUE;
    }

    return FALSE;
}

/*──────────────────────────────────────────────────────────────────────────*/
// reajust pos objet en 2D (x,z) sur le terrain uniquement au point chaud

#define SQR_2 1518500250 /* sqr(2)<<30 */

S32 DoCornerReajustExtPt(void) {
    U32 flagspecial = FALSE;
    U32 algo = 1; // ORTHO
    S32 t;
    S32 modif = FALSE;

    // teste des decors en meme temps ?

    if (GiveTerrainCol(Nxw, Nzw) == 1) {
        // s'il y a un objet du decors entre le sol et nous,
        // on ne tient pas compte de la collision

        S32 y = CalculAltitudeObjet(Nxw, Nzw, APtObj->CodeJeu >> 4);

        if (ReajustPosDecors(Nxw + APtObj->XMin, Nzw + APtObj->ZMin,
                             Nxw + APtObj->XMax, Nzw + APtObj->ZMax,
                             y, Nyw) != y) {
            return FALSE;
        }

        // On est en collision avec le terrain
        // determine quel type de collision

        if (Nxw / 512 == OldX / 512 AND Nzw / 512 == OldZ / 512) {
            algo = 2; // meme carre, col diagonale
        } else {
            // carres differents

            t = MapPolyGround[IndexMapPolyTested].Col + MapPolyGround[IndexMapPolyTested + 1].Col;

            if ((Nxw / 512 == OldX / 512) OR(Nzw / 512 == OldZ / 512)) {
                if (t == 2) {
                    // Full Carre Collision
                    if (GiveTerrainCol(Nxw, OldZ) == 1 AND GiveTerrainCol(OldX, Nzw) == 1) {
                        algo = 2;
                    }
                } else // le carre dest demi col
                {
                    S32 numtriangle = GiveTriangle(Nxw, Nzw);

                    if (MapPolyGround[IndexMapPolyTested].Sens == 0) {
                        if (numtriangle == 0) // poly de gauche
                        {
                            if (OldX > Nxw AND OldZ < Nzw) {
                                algo = 2; // diagonale
                            }
                        } else // poly de droite
                        {
                            if (OldX < Nxw AND OldZ < Nzw) {
                                algo = 2; // diagonale
                            }
                        }
                    } else {
                        if (numtriangle == 0) // poly de gauche
                        {
                            if (OldX > Nxw AND OldZ > Nzw) {
                                algo = 2; // diagonale
                            }
                        } else // poly de droite
                        {
                            if (OldX<Nxw AND OldZ> Nzw) {
                                algo = 2; // diagonale
                            }
                        }
                    }
                }
            }
        }

        if (algo == 1) // orthogonale
        {
            if (GiveTerrainCol(Nxw, OldZ) == 1) {
                if (GiveTerrainCol(OldX, Nzw) != 1) {
                    // Recale Twinsen contre le mur en X
                    if (OldX > Nxw) {
                        SaveNxw = (OldX / SIZE_BRICK_XZ) * SIZE_BRICK_XZ + 1;
                    } else {
                        SaveNxw = (OldX / SIZE_BRICK_XZ + 1) * SIZE_BRICK_XZ - 1;
                    }
                } else {
                    if (GiveTerrainCol(OldX, OldZ) != 1) {
                        // Recale Twinsen contre le mur en X
                        if (OldX > Nxw) {
                            SaveNxw = (OldX / SIZE_BRICK_XZ) * SIZE_BRICK_XZ + 1;
                        } else {
                            SaveNxw = (OldX / SIZE_BRICK_XZ + 1) * SIZE_BRICK_XZ - 1;
                        }

                        // Recale Twinsen contre le mur en Z
                        if (OldZ > Nzw) {
                            SaveNzw = (OldZ / SIZE_BRICK_XZ) * SIZE_BRICK_XZ + 1;
                        } else {
                            SaveNzw = (OldZ / SIZE_BRICK_XZ + 1) * SIZE_BRICK_XZ - 1;
                        }
                    } else {
#ifdef LBA_EDITOR
                        if (OptionsDebug & OPT_COL_ANGLE) {
                            MessageF(FALSE, "Old Pos dans Col 1: %s", APtObj->Name);
                        }
#endif

                        SaveNxw = OldX;
                        SaveNzw = OldZ;
                    }
                }
            } else {
                // Recale Twinsen contre le mur en Z
                if (OldZ > Nzw) {
                    SaveNzw = (OldZ / SIZE_BRICK_XZ) * SIZE_BRICK_XZ + 1;
                } else {
                    SaveNzw = (OldZ / SIZE_BRICK_XZ + 1) * SIZE_BRICK_XZ - 1;
                }
            }
        } else {
            // algo diagonale

            S32 angle, d, tx, tz;

            angle = GetAngle2D(OldX, OldZ, Nxw, Nzw);

            d = Distance2D(OldX, OldZ, Nxw, Nzw);

            if (MapPolyGround[IndexMapPolyTested].Sens == 0) {
                // sens 0
                tx = tz = MULDIV64(d, (S32)(SinTab[(angle - 512 + 1024) & 4095]) << 16, SQR_2);
            } else {
                // sens 1
                tz = MULDIV64(d, (S32)(SinTab[(angle + 512 + 1024) & 4095]) << 16, SQR_2);
                tx = -tz;
            }

            SaveNxw = OldX + tx;
            SaveNzw = OldZ + tz;

            if (GiveTerrainCol(SaveNxw, SaveNzw) == 1) {
                // pos pas bonne, essaie l'autre point
                tx = -tx;
                tz = -tz;

                SaveNxw = Nxw + tx;
                SaveNzw = Nzw + tz;

                if (GiveTerrainCol(SaveNxw, SaveNzw) == 1) {
                    // pos pas bonne, tant pis, on restore l'ancienne
                    SaveNxw = OldX;
                    SaveNzw = OldZ;
                }
            }
        }
    }

    //fincorner:
    if (SaveNxw != Nxw OR SaveNzw != Nzw) {
        modif = TRUE;
    }

    Nxw = SaveNxw;
    Nzw = SaveNzw;

    return modif;
}

/*──────────────────────────────────────────────────────────────────────────*/
S32 GetSkateBeta(S32 nx, S32 nz) {
    S32 numtriangle;
    S32 beta;
    S32 xi, zi;
    S32 y0, y1, y2, y3;

    xi = nx >> 9; // div512
    zi = nz >> 9; // div512

    zi *= 65;

    y0 = MapSommetY[zi + xi];
    y1 = MapSommetY[(zi + 65) + xi];
    y2 = MapSommetY[(zi + 65) + (xi + 1)];
    y3 = MapSommetY[zi + (xi + 1)];

    numtriangle = GiveTriangle(Nxw, Nzw);

    if (MapPolyGround[IndexMapPolyTested].Sens == 0) {
        if (numtriangle == 0) // Poly de gauche
        {
            beta = GetAngle2D(y2, y1, y1, y0);
        } else // Poly de droite
        {
            beta = GetAngle2D(y3, y2, y0, y3);
        }
    } else {
        if (numtriangle == 0) // Poly de gauche
        {
            beta = GetAngle2D(y3, y1, y0, y0);
        } else // Poly de droite
        {
            beta = GetAngle2D(y2, y2, y1, y3);
        }
    }

    return (beta);
}
#endif // COMPILATOR

/*──────────────────────────────────────────────────────────────────────────*/
// Gestion de l'animation de l'objet scénarique passé (numobj) en mode
// extérieur. Fait appel à certaines fonctions utilisées aussi en intérieur
void DoAnimExt(U8 numobj) {
#ifndef COMPILATOR
    T_OBJET *ptrobj;
    S32 n;
    S32 validepos = TRUE;
    S32 oldcarryby;
    S32 oldworkflags;
    U8 col;

    APtObj = ptrobj = &ListObjet[(AnimNumObj = numobj)];

    if (APtObj->Obj.Body.Num == -1)
        return;

    OldX = APtObj->OldPosX;
    OldY = APtObj->OldPosY;
    OldZ = APtObj->OldPosZ;

    oldcarryby = APtObj->CarryBy;
    oldworkflags = APtObj->WorkFlags;

#ifdef LBA_EDITOR
    if (numobj != NUM_PERSO)
        validepos = FALSE;
#else
    if (numobj == NUM_PERSO) {
        LastValidePos = ValidePos;
        ValidePos = TRUE; // considère par défaut qu'on
                          // est sur une position valide
    } else
        validepos = FALSE;
#endif

    /* gestion DEP OBJET SPRITE */

    if (APtObj->Flags & SPRITE_3D) {
        if (APtObj->HitForce != 0)
            APtObj->WorkFlags |= OK_HIT;

        Nxw = APtObj->Obj.X;
        Nyw = APtObj->Obj.Y;
        Nzw = APtObj->Obj.Z;

        if (!(APtObj->WorkFlags & FALLING)) {
            if (APtObj->SRot != 0) /* vitesse deplacement */
            {
                n = GetDeltaMove(&APtObj->BoundAngle.Move);

                Rotate(n, 0, APtObj->Obj.Alpha); /* alpha */
                Nyw = APtObj->Obj.Y - Z0;
                Rotate(0, X0, APtObj->Obj.Beta);
                Nxw = APtObj->Obj.X + X0;
                Nzw = APtObj->Obj.Z + Z0;
            }
        }
    }
    /* gestion anim/DEP OBJET 3D */
    else {
        GereObjAnim(numobj); // pas SPRITE_3D

        // Special Twinsen
        if (APtObj->Move == MOVE_BUGGY_MANUAL) {
            DoAnimBuggy(APtObj);
        }
    }

    if (!(APtObj->WorkFlags & FALLING)
            AND(APtObj->Flags & PUSHABLE)) {
        Nxw += APtObj->Coord.Push.PushX;
        Nyw += APtObj->Coord.Push.PushY;
        Nzw += APtObj->Coord.Push.PushZ;

        APtObj->Coord.Push.PushX = 0;
        APtObj->Coord.Push.PushY = 0;
        APtObj->Coord.Push.PushZ = 0;
    }

    // pour tous obj/sprite 3d
    if (!CheckCarryBy(numobj)) {
        validepos = FALSE;
    }

    // je tombe
    if (APtObj->WorkFlags & FALLING) {
        /* gestion chute avec proportion sur y */
        Nxw = OldX;
        Nyw = OldY + StepFalling;
        Nzw = OldZ;

        validepos = FALSE;
    }

    // Je Glisse sur une collision interdite
    if (APtObj->WorkFlags & SKATING) {
        // Verifie qu'on doit toujours glisser
        if (GiveTerrainCol(Nxw, Nzw)) {
            S32 beta = GetSkateBeta(Nxw, Nzw);

            // Se trouve-t-on sur les genoux ou sur les fesses ?
#ifdef LBA_EDITOR
            if (!strcmp(GenAnimSkate, GEN_ANIM_SKATEG))
#else
            if (GenAnimSkate == GEN_ANIM_SKATEG)
#endif
            {
                // Sur les genoux, on glisse en arrière !

                beta += 2048;
                beta &= 4095;
            }

            ChangeSpeedBoundAngleMove(&APtObj->BoundAngle,
                                      APtObj->SRot,
                                      beta);

            InitAnim(GenAnimSkate, ANIM_REPEAT, numobj);

            Nyw = CalculAltitudeObjet(Nxw, Nzw, APtObj->CodeJeu >> 4);

            validepos = FALSE;
        } else {
            AnnuleSkating();
        }
    } else {
        if (numobj == NUM_PERSO
                          AND GiveTerrainCol(Nxw, Nzw)) {
            validepos = FALSE;
        }
    }

    /* test/reajuste pos */

    if (APtObj->Flags & CHECK_BRICK_COL) {
        S32 memox, memoy, memoz;

        YMap = 0;

        /* reajuste le nyw (nouvelle pos) par rapport à la col en cours	*/
        if (APtObj->CarryBy == -1 AND(col = WorldColBrickExt(OldX, OldY, OldZ)) != 0) {
            ReajustPosExt(col);
        }

        if (APtObj->Flags & CHECK_OBJ_COL) {
            CheckObjCol(numobj);
        }

        /*		if( numobj==NUM_PERSO )
		{
			CheckDartCol( NUM_PERSO ) ;
		}
*/
        // Optimisation en inversant l'ordre des 2 tests
        if (APtObj->WorkFlags & FALLING) {
            if (APtObj->CarryBy != -1) {
                ReceptionObj();
            }
        }

        // ■■■■■■■■■ 4 coins test la col de la nouvelle pos + size zv

        memox = SaveNxw = Nxw;
        memoy = Nyw;
        memoz = SaveNzw = Nzw;

        Col1 = 0;
        NumDecorsCol = -1;

        if (DoCornerReajustDecors()) {
            // Tente un second recalage au cas ou se serait fait
            // recaler dans un autre objet

            // ATTENTION: peut-etre dangereux de mettre ca en commentaire
            if (DoCornerReajustDecors()) {
                // on y est pas arrivé, tant pis, on restore
                // sa position
                if (WorldColBrickDecors(Nxw + APtObj->XMin, Nyw + APtObj->YMin + 1, Nzw + APtObj->ZMin,
                                        Nxw + APtObj->XMax, Nyw + APtObj->YMax, Nzw + APtObj->ZMax)) {
                    Nxw = OldX;
                    Nzw = OldZ;
                }
            }

            if (APtObj->CarryBy == -1 AND(oldcarryby == -1) // pas de reajustement si on vient de quitter un CarryBy
                                   AND !(APtObj->WorkFlags & ANIM_MASTER_GRAVITY)
                                       AND GiveTerrainCol(Nxw, Nzw)) // tiens, on m'a mis dans une
            {                                                        // collision interdite !
                Nxw = OldX;
                Nzw = OldZ;
            }
        }

        SaveNxw = Nxw;
        SaveNzw = Nzw;

        if ((!(APtObj->WorkFlags & (ANIM_MASTER_GRAVITY | SKATING))
                 OR(APtObj->WorkFlags & FLY_JETPACK))
                AND(APtObj->CarryBy == -1)
                    AND(oldcarryby == -1)) // pas de reajustement si on vient de quitter un CarryBy
        {
            if (DoCornerReajustExtPt()) {
                if (WorldColBrickDecors(Nxw + APtObj->XMin, Nyw + APtObj->YMin + 1, Nzw + APtObj->ZMin,
                                        Nxw + APtObj->XMax, Nyw + APtObj->YMax, Nzw + APtObj->ZMax)
                        OR GiveTerrainCol(Nxw, Nzw)) {
                    // on m'a poussé dans un objet du décors
                    Nxw = OldX;
                    Nzw = OldZ;
                }
            }
        }

        //************** ATTENTION: test de blindage collis avec objs
        if (memox != Nxw OR memoy != Nyw OR memoz != Nzw) {
            // houullla !!!!!!!!!
            // on s'est fait recaler par le decors, vérifie qu'on
            // n'a pas été éjecter dans un objet sce

            if (IsObjCol(numobj)) {
                // tant pis, on restore l'ancienne pos, et toc!
                Nxw = OldX;
                Nyw = OldY;
                Nzw = OldZ;
            }
        }
        //**************

        // ■■■■■■■■■	test la col de la nouvelle pos reelle (pour le sol)
        if ((APtObj->Col = col = WorldColBrickExt(Nxw, Nyw, Nzw)) != 0) {
            if (col == 1) {
                if (APtObj->WorkFlags & FALLING) {
                    ReceptionObj();
                    ReajustPosExt(col);
                } else {
                    /* fait glisser (dernier recours) */

                    /*■■■■■■■■■*/ /* regarde de dep X puis Z par rapport à old pos */

                    if ((!(APtObj->WorkFlags & ANIM_MASTER_GRAVITY)
                             OR(APtObj->WorkFlags & FLY_JETPACK))
                            AND(APtObj->CarryBy == -1)) {
                        if (WorldColBrickExt(Nxw, Nyw, OldZ) != 0) {
                            if (WorldColBrickExt(OldX, Nyw, Nzw) != 0) {
                                // Cas particulier
                                // Pour virer bug peintre dans 0_EGOUT2.SCE
                                // Objet etant en ANIM_MASTER_GRAVITY, ayant le flag
                                // BRICK_COL mais pas le flag OBJ_FALLABLE alors
                                // que son anim boucle sur une frame ayant une
                                // translation en Y (plonge)
                                if (!(ptrobj->Flags & OBJ_FALLABLE) AND ptrobj->WorkFlags & ANIM_MASTER_GRAVITY) {
                                    ReajustPosExt(col);
                                } else {
                                    validepos = FALSE;
                                    goto findoanimext; /* pos pas acceptee */
                                }
                            } else {
                                Nxw = OldX;
                            }
                        } else {
                            Nzw = OldZ;
                        }
                    }
                }
            } else {
                if (APtObj->WorkFlags & FALLING) {
                    ReceptionObj();
                }

                // orrection bug chariot dans descente twinsen touche col penchée
                if (APtObj->CarryBy == -1) {
                    ReajustPosExt(col);
                }
            }

            APtObj->WorkFlags &= ~FALLING;
        } else /* col == 0 */
        {
            /* si sous les pieds != cube plein: susceptible de tomber ou reajuste y */
            if ((APtObj->Flags & OBJ_FALLABLE) AND(APtObj->CarryBy == -1)) {
                col = WorldColBrickExt(Nxw, Nyw - 1, Nzw);
                if (col) {
                    if (APtObj->WorkFlags & FALLING) {
                        ReceptionObj();
                    }

                    ReajustPosExt(col);
                } else {
                    validepos = FALSE;

                    if (!(APtObj->WorkFlags & ANIM_MASTER_GRAVITY)) {
                        /*						if( !StartYFalling
						AND (numobj==NUM_PERSO)
						AND !FlagClimbing )
						{
							StartYFalling = Nyw ;
						}
*/
                        if (!(APtObj->WorkFlags & FALLING)) {
                            Nyw -= DEMI_BRICK_Y; // Magouille pour checkobjcol
                            CheckObjCol(numobj);
                            if (ptrobj->ObjCol == 255)
                                Nyw += DEMI_BRICK_Y; // Magouille pour checkobjcol
                        }

                        if (ptrobj->ObjCol == 255) {
                            if (APtObj->Flags & SPRITE_3D) {
                                if (WorldColBrickExt(Nxw + APtObj->XMin, Nyw - 1, Nzw + APtObj->ZMin) != 1 AND WorldColBrickExt(Nxw + APtObj->XMin, Nyw - 1, Nzw + APtObj->ZMax) != 1 AND WorldColBrickExt(Nxw + APtObj->XMax, Nyw - 1, Nzw + APtObj->ZMin) != 1 AND WorldColBrickExt(Nxw + APtObj->XMax, Nyw - 1, Nzw + APtObj->ZMax) != 1) {
                                    APtObj->WorkFlags |= FALLING;
                                }
                            } else // obj 3D
                            {
                                if (!WorldColBrickExt(Nxw, Nyw - SIZE_BRICK_Y / 2, Nzw)
                                        AND !(APtObj->WorkFlags & SKATING)
                                            AND((APtObj->CodeJeu >> 4) == 0)
                                                AND(GiveTerrainCol(Nxw, Nzw)
                                                            OR oldcarryby != -1 OR oldworkflags &
                                                    ANIM_MASTER_GRAVITY
                                                        OR(APtObj->WorkFlags & (FALLING | FLY_JETPACK | CHECK_FALLING))))
                                //							      OR APtObj->Col) )
                                // rajouter test terrain collision interdite sur OldX, OldY, OldZ ?
                                {
                                    if (numobj != NUM_PERSO
                                                      OR !FlagClimbing) {
                                        /*#ifdef	LBA_EDITOR
									if( strcasecmp(APtObj->StringAnim,GEN_ANIM_ARRIMAGE) )
#else
									if( APtObj->GenAnim!=GEN_ANIM_ARRIMAGE )
#endif
*/
                                        {
                                            InitAnim(GEN_ANIM_TOMBE, ANIM_TEMPO, numobj);
                                            APtObj->WorkFlags |= FALLING;
                                            PtrZoneClimb = NULL;

                                            if (!StartYFalling
                                                    AND numobj == NUM_PERSO) {
                                                StartYFalling = Nyw;
                                            }
                                        }
                                    }
                                } else {
                                    ReajustPosExt(1);

                                    // Bug Plaque ejectee quand on saute
                                    // dessus
                                    CheckObjCol(numobj);
                                }
                            }
                        } else if (APtObj->WorkFlags & FALLING) {
                            ReceptionObj();
                        }
                    }
                }
            }
        }

        // Mort si l'objet touche le fond du cube -
        //		if( YMap<0 )	APtObj->LifePoint = 0 ;	// bye bye
        //		if( APtObj->Obj.Y<=0 )	APtObj->LifePoint = 0 ;	// bye bye
    } else /* ! Flags & CHECK_BRICK_COL */
    {
        if (APtObj->Flags & CHECK_OBJ_COL) {
            CheckObjCol(numobj);
        }
    }

    // test pour le pingouin: bit 7 champ Col à 1 si touche mur
    if (Col1 != 0)
        ptrobj->Col |= 128;

    /* protege sortie du cube */

    // à virer puisqu'on pourra descendre en dessous de 0
    //	if( Nyw < 0 ) Nyw = 0 ;

    // si on passe par l'un de ces tests, cela signifie que l'on n'est
    // pas dans une zone de changement de cube, donc on tente de lancer
    // le cube phantom.sce pour aller y mourir

    FlagHeroOutX = FlagHeroOutZ = FALSE;
    PhantomX = PhantomY = 0; // peut etre sorti ailleurs

    if (!NumBuggy OR numobj != NUM_BUGGY OR !IsBuggyPresent()) {
        S32 flagchgcube = TRUE;
        S32 marge = DEMI_BRICK_XZ;

        if (numobj == NUM_PERSO) {
#ifdef LBA_EDITOR
            if (!strcasecmp(APtObj->StringAnim, GEN_ANIM_CHOC)
                    OR !strcasecmp(APtObj->StringAnim, GEN_ANIM_CHOC2)
                        OR !strcasecmp(APtObj->StringAnim, GEN_ANIM_ENCAISSE))
#else
            if (APtObj->GenAnim == GEN_ANIM_CHOC OR APtObj->GenAnim == GEN_ANIM_CHOC2 OR APtObj->GenAnim == GEN_ANIM_ENCAISSE)
#endif
            {
                marge += 10;
                flagchgcube = FALSE;
            }
        } else
            flagchgcube = FALSE;

        if (Nxw < marge) {
            if (flagchgcube)
                FlagHeroOutX = TRUE;
            Nxw = marge;
            PhantomX = -1;
        }

        if (Nzw < marge) {
            if (flagchgcube)
                FlagHeroOutZ = TRUE;
            Nzw = marge;
            PhantomY = -1;
        }

        if (Nxw > 64 * SIZE_BRICK_XZ - marge) {
            if (flagchgcube)
                FlagHeroOutX = TRUE;
            Nxw = 64 * SIZE_BRICK_XZ - marge;
            PhantomX = 1;
        }

        if (Nzw > 64 * SIZE_BRICK_XZ - marge) {
            if (flagchgcube)
                FlagHeroOutZ = TRUE;
            Nzw = 64 * SIZE_BRICK_XZ - marge;
            PhantomY = 1;
        }
    }

    if (!GereCodesJeu(numobj))
        validepos = FALSE;

    if ((PhantomX OR PhantomY)
            AND(numobj == NUM_PERSO)
                AND(APtObj->CarryBy == -1 OR APtObj->CarryBy & CARRY_BY_DECORS)
                    AND((Comportement != C_PROTOPACK) // on ne meurre pas en marche jetpack
                        AND(Comportement != C_JETPACK)
#ifdef LBA_EDITOR
                            OR strcasecmp(APtObj->StringAnim, GEN_ANIM_MARCHE)))
#else
                            OR APtObj->GenAnim != GEN_ANIM_MARCHE))
#endif
    {
        S32 index;

        index = (CurrentCubeY + PhantomY) * 16 + (CurrentCubeX + PhantomX);
        index = IsleMapIndex[index] & 127; // bug compilo ou debug ?

        if (!index) // pas de cube
        {
            // allez, on va mourir dans le cube phantom !
            NewCube = NUM_CUBE_PHANTOM;
            FlagChgCube = 1; // passe en mode NewPos

            NewPosY = Nyw;

            switch (PhantomX) {
            case -1:
                NewPosX = 32768 - SIZE_BRICK_XZ - SIZE_BRICK_XZ / 2;
                break;

            case 1:
                NewPosX = SIZE_BRICK_XZ + SIZE_BRICK_XZ / 2;
                break;

            default:
                NewPosX = Nxw;
                break;
            }

            switch (PhantomY) {
            case -1:
                NewPosZ = 32768 - SIZE_BRICK_XZ - SIZE_BRICK_XZ / 2;
                break;

            case 1:
                NewPosZ = SIZE_BRICK_XZ + SIZE_BRICK_XZ / 2;
                break;

            default:
                NewPosZ = Nzw;
                break;
            }

#ifndef LBA_EDITOR
            PhantomX += CurrentCubeX;
            PhantomY += CurrentCubeY;
#endif

            PhantomIsland = Island; // memo island
            FlagReajustPosTwinsen = TRUE;
            validepos = FALSE;
        }
    }

    if (APtObj->WorkFlags & SKATING) {
        if (abs(Nxw - OldX) >= 10 OR abs(Nyw - OldY) >= 10 OR abs(Nzw - OldZ) >= 10) {
            TimerSkating = TimerRefHR + SKATING_TIME_OUT;
        } else if (TimerRefHR > TimerSkating) {
            AnnuleSkating();
        }
    }

    APtObj->Obj.X = Nxw;
    APtObj->Obj.Y = Nyw;
    APtObj->Obj.Z = Nzw;

findoanimext:

    if (numobj == NUM_PERSO) {
        S16 nu = APtObj->CarryBy;

#ifndef LBA_EDITOR
        if (!LastValidePos AND APtObj->LifePoint <= 0) {
            validepos = FALSE;
        }
#endif

        // pour le protopack
        if (Comportement == C_PROTOPACK
#ifdef LBA_EDITOR
                                AND strcasecmp(APtObj->StringAnim, GEN_ANIM_MARCHE))
#else
                                AND APtObj->GenAnim == GEN_ANIM_MARCHE)
#endif
        {
            validepos = FALSE;
        }

        // blindage Carriers
        if ((nu != -1)
                AND !(nu & CARRY_BY_DECORS)
                    AND(ListObjet[nu].Flags & POS_INVALIDE)) // POS_INVALID
        {
            validepos = FALSE;
        }

#ifdef LBA_EDITOR
        if (validepos) {
            ValideCube = NumCube;
            ValidePosX = OldX;
            ValidePosY = OldY;
            ValidePosZ = OldZ;
            ValideBeta = APtObj->Obj.Beta;
        }
#else
        if (validepos) {
#ifdef DEBUG_TOOLS
            if (CheckKey(K_U))
                Message("ValidePos", FALSE);
#endif
            // sauve etat heros pour le prochain SaveValidePos()
            memcpy(&ValideHero, ptrobj, sizeof(T_OBJET));

            ValideHero.Obj.X = OldX;
            ValideHero.Obj.Y = OldY;
            ValideHero.Obj.Z = OldZ;

            if (FirstSave) // on vient de changer de cube
            {
                SaveValidePos();
            }
        } else {
            ValidePos = FALSE;

            if (LastValidePos
                    AND !FirstSave        // on vient de changer de cube
                        AND !FlagChgCube) // on ne sauve pas le contexte
                                          // si l'autosave n'a pas encore
                                          // été faite
            {
                SaveValidePos();
            }
        }

        FirstSave = FALSE;
#endif
    } else {
        FlagHeroOutX = FALSE;
        FlagHeroOutZ = FALSE;
    }
#endif
}

/*══════════════════════════════════════════════════════════════════════════*
			 ▀▀█▀▀ █▀▀▀█ █▀▀▀█ █     ██▀▀▀
			   ██  ██  █ ██  █ ██    ▀▀▀▀█
			   ▀▀  ▀▀▀▀▀ ▀▀▀▀▀ ▀▀▀▀▀ ▀▀▀▀▀
		     (partie seulement utilisée dans l'outil)
 *══════════════════════════════════════════════════════════════════════════*/

#ifdef LBA_EDITOR
// Affiche la feuille (carré) d'un polygone indexé par xi,zi
void DrawGrilleFeuille3D(S32 xi, S32 zi) {
    S32 x0, z0, x3, z3;
    S32 x2, z2, x1, z1;
    S32 cx0, cy0, cx1, cy1;
    S32 sens;
    S32 beta;
    S32 y0, y1, y2, y3;

    y0 = MapSommetY[zi * 65 + xi];
    y1 = MapSommetY[(zi + 1) * 65 + xi];
    y2 = MapSommetY[(zi + 1) * 65 + xi + 1];
    y3 = MapSommetY[zi * 65 + xi + 1];

    x0 = SommetRot[zi + 0][xi + 0].X2D;
    z0 = SommetRot[zi + 0][xi + 0].Y2D;
    x1 = SommetRot[zi + 1][xi + 0].X2D;
    z1 = SommetRot[zi + 1][xi + 0].Y2D;
    x2 = SommetRot[zi + 1][xi + 1].X2D;
    z2 = SommetRot[zi + 1][xi + 1].Y2D;
    x3 = SommetRot[zi + 0][xi + 1].X2D;
    z3 = SommetRot[zi + 0][xi + 1].Y2D;

    UnsetClip();

    cx0 = ade_min(ade_min(x0, x1), ade_min(x2, x3));
    cy0 = ade_min(ade_min(z0, z1), ade_min(z2, z3));
    cx1 = ade_max(ade_max(x0, x1), ade_max(x2, x3));
    cy1 = ade_max(ade_max(z0, z1), ade_max(z2, z3));

    sens = MapPolyGround[zi * 64 * 2 + xi * 2].Sens;

    DrawFeuille3D(x0, z0,
                  x1, z1,
                  x2, z2,
                  x3, z3,
                  sens,
                  GREEN,
                  FALSE);

    CoulText(YELLOW, BLACK);

    if (!sens) {
        Line(x0, z0, x2, z2, GREEN);

        // poly de gauche (0)
        beta = GetAngle2D(y2, y1, y1, y0);
        GraphPrintf(FALSE, 0, 50, "Beta0= %d", beta);
        GraphPrintf(FALSE, 0, 60, "y0-y1=%d   y1-y2=%d ", y0 - y1, y1 - y2);

        // Poly de droite (1)
        beta = GetAngle2D(y3, y2, y0, y3);
        GraphPrintf(FALSE, 0, 80, "Beta1= %d", beta);
        GraphPrintf(FALSE, 0, 90, "y3-y2=%d   y0-y3=%d ", y3 - y2, y0 - y3);
    } else {
        Line(x1, z1, x3, z3, YELLOW);

        // Poly de gauche (0)
        beta = GetAngle2D(y3, y1, y0, y0);
        GraphPrintf(FALSE, 0, 50, "Beta0= %d", beta);
        GraphPrintf(FALSE, 0, 60, "y0-y1=%d   y0-y3=%d ", y0 - y1, y0 - y3);

        // Poly de droite (1)
        beta = GetAngle2D(y2, y2, y1, y3);
        GraphPrintf(FALSE, 0, 80, "Beta1= %d", beta);
        GraphPrintf(FALSE, 0, 90, "y3-y2=%d   y1-y2=%d ", y3 - y2, y1 - y2);
    }

    BoxStaticAdd(cx0, cy0, cx1, cy1);
}
#endif

/*──────────────────────────────────────────────────────────────────────────*/
// Affiche la table de fog dans un box passé en paramètres
void AffFogTable(S32 x0, S32 y0, S32 x1, S32 y1) {
    S32 x, y, xl, yl, fi;
    S32 yo;
    S32 xo = x0;

    for (xl = 0; xl < 256; xl++) {
        yo = y0;
        x = RegleTrois(x0, x1, 256, xl + 1);

        for (fi = 0; fi < 16; fi++) {
            for (yl = 0; yl < 16; yl++) {
                y = RegleTrois(y0, y1, 256, (fi * 16) + yl + 1);
                Box(xo, yo, x, y, PtrNuances[(fi * 16 * 256) + yl * 256 + xl]);

                yo = y + 1;
            }
        }
        xo = x + 1;
    }
}

/*──────────────────────────────────────────────────────────────────────────*/
// Affiche la palette a l'ecran (Ephémère car aucun rapport avec 3Dext)
void AffPalette() {
    S32 t;
    S32 x, y;

    x = y = 0;

    Cls();

    for (t = 0; t < 256; t++) {
        Box(x, y, x + 17, y + 9, t);

        y += 10;

        if (y >= 160) {
            GraphPrintf(FALSE, x + 2, 165, "%d", t / 16);
            y = 0;
            x += 18;
        }
    }

    // Init Textures
    PtrMap = ObjPtrMap = BufferTexture;
    RepMask = 0xFFFF;

    // Affiche page de texture
    RestoreBlock(Log, PtrMap, 383, 223, 638, 478);
    Rect(382, 222, 639, 479, LBAWHITE);

    // Affiche page de texture SkySea
    RestoreBlock(Log, GroundTexture, 5, 223, 260, 478);
    Rect(4, 222, 261, 479, LBAWHITE);

    // Affiche Table de Fog
    //	AffFogTable( 383, 0, 643, 220 ) ;

    BoxUpdate();

    while (MyKey) {
        ManageTime();
        MyGetInput();

        if (AnimateTexture) {
            DoTextureAnimation();
            // Affiche page de texture
            RestoreBlock(Log, PtrMap, 383, 223, 638, 478);
            BoxStaticAdd(383, 223, 638, 478);
            BoxUpdate();
        }
    }

    FirstTime = AFF_ALL_FLIP;
}

/*──────────────────────────────────────────────────────────────────────────*/
// Affiche le terrain en mode "SPOTS"
void RedrawSpeedTerrain() {
    Cls();
    SetFollowCamera(VueOffsetX, VueOffsetY, VueOffsetZ, AlphaCam, BetaCam, GammaCam, VueDistance);
    AffichageTerrainRapide();
    BoxUpdate();

#ifdef LBA_EDITOR
    if (FlagInfos & INFO_REPERE) {
        DrawRepereXYZ(560, 420, 639, 479);
    }

    if (FlagInfos & INFO_CAMERA) {
        DrawCameraInfos();
    }

    GereDebug();
#endif

    FirstTime = AFF_ALL_FLIP;
}

#ifdef LBA_EDITOR
/*──────────────────────────────────────────────────────────────────────────*/
// Rotation du terrain à la souris
S32 GereMouseMovements(S32 boutons) {
    if (CubeMode != CUBE_EXTERIEUR)
        return FALSE;

    if (MyClick == boutons) {
        SaveTimer();

        ManageMouse();
        while (MyClick) {
            MyGetInput();
            ManageMouse();

            AlphaCam += MouseYDep;
            BetaCam += MouseXDep;

            AlphaCam &= 4095;
            BetaCam &= 4095;

            RedrawSpeedTerrain();
        }

        FirstTime = AFF_ALL_FLIP;
        RestoreTimer();
        return (TRUE);
    }

    return (FALSE);
}
#endif

/*──────────────────────────────────────────────────────────────────────────*/
#ifndef LBA_EDITOR
/* Last frame's ManualCameraOrbitActive. The flag itself is cleared once per frame
 * before the manual sources are driven, so it cannot tell a new orbit gesture from
 * one already under way; this remembers that across the frame boundary. */
static S32 ManualCameraOrbitPrev = FALSE;

/* The orbit speed still to be spent after the player stops driving. Set from the
 * last nudge, decayed once per frame, and fed back through the same nudge so the
 * follow-through is the gesture continuing rather than a second mechanism. */
static S32 ManualCameraGlideBeta = 0;

/* Abandon whatever orbit gesture was in flight: the tail still to be spent, the
 * flags that say a gesture is under way, and any pending console nudge.
 *
 * Everything above is per-frame state that only means anything while the player
 * is driving the exterior Auto camera frame after frame. A camera zone, a
 * cutscene, an interior or a re-centre all take the camera away mid-gesture, and
 * without this the state waits instead of ending: the tail resumes on the first
 * frame back and pans a camera the player has since re-centred, and the stale
 * "gesture already under way" flag makes the next nudge skip its realign,
 * reintroducing the very snap the realign exists to prevent -- at the worst
 * moment, since an authored shot leaves BetaCam furthest from the hero-relative
 * angle. Callers are the places that own such a discontinuity. */
void FollowCamForgetManualGesture(void) {
    ManualCameraGlideBeta = 0;
    ManualCameraOrbitActive = FALSE;
    ManualCameraOrbitPrev = FALSE;
    DbgCamNudgeFrames = 0;
}

/* Hand the Auto camera the angle the camera is currently at, so the target it
 * recomputes comes out there rather than somewhere it will travel to.
 *
 * Two callers, one need. An orbit gesture starting: fold in the rotation the hero
 * accumulated while the camera held its angle. A camera zone firing: adopt the
 * angle the authored shot just chose. Both write BetaCam directly, and the follow
 * update knows nothing of either, so without this it reads the difference as
 * catch-up and spends it.
 *
 * AddBetaCam is an offset *from the hero's facing*, so a held BetaCam and a hero
 * who turns under it drift apart with nothing to discharge the difference. The
 * next nudge would inherit that whole stale delta and, because orbiting also
 * selects the tight manual divisor, spend half of it in a single frame -- a 1-unit
 * stick touch snapping the view three quarters of a turn (#450). Realigning first
 * means the stick delta applies from where the camera is.
 *
 * With the camera already on target this changes nothing. With it still lerping
 * toward one, the pending catch-up is dropped as well -- a few degrees at most,
 * and the point: an orbit should start from the angle actually on screen. */
void FollowCamAdoptAngle(void) {
    T_OBJ_3D *ptr3d = &ListObjet[NumObjFollow].Obj;

    /* The inverse of the follow update's target rule: it derives the target from
     * (AddBetaCam + hero Beta), so solving that for the current BetaCam gives the
     * offset that leaves the camera where it stands. */
    AddBetaCam = FollowCamPanForAngle(BetaCam, ptr3d->Beta);
}

/* Apply a per-frame manual camera nudge from an analog source (mouse drag or
 * right stick).  dBeta orbits horizontally (AddBetaCam), dAlpha tilts elevation
 * (AlphaCam), dZoom dollies the arm (FollowCamBaseDist; positive zooms in).
 * Shared by every manual-camera source so they clamp each axis identically.
 * Only horizontal orbit raises ManualCameraOrbitActive (it selects the tight
 * BetaCam lerp in PERSO.CPP) and re-engages the follow grace period; elevation
 * and zoom leave the auto-follow tracking the hero.  Returns nonzero if any axis
 * changed so the caller can flag a redraw.  The caller clears
 * ManualCameraOrbitActive once per frame before driving sources. */
static S32 ApplyManualCameraNudge(S32 dBeta, S32 dAlpha, S32 dZoom) {
    S32 changed = 0;

    if (dBeta != 0) {
        /* Remember the speed the player is orbiting at, so releasing can ease out
         * from it rather than stop on the spot. */
        ManualCameraGlideBeta = dBeta;

        /* Only on the gesture's first frame: once orbiting, AddBetaCam has to keep
         * accumulating so the stick moves the camera by what the player asked for
         * rather than by half of it every frame. Only where the follow update that
         * owns this target rule actually runs, too -- inside a camera zone or a
         * cutscene BetaCam belongs to the authored shot, and realigning to it would
         * be reaching into a path this knows nothing about. */
        if (!ManualCameraOrbitActive AND !ManualCameraOrbitPrev
                AND CubeMode == CUBE_EXTERIEUR AND !CameraZone AND !CinemaMode)
            FollowCamAdoptAngle();

        AddBetaCam = (AddBetaCam - dBeta) & 4095;
        ManualCameraOrbitActive = TRUE;
        FirstTime = AFF_ALL_FLIP;
        changed = 1;
    }
    if (dAlpha != 0) {
        AlphaCam += dAlpha;
        if (AlphaCam < FOLLOW_CAM_ALPHA_MIN)
            AlphaCam = FOLLOW_CAM_ALPHA_MIN;
        if (AlphaCam > FOLLOW_CAM_ALPHA_MAX)
            AlphaCam = FOLLOW_CAM_ALPHA_MAX;
        FirstTime = AFF_ALL_FLIP;
        changed = 1;
    }
    /* Only horizontal orbit pauses the auto-recenter; elevation and zoom leave
     * the follow tracking the hero (matches the numpad keyboard controls). */
    if (dBeta != 0)
        FollowCamReengageDelay = FollowCamReengageFrames;

    if (dZoom != 0) {
        FollowCamBaseDist -= dZoom;
        if (FollowCamBaseDist < FOLLOW_CAM_DIST_MIN)
            FollowCamBaseDist = FOLLOW_CAM_DIST_MIN;
        if (FollowCamBaseDist > FOLLOW_CAM_DIST_MAX)
            FollowCamBaseDist = FOLLOW_CAM_DIST_MAX;
        FirstTime = AFF_ALL_FLIP;
        changed = 1;
    }

    /* Any manual camera input yields the pitch/distance auto-assist (HD recompose
     * + ground-lift) for a grace window; it eases back in afterward, so the
     * assist never fights the player's tilt/zoom/orbit while they are driving. */
    if (dBeta != 0 OR dAlpha != 0 OR dZoom != 0)
        FollowCamManualHold = FollowCamManualHoldFrames;

    return changed;
}

/* Map a right-stick axis (SDL units, -32767..32767) to a per-frame camera step.
 * Per-axis deadzone reusing JoystickDeadzone, then a linear ramp to maxStep at
 * full deflection, scaled by sensitivity as a 10%-100% fraction (sens 10 = the
 * full maxStep ceiling, sens 1 = a tenth of it). */
static S32 StickAxisToCamStep(S16 axis, S32 sens, S32 maxStep) {
    S32 v = axis;
    S32 mag = (v < 0) ? -v : v;
    if (mag <= JoystickDeadzone)
        return 0;
    S32 den = 32767 - JoystickDeadzone;
    if (den < 1)
        den = 1;
    S32 step = (mag - JoystickDeadzone) * maxStep / den; /* 0..maxStep */
    step = step * sens / 10;                             /* sens 1-10 -> 10%-100% */
    return (v < 0) ? -step : step;
}
#endif

/*──────────────────────────────────────────────────────────────────────────*/
// Gestion zoom/pivot du terrain et position caméra
#ifdef LBA_EDITOR
S32 GereExtKeys(U32 key, U32 flagmenu)
#else
S32 GereExtKeys(U32 key)
#endif
{
    S32 flag = 0;
    S32 savetimer;
    S32 savetimer2;

    if (CubeMode != CUBE_EXTERIEUR OR(!key AND !FollowCamera))
        return (FALSE);

    savetimer = TimerRefHR;
    savetimer2 = TimerSystemHR;

    SaveTimer();

#ifdef LBA_EDITOR
    while (key == K_PAGE_DOWN) {
        MyGetInput();
        key = Key;
        flag = 1;

        if (Reglage > 0) {
            if (TimerRefHR > savetimer + 100) {
                Reglage--;
                savetimer = TimerRefHR;
            }
        }

        FirstTime = AFF_ALL_FLIP;
    }

    while (key == K_PAGE_UP) {
        MyGetInput();
        key = Key;
        flag = 1;

        if (Reglage < 100) {
            if (TimerRefHR > savetimer + 100) {
                Reglage++;
                savetimer = TimerRefHR;
            }
        }

        FirstTime = AFF_ALL_FLIP;
    }

    while (key == K_PLUS) {
        MyGetInput();
        key = Key;
        flag = 1;

        if (TimerRefHR > savetimer + 1000)
            VueDistance += 500;
        else
            VueDistance += 100;

        RedrawSpeedTerrain();
    }

    while (key == K_MOINS) {
        MyGetInput();
        key = Key;
        flag = 1;

        if (TimerRefHR > savetimer + 1000)
            VueDistance -= 500;
        else
            VueDistance -= 100;

        RedrawSpeedTerrain();
    }

    while (key == K_U
                      OR(flagmenu AND key == K_PAGE_DOWN)) {
        key = Key;
        flag = 1;
        if (TimerRefHR > savetimer + 1000)
            VueOffsetY += 500;
        else
            VueOffsetY += 100;
        StartYCube = VueOffsetY / SIZE_BRICK_Y;
        RedrawSpeedTerrain();
    }

    while (key == K_J
                      OR(flagmenu AND key == K_PAGE_UP)) {
        key = Key;
        flag = 1;
        if (TimerRefHR > savetimer + 1000)
            VueOffsetY -= 500;
        else
            VueOffsetY -= 100;
        StartYCube = VueOffsetY / SIZE_BRICK_Y;
        RedrawSpeedTerrain();
    }

    while (key == K_5) {
        key = Key;
        flag = 1;
        if (TimerRefHR > savetimer + 1000)
            GammaCam += 30;
        else
            GammaCam += 10;
        RedrawSpeedTerrain();
    }

    while (key == K_6) {
        key = Key;
        flag = 1;
        if (TimerRefHR > savetimer + 1000)
            GammaCam -= 30;
        else
            GammaCam -= 10;
        RedrawSpeedTerrain();
    }

    if (flagmenu) {
        while (Input & I_JOY) {
            if (Input & I_LEFT) {
                if (TimerRefHR > savetimer + 1000)
                    VueOffsetX -= 500;
                else
                    VueOffsetX -= 100;
            }

            if (Input & I_RIGHT) {
                if (TimerRefHR > savetimer + 1000)
                    VueOffsetX += 500;
                else
                    VueOffsetX += 100;
            }

            if (Input & I_UP) {
                if (TimerRefHR > savetimer + 1000)
                    VueOffsetZ -= 500;
                else
                    VueOffsetZ -= 100;
            }

            if (Input & I_DOWN) {
                if (TimerRefHR > savetimer + 1000)
                    VueOffsetZ += 500;
                else
                    VueOffsetZ += 100;
            }

            StartXCube = VueOffsetX / SIZE_BRICK_XZ;
            StartZCube = VueOffsetZ / SIZE_BRICK_XZ;
            RedrawSpeedTerrain();
            MyGetInput();
            flag = 1;
        }
    }
    /*
	// Reglage ChampX/ChampZ- (Focale)
	while( Key==K_9 )
	{
		if( ChampX>0 )
		{
			ChampX -= 10 ;
			ChampZ -= 10 ;
			PtrInit3DGame() ;
			CameraCenter( 2 ) ;
			FirstTime = AFF_ALL_FLIP ;
			RedrawSpeedTerrain() ;
		}
	}

	// Reglage ChampX/ChampZ+ (Focale)
	while( Key==K_0 )
	{
		if( ChampX<2048 )
		{
			ChampX += 10 ;
			ChampZ += 10 ;
			PtrInit3DView() ;
			CameraCenter( 2 ) ;
			FirstTime = AFF_ALL_FLIP ;
			RedrawSpeedTerrain() ;
		}
	}
*/
    // Reglage StartZFog
    while (CheckKey(K_9)) {
        if (CheckKey(K_DOWN) AND StartZFog > NearClip) {
            if (TimerSystemHR > savetimer2 + 3000)
                StartZFog -= 2000;
            else
                StartZFog -= 100;
            PtrInit3DGame();
            CameraCenter(2);
            FirstTime = AFF_ALL_FLIP;
            SetFog(StartZFog, ClipZFar);
            AffScene(AFF_ALL_FLIP);
        } else if (CheckKey(K_UP) AND StartZFog < ClipZFar - 100) {
            if (TimerSystemHR > savetimer2 + 3000)
                StartZFog += 2000;
            else
                StartZFog += 100;
            PtrInit3DGame();
            CameraCenter(2);
            FirstTime = AFF_ALL_FLIP;
            SetFog(StartZFog, ClipZFar);
            AffScene(AFF_ALL_FLIP);
        }
    }

    // Reglage ClipZFar
    while (CheckKey(K_0)) {
        if (CheckKey(K_DOWN) AND ClipZFar > StartZFog + 100) {
            if (TimerSystemHR > savetimer2 + 3000)
                ClipZFar -= 2000;
            else
                ClipZFar -= 100;
            PtrInit3DGame();
            CameraCenter(2);
            FirstTime = AFF_ALL_FLIP;
            SetFog(StartZFog, ClipZFar);
            AffScene(AFF_ALL_FLIP);
        } else if (CheckKey(K_UP) AND ClipZFar < 200000L) {
            if (TimerSystemHR > savetimer2 + 3000)
                ClipZFar += 2000;
            else
                ClipZFar += 100;
            PtrInit3DGame();
            CameraCenter(2);
            FirstTime = AFF_ALL_FLIP;
            SetFog(StartZFog, ClipZFar);
            AffScene(AFF_ALL_FLIP);
        }
    }
#else // LBA_EDITOR
    // Mode Jeu uniquement
// penser à remettre en DEBUG_TOOLS
#if defined(DEBUG_TOOLS) || defined(TEST_TOOLS)
    while (key == K_PAGE_DOWN) {
        MyGetInput();
        key = Key;
        flag = 1;

        if (AlphaCam < 600) {
            if (TimerRefHR > savetimer + 1000)
                AlphaCam += 10;
            else
                AlphaCam += 3;
        }

        RedrawSpeedTerrain();
    }

    while (key == K_PAGE_UP) {
        MyGetInput();
        key = Key;
        flag = 1;

        if (AlphaCam > 150) {
            if (TimerRefHR > savetimer + 1000)
                AlphaCam -= 10;
            else
                AlphaCam -= 3;
        }

        RedrawSpeedTerrain();
    }
#endif

    if (Input & I_CAMERA_LEVEL_MOINS) {
        if (FollowCamera) {
            if (AlphaCam > FOLLOW_CAM_ALPHA_MIN)
                AlphaCam -= FOLLOW_CAM_ALPHA_STEP;
            FirstTime = AFF_ALL_FLIP;
        } else {
            if (VueCamera > 0) {
                VueCamera--;
                FirstTime = AFF_ALL_FLIP;
                CameraCenter(2);
            }
            InitWaitNoInput(I_CAMERA_LEVEL_MOINS);
        }
    }

    if (Input & I_CAMERA_LEVEL_PLUS) {
        if (FollowCamera) {
            if (AlphaCam < FOLLOW_CAM_ALPHA_MAX)
                AlphaCam += FOLLOW_CAM_ALPHA_STEP;
            FirstTime = AFF_ALL_FLIP;
        } else {
            if (VueCamera < NB_VUE_CAMERA - 1) {
                VueCamera++;
                FirstTime = AFF_ALL_FLIP;
                CameraCenter(2);
            }
            InitWaitNoInput(I_CAMERA_LEVEL_PLUS);
        }
    }

    if (FollowCamera) {
        if (CheckKey(K_LBRACKET)) {
            AddBetaCam = (AddBetaCam - FOLLOW_CAM_PAN_STEP) & 4095;
            FirstTime = AFF_ALL_FLIP;
            flag = 1;
        }
        if (CheckKey(K_RBRACKET)) {
            AddBetaCam = (AddBetaCam + FOLLOW_CAM_PAN_STEP) & 4095;
            FirstTime = AFF_ALL_FLIP;
            flag = 1;
        }
        /* Zoom: numpad / = closer, numpad * = further */
        /* Clamp after stepping, not before. Testing the limit first lets the last step cross it
         * by up to a step's worth, so the boom settles a little inside the hero at one end and
         * past the far limit at the other. The mouse and stick reach the same value through
         * ApplyManualCameraNudge, which clamps after, so the two routes disagreed about how far
         * zoom goes. */
        if (CheckKey(K_NUMPAD_DIV)) {
            FollowCamBaseDist -= FOLLOW_CAM_ZOOM_STEP;
            if (FollowCamBaseDist < FOLLOW_CAM_DIST_MIN)
                FollowCamBaseDist = FOLLOW_CAM_DIST_MIN;
            FirstTime = AFF_ALL_FLIP;
            flag = 1;
        }
        if (CheckKey(K_NUMPAD_MUL)) {
            FollowCamBaseDist += FOLLOW_CAM_ZOOM_STEP;
            if (FollowCamBaseDist > FOLLOW_CAM_DIST_MAX)
                FollowCamBaseDist = FOLLOW_CAM_DIST_MAX;
            FirstTime = AFF_ALL_FLIP;
            flag = 1;
        }

        /* --- Manual camera: mouse drag orbit, elevation, wheel zoom, recenter --- */
        ManualCameraOrbitPrev = ManualCameraOrbitActive;
        ManualCameraOrbitActive = FALSE;
        {
            /* Relative mouse mode: deltas keep flowing past window edge.
             * Drag mode: active while right button held.
             * Free mode: always active.
             * Disabled when mouse camera conditions not met. */
            static S32 prevRelative = 0;
            S32 wantRelative = 0;
            if (FlagMouseCamera AND CubeMode == CUBE_EXTERIEUR)
                wantRelative = FlagMouseCameraDrag ? (Click & 2) : 1;
            if (wantRelative != prevRelative) {
                SetWindowRelativeMouse(wantRelative ? true : false);
                prevRelative = wantRelative;
            }
        }
        if (FlagMouseCamera AND CubeMode == CUBE_EXTERIEUR) {
            ManageMouse();

            S32 orbitActive = FlagMouseCameraDrag ? (Click & 2) : 1;
            if (orbitActive) {
                S32 rawX = MouseXDep;
                S32 rawY = MouseYDep;

                /* Dead zone on raw pixels before scaling */
                if (rawX > -MOUSE_CAM_DEAD_ZONE AND rawX < MOUSE_CAM_DEAD_ZONE)
                    rawX = 0;
                if (rawY > -MOUSE_CAM_DEAD_ZONE AND rawY < MOUSE_CAM_DEAD_ZONE)
                    rawY = 0;

                S32 dx = rawX * MouseCameraSensX / MouseCameraDivisor;
                S32 dy = rawY * MouseCameraSensY / MouseCameraDivisor;
                if (MouseCameraInvertY)
                    dy = -dy;

                flag |= ApplyManualCameraNudge(dx, dy, 0);
            }

            S32 wy = ConsumeMouseWheelY();
            if (wy != 0)
                flag |= ApplyManualCameraNudge(
                    0, 0, wy * FOLLOW_CAM_ZOOM_STEP * MOUSE_CAM_ZOOM_MULT);

            if (Click & 4) {
                CameraCenter(1);
                FirstTime = AFF_ALL_FLIP;
                flag = 1;
            }
        }

        /* Gamepad right stick: analog orbit + elevation through the same nudge.
         * UpdateJoystick suppresses the digital RSTICK scancodes while this owns
         * the stick (Auto camera, exterior), so the two never double-drive. */
        if (GamepadCameraAnalog AND JoystickIsPresent()) {
            S16 rsx = 0;
            S16 rsy = 0;
            JoystickGetRightStick(&rsx, &rsy);
            S32 dBeta = StickAxisToCamStep(rsx, GamepadCameraSensX, GamepadCamMaxBeta);
            S32 dAlpha = StickAxisToCamStep(rsy, GamepadCameraSensY, GamepadCamMaxAlpha);
            if (GamepadCameraInvertY)
                dAlpha = -dAlpha;
            if (dBeta != 0 OR dAlpha != 0)
                flag |= ApplyManualCameraNudge(dBeta, dAlpha, 0);
        }

        /* Console `camnudge`: stand in for the stick/mouse so a headless run can
         * drive the analog camera. Same nudge entry point and same point in the
         * frame as the real sources, so it reproduces their ordering exactly. */
        if (DbgCamNudgeFrames > 0) {
            flag |= ApplyManualCameraNudge(DbgCamNudgeBeta, DbgCamNudgeAlpha, 0);
            DbgCamNudgeFrames--;
        }

        /* Orbit follow-through. The lerp eases the camera up to the stick's speed
         * over several frames but has nothing to ease it back down: on the frame
         * the stick centres, the orbit stops dead from full speed. Decaying the
         * last speed and feeding it back through the same nudge spends the tail
         * over a few frames instead, so the two ends of a gesture match.
         *
         * It runs only when no source drove this frame, and it moves AddBetaCam
         * exactly as the player's own input would, so the angle the camera ends
         * up holding is the one the follow update recomputes. Nothing is left
         * owed. A camera zone, a cutscene or a re-centre ends the gesture through
         * FollowCamForgetManualGesture rather than being tested for here: this
         * runs only where the exterior Auto camera already has the camera, so a
         * check for those states here would never be true.
         *
         * Tuned for the stick, which springs back to centre, so "no input this
         * frame" reliably means the player let go. A mouse held still reports the
         * same thing while very much still being driven, so a paused drag coasts
         * and a flick overshoots where the pointer stopped. Wrong for the mouse,
         * and worth splitting the two sources apart; left shared for now because
         * the pad is the one that has to be right, being the only control some
         * setups have. */
        if (!ManualCameraOrbitActive AND ManualCameraGlideBeta != 0) {
            /* The console sets cvars without going through the config's range
             * check, so clamp where it is used: at 100 the tail is a fixed point
             * that never decays, and beyond that it grows without bound. */
            S32 keep = FollowCamOrbitGlide;
            if (keep < 0)
                keep = 0;
            if (keep > FOLLOW_CAM_ORBIT_GLIDE_MAX)
                keep = FOLLOW_CAM_ORBIT_GLIDE_MAX;

            ManualCameraGlideBeta = ManualCameraGlideBeta * keep / 100;
            if (ManualCameraGlideBeta > -FOLLOW_CAM_ORBIT_GLIDE_STOP AND ManualCameraGlideBeta < FOLLOW_CAM_ORBIT_GLIDE_STOP)
                ManualCameraGlideBeta = 0;
            else
                flag |= ApplyManualCameraNudge(ManualCameraGlideBeta, 0, 0);
        }
    }
#endif

    RestoreTimer();

    return (flag);
}

#ifdef LBA_EDITOR

#define COUL_REPERE YELLOW
#define TAILLE_REPERE 1000 / 35

/*──────────────────────────────────────────────────────────────────────────*/
// Dessine le repère dans un rectangle pour ... se repérer !!!
void DrawRepereXYZ(S32 x0, S32 y0, S32 x1, S32 y1) {
    S32 x, z;
    S32 demiw = (x1 - x0) / 2;
    S32 demih = (y1 - y0) / 2;

    if (CubeMode != CUBE_EXTERIEUR)
        return;

    // repère des petits axes x, y et z
    SetProjection(x0 + demiw, y0 + demih, 10, 1024, 1024);
    SetFollowCamera(0, 0, 0, AlphaCam, BetaCam, GammaCam, 1000);

    CoulText(YELLOW, -1);

    LongWorldRotatePoint(0, 0, 0);
    LongProjectPoint(X0, Y0, Z0);
    x = Xp;
    z = Yp;
    LongWorldRotatePoint(TAILLE_REPERE, 0, 0);
    LongProjectPoint(X0, Y0, Z0);
    Line(x, z, Xp, Yp, COUL_REPERE);
    GraphPrintf(FALSE, Xp, Yp, "X");
    //	AffString(Xp,Yp,"X");

    LongWorldRotatePoint(0, 0, 0);
    LongProjectPoint(X0, Y0, Z0);
    x = Xp;
    z = Yp;
    LongWorldRotatePoint(0, 0, TAILLE_REPERE);
    LongProjectPoint(X0, Y0, Z0);
    Line(x, z, Xp, Yp, COUL_REPERE);
    GraphPrintf(FALSE, Xp, Yp, "Z");
    //	AffString(Xp,Yp,"Z");

    LongWorldRotatePoint(0, 0, 0);
    LongProjectPoint(X0, Y0, Z0);
    x = Xp;
    z = Yp;
    LongWorldRotatePoint(0, TAILLE_REPERE, 0);
    LongProjectPoint(X0, Y0, Z0);
    Line(x, z, Xp, Yp, COUL_REPERE);
    GraphPrintf(FALSE, Xp, Yp, "Y");
    //	AffString(Xp, Yp,"Y");

    BoxStaticAdd(x0, y0, x1, y1);

    PtrInit3DView();
}

/*──────────────────────────────────────────────────────────────────────────*/
// Le nom de la fonction est assez parlant, non ?
void DrawCameraInfos() {
    InitPalMenu();

    OpenWindow(&DWin, 10, 20, 20, 25);

    WinText(&DWin, "VueOffsetX  : ", VueOffsetX);
    WinText(&DWin, "VueOffsetY  : ", VueOffsetY);
    WinText(&DWin, "VueOffsetZ  : ", VueOffsetZ);
    WinSepar(&DWin);
    WinText(&DWin, "AlphaCam    : ", AlphaCam);
    WinText(&DWin, "BetaCam     : ", BetaCam);
    WinText(&DWin, "GammaCam    : ", GammaCam);
    WinSepar(&DWin);
    WinText(&DWin, "CameraAlpha : ", CameraAlpha);
    WinText(&DWin, "CameraBeta  : ", CameraBeta);
    WinText(&DWin, "CameraGamma : ", CameraGamma);
    WinSepar(&DWin);
    WinText(&DWin, "VueDistance : ", VueDistance);

    WinSepar(&DWin);
    WinText(&DWin, "ChampX      : ", ChampZ);
    WinText(&DWin, "ChampZ      : ", ChampX);

    WinSepar(&DWin);
    WinText(&DWin, "AlphaLight  : ", AlphaLight);
    WinText(&DWin, "BetaLight   : ", BetaLight);

    WinSepar(&DWin);
    WinText(&DWin, "CameraX     : ", CameraX);
    WinText(&DWin, "CameraY     : ", CameraY);
    WinText(&DWin, "CameraZ     : ", CameraZ);
    WinSepar(&DWin);
    WinText(&DWin, "CameraXr    : ", CameraXr);
    WinText(&DWin, "CameraYr    : ", CameraYr);
    WinText(&DWin, "CameraZr    : ", CameraZr);
    WinSepar(&DWin);
    WinText(&DWin, "NearClip    : ", NearClip);
    WinText(&DWin, "StartZFog   : ", StartZFog);
    WinText(&DWin, "ClipZFar    : ", ClipZFar);

    FlipWindow(&DWin);
}

#define CREATE_ZONE_NORD 1
#define CREATE_ZONE_SUD 2
#define CREATE_ZONE_EST 4
#define CREATE_ZONE_OUEST 8

/*──────────────────────────────────────────────────────────────────────────*/
// Cherche le numéro d'un cube dans CUBE.LST d'après sa position dans la carte
// Cette fonction va gicler car il n'y a plus de normalisation des noms de cubes
// extérieurs ! (domage !!!!)
S16 SearchNumCube(S32 x, S32 y) {
    char *pt;
    char scename[20];
    S16 n = 0;
    S32 len;

    if (Island < 10)
        snprintf(scename, sizeof(scename), "%1d_x%02d_%02d", Island, x, y);
    else
        snprintf(scename, sizeof(scename), "%2d_%02d_%02d", Island, x, y);

    len = strlen(scename);

    InitDefFile(CubeLstName);

    do {
        pt = DefFileBufferReadString(Itoa(n));

        if (pt AND strchr(pt, ';'))
            *(char *)strchr(pt, ';') = '\0';

        if (pt != 0 AND !strncasecmp(scename, pt, len))
            return n;

        n++;
    } while (pt);

    return 0;
}

/*──────────────────────────────────────────────────────────────────────────*/
// Créer une zone de changement de cube automatiquement
void CreateExtZone(S32 x0, S32 y0, S32 z0, S32 x1, S32 y1, S32 z1,
                   S32 xd, S32 yd, S32 zd, S32 cubex, S32 cubey) {
    T_ZONE *ptrz;

    if (NbZones < MAX_ZONES - 1) {
        ptrz = &ListZone[NbZones];

        ptrz->Num = SearchNumCube(cubex, cubey);
        if (!ptrz->Num)
            return;

        ptrz->X0 = x0;
        ptrz->Y0 = y0;
        ptrz->Z0 = z0;
        ptrz->X1 = x1;
        ptrz->Y1 = y1;
        ptrz->Z1 = z1;
        ptrz->Snap = TRUE;

        ptrz->Type = 0;
        ptrz->Info0 = xd;
        ptrz->Info1 = yd;
        ptrz->Info2 = zd;
        ptrz->Info3 = 0;
        ptrz->Info4 = 0;
        ptrz->Info5 = 0;
        ptrz->Info6 = 0; // Reajust pos twinsen
        ptrz->Info7 = 1 | ZONE_ON;

        NbZones++;
        Modif = TRUE;

        IndexZone = NbZones - 1;
    } else {
        Message("MAX ZONES DEC atteind!", TRUE);
    }
}

/*──────────────────────────────────────────────────────────────────────────*/
// Créer toutes les zones de changement de cube sélectionnées dans un cube
// extérieur automatiquement
void CreateExtZones(S32 zones, S32 hauteur) {
    if (CubeMode != CUBE_EXTERIEUR)
        return;

    if (zones & CREATE_ZONE_NORD) {
        CreateExtZone(0, 0, 0, 32767, hauteur, 512,
                      0, 0, 32768 - 1024, CurrentCubeX, CurrentCubeY - 1);
    }

    if (zones & CREATE_ZONE_SUD) {
        CreateExtZone(0, 0, 32768 - 512, 32767, hauteur, 32767,
                      0, 0, 512, CurrentCubeX, CurrentCubeY + 1);
    }

    if (zones & CREATE_ZONE_OUEST) {
        CreateExtZone(0, 0, 0, 512, hauteur, 32767,
                      32768 - 1024, 0, 0, CurrentCubeX - 1, CurrentCubeY);
    }

    if (zones & CREATE_ZONE_EST) {
        CreateExtZone(32768 - 512, 0, 0, 32767, hauteur, 32767,
                      512, 0, 0, CurrentCubeX + 1, CurrentCubeY);
    }
}

/*──────────────────────────────────────────────────────────────────────────*/
// Menu de création automatique des zones de changement de cube
void MenuCreateExtZones() {
    T_MENU menu;
    S32 zones = CREATE_ZONE_NORD | CREATE_ZONE_SUD | CREATE_ZONE_EST | CREATE_ZONE_OUEST;
    S32 select, flag = FALSE;
    S32 hauteur = 15000L;

    BackupScreen(TRUE);

    OpenMenu(&menu, 15, 9);
    AddText(&menu, 0, 0, 15, 1, FLAG_CENTRE + FLAG_CONTOUR, "Création des zones");
    AddButton(&menu, 98, 0, 1, 1, 1, NO_FLAG, "■");

    AddSwitch(&menu, 1, 0, 1, 15, 1, FLAG_CENTRE, "ZONE NORD", &zones, CREATE_ZONE_NORD);
    AddSwitch(&menu, 2, 0, 2, 15, 1, FLAG_CENTRE, "ZONE SUD", &zones, CREATE_ZONE_SUD);
    AddSwitch(&menu, 3, 0, 3, 15, 1, FLAG_CENTRE, "ZONE EST", &zones, CREATE_ZONE_EST);
    AddSwitch(&menu, 4, 0, 4, 15, 1, FLAG_CENTRE, "ZONE OUEST", &zones, CREATE_ZONE_OUEST);

    AddChangeValue(&menu, 10, 0, 6, 15, 1, NO_FLAG, "Hauteur:", &hauteur, 1, 256, 65536);

    AddButton(&menu, 99, 0, 8, 15, 1, FLAG_RED + FLAG_CENTRE + FLAG_CONTOUR, "OK");

    DrawMenu(&menu, -1, -1);

    while (flag != -1) {
        MyGetInput();

        select = GereMenu(&menu);
        BoxUpdate(); // pour la souris

        if (MyKey == K_ESC)
            select = 98;
        if (MyKey == K_ENTER)
            select = 99;

        switch (select) {
        case 98: // Exit
        case 99: // Ok
            flag = -1;
            break;
        }
    }

    if (select != 98 AND zones)
        CreateExtZones(zones, hauteur);

    RestoreScreen();
}
#endif // LBA_EDITOR
