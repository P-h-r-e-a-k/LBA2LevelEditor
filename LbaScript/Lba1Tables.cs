namespace LBA2LevelEditor.LbaScript;

// Opcode tables of Little Big Adventure 1's life and track scripts. The two games share the VM design
// (same IF-chain / jump-by-offset model, END_COMPORTEMENT-terminated blocks) but not the numbering or
// operand widths: LBA1 has 106 life actions, 30 conditions, byte-sized variables and no
// SWITCH / AND_IF. Layouts follow LBArchitect's decompiler tables (Scene1LibDecomp / SceneLib1Tab) and are
// checked against every script of the retail SCENE.HQR (tools/ScriptRoundTrip, `lba1` commands).
internal static class Lba1Tables
{
    private static readonly ArgDef[] None = Lba2Tables.None;
    private static ArgDef U8(string n) => Lba2Tables.U8(n);
    private static ArgDef S8(string n) => Lba2Tables.S8(n);
    private static ArgDef U16(string n) => Lba2Tables.U16(n);
    private static ArgDef S16(string n, ArgRole r = ArgRole.Plain) => Lba2Tables.S16(n, r);
    private static ArgDef Obj() => Lba2Tables.Obj();
    private static ArgDef Pt() => Lba2Tables.Pt();
    private static ArgDef Jmp() => Lba2Tables.Jmp();

    private static LifeOpDef Op(byte id, string name, params ArgDef[] args) => new(id, name, LifeForm.Plain, args);
    private static LifeOpDef If(byte id, string name) => new(id, name, LifeForm.Cond, None);

    private static readonly LifeOpDef[] LifeDefs =
    {
        Op(0, "END"),
        Op(1, "NOP"),
        If(2, "SNIF"),
        Op(3, "OFFSET", Jmp()),
        If(4, "NEVERIF"),
        If(6, "NO_IF"),
        Op(10, "LABEL", U8("label")),
        Op(11, "RETURN"),
        If(12, "IF"),
        If(13, "SWIF"),
        If(14, "ONEIF"),
        Op(15, "ELSE", Jmp()),
        Op(16, "ENDIF"),
        Op(17, "BODY", S8("body")),
        Op(18, "BODY_OBJ", Obj(), S8("body")),
        Op(19, "ANIM", U8("anim")),
        Op(20, "ANIM_OBJ", Obj(), U8("anim")),
        Op(21, "SET_LIFE", S16("offset")),
        Op(22, "SET_LIFE_OBJ", Obj(), U16("offset")),
        Op(23, "SET_TRACK", S16("track", ArgRole.TrackOffset)),
        Op(24, "SET_TRACK_OBJ", Obj(), S16("track", ArgRole.TrackOffset)),
        Op(25, "MESSAGE", S16("message")),
        Op(26, "FALLABLE", U8("mode")),
        new(27, "SET_DIR", LifeForm.Dir, new[] { U8("move") }),
        new(28, "SET_DIR_OBJ", LifeForm.Dir, new[] { Obj(), U8("move") }),
        Op(29, "CAM_FOLLOW", Obj()),
        Op(30, "COMPORTEMENT_HERO", U8("comportement")),
        Op(31, "SET_VAR_CUBE", U8("var"), U8("value")),
        Op(32, "COMPORTEMENT", U8("index")),
        Op(33, "SET_COMPORTEMENT", S16("comportement", ArgRole.LifeOffset)),
        Op(34, "SET_COMPORTEMENT_OBJ", Obj(), S16("comportement", ArgRole.LifeOffset)),
        Op(35, "END_COMPORTEMENT"),
        Op(36, "SET_VAR_GAME", U8("var"), U8("value")),
        Op(37, "KILL_OBJ", Obj()),
        Op(38, "SUICIDE"),
        Op(39, "USE_ONE_LITTLE_KEY"),
        Op(40, "GIVE_GOLD_PIECES", S16("amount")),
        Op(41, "END_LIFE"),
        Op(42, "STOP_L_TRACK"),
        Op(43, "RESTORE_L_TRACK"),
        Op(44, "MESSAGE_OBJ", Obj(), S16("message")),
        Op(45, "INC_CHAPTER"),
        Op(46, "FOUND_OBJECT", U8("item")),
        Op(47, "SET_DOOR_LEFT", S16("width")),
        Op(48, "SET_DOOR_RIGHT", S16("width")),
        Op(49, "SET_DOOR_UP", S16("width")),
        Op(50, "SET_DOOR_DOWN", S16("width")),
        Op(51, "GIVE_BONUS", U8("flag")),
        Op(52, "CHANGE_CUBE", U8("cube")),
        Op(53, "OBJ_COL", U8("on")),
        Op(54, "BRICK_COL", U8("mode")),
        If(55, "OR_IF"),
        Op(56, "INVISIBLE", U8("on")),
        Op(57, "ZOOM", U8("on")),
        Op(58, "POS_POINT", Pt()),
        Op(59, "SET_MAGIC_LEVEL", U8("level")),
        Op(60, "SUB_MAGIC_POINT", U8("amount")),
        Op(61, "SET_LIFE_POINT_OBJ", Obj(), U8("life")),
        Op(62, "SUB_LIFE_POINT_OBJ", Obj(), U8("life")),
        Op(63, "HIT_OBJ", Obj(), U8("force")),
        Op(64, "PLAY_FLA", new ArgDef("name", ArgType.CStr)),
        Op(65, "PLAY_MIDI", U8("midi")),
        Op(66, "INC_CLOVER_BOX"),
        Op(67, "SET_USED_INVENTORY", U8("item")),
        Op(68, "ADD_CHOICE", S16("message")),
        Op(69, "ASK_CHOICE", S16("message")),
        Op(70, "BIG_MESSAGE", S16("message")),
        Op(71, "INIT_PINGOUIN", Obj()),
        Op(72, "SET_HOLO_POS", U8("pos")),
        Op(73, "CLR_HOLO_POS", U8("pos")),
        Op(74, "ADD_FUEL", U8("amount")),
        Op(75, "SUB_FUEL", U8("amount")),
        Op(76, "SET_GRM", U8("grm")),
        Op(77, "SAY_MESSAGE", S16("message")),
        Op(78, "SAY_MESSAGE_OBJ", Obj(), S16("message")),
        Op(79, "FULL_POINT"),
        Op(80, "BETA", S16("angle")),
        Op(81, "GRM_OFF"),
        Op(82, "FADE_PAL_RED"),
        Op(83, "FADE_ALARM_RED"),
        Op(84, "FADE_ALARM_PAL"),
        Op(85, "FADE_RED_PAL"),
        Op(86, "FADE_RED_ALARM"),
        Op(87, "FADE_PAL_ALARM"),
        Op(88, "EXPLODE_OBJ", Obj()),
        Op(89, "BULLE_ON"),
        Op(90, "BULLE_OFF"),
        Op(91, "ASK_CHOICE_OBJ", Obj(), S16("message")),
        Op(92, "SET_DARK_PAL"),
        Op(93, "SET_NORMAL_PAL"),
        Op(94, "MESSAGE_SENDELL"),
        Op(95, "ANIM_SET", U8("anim")),
        Op(96, "HOLOMAP_TRAJ", U8("trajectory")),
        Op(97, "GAME_OVER"),
        Op(98, "THE_END"),
        Op(99, "MIDI_OFF"),
        Op(100, "PLAY_CD_TRACK", U8("track")),
        Op(101, "PROJ_ISO"),
        Op(102, "PROJ_3D"),
        Op(103, "TEXT", S16("text")),
        Op(104, "CLEAR_TEXT"),
        Op(105, "BRUTAL_EXIT"),
    };

    private static CondDef C(byte id, string name, string? operand, ValueKind value) => new(id, name, operand, value);

    private static readonly CondDef[] CondDefs =
    {
        C(0, "COL", null, ValueKind.U8),
        C(1, "COL_OBJ", "obj", ValueKind.U8),
        C(2, "DISTANCE", "obj", ValueKind.S16),
        C(3, "ZONE", null, ValueKind.U8),
        C(4, "ZONE_OBJ", "obj", ValueKind.U8),
        C(5, "BODY", null, ValueKind.S8),
        C(6, "BODY_OBJ", "obj", ValueKind.S8),
        C(7, "ANIM", null, ValueKind.U8),
        C(8, "ANIM_OBJ", "obj", ValueKind.U8),
        C(9, "L_TRACK", null, ValueKind.U8),
        C(10, "L_TRACK_OBJ", "obj", ValueKind.U8),
        C(11, "VAR_CUBE", "var", ValueKind.U8),
        C(12, "CONE_VIEW", "obj", ValueKind.S16),
        C(13, "HIT_BY", null, ValueKind.U8),
        C(14, "ACTION", null, ValueKind.U8),
        C(15, "VAR_GAME", "var", ValueKind.U8),
        C(16, "LIFE_POINT", null, ValueKind.U8),
        C(17, "LIFE_POINT_OBJ", "obj", ValueKind.U8),
        C(18, "NB_LITTLE_KEYS", null, ValueKind.U8),
        C(19, "NB_GOLD_PIECES", null, ValueKind.S16),
        C(20, "COMPORTEMENT_HERO", null, ValueKind.U8),
        C(21, "CHAPTER", null, ValueKind.U8),
        C(22, "DISTANCE_3D", "obj", ValueKind.S16),
        C(25, "USE_INVENTORY", "item", ValueKind.U8),
        C(26, "CHOICE", null, ValueKind.S16),
        C(27, "FUEL", null, ValueKind.U8),
        C(28, "CARRY_BY", null, ValueKind.U8),
        C(29, "CDROM", null, ValueKind.U8),
    };

    private static TrackOpDef T(byte id, string name, params ArgDef[] args) => new(id, name, args);
    private static ArgDef Hid8(string n) => Lba2Tables.Hid8(n);
    private static ArgDef Hid16(string n) => Lba2Tables.Hid16(n);
    private static ArgDef Hid32(string n) => Lba2Tables.Hid32(n);

    private static readonly TrackOpDef[] TrackDefs =
    {
        T(0, "END"),
        T(1, "NOP"),
        T(2, "BODY", S8("body")),
        T(3, "ANIM", U8("anim")),
        T(4, "GOTO_POINT", Pt()),
        T(5, "WAIT_ANIM"),
        T(6, "LOOP"),
        T(7, "ANGLE", S16("angle")),
        T(8, "POS_POINT", Pt()),
        T(9, "LABEL", U8("label")),
        T(10, "GOTO", Jmp()),
        T(11, "STOP"),
        T(12, "GOTO_SYM_POINT", Pt()),
        T(13, "WAIT_NB_ANIM", U8("count"), Hid8("counter")),
        T(14, "SAMPLE", S16("sample")),
        T(15, "GOTO_POINT_3D", Pt()),
        T(16, "SPEED", S16("speed")),
        T(17, "BACKGROUND", U8("on")),
        T(18, "WAIT_NB_SECOND", U8("seconds"), Hid32("timer")),
        T(19, "NO_BODY"),
        T(20, "BETA", S16("angle")),
        T(21, "OPEN_LEFT", S16("width")),
        T(22, "OPEN_RIGHT", S16("width")),
        T(23, "OPEN_UP", S16("width")),
        T(24, "OPEN_DOWN", S16("width")),
        T(25, "CLOSE"),
        T(26, "WAIT_DOOR"),
        T(27, "SAMPLE_RND", S16("sample")),
        T(28, "SAMPLE_ALWAYS", S16("sample")),
        T(29, "SAMPLE_STOP", S16("sample")),
        T(30, "PLAY_FLA", new ArgDef("name", ArgType.CStr)),
        T(31, "REPEAT_SAMPLE", U8("count")),
        T(32, "SIMPLE_SAMPLE", S16("sample")),
        T(33, "FACE_TWINSEN", Hid16("angle")),
        T(34, "ANGLE_RND", S16("range"), Hid16("state")),
    };

    // MOVE_* ids: 0 none, 1 manual, 2 follow, 3 track, 4 follow (variant), 5 track+attack, 6 same X/Z, 7 random.
    private static readonly string[] MoveNames =
    {
        "NO_MOVE", "MOVE_MANUAL", "MOVE_FOLLOW", "MOVE_TRACK", "MOVE_FOLLOW_2", "MOVE_TRACK_ATTACK", "MOVE_SAME_XZ", "MOVE_RANDOM",
    };

    public static OpcodeSet Build() => new()
    {
        Name = "LBA1",
        LifeDefs = LifeDefs,
        TrackDefs = TrackDefs,
        CondDefs = CondDefs,
        MoveNames = MoveNames,
        MoveTakesParam = move => move == 2,
        AndIf = 12,     // consecutive IFs sharing one false target are an AND
        HasSwitch = false,
        TrackHiddenDefault = (op, _, _) => op switch
        {
            33 => -1,   // FACE_TWINSEN: angle not computed yet
            34 => -1,   // ANGLE_RND: state
            _ => 0,
        },
    };
}
