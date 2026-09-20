namespace LBA2LevelEditor.LbaScript;

// Everything about a game's script bytecode that the translator has to know: the opcode tables
// plus the handful of engine rules that differ between LBA1 and LBA2. The translator is written
// against `Opcodes` (below), which forwards to whichever set is active; SceneScripts activates
// the set of the game its scene belongs to around every operation.
internal sealed class OpcodeSet
{
    public required string Name { get; init; }
    public required LifeOpDef[] LifeDefs { get; init; }
    public required TrackOpDef[] TrackDefs { get; init; }
    public required CondDef[] CondDefs { get; init; }

    // MOVE_* ids in id order (index == id).
    public required string[] MoveNames { get; init; }

    // SET_DIR / SET_DIR_OBJ read one more operand byte after some move modes.
    public required Func<long, bool> MoveTakesParam { get; init; }

    // The opcode a non-final leaf of an `&&` chain compiles to: LBA2 has AND_IF, LBA1 chains plain IFs.
    public required byte AndIf { get; init; }

    public required bool HasSwitch { get; init; }

    // Value a hidden (runtime scratch) track operand has in a freshly authored script.
    public required Func<byte, long[], int, long> TrackHiddenDefault { get; init; }

    // LT_* comparison ids with their C spellings.
    public string[] TestSymbols { get; } = { "==", ">", "<", ">=", "<=", "!=" };

    private LifeOpDef?[]? lifeById;
    private TrackOpDef?[]? trackById;
    private CondDef?[]? condById;
    private Dictionary<string, LifeOpDef>? lifeByName;
    private Dictionary<string, TrackOpDef>? trackByName;
    private Dictionary<string, CondDef>? condByName;

    private static T?[] Index<T>(T[] defs, Func<T, byte> id) where T : class
    {
        var table = new T?[256];
        foreach (var d in defs) table[id(d)] = d;
        return table;
    }

    public LifeOpDef? Life(byte id) => (lifeById ??= Index(LifeDefs, d => d.Id))[id];
    public TrackOpDef? Track(byte id) => (trackById ??= Index(TrackDefs, d => d.Id))[id];
    public CondDef? Cond(byte id) => (condById ??= Index(CondDefs, d => d.Id))[id];

    public LifeOpDef? Life(string name) => (lifeByName ??= LifeDefs.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase)).GetValueOrDefault(name);
    public TrackOpDef? Track(string name) => (trackByName ??= TrackDefs.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase)).GetValueOrDefault(name);
    public CondDef? Cond(string name) => (condByName ??= CondDefs.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase)).GetValueOrDefault(name);
}

// The opcode tables of the active game (LBA2 unless a scope says otherwise). Kept as a static facade
// so the translator's many lookups read the same for both games.
internal static class Opcodes
{
    public static readonly OpcodeSet Lba2 = new()
    {
        Name = "LBA2",
        LifeDefs = Lba2Tables.LifeDefs,
        TrackDefs = Lba2Tables.TrackDefs,
        CondDefs = Lba2Tables.CondDefs,
        MoveNames = Lba2Tables.MoveNames,
        MoveTakesParam = Lba2Tables.MoveTakesParam,
        AndIf = 112,
        HasSwitch = true,
        TrackHiddenDefault = (op, a, _) => op switch
        {
            6 => a[0],      // LOOP: counter starts at the loop count
            34 => -1,       // ANGLE_RND: state
            _ => 0,         // WAIT_NB_ANIM counter, WAIT_NB_* timers
        },
    };

    public static readonly OpcodeSet Lba1 = Lba1Tables.Build();

    [ThreadStatic] private static OpcodeSet? current;

    public static OpcodeSet Active => current ?? Lba2;

    // Makes `set` the active table set until the returned scope is disposed.
    public static IDisposable Use(OpcodeSet set)
    {
        var previous = current;
        current = set;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly OpcodeSet? previous;
        public Scope(OpcodeSet? previous) => this.previous = previous;
        public void Dispose() => current = previous;
    }

    public static string[] MoveNames => Active.MoveNames;
    public static bool MoveTakesParam(long move) => Active.MoveTakesParam(move);
    public static string[] TestSymbols => Active.TestSymbols;

    public static IReadOnlyList<LifeOpDef> LifeOps => Active.LifeDefs;
    public static IReadOnlyList<TrackOpDef> TrackOps => Active.TrackDefs;
    public static IReadOnlyList<CondDef> Conditions => Active.CondDefs;

    public static LifeOpDef? Life(byte id) => Active.Life(id);
    public static TrackOpDef? Track(byte id) => Active.Track(id);
    public static CondDef? Cond(byte id) => Active.Cond(id);

    public static LifeOpDef? Life(string name) => Active.Life(name);
    public static TrackOpDef? Track(string name) => Active.Track(name);
    public static CondDef? Cond(string name) => Active.Cond(name);
}
