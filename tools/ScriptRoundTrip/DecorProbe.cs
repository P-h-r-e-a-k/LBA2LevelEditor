using LBAAssembler;
using LBAAssembler.Scenes;
using LBAAssembler.Terrain;

namespace ScriptRoundTrip;

// Where does a scene's hero stand relative to the island's terrain and decor boxes? (read only)
//   decorprobe <island file> <scene>
internal static class DecorProbe
{
    public static int Run(string[] args)
    {
        var dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
        var island = IslandFile.Load(Path.Combine(dir, args.Length > 1 ? args[1] : "DESERT.ILE"));
        var scene = new SceneStore(SceneGame.Lba2, dir).Load(args.Length > 2 ? int.Parse(args[2]) : 61);
        var hero = scene.Hero;
        double wx = scene.CubeX * 32768.0 + hero.X, wz = scene.CubeY * 32768.0 + hero.Z;
        Console.WriteLine($"scene cube ({scene.CubeX},{scene.CubeY}) hero local ({hero.X},{hero.Y},{hero.Z}) world ({wx},{wz})");
        Console.WriteLine($"terrain altitude there: {IslandOps.Altitude(island, wx, wz)}");
        foreach (var (cx, cz, cube) in IslandOps.CubeCells(island))
            foreach (var d in cube.Decors)
            {
                double ox = cx * 32768.0, oz = cz * 32768.0;
                if (wx >= ox + d.XMin && wx <= ox + d.XMax && wz >= oz + d.ZMin && wz <= oz + d.ZMax)
                    Console.WriteLine($"  decor body {d.Body & 0xFFFF} at ({d.X},{d.Y},{d.Z}) ZV x {d.XMin}..{d.XMax} y {d.YMin}..{d.YMax} z {d.ZMin}..{d.ZMax}");
            }
        return 0;
    }
}
