using System.IO;
using System.Runtime.InteropServices;
using LBAAssembler;

namespace ScriptRoundTrip;

// interiorall <sandbox> [first] [last] [rounds]: loads every scene through the native renderer the way the editor's scene box does (load, one frame,
// the stitched render) and prints each number before it starts, so a native crash names its scene (set LBA2_RENDERER_CRASHLOG=<file> for the faulting
// addresses). Sandbox folder only (hard links of the game files: the renderer reads them).
internal static class InteriorAllTest
{
    public static int Run(string[] args)
    {
        var sandbox = args[1];
        var first = args.Length > 2 ? int.Parse(args[2]) : 0;
        var last = args.Length > 3 ? int.Parse(args[3]) : 230;
        var rounds = args.Length > 4 ? int.Parse(args[4]) : 1;
        var dll = Environment.GetEnvironmentVariable("LBA2_RENDERER_DLL") ??
                  @"E:\dump\LBAAssembler\native\lba2-classic-community\out\build\windows_ucrt64_static\SOURCES\3DEXT\liblba2_renderer.dll";
        using var lib = new RendererLibraryApi(dll);
        if (!lib.IsLoaded) { Console.WriteLine($"could not load {dll}"); return 2; }
        if (!lib.SetDataRoot(sandbox) || !lib.Initialize()) { Console.WriteLine("native init failed"); return 2; }
        var canvas = new byte[3712 * 2400];
        // (the editor always has an island loaded and drawn before an interior is picked: the renderer needs a first exterior frame)
        if (lib.LoadIsland("desert") == 0) { Console.WriteLine("LoadIsland failed"); return 2; }
        lib.SetViewTarget(8 * 32768 + 16384, 10000, 9 * 32768 + 16384);
        lib.SetCamera(240, -256, 0, 30000);
        lib.RenderFrameWide(2);
        var archive = HqrArchive.Open(System.IO.Path.Combine(sandbox, "SCENE.HQR"));
        int loaded = 0, skipped = 0;
        for (var round = 0; round < rounds; round++)
            for (var scene = first; scene <= last; scene++)
            {
                // (the scene box only hands interiors to the interior renderer: the scene record's sixth byte, CubeMode, is 0)
                byte[]? bytes = null;
                try { if (scene + 1 < archive.Count && archive.IsValid(scene + 1)) bytes = archive.Read(scene + 1); } catch (InvalidDataException) { }
                if (bytes is not { Length: > 5 } || bytes[5] != 0) { skipped++; continue; }
                Console.Write($"{scene} "); Console.Out.Flush();
                if (!lib.LoadInteriorScene(scene)) { skipped++; continue; }
                lib.GetInteriorCameraPosition(out _, out _, out _);
                lib.RenderInteriorFrame();
                var handle = GCHandle.Alloc(canvas, GCHandleType.Pinned);
                try { lib.RenderInteriorFull(handle.AddrOfPinnedObject(), 3712, 2400); }
                finally { handle.Free(); }
                loaded++;
            }
        Console.WriteLine($"\ninteriorall: {loaded} scenes loaded and drawn, {skipped} not interiors, in {rounds} round(s)");
        return 0;
    }
}
