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
    std::string LifeScript;
    std::string TrackScript;
};

static std::vector<RendererActor> g_actors;
static std::vector<S32> g_waypointX;
static std::vector<S32> g_waypointY;
static std::vector<S32> g_waypointZ;

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
static void DecodeLifeScript(T_OBJET *ptrobj, std::string &out)
{
    if (!ptrobj->PtrLife) { out = "(no life script)\n"; return; }
    U8 *base = ptrobj->PtrLife;
    const S32 maxBytes = 16384;
    S32 offset = 0;
    char line[160];

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

        case LM_IF: ok = DecodeLifeCondition(base, &offset, maxBytes, "IF", out); break;
        case LM_AND_IF: ok = DecodeLifeCondition(base, &offset, maxBytes, "AND_IF", out); break;
        case LM_OR_IF: ok = DecodeLifeCondition(base, &offset, maxBytes, "OR_IF", out); break;
        case LM_SWIF: ok = DecodeLifeCondition(base, &offset, maxBytes, "SWIF", out); break;
        case LM_SNIF: ok = DecodeLifeCondition(base, &offset, maxBytes, "SNIF", out); break;
        case LM_ONEIF: ok = DecodeLifeCondition(base, &offset, maxBytes, "ONEIF", out); break;
        case LM_NEVERIF: ok = DecodeLifeCondition(base, &offset, maxBytes, "NEVERIF", out); break;

        case LM_OFFSET:
        case LM_ELSE:
            if (offset + 1 >= maxBytes) { ok = false; break; }
            {
                S16 target = *(S16 *)(base + offset); offset += 2;
                std::snprintf(line, sizeof(line), "%s -> goto %d\n", op == LM_OFFSET ? "OFFSET" : "ELSE", (int)target);
                out += line;
            }
            break;

        // One-byte operand (usually an object number or a small constant).
        case LM_BODY: case LM_FALLABLE: case LM_SET_DIR: case LM_CAM_FOLLOW:
        case LM_COMPORTEMENT_HERO: case LM_COMPORTEMENT: case LM_KILL_OBJ:
        case LM_FOUND_OBJECT: case LM_GIVE_BONUS: case LM_CHANGE_CUBE:
        case LM_OBJ_COL: case LM_BRICK_COL: case LM_INVISIBLE: case LM_POS_POINT:
        case LM_SET_MAGIC_LEVEL: case LM_SUB_MAGIC_POINT: case LM_INC_CLOVER_BOX:
        case LM_SET_USED_INVENTORY: case LM_SET_HOLO_POS: case LM_CLR_HOLO_POS:
        case LM_ADD_FUEL: case LM_SUB_FUEL:
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

        // Two-byte operand: a single S16 value.
        case LM_ANIM: case LM_MESSAGE: case LM_SET_TRACK: case LM_SET_COMPORTEMENT:
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
            actor.Beta = ptrobj->Obj.Beta;
            actor.GenBody = ptrobj->GenBody;
            actor.GenAnim = ptrobj->GenAnim;
            actor.LifePoint = ptrobj->LifePoint;
            actor.Armure = ptrobj->Armure;
            actor.HitForce = ptrobj->HitForce;
            actor.Move = ptrobj->Move;
            actor.WaypointStart = (S32)g_waypointX.size();
            ExtractTrackWaypoints(ptrobj, baseX, baseZ);
            actor.WaypointCount = (S32)g_waypointX.size() - actor.WaypointStart;
            DecodeLifeScript(ptrobj, actor.LifeScript);
            DecodeTrackScript(ptrobj, actor.TrackScript);
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

int RendererGetActorAttributes(int index, int *beta, int *body, int *anim, int *lifePoint, int *armor, int *hitForce, int *move)
{
    if (index < 0 || index >= (int)g_actors.size()) return 0;
    const RendererActor &a = g_actors[(size_t)index];
    if (beta) *beta = a.Beta;
    if (body) *body = a.GenBody;
    if (anim) *anim = a.GenAnim;
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
