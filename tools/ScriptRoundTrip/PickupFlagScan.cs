using LBAAssembler;
using LBAAssembler.LbaScript;
using LBAAssembler.Scenes;

namespace ScriptRoundTrip;

// Scans every retail life script for the "one-shot pickup" shape:
//   if (VAR_x(N) == 0) { ... give the reward ... SET_VAR_x(N, v);  // or ADD_VAR_x(N, v) }
// where VAR_x/SET_VAR_x is either the per-scene VAR_CUBE or the global VAR_GAME array. Two independent
// bugs fall out of this shape:
//   - DUPLICATE: two different actors (in the same scene for a cube var, anywhere for a game var) guard
//     themselves on the *same* flag number, so collecting one silently disables the other (or, if both
//     fire before either sets the flag, both give their reward for free forever).
//   - ORPHAN-READ: the guard tests a flag that nothing anywhere in that actor's own script ever writes,
//     so the box never remembers it was opened and can be opened again every time.
//
//   pickupflags [game dir]
internal static class PickupFlagScan
{
    private static readonly HashSet<byte> WriteOps = new() { 31, 36, 128, 129, 130, 131 }; // SET/ADD/SUB _VAR_CUBE / _VAR_GAME
    private static bool IsCubeWrite(byte op) => op is 31 or 130 or 131;

    private sealed class Guard
    {
        public bool IsCube;
        public int Var;
        public byte Test;
        public int Value;
        public int Target;   // jump target: instructions before this offset are "inside" the guard
        public int Offset;
    }

    private sealed class Pickup
    {
        public bool IsCube;
        public int Var;
        public int Scene, Actor, Comp, Offset;
        public string Actor2 = "";
        public int SetValue;
        public string Body = "";     // notable reward ops seen inside the guarded body
        public bool SelfSet;         // true = found via the "SET covered by guard" pass, false = orphan-read pass
    }

    public static int Run(string[] args)
    {
        var root = args.Length > 1 ? args[1] : @"E:\GOG Games\LBA2 - Death rooms";
        var scenePath = Path.Combine(root, "SCENE.HQR");
        var archive = HqrArchive.Open(scenePath);
        var entries = HqrArchive.CountEntries(scenePath);
        var sceneNames = File.Exists(@"E:\dump\LBAAssembler\Assets\FileDesc\SCENE2.HQD")
            ? File.ReadAllLines(@"E:\dump\LBAAssembler\Assets\FileDesc\SCENE2.HQD", System.Text.Encoding.Latin1)
            : Array.Empty<string>();

        var pickups = new List<Pickup>();
        var sceneNameOf = new Dictionary<int, string>();

        for (var entry = 1; entry < entries; entry++)
        {
            if (!archive.IsValid(entry)) continue;
            var scene = entry - 1;
            var model = SceneSerializer.Parse(SceneGame.Lba2, archive.Read(entry));
            sceneNameOf[scene] = sceneNames.ElementAtOrDefault(entry + 1) ?? "";
            for (var ai = 0; ai < model.Actors.Count; ai++)
            {
                var a = model.Actors[ai];
                if (a.Life.Length == 0) continue;
                var code = Bytecode.DecodeLife(a.Life, out _);
                var actorLabel = ai == 0 ? "hero" : a.IsSprite ? $"sprite {a.Sprite}" : $"ent {a.Entity}/body {a.Body}";
                ScanActor(code, scene, ai, actorLabel, pickups);
            }
        }

        Console.WriteLine($"scenes scanned; one-shot-guarded writes found = {pickups.Count(p => p.SelfSet)}; orphan-read candidates = {pickups.Count(p => !p.SelfSet)}");

        Console.WriteLine();
        Console.WriteLine("==== DUPLICATE ONE-SHOT FLAGS (two+ actors guard on the same flag) ====");
        var dupGroups = pickups.Where(p => p.SelfSet)
            .GroupBy(p => p.IsCube ? $"cube s{p.Scene} #{p.Var}" : $"game #{p.Var}")
            .Where(g => g.Select(p => (p.Scene, p.Actor)).Distinct().Count() > 1)
            .OrderBy(g => g.Key);
        var dupCount = 0;
        foreach (var g in dupGroups)
        {
            dupCount++;
            Console.WriteLine($"-- {g.Key} --  ({g.Select(p => (p.Scene, p.Actor)).Distinct().Count()} distinct actors)");
            foreach (var p in g.OrderBy(p => p.Scene).ThenBy(p => p.Actor).ThenBy(p => p.Offset))
                Console.WriteLine($"   scene {p.Scene} [{sceneNameOf.GetValueOrDefault(p.Scene, "")}] actor {p.Actor} ({p.Actor2}) comp {p.Comp} @{p.Offset}: set={p.SetValue} body={{{p.Body}}}");
        }
        Console.WriteLine($"total duplicate groups = {dupCount}");

        Console.WriteLine();
        Console.WriteLine("==== ORPHAN-READ FLAGS (guard reads a flag the actor's own script never writes) ====");
        var orphans = pickups.Where(p => !p.SelfSet).ToList();
        foreach (var p in orphans.OrderBy(p => p.IsCube).ThenBy(p => p.Scene).ThenBy(p => p.Actor))
            Console.WriteLine($"   {(p.IsCube ? "cube" : "game")} #{p.Var}  scene {p.Scene} [{sceneNameOf.GetValueOrDefault(p.Scene, "")}] actor {p.Actor} ({p.Actor2}) comp {p.Comp} @{p.Offset}: body={{{p.Body}}}");
        Console.WriteLine($"total orphan-read candidates = {orphans.Count}");

        return 0;
    }

    private static void ScanActor(List<Instr> code, int scene, int actor, string actorLabel, List<Pickup> pickups)
    {
        // Pass 1: every guard (VAR_CUBE/VAR_GAME condition) covering the instruction range it protects,
        // and every write (SET/ADD/SUB _VAR_CUBE/_VAR_GAME) anywhere in the actor, for the orphan-read pass.
        var guards = new List<Guard>();
        var writesAnywhere = new HashSet<(bool IsCube, int Var)>();
        foreach (var ins in code)
        {
            if (!WriteOps.Contains(ins.Op)) continue;
            writesAnywhere.Add((IsCubeWrite(ins.Op), (int)ins.A[0]));
        }

        for (var i = 0; i < code.Count; i++)
        {
            var ins = code[i];
            var def = Opcodes.Life(ins.Op)!;

            if (def.Form == LifeForm.Cond)
            {
                if (ins.Func == 11 || ins.Func == 15) // VAR_CUBE / VAR_GAME
                    guards.Add(new Guard { IsCube = ins.Func == 11, Var = ins.FuncArg, Test = ins.Test, Value = ins.Value, Target = ins.Target, Offset = ins.Offset });
                continue;
            }
            if (def.Form is LifeForm.Switch or LifeForm.Case) continue;

            if (ins.Op == 35) { guards.Clear(); continue; } // END_COMPORTEMENT

            if (WriteOps.Contains(ins.Op))
            {
                var isCube = IsCubeWrite(ins.Op);
                var setVar = (int)ins.A[0];
                var setVal = (int)ins.A[1];

                var covering = guards.Where(g => g.IsCube == isCube && g.Var == setVar && g.Offset < ins.Offset && ins.Offset < g.Target).ToList();
                if (covering.Count == 0) continue; // plain write, not a one-shot guard shape

                var body = DescribeBody(code, IndexAfterOffset(code, covering[0].Offset), i);
                pickups.Add(new Pickup { IsCube = isCube, Var = setVar, Scene = scene, Actor = actor, Comp = CompOf(code, i), Offset = covering[0].Offset, Actor2 = actorLabel, SetValue = setVal, Body = body, SelfSet = true });
            }
        }

        // Pass 2: "== 0" guards on VAR_CUBE/VAR_GAME whose covered body has a reward shape but the actor's
        // script never writes that same var anywhere (not just outside the guard - anywhere at all).
        for (var i = 0; i < code.Count; i++)
        {
            var ins = code[i];
            var def = Opcodes.Life(ins.Op)!;
            if (def.Form != LifeForm.Cond) continue;
            if (ins.Func != 11 && ins.Func != 15) continue;
            if (Opcodes.TestSymbols[ins.Test] != "==" || ins.Value != 0) continue;
            var isCube = ins.Func == 11;
            var varId = ins.FuncArg;
            if (writesAnywhere.Contains((isCube, varId))) continue;

            var body = DescribeBody(code, i + 1, IndexAtOrAfterOffset(code, ins.Target));
            if (body.Length == 0) continue; // not reward-shaped; too noisy to report
            pickups.Add(new Pickup { IsCube = isCube, Var = varId, Scene = scene, Actor = actor, Comp = CompOf(code, i), Offset = ins.Offset, Actor2 = actorLabel, SetValue = 0, Body = body, SelfSet = false });
        }
    }

    private static int CompOf(List<Instr> code, int uptoIdx)
    {
        var comp = 0;
        for (var i = 0; i < uptoIdx && i < code.Count; i++) if (code[i].Op == 35) comp++;
        return comp;
    }

    private static int IndexAfterOffset(List<Instr> code, int offset)
    {
        for (var i = 0; i < code.Count; i++) if (code[i].Offset == offset) return i + 1;
        return 0;
    }

    private static int IndexAtOrAfterOffset(List<Instr> code, int offset)
    {
        for (var i = 0; i < code.Count; i++) if (code[i].Offset >= offset) return i;
        return code.Count;
    }

    private static readonly HashSet<byte> RewardOps = new() { 46, 66, 40, 136, 61, 110 }; // FOUND_OBJECT, INC_CLOVER_BOX, GIVE/ADD_GOLD_PIECES, SET/ADD_LIFE_POINT_OBJ

    private static string DescribeBody(List<Instr> code, int fromIdx, int toIdx)
    {
        var names = new List<string>();
        for (var k = Math.Max(0, fromIdx); k < Math.Min(code.Count, toIdx); k++)
        {
            var ins = code[k];
            if (!RewardOps.Contains(ins.Op)) continue;
            var def = Opcodes.Life(ins.Op);
            var arg = ins.A.Length > 0 ? ins.A[0].ToString() : "";
            names.Add($"{def?.Name}({arg})");
        }
        return string.Join(",", names.Distinct());
    }
}
