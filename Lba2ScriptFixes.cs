using LBAAssembler.LbaScript;

namespace LBAAssembler;

// Tools > LBA2: Fix scripting errors. Patches three known retail SCENE.HQR script bugs (docs/LBA2_SCRIPT_BUGS.md):
//   * scene 36 (White Leaf Desert Bazaar): buying the holomap calls found_object(0) but never sets
//     var_game(0), so the holomap is paid for but the holomap feature never actually unlocks (compare the
//     Citadel Island shop, scene 14, which sets it correctly right after the same found_object(0) call).
//   * scene 100 (Wannies Island mine, Dino-Fly minecart clover box): tests var_game(242) to avoid
//     re-granting itself but never sets it, so it can be farmed by leaving and re-entering the scene.
//   * scene 180 (Island CX secret passage clover box): shares var_game(244) with the unrelated clover box
//     in scene 129 (Island Under Celebration) - collecting one breaks the other. Moved to var_game(247),
//     the next number in that block (240-246, one flag per clover box) that no script uses.
// Each entry only patches if the actor's script still has the exact known-buggy text; running this again
// (or on an already-fixed SCENE.HQR) reports nothing to change rather than touching anything.
internal static class Lba2ScriptFixes
{
    public const string Title = "Fix LBA2 scripting errors";

    public static readonly IReadOnlyList<int> TargetScenes = new[] { 36, 100, 180 };

    internal sealed record FixResult(bool Changed, string Message, IReadOnlyList<int> TouchedScenes);

    private sealed record Fix(int Scene, int Actor, string Summary, string Find, string Replace);

    private static readonly Fix[] Fixes =
    {
        new(36, 0, "White Leaf Desert Bazaar: buying the holomap now sets var_game(0), so it actually unlocks",
            Find:
                "                        give_gold_pieces(10);\n" +
                "                        kill_obj(4);\n" +
                "                        found_object(0);\n",
            Replace:
                "                        give_gold_pieces(10);\n" +
                "                        kill_obj(4);\n" +
                "                        found_object(0);\n" +
                "                        set_var_game(0, 1);\n"),

        new(100, 5, "Wannies Island mine: the Dino-Fly minecart clover box now marks itself collected (var_game(242))",
            Find:
                "        inc_clover_box();\n" +
                "        sample(662);\n" +
                "        sample(663);\n" +
                "        suicide();\n",
            Replace:
                "        inc_clover_box();\n" +
                "        set_var_game(242, 1);\n" +
                "        sample(662);\n" +
                "        sample(663);\n" +
                "        suicide();\n"),

        new(180, 16, "Island CX secret passage: its clover box now uses its own flag, var_game(247), instead of scene 129's var_game(244)",
            Find:
                "        if (0 < var_game(244))\n" +
                "        {\n" +
                "            give_bonus(0);\n" +
                "        }\n" +
                "        else\n" +
                "        {\n" +
                "            inc_clover_box();\n" +
                "            set_var_game(244, 1);\n" +
                "        }\n",
            Replace:
                "        if (0 < var_game(247))\n" +
                "        {\n" +
                "            give_bonus(0);\n" +
                "        }\n" +
                "        else\n" +
                "        {\n" +
                "            inc_clover_box();\n" +
                "            set_var_game(247, 1);\n" +
                "        }\n"),
    };

    public static FixResult Apply(ScriptSession session)
    {
        var applied = new List<string>();
        var alreadyDone = 0;
        var skipped = new List<string>();
        var touched = new List<int>();

        foreach (var fix in Fixes)
        {
            var scripts = session.GetScene(fix.Scene);
            if (scripts is null) { skipped.Add($"scene {fix.Scene}: couldn't be loaded."); continue; }

            var text = scripts.GetText(fix.Actor, ScriptKind.Life);
            if (text.Contains(fix.Replace, StringComparison.Ordinal)) { alreadyDone++; continue; }
            if (!text.Contains(fix.Find, StringComparison.Ordinal))
            {
                skipped.Add($"scene {fix.Scene} actor {fix.Actor}: expected script text not found (already edited?) - left alone.");
                continue;
            }

            var patched = ReplaceOnce(text, fix.Find, fix.Replace);
            var (_, error, _) = scripts.CheckTextFull(fix.Actor, ScriptKind.Life, patched);
            if (error is not null)
            {
                skipped.Add($"scene {fix.Scene} actor {fix.Actor}: the patched script failed to compile ({error}) - left alone.");
                continue;
            }

            scripts.SetText(fix.Actor, ScriptKind.Life, patched);
            applied.Add(fix.Summary);
            touched.Add(fix.Scene);
        }

        if (applied.Count == 0)
        {
            var msg = alreadyDone == Fixes.Length
                ? "All three fixes are already applied; nothing to change."
                : ("Nothing was changed." + (skipped.Count > 0 ? " " + string.Join(" ", skipped) : ""));
            return new FixResult(false, msg, Array.Empty<int>());
        }

        var save = session.SaveAll();
        if (!save.Ok) return new FixResult(false, $"Not saved: {save.Message}", Array.Empty<int>());

        var lines = applied.Select(a => "- " + a).ToList();
        if (alreadyDone > 0) lines.Add($"(already applied earlier: {alreadyDone})");
        lines.AddRange(skipped.Select(s => "! " + s));
        return new FixResult(true, string.Join("\n", lines), touched);
    }

    // Applies one, known-unique replacement. string.Replace would silently rewrite every match; this makes
    // sure exactly the one occurrence that was checked against Find is the one that changes.
    private static string ReplaceOnce(string text, string find, string replace)
    {
        var i = text.IndexOf(find, StringComparison.Ordinal);
        return text[..i] + replace + text[(i + find.Length)..];
    }
}
