using System.Runtime.InteropServices;
using LBA2LevelEditor;

namespace ScriptRoundTrip;

// The editor picks a scene of another island straight after loading that island: an exterior island load + wide render, then an
// interior scene load + stitched render, from different threads (the render loop runs on the thread pool, the scene load on the UI
// thread). "interiorstress [folder] [loops] [sameThread]" repeats that; a native crash here kills the process. Sandbox folder only.
internal static class InteriorStressTest
{
    public static int Run(string[] args)
    {
        var sandbox = args.Length > 1 ? args[1] : @"E:\dump\_lba2isl";
        var loops = args.Length > 2 ? int.Parse(args[2]) : 100;
        var sameThread = args.Length > 3 && args[3] == "same";
        var dll = Environment.GetEnvironmentVariable("LBA2_RENDERER_DLL") ??
                  @"E:\dump\LBA2LevelEditor\native\lba2-classic-community\out\build\windows_ucrt64_static\SOURCES\3DEXT\liblba2_renderer.dll";
        using var lib = new RendererLibraryApi(dll);
        if (!lib.IsLoaded) { Console.WriteLine($"could not load {dll}"); return 2; }
        if (!lib.SetDataRoot(sandbox) || !lib.Initialize()) { Console.WriteLine("native init failed"); return 2; }
        var canvas = new byte[3712 * 2400];
        // the floor query behind dropping Twinsen: J. Baldino's house (scene 38) starts him at (2816, 2816, 1792), on the upper walkway
        if (lib.LoadInteriorScene(38))
        {
            var walkway = lib.InteriorFloorY(2816, 2816 + 767, 1792);
            var below = lib.InteriorFloorY(2816, 2816 - 1, 1792);
            var outside = lib.InteriorFloorY(-5, 3000, 1792);
            Console.WriteLine($"  floor at the hero start: {walkway} (expect 2816), column below the walkway: {below}, outside the scene: {outside} (expect -1)");
            for (var x = 0; x < 32768; x += 1536) Console.WriteLine("  row z=1792: x=" + x + " floor " + lib.InteriorFloorY(x, 24 * 256, 1792));
            if (walkway != 2816) { Console.WriteLine("FAILED: floor query"); return 1; }
        }

        void Exterior(string island)
        {
            if (lib.LoadIsland(island) == 0) throw new InvalidOperationException("LoadIsland failed");
            lib.SetViewTarget(8 * 32768 + 16384, 10000, 9 * 32768 + 16384);
            lib.SetCamera(240, -256, 0, 30000);
            lib.RenderFrameWide(2);
        }
        var skipped = 0;
        void TopDown(string island, int cubes)
        {
            if (lib.LoadIsland(island) == 0) throw new InvalidOperationException("LoadIsland failed");
            lib.SetDrawSky(false);
            for (var cy = 0; cy < 16 && cubes > 0; cy++)
            for (var cx = 0; cx < 16 && cubes > 0; cx++)
            {
                if (lib.SetViewTarget(cx * 32768 + 16384, 3000, cy * 32768 + 16384) == 0) continue;
                lib.SetCamera(1023, 0, 0, 50000);
                lib.SetDrawSea(false); lib.RenderFrame();
                lib.SetDrawSea(true); lib.RenderFrame();
                cubes--;
            }
            lib.SetDrawSea(true);
            lib.SetDrawSky(true);
        }
        void Interior(int scene)
        {
            if (!lib.LoadInteriorScene(scene)) { skipped++; return; }
            lib.RenderInteriorFrame();
            var handle = GCHandle.Alloc(canvas, GCHandleType.Pinned);
            try { lib.RenderInteriorFull(handle.AddrOfPinnedObject(), 3712, 2400); }
            finally { handle.Free(); }
        }

        var scenes = new[] { 194, 193, 195, 140, 139 };
        var islands = new[] { "desert", "citadel", "emeraude" };
        for (var i = 0; i < loops; i++)
        {
            var island = islands[i % islands.Length];
            var scene = scenes[i % scenes.Length];
            if (sameThread) { Exterior(island); TopDown(island, 12); } else { Task.Run(() => Exterior(island)).Wait(); Task.Run(() => TopDown(island, 12)).Wait(); }
            Interior(scene);
            if (i % 10 == 0) Console.WriteLine($"  {i}: {island} -> scene {scene}");
        }
        Console.WriteLine($"interior stress: {loops} island -> interior cycles survived, {skipped} not interiors ({(sameThread ? "one thread" : "thread pool + main")})");
        return 0;
    }
}
