using System.Text;
using System.Text.Json;

namespace ScriptRoundTrip;

// Builds docs/LBA2_GAME_FLAGS.md and docs/lba2_game_flags.json from three layers, later ones winning:
//   1. LBATrainer's quests.xml (names + observed values for indices 40-255),
//   2. gameflags_curated.txt (names, kinds, corrections and notes written from the engine source and the scripts),
//   3. the evidence mined from the retail scripts: where each var is set / read, which values appear, which decors hang off it.
internal static partial class GameFlagMining
{
    private const int FirstDemoScene = 193;

    private sealed class Curated
    {
        public int Id;
        public string Symbol = "", Kind = "", Name = "", Values = "", Notes = "";
    }

    private sealed class Flag
    {
        public int Id;
        public string Symbol = "", Kind = "unused", Name = "", Notes = "", Source = "";
        public List<(string Value, string Text)> Values = new();
        public List<string> WrittenBy = new();      // "s27 School of Magic (=4,=3)"
        public List<int> WrittenScenes = new();
        public List<int> ReadScenes = new();
        public List<string> Decors = new();
        public List<int> Undocumented = new();
        public bool DemoOnly, AnyUse;
    }

    private static List<Curated> ReadCurated(string path)
    {
        var list = new List<Curated>();
        if (!File.Exists(path)) return list;
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split('|');
            if (p.Length != 6) throw new InvalidDataException($"curated line needs exactly 6 pipe-separated fields ({p.Length} found): {line}");
            var range = p[0].Trim().Split('-');
            var from = int.Parse(range[0]);
            var to = range.Length > 1 ? int.Parse(range[1]) : from;
            for (var id = from; id <= to; id++)
                list.Add(new Curated { Id = id, Symbol = p[1].Trim(), Kind = p[2].Trim(), Name = p[3].Trim(), Values = p[4].Trim(), Notes = p[5].Trim() });
        }
        return list;
    }

    private static List<(string Value, string Text)> ParseValues(string values)
    {
        var list = new List<(string, string)>();
        if (values.Length == 0 || values == "-") return list;
        foreach (var part in values.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            list.Add((part[..eq].Trim(), part[(eq + 1)..].Trim()));
        }
        return list;
    }

    private static string ShortScene(int scene, Dictionary<int, (int Island, int Mode, string Name)> info)
    {
        if (!info.TryGetValue(scene, out var s)) return $"s{scene}";
        var n = s.Name;
        var p = n.IndexOf(" (room", StringComparison.Ordinal);
        if (p > 0) n = n[..p];
        return $"s{scene} {n}";
    }

    private static void WriteDocs(string outDir, string trainerXml, string curatedPath, List<Event> events,
        Dictionary<int, (int Island, int Mode, string Name)> sceneInfo,
        List<(string Island, int Cube, int Index, int Var, bool Negated, int Body, int X, int Y, int Z)> decors)
    {
        var trainer = File.Exists(trainerXml) ? ReadTrainer(trainerXml).ToDictionary(t => t.Index) : new();
        var curated = ReadCurated(curatedPath).ToDictionary(c => c.Id);
        var flags = new List<Flag>();

        for (var id = 0; id < 256; id++)
        {
            var f = new Flag { Id = id };
            trainer.TryGetValue(id, out var t);
            curated.TryGetValue(id, out var c);
            var ev = events.Where(e => e.Var == id).ToList();
            var dec = decors.Where(d => d.Var == id).ToList();
            f.AnyUse = ev.Count > 0 || dec.Count > 0;

            // ---- text layer
            f.Symbol = c?.Symbol ?? "";
            f.Name = !string.IsNullOrEmpty(c?.Name) ? c!.Name : t.Name ?? "";
            f.Notes = c?.Notes ?? "";
            if (c is not null && c.Values == "-") f.Values = new();
            else if (!string.IsNullOrEmpty(c?.Values)) f.Values = ParseValues(c!.Values);
            else if (t.Values is not null) f.Values = t.Values.Select(v => (v.Value.ToString(), v.Label)).ToList();
            f.Source = c is null ? (t.Name is not null ? "trainer" : "none")
                : string.IsNullOrEmpty(c.Name) && t.Name is not null ? "trainer+curated" : "curated";
            if (!string.IsNullOrEmpty(c?.Kind)) f.Kind = c!.Kind;
            else if (t.Name is not null) f.Kind = f.Values.Count > 2 ? "quest" : "flag";
            else if (f.AnyUse) f.Kind = "flag";
            if (f.Kind == "inventory" && f.Values.Count == 0) f.Values = new() { ("0", "not owned"), ("1", "owned") };
            if (f.Name.Length == 0) f.Name = f.AnyUse ? "(unnamed)" : "Unused";
            if (f.Name == "Unknown") f.Name = f.AnyUse ? "Unknown (in use, purpose unclear)" : "Unused";

            // ---- mined layer
            var main = ev.Where(e => e.Scene < FirstDemoScene).ToList();
            f.DemoOnly = ev.Count > 0 && main.Count == 0;
            foreach (var g in main.Where(e => e.Kind is "SET" or "ADD" or "SUB" or "TRACK_TO_VAR" or "FOUND" or "STATE_INV" or "SET_USED").GroupBy(e => e.Scene).OrderBy(g => g.Key))
            {
                var ops = g.Select(e => e.Kind switch
                {
                    "SET" => $"={e.Value}", "ADD" => $"+{e.Value}", "SUB" => $"-{e.Value}", "TRACK_TO_VAR" => "saves track label",
                    "FOUND" => "found-object", "STATE_INV" => $"icon state {e.Value}", _ => "marked used",
                }).Distinct().Take(8);
                f.WrittenBy.Add($"{ShortScene(g.Key, sceneInfo)} ({string.Join(", ", ops)})");
                f.WrittenScenes.Add(g.Key);
            }
            f.ReadScenes = main.Where(e => e.Kind is "TEST" or "CASE" or "USE_INV" or "VAR_TO_TRACK").Select(e => e.Scene).Distinct().OrderBy(x => x).ToList();
            foreach (var g in dec.GroupBy(d => (d.Island, d.Cube, d.Negated)).OrderBy(g => g.Key.Island).ThenBy(g => g.Key.Cube))
                f.Decors.Add($"{g.Key.Island.ToUpperInvariant()} cube {g.Key.Cube}: {g.Count()} decor(s), body {string.Join("/", g.Select(d => d.Body).Distinct().OrderBy(x => x))}, {(g.Key.Negated ? "visible only while set" : "hidden while set")}");

            if (f.Kind is "quest" or "flag")
            {
                var documented = f.Values.Select(v => v.Value).ToHashSet();
                f.Undocumented = ev.Where(e => e.Kind is "SET" or "TEST" or "CASE").Select(e => e.Value).Distinct()
                    .Where(v => !documented.Contains(v.ToString())).OrderBy(x => x).ToList();
                if (f.Values.Count == 0) f.Undocumented = new();
            }
            flags.Add(f);
        }

        WriteMarkdown(Path.Combine(outDir, "LBA2_GAME_FLAGS.md"), flags);
        WriteFlagsJson(Path.Combine(outDir, "lba2_game_flags.json"), flags);
        Console.WriteLine($"flags: {flags.Count(f => f.Source == "curated")} curated, {flags.Count(f => f.Source == "trainer")} trainer-only, {flags.Count(f => f.Source == "none" && f.AnyUse)} in use but unnamed, {flags.Count(f => !f.AnyUse && f.Kind == "unused")} unused");
    }

    private static void WriteMarkdown(string path, List<Flag> flags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LBA2 game flags (`ListVarGame`)");
        sb.AppendLine();
        sb.AppendLine("Little Big Adventure 2 keeps its story in one array of **256 signed 16-bit values**, `ListVarGame[0..255]` (`MAX_VARS_GAME`). It is saved with the game (`SAVEGAME.CPP`, first 512 bytes of the context) and is read and written by the life scripts of every scene. Unlike LBA1, where a game flag is a yes/no, an LBA2 flag is usually a **number**: a stage of a quest, a count, a track label, or a bitmask, so the meaning of a value matters as much as the index.");
        sb.AppendLine();
        sb.AppendLine("This list was compiled from four sources, and every entry says which one it rests on:");
        sb.AppendLine();
        sb.AppendLine("- **LBATrainer** `quests.xml` (names and the values its author observed for indices 40-255). Index = `(memoryOffset - 0x57BF3) / 2`; that mapping is confirmed against the trainer's own inventory list (Holomap = 0, Darts = 2, Blowgun = 23).");
        sb.AppendLine("- **The engine source** (`COMMON.H` `FLAG_*` constants, `GERELIFE.CPP`, `PERSO.CPP`, `INVENT.CPP`, `PLAYACF.CPP`, `WAGON.CPP`, `3DEXT/DECORS.CPP`, ...): what the engine itself does with an index.");
        sb.AppendLine("- **Every retail life script**: all 222 scenes of the original `SCENE.HQR` (6,000+ scripts) were decoded and each `SET_VAR_GAME`, `ADD_VAR_GAME`, `SUB_VAR_GAME`, `LF_VAR_GAME` test, `SWITCH`/`CASE`, `TRACK_TO_VAR_GAME`, `VAR_GAME_TO_TRACK`, `FOUND_OBJECT`, `STATE_INVENTORY` and `USE_INVENTORY` was recorded with its enclosing conditions and nearby dialogue (5,435 events).");
        sb.AppendLine("- **The island files** (`*.ILE`): a decor object hides or shows itself from a flag (`Beta >> 16`; positive = hidden while set, negative = visible only while set). 189 decors depend on a flag.");
        sb.AppendLine();
        sb.AppendLine("Each entry's `source` says where its name comes from: `curated` = named from the engine source and the scripts (the trainer's name, where there was one, was cross-checked against the values and scenes the scripts use, then kept or corrected); `trainer+curated` = the trainer's name and values were kept after the same cross-check, with notes added; `trainer` = the trainer's entry unchanged. The *Written by / Read in* lines are always mined from the scripts and are exact.");
        sb.AppendLine();
        sb.AppendLine("Scene numbers are `SCENE.HQR` numbers (entry - 1) written `s27`. Scenes 193 and up are the demo's copies; flags used only there are marked **demo only**.");
        sb.AppendLine();
        sb.AppendLine("## How the engine uses the array");
        sb.AppendLine();
        sb.AppendLine("| Mechanism | Detail |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| Life-script writes | `SET_VAR_GAME(var, value)`, `ADD_VAR_GAME`, `SUB_VAR_GAME` (saturating at the S16 limits). Writing var 8 also updates `NbGoldPieces` / `NbZlitosPieces`. |");
        sb.AppendLine("| Life-script reads | `LF_VAR_GAME(var)` (S16), `LF_CHAPTER` (= var 253), `LF_USE_INVENTORY(item)` (var = item slot), `LF_NB_GOLD_PIECES`. |");
        sb.AppendLine("| Inventory | slots 0-39 (`MAX_INVENTORY`) are `ListVarGame[slot]`; a quantity for the countable ones (`FLAG_INV_FEWER`: darts, kashes, penguins, gems), otherwise 0/1. `FOUND_OBJECT`, `STATE_INVENTORY` and `SET_USED_INVENTORY` take the slot number. |");
        sb.AppendLine("| Track labels | `TRACK_TO_VAR_GAME(var)` stores the actor's current track label, `VAR_GAME_TO_TRACK(var)` sends the actor to that label. Vars 40 (Zoe) and 80 (the wizard pedlar) are used this way. |");
        sb.AppendLine("| Decor visibility | island decors carry a var in the high 16 bits of `Beta`; the engine re-evaluates them when a cube loads (`FixeObjetsDecorsInvisibles`). |");
        sb.AppendLine("| Movies | vars 235-237 are a 48-bit mask of the ACF cut scenes already played. |");
        sb.AppendLine("| Cheats / console | the native engine's console has `vargame <n> [value]`, `flags [all]` (dumps the named flags) and `give <item> [n]`. |");
        sb.AppendLine();
        sb.AppendLine("## Index");
        sb.AppendLine();
        sb.AppendLine("Kinds: `inventory` item slot (0/1), `count` quantity, `quest` several stages, `flag` on/off, `track` an NPC's track label, `bits` bitmask, `decor` mainly gates island decor, `engine` used by engine code, `unused`.");
        sb.AppendLine();
        sb.AppendLine("| # | Symbol | Kind | Name |");
        sb.AppendLine("|--:|---|---|---|");
        foreach (var f in flags)
            sb.AppendLine($"| {f.Id} | {(f.Symbol.Length > 0 ? "`" + f.Symbol + "`" : "")} | {f.Kind} | {Esc(f.Name)}{(f.DemoOnly ? " *(demo only)*" : "")} |");
        sb.AppendLine();

        void Section(string title, string intro, Func<Flag, bool> pick)
        {
            var group = flags.Where(pick).ToList();
            if (group.Count == 0) return;
            sb.AppendLine($"## {title}");
            sb.AppendLine();
            if (intro.Length > 0) { sb.AppendLine(intro); sb.AppendLine(); }
            foreach (var f in group) AppendFlag(sb, f);
        }

        Section("Inventory (0-39)", "One slot per item. 0 = not owned, 1 = owned unless a count is noted. Several slots are reused by a second item in a later chapter (the icon changes with `STATE_INVENTORY`).", f => f.Id < 40);
        Section("Scenario flags (40-195)", "", f => f.Id is >= 40 and <= 195 && (f.AnyUse || f.Source != "none"));
        Section("Engine-reserved and late flags (235-255)", "", f => f.Id >= 235 && (f.AnyUse || f.Source == "curated" || f.Source == "trainer") && f.Kind != "unused");

        var unused = flags.Where(f => f.Id >= 40 && f.Kind == "unused" && !f.AnyUse).Select(f => f.Id).ToList();
        sb.AppendLine("## Unused indices");
        sb.AppendLine();
        sb.AppendLine("No script, engine or decor references these indices in the retail data: " + Ranges(unused) + ". They are free for new content (script-created flags in an editor should start above 195, avoiding 235-237 and 249-255).");
        sb.AppendLine();
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Esc(string s) => s.Replace("|", "\\|");

    private static string Ranges(List<int> ids)
    {
        var parts = new List<string>();
        for (var i = 0; i < ids.Count;)
        {
            var j = i;
            while (j + 1 < ids.Count && ids[j + 1] == ids[j] + 1) j++;
            parts.Add(j > i ? $"{ids[i]}-{ids[j]}" : ids[i].ToString());
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private static void AppendFlag(StringBuilder sb, Flag f)
    {
        sb.AppendLine($"### {f.Id} - {f.Name}{(f.DemoOnly ? " (demo only)" : "")}");
        sb.AppendLine();
        sb.AppendLine($"`{f.Kind}`{(f.Symbol.Length > 0 ? " - `" + f.Symbol + "`" : "")} - source: {f.Source}");
        sb.AppendLine();
        if (f.Values.Count > 0 && f.Values.Count <= 2 && f.Values.All(v => v.Text.Length < 70))
            sb.AppendLine("- **Values:** " + string.Join("; ", f.Values.Select(v => $"`{v.Value}` = {v.Text}")));
        else if (f.Values.Count > 0)
        {
            sb.AppendLine("- **Values:**");
            foreach (var v in f.Values) sb.AppendLine($"  - `{v.Value}` = {v.Text}");
        }
        else if (f.Kind == "inventory") sb.AppendLine("- **Values:** `0` = not owned; `1` = owned");
        if (f.Undocumented.Count > 0) sb.AppendLine($"- **Also used by scripts, not in the list above:** {string.Join(", ", f.Undocumented.Take(20))}{(f.Undocumented.Count > 20 ? ", ..." : "")}");
        if (f.Notes.Length > 0) sb.AppendLine("- **Notes:** " + f.Notes);
        if (f.WrittenBy.Count > 0)
            sb.AppendLine("- **Written by:** " + string.Join("; ", f.WrittenBy.Take(6)) + (f.WrittenBy.Count > 6 ? $"; ... ({f.WrittenBy.Count - 6} more scenes)" : ""));
        if (f.ReadScenes.Count > 0)
            sb.AppendLine($"- **Read in {f.ReadScenes.Count} scene(s):** " + string.Join(", ", f.ReadScenes.Take(14).Select(s => "s" + s)) + (f.ReadScenes.Count > 14 ? ", ..." : ""));
        foreach (var d in f.Decors) sb.AppendLine("- **Island decor:** " + d);
        sb.AppendLine();
    }

    private static void WriteFlagsJson(string path, List<Flag> flags)
    {
        var doc = flags.Select(f => new
        {
            id = f.Id, symbol = f.Symbol, kind = f.Kind, name = f.Name, source = f.Source, demoOnly = f.DemoOnly,
            values = f.Values.Select(v => new { value = v.Value, meaning = v.Text }),
            undocumentedValues = f.Undocumented, notes = f.Notes,
            writtenBy = f.WrittenBy, readInScenes = f.ReadScenes, decors = f.Decors,
        });
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
    }
}
