using System.Text.RegularExpressions;

namespace LBAAssembler;

// How the Scenes menu writes a scene's name: the game's own description less what the menu already says (the scene's number, "(room #12)",
// and the island the scene is listed under) and with a capital letter to start.
//   "12: Temple of Bú, 1st scene (room #12)" -> "Temple of Bú, 1st scene"
//   "Citadel Island, near the tavern"        -> "Near the tavern"
internal static partial class SceneMenuNames
{
    // What descriptions call islands, in LBA1 and LBA2 (including the game's own spelling of "Rebelion Island"): a description that starts
    // with one of these and a comma is stripped of it, whichever island's list it is in.
    public static readonly string[] IslandNames =
    {
        "Citadel Island", "Principal Island", "White Leaf Desert", "Proxima Island", "Rebellion Island", "Rebelion Island", "Hamalayi Mountains",
        "Tippet Island", "Brundle Island", "Fortress Island", "Polar Island",
        "Emerald Moon", "Otringal", "Wannies Island", "Francos Island", "Franco Island", "Mosquibees Island", "Island CX", "Island Under Celebration",
        "Celebration Island", "The Elevator Platform Island", "Desert Island",
    };

    [GeneratedRegex(@"^\s*\d+\s*:\s*")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"\s*\(room\s*#\s*\d+\)")]
    private static partial Regex RoomNumber();

    public static string Clean(string description, IEnumerable<string>? moreIslands = null)
    {
        var text = RoomNumber().Replace(LeadingNumber().Replace(description, ""), "").Trim();
        foreach (var island in IslandNames.Concat(moreIslands ?? Array.Empty<string>()).OrderByDescending(n => n.Length))
        {
            if (!text.StartsWith(island + ",", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = text[(island.Length + 1)..].Trim();
            if (rest.Length > 0) text = rest;
            break;
        }
        return text.Length == 0 ? description : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
