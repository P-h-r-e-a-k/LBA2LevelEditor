using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LBA2LevelEditor;
using LBA2LevelEditor.LbaScript;
using LBA2LevelEditor.Scenes;
using LBA2LevelEditor.Terrain;

namespace ScriptRoundTrip;

// Data-mines every retail LBA2 scene script for uses of ListVarGame (the "game flags"): each SET / ADD / SUB, every
// comparison in an IF / SWITCH, the track-label transfers, the inventory opcodes that name a var by number, and the
// island decor objects whose visibility hangs off a var. Each event keeps the guard conditions that enclose it and the
// dialogue ids around it so flags can be named from what the game does with them.
//
//   gameflags <retail game dir> <output dir>
internal static partial class GameFlagMining
{
    private static readonly string[] IslandNames =
        { "citadel", "sendell", "desert", "emeraude", "otringal", "celebrat", "platform", "mosquibe", "knartas", "ilotcx", "ascence", "souscelb" };

    private const int FlagMoney = 8, FlagClover = 251, FlagChapter = 253;

    private sealed class Event
    {
        public int Var;
        public string Kind = "";      // SET ADD SUB TEST CASE TRACK_TO_VAR VAR_TO_TRACK USE_INV FOUND STATE_INV SET_USED
        public string Test = "";      // for TEST / CASE
        public int Value;
        public int Scene, Actor, Comp, Offset;
        public string Guards = "";
        public List<int> Msgs = new();
    }

    private sealed class TextBank
    {
        private readonly Dictionary<int, string> texts = new();
        public string Get(int id) => texts.TryGetValue(id, out var t) ? t : "";

        public static TextBank Load(HqrArchive text, int language, int file)
        {
            var bank = new TextBank();
            var order = language * 30 + file * 2;
            if (!text.IsValid(order) || !text.IsValid(order + 1)) return bank;
            var ids = text.Read(order);
            var data = text.Read(order + 1);
            var count = ids.Length / 2;
            for (var i = 0; i < count; i++)
            {
                int off0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2));
                int off1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2 + 2));
                if (off1 <= off0 || off1 > data.Length) continue;
                var sb = new StringBuilder();
                for (var p = off0 + 1; p < off1 - 1; p++)
                {
                    var c = data[p];
                    if (c == 0) break;
                    sb.Append(c < 32 ? (c == 1 ? '|' : ' ') : (char)c);
                }
                bank.texts[BinaryPrimitives.ReadUInt16LittleEndian(ids.AsSpan(i * 2))] = sb.ToString().Replace('\u00a0', ' ');
            }
            return bank;
        }
    }

    public static int Run(string[] args)
    {
        var root = args.Length > 1 ? args[1] : @"E:\GOG Games\LBA2 - Death rooms";
        var outDir = args.Length > 2 ? args[2] : Path.Combine(Environment.CurrentDirectory, "gameflags-out");
        Directory.CreateDirectory(outDir);

        var scenePath = Path.Combine(root, "SCENE.HQR");
        var archive = HqrArchive.Open(scenePath);
        var entries = HqrArchive.CountEntries(scenePath);
        var text = HqrArchive.Open(Path.Combine(root, "TEXT.HQR"));
        var language = DetectEnglish(text);
        Console.WriteLine($"TEXT.HQR English language index = {language}");

        var sceneNames = ReadHqd(@"E:\dump\LBA2LevelEditor\Assets\FileDesc\SCENE2.HQD");
        var bodyNames = ReadHqd(@"E:\dump\LBA2LevelEditor\Assets\FileDesc\BODY2.HQD");
        var banks = new Dictionary<int, TextBank>();
        TextBank Bank(int island)
        {
            if (!banks.TryGetValue(island, out var b)) banks[island] = b = TextBank.Load(text, language, 3 + island);
            return b;
        }

        var events = new List<Event>();
        var sceneInfo = new Dictionary<int, (int Island, int Mode, string Name)>();
        var actorInfo = new Dictionary<(int, int), string>();
        var decodeFailures = 0;

        for (var entry = 1; entry < entries; entry++)
        {
            if (!archive.IsValid(entry)) continue;
            var scene = entry - 1;
            var model = SceneSerializer.Parse(SceneGame.Lba2, archive.Read(entry));
            sceneInfo[scene] = (model.Island, model.CubeMode, sceneNames.ElementAtOrDefault(entry + 1) ?? "");
            for (var ai = 0; ai < model.Actors.Count; ai++)
            {
                var a = model.Actors[ai];
                actorInfo[(scene, ai)] = ai == 0 ? "hero" : a.IsSprite ? $"sprite {a.Sprite}" : $"ent {a.Entity}/body {a.Body}";
                if (a.Life.Length == 0) continue;
                var code = Bytecode.DecodeLife(a.Life, out var error);
                if (error is not null) decodeFailures++;
                MineActor(code, scene, ai, events);
            }
        }
        Console.WriteLine($"scenes={sceneInfo.Count} events={events.Count} lifeDecodeFailures={decodeFailures}");

        // Island decors that hide on a var.
        var decorRows = new List<(string Island, int Cube, int Index, int Var, bool Negated, int Body, int X, int Y, int Z)>();
        foreach (var name in IslandNames.Append("citabau").Append("celebra2"))
        {
            var path = Path.Combine(root, name.ToUpperInvariant() + ".ILE");
            if (!File.Exists(path)) continue;
            var island = IslandFile.Load(path);
            foreach (var (cubeId, cube) in island.Cubes)
                for (var i = 0; i < cube.Decors.Count; i++)
                {
                    var v = cube.Decors[i].Beta >> 16;
                    if (v == 0) continue;
                    var d = cube.Decors[i];
                    decorRows.Add((name, cubeId, i, Math.Abs(v), v < 0, d.Body & 0xFFFF, d.X, d.Y, d.Z));
                }
        }
        Console.WriteLine($"decors gated on a var = {decorRows.Count}");

        var trainer = args.Length > 3 ? args[3] : @"E:\Users\Matt\source\repos\LBATrainer\files\languages\ENG\LBA2\quests.xml";
        if (File.Exists(trainer)) WriteTrainerComparison(Path.Combine(outDir, "trainer_vs_mined.txt"), trainer, events);
        var curatedPath = args.Length > 4 ? args[4] : @"E:\dump\LBA2LevelEditor\tools\ScriptRoundTrip\gameflags_curated.txt";
        WriteDocs(outDir, trainer, curatedPath, events, sceneInfo, decorRows);
        WriteSanity(Path.Combine(outDir, "sanity.txt"), trainer, events, sceneInfo, decorRows);
        WriteDigest(Path.Combine(outDir, "digest.txt"), events, sceneInfo, actorInfo, Bank);
        WriteEvents(Path.Combine(outDir, "events.txt"), events, sceneInfo, actorInfo, Bank);
        WriteSummary(Path.Combine(outDir, "summary.txt"), events, sceneInfo, decorRows);
        WriteJson(Path.Combine(outDir, "events.json"), events, sceneInfo, actorInfo, decorRows);
        Console.WriteLine($"wrote {outDir}");
        return 0;
    }

    // ---- per-actor mining ---------------------------------------------------------------------------------------

    private static void MineActor(List<Instr> code, int scene, int actor, List<Event> events)
    {
        var comp = 0;
        var compStart = 0;
        var conds = new List<Instr>();                         // conditions seen in this comportement
        var switches = new Stack<(byte Func, int Arg)>();
        var cases = new List<(Instr Case, byte Func, int Arg)>();

        for (var i = 0; i < code.Count; i++)
        {
            var ins = code[i];
            var def = Opcodes.Life(ins.Op)!;

            if (def.Form == LifeForm.Cond)
            {
                conds.Add(ins);
                var v = VarOf(ins.Func, ins.FuncArg);
                if (v >= 0) Add(events, ins, v, "TEST", Opcodes.TestSymbols[ins.Test], ins.Value, scene, actor, comp, code, i, compStart, conds, cases, switches);
                continue;
            }
            if (def.Form == LifeForm.Switch) { switches.Push((ins.Func, ins.FuncArg)); continue; }
            if (def.Form == LifeForm.Case)
            {
                if (switches.Count > 0)
                {
                    var (f, arg) = switches.Peek();
                    cases.Add((ins, f, arg));
                    var v = VarOf(f, arg);
                    if (v >= 0) Add(events, ins, v, "CASE", Opcodes.TestSymbols[ins.Test], ins.Value, scene, actor, comp, code, i, compStart, conds, cases, switches);
                }
                continue;
            }

            switch (ins.Op)
            {
                case 118: if (switches.Count > 0) switches.Pop(); break;                                   // END_SWITCH
                case 35: comp++; compStart = i + 1; conds.Clear(); cases.Clear(); break;                   // END_COMPORTEMENT
                case 36: Add(events, ins, (int)ins.A[0], "SET", "", (int)ins.A[1], scene, actor, comp, code, i, compStart, conds, cases, switches); break;
                case 128: Add(events, ins, (int)ins.A[0], "ADD", "", (int)ins.A[1], scene, actor, comp, code, i, compStart, conds, cases, switches); break;
                case 129: Add(events, ins, (int)ins.A[0], "SUB", "", (int)ins.A[1], scene, actor, comp, code, i, compStart, conds, cases, switches); break;
                case 101: Add(events, ins, (int)ins.A[0], "TRACK_TO_VAR", "", 0, scene, actor, comp, code, i, compStart, conds, cases, switches); break;
                case 102: Add(events, ins, (int)ins.A[0], "VAR_TO_TRACK", "", 0, scene, actor, comp, code, i, compStart, conds, cases, switches); break;
                case 45: Add(events, ins, FlagChapter, "ADD", "", 1, scene, actor, comp, code, i, compStart, conds, cases, switches); break;  // INC_CHAPTER
                case 40: Add(events, ins, FlagMoney, "SUB", "", (int)ins.A[0], scene, actor, comp, code, i, compStart, conds, cases, switches); break;
                case 136: Add(events, ins, FlagMoney, "ADD", "", (int)ins.A[0], scene, actor, comp, code, i, compStart, conds, cases, switches); break;
                case 46: Add(events, ins, (int)ins.A[0], "FOUND", "", 0, scene, actor, comp, code, i, compStart, conds, cases, switches); break;
                case 67: Add(events, ins, (int)ins.A[0], "SET_USED", "", 0, scene, actor, comp, code, i, compStart, conds, cases, switches); break;
                case 111: Add(events, ins, (int)ins.A[0], "STATE_INV", "", (int)ins.A[1], scene, actor, comp, code, i, compStart, conds, cases, switches); break;
                case 66: Add(events, ins, FlagClover, "INC_CLOVER_BOX", "", 0, scene, actor, comp, code, i, compStart, conds, cases, switches); break;  // INC_CLOVER_BOX (max, not the var)
            }
        }
    }

    // The var a condition (LF_*) reads: VAR_GAME(n) -> n, CHAPTER -> 253, USE_INVENTORY(n) -> n, NB_GOLD_PIECES -> 8.
    private static int VarOf(byte func, int arg) => func switch
    {
        15 => arg,
        21 => FlagChapter,
        25 => arg,
        19 => FlagMoney,
        _ => -1,
    };

    private static void Add(List<Event> events, Instr ins, int var, string kind, string test, int value, int scene, int actor, int comp,
        List<Instr> code, int index, int compStart, List<Instr> conds, List<(Instr Case, byte Func, int Arg)> cases, Stack<(byte Func, int Arg)> switches)
    {
        if (kind == "TEST" && ins.Func == 25) kind = "USE_INV";
        var e = new Event { Var = var, Kind = kind, Test = test, Value = value, Scene = scene, Actor = actor, Comp = comp, Offset = ins.Offset };

        // Guards: every condition of this comportement whose jump range covers the instruction (the instruction itself excluded).
        var guards = new List<string>();
        foreach (var c in conds)
            if (!ReferenceEquals(c, ins) && c.Offset < ins.Offset && ins.Offset < c.Target) guards.Add(Describe(c));
        foreach (var (cs, f, arg) in cases)
            if (!ReferenceEquals(cs, ins) && cs.Offset < ins.Offset && ins.Offset < cs.Target) guards.Add($"{FuncName(f, arg)} {Opcodes.TestSymbols[cs.Test]} {cs.Value}");
        e.Guards = string.Join(" && ", guards);

        // Dialogue ids in a small window around the event, same comportement.
        for (var k = Math.Max(compStart, index - 6); k < Math.Min(code.Count, index + 7); k++)
        {
            var m = MessageId(code[k]);
            if (m >= 0 && !e.Msgs.Contains(m)) e.Msgs.Add(m);
        }
        events.Add(e);
    }

    private static int MessageId(Instr i) => i.Op switch
    {
        25 or 68 or 69 or 78 or 88 => (int)i.A[0],
        44 or 91 or 104 => (int)i.A[1],
        154 => (int)i.A[3],
        _ => -1,
    };

    private static string FuncName(byte func, int arg)
    {
        var cd = Opcodes.Cond(func)!;
        return cd.OperandName is null ? cd.Name : $"{cd.Name}({arg})";
    }

    private static string Describe(Instr c) => $"{FuncName(c.Func, c.FuncArg)} {Opcodes.TestSymbols[c.Test]} {c.Value}";

    // ---- output -------------------------------------------------------------------------------------------------

    private static string SceneLabel(int scene, Dictionary<int, (int Island, int Mode, string Name)> info)
        => info.TryGetValue(scene, out var s) ? $"scene {scene} [{s.Name}]" : $"scene {scene}";

    private static void WriteEvents(string path, List<Event> events, Dictionary<int, (int Island, int Mode, string Name)> sceneInfo,
        Dictionary<(int, int), string> actorInfo, Func<int, TextBank> bank)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var g in events.GroupBy(e => e.Var).OrderBy(g => g.Key))
        {
            w.WriteLine($"==== VAR {g.Key} ====");
            foreach (var e in g.OrderBy(e => e.Scene).ThenBy(e => e.Actor).ThenBy(e => e.Offset))
            {
                var what = e.Kind switch
                {
                    "SET" => $"SET = {e.Value}",
                    "ADD" => $"ADD {e.Value}",
                    "SUB" => $"SUB {e.Value}",
                    "TEST" => $"IF {e.Test} {e.Value}",
                    "CASE" => $"CASE {e.Test} {e.Value}",
                    "STATE_INV" => $"STATE_INVENTORY model {e.Value}",
                    _ => e.Kind,
                };
                var actor = actorInfo.GetValueOrDefault((e.Scene, e.Actor), "?");
                w.WriteLine($"  {what,-22} {SceneLabel(e.Scene, sceneInfo)} actor {e.Actor} ({actor}) comp {e.Comp} @{e.Offset}");
                if (e.Guards.Length > 0) w.WriteLine($"      when {e.Guards}");
                if (e.Msgs.Count > 0 && sceneInfo.TryGetValue(e.Scene, out var si))
                {
                    var b = bank(si.Island);
                    foreach (var m in e.Msgs.Take(4))
                    {
                        var t = b.Get(m);
                        w.WriteLine($"      msg {m}: {(t.Length > 110 ? t[..110] + "..." : t)}");
                    }
                }
            }
            w.WriteLine();
        }
    }

    private static void WriteSummary(string path, List<Event> events, Dictionary<int, (int Island, int Mode, string Name)> sceneInfo,
        List<(string Island, int Cube, int Index, int Var, bool Negated, int Body, int X, int Y, int Z)> decors)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        for (var v = 0; v < 256; v++)
        {
            var ev = events.Where(e => e.Var == v).ToList();
            var dec = decors.Where(d => d.Var == v).ToList();
            if (ev.Count == 0 && dec.Count == 0) { w.WriteLine($"var {v,3}: (no script use)"); continue; }

            string Vals(IEnumerable<Event> es) => string.Join(",", es.Select(e => e.Value).Distinct().OrderBy(x => x));
            var sets = ev.Where(e => e.Kind == "SET").ToList();
            var adds = ev.Where(e => e.Kind == "ADD").ToList();
            var subs = ev.Where(e => e.Kind == "SUB").ToList();
            var tests = ev.Where(e => e.Kind is "TEST" or "CASE").ToList();
            var line = new List<string>();
            if (sets.Count > 0) line.Add($"set{{{Vals(sets)}}}x{sets.Count}");
            if (adds.Count > 0) line.Add($"add{{{Vals(adds)}}}x{adds.Count}");
            if (subs.Count > 0) line.Add($"sub{{{Vals(subs)}}}x{subs.Count}");
            if (tests.Count > 0) line.Add("tested{" + string.Join(",", tests.Select(t => t.Test + t.Value).Distinct().OrderBy(x => x, StringComparer.Ordinal)) + $"}}x{tests.Count}");
            foreach (var k in new[] { "TRACK_TO_VAR", "VAR_TO_TRACK", "USE_INV", "FOUND", "STATE_INV", "SET_USED", "INC_CLOVER_BOX" })
            {
                var n = ev.Count(e => e.Kind == k);
                if (n > 0) line.Add($"{k}x{n}");
            }
            if (dec.Count > 0) line.Add($"decors x{dec.Count}");
            var scenes = ev.Select(e => e.Scene).Distinct().OrderBy(x => x).ToList();
            w.WriteLine($"var {v,3}: {string.Join(" ", line)} | scenes {string.Join(",", scenes)}");
        }

        w.WriteLine();
        w.WriteLine("Decors gated on a var (island, cube, decor index, var, hidden-when, body, x y z):");
        foreach (var d in decors.OrderBy(d => d.Var).ThenBy(d => d.Island).ThenBy(d => d.Cube))
            w.WriteLine($"  var {d.Var,3} {(d.Negated ? "visible only when set  " : "hidden when set       ")} {d.Island} cube {d.Cube} decor {d.Index} body {d.Body} ({d.X},{d.Y},{d.Z})");
    }

    private static void WriteJson(string path, List<Event> events, Dictionary<int, (int Island, int Mode, string Name)> sceneInfo,
        Dictionary<(int, int), string> actorInfo,
        List<(string Island, int Cube, int Index, int Var, bool Negated, int Body, int X, int Y, int Z)> decors)
    {
        var doc = new
        {
            scenes = sceneInfo.ToDictionary(s => s.Key.ToString(), s => new { island = s.Value.Island, mode = s.Value.Mode, name = s.Value.Name }),
            events = events.Select(e => new { e.Var, e.Kind, e.Test, e.Value, e.Scene, e.Actor, e.Comp, e.Offset, e.Guards, e.Msgs, actor = actorInfo.GetValueOrDefault((e.Scene, e.Actor), "") }),
            decors = decors.Select(d => new { island = d.Island, cube = d.Cube, index = d.Index, var = d.Var, negated = d.Negated, body = d.Body, d.X, d.Y, d.Z }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
    }

    // LBATrainer's quests.xml keys each quest by process memory offset; the list starts at ListVarGame[0] = 0x57BF3 and each
    // var is an S16, so index = (offset - 0x57BF3) / 2.
    private static List<(int Index, string Name, List<(int Value, string Label)> Values)> ReadTrainer(string path)
    {
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(File.ReadAllText(path, Encoding.Latin1).Replace("encoding=\"iso-8859-1\"", "").Replace("<?xml version=\"1.0\" ?>", ""));
        var list = new List<(int, string, List<(int, string)>)>();
        foreach (System.Xml.XmlNode q in doc.SelectNodes("//quest")!)
        {
            var off = Convert.ToInt32(q.SelectSingleNode("memoryOffset")!.InnerText.Trim(), 16);
            var values = new List<(int, string)>();
            foreach (System.Xml.XmlNode s in q.SelectNodes("subquests/subquest")!)
                values.Add((int.Parse(s.SelectSingleNode("value")!.InnerText.Trim()), s.SelectSingleNode("name")!.InnerText.Trim()));
            list.Add(((off - 0x57BF3) / 2, q.SelectSingleNode("name")!.InnerText.Trim(), values));
        }
        return list;
    }

    private static void WriteTrainerComparison(string path, string trainerXml, List<Event> events)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var (index, name, values) in ReadTrainer(trainerXml))
        {
            var ev = events.Where(e => e.Var == index).ToList();
            var known = values.Select(v => v.Value).ToHashSet();
            var setV = ev.Where(e => e.Kind == "SET").Select(e => e.Value).Distinct().OrderBy(x => x).ToList();
            var testV = ev.Where(e => e.Kind is "TEST" or "CASE").Select(e => e.Value).Distinct().OrderBy(x => x).ToList();
            var addSub = ev.Where(e => e.Kind is "ADD" or "SUB").Select(e => e.Kind + e.Value).Distinct().ToList();
            var extra = setV.Concat(testV).Where(v => !known.Contains(v)).Distinct().OrderBy(x => x).ToList();
            var status = ev.Count == 0 ? "NO-SCRIPT-USE" : extra.Count > 0 ? "EXTRA-VALUES" : "ok";
            w.WriteLine($"[{index}] {name} => {status}  trainer={{{string.Join(",", known.OrderBy(x => x))}}} set={{{string.Join(",", setV)}}} test={{{string.Join(",", testV)}}} addsub={{{string.Join(",", addSub)}}} extra={{{string.Join(",", extra)}}}");
        }
    }

    // One line per var: trainer name, values it is set to, and where (scene number + short place name) it is written / read.
    private static void WriteSanity(string path, string trainerXml, List<Event> events, Dictionary<int, (int Island, int Mode, string Name)> sceneInfo,
        List<(string Island, int Cube, int Index, int Var, bool Negated, int Body, int X, int Y, int Z)> decors)
    {
        var trainer = File.Exists(trainerXml) ? ReadTrainer(trainerXml).ToDictionary(t => t.Index) : new();
        string Short(int scene)
        {
            if (!sceneInfo.TryGetValue(scene, out var s)) return scene.ToString();
            var n = s.Name;
            var p = n.IndexOf(" (room", StringComparison.Ordinal);
            if (p > 0) n = n[..p];
            return $"{scene}:{n}";
        }
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        for (var v = 0; v < 256; v++)
        {
            var ev = events.Where(e => e.Var == v).ToList();
            var tn = trainer.TryGetValue(v, out var t) ? t.Name : "-";
            w.WriteLine($"[{v}] trainer: {tn}");
            var writers = ev.Where(e => e.Kind is "SET" or "ADD" or "SUB" or "TRACK_TO_VAR" or "FOUND").GroupBy(e => e.Scene).OrderBy(g => g.Key).ToList();
            var readers = ev.Where(e => e.Kind is "TEST" or "CASE" or "USE_INV" or "VAR_TO_TRACK").Select(e => e.Scene).Distinct().OrderBy(x => x).ToList();
            if (writers.Count > 0)
                w.WriteLine("    written: " + string.Join(" ; ", writers.Take(14).Select(g => Short(g.Key) + " " + string.Join("/", g.Select(e => e.Kind == "SET" ? $"={e.Value}" : e.Kind == "ADD" ? $"+{e.Value}" : e.Kind == "SUB" ? $"-{e.Value}" : e.Kind).Distinct()))) + (writers.Count > 14 ? $" ; ...(+{writers.Count - 14} scenes)" : ""));
            var readOnly = readers.Except(writers.Select(g => g.Key)).ToList();
            if (readOnly.Count > 0) w.WriteLine("    read only in: " + string.Join(" ; ", readOnly.Take(10).Select(Short)) + (readOnly.Count > 10 ? $" ; ...(+{readOnly.Count - 10})" : ""));
            foreach (var g in decors.Where(d => d.Var == v).GroupBy(d => (d.Island, d.Cube, d.Negated)).Take(6))
                w.WriteLine($"    decor: {g.Key.Island} cube {g.Key.Cube} x{g.Count()} ({(g.Key.Negated ? "shown only when set" : "hidden when set")}) bodies {string.Join(",", g.Select(d => d.Body).Distinct())}");
        }
    }

    // Compact write-only digest for reading: one line per SET/ADD/SUB/TRACK/FOUND event outside the demo scenes (194+), with
    // the enclosing guards and up to two nearby dialogue lines.
    private static void WriteDigest(string path, List<Event> events, Dictionary<int, (int Island, int Mode, string Name)> sceneInfo,
        Dictionary<(int, int), string> actorInfo, Func<int, TextBank> bank)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        for (var v = 0; v < 256; v++)
        {
            var ev = events.Where(e => e.Var == v && e.Scene < FirstDemoScene && e.Kind is "SET" or "ADD" or "SUB" or "TRACK_TO_VAR" or "FOUND" or "STATE_INV" or "SET_USED").OrderBy(e => e.Scene).ThenBy(e => e.Actor).ThenBy(e => e.Offset).ToList();
            w.WriteLine($"## VAR {v}");
            foreach (var e in ev)
            {
                var what = e.Kind switch { "SET" => $"={e.Value}", "ADD" => $"+{e.Value}", "SUB" => $"-{e.Value}", "STATE_INV" => $"state{e.Value}", _ => e.Kind };
                var guards = e.Guards.Length > 170 ? "..." + e.Guards[^170..] : e.Guards;
                var msgs = "";
                if (e.Msgs.Count > 0 && sceneInfo.TryGetValue(e.Scene, out var si))
                {
                    var b = bank(si.Island);
                    msgs = " || " + string.Join(" / ", e.Msgs.Take(2).Select(m => { var t = b.Get(m); return t.Length > 75 ? t[..75] + "…" : t; }));
                }
                w.WriteLine($"  s{e.Scene} a{e.Actor} c{e.Comp} {what} <- {guards}{msgs}");
            }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static List<string> ReadHqd(string path) => File.Exists(path) ? File.ReadAllLines(path, Encoding.Latin1).ToList() : new();

    // Picks the TEXT.HQR language whose game bank (file 2) reads as English.
    private static int DetectEnglish(HqrArchive text)
    {
        var best = 0;
        var bestScore = -1;
        for (var lang = 0; lang < 8; lang++)
        {
            var bank = TextBank.Load(text, lang, 3);
            var score = 0;
            foreach (var id in Enumerable.Range(0, 400))
            {
                var t = bank.Get(id);
                if (t.Contains(" the ") || t.Contains(" you ") || t.Contains(" and ")) score++;
            }
            if (score > bestScore) { bestScore = score; best = lang; }
        }
        return best;
    }
}
