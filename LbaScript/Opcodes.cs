namespace LBA2LevelEditor.LbaScript;

// Authoritative opcode/operand tables for LBA2's two bytecode languages, the
// life script (LM_* actions, LF_* conditions, LT_* comparisons; interpreted by
// GERELIFE.CPP's DoLife/DoFuncLife/DoTest) and the track script (TM_*;
// GERETRAK.CPP's DoTrack). Every row below was transcribed from the
// interpreter's own `case` bodies -- an operand's size is however many bytes
// that case consumes through PtrPrg / ptrtrack -- not from the older
// RENDERER_ACTORS.CPP disassembler, which is a partial one-way decoder (it
// e.g. treats LM_INC_CLOVER_BOX as taking an operand byte; DoLife's case
// takes none).
//
// The interpreter's switch statements have no `default:`, so any opcode id
// that has no `case` (NOP, ENDIF, DEFAULT, END_SWITCH, REM, SPY, DEBUG...)
// consumes zero operand bytes and does nothing. They are listed here with an
// empty argument list for exactly that reason.

internal enum ArgType : byte { U8, S8, U16, S16, U32, Jump, CStr }

// What an operand refers to. Only used to give the C-style text symbolic
// names for things that are really byte offsets or cross-script indices.
internal enum ArgRole : byte
{
    Plain,
    Obj,            // actor index in the same scene
    Point,          // track waypoint index (scene's ListBrickTrack)
    TrackOffset,    // byte offset into a track script (own, or Obj's for *_OBJ)
    LifeOffset,     // byte offset of a COMPORTEMENT block in a life script
    Hidden,         // runtime scratch byte(s) stored inside the bytecode
}

internal readonly record struct ArgDef(string Name, ArgType Type, ArgRole Role = ArgRole.Plain);

// Dir is SET_DIR / SET_DIR_OBJ: after the move-mode byte, GERELIFE.CPP's
// AdjustDirObject() reads one *more* operand byte for some modes (see
// Opcodes.MoveTakesParam), so the instruction length depends on its value.
internal enum LifeForm : byte { Plain, Cond, Switch, Case, Dir }

internal sealed record LifeOpDef(byte Id, string Name, LifeForm Form, ArgDef[] Args);

internal sealed record TrackOpDef(byte Id, string Name, ArgDef[] Args);

// One LF_* condition: its opcode id, how many operand bytes DoFuncLife reads
// after it, and how wide/signed the comparison value that follows the LT_*
// test byte is (TypeAnswer: RET_S8 signed byte, RET_U8 unsigned byte,
// RET_S16 two-byte signed).
internal enum ValueKind : byte { S8, U8, S16 }

internal sealed record CondDef(byte Id, string Name, string? OperandName, ValueKind Value);

internal static class Opcodes
{
    private static ArgDef U8(string n, ArgRole r = ArgRole.Plain) => new(n, ArgType.U8, r);
    private static ArgDef S8(string n) => new(n, ArgType.S8);
    private static ArgDef U16(string n) => new(n, ArgType.U16);
    private static ArgDef S16(string n, ArgRole r = ArgRole.Plain) => new(n, ArgType.S16, r);
    private static ArgDef Obj(string n = "obj") => new(n, ArgType.U8, ArgRole.Obj);
    private static ArgDef Pt(string n = "point") => new(n, ArgType.U8, ArgRole.Point);
    private static ArgDef Jmp(string n = "target") => new(n, ArgType.Jump);

    private static readonly ArgDef[] None = Array.Empty<ArgDef>();

    // ---------------------------------------------------------------------
    // Life actions (LM_*), COMMON.H lines ~1100-1250.
    // ---------------------------------------------------------------------
    private static readonly LifeOpDef[] lifeDefs =
    {
        new(0, "END", LifeForm.Plain, None),
        new(1, "NOP", LifeForm.Plain, None),
        new(2, "SNIF", LifeForm.Cond, None),
        new(3, "OFFSET", LifeForm.Plain, new[] { Jmp() }),
        new(4, "NEVERIF", LifeForm.Cond, None),
        new(10, "PALETTE", LifeForm.Plain, new[] { U8("palette") }),
        new(11, "RETURN", LifeForm.Plain, None),
        new(12, "IF", LifeForm.Cond, None),
        new(13, "SWIF", LifeForm.Cond, None),
        new(14, "ONEIF", LifeForm.Cond, None),
        new(15, "ELSE", LifeForm.Plain, new[] { Jmp() }),
        new(16, "ENDIF", LifeForm.Plain, None),
        new(17, "BODY", LifeForm.Plain, new[] { U8("body") }),
        new(18, "BODY_OBJ", LifeForm.Plain, new[] { Obj(), U8("body") }),
        new(19, "ANIM", LifeForm.Plain, new[] { U16("anim") }),
        new(20, "ANIM_OBJ", LifeForm.Plain, new[] { Obj(), U16("anim") }),
        new(21, "SET_CAMERA", LifeForm.Plain, new[] { U8("zone"), U8("on") }),
        new(22, "CAMERA_CENTER", LifeForm.Plain, new[] { U8("angle") }),
        new(23, "SET_TRACK", LifeForm.Plain, new[] { S16("track", ArgRole.TrackOffset) }),
        new(24, "SET_TRACK_OBJ", LifeForm.Plain, new[] { Obj(), S16("track", ArgRole.TrackOffset) }),
        new(25, "MESSAGE", LifeForm.Plain, new[] { S16("message") }),
        new(26, "FALLABLE", LifeForm.Plain, new[] { U8("mode") }),
        new(27, "SET_DIR", LifeForm.Dir, new[] { U8("move") }),
        new(28, "SET_DIR_OBJ", LifeForm.Dir, new[] { Obj(), U8("move") }),
        new(29, "CAM_FOLLOW", LifeForm.Plain, new[] { Obj() }),
        new(30, "COMPORTEMENT_HERO", LifeForm.Plain, new[] { U8("comportement") }),
        new(31, "SET_VAR_CUBE", LifeForm.Plain, new[] { U8("var"), U8("value") }),
        new(32, "COMPORTEMENT", LifeForm.Plain, new[] { U8("index") }),
        new(33, "SET_COMPORTEMENT", LifeForm.Plain, new[] { S16("comportement", ArgRole.LifeOffset) }),
        new(34, "SET_COMPORTEMENT_OBJ", LifeForm.Plain, new[] { Obj(), S16("comportement", ArgRole.LifeOffset) }),
        new(35, "END_COMPORTEMENT", LifeForm.Plain, None),
        new(36, "SET_VAR_GAME", LifeForm.Plain, new[] { U8("var"), S16("value") }),
        new(37, "KILL_OBJ", LifeForm.Plain, new[] { Obj() }),
        new(38, "SUICIDE", LifeForm.Plain, None),
        new(39, "USE_ONE_LITTLE_KEY", LifeForm.Plain, None),
        new(40, "GIVE_GOLD_PIECES", LifeForm.Plain, new[] { S16("amount") }),
        new(41, "END_LIFE", LifeForm.Plain, None),
        new(42, "STOP_L_TRACK", LifeForm.Plain, None),
        new(43, "RESTORE_L_TRACK", LifeForm.Plain, None),
        new(44, "MESSAGE_OBJ", LifeForm.Plain, new[] { Obj(), S16("message") }),
        new(45, "INC_CHAPTER", LifeForm.Plain, None),
        new(46, "FOUND_OBJECT", LifeForm.Plain, new[] { U8("item") }),
        new(47, "SET_DOOR_LEFT", LifeForm.Plain, new[] { S16("width") }),
        new(48, "SET_DOOR_RIGHT", LifeForm.Plain, new[] { S16("width") }),
        new(49, "SET_DOOR_UP", LifeForm.Plain, new[] { S16("width") }),
        new(50, "SET_DOOR_DOWN", LifeForm.Plain, new[] { S16("width") }),
        new(51, "GIVE_BONUS", LifeForm.Plain, new[] { U8("flag") }),
        new(52, "CHANGE_CUBE", LifeForm.Plain, new[] { U8("cube") }),
        new(53, "OBJ_COL", LifeForm.Plain, new[] { U8("on") }),
        new(54, "BRICK_COL", LifeForm.Plain, new[] { U8("mode") }),
        new(55, "OR_IF", LifeForm.Cond, None),
        new(56, "INVISIBLE", LifeForm.Plain, new[] { U8("on") }),
        new(57, "SHADOW_OBJ", LifeForm.Plain, new[] { Obj(), U8("on") }),
        new(58, "POS_POINT", LifeForm.Plain, new[] { Pt() }),
        new(59, "SET_MAGIC_LEVEL", LifeForm.Plain, new[] { U8("level") }),
        new(60, "SUB_MAGIC_POINT", LifeForm.Plain, new[] { U8("amount") }),
        new(61, "SET_LIFE_POINT_OBJ", LifeForm.Plain, new[] { Obj(), U8("life") }),
        new(62, "SUB_LIFE_POINT_OBJ", LifeForm.Plain, new[] { Obj(), U8("life") }),
        new(63, "HIT_OBJ", LifeForm.Plain, new[] { Obj(), U8("force") }),
        new(64, "PLAY_ACF", LifeForm.Plain, new[] { new ArgDef("name", ArgType.CStr) }),
        new(65, "ECLAIR", LifeForm.Plain, new[] { U8("tenths") }),
        new(66, "INC_CLOVER_BOX", LifeForm.Plain, None),
        new(67, "SET_USED_INVENTORY", LifeForm.Plain, new[] { U8("item") }),
        new(68, "ADD_CHOICE", LifeForm.Plain, new[] { S16("message") }),
        new(69, "ASK_CHOICE", LifeForm.Plain, new[] { S16("message") }),
        new(70, "INIT_BUGGY", LifeForm.Plain, new[] { U8("mode") }),
        new(71, "MEMO_ARDOISE", LifeForm.Plain, new[] { U8("plan") }),
        new(72, "SET_HOLO_POS", LifeForm.Plain, new[] { U8("pos") }),
        new(73, "CLR_HOLO_POS", LifeForm.Plain, new[] { U8("pos") }),
        new(74, "ADD_FUEL", LifeForm.Plain, new[] { U8("amount") }),
        new(75, "SUB_FUEL", LifeForm.Plain, new[] { U8("amount") }),
        new(76, "SET_GRM", LifeForm.Plain, new[] { U8("zone"), U8("on") }),
        new(77, "SET_CHANGE_CUBE", LifeForm.Plain, new[] { U8("zone"), U8("on") }),
        new(78, "MESSAGE_ZOE", LifeForm.Plain, new[] { S16("message") }),
        new(79, "FULL_POINT", LifeForm.Plain, None),
        new(80, "BETA", LifeForm.Plain, new[] { S16("angle") }),
        new(81, "FADE_TO_PAL", LifeForm.Plain, new[] { U8("palette") }),
        new(82, "ACTION", LifeForm.Plain, None),
        new(83, "SET_FRAME", LifeForm.Plain, new[] { U8("frame") }),
        new(84, "SET_SPRITE", LifeForm.Plain, new[] { S16("sprite") }),
        new(85, "SET_FRAME_3DS", LifeForm.Plain, new[] { U8("frame") }),
        new(86, "IMPACT_OBJ", LifeForm.Plain, new[] { Obj(), U16("impact"), S16("dy") }),
        new(87, "IMPACT_POINT", LifeForm.Plain, new[] { Pt(), U16("impact") }),
        new(88, "ADD_MESSAGE", LifeForm.Plain, new[] { S16("message") }),
        new(89, "BULLE", LifeForm.Plain, new[] { U8("value") }),
        new(90, "NO_CHOC", LifeForm.Plain, new[] { U8("on") }),
        new(91, "ASK_CHOICE_OBJ", LifeForm.Plain, new[] { Obj(), S16("message") }),
        new(92, "CINEMA_MODE", LifeForm.Plain, new[] { U8("mode") }),
        new(93, "SAVE_HERO", LifeForm.Plain, None),
        new(94, "RESTORE_HERO", LifeForm.Plain, None),
        new(95, "ANIM_SET", LifeForm.Plain, new[] { U16("anim") }),
        new(96, "PLUIE", LifeForm.Plain, new[] { U8("tenths") }),
        new(97, "GAME_OVER", LifeForm.Plain, None),
        new(98, "THE_END", LifeForm.Plain, None),
        new(99, "ESCALATOR", LifeForm.Plain, new[] { U8("zone"), U8("on") }),
        new(100, "PLAY_MUSIC", LifeForm.Plain, new[] { U8("music") }),
        new(101, "TRACK_TO_VAR_GAME", LifeForm.Plain, new[] { U8("var") }),
        new(102, "VAR_GAME_TO_TRACK", LifeForm.Plain, new[] { U8("var") }),
        new(103, "ANIM_TEXTURE", LifeForm.Plain, new[] { U8("on") }),
        new(104, "ADD_MESSAGE_OBJ", LifeForm.Plain, new[] { Obj(), S16("message") }),
        new(105, "BRUTAL_EXIT", LifeForm.Plain, None),
        new(106, "REM", LifeForm.Plain, None),
        new(107, "ECHELLE", LifeForm.Plain, new[] { U8("zone"), U8("on") }),
        new(108, "SET_ARMURE", LifeForm.Plain, new[] { S8("armor") }),
        new(109, "SET_ARMURE_OBJ", LifeForm.Plain, new[] { Obj(), S8("armor") }),
        new(110, "ADD_LIFE_POINT_OBJ", LifeForm.Plain, new[] { Obj(), U8("life") }),
        new(111, "STATE_INVENTORY", LifeForm.Plain, new[] { U8("item"), U8("state") }),
        new(112, "AND_IF", LifeForm.Cond, None),
        new(113, "SWITCH", LifeForm.Switch, None),
        new(114, "OR_CASE", LifeForm.Case, None),
        new(115, "CASE", LifeForm.Case, None),
        new(116, "DEFAULT", LifeForm.Plain, None),
        new(117, "BREAK", LifeForm.Plain, new[] { Jmp() }),
        new(118, "END_SWITCH", LifeForm.Plain, None),
        new(119, "SET_HIT_ZONE", LifeForm.Plain, new[] { U8("zone"), U8("on") }),
        new(120, "SAVE_COMPORTEMENT", LifeForm.Plain, None),
        new(121, "RESTORE_COMPORTEMENT", LifeForm.Plain, None),
        new(122, "SAMPLE", LifeForm.Plain, new[] { S16("sample") }),
        new(123, "SAMPLE_RND", LifeForm.Plain, new[] { S16("sample") }),
        new(124, "SAMPLE_ALWAYS", LifeForm.Plain, new[] { S16("sample") }),
        new(125, "SAMPLE_STOP", LifeForm.Plain, new[] { S16("sample") }),
        new(126, "REPEAT_SAMPLE", LifeForm.Plain, new[] { S16("sample"), U8("count") }),
        new(127, "BACKGROUND", LifeForm.Plain, new[] { U8("on") }),
        new(128, "ADD_VAR_GAME", LifeForm.Plain, new[] { U8("var"), S16("value") }),
        new(129, "SUB_VAR_GAME", LifeForm.Plain, new[] { U8("var"), S16("value") }),
        new(130, "ADD_VAR_CUBE", LifeForm.Plain, new[] { U8("var"), U8("value") }),
        new(131, "SUB_VAR_CUBE", LifeForm.Plain, new[] { U8("var"), U8("value") }),
        new(133, "SET_RAIL", LifeForm.Plain, new[] { U8("zone"), U8("on") }),
        new(134, "INVERSE_BETA", LifeForm.Plain, None),
        new(135, "NO_BODY", LifeForm.Plain, None),
        new(136, "ADD_GOLD_PIECES", LifeForm.Plain, new[] { S16("amount") }),
        new(137, "STOP_L_TRACK_OBJ", LifeForm.Plain, new[] { Obj() }),
        new(138, "RESTORE_L_TRACK_OBJ", LifeForm.Plain, new[] { Obj() }),
        new(139, "SAVE_COMPORTEMENT_OBJ", LifeForm.Plain, new[] { Obj() }),
        new(140, "RESTORE_COMPORTEMENT_OBJ", LifeForm.Plain, new[] { Obj() }),
        new(141, "SPY", LifeForm.Plain, None),
        new(142, "DEBUG", LifeForm.Plain, None),
        new(143, "DEBUG_OBJ", LifeForm.Plain, None),
        new(144, "POPCORN", LifeForm.Plain, None),
        new(145, "FLOW_POINT", LifeForm.Plain, new[] { Pt(), U8("flow") }),
        new(146, "FLOW_OBJ", LifeForm.Plain, new[] { Obj(), U8("flow") }),
        new(147, "SET_ANIM_DIAL", LifeForm.Plain, new[] { U16("anim") }),
        new(148, "PCX", LifeForm.Plain, new[] { U8("pcx"), U8("effect") }),
        new(149, "END_MESSAGE", LifeForm.Plain, None),
        new(150, "END_MESSAGE_OBJ", LifeForm.Plain, new[] { Obj() }),
        new(151, "PARM_SAMPLE", LifeForm.Plain, new[] { S16("decalage"), U8("volume"), S16("frequency") }),
        new(152, "NEW_SAMPLE", LifeForm.Plain, new[] { S16("sample"), S16("decalage"), U8("volume"), S16("frequency") }),
        new(153, "POS_OBJ_AROUND", LifeForm.Plain, new[] { Obj(), U8("around") }),
        new(154, "PCX_MESS_OBJ", LifeForm.Plain, new[] { U8("pcx"), U8("effect"), Obj(), S16("message") }),
    };

    // ---------------------------------------------------------------------
    // Life conditions (LF_*), DoFuncLife's `case` bodies. OperandName is
    // non-null when the case reads one byte after the opcode (`*PtrPrg++`).
    // Value is the TypeAnswer the case leaves set (default RET_S8).
    // ---------------------------------------------------------------------
    private static readonly CondDef[] condDefs =
    {
        new(0, "COL", null, ValueKind.S8),
        new(1, "COL_OBJ", "obj", ValueKind.S8),
        new(2, "DISTANCE", "obj", ValueKind.S16),
        new(3, "ZONE", null, ValueKind.S8),
        new(4, "ZONE_OBJ", "obj", ValueKind.S8),
        new(5, "BODY", null, ValueKind.S8),
        new(6, "BODY_OBJ", "obj", ValueKind.S8),
        new(7, "ANIM", null, ValueKind.S16),
        new(8, "ANIM_OBJ", "obj", ValueKind.S16),
        new(9, "L_TRACK", null, ValueKind.U8),
        new(10, "L_TRACK_OBJ", "obj", ValueKind.U8),
        new(11, "VAR_CUBE", "var", ValueKind.U8),
        new(12, "CONE_VIEW", "obj", ValueKind.S16),
        new(13, "HIT_BY", null, ValueKind.S8),
        new(14, "ACTION", null, ValueKind.S8),
        new(15, "VAR_GAME", "var", ValueKind.S16),
        new(16, "LIFE_POINT", null, ValueKind.S16),
        new(17, "LIFE_POINT_OBJ", "obj", ValueKind.S16),
        new(18, "NB_LITTLE_KEYS", null, ValueKind.S8),
        new(19, "NB_GOLD_PIECES", null, ValueKind.S16),
        new(20, "COMPORTEMENT_HERO", null, ValueKind.S8),
        new(21, "CHAPTER", null, ValueKind.S8),
        new(22, "DISTANCE_3D", "obj", ValueKind.S16),
        new(23, "MAGIC_LEVEL", null, ValueKind.S8),
        new(24, "MAGIC_POINT", null, ValueKind.S8),
        new(25, "USE_INVENTORY", "item", ValueKind.S8),
        new(26, "CHOICE", null, ValueKind.S16),
        new(27, "FUEL", null, ValueKind.S16),
        new(28, "CARRY_BY", null, ValueKind.S8),
        new(29, "CDROM", null, ValueKind.S8),
        new(30, "ECHELLE", "zone", ValueKind.S8),
        new(31, "RND", "max", ValueKind.U8),
        new(32, "RAIL", "zone", ValueKind.S8),
        new(33, "BETA", null, ValueKind.S16),
        new(34, "BETA_OBJ", "obj", ValueKind.S16),
        new(35, "CARRY_OBJ_BY", "obj", ValueKind.S8),
        new(36, "ANGLE", "obj", ValueKind.S16),
        new(37, "DISTANCE_MESSAGE", "obj", ValueKind.S16),
        new(38, "HIT_OBJ_BY", "obj", ValueKind.S8),
        new(39, "REAL_ANGLE", "obj", ValueKind.S16),
        new(40, "DEMO", null, ValueKind.S8),
        new(41, "COL_DECORS", null, ValueKind.U8),
        new(42, "COL_DECORS_OBJ", "obj", ValueKind.S8),
        new(43, "PROCESSOR", null, ValueKind.S8),
        new(44, "OBJECT_DISPLAYED", "obj", ValueKind.S8),
        new(45, "ANGLE_OBJ", "obj", ValueKind.S16),
    };

    // MOVE_* ids (COMMON.H "Script: defines") in id order; index == id.
    public static readonly string[] MoveNames =
    {
        "NO_MOVE", "MOVE_MANUAL", "MOVE_FOLLOW", "MOVE_TRACK", "MOVE_FOLLOW_2", "MOVE_TRACK_ATTACK",
        "MOVE_SAME_XZ", "MOVE_PINGOUIN", "MOVE_WAGON", "MOVE_CIRCLE", "MOVE_CIRCLE2", "MOVE_SAME_XZ_BETA",
        "MOVE_BUGGY", "MOVE_BUGGY_MANUAL",
    };

    // Modes for which AdjustDirObject() consumes an extra byte from the script:
    // MOVE_FOLLOW / MOVE_SAME_XZ / MOVE_SAME_XZ_BETA take the object to follow
    // (Info3), MOVE_CIRCLE / MOVE_CIRCLE2 the flag point to orbit.
    public static bool MoveTakesParam(long move) => move is 2 or 6 or 9 or 10 or 11;

    // LT_* comparison ids, in COMMON.H order, with their C spellings.
    public static readonly string[] TestSymbols = { "==", ">", "<", ">=", "<=", "!=" };

    // ---------------------------------------------------------------------
    // Track actions (TM_*), DoTrack / SearchOffsetTrack. Args marked Hidden
    // are runtime scratch bytes the interpreter writes back into the script
    // memory itself (loop counters, wait timers, "angle in progress"
    // sentinels); they have to be present in the byte stream but carry no
    // authored meaning.
    // ---------------------------------------------------------------------
    private static ArgDef Hid8(string n) => new(n, ArgType.U8, ArgRole.Hidden);
    private static ArgDef Hid16(string n) => new(n, ArgType.S16, ArgRole.Hidden);
    private static ArgDef Hid32(string n) => new(n, ArgType.U32, ArgRole.Hidden);

    private static readonly TrackOpDef[] trackDefs =
    {
        new(0, "END", None),
        new(1, "NOP", None),
        new(2, "BODY", new[] { U8("body") }),
        new(3, "ANIM", new[] { U16("anim") }),
        new(4, "GOTO_POINT", new[] { Pt() }),
        new(5, "WAIT_ANIM", None),
        new(6, "LOOP", new[] { U8("count"), Hid8("counter"), Jmp() }),
        new(7, "ANGLE", new[] { S16("angle") }),
        new(8, "POS_POINT", new[] { Pt() }),
        new(9, "LABEL", new[] { U8("label") }),
        new(10, "GOTO", new[] { Jmp() }),
        new(11, "STOP", None),
        new(12, "GOTO_SYM_POINT", new[] { Pt() }),
        new(13, "WAIT_NB_ANIM", new[] { U8("count"), Hid8("counter") }),
        new(14, "SAMPLE", new[] { S16("sample") }),
        new(15, "GOTO_POINT_3D", new[] { Pt() }),
        new(16, "SPEED", new[] { S16("speed") }),
        new(17, "BACKGROUND", new[] { U8("on") }),
        new(18, "WAIT_NB_SECOND", new[] { U8("seconds"), Hid32("timer") }),
        new(19, "NO_BODY", None),
        new(20, "BETA", new[] { S16("angle") }),
        new(21, "OPEN_LEFT", new[] { S16("width") }),
        new(22, "OPEN_RIGHT", new[] { S16("width") }),
        new(23, "OPEN_UP", new[] { S16("width") }),
        new(24, "OPEN_DOWN", new[] { S16("width") }),
        new(25, "CLOSE", None),
        new(26, "WAIT_DOOR", None),
        new(27, "SAMPLE_RND", new[] { S16("sample") }),
        new(28, "SAMPLE_ALWAYS", new[] { S16("sample") }),
        new(29, "SAMPLE_STOP", new[] { S16("sample") }),
        new(30, "PLAY_ACF", new[] { new ArgDef("name", ArgType.CStr) }),
        new(31, "REPEAT_SAMPLE", new[] { S16("count") }),
        new(32, "SIMPLE_SAMPLE", new[] { S16("sample") }),
        new(33, "FACE_TWINSEN", new[] { S16("angle") }),
        new(34, "ANGLE_RND", new[] { S16("range"), Hid16("state") }),
        new(35, "REM", None),
        new(36, "WAIT_NB_DIZIEME", new[] { U8("tenths"), Hid32("timer") }),
        new(37, "DO", None),
        new(38, "SPRITE", new[] { S16("sprite") }),
        new(39, "WAIT_NB_SECOND_RND", new[] { U8("seconds"), Hid32("timer") }),
        new(40, "AFF_TIMER", None),
        new(41, "SET_FRAME", new[] { U8("frame") }),
        new(42, "SET_FRAME_3DS", new[] { U8("frame") }),
        new(43, "SET_START_3DS", new[] { U8("frame") }),
        new(44, "SET_END_3DS", new[] { U8("frame") }),
        new(45, "START_ANIM_3DS", new[] { U8("fps") }),
        new(46, "STOP_ANIM_3DS", None),
        new(47, "WAIT_ANIM_3DS", None),
        new(48, "WAIT_FRAME_3DS", new[] { U8("frame") }),
        new(49, "WAIT_NB_DIZIEME_RND", new[] { U8("tenths"), Hid32("timer") }),
        new(50, "DECALAGE", new[] { S16("decalage") }),
        new(51, "FREQUENCE", new[] { S16("frequency") }),
        new(52, "VOLUME", new[] { U8("volume") }),
    };

    private static readonly LifeOpDef?[] lifeById = Index(lifeDefs, d => d.Id, 256);
    private static readonly TrackOpDef?[] trackById = Index(trackDefs, d => d.Id, 256);
    private static readonly CondDef?[] condById = Index(condDefs, d => d.Id, 256);

    private static readonly Dictionary<string, LifeOpDef> lifeByName = lifeDefs.ToDictionary(d => d.Name, StringComparer.Ordinal);
    private static readonly Dictionary<string, TrackOpDef> trackByName = trackDefs.ToDictionary(d => d.Name, StringComparer.Ordinal);
    private static readonly Dictionary<string, CondDef> condByName = condDefs.ToDictionary(d => d.Name, StringComparer.Ordinal);

    private static T?[] Index<T>(T[] defs, Func<T, byte> id, int size) where T : class
    {
        var table = new T?[size];
        foreach (var d in defs) table[id(d)] = d;
        return table;
    }

    public static IReadOnlyList<LifeOpDef> LifeOps => lifeDefs;
    public static IReadOnlyList<TrackOpDef> TrackOps => trackDefs;
    public static IReadOnlyList<CondDef> Conditions => condDefs;

    public static LifeOpDef? Life(byte id) => lifeById[id];
    public static TrackOpDef? Track(byte id) => trackById[id];
    public static CondDef? Cond(byte id) => condById[id];

    public static LifeOpDef? Life(string name) => lifeByName.GetValueOrDefault(name);
    public static TrackOpDef? Track(string name) => trackByName.GetValueOrDefault(name);
    public static CondDef? Cond(string name) => condByName.GetValueOrDefault(name);
}
