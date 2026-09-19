using System.Text.RegularExpressions;
using LBA2LevelEditor.LbaScript;

namespace LBA2LevelEditor;

internal sealed record CompletionItem(string Signature, string Description, string InsertText, string Category, string Name);

// Autocomplete / keyword-reference data for the C-style script dialect,
// generated from the opcode tables (so it can never drift from what the
// compiler accepts). Human-written descriptions come from Lba2ScriptOpcodes'
// older tables where a name matches; everything else gets a generated one.
internal static partial class ScriptCompletions
{
    private static readonly Lazy<IReadOnlyList<CompletionItem>> life = new(BuildLife);
    private static readonly Lazy<IReadOnlyList<CompletionItem>> track = new(BuildTrack);

    public static IReadOnlyList<CompletionItem> For(ScriptKind kind) => kind == ScriptKind.Life ? life.Value : track.Value;

    private static string Describe(Lba2ScriptOpcodes.OpcodeKind kind, string name, string fallback) =>
        Lba2ScriptOpcodes.All.FirstOrDefault(o => o.Kind == kind && o.Name == name)?.Description ?? fallback;

    private static string ArgList(ArgDef[] args) => string.Join(", ", args.Where(a => a.Role != ArgRole.Hidden).Select(a => a.Name));

    private static CompletionItem Call(string name, ArgDef[] args, string category, string description)
    {
        var visible = args.Where(a => a.Role != ArgRole.Hidden).ToArray();
        var sig = $"{name}({ArgList(args)});";
        return new CompletionItem(sig, description, visible.Length == 0 ? $"{name}();" : $"{name}(", category, name);
    }

    private static IReadOnlyList<CompletionItem> BuildLife()
    {
        var items = new List<CompletionItem>
        {
            new("if (cond) { ... } else { ... }", "Branch. Conditions are comparisons joined with && and ||, e.g. DISTANCE(0) < 500 && ZONE() == 1.", "if (", "keyword", "if"),
            new("swif (cond) { ... }", "Like if, but re-tested every time the line is reached (SWIF).", "swif (", "keyword", "swif"),
            new("oneif (cond) { ... }", "Like if, but only taken the first time (ONEIF).", "oneif (", "keyword", "oneif"),
            new("while (cond) { ... }", "Loop: an IF that skips the body, and an OFFSET jump back to the test.", "while (", "keyword", "while"),
            new("switch (FUNC(x)) { case 1: ... break; default: ... }", "Multi-way branch on one condition function. case > 5: is allowed; stacked cases (case 1: case 2:) mean either.", "switch (", "keyword", "switch"),
            new("case value:", "One arm of a switch. An operator may precede the value: case >= 5:.", "case ", "keyword", "case"),
            new("default:", "Fallback arm of a switch.", "default:", "keyword", "default"),
            new("break;", "Leave the enclosing switch (BREAK).", "break;", "keyword", "break"),
            new("return;", "Stop running this comportement for this tick (RETURN).", "return;", "keyword", "return"),
            new("goto label;", "Unconditional jump (OFFSET) to a label defined as  label:", "goto ", "keyword", "goto"),
            new("void comportement_N() { ... }", "A comportement block. SET_COMPORTEMENT(comportement_N) selects it for the next tick; it is not a call.", "void comportement_", "keyword", "void"),
        };

        foreach (var d in Opcodes.LifeOps)
        {
            switch (d.Form)
            {
                case LifeForm.Plain or LifeForm.Dir:
                    if (d.Id is LifeText.OpReturn or LifeText.OpEnd or LifeText.OpEndComportement or LifeText.OpOffset or LifeText.OpBreak) continue; // spelled by keyword / implicit
                    var extra = d.Form == LifeForm.Dir ? " (+ object/point for FOLLOW / SAME_XZ / CIRCLE modes)" : "";
                    items.Add(Call(d.Name, d.Args, "life action", Describe(Lba2ScriptOpcodes.OpcodeKind.LifeAction, d.Name, $"Life-script opcode {d.Id}.") + extra));
                    break;
                case LifeForm.Cond:
                    items.Add(new CompletionItem($"{d.Name}(condition, label);", "Low-level form of a conditional jump (jumps to label when the condition is false; OR_IF when true). Prefer if/while.", $"{d.Name}(", "low-level", d.Name));
                    break;
            }
        }

        foreach (var c in Opcodes.Conditions)
        {
            var sig = c.OperandName is null ? $"{c.Name}() <op> value" : $"{c.Name}({c.OperandName}) <op> value";
            items.Add(new CompletionItem(sig, Describe(Lba2ScriptOpcodes.OpcodeKind.LifeCondition, c.Name, $"Condition {c.Id} ({c.Value} comparison value)."),
                c.OperandName is null ? $"{c.Name}() " : $"{c.Name}(", "condition", c.Name));
        }

        foreach (var m in Opcodes.MoveNames)
            items.Add(new CompletionItem(m, $"Movement mode for SET_DIR / SET_DIR_OBJ.", m, "constant", m));
        return items;
    }

    private static IReadOnlyList<CompletionItem> BuildTrack()
    {
        var items = new List<CompletionItem>();
        foreach (var d in Opcodes.TrackOps)
        {
            if (d.Id == 0) continue; // END is implicit
            items.Add(Call(d.Name, d.Args, "track action", Describe(Lba2ScriptOpcodes.OpcodeKind.TrackAction, d.Name, $"Track-script opcode {d.Id}.")));
        }
        return items;
    }

    // Names defined by the text itself, offered wherever a label / comportement
    // reference is expected: comportement_N functions from a life script and
    // label_N symbols from a track script.
    public static IReadOnlyList<CompletionItem> Symbols(string lifeText, string trackText)
    {
        var items = new List<CompletionItem>();
        foreach (Match m in FunctionRegex().Matches(lifeText))
            items.Add(new CompletionItem(m.Groups[1].Value, "Comportement defined in this life script.", m.Groups[1].Value, "symbol", m.Groups[1].Value));

        var seen = new Dictionary<string, int>();
        foreach (Match m in LabelRegex().Matches(trackText))
        {
            var id = m.Groups[1].Value;
            seen[id] = seen.GetValueOrDefault(id) + 1;
            var name = TrackText.LabelSymbol(int.Parse(id), seen[id]);
            items.Add(new CompletionItem(name, "Track label: usable in SET_TRACK / SET_TRACK_OBJ and (inside a track) GOTO.", name, "symbol", name));
        }
        return items;
    }

    [GeneratedRegex(@"\bvoid\s+([A-Za-z_]\w*)\s*\(")]
    private static partial Regex FunctionRegex();

    [GeneratedRegex(@"\bLABEL\(\s*(\d+)\s*\)")]
    private static partial Regex LabelRegex();
}
