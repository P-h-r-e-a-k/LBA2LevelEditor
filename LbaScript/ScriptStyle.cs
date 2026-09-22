namespace LBAAssembler.LbaScript;

// Presentation options for the C text the decompilers write. They only affect
// how text is *printed*; the compiler accepts either spelling.
public static class ScriptStyle
{
    // Function names (opcodes and condition functions such as SET_TRACK or
    // DISTANCE) print in lowercase: set_track(...), distance(0). On by default.
    // In life scripts the flat low-level forms of the control-flow opcodes
    // (IF(...), ELSE(...), CASE(...), SWITCH(...)) always stay uppercase: their
    // lowercase spellings are the structured keywords.
    public static bool LowercaseNames { get; set; } = true;

    private static readonly HashSet<string> LifeKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "while", "switch", "case", "default", "break", "return", "goto", "void", "swif", "oneif", "snif", "neverif",
    };

    // `name` as the canonical (uppercase) opcode / condition name -> the
    // spelling to print. Track scripts have no keywords, so every name lowers.
    internal static string Func(string name, bool life = true)
    {
        if (!LowercaseNames) return name;
        var lower = name.ToLowerInvariant();
        return life && LifeKeywords.Contains(lower) ? name : lower;
    }
}
