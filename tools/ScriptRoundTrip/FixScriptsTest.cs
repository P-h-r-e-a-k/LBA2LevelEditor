using LBA2LevelEditor;

namespace ScriptRoundTrip;

// Applies Lba2ScriptFixes against a game directory (normally a sandbox copy of SCENE.HQR) and prints the
// result, so the fix can be exercised and its output inspected without going through the WPF UI.
//   fixscripts <game dir>
internal static class FixScriptsTest
{
    public static int Run(string[] args)
    {
        var dir = args.Length > 1 ? args[1] : throw new ArgumentException("usage: fixscripts <game dir>");
        var session = new ScriptSession(() => dir);
        var result = Lba2ScriptFixes.Apply(session);
        Console.WriteLine($"Changed={result.Changed}");
        Console.WriteLine($"TouchedScenes=[{string.Join(",", result.TouchedScenes)}]");
        Console.WriteLine(result.Message);
        return 0;
    }
}
