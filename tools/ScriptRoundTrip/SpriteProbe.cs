using LBA2LevelEditor;
using LBA2LevelEditor.Assets;

namespace ScriptRoundTrip;

// assets probe <file.hqr>: how many entries decode as run-length pictures, and the first bytes of some that do not.
internal static class SpriteProbe
{
    public static int Run(string path)
    {
        var hqr = HqrFile.Parse(File.ReadAllBytes(path));
        int ok = 0, bad = 0, empty = 0;
        var shown = 0;
        for (var i = 0; i < hqr.Count; i++)
        {
            if (hqr.IsEmpty(i)) { empty++; continue; }
            var data = hqr.Read(i);
            try
            {
                var image = GphImage.Decode(data);
                if (image.Length == data.Length) ok++; else { bad++; if (shown++ < 6) Console.WriteLine($"  {i}: decodes {image.Length} of {data.Length} bytes; head {Convert.ToHexString(data.AsSpan(0, Math.Min(24, data.Length)))}"); }
            }
            catch (InvalidDataException) { bad++; if (shown++ < 6) Console.WriteLine($"  {i}: not a picture ({data.Length} bytes); head {Convert.ToHexString(data.AsSpan(0, Math.Min(24, data.Length)))}"); }
        }
        Console.WriteLine($"{Path.GetFileName(path)}: {hqr.Count} slots, {empty} empty, {ok} exact pictures, {bad} others");
        return 0;
    }
}
