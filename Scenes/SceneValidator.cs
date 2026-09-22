using LBAAssembler.LbaScript;

namespace LBAAssembler.Scenes;

internal enum SceneIssueSeverity { Error, Warning }

// One finding. Where names the part of the scene ("actor 5 life script", "zone 3", "header").
internal sealed record SceneIssue(SceneIssueSeverity Severity, string Where, string Message)
{
    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()}: {Where}: {Message}";
}

internal sealed class SceneValidationOptions
{
    // How many scenes the game has (cube-change targets must be below it); unknown = not checked.
    public int? SceneCount;
    // LBA1 (0..119) or LBA2 (0..221) scene numbers that exist, when the caller knows more than a count.
    public Func<int, bool>? SceneExists;
}

// The engine's own limits and assumptions, taken from its source (COMMON.H, DISKFUNC.C / .CPP, GERELIFE, GRILLE) and
// checked against every retail scene: retail scenes produce no errors. Errors are things the engine would misread or
// overflow; warnings are things that are legal but almost certainly not what was meant.
internal static class SceneValidator
{
    public const int MaxObjects = 100;   // MAX_OBJETS
    public const int MaxZones = 255;     // MAX_ZONES
    public const int MaxTrackPoints = 255; // MAX_TRACKS

    public static List<SceneIssue> Validate(SceneModel scene, SceneValidationOptions? options = null)
    {
        options ??= new SceneValidationOptions();
        var issues = new List<SceneIssue>();
        void Error(string where, string message) => issues.Add(new(SceneIssueSeverity.Error, where, message));
        void Warn(string where, string message) => issues.Add(new(SceneIssueSeverity.Warning, where, message));

        var lba1 = scene.Game == SceneGame.Lba1;
        int maxScript = lba1 ? ushort.MaxValue : short.MaxValue;
        var coordMax = lba1 ? short.MaxValue : int.MaxValue;

        // ---- counts ----
        if (scene.Actors.Count < 1) { Error("scene", "the scene has no hero."); return issues; }
        if (scene.Actors.Count > MaxObjects) Error("scene", $"{scene.Actors.Count} actors; the engine holds at most {MaxObjects} (the hero counts as one).");
        if (scene.Zones.Count > MaxZones) Error("scene", $"{scene.Zones.Count} zones; the engine's limit is {MaxZones}.");
        if (scene.TrackPoints.Count > MaxTrackPoints) Error("scene", $"{scene.TrackPoints.Count} track points; the engine's limit is {MaxTrackPoints}.");

        // ---- header ----
        if (!lba1)
        {
            if (scene.CubeMode is not (0 or 1)) Warn("header", $"cube mode {scene.CubeMode}: 0 is an interior, 1 an exterior.");
            if (scene.CubeMode == 1 && (scene.CubeX > 15 || scene.CubeY > 15)) Warn("header", $"island cube ({scene.CubeX}, {scene.CubeY}) lies outside the 16 x 16 cube grid.");
        }
        if (scene.AlphaLight is < 0 or > 4095 || scene.BetaLight is < 0 or > 4095) Warn("header", "light angles are 0..4095.");
        if (lba1 && options.SceneCount is { } gameOver && scene.GameOverScene >= gameOver) Warn("header", $"game over scene {scene.GameOverScene} doesn't exist.");

        // ---- actors ----
        using (Opcodes.Use(lba1 ? Opcodes.Lba1 : Opcodes.Lba2))
        {
            for (var n = 0; n < scene.Actors.Count; n++)
            {
                var a = scene.Actors[n];
                var name = n == 0 ? "hero" : $"actor {n}";
                if (Math.Abs((long)a.X) > coordMax || Math.Abs((long)a.Z) > coordMax) Error(name, "position out of range.");
                if (a.X < 0 || a.Z < 0 || a.X > 32767 || a.Z > 32767) Warn(name, $"position ({a.X}, {a.Y}, {a.Z}) lies outside the scene's 64 x 64 cell area.");
                if (n > 0)
                {
                    if (a.IsSprite && a.Entity != -1 && lba1) Warn(name, "a sprite actor normally has entity -1.");
                    if (!a.IsSprite && a.Entity < 0 && (a.Flags & 0x0200) == 0) Warn(name, "a 3D actor without an entity can't be drawn.");
                    if ((a.Flags & 0x0008) != 0)
                    {
                        if (!a.IsSprite) Warn(name, "SPRITE_CLIP (a door) is set on an actor that isn't a sprite.");
                        else if (lba1 && (a.Info[0] >= a.Info[2] || a.Info[1] >= a.Info[3])) Warn(name, "the door's screen clip rectangle (info 0..3) is empty or reversed.");
                    }
                }
                CheckScript(scene, n, name, "track", a.Track, ScriptKind.Track, maxScript, Error, Warn);
                CheckScript(scene, n, name, "life", a.Life, ScriptKind.Life, maxScript, Error, Warn);
            }
        }

        // ---- zones ----
        for (var i = 0; i < scene.Zones.Count; i++)
        {
            var z = scene.Zones[i];
            var name = $"zone {i}";
            if (z.Info.Length != (lba1 ? 4 : 8)) Error(name, $"{z.Info.Length} info values; this game's zones have {(lba1 ? 4 : 8)}.");
            if (z.X0 > z.X1 || z.Y0 > z.Y1 || z.Z0 > z.Z1) Warn(name, "the box is reversed (a min above a max), so nothing can stand in it.");
            if (z.Type == 0)
            {
                var target = lba1 ? (z.Info.Length > 0 ? z.Info[0] : -1) : z.Num;
                if (target < 0) Error(name, "a cube change to a negative scene number.");
                else if (options.SceneExists is { } exists && !exists(target)) Error(name, $"a cube change to scene {target}, which doesn't exist.");
                else if (options.SceneCount is { } count && target >= count) Error(name, $"a cube change to scene {target}; the game has {count} scenes.");
            }
        }
        return issues;
    }

    private static void CheckScript(SceneModel scene, int actorIndex, string actorName, string label, byte[] code, ScriptKind kind, int maxLength,
        Action<string, string> error, Action<string, string> warn)
    {
        var where = $"{actorName} {label} script";
        if (code.Length > maxLength) { error(where, $"{code.Length} bytes; the record stores a script length in {(maxLength == ushort.MaxValue ? 16 : 15)} bits."); return; }
        if (code.Length == 0) return;

        List<Instr> instructions;
        ScriptFormatException? failure;
        instructions = kind == ScriptKind.Life ? Bytecode.DecodeLife(code, out failure) : Bytecode.DecodeTrack(code, out failure);
        if (failure is not null) { error(where, failure.Message); return; }

        var starts = new HashSet<int>(instructions.Select(i => i.Offset)) { code.Length };
        foreach (var ins in instructions)
        {
            // a track GOTO to -1 is legal (LBA1 uses it in retail scripts to mean "the start")
            if (Bytecode.TryGetTarget(kind, ins, out var target) && !starts.Contains(target) && !(kind == ScriptKind.Track && target == -1))
                error(where, $"the jump at byte {ins.Offset} lands on {target}, which isn't the start of an instruction.");

            var args = kind == ScriptKind.Life ? Opcodes.Life(ins.Op)?.Args : Opcodes.Track(ins.Op)?.Args;
            if (args is null) continue;
            for (var k = 0; k < args.Length && k < ins.A.Length; k++)
            {
                if (args[k].Role == ArgRole.Obj && (ins.A[k] < 0 || ins.A[k] >= scene.Actors.Count))
                    warn(where, $"byte {ins.Offset}: refers to actor {ins.A[k]}, but the scene has {scene.Actors.Count} actors (0..{scene.Actors.Count - 1}).");
                else if (args[k].Role == ArgRole.Point && (ins.A[k] < 0 || ins.A[k] >= scene.TrackPoints.Count))
                    warn(where, $"byte {ins.Offset}: refers to track point {ins.A[k]}, but the scene has {scene.TrackPoints.Count}.");
            }
        }
    }
}
