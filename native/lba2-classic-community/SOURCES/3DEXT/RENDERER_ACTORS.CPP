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
#include <3D/LPROJ.H>
#include <3D/LROT3D.H>
#include <OBJECT/AFF_OBJ.H>
#include <ANIM/INTFRAME.H>
#include <vector>
#include <map>
#include <string>
#include <cstdio>
#include <cstring>
#include <algorithm>

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
    // Attributes surfaced for the editor's actor inspector panel, read
    // straight off T_OBJET at scan time (DEFINES.H: the fields below are
    // direct T_OBJET members, not nested under .Obj -- only position/angle
    // live in the embedded T_OBJ_3D Obj).
    S16 Beta;
    S16 GenBody;
    U16 GenAnim;
    S16 LifePoint;
    U8 Armure;
    U8 HitForce;
    U8 Move;
    U32 Flags;
    std::string LifeScript;
    std::string TrackScript;
    // Where this actor actually lives on disk -- needed only for the
    // editor's write path (SceneEdit below), not for anything read-only.
    // ObjectSlot is -1 for a newly-added actor until it's actually been
    // placed into ListObjet by ReapplyPendingEditsToListObjet() at least
    // once (its final slot depends on NbObjets at that time, which can
    // still change if more actors get added first).
    S32 SceneIndex = -1;
    S32 ObjectSlot = -1;
    S32 CubeX = -1;
    S32 CubeY = -1;
    // Snapshot of this actor's own real moveset (RendererGetActorNativeAnims'
    // own live walk of PtrFile3D), refreshed opportunistically whenever this
    // actor's scene is genuinely live (see CacheNativeAnimsForScene) --
    // that's the only time PtrFile3D is actually populated (DISKFUNC.CPP's
    // LoadScene() only assigns it "if (!FlagLoadGame)", which real cube
    // loading always is but nothing else here ever is). Sticks around once
    // populated so the editor's Animation picker still shows this actor's
    // real natural moveset after the camera has since moved off its cube,
    // rather than silently falling back to the same generic full-archive
    // list for every actor whose cube isn't the *one* currently loaded --
    // confirmed by report as the reason that list looked suspiciously
    // identical across different actors.
    std::vector<S32> CachedNativeAnims;
    // True only for an actor created by RendererAddActor() that hasn't had
    // its attributes explicitly applied since (RendererSetActorAttributes
    // clears it) -- never true for anything read from a real scene file.
    // Consulted by AffichageActorsZBuf (EXTFUNC.CPP) to render this actor
    // as a flat black silhouette instead of whatever body/anim it happens
    // to default to, so an editor-added actor waiting on a real body
    // choice reads as an obvious placeholder rather than a normal-looking
    // (and easily mistaken for deliberately-chosen) Twinsen.
    bool IsPlaceholderBody = false;
    // Raw BODY.HQR/ANIM.HQR indices (what Obj.Body.Num/Obj.Anim.Num hold once
    // a scene is live), snapshotted whenever this actor's scene is loaded, so
    // the attributes dialog never has to re-load the scene to resolve the
    // generic per-actor numbers. -1 until first snapshotted.
    S32 ResolvedBody = -1;
    S32 ResolvedAnim = -1;
    // True once GenBody/GenAnim hold raw archive indices (set by
    // RendererSetActorAttributes/RendererAddActor) rather than the generic
    // per-actor numbers straight from disk; resolving those again through
    // SearchBody/SearchAnim would map them to the wrong body.
    bool GenIsRaw = false;
    // Body-bounds snapshot (T_OBJET XMin..ZMax) taken with the resolved
    // body, so click targets can be sized for actors whose scene is not the
    // one live right now.
    bool BoundsValid = false;
    S32 BoundsX[2] = {0, 0}, BoundsY[2] = {0, 0}, BoundsZ[2] = {0, 0};
    // True once RendererRemoveActor() has undone this actor -- see that
    // function's own comment for why this is a soft delete (g_actors is
    // never actually shrunk, since every other actor's index is that
    // vector's own position and must stay stable across a removal).
    // RendererGetActor() is the only thing that checks this; every other
    // Get/Set API is reached exclusively through an index a live GetActor()
    // call has already returned, so a removed actor is otherwise just
    // never noticed by anything again.
    bool Removed = false;
};

static std::vector<RendererActor> g_actors;
static std::vector<S32> g_waypointX;
static std::vector<S32> g_waypointY;
static std::vector<S32> g_waypointZ;

// Session edits (attribute changes, re-encoded scripts, new actors) that
// haven't been written back to SCENE.HQR yet, keyed by scene index -- see
// SceneEdit's own comment block further down for the full design.
struct PendingAttributeEdit {
    bool HasPosition = false;
    S32 X = 0, Y = 0, Z = 0; // cube-local, matching what the record itself stores
    bool HasAttributes = false;
    S16 Beta = 0;
    U8 GenBody = 0;
    U16 GenAnim = 0;
    S16 LifePoint = 0;
    U8 Armure = 0;
    U8 HitForce = 0;
    U8 Move = 0;
    bool HasFlags = false;
    U32 Flags = 0;
};
struct PendingScriptEdit {
    std::vector<U8> LifeBytes;
    std::vector<U8> TrackBytes;
};
struct PendingNewActor {
    S32 X = 0, Y = 0, Z = 0; // cube-local
    S16 Beta = 0;
    U8 GenBody = 0;
    U16 GenAnim = 0;
    S16 LifePoint = 0;
    U8 Armure = 0;
    U8 HitForce = 0;
    U8 Move = 0;
    U32 Flags = CHECK_OBJ_COL | CHECK_BRICK_COL | CHECK_ZONE | CHECK_CODE_JEU; // sane defaults for a new NPC
    std::vector<U8> LifeBytes;
    std::vector<U8> TrackBytes;
    S32 FlatIndex = -1; // index into g_actors, so ObjectSlot can be filled in once assigned
};
struct ScenePendingEdits {
    std::map<S32, PendingAttributeEdit> AttributeEdits; // keyed by ObjectSlot
    std::map<S32, PendingScriptEdit> ScriptEdits;        // keyed by ObjectSlot
    std::vector<PendingNewActor> NewActors;
    bool Dirty = false;
};
static std::map<S32, ScenePendingEdits> g_pendingEdits; // keyed by SceneIndex

// Scene index for each of the island's 16x16 cubes (-1 where no exterior
// scene covers that cube), filled in by RendererScanIslandActors() -- it
// already visits every scene and reads each one's (cubeX, cubeY) via
// PeekSceneCube(), so this is a free byproduct of that scan rather than a
// second pass. Used by RendererLoadCubeScene() to find which scene to load
// -- with real actor data and initialized bodies -- for whichever cube the
// camera has just moved to.
static S32 g_sceneForCube[16][16];

// ---------------------------------------------------------------------
// Read-only script disassembly.
//
// Both decoders below are a purely linear walk of the bytecode from its
// start -- they print each opcode's operands as text (including jump
// targets, as plain numbers) without ever following a jump, exactly like
// ExtractTrackWaypoints() above. This is a deliberately narrower job than
// simulating the VM: enough to let someone read what a script does without
// running any of it. Every opcode-length entry below was hand-verified by
// reading the actual case in DoLife()/DoFuncLife() (GERELIFE.CPP); an
// opcode this table doesn't recognize is *never* guessed at, since a wrong
// guess would silently misalign every opcode that follows it -- the decoder
// stops there and says so, the same fallback ExtractTrackWaypoints uses for
// unrecognized TM_ opcodes.
// ---------------------------------------------------------------------

// DoFuncLife's condition table (LF_*, GERELIFE.CPP lines ~40-565): every one
// of the 46 entries (NB_FUNCS_LIFE) reads its own opcode byte plus 0 or 1
// operand byte (almost always an object number), and sets TypeAnswer to
// either RET_S16 (comparison value is 2 bytes) or a 1-byte type (RET_S8 /
// RET_U8 -- these two only ever differ in signedness, never in size, so
// they're not distinguished here). None of the 46 set RET_STRING.
struct LifeCondInfo { U8 operandBytes; bool valueIsS16; const char *name; };
static const LifeCondInfo LifeConditions[46] = {
    /* 0*/ {0, false, "COL"},
    /* 1*/ {1, false, "COL_OBJ"},
    /* 2*/ {1, true, "DISTANCE"},
    /* 3*/ {0, false, "ZONE"},
    /* 4*/ {1, false, "ZONE_OBJ"},
    /* 5*/ {0, false, "BODY"},
    /* 6*/ {1, false, "BODY_OBJ"},
    /* 7*/ {0, true, "ANIM"},
    /* 8*/ {1, true, "ANIM_OBJ"},
    /* 9*/ {0, false, "L_TRACK"},
    /*10*/ {1, false, "L_TRACK_OBJ"},
    /*11*/ {1, false, "VAR_CUBE"},
    /*12*/ {1, true, "CONE_VIEW"},
    /*13*/ {0, false, "HIT_BY"},
    /*14*/ {0, false, "ACTION"},
    /*15*/ {1, true, "VAR_GAME"},
    /*16*/ {0, true, "LIFE_POINT"},
    /*17*/ {1, true, "LIFE_POINT_OBJ"},
    /*18*/ {0, false, "NB_LITTLE_KEYS"},
    /*19*/ {0, true, "NB_GOLD_PIECES"},
    /*20*/ {0, false, "COMPORTEMENT_HERO"},
    /*21*/ {0, false, "CHAPTER"},
    /*22*/ {1, true, "DISTANCE_3D"},
    /*23*/ {0, false, "MAGIC_LEVEL"},
    /*24*/ {0, false, "MAGIC_POINT"},
    /*25*/ {1, false, "USE_INVENTORY"},
    /*26*/ {0, true, "CHOICE"},
    /*27*/ {0, true, "FUEL"},
    /*28*/ {0, false, "CARRY_BY"},
    /*29*/ {0, false, "CDROM"},
    /*30*/ {1, false, "ECHELLE"},
    /*31*/ {1, false, "RND"},
    /*32*/ {1, false, "RAIL"},
    /*33*/ {0, true, "BETA"},
    /*34*/ {1, true, "BETA_OBJ"},
    /*35*/ {1, false, "CARRY_OBJ_BY"},
    /*36*/ {1, true, "ANGLE"},
    /*37*/ {1, true, "DISTANCE_MESSAGE"},
    /*38*/ {1, false, "HIT_OBJ_BY"},
    /*39*/ {1, true, "REAL_ANGLE"},
    /*40*/ {0, false, "DEMO"},
    /*41*/ {0, false, "COL_DECORS"},
    /*42*/ {1, false, "COL_DECORS_OBJ"},
    /*43*/ {0, false, "PROCESSOR"},
    /*44*/ {1, false, "OBJECT_DISPLAYED"},
    /*45*/ {1, true, "ANGLE_OBJ"},
};

static const char *TestOpName(U8 test)
{
    switch (test) {
    case LT_EQUAL: return "==";
    case LT_SUP: return ">";
    case LT_LESS: return "<";
    case LT_SUP_EQUAL: return ">=";
    case LT_LESS_EQUAL: return "<=";
    case LT_DIFFERENT: return "!=";
    default: return "?";
    }
}

// Appends one decoded "IF/SWIF/ONEIF/AND_IF/OR_IF/SNIF/NEVERIF" line and
// advances *offset past it, or returns false (leaving *offset unmoved) if
// the condition byte isn't one of the 46 known LF_ codes.
static bool DecodeLifeCondition(U8 *base, S32 *offset, S32 maxBytes, const char *mnemonic, std::string &out)
{
    S32 o = *offset;
    if (o >= maxBytes) return false;
    U8 lf = base[o];
    if (lf >= 46) return false;
    const LifeCondInfo &info = LifeConditions[lf];
    o++;
    S32 operand = 0;
    if (info.operandBytes) {
        if (o >= maxBytes) return false;
        operand = base[o];
        o++;
    }
    if (o >= maxBytes) return false;
    U8 test = base[o];
    o++;
    S32 value;
    if (info.valueIsS16) {
        if (o + 1 >= maxBytes) return false;
        value = *(S16 *)(base + o);
        o += 2;
    } else {
        if (o >= maxBytes) return false;
        value = (S8)base[o];
        o += 1;
    }
    if (o + 1 >= maxBytes) return false;
    S16 jumpTarget = *(S16 *)(base + o);
    o += 2;

    char line[160];
    if (info.operandBytes)
        std::snprintf(line, sizeof(line), "%s %s(obj=%d) %s %d -> goto %d\n",
                      mnemonic, info.name, operand, TestOpName(test), value, (int)jumpTarget);
    else
        std::snprintf(line, sizeof(line), "%s %s %s %d -> goto %d\n",
                      mnemonic, info.name, TestOpName(test), value, (int)jumpTarget);
    out += line;
    *offset = o;
    return true;
}

// The action-opcode subset of LM_* (GERELIFE.CPP's DoLife()) whose operand
// layout has been read and confirmed. Life scripts are much larger than
// track scripts (131 total LM_ opcodes vs. track's ~30 TM_ opcodes), so this
// intentionally covers the common subset actually seen in NPC behavior
// scripts (control flow, body/anim, dialogue, vars, doors, gold/keys,
// comportement blocks) rather than all 131; anything past LM_BETA (80), and
// a handful of rarer gaps below it (camera scripting, palette/lightning FX,
// buggy init), reports as unsupported instead of guessing.
static const char *LmName(U8 op)
{
    switch (op) {
    case LM_BODY: return "BODY";
    case LM_BODY_OBJ: return "BODY_OBJ";
    case LM_ANIM: return "ANIM";
    case LM_ANIM_OBJ: return "ANIM_OBJ";
    case LM_SET_TRACK: return "SET_TRACK";
    case LM_SET_TRACK_OBJ: return "SET_TRACK_OBJ";
    case LM_MESSAGE: return "MESSAGE";
    case LM_MESSAGE_OBJ: return "MESSAGE_OBJ";
    case LM_FALLABLE: return "FALLABLE";
    case LM_SET_DIR: return "SET_DIR";
    case LM_SET_DIR_OBJ: return "SET_DIR_OBJ";
    case LM_CAM_FOLLOW: return "CAM_FOLLOW";
    case LM_COMPORTEMENT_HERO: return "COMPORTEMENT_HERO";
    case LM_COMPORTEMENT: return "COMPORTEMENT";
    case LM_SET_COMPORTEMENT: return "SET_COMPORTEMENT";
    case LM_SET_COMPORTEMENT_OBJ: return "SET_COMPORTEMENT_OBJ";
    case LM_KILL_OBJ: return "KILL_OBJ";
    case LM_GIVE_GOLD_PIECES: return "GIVE_GOLD_PIECES";
    case LM_ADD_GOLD_PIECES: return "ADD_GOLD_PIECES";
    case LM_STOP_L_TRACK: return "STOP_L_TRACK";
    case LM_RESTORE_L_TRACK: return "RESTORE_L_TRACK";
    case LM_FOUND_OBJECT: return "FOUND_OBJECT";
    case LM_SET_DOOR_LEFT: return "SET_DOOR_LEFT";
    case LM_SET_DOOR_RIGHT: return "SET_DOOR_RIGHT";
    case LM_SET_DOOR_UP: return "SET_DOOR_UP";
    case LM_SET_DOOR_DOWN: return "SET_DOOR_DOWN";
    case LM_GIVE_BONUS: return "GIVE_BONUS";
    case LM_CHANGE_CUBE: return "CHANGE_CUBE";
    case LM_OBJ_COL: return "OBJ_COL";
    case LM_BRICK_COL: return "BRICK_COL";
    case LM_INVISIBLE: return "INVISIBLE";
    case LM_POS_POINT: return "POS_POINT";
    case LM_SET_MAGIC_LEVEL: return "SET_MAGIC_LEVEL";
    case LM_SUB_MAGIC_POINT: return "SUB_MAGIC_POINT";
    case LM_SET_LIFE_POINT_OBJ: return "SET_LIFE_POINT_OBJ";
    case LM_SUB_LIFE_POINT_OBJ: return "SUB_LIFE_POINT_OBJ";
    case LM_HIT_OBJ: return "HIT_OBJ";
    case LM_INC_CLOVER_BOX: return "INC_CLOVER_BOX";
    case LM_SET_USED_INVENTORY: return "SET_USED_INVENTORY";
    case LM_SET_HOLO_POS: return "SET_HOLO_POS";
    case LM_CLR_HOLO_POS: return "CLR_HOLO_POS";
    case LM_ADD_FUEL: return "ADD_FUEL";
    case LM_SUB_FUEL: return "SUB_FUEL";
    case LM_BETA: return "BETA";
    case LM_INIT_BUGGY: return "INIT_BUGGY";
    default: return "?";
    }
}

// The action-opcode subset of LM_* (GERELIFE.CPP's DoLife()) whose operand
// layout has been read and confirmed. Life scripts are much larger than
// track scripts (131 total LM_ opcodes vs. track's ~30 TM_ opcodes), so this
// intentionally covers the common subset actually seen in NPC behavior
// scripts (control flow, body/anim, dialogue, vars, doors, gold/keys,
// comportement blocks) rather than all 131; anything past LM_BETA (80), and
// a handful of rarer gaps below it (camera scripting, palette/lightning FX,
// buggy init), reports as unsupported instead of guessing.
// outInitialBody/outInitialAnim (both optional) recover the actor's actual
// starting body/animation -- the *disk* GenBody/GenAnim fields this method's
// only caller (RendererScanIslandActors) would otherwise use are frequently
// just a placeholder (GEN_BODY_NORMAL/GEN_ANIM_RIEN, both 0) that the
// script's own opening BODY/ANIM lines immediately override on the actor's
// first tick (GERELIFE.CPP's DoLife() runs from offset 0 the very first time
// it's called -- see PERSO.CPP's OffsetLife check), which this pure
// loading-side scan never simulates. Capturing the first BODY/ANIM seen
// *before any branch* mirrors exactly what DoLife() would have executed
// unconditionally on that first tick; a branch (IF/AND_IF/.../ELSE/OFFSET)
// makes everything after it conditional, so scanning stops trusting further
// BODY/ANIM lines as "definitely the starting one" at that point (though the
// text decode below keeps going for display).
static void DecodeLifeScript(T_OBJET *ptrobj, std::string &out, S16 *outInitialBody = NULL, U16 *outInitialAnim = NULL)
{
    if (!ptrobj->PtrLife) { out = "(no life script)\n"; return; }
    U8 *base = ptrobj->PtrLife;
    const S32 maxBytes = 16384;
    S32 offset = 0;
    char line[160];
    bool unconditional = true;

    while (offset < maxBytes) {
        U8 op = base[offset];
        offset++;
        bool ok = true;

        switch (op) {
        case LM_END: out += "END\n"; return;
        case LM_RETURN: out += "RETURN\n"; return;
        case LM_END_LIFE: out += "END_LIFE\n"; return;
        case LM_SUICIDE: out += "SUICIDE\n"; return;
        case LM_NOP: out += "NOP\n"; break;
        case LM_ENDIF: out += "ENDIF\n"; break;
        case LM_END_COMPORTEMENT: out += "END_COMPORTEMENT\n"; break;
        case LM_INC_CHAPTER: out += "INC_CHAPTER\n"; break;
        case LM_USE_ONE_LITTLE_KEY: out += "USE_ONE_LITTLE_KEY\n"; break;
        case LM_FULL_POINT: out += "FULL_POINT\n"; break;
        case LM_STOP_L_TRACK: out += "STOP_L_TRACK\n"; break;
        case LM_RESTORE_L_TRACK: out += "RESTORE_L_TRACK\n"; break;

        case LM_IF: ok = DecodeLifeCondition(base, &offset, maxBytes, "IF", out); unconditional = false; break;
        case LM_AND_IF: ok = DecodeLifeCondition(base, &offset, maxBytes, "AND_IF", out); unconditional = false; break;
        case LM_OR_IF: ok = DecodeLifeCondition(base, &offset, maxBytes, "OR_IF", out); unconditional = false; break;
        case LM_SWIF: ok = DecodeLifeCondition(base, &offset, maxBytes, "SWIF", out); unconditional = false; break;
        case LM_SNIF: ok = DecodeLifeCondition(base, &offset, maxBytes, "SNIF", out); unconditional = false; break;
        case LM_ONEIF: ok = DecodeLifeCondition(base, &offset, maxBytes, "ONEIF", out); unconditional = false; break;
        case LM_NEVERIF: ok = DecodeLifeCondition(base, &offset, maxBytes, "NEVERIF", out); unconditional = false; break;

        case LM_OFFSET:
        case LM_ELSE:
            if (offset + 1 >= maxBytes) { ok = false; break; }
            {
                S16 target = *(S16 *)(base + offset); offset += 2;
                std::snprintf(line, sizeof(line), "%s -> goto %d\n", op == LM_OFFSET ? "OFFSET" : "ELSE", (int)target);
                out += line;
            }
            unconditional = false;
            break;

        case LM_BODY:
            if (offset >= maxBytes) { ok = false; break; }
            {
                U8 a = base[offset]; offset++;
                if (unconditional && outInitialBody) *outInitialBody = (S16)a;
                std::snprintf(line, sizeof(line), "%s %d\n", LmName(op), (int)a);
                out += line;
            }
            break;

        // One-byte operand (usually an object number or a small constant).
        // LM_INIT_BUGGY (GERELIFE.CPP: "num = *PtrPrg++; InitBuggy(numobj,
        // num);") fits this same one-byte shape -- added after a report of
        // an actual vehicle actor's script decode stopping dead exactly at
        // this opcode ("[opcode 70 not decoded -- stopping]"), truncating
        // everything after it in the Script tab.
        case LM_FALLABLE: case LM_SET_DIR: case LM_CAM_FOLLOW:
        case LM_COMPORTEMENT_HERO: case LM_COMPORTEMENT: case LM_KILL_OBJ:
        case LM_FOUND_OBJECT: case LM_GIVE_BONUS: case LM_CHANGE_CUBE:
        case LM_OBJ_COL: case LM_BRICK_COL: case LM_INVISIBLE: case LM_POS_POINT:
        case LM_SET_MAGIC_LEVEL: case LM_SUB_MAGIC_POINT: case LM_INC_CLOVER_BOX:
        case LM_SET_USED_INVENTORY: case LM_SET_HOLO_POS: case LM_CLR_HOLO_POS:
        case LM_ADD_FUEL: case LM_SUB_FUEL: case LM_INIT_BUGGY:
            if (offset >= maxBytes) { ok = false; break; }
            {
                U8 a = base[offset]; offset++;
                std::snprintf(line, sizeof(line), "%s %d\n", LmName(op), (int)a);
                out += line;
            }
            break;

        // Two-byte operand: (num, value) pair.
        case LM_BODY_OBJ: case LM_SET_DIR_OBJ:
            if (offset + 1 >= maxBytes) { ok = false; break; }
            {
                U8 n = base[offset], v = base[offset + 1]; offset += 2;
                std::snprintf(line, sizeof(line), "%s obj=%d %d\n", LmName(op), (int)n, (int)v);
                out += line;
            }
            break;
        case LM_SET_LIFE_POINT_OBJ: case LM_SUB_LIFE_POINT_OBJ: case LM_HIT_OBJ:
            if (offset + 1 >= maxBytes) { ok = false; break; }
            {
                U8 n = base[offset], v = base[offset + 1]; offset += 2;
                std::snprintf(line, sizeof(line), "%s obj=%d %d\n", LmName(op), (int)n, (int)v);
                out += line;
            }
            break;
        case LM_SET_VAR_CUBE:
            if (offset + 1 >= maxBytes) { ok = false; break; }
            {
                U8 n = base[offset], v = base[offset + 1]; offset += 2;
                std::snprintf(line, sizeof(line), "SET_VAR_CUBE[%d] = %d\n", (int)n, (int)v);
                out += line;
            }
            break;

        case LM_ANIM:
            if (offset + 1 >= maxBytes) { ok = false; break; }
            {
                S16 v = *(S16 *)(base + offset); offset += 2;
                if (unconditional && outInitialAnim) *outInitialAnim = (U16)v;
                std::snprintf(line, sizeof(line), "%s %d\n", LmName(op), (int)v);
                out += line;
            }
            break;

        // Two-byte operand: a single S16 value.
        case LM_MESSAGE: case LM_SET_TRACK: case LM_SET_COMPORTEMENT:
        case LM_GIVE_GOLD_PIECES: case LM_ADD_GOLD_PIECES: case LM_SET_DOOR_LEFT:
        case LM_SET_DOOR_RIGHT: case LM_SET_DOOR_UP: case LM_SET_DOOR_DOWN: case LM_BETA:
            if (offset + 1 >= maxBytes) { ok = false; break; }
            {
                S16 v = *(S16 *)(base + offset); offset += 2;
                std::snprintf(line, sizeof(line), "%s %d\n", LmName(op), (int)v);
                out += line;
            }
            break;

        // Three-byte operand: (num, S16 value).
        case LM_ANIM_OBJ: case LM_SET_TRACK_OBJ: case LM_SET_COMPORTEMENT_OBJ: case LM_MESSAGE_OBJ:
            if (offset + 2 >= maxBytes) { ok = false; break; }
            {
                U8 n = base[offset]; S16 v = *(S16 *)(base + offset + 1); offset += 3;
                std::snprintf(line, sizeof(line), "%s obj=%d %d\n", LmName(op), (int)n, (int)v);
                out += line;
            }
            break;
        case LM_SET_VAR_GAME:
            if (offset + 2 >= maxBytes) { ok = false; break; }
            {
                U8 n = base[offset]; S16 v = *(S16 *)(base + offset + 1); offset += 3;
                std::snprintf(line, sizeof(line), "SET_VAR_GAME[%d] = %d\n", (int)n, (int)v);
                out += line;
            }
            break;

        case LM_PLAY_ACF: {
            std::string name;
            while (offset < maxBytes && base[offset] != 0) { name += (char)base[offset]; offset++; }
            if (offset >= maxBytes) { ok = false; break; }
            offset++; // trailing NUL
            std::snprintf(line, sizeof(line), "PLAY_ACF \"%s\"\n", name.c_str());
            out += line;
            break;
        }

        default:
            ok = false;
            break;
        }

        if (!ok) {
            std::snprintf(line, sizeof(line), "[opcode %d not decoded -- stopping]\n", (int)op);
            out += line;
            return;
        }
    }
    out += "[script longer than decode budget -- stopping]\n";
}

static const char *TmName(U8 op)
{
    switch (op) {
    case TM_LABEL: return "LABEL";
    case TM_BODY: return "BODY";
    case TM_BACKGROUND: return "BACKGROUND";
    case TM_SET_FRAME: return "SET_FRAME";
    case TM_SET_FRAME_3DS: return "SET_FRAME_3DS";
    case TM_SET_START_3DS: return "SET_START_3DS";
    case TM_SET_END_3DS: return "SET_END_3DS";
    case TM_START_ANIM_3DS: return "START_ANIM_3DS";
    case TM_WAIT_FRAME_3DS: return "WAIT_FRAME_3DS";
    case TM_VOLUME: return "VOLUME";
    case TM_ANIM: return "ANIM";
    case TM_SAMPLE: return "SAMPLE";
    case TM_SAMPLE_RND: return "SAMPLE_RND";
    case TM_SAMPLE_ALWAYS: return "SAMPLE_ALWAYS";
    case TM_SAMPLE_STOP: return "SAMPLE_STOP";
    case TM_REPEAT_SAMPLE: return "REPEAT_SAMPLE";
    case TM_SIMPLE_SAMPLE: return "SIMPLE_SAMPLE";
    case TM_WAIT_NB_ANIM: return "WAIT_NB_ANIM";
    case TM_ANGLE: return "ANGLE";
    case TM_FACE_TWINSEN: return "FACE_TWINSEN";
    case TM_OPEN_LEFT: return "OPEN_LEFT";
    case TM_OPEN_RIGHT: return "OPEN_RIGHT";
    case TM_OPEN_UP: return "OPEN_UP";
    case TM_OPEN_DOWN: return "OPEN_DOWN";
    case TM_BETA: return "BETA";
    case TM_GOTO: return "GOTO";
    case TM_SPEED: return "SPEED";
    case TM_SPRITE: return "SPRITE";
    case TM_DECALAGE: return "DECALAGE";
    case TM_FREQUENCE: return "FREQUENCE";
    case TM_ANGLE_RND: return "ANGLE_RND";
    case TM_LOOP: return "LOOP";
    case TM_WAIT_NB_SECOND: return "WAIT_NB_SECOND";
    case TM_WAIT_NB_SECOND_RND: return "WAIT_NB_SECOND_RND";
    case TM_WAIT_NB_DIZIEME: return "WAIT_NB_DIZIEME";
    case TM_WAIT_NB_DIZIEME_RND: return "WAIT_NB_DIZIEME_RND";
    default: return "?";
    }
}

// Same linear-walk approach as ExtractTrackWaypoints, but rendering each
// opcode as a readable text line instead of only collecting waypoints.
static void DecodeTrackScript(T_OBJET *ptrobj, std::string &out)
{
    if (!ptrobj->PtrTrack) { out = "(no track script)\n"; return; }
    U8 *base = ptrobj->PtrTrack;
    S32 offset = 0;
    const S32 maxBytes = 8192;
    char line[160];

    while (offset < maxBytes) {
        U8 op = base[offset];
        offset++;
        switch (op) {
        case TM_GOTO_POINT: case TM_GOTO_POINT_3D: case TM_GOTO_SYM_POINT: case TM_POS_POINT: {
            if (offset >= maxBytes) goto trackUnsupported;
            U8 index = base[offset]; offset++;
            const char *name = op == TM_GOTO_POINT ? "GOTO_POINT" : op == TM_GOTO_POINT_3D ? "GOTO_POINT_3D"
                : op == TM_GOTO_SYM_POINT ? "GOTO_SYM_POINT" : "POS_POINT";
            std::snprintf(line, sizeof(line), "%s point=%d\n", name, (int)index);
            out += line;
            break;
        }
        case TM_LABEL: case TM_BODY: case TM_BACKGROUND: case TM_SET_FRAME:
        case TM_SET_FRAME_3DS: case TM_SET_START_3DS: case TM_SET_END_3DS:
        case TM_START_ANIM_3DS: case TM_WAIT_FRAME_3DS: case TM_VOLUME:
            if (offset >= maxBytes) goto trackUnsupported;
            std::snprintf(line, sizeof(line), "%s %d\n", TmName(op), (int)base[offset]);
            out += line;
            offset++;
            break;
        case TM_ANIM: case TM_SAMPLE: case TM_SAMPLE_RND: case TM_SAMPLE_ALWAYS:
        case TM_SAMPLE_STOP: case TM_REPEAT_SAMPLE: case TM_SIMPLE_SAMPLE:
        case TM_WAIT_NB_ANIM: case TM_ANGLE: case TM_FACE_TWINSEN: case TM_OPEN_LEFT:
        case TM_OPEN_RIGHT: case TM_OPEN_UP: case TM_OPEN_DOWN: case TM_BETA:
        case TM_GOTO: case TM_SPEED: case TM_SPRITE: case TM_DECALAGE: case TM_FREQUENCE:
            if (offset + 1 >= maxBytes) goto trackUnsupported;
            std::snprintf(line, sizeof(line), "%s %d\n", TmName(op), (int)*(S16 *)(base + offset));
            out += line;
            offset += 2;
            break;
        case TM_ANGLE_RND: case TM_LOOP:
            if (offset + 3 >= maxBytes) goto trackUnsupported;
            std::snprintf(line, sizeof(line), "%s\n", TmName(op));
            out += line;
            offset += 4;
            break;
        case TM_WAIT_NB_SECOND: case TM_WAIT_NB_SECOND_RND: case TM_WAIT_NB_DIZIEME: case TM_WAIT_NB_DIZIEME_RND:
            if (offset + 4 >= maxBytes) goto trackUnsupported;
            std::snprintf(line, sizeof(line), "%s\n", TmName(op));
            out += line;
            offset += 5;
            break;
        case TM_PLAY_ACF: {
            std::string name;
            while (offset < maxBytes && base[offset] != 0) { name += (char)base[offset]; offset++; }
            if (offset >= maxBytes) goto trackUnsupported;
            offset++;
            std::snprintf(line, sizeof(line), "PLAY_ACF \"%s\"\n", name.c_str());
            out += line;
            break;
        }
        // No operand bytes (see ExtractTrackWaypoints' own comment on these
        // same two opcodes) -- added after a report of an actual vehicle
        // actor's track script stopping decode dead exactly at TM_WAIT_ANIM,
        // which uncovered TM_STOP right behind it the moment that first gap
        // was fixed.
        case TM_WAIT_ANIM:
            out += "WAIT_ANIM\n";
            break;
        case TM_STOP:
            out += "STOP\n";
            break;
        case TM_END:
            out += "END\n";
            return;
        default:
            goto trackUnsupported;
        }
        continue;
    trackUnsupported:
        std::snprintf(line, sizeof(line), "[opcode %d not decoded -- stopping]\n", (int)op);
        out += line;
        return;
    }
    out += "[script longer than decode budget -- stopping]\n";
}

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
        // No operand bytes at all (GERETRAK.CPP's own TM_WAIT_ANIM/TM_STOP
        // cases only ever manipulate ptrobj->OffsetTrack itself, never read
        // past the opcode byte the loop above already consumed) -- added
        // after a report of an actual vehicle actor's track script stopping
        // decode dead exactly at TM_WAIT_ANIM, which uncovered TM_STOP
        // right behind it the moment that first gap was fixed. Continuing
        // past TM_STOP rather than treating it like TM_END's own hard stop:
        // it means "nothing more to do at runtime from here", not "nothing
        // more exists in the byte stream" -- a labeled jump target reachable
        // via GOTO could still legitimately follow it.
        case TM_WAIT_ANIM:
        case TM_STOP:
            break;
        case TM_END:
            return;
        default:
            return; // unrecognized opcode: stop rather than risk misreading
        }
    }
}

// FICHE.CPP -- resolves a per-actor generic body/anim number (numbody/
// numanim) through numobj's own PtrFile3D fiche table into a raw BODY.HQR/
// ANIM.HQR archive index, or -1 if that generic number isn't in the table.
// See this function's own use below for why RendererScanIslandActors needs
// it.
extern S32 SearchBody(U8 numbody, S32 numobj);
extern S32 SearchAnim(U16 numanim, S32 numobj);

static int LoadSceneLive(S32 scene);

int RendererScanIslandActors(int islandIndex)
{
    g_actors.clear();
    g_waypointX.clear();
    g_waypointY.clear();
    g_waypointZ.clear();
    // Scene indices are per-island; a stale edit keyed by scene 5 from the
    // previous island would otherwise silently apply to scene 5 of whatever
    // island loads next.
    g_pendingEdits.clear();
    for (S32 cy = 0; cy < 16; cy++)
        for (S32 cx = 0; cx < 16; cx++)
            g_sceneForCube[cy][cx] = -1;
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
        if (cubeX >= 0 && cubeX < 16 && cubeY >= 0 && cubeY < 16) g_sceneForCube[cubeY][cubeX] = scene;
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
            actor.Beta = ptrobj->Obj.Beta;
            actor.GenBody = ptrobj->GenBody;
            actor.GenAnim = ptrobj->GenAnim;
            actor.LifePoint = ptrobj->LifePoint;
            actor.Armure = ptrobj->Armure;
            actor.HitForce = ptrobj->HitForce;
            actor.Move = ptrobj->Move;
            actor.Flags = ptrobj->Flags;
            actor.WaypointStart = (S32)g_waypointX.size();
            ExtractTrackWaypoints(ptrobj, baseX, baseZ);
            actor.WaypointCount = (S32)g_waypointX.size() - actor.WaypointStart;
            // DecodeLifeScript overwrites actor.GenBody/GenAnim (already
            // seeded above from the raw disk fields) only if it finds an
            // unconditional BODY/ANIM line -- see its own comment for why
            // the disk fields alone are frequently just a placeholder.
            //
            // What ends up in actor.GenBody/GenAnim here is still the
            // *generic* per-actor number straight from disk (or the life
            // script) -- small, per-object-fiche-table-relative values,
            // e.g. "this actor's own 3rd body", not a BODY.HQR index --
            // despite this struct field's own name suggesting otherwise
            // (an assumption that holds for an actor *added* through this
            // editor, which really does start out holding a raw index by
            // construction, but not for one read from a real scene file).
            // Resolving it to a real archive index needs this object's own
            // PtrFile3D fiche table (SearchBody()/SearchAnim(), FICHE.CPP)
            // -- which this scan can't safely reach: it runs under
            // FlagLoadGame=TRUE (set just above, to skip LoadScene()'s own
            // LoadFile3D() call, since this renderer-only build never
            // populates LoadFile3D's target BufferFile3D), and DISKFUNC.CPP's
            // own LoadScene() only ever assigns PtrFile3D "if (!FlagLoadGame)"
            // -- confirmed the hard way, a first attempt at resolving right
            // here crashed on startup dereferencing whatever PtrFile3D
            // happened to hold (never actually assigned in this mode) for
            // every single scanned object. See RendererGetActorAttributes
            // instead, which resolves this lazily, only once this specific
            // actor's own scene is genuinely live (PtrFile3D guaranteed
            // real then) rather than during this position-only scan.
            DecodeTrackScript(ptrobj, actor.TrackScript);
            {
                S16 initialBody = -1;
                U16 initialAnim = 0xFFFF;
                DecodeLifeScript(ptrobj, actor.LifeScript, &initialBody, &initialAnim);
                if (initialBody >= 0) actor.GenBody = initialBody;
                if (initialAnim != 0xFFFF) actor.GenAnim = initialAnim;
            }
            actor.SceneIndex = scene;
            actor.ObjectSlot = n;
            actor.CubeX = cubeX;
            actor.CubeY = cubeY;
            g_actors.push_back(actor);
        }
    }

    // Preload pass: bring each scene fully live once (real PtrFile3D, real
    // StartInitAllObjs) so every actor's resolved raw body/anim and moveset
    // are cached up front -- the attributes dialog then never has to
    // temp-load a far actor's cube (which churned shared native state and
    // could resolve the wrong body).
    FlagLoadGame = savedFlagLoadGame;
    {
        std::vector<S32> scenes;
        for (size_t gi = 0; gi < g_actors.size(); gi++) {
            S32 s = g_actors[gi].SceneIndex;
            if (std::find(scenes.begin(), scenes.end(), s) == scenes.end()) scenes.push_back(s);
        }
        for (size_t si = 0; si < scenes.size(); si++) LoadSceneLive(scenes[si]);
    }
    FlagLoadGame = savedFlagLoadGame;
    LastCubeMode = savedLastCubeMode;
    return (int)g_actors.size();
}

extern void StartInitAllObjs(void);

// Loads the real scene for the cube the camera has just moved to -- with
// live NbObjets/ListObjet actor data and (via StartInitAllObjs(), which the
// engine's own ChangeCube() calls for the same reason on a real cube
// transition) each actor's body/anim actually initialized, ready for
// AffichageActorsZBuf() (EXTFUNC.CPP) to draw. Called from
// lba2_renderer_load_cube() only when the cube actually changes.
//
// Deliberately does NOT call the full ChangeCube(): that also repositions
// the hero from NewPosX/Y/Z-style gameplay state this headless renderer
// never sets, which would be wrong here. This calls LoadScene() +
// StartInitAllObjs() directly instead -- the two calls ChangeCube() itself
// makes for the same reason on a real cube transition -- to get body/anim
// initialization without the hero-reposition/save-state logic that comes
// with the rest of ChangeCube().
//
// Unlike RendererScanIslandActors() above, this does NOT set
// FlagLoadGame=TRUE: LoadScene() (DISKFUNC.CPP) only loads each object's
// PtrFile3D (the per-object generic-body-name table SearchBody() walks to
// resolve a GenBody index to an actual HQR_Bodys entry -- see FICHE.CPP)
// when FlagLoadGame is false, and StartInitAllObjs()'s InitBody() calls
// crash dereferencing a null PtrFile3D otherwise (confirmed: SearchBody()
// segfault, gdb-bisected while adding this). FlagLoadGame=FALSE also
// enables LoadScene()'s fade-to-black -- but only when CubeMode+
// LastCubeMode != 2 (matching CUBE_EXTERIEUR to CUBE_EXTERIEUR sums to
// exactly that), so keeping LastCubeMode=CUBE_EXTERIEUR set here (as the
// actor scan also does) skips the fade the same way FlagLoadGame=TRUE would
// have. FlagDrawHorizon is intentionally left alone (already TRUE) since
// its own OR-branch of that same condition would otherwise force the fade
// regardless of the CubeMode sum.
//
// A cube with no scene (g_sceneForCube == -1, e.g. open sea) leaves
// NbObjets/ListObjet however LoadCube() left them; AffichageActorsZBuf()
// looping over stale actors from a previous cube would draw them in the
// wrong place, so the caller must not skip this and must treat a false
// return as "no actors for this cube" by clearing NbObjets itself.
extern void InitBody(U8 gennewbody, U8 numobj);
extern S32 InitAnim(U16 gennewanim, U8 flag, U8 numobj);

// Retail actors get their real starting body/animation from their life
// script's opening unconditional BODY/ANIM lines on the first game tick; the
// disk GenBody/GenAnim fields are frequently just placeholders. This renderer
// never runs scripts, so apply those opening lines here right after a scene's
// StartInitAllObjs(), before any session edits are reapplied on top.
static void ApplyInitialScriptState(void)
{
    for (S32 n = 1; n < NbObjets; n++) {
        T_OBJET *ptrobj = &ListObjet[n];
        if (ptrobj->Flags & SPRITE_3D) continue;
        S16 body = -1;
        U16 anim = 0xFFFF;
        std::string ignored;
        DecodeLifeScript(ptrobj, ignored, &body, &anim);
        if (body >= 0) InitBody((U8)body, (U8)n);
        if (anim != 0xFFFF) InitAnim(anim, ANIM_REPEAT, (U8)n);
    }
}

static void ReapplyPendingEditsToListObjet(S32 sceneIndex);
static int WalkFile3DNativeAnims(const U8 *ptrc, int *outAnims, int maxCount);

// Snapshots every known actor's real moveset for sceneIndex into its own
// CachedNativeAnims, while PtrFile3D is actually valid for it (right after
// a real LoadScene() -- see CachedNativeAnims' own comment for why that's
// the only time). A fixed 128-entry stack buffer matches the cap the C#
// side already applies to this same walk (RendererLibraryApi.cs's
// GetActorNativeAnims); no real character fiche table gets remotely close
// to that many F_ANIM entries.
static void CacheNativeAnimsForScene(S32 sceneIndex)
{
    for (size_t gi = 0; gi < g_actors.size(); gi++) {
        RendererActor &ga = g_actors[gi];
        if (ga.SceneIndex != sceneIndex || ga.ObjectSlot < 1 || ga.ObjectSlot >= NbObjets) continue;
        const U8 *ptrFile3D = ListObjet[ga.ObjectSlot].PtrFile3D;
        if (!ptrFile3D) continue;
        S32 buffer[128];
        int count = WalkFile3DNativeAnims(ptrFile3D, buffer, 128);
        if (count <= 0) continue;
        if (count > 128) count = 128;
        ga.CachedNativeAnims.assign(buffer, buffer + count);
    }
}

// See RendererActor::ResolvedBody. Obj.Body.Num/Obj.Anim.Num hold the raw
// archive index the engine actually resolved for this object, i.e. exactly
// what is drawn.
static void CacheResolvedBodyAnimForScene(S32 sceneIndex)
{
    for (size_t gi = 0; gi < g_actors.size(); gi++) {
        RendererActor &ga = g_actors[gi];
        if (ga.SceneIndex != sceneIndex || ga.ObjectSlot < 1 || ga.ObjectSlot >= NbObjets) continue;
        const T_OBJET &o = ListObjet[ga.ObjectSlot];
        if (o.Flags & SPRITE_3D) continue;
        if (!ga.GenIsRaw && o.Obj.Body.Num >= 0) ga.ResolvedBody = (S32)o.Obj.Body.Num;
        if (!ga.GenIsRaw && o.Obj.Anim.Num >= 0) ga.ResolvedAnim = (S32)o.Obj.Anim.Num;
        if (o.Obj.Body.Num >= 0) {
            ga.BoundsValid = true;
            ga.BoundsX[0] = o.XMin; ga.BoundsX[1] = o.XMax;
            ga.BoundsY[0] = o.YMin; ga.BoundsY[1] = o.YMax;
            ga.BoundsZ[0] = o.ZMin; ga.BoundsZ[1] = o.ZMax;
        }
    }
}

static int LoadSceneLive(S32 scene);

int RendererLoadCubeScene(int cubeX, int cubeY)
{
    if (cubeX < 0 || cubeX >= 16 || cubeY < 0 || cubeY >= 16) return 0;
    S32 scene = g_sceneForCube[cubeY][cubeX];
    if (scene < 0) return 0;
    return LoadSceneLive(scene);
}

static int LoadSceneLive(S32 scene)
{

    S32 savedFlagLoadGame = FlagLoadGame;
    S32 savedLastCubeMode = LastCubeMode;
    FlagLoadGame = FALSE;
    LastCubeMode = CUBE_EXTERIEUR;

    S32 ok = LoadScene(scene);
    if (ok) {
        StartInitAllObjs();
        ApplyInitialScriptState();
        // LoadScene() just repopulated ListObjet/NbObjets straight off disk,
        // discarding any session edits made earlier -- reapply them now so
        // an edited actor doesn't appear to "revert" every time the camera
        // pans back onto its cube (which happens constantly with wide-radius
        // rendering; see AffGrilleExtWide's own comment on how often cubes
        // reload).
        ReapplyPendingEditsToListObjet(scene);
        CacheNativeAnimsForScene(scene);
        CacheResolvedBodyAnimForScene(scene);
    }

    FlagLoadGame = savedFlagLoadGame;
    LastCubeMode = savedLastCubeMode;
    return ok ? 1 : 0;
}

// Loads and renders an LBA2 indoor/interior scene: a completely different
// path from every RendererLoad*CubeScene above, which all assume an
// exterior island's 3D terrain. Interior scenes are the older, LBA1-derived
// isometric "brick grid" renderer (GRILLE.CPP's InitGrille/AffGrille) --
// simple 2D sprite tiles composited with a fixed-angle camera, not 3D
// geometry. Retail never mixes the two in one call path either: the game's
// own ChangeCube() (OBJECT.CPP) picks between InitGrilleExt (exterior) and
// InitGrille (interior) via a PtrInitGrille function pointer keyed off this
// same CubeMode byte, which LoadScene() (DISKFUNC.CPP) reads straight off
// the scene's own on-disk header -- exactly like every RendererLoad*
// function above already relies on for other scene fields.
//
// Skips ChangeCube() entirely (same reason RendererLoadCubeData bypasses it
// for exterior cubes: it also repositions the hero from gameplay state this
// headless renderer never sets) and calls InitGrille(NewCube) directly --
// NewCube is exactly what ChangeCube() itself passes PtrInitGrille, since
// LoadScene() already sets NewCube = numscene before returning.
extern S32 CubeMode;
extern S32 NewCube;
extern void InitGrille(U32 numcube);
extern void Init3DView(void);
extern void CameraCenter(S32 flagbeta);
extern U8 NumObjFollow;
extern void SetPosCamera(S32 x, S32 y, S32 z);
extern S32 ObjectSetInterDep(T_OBJ_3D *obj);

// AffichageActorsZBuf's own per-object draw sequence (EXTFUNC.CPP), adapted
// for the interior renderer. Two differences from the exterior version:
//
// 1. Filler: plain FILL_POLY_TEXTURES, not FILL_POLY_FOG_ZBUFFER, and no fog
//    CLUT. Retail only switches to the fog/Z-buffer fillers outdoors
//    (OBJECT.CPP, all guarded by CubeMode == CUBE_EXTERIEUR). Indoors,
//    BufCube and BufferBrick are carved out of the *same* PtrZBuffer
//    allocation (MEM.CPP: BufCube = ListMem[MEM_PTR_ZBUFFER]; BufferBrick =
//    BufCube + cube size), so a Z-buffered fill writes depth values straight
//    over the loaded brick grid and sprite art. That was the memory
//    corruption seen when this first drew actors: the first frame looked
//    fine (bricks were already drawn), and every later AffGrille() read
//    overwritten brick data and painted speckled noise. Ruled out on the way
//    (each tested, none the cause): animation interpolation, fill-mode
//    reassertion, drawing actors only once, the framebuffer clear.
// 2. ObjectSetInterDep before ObjectSetInterFrame, saving/restoring X/Y/Z
//    around it (see AffichageBodyPreview's comment: real actors get it from
//    the main per-object update loop, which this renderer never runs; it
//    also applies root motion, which would otherwise creep actors on every
//    re-render).
//
// Occlusion by walls in front of an actor: AffGrille never populates a
// Z-buffer (interior bricks use their own screen-column ordering,
// ListBrickColon), so retail's DrawRecover path (INTEXT.CPP) is used instead:
// DrawOverBrick re-blits the pristine brick pixels (kept in Screen) that sit
// in front of the actor, masked over the actor just drawn.
struct InteriorActorScreen {
    bool Valid;
    S32 X, Y;
    S32 MinX, MinY, MaxX, MaxY;
};
static InteriorActorScreen g_interiorActorScreens[MAX_OBJETS];

static void RendererDrawInteriorActors(void)
{
    for (S32 n = 0; n < MAX_OBJETS; n++) g_interiorActorScreens[n].Valid = false;

    for (S32 n = 1; n < NbObjets; n++) {
        T_OBJET *ptrobj = &ListObjet[n];

        if (ptrobj->Flags & SPRITE_3D) continue;
        if (ptrobj->Obj.Body.Num < 0) continue;

        if (!(ptrobj->WorkFlags & MANUAL_INTER_FRAME)) {
            S32 savedX = ptrobj->Obj.X, savedY = ptrobj->Obj.Y, savedZ = ptrobj->Obj.Z;
            ObjectSetInterDep(&ptrobj->Obj);
            ptrobj->Obj.X = savedX;
            ptrobj->Obj.Y = savedY;
            ptrobj->Obj.Z = savedZ;
        }

        LongWorldRotatePoint(ptrobj->Obj.X, ptrobj->Obj.Y, ptrobj->Obj.Z);
        if (!LongProjectPoint(X0, Y0, Z0)) continue;
        S32 screenX = Xp, screenY = Yp;

        // Plain textured filler, not FILL_POLY_FOG_ZBUFFER: retail only ever
        // switches to the fog/Z-buffer fillers outdoors (OBJECT.CPP, all
        // guarded by CubeMode == CUBE_EXTERIEUR). Indoors, BufCube and
        // BufferBrick are carved out of the *same* PtrZBuffer allocation
        // (MEM.CPP: BufCube = ListMem[MEM_PTR_ZBUFFER]; BufferBrick =
        // BufCube + cube size), so a Z-buffered fill writes depth values
        // straight over the loaded brick grid and sprite data -- the
        // corruption AffGrille() then reads on the next render.
        Switch_Fillers(FILL_POLY_TEXTURES);

        if (!(ptrobj->WorkFlags & MANUAL_INTER_FRAME)) {
            ObjectSetInterFrame(&ptrobj->Obj);
        }

        UnsetClip();
        InteriorActorScreen &rec = g_interiorActorScreens[n];
        rec.Valid = false;
        // Retail (OBJECT.CPP): ObjectDisplay returns non-zero if any of the
        // body is on screen, and leaves the drawn region in ScreenX/YMin/Max.
        // DrawOverBrick must be clipped to exactly that region -- with the
        // full-screen clip UnsetClip leaves, it re-blitted every brick in
        // front of the actor's cell over the *whole* screen, covering other
        // actors that should not be occluded.
        if (ObjectDisplay(&ptrobj->Obj)) {
            SetClip(ScreenXMin, ScreenYMin, ScreenXMax, ScreenYMax);
            if (ClipXMin <= ClipXMax && ClipYMin <= ClipYMax) {
                DrawOverBrick(ptrobj->Obj.X / SIZE_BRICK_XZ, ptrobj->Obj.Y / SIZE_BRICK_Y, ptrobj->Obj.Z / SIZE_BRICK_XZ);
                rec.Valid = true;
                rec.X = screenX; rec.Y = screenY;
                rec.MinX = ClipXMin; rec.MinY = ClipYMin; rec.MaxX = ClipXMax; rec.MaxY = ClipYMax;
            }
            UnsetClip();
        }
    }
}

// True only when numscene's own on-disk header says CUBE_INTERIEUR --
// callers (the C# side) use this to decide which Load*/Render* pair to call
// for a given scene before committing to either path. Loads the scene as a
// side effect (same as every other RendererLoad* here), since the mode byte
// can only be read by actually loading it.
int RendererLoadInteriorScene(int numscene)
{
    if (numscene < 0) return 0;

    S32 ok = LoadScene(numscene);
    if (!ok) return 0;

    if (CubeMode != CUBE_INTERIEUR) return 0; // caller should use an exterior Load* instead

    StartInitAllObjs();
    ApplyInitialScriptState();

    // Init3DView() (GAMEMENU.CPP) sets up the fixed isometric projection
    // (SetIsoProjection/SetAngleCamera(0,0,0)) interior scenes always use --
    // unlike the exterior view's free-orbiting camera, there is no rotation
    // here, so this only ever needs setting once per scene load, not once
    // per frame. NumObjFollow=0 (the hero's own object slot, per
    // LoadScene()'s own "HERO_START" section -- ListObjet[0]) then
    // CameraCenter(1) snaps the camera to the hero's containing brick
    // (StartXCube/Y/Z), matching what the retail game does on every real
    // cube entry (INTEXT.CPP).
    Init3DView();
    NumObjFollow = 0;
    CameraCenter(1);

    InitGrille((U32)NewCube);

    // Re-apply this session's attribute/position edits (kept across reloads,
    // same as the exterior path's own reload does).
    ReapplyPendingEditsToListObjet(numscene);

    // Makes this scene's actors reachable through the same g_actors-indexed
    // Get/Set APIs the exterior path uses (attributes window etc.). Interior
    // coordinates are already scene-local, so cube offsets are 0.
    g_actors.clear();
    g_waypointX.clear();
    g_waypointY.clear();
    g_waypointZ.clear();
    for (S32 n = 1; n < NbObjets; n++) {
        T_OBJET *ptrobj = &ListObjet[n];
        RendererActor actor;
        actor.X = ptrobj->Obj.X;
        actor.Y = ptrobj->Obj.Y;
        actor.Z = ptrobj->Obj.Z;
        actor.Beta = ptrobj->Obj.Beta;
        actor.GenBody = ptrobj->GenBody;
        actor.GenAnim = ptrobj->GenAnim;
        actor.LifePoint = ptrobj->LifePoint;
        actor.Armure = ptrobj->Armure;
        actor.HitForce = ptrobj->HitForce;
        actor.Move = ptrobj->Move;
        actor.Flags = ptrobj->Flags;
        actor.WaypointStart = 0;
        actor.WaypointCount = 0;
        DecodeTrackScript(ptrobj, actor.TrackScript);
        DecodeLifeScript(ptrobj, actor.LifeScript);
        actor.SceneIndex = numscene;
        actor.ObjectSlot = n;
        actor.CubeX = 0;
        actor.CubeY = 0;
        {
            std::map<S32, ScenePendingEdits>::iterator pit = g_pendingEdits.find(numscene);
            if (pit != g_pendingEdits.end()) {
                std::map<S32, PendingAttributeEdit>::iterator ait = pit->second.AttributeEdits.find(n);
                if (ait != pit->second.AttributeEdits.end() && ait->second.HasAttributes) actor.GenIsRaw = true;
            }
        }
        g_actors.push_back(actor);
    }
    CacheNativeAnimsForScene(numscene);
    CacheResolvedBodyAnimForScene(numscene);

    return 1;
}

// Repositions the interior camera set up by RendererLoadInteriorScene
// without touching its angle (still whatever Init3DView's own
// SetAngleCamera(0,0,0) left it at -- see that function's own comment on
// why the fixed isometric angle only ever needs setting once). Lets the C#
// side pan freely around the loaded scene's own brick grid (SIZE_CUBE_X/Y/Z
// * SIZE_BRICK_XZ/Y, COMMON.H -- up to 64*512 world units on X/Z) via its
// existing pan scrollbars, mirroring CameraCenter(1)/(0) (INTEXT.CPP)
// without that call's own hero-lookup (there is no "the hero moved" here,
// just "the user scrolled").
//
// Calling SetPosCamera() alone (an earlier version of this function) had no
// visible effect on the brick grid at all -- confirmed live: bricks stayed
// exactly where they were before the "pan", while actors (which do use
// SetPosCamera's own CameraXr/Yr/Zr, via LongWorldRotatePoint/
// LongProjectPoint) rendered as garbage. Root cause: AffGrille()'s own
// brick positions are computed relative to StartXCube/Y/Z (GRILLE.CPP's
// AffBrickBlockColon: "Map2Screen(x - StartXCube, ...)"), a *different*
// pair of globals CameraCenter(1) derives from the same source position but
// SetPosCamera itself never touches -- so a real pan needs both, kept in
// sync exactly like CameraCenter(1)'s own StartXCube=VueOffsetX/
// SIZE_BRICK_XZ; SetPosCamera(StartXCube*SIZE_BRICK_XZ, ...) does (case 0
// there, "VueOffsetX = StartXCube*SIZE_BRICK_XZ", is the same "reapply an
// already-brick-aligned position with no new source object" shape this
// wants). The integer division also means the actual camera position this
// resolves to is always brick-aligned (a multiple of 512 on X/Z, 256 on Y),
// same as CameraCenter(1)'s own hero-centered snap.
extern S32 VueOffsetX, VueOffsetY, VueOffsetZ;
int RendererSetInteriorCameraPosition(int x, int y, int z)
{
    StartXCube = x / SIZE_BRICK_XZ;
    StartYCube = y / SIZE_BRICK_Y;
    StartZCube = z / SIZE_BRICK_XZ;
    VueOffsetX = StartXCube * SIZE_BRICK_XZ;
    VueOffsetY = StartYCube * SIZE_BRICK_Y;
    VueOffsetZ = StartZCube * SIZE_BRICK_XZ;
    SetPosCamera(VueOffsetX, VueOffsetY, VueOffsetZ);
    return 1;
}

// Reads back wherever the interior camera actually is right now --
// CameraCenter(1)'s own StartXCube/Y/Z-derived snap position immediately
// after RendererLoadInteriorScene, or the last position
// RendererSetInteriorCameraPosition set otherwise. The C# side needs this
// because there is no other way to know where CameraCenter(1) actually put
// the camera: confirmed live to be nothing like the hero object's own raw
// GetActor(0, ...) position (e.g. one real scene: hero at world (229376, 0,
// 262144) -- an address on the outdoor island's own much larger coordinate
// scheme -- while CameraCenter(1) actually snapped the camera to (0, 0,
// 2048), local to this scene's own 64x512-unit brick grid). Seeding the pan
// scrollbars' initial value from the hero's own position instead of this
// getter was the real bug behind interior panning going to visibly
// corrupted brick noise on the very first scroll: the scrollbar's Value
// ended up set to a six-figure coordinate hundreds of bricks outside this
// scene's own grid, and even one page-sized scroll from there fed AffGrille()
// a camera position its own screen-projection math was never exercised
// against.
extern S32 CameraXr, CameraYr;
int RendererGetInteriorCameraPosition(int *x, int *y, int *z)
{
    if (x) *x = (int)CameraXr;
    if (y) *y = (int)CameraYr;
    if (z) *z = (int)CameraZr;
    return 1;
}

// Draws the currently-loaded interior scene's brick grid into the same Log
// framebuffer lba2_renderer_framebuffer() already exposes for the exterior
// path (AffGrille ultimately calls AffGraph(), the same primitive the
// exterior path's own terrain drawing uses). Clears the whole framebuffer
// directly first, rather than through the generic Cls() macro (BoxCls over
// BoxClipXMin/Min/Max): that macro only clears whatever region the
// dirty-box system (BoxMovingAdd/BoxUpdate) has been told is dirty, which
// this headless single-shot renderer never maintains. (This was added
// defensively and not tested in isolation against Cls().) A real 3D
// exterior frame never needed any explicit clear (sky+terrain always
// covers every pixel); interior bricks can leave gaps.
//
// Actors are drawn on top of the bricks by RendererDrawInteriorActors
// (above). They are not occluded by walls in front of them -- see that
// function's own comment.
extern void *Log;
extern U32 ModeDesiredX, ModeDesiredY;
int RendererRenderInteriorFrame(void)
{
    memset(Log, 0, (size_t)ModeDesiredX * (size_t)ModeDesiredY);
    AffGrille();
    CopyScreen(Log, Screen);
    RendererDrawInteriorActors();
    return 1;
}

// Whole-scene render: the interior view is a fixed 640x480 window onto a much
// larger isometric plane, so to show it all at once the camera is stepped
// over the 64x64 brick grid and every tile is composited into one canvas. A
// brick at (xm,ym,zm) lands at screen ((xm-sx)-(zm-sz))*24+288,
// ((xm-sx)+(zm-sz))*12-(ym-sy)*15+226 for a camera at brick (sx,sy,sz), so a
// tile's canvas origin depends only on (sx,sz); overlapping tiles agree where
// they overlap. Palette index 0 is treated as background.
#define INTERIOR_CANVAS_W 3712
#define INTERIOR_CANVAS_H 2400
static S32 g_interiorActorCanvas[MAX_OBJETS][6];
static bool g_interiorActorCanvasValid[MAX_OBJETS];

int RendererRenderInteriorFull(unsigned char *canvas, int width, int height)
{
    if (!canvas || width != INTERIOR_CANVAS_W || height != INTERIOR_CANVAS_H) return 0;
    memset(canvas, 0, (size_t)width * (size_t)height);
    for (S32 n = 0; n < MAX_OBJETS; n++) g_interiorActorCanvasValid[n] = false;

    static const S32 steps[] = {0, 8, 16, 24, 32, 40, 48, 56, 63};
    const S32 nsteps = (S32)(sizeof(steps) / sizeof(steps[0]));
    for (S32 iz = 0; iz < nsteps; iz++) {
        for (S32 ix = 0; ix < nsteps; ix++) {
            S32 sx = steps[ix], sz = steps[iz];
            RendererSetInteriorCameraPosition(sx * SIZE_BRICK_XZ, 0, sz * SIZE_BRICK_XZ);
            RendererRenderInteriorFrame();

            S32 ox = (sx - sz) * 24 + 1536 - 288 + 300;
            S32 oy = (sx + sz) * 12 + 400;
            const U8 *src = (const U8 *)Log;
            for (S32 y = 0; y < (S32)ModeDesiredY; y++) {
                S32 cy = y + oy;
                if (cy < 0 || cy >= height) continue;
                U8 *dst = canvas + (size_t)cy * (size_t)width;
                const U8 *row = src + (size_t)y * (size_t)ModeDesiredX;
                for (S32 x = 0; x < (S32)ModeDesiredX; x++) {
                    S32 cx = x + ox;
                    if (cx < 0 || cx >= width) continue;
                    if (row[x]) dst[cx] = row[x];
                }
            }

            for (S32 n = 1; n < MAX_OBJETS; n++) {
                const InteriorActorScreen &rec = g_interiorActorScreens[n];
                if (!rec.Valid) continue;
                // Keep the tile that puts the actor nearest the tile centre,
                // so the recorded hit box is never one clipped at a tile edge.
                S32 dist = abs(rec.X - (S32)ModeDesiredX / 2) + abs(rec.Y - (S32)ModeDesiredY / 2);
                if (g_interiorActorCanvasValid[n] && g_interiorActorCanvas[n][5] <= dist) continue;
                g_interiorActorCanvasValid[n] = true;
                g_interiorActorCanvas[n][0] = rec.X + ox;
                g_interiorActorCanvas[n][1] = rec.Y + oy;
                g_interiorActorCanvas[n][2] = (rec.MaxX - rec.MinX) / 2;
                g_interiorActorCanvas[n][3] = (rec.MaxY - rec.MinY) / 2;
                g_interiorActorCanvas[n][4] = 0;
                g_interiorActorCanvas[n][5] = dist;
            }
        }
    }
    return 1;
}

// Canvas position + hit half-size of actor g_actors[index], as of the last
// RendererRenderInteriorFull. Returns 0 if that actor wasn't drawn.
int RendererGetInteriorActorCanvas(int index, int *x, int *y, int *halfW, int *halfH)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    S32 slot = g_actors[(size_t)index].ObjectSlot;
    if (slot < 1 || slot >= MAX_OBJETS || !g_interiorActorCanvasValid[slot]) return 0;
    if (x) *x = g_interiorActorCanvas[slot][0];
    if (y) *y = g_interiorActorCanvas[slot][1];
    if (halfW) *halfW = g_interiorActorCanvas[slot][2];
    if (halfH) *halfH = g_interiorActorCanvas[slot][3];
    return 1;
}

int RendererGetActorCount()
{
    return (int)g_actors.size();
}

int RendererGetActor(int index, int *x, int *y, int *z, int *waypointCount)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    const RendererActor &a = g_actors[(size_t)index];
    if (a.Removed) return 0;
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

extern S32 NewCube;
extern S32 CurrentCubeX, CurrentCubeY;
extern int RendererLoadNeighborCubeData(int x, int y);

// a.GenBody/GenAnim, for an actor scanned straight off a real scene file
// (as opposed to one added through this editor), are still the *generic*
// per-actor numbers from disk -- see RendererScanIslandActors's own
// comment for the full writeup of why, and why that scan itself can't
// safely resolve them (no valid PtrFile3D to resolve *through* at that
// point). By the time this is actually called, though, the object may
// well be live: if this exact actor's own scene is the one currently
// loaded into ListObjet (NewCube == a.SceneIndex) -- which happens simply
// by the camera having panned across its cube, entirely independent of
// this dialog -- LoadScene() has, for that real load, genuinely assigned
// ListObjet[a.ObjectSlot].PtrFile3D (DISKFUNC.CPP only skips that "if
// (!FlagLoadGame)", and the camera's own cube loading always runs with
// FlagLoadGame=FALSE -- see RendererLoadCubeScene). Resolving lazily here,
// through that now-valid live fiche table, is what makes this match what
// the island view already renders instead of misreading the raw generic
// number as if it were already a BODY.HQR/ANIM.HQR index (confirmed by a
// report of exactly that split: a MOSQUIBE.ILE dragon with a correct body
// in the island view and a different, wrong one in its own attributes
// dialog).
//
// If this actor's scene *isn't* the live one -- the common case for
// anything the camera hasn't happened to pan across yet, confirmed by a
// second report for an actor whose location the camera had never visited
// at all -- temporarily loads it via RendererLoadNeighborCubeData, the
// exact same "load a cube's data without touching CurrentCubeX/Y" call
// AffGrilleExtWide's own neighbor-cube loop (EXTFUNC.CPP) already uses
// every single frame at wide zoom levels, proven safe there. Restoring
// the camera's own actual cube (CurrentCubeX/Y) immediately after mirrors
// that same loop's own end-of-frame restore, so nothing about the live
// view changes -- this all happens between frames, invisibly, from the
// caller's perspective. Falls back to the stored (possibly still-generic)
// value if even that fails (e.g. the actor's own scene no longer exists).
extern void NativeDebugLog(const char *fmt, ...);

int RendererGetActorAttributes(int index, int *beta, int *body, int *anim, int *lifePoint, int *armor, int *hitForce, int *move)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    const RendererActor &a = g_actors[(size_t)index];
    S32 resolvedBody = a.GenBody;
    S32 resolvedAnim = a.GenAnim;
    const bool useCache = !a.GenIsRaw && a.ResolvedBody >= 0;
    if (useCache) {
        resolvedBody = a.ResolvedBody;
        if (a.ResolvedAnim >= 0) resolvedAnim = a.ResolvedAnim;
    }

    bool loadedTemporarily = false;
    if (!a.GenIsRaw && !useCache && NewCube != a.SceneIndex && a.CubeX >= 0 && a.CubeY >= 0)
        loadedTemporarily = RendererLoadNeighborCubeData(a.CubeX, a.CubeY) != 0;

    if (!a.GenIsRaw && !useCache && (NewCube == a.SceneIndex || loadedTemporarily) && a.ObjectSlot >= 1 && a.ObjectSlot < NbObjets && ListObjet[a.ObjectSlot].PtrFile3D) {
        S32 b = SearchBody((U8)a.GenBody, a.ObjectSlot);
        if (b >= 0) resolvedBody = b;
        S32 an = SearchAnim(a.GenAnim, a.ObjectSlot);
        if (an >= 0) resolvedAnim = an;
        if (resolvedBody != a.GenBody || resolvedAnim != a.GenAnim)
            NativeDebugLog("RendererGetActorAttributes: index=%d slot=%d resolved generic body=%d anim=%d -> raw body=%d anim=%d%s",
                index, (int)a.ObjectSlot, (int)a.GenBody, (int)a.GenAnim, (int)resolvedBody, (int)resolvedAnim,
                loadedTemporarily ? " (via temporary cube load)" : "");
    }

    if (loadedTemporarily) RendererLoadNeighborCubeData(CurrentCubeX, CurrentCubeY);
    if (beta) *beta = a.Beta;
    if (body) *body = resolvedBody;
    if (anim) *anim = resolvedAnim;
    if (lifePoint) *lifePoint = a.LifePoint;
    if (armor) *armor = a.Armure;
    if (hitForce) *hitForce = a.HitForce;
    if (move) *move = a.Move;
    return 1;
}

// Copies a NUL-terminated decoded script (life, then track, separated by a
// blank line) into buffer, truncating to bufferSize-1 bytes if needed.
// Returns the number of bytes the full text needs (including the NUL),
// mirroring the usual "call once to size, once to fill" C string convention
// -- callers that only care whether it fit can just compare the return
// value against bufferSize.
int RendererGetActorScript(int index, char *buffer, int bufferSize)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    const RendererActor &a = g_actors[(size_t)index];
    std::string full = "-- Life script --\n" + a.LifeScript + "\n-- Track script --\n" + a.TrackScript;
    int needed = (int)full.size() + 1;
    if (buffer && bufferSize > 0) {
        int toCopy = std::min(bufferSize - 1, (int)full.size());
        std::memcpy(buffer, full.data(), (size_t)toCopy);
        buffer[toCopy] = '\0';
    }
    return needed;
}

// ---------------------------------------------------------------------
// Session editing: attribute/position/script edits and new actors, kept in
// g_pendingEdits (keyed by scene index, see its own struct comments above)
// until either the scene reloads (ReapplyPendingEditsToListObjet patches
// them straight back into the freshly-loaded ListObjet, so they don't
// appear to "revert" while panning) or the editor explicitly saves them to
// SCENE.HQR (a separate, not-yet-written piece -- these functions only ever
// touch in-memory state; nothing here writes to disk).
//
// Deliberately restricted to real scene actors (ObjectSlot/NewActor, never
// slot 0): the hero object has special-cased behavior throughout the engine
// (InitBody's own ChoiceHeroBody() override, save-state fields elsewhere)
// that a generic "edit any actor" tool has no business overriding.
// ---------------------------------------------------------------------

extern void InitObject(U8 numobj);
extern void ObjectInitBody(T_OBJ_3D *obj, void *body);
extern void ObjectInitAnim(T_OBJ_3D *obj, void *anim);
extern void *GivePtrBody(S32 index);
extern void *GivePtrAnim(S32 index);
extern S32 NewCube;

// GenBody/GenAnim, here and on PendingAttributeEdit/PendingNewActor, hold a
// raw BODY.HQR/ANIM.HQR archive index -- what the editor's own body/anim
// pickers list (built directly from those archives via HQD description
// files, see ActorAttributesWindow.xaml.cs) -- not the "generic" per-object
// index InitBody()/InitAnim() (OBJECT.CPP) resolve through SearchBody()/
// SearchAnim()'s per-object PtrFile3D conversion table (FICHE.CPP). Those
// generic tables are per-object and usually tiny (a handful of entries an
// actor's own script actually uses), so a generic index sized off the full
// archive almost never resolves to the intended body/anim -- often not at
// all, silently falling back to GEN_BODY_NORMAL/GEN_ANIM_RIEN. Calling
// ObjectInitBody()/ObjectInitAnim() directly with the archive index (the
// same primitives InitBody/InitAnim call internally, after their own
// generic-to-archive resolution) sidesteps that mismatch entirely. See
// EXTFUNC.CPP's AffichageBodyPreview for the same fix and fuller writeup;
// this is the same bug for real actors instead of the preview's scratch
// object.
// True if animPtr's own group count fits the fixed-size interpolation
// buffer -- duplicated from EXTFUNC.CPP's AffichageBodyPreview, see its own
// comment for the full writeup (this used to compare against the body's
// own group count, which was wrong and rejected ordinary pairings).
static bool AnimFitsBody(const void *bodyPtr, const void *animPtr)
{
    (void)bodyPtr;
    U16 animGroups = ((const U16 *)animPtr)[1];
    return animGroups <= 31;
}

static void ApplyRawBodyAnim(T_OBJET *ptrobj, S32 rawBody, S32 rawAnim)
{
    void *bodyPtr = GivePtrBody(rawBody);
    void *animPtr = GivePtrAnim(rawAnim);
    if (!bodyPtr || !animPtr || !AnimFitsBody(bodyPtr, animPtr)) return;
    ptrobj->Obj.Body.Num = -1; // force ObjectInitBody to commit immediately -- see its own Body.Num==-1 check
    ObjectInitBody(&ptrobj->Obj, (void *)(uintptr_t)(U32)rawBody);
    ptrobj->Obj.Anim.Num = -1; // force ObjectInitAnim to (re)init even if rawAnim happens to be unchanged
    ptrobj->FlagAnim = 0; // matches StartInitObj's own reset before its InitAnim() call
    ObjectInitAnim(&ptrobj->Obj, (void *)(uintptr_t)(U32)rawAnim);
}

// Defined in EXTFUNC.CPP (the shared engine core) -- see
// g_placeholderBodySlot's own comment there for why this pushes into an
// engine-owned array rather than the engine calling back into this
// (editor-only, 3DEXT) layer to ask.
extern void SetPlaceholderBodySlot(S32 slot, int isPlaceholder);
extern void ClearAllPlaceholderBodySlots(void);
extern void NativeDebugLog(const char *fmt, ...);

static void ReapplyPendingEditsToListObjet(S32 sceneIndex)
{
    // This whole project builds as C++98 (CMakeLists.txt's own
    // CMAKE_CXX_STANDARD 98) -- no auto, no range-based for, no std::move,
    // matching the style of every other function in this file.
    std::map<S32, ScenePendingEdits>::iterator it = g_pendingEdits.find(sceneIndex);
    if (it == g_pendingEdits.end()) return;
    ScenePendingEdits &edits = it->second;

    for (std::map<S32, PendingAttributeEdit>::iterator ait = edits.AttributeEdits.begin(); ait != edits.AttributeEdits.end(); ++ait) {
        S32 slot = ait->first;
        if (slot < 1 || slot >= NbObjets) continue; // slot 0 excluded; new actors not yet placed handled below
        const PendingAttributeEdit &e = ait->second;
        T_OBJET *ptrobj = &ListObjet[slot];
        if (e.HasPosition) {
            ptrobj->Obj.X = e.X; ptrobj->Obj.Y = e.Y; ptrobj->Obj.Z = e.Z;
            ptrobj->OldPosX = e.X; ptrobj->OldPosY = e.Y; ptrobj->OldPosZ = e.Z;
        }
        if (e.HasAttributes) {
            ptrobj->Obj.Beta = e.Beta;
            ptrobj->LifePoint = e.LifePoint;
            ptrobj->Armure = e.Armure;
            ptrobj->HitForce = e.HitForce;
            ptrobj->Move = e.Move;
            if (ptrobj->GenBody != e.GenBody || ptrobj->GenAnim != e.GenAnim) {
                ptrobj->GenBody = e.GenBody;
                ptrobj->GenAnim = e.GenAnim;
                ApplyRawBodyAnim(ptrobj, e.GenBody, e.GenAnim);
            }
        }
        if (e.HasFlags) ptrobj->Flags = e.Flags;
    }

    for (std::map<S32, PendingScriptEdit>::iterator sit = edits.ScriptEdits.begin(); sit != edits.ScriptEdits.end(); ++sit) {
        S32 slot = sit->first;
        if (slot < 1 || slot >= NbObjets) continue;
        // Points straight at the pending edit's own owned buffer rather than
        // copying -- safe as long as this function (which re-derives every
        // pointer fresh) is the only thing ever handing these out, and it's
        // re-run after every mutation of g_pendingEdits (see the Set*/Add*
        // functions below), so a stale pointer from a resized vector never
        // survives past the mutation that could have invalidated it.
        ListObjet[slot].PtrTrack = sit->second.TrackBytes.empty() ? NULL : &sit->second.TrackBytes[0];
        ListObjet[slot].PtrLife = sit->second.LifeBytes.empty() ? NULL : &sit->second.LifeBytes[0];
    }

    // A real object's PtrFile3D/IndexFile3D (the per-object "generic body
    // name" conversion table SearchBody()/SearchAnim() walk -- FICHE.CPP) is
    // set by LoadScene()'s own per-object loop (DISKFUNC.CPP: "ptrobj->
    // PtrFile3D = LoadFile3D(indexfile3d);") before a real actor's script
    // ever runs. InitObject() only zeroes a slot, so a synthetic slot never
    // gets this on its own -- borrowed here (not for the *initial*
    // body/anim, which ApplyRawBodyAnim below sets directly by archive
    // index, but) so that a NULL PtrFile3D doesn't crash SearchBody()/
    // SearchAnim() the first time this new actor's own life script runs a
    // BODY/ANIM opcode during ordinary gameplay simulation. Slot 1, if this
    // scene already has any real actor, has already been through that real
    // load and is safe to copy from; LoadFile3D(0) (BufferFile3D is a
    // global boot-time resource, loaded once regardless of which scene/
    // actors exist) is the fallback for a scene with none.
    U8 *fallbackPtrFile3D = NbObjets > 1 ? ListObjet[1].PtrFile3D : (U8 *)LoadFile3D(0);
    S16 fallbackIndexFile3D = NbObjets > 1 ? ListObjet[1].IndexFile3D : (S16)0;

    for (size_t i = 0; i < edits.NewActors.size(); i++) {
        if (NbObjets >= MAX_OBJETS) break; // full; RendererAddActor already refuses beyond this
        PendingNewActor &newActor = edits.NewActors[i];
        S32 slot = NbObjets++;
        InitObject((U8)slot);
        T_OBJET *ptrobj = &ListObjet[slot];
        ptrobj->PtrFile3D = fallbackPtrFile3D;
        ptrobj->IndexFile3D = fallbackIndexFile3D;
        ptrobj->Obj.X = newActor.X; ptrobj->Obj.Y = newActor.Y; ptrobj->Obj.Z = newActor.Z;
        ptrobj->OldPosX = newActor.X; ptrobj->OldPosY = newActor.Y; ptrobj->OldPosZ = newActor.Z;
        ptrobj->Obj.Beta = newActor.Beta;
        ptrobj->GenBody = newActor.GenBody;
        ptrobj->GenAnim = newActor.GenAnim;
        ptrobj->LifePoint = newActor.LifePoint;
        ptrobj->Armure = newActor.Armure;
        ptrobj->HitForce = newActor.HitForce;
        ptrobj->Move = newActor.Move;
        ptrobj->Flags = newActor.Flags;
        ApplyRawBodyAnim(ptrobj, newActor.GenBody, newActor.GenAnim);
        ptrobj->PtrTrack = newActor.TrackBytes.empty() ? NULL : &newActor.TrackBytes[0];
        ptrobj->PtrLife = newActor.LifeBytes.empty() ? NULL : &newActor.LifeBytes[0];
        if (newActor.FlatIndex >= 0 && newActor.FlatIndex < (S32)g_actors.size())
            g_actors[(size_t)newActor.FlatIndex].ObjectSlot = slot;
    }

    // Syncs the engine's own placeholder-slot flags (EXTFUNC.CPP's
    // g_placeholderBodySlot) to match g_actors for this scene, covering
    // every actor here -- not just the NewActors placed just above -- since
    // ObjectSlot can shift across reloads if the actor count in this scene
    // changes (e.g. another actor added or removed), which would otherwise
    // leave a stale slot number flagged from a previous reload's own
    // numbering. Clearing every slot in this scene first, then re-marking
    // only the ones g_actors still says are placeholders, keeps this
    // correct regardless of how slot numbers moved around.
    ClearAllPlaceholderBodySlots();
    for (size_t gi = 0; gi < g_actors.size(); gi++) {
        const RendererActor &ga = g_actors[gi];
        if (ga.SceneIndex == sceneIndex && ga.ObjectSlot >= 1 && ga.ObjectSlot < NbObjets && ga.IsPlaceholderBody) {
            SetPlaceholderBodySlot(ga.ObjectSlot, 1);
            NativeDebugLog("ReapplyPendingEditsToListObjet: scene=%d slot=%d flagged placeholder-black", sceneIndex, (int)ga.ObjectSlot);
        }
    }
}

// Re-applies pending edits for whichever scene is currently loaded into
// ListObjet, if any -- for immediate visual feedback right after an edit,
// without waiting for the next cube transition to trigger
// RendererLoadCubeScene's own reapply.
static void ReapplyIfCurrentlyLoaded(S32 sceneIndex)
{
    if (NewCube == sceneIndex) {
        ReapplyPendingEditsToListObjet(sceneIndex);
        CacheResolvedBodyAnimForScene(sceneIndex);
    }
}

int RendererSetActorAttributes(int index, int beta, int body, int anim, int lifePoint, int armor, int hitForce, int move)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    RendererActor &a = g_actors[(size_t)index];
    if (a.ObjectSlot == 0) return 0; // hero excluded, see file header comment
    NativeDebugLog("RendererSetActorAttributes: index=%d slot=%d body=%d anim=%d beta=%d", index, (int)a.ObjectSlot, body, anim, beta);
    a.Beta = (S16)beta;
    a.GenBody = (U8)body;
    a.GenAnim = (U16)anim;
    a.LifePoint = (S16)lifePoint;
    a.Armure = (U8)armor;
    a.HitForce = (U8)hitForce;
    a.Move = (U8)move;
    a.IsPlaceholderBody = false; // see the field's own comment -- an explicit Apply means the user has reviewed this actor
    a.GenIsRaw = true;

    PendingAttributeEdit &edit = g_pendingEdits[a.SceneIndex].AttributeEdits[a.ObjectSlot];
    edit.HasAttributes = true;
    edit.Beta = a.Beta; edit.GenBody = a.GenBody; edit.GenAnim = a.GenAnim;
    edit.LifePoint = a.LifePoint; edit.Armure = a.Armure; edit.HitForce = a.HitForce; edit.Move = a.Move;
    g_pendingEdits[a.SceneIndex].Dirty = true;

    ReapplyIfCurrentlyLoaded(a.SceneIndex);
    return 1;
}

int RendererSetActorPosition(int index, int worldX, int worldY, int worldZ)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    RendererActor &a = g_actors[(size_t)index];
    if (a.ObjectSlot == 0) return 0;
    a.X = worldX; a.Y = worldY; a.Z = worldZ;

    PendingAttributeEdit &edit = g_pendingEdits[a.SceneIndex].AttributeEdits[a.ObjectSlot];
    edit.HasPosition = true;
    edit.X = worldX - a.CubeX * ACTORS_WORLD_CUBE_SIZE;
    edit.Y = worldY;
    edit.Z = worldZ - a.CubeY * ACTORS_WORLD_CUBE_SIZE;
    g_pendingEdits[a.SceneIndex].Dirty = true;

    ReapplyIfCurrentlyLoaded(a.SceneIndex);
    return 1;
}

// Adds a new actor to the scene covering (worldX, worldZ)'s cube, with the
// given starting attributes and a trivial one-opcode (END) life and track
// script. Returns the new actor's g_actors index (usable with every other
// Get/Set actor API right away), or -1 if that world position isn't on any
// loaded scene, or the target scene is already at MAX_OBJETS.
int RendererAddActor(int worldX, int worldY, int worldZ, int beta, int body, int anim, int lifePoint, int armor, int hitForce, int move)
{
    S32 cubeX = worldX / ACTORS_WORLD_CUBE_SIZE;
    S32 cubeY = worldZ / ACTORS_WORLD_CUBE_SIZE;
    if (cubeX < 0 || cubeX >= 16 || cubeY < 0 || cubeY >= 16) return -1;
    S32 sceneIndex = g_sceneForCube[cubeY][cubeX];
    if (sceneIndex < 0) return -1;

    S32 existingSlots = 1; // hero always counts as one
    std::map<S32, ScenePendingEdits>::iterator pendingIt = g_pendingEdits.find(sceneIndex);
    S32 pendingNewCount = pendingIt != g_pendingEdits.end() ? (S32)pendingIt->second.NewActors.size() : 0;
    for (size_t i = 0; i < g_actors.size(); i++) if (g_actors[i].SceneIndex == sceneIndex) existingSlots++;
    if (existingSlots + pendingNewCount >= MAX_OBJETS) return -1;

    PendingNewActor newActor;
    newActor.X = worldX - cubeX * ACTORS_WORLD_CUBE_SIZE;
    newActor.Y = worldY;
    newActor.Z = worldZ - cubeY * ACTORS_WORLD_CUBE_SIZE;
    newActor.Beta = (S16)beta;
    newActor.GenBody = (U8)body;
    newActor.GenAnim = (U16)anim;
    newActor.LifePoint = (S16)lifePoint;
    newActor.Armure = (U8)armor;
    newActor.HitForce = (U8)hitForce;
    newActor.Move = (U8)move;
    newActor.LifeBytes.push_back((U8)LM_END);
    newActor.TrackBytes.push_back((U8)TM_END);

    RendererActor actor;
    actor.X = worldX; actor.Y = worldY; actor.Z = worldZ;
    actor.WaypointStart = 0;
    actor.WaypointCount = 0;
    actor.Beta = newActor.Beta;
    actor.GenBody = newActor.GenBody;
    actor.GenAnim = newActor.GenAnim;
    actor.LifePoint = newActor.LifePoint;
    actor.Armure = newActor.Armure;
    actor.HitForce = newActor.HitForce;
    actor.Move = newActor.Move;
    actor.Flags = newActor.Flags;
    actor.LifeScript = "END\n";
    actor.TrackScript = "END\n";
    actor.SceneIndex = sceneIndex;
    actor.ObjectSlot = -1; // filled in once ReapplyPendingEditsToListObjet places it
    actor.CubeX = cubeX;
    actor.CubeY = cubeY;
    actor.IsPlaceholderBody = true; // see the field's own comment -- cleared once the user applies real attributes
    actor.GenIsRaw = true;
    g_actors.push_back(actor);
    S32 flatIndex = (S32)g_actors.size() - 1;

    ScenePendingEdits &edits = g_pendingEdits[sceneIndex];
    newActor.FlatIndex = flatIndex;
    edits.NewActors.push_back(newActor);
    edits.Dirty = true;

    NativeDebugLog("RendererAddActor: scene=%d flatIndex=%d world=(%d,%d,%d) body=%d anim=%d", sceneIndex, flatIndex, worldX, worldY, worldZ, body, anim);
    ReapplyIfCurrentlyLoaded(sceneIndex);
    return flatIndex;
}

// True only for an actor RendererAddActor() created that hasn't had
// RendererSetActorAttributes called on it since -- the editor uses this to
// decide whether closing that actor's attributes window (without ever
// clicking Apply) should undo the add via RendererRemoveActor below, rather
// than leaving an attribute-less actor sitting in the world. Never true for
// anything read from a real scene file.
int RendererIsActorPlaceholder(int index)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    const RendererActor &a = g_actors[(size_t)index];
    return (!a.Removed && a.IsPlaceholderBody) ? 1 : 0;
}

// Undoes RendererAddActor() for an actor the user never actually kept --
// refuses (returns 0) for anything else, including an actor that started as
// a placeholder but has since had real attributes applied, since removing
// an actor a user has already set up would silently destroy their work.
//
// This never erases from g_actors itself: every other actor's index is that
// vector's own position, and shifting it after a removal would invalidate
// every other actor index the editor (and every open window keyed by one)
// is currently holding. Marking Removed instead, which RendererGetActor()
// checks, makes the actor invisible to the editor's own enumeration -- see
// the field's own comment -- while every later index stays exactly where it
// was.
//
// Dropping this actor's own PendingNewActor out of edits.NewActors stops
// ReapplyPendingEditsToListObjet from placing it into ListObjet the next
// time this scene's cube actually reloads from disk (which, with the
// interactive view's own wide-radius render path always active -- see
// MainWindow's own wideRadius comment -- happens on close to every frame
// while this cube is on screen); that reload is also what actually erases
// an already-placed slot, since nothing here can retroactively un-place a
// slot in an already-live ListObjet without also renumbering every other
// object's own slot mid-frame.
int RendererRemoveActor(int index)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    RendererActor &a = g_actors[(size_t)index];
    if (a.Removed) return 1; // already gone; not an error to ask twice
    if (!a.IsPlaceholderBody) return 0;

    a.Removed = true;
    std::map<S32, ScenePendingEdits>::iterator it = g_pendingEdits.find(a.SceneIndex);
    if (it != g_pendingEdits.end()) {
        std::vector<PendingNewActor> &list = it->second.NewActors;
        for (size_t i = 0; i < list.size(); i++) {
            if (list[i].FlatIndex == index) { list.erase(list.begin() + (long)i); break; }
        }
        it->second.Dirty = true;
    }

    NativeDebugLog("RendererRemoveActor: index=%d scene=%d", index, (int)a.SceneIndex);
    ReapplyIfCurrentlyLoaded(a.SceneIndex);
    return 1;
}

// Full designer-authored T_OBJET.Flags word (COMMON.H) -- collision/physics/
// visibility toggles like OBJ_FALLABLE (gravity) and CHECK_CODE_JEU (reacts
// to water/lava/gas/electric/escalator bricks it stands on), not just the
// handful of attributes RendererGetActorAttributes already covers. A
// separate call rather than extending that one's signature, since this
// project's other native APIs never break existing out-param lists.
int RendererGetActorFlags(int index, unsigned int *flags)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    if (flags) *flags = g_actors[(size_t)index].Flags;
    return 1;
}

int RendererSetActorFlags(int index, unsigned int flags)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    RendererActor &a = g_actors[(size_t)index];
    if (a.ObjectSlot == 0) return 0; // hero excluded, see file header comment
    a.Flags = flags;

    PendingAttributeEdit &edit = g_pendingEdits[a.SceneIndex].AttributeEdits[a.ObjectSlot];
    edit.HasFlags = true;
    edit.Flags = flags;
    g_pendingEdits[a.SceneIndex].Dirty = true;

    ReapplyIfCurrentlyLoaded(a.SceneIndex);
    return 1;
}

// Screen-space hit-testing helper: an actor's real rendered body size varies
// hugely (a rat vs. a building-sized boss) and with camera distance, so a
// fixed-size click target is either too small for big/close bodies or
// needlessly huge for small/far ones. XMin/XMax/YMin/YMax/ZMin/ZMax
// (T_OBJET, set by InitBody/InitBodyZV -- OBJECT.CPP) are the actor's own
// local bounding box; the caller projects target position +/- these corners
// through lba2_renderer_project_point() to get a real screen-space box.
// Only available for an actor whose scene is the one *currently* loaded
// into ListObjet (matching what's actually on screen right now) -- the
// scan-time g_actors snapshot never runs InitBody, so bounds would be
// meaningless zeros for anything else. Returns 0 (not 1) for a NO_BODY
// actor (Body.Num < 0) or one whose scene isn't currently loaded, so
// callers know to fall back to a fixed-size target instead of drawing a
// degenerate zero-size hit box.
int RendererGetActorBounds(int index, int *xMin, int *xMax, int *yMin, int *yMax, int *zMin, int *zMax)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    const RendererActor &a = g_actors[(size_t)index];
    if (a.SceneIndex != NewCube || a.ObjectSlot < 1 || a.ObjectSlot >= NbObjets) {
        // Not the live scene: fall back to the snapshot taken when it was
        // last loaded (see CacheResolvedBodyAnimForScene).
        if (!a.BoundsValid) return 0;
        if (xMin) *xMin = a.BoundsX[0]; if (xMax) *xMax = a.BoundsX[1];
        if (yMin) *yMin = a.BoundsY[0]; if (yMax) *yMax = a.BoundsY[1];
        if (zMin) *zMin = a.BoundsZ[0]; if (zMax) *zMax = a.BoundsZ[1];
        return 1;
    }
    const T_OBJET &o = ListObjet[a.ObjectSlot];
    if (o.Obj.Body.Num < 0) return 0;
    if (xMin) *xMin = o.XMin; if (xMax) *xMax = o.XMax;
    if (yMin) *yMin = o.YMin; if (yMax) *yMax = o.YMax;
    if (zMin) *zMin = o.ZMin; if (zMax) *zMax = o.ZMax;
    return 1;
}

// Walks ListObjet[slot].PtrFile3D exactly the way FICHE.CPP's own
// SearchAnim()/SearchBody() do -- same command byte, same length-prefixed
// skip arithmetic for every command type -- except instead of stopping at
// the first F_ANIM matching one requested generic id, it records every
// F_ANIM entry's hqranim value and keeps walking to the end (the 255
// sentinel). The length-prefixed skip (a size byte immediately following
// each entry's own generic-id field, counted from that size byte's own
// position to the next command) is what makes this safe to walk generically
// without understanding each command's variable-length action-data payload:
// it's the exact same arithmetic SearchAnim's own "next anim"/SearchBody's
// own "next anim" mismatch branches already use in shipped, working code.
static int WalkFile3DNativeAnims(const U8 *ptrc, int *outAnims, int maxCount)
{
    int count = 0;
    U8 command;
    while ((command = *ptrc++) != 255) {
        if (command == F_ANIM) {
            ptrc += 2; // 16-bit generic anim id -- not needed here
            U8 size = *ptrc;
            S16 hqranim = *(const S16 *)(ptrc + 1);
            if (hqranim >= 0) {
                if (count < maxCount) outAnims[count] = hqranim;
                count++;
            }
            ptrc += size;
        } else if (command == F_BODY) {
            ptrc++; // 8-bit generic body id
            ptrc += *ptrc;
        } else {
            ptrc++; // FICHE.CPP's own default case: every other command type
            ptrc += *ptrc; // also uses an 8-bit generic id + length-prefixed skip
        }
    }
    return count;
}

int RendererGetActorNativeAnims(int index, int *outAnims, int maxCount)
{
    if (index < 0 || index >= (int)g_actors.size()) return -1;
    RendererActor &a = g_actors[(size_t)index];
    // Same "is this actor's scene actually the one live in ListObjet right
    // now" guard as RendererGetActorBounds -- PtrFile3D is only populated by
    // LoadScene() for the currently-loaded scene (see RendererLoadCubeActors's
    // own comment on FlagLoadGame); the scan-time g_actors snapshot never
    // runs that load, so anything else has no valid table to walk. If it
    // isn't live yet, temporarily loads it (same RendererLoadNeighborCubeData
    // trick as RendererGetActorAttributes, see its own comment for the full
    // reasoning) rather than only relying on CachedNativeAnims -- which
    // still exists as a last-resort fallback below for the rare case that
    // temporary load itself fails.
    bool loadedTemporarily = false;
    if (a.SceneIndex != NewCube && a.CachedNativeAnims.empty() && a.CubeX >= 0 && a.CubeY >= 0)
        loadedTemporarily = RendererLoadNeighborCubeData(a.CubeX, a.CubeY) != 0;

    if ((a.SceneIndex == NewCube || loadedTemporarily) && a.ObjectSlot >= 1 && a.ObjectSlot < NbObjets && ListObjet[a.ObjectSlot].PtrFile3D) {
        int count = WalkFile3DNativeAnims(ListObjet[a.ObjectSlot].PtrFile3D, outAnims, maxCount);
        if (count > 0) {
            int cacheCount = count > 128 ? 128 : count;
            a.CachedNativeAnims.assign(outAnims, outAnims + cacheCount);
        }
        if (loadedTemporarily) RendererLoadNeighborCubeData(CurrentCubeX, CurrentCubeY);
        return count;
    }
    if (loadedTemporarily) RendererLoadNeighborCubeData(CurrentCubeX, CurrentCubeY);

    if (!a.CachedNativeAnims.empty()) {
        int count = (int)a.CachedNativeAnims.size();
        int copyCount = count < maxCount ? count : maxCount;
        for (int i = 0; i < copyCount; i++) outAnims[i] = a.CachedNativeAnims[(size_t)i];
        return count;
    }
    return -1;
}
