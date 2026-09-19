using LBA2LevelEditor;
using LBA2LevelEditor.LbaScript;

namespace ScriptRoundTrip;

// Cross-checks ScriptSession's C# reproduction of the native actor ordering
// (scene order, objects 1..N-1) against the real native library: for every
// island the actor count must match and every actor's world position must equal
// the position stored in the scene record it is mapped to.
internal static class NativeMap
{
    private static readonly string[] Islands =
    {
        "CITADEL", "MOON", "DESERT", "EMERAUDE", "OTRINGAL", "CELEBRAT", "PLATFORM", "MOSQUIBE", "KNARTAS", "ILOTCX", "ASCENCE", "SOUSCELB",
    };

    public static int Run()
    {
        var gameRoot = Path.GetDirectoryName(Program.HqrPath)!;
        var dll = Environment.GetEnvironmentVariable("LBA2_RENDERER_DLL") ??
                  @"E:\dump\LBA2LevelEditor\native\lba2-classic-community\out\build\windows_ucrt64_static\SOURCES\3DEXT\liblba2_renderer.dll";
        using var lib = new RendererLibraryApi(dll);
        if (!lib.IsLoaded) { Console.WriteLine($"could not load {dll}"); return 2; }
        if (!lib.SetDataRoot(gameRoot) || !lib.Initialize()) { Console.WriteLine("native init failed"); return 2; }

        var session = new ScriptSession(() => gameRoot);
        var archive = HqrArchive.Open(Program.HqrPath);
        var bad = 0;
        foreach (var (name, index) in Islands.Select((n, i) => (n, i)))
        {
            if (lib.LoadIsland(name) == 0) { Console.WriteLine($"{name}: native LoadIsland failed"); continue; }
            var native = lib.GetActorCount();
            var mapped = session.ExteriorActorCount(index);
            var mismatches = 0;
            for (var i = 0; i < Math.Min(native, mapped); i++)
            {
                var src = session.ExteriorActor(index, i)!.Value;
                var rec = SceneRecord.Parse(archive.Read(src.Scene + 1));
                var a = rec.Actors[src.Slot];
                lib.GetActor(i, out var x, out var y, out var z, out _);
                if (x != rec.CubeX * 32768 + a.X || y != a.Y || z != rec.CubeY * 32768 + a.Z) mismatches++;
            }
            var ok = native == mapped && mismatches == 0;
            if (!ok) bad++;
            Console.WriteLine($"{name,-9} native actors={native,4}  mapped={mapped,4}  position mismatches={mismatches}  {(ok ? "OK" : "MISMATCH")}");
        }
        return bad == 0 ? 0 : 1;
    }
}
