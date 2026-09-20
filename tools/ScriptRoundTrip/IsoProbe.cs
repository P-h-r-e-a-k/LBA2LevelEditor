using LBA2LevelEditor.Lba1.Runtime;

namespace ScriptRoundTrip;

internal static class IsoProbe
{
    public static int Run(string[] args)
    {
        var path = args.Length > 1 ? args[1] : @"E:\GOG Games\Little Big Adventure\LBA.iso";
        using var iso = new Lba1Iso(path);
        Console.WriteLine($"{iso.Files.Count} files on the disc, {iso.SectorCount} sectors");
        foreach (var f in iso.Files.Where(f => !f.Path.Contains("/VOX/", StringComparison.OrdinalIgnoreCase)).Take(80))
            Console.WriteLine($"  {f.Path}  ({f.Size} bytes)");
        var flas = iso.Files.Where(f => f.Path.EndsWith(".FLA", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"{flas.Count} FLA films");
        if (flas.Count > 0)
        {
            var d = iso.Read(flas[0]);
            Console.WriteLine($"{flas[0].Path}: {BitConverter.ToString(d, 0, 48)}");
        }
        return 0;
    }
}
