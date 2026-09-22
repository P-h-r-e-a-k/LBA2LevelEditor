using System.Text;
using LBA2LevelEditor;
using LBA2LevelEditor.LbaScript;
using LBA2LevelEditor.Scenes;

namespace ScriptRoundTrip;

// Data-mines every retail LBA2 scene for things that react to the Horn of the Blue Triton (inventory slot 22,
// behaviour C_CONQUE = 6). The engine's horn throws life hearts (ThrowConque -> ExtraBonus); a heart that touches any
// living, non-sprite actor adds LifePoint += Divers * 5, so the scripts can only notice it through the hero's
// behaviour, the item slot, or the actor's own life points. This lists all three kinds of evidence.
//
//   horn <retail game dir> <output dir>
internal static partial class GameFlagMining
{
    private const int FlagConque = 22, CConque = 6;

    public static int RunHorn(string[] args)
    {
        var root = args.Length > 1 ? args[1] : @"E:\GOG Games\LBA2 - Death rooms";
        var outDir = args.Length > 2 ? args[2] : Path.Combine(Environment.CurrentDirectory, "horn-out");
        Directory.CreateDirectory(outDir);

        var scenePath = Path.Combine(root, "SCENE.HQR");
        var archive = HqrArchive.Open(scenePath);
        var entries = HqrArchive.CountEntries(scenePath);
        var text = HqrArchive.Open(Path.Combine(root, "TEXT.HQR"));
        var language = DetectEnglish(text);
        var sceneNames = ReadHqd(@"E:\dump\LBA2LevelEditor\Assets\FileDesc\SCENE2.HQD");
        var banks = new Dictionary<int, TextBank>();
        TextBank Bank(int island)
        {
            if (!banks.TryGetValue(island, out var b)) banks[island] = b = TextBank.Load(text, language, 3 + island);
            return b;
        }

        var behaviour = new List<string>();
        var slot = new List<string>();
        var life = new List<string>();
        var lifeActors = new HashSet<(int, int)>();

        for (var entry = 1; entry < entries; entry++)
        {
            if (!archive.IsValid(entry)) continue;
            var scene = entry - 1;
            var raw = archive.Read(entry);
            var model = SceneSerializer.Parse(SceneGame.Lba2, raw);
            var label = $"s{scene} [{sceneNames.ElementAtOrDefault(entry + 1) ?? ""}]";
            var bank = Bank(model.Island);

            for (var ai = 0; ai < model.Actors.Count; ai++)
            {
                var a = model.Actors[ai];
                if (a.Life.Length == 0) continue;
                var code = Bytecode.DecodeLife(a.Life, out _);
                var who = ai == 0 ? "hero" : a.IsSprite ? $"sprite {a.Sprite}" : $"ent {a.Entity}/body {a.Body}";
                var comp = 0;
                for (var i = 0; i < code.Count; i++)
                {
                    var ins = code[i];
                    var def = Opcodes.Life(ins.Op)!;
                    string Ctx() => $"{label} actor {ai} ({who}) comp {comp} @{ins.Offset}: ";
                    string Guards()
                    {
                        var g = new List<string>();
                        foreach (var c in code)
                            if (c.Offset < ins.Offset && ins.Offset < c.Target && Opcodes.Life(c.Op)!.Form == LifeForm.Cond)
                                g.Add($"{FuncName(c.Func, c.FuncArg)} {Opcodes.TestSymbols[c.Test]} {c.Value}");
                        return g.Count == 0 ? "" : "  [inside " + string.Join(" && ", g) + "]";
                    }
                    string Says()
                    {
                        var ids = new List<int>();
                        for (var k = Math.Max(0, i - 6); k < Math.Min(code.Count, i + 8); k++)
                        {
                            var m = MessageId(code[k]);
                            if (m >= 0 && !ids.Contains(m)) ids.Add(m);
                        }
                        return ids.Count == 0 ? "" : "\n      says: " + string.Join(" | ", ids.Select(m => $"#{m} \"{bank.Get(m)}\""));
                    }

                    if (def.Form == LifeForm.Cond)
                    {
                        var f = ins.Func;
                        if (f == 20) behaviour.Add(Ctx() + $"IF COMPORTEMENT_HERO {Opcodes.TestSymbols[ins.Test]} {ins.Value}{Guards()}{Says()}");
                        else if (((f == 8 && ins.FuncArg == 0) || (f == 7 && ai == 0)) && ins.Value == 15)
                            behaviour.Add(Ctx() + $"IF hero ANIM {Opcodes.TestSymbols[ins.Test]} 15 (GEN_ANIM_LANCE: horn / magic ball throw){Guards()}{Says()}");
                        else if (f == 25 && ins.FuncArg == FlagConque) slot.Add(Ctx() + $"IF USE_INVENTORY(22) {Opcodes.TestSymbols[ins.Test]} {ins.Value}{Guards()}{Says()}");
                        else if (f == 15 && ins.FuncArg == FlagConque) slot.Add(Ctx() + $"IF VAR_GAME(22) {Opcodes.TestSymbols[ins.Test]} {ins.Value}{Guards()}{Says()}");
                        else if (f == 16 || f == 17)
                        {
                            lifeActors.Add((scene, ai));
                            var subject = f == 16 ? "own" : $"actor {ins.FuncArg}";
                            life.Add(Ctx() + $"IF LIFE_POINT({subject}) {Opcodes.TestSymbols[ins.Test]} {ins.Value}{Guards()}{Says()}");
                        }
                        continue;
                    }
                    if (def.Form == LifeForm.Switch)
                    {
                        if (ins.Func == 15 && ins.FuncArg == FlagConque) slot.Add(Ctx() + "SWITCH VAR_GAME(22)");
                        if (ins.Func == 20) behaviour.Add(Ctx() + "SWITCH COMPORTEMENT_HERO");
                        if (ins.Func == 16 || ins.Func == 17) { lifeActors.Add((scene, ai)); life.Add(Ctx() + $"SWITCH LIFE_POINT({(ins.Func == 16 ? "own" : "actor " + ins.FuncArg)})"); }
                        continue;
                    }
                    switch (ins.Op)
                    {
                        case 35: comp++; break;
                        case 30: behaviour.Add(Ctx() + $"COMPORTEMENT_HERO({ins.A[0]}) (set hero behaviour){Guards()}{Says()}"); break;
                        case 46 or 67 or 111 when ins.A[0] == FlagConque:
                            slot.Add(Ctx() + $"{def.Name}(22{(ins.Op == 111 ? ", " + ins.A[1] : "")}){Guards()}{Says()}"); break;
                        case 36 or 128 or 129 when ins.A[0] == FlagConque:
                            slot.Add(Ctx() + $"{def.Name}(22, {ins.A[1]}){Guards()}{Says()}"); break;
                        case 61 or 62 or 110:
                            life.Add(Ctx() + $"{def.Name}(actor {ins.A[0]}, {ins.A[1]}){Guards()}"); break;
                    }
                }
            }
        }

        File.WriteAllLines(Path.Combine(outDir, "behaviour.txt"), behaviour, new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(outDir, "slot22.txt"), slot, new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(outDir, "life.txt"), life, new UTF8Encoding(false));
        Console.WriteLine($"behaviour={behaviour.Count} slot22={slot.Count} life={life.Count} lifeActors={lifeActors.Count} -> {outDir}");
        return 0;
    }

    // horntext <retail dir> <regex>: every English dialogue line matching the pattern, and the scene actors that speak it.
    public static int RunHornText(string[] args)
    {
        var root = args[1];
        var rx = new System.Text.RegularExpressions.Regex(args[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var scenePath = Path.Combine(root, "SCENE.HQR");
        var archive = HqrArchive.Open(scenePath);
        var entries = HqrArchive.CountEntries(scenePath);
        var text = HqrArchive.Open(Path.Combine(root, "TEXT.HQR"));
        var language = DetectEnglish(text);
        var sceneNames = ReadHqd(@"E:\dump\LBA2LevelEditor\Assets\FileDesc\SCENE2.HQD");
        var banks = new Dictionary<int, TextBank>();
        TextBank Bank(int island)
        {
            if (!banks.TryGetValue(island, out var b)) banks[island] = b = TextBank.Load(text, language, 3 + island);
            return b;
        }

        for (var entry = 1; entry < entries; entry++)
        {
            if (!archive.IsValid(entry)) continue;
            var scene = entry - 1;
            var model = SceneSerializer.Parse(SceneGame.Lba2, archive.Read(entry));
            var bank = Bank(model.Island);
            for (var ai = 0; ai < model.Actors.Count; ai++)
            {
                var a = model.Actors[ai];
                if (a.Life.Length == 0) continue;
                var seen = new HashSet<int>();
                foreach (var ins in Bytecode.DecodeLife(a.Life, out _))
                {
                    var m = MessageId(ins);
                    if (m < 0 || !seen.Add(m)) continue;
                    var t = bank.Get(m);
                    if (t.Length > 0 && rx.IsMatch(t))
                        Console.WriteLine($"s{scene} [{sceneNames.ElementAtOrDefault(entry + 1) ?? ""}] actor {ai} ({(ai == 0 ? "hero" : a.IsSprite ? "sprite " + a.Sprite : $"ent {a.Entity}")}) #{m}: {t}");
                }
            }
        }
        return 0;
    }

    // horndump <retail dir> <scene:actor>...: the actor's stats and its life script as C text.
    public static int RunHornDump(string[] args)
    {
        var archive = HqrArchive.Open(Path.Combine(args[1], "SCENE.HQR"));
        for (var k = 2; k < args.Length; k++)
        {
            var parts = args[k].Split(':');
            var scene = int.Parse(parts[0]);
            var actor = int.Parse(parts[1]);
            var raw = archive.Read(scene + 1);
            var a = SceneSerializer.Parse(SceneGame.Lba2, raw).Actors[actor];
            Console.WriteLine($"==== s{scene} actor {actor}: {(a.IsSprite ? $"sprite {a.Sprite}" : $"ent {a.Entity}/body {a.Body}")} life {a.LifePoints} armor {a.Armor} hitforce {a.HitForce} flags 0x{a.Flags:X} option 0x{a.OptionFlags:X} at ({a.X},{a.Y},{a.Z})");
            Console.WriteLine(SceneScripts.Load(raw, scene).GetText(actor, ScriptKind.Life));
        }
        return 0;
    }
}
