using System.IO;

namespace LBAAssembler;

// The scenes of an LBA2 game folder as (number, "number: description") for pickers. Scene N is SCENE.HQR entry N + 1;
// entry 0 holds the size of the largest scene.
internal static class Lba2SceneList
{
    public static List<(int Id, string Label)> Load(string directory)
    {
        var path = Path.Combine(directory, "SCENE.HQR");
        var result = new List<(int, string)>();
        if (!File.Exists(path)) return result;
        var count = HqrArchive.CountEntries(path);
        var names = HqdDescriptions.Load("SCENE2.HQD", count).Names;
        var archive = HqrArchive.Open(path);
        for (var entry = 1; entry < count; entry++)
        {
            if (!archive.IsValid(entry)) continue;
            var name = (entry < names.Count ? names[entry] : null)?.Replace("White Leaf Desert", "Desert Island");      // (what LBA2 itself calls that island)
            result.Add((entry - 1, name is null ? $"{entry - 1}" : $"{entry - 1}: {name}"));
        }
        return result;
    }
}
