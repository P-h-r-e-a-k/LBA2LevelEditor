using System.Runtime.InteropServices;
using LBAAssembler;
using LBAAssembler.Terrain;

namespace ScriptRoundTrip;

// The live preview of unsaved terrain edits: the native renderer, pointed at a LiveDataRoot (hard links of a game folder plus a
// rewritable copy of the island), must draw an island edit as soon as the island is written there and loaded again, while the
// game folder's own file stays untouched. Uses a sandbox copy of the game folder (argument 2), never the real one.
internal static class LiveIslandTest
{
    public static int Run(string[] args)
    {
        var sandbox = args.Length > 1 ? args[1] : @"E:\dump\_lba2isl";
        if (!Directory.Exists(sandbox) || !File.Exists(Path.Combine(sandbox, "DESERT.ILE"))) { Console.WriteLine($"no sandbox game folder at {sandbox}"); return 2; }
        var dll = Environment.GetEnvironmentVariable("LBA2_RENDERER_DLL") ??
                  @"E:\dump\LBAAssembler\native\lba2-classic-community\out\build\windows_ucrt64_static\SOURCES\3DEXT\liblba2_renderer.dll";
        var realBefore = File.ReadAllBytes(Path.Combine(sandbox, "DESERT.ILE"));
        LiveDataRoot.CleanStale(sandbox);
        using var live = LiveDataRoot.Create(sandbox, "DESERT.ILE");
        if (live is null) { Console.WriteLine("couldn't create the live folder"); return 2; }
        var failures = 0;
        void Check(string what, bool ok, string detail = "") { Console.WriteLine($"  {(ok ? "ok    " : "FAILED")} {what} {detail}"); if (!ok) failures++; }

        using var lib = new RendererLibraryApi(dll);
        if (!lib.IsLoaded) { Console.WriteLine($"could not load {dll}"); return 2; }
        // like the editor: the session starts on the real game folder and is pointed at the live folder later
        if (!lib.SetDataRoot(sandbox) || !lib.Initialize()) { Console.WriteLine("native init failed"); return 2; }

        byte[] Frame()
        {
            if (lib.LoadIsland("desert") == 0) throw new InvalidOperationException("LoadIsland failed");
            lib.SetViewTarget(8 * 32768 + 16384, 10000, 9 * 32768 + 16384);
            lib.SetCamera(240, -256, 0, 30000);
            if (lib.RenderFrameWide(2) == 0) throw new InvalidOperationException("render failed");
            var p = lib.GetFramebuffer(out var w, out var h, out var pitch);
            var pixels = new byte[w * h];
            for (var row = 0; row < h; row++) Marshal.Copy(p + row * pitch, pixels, row * w, w);
            return pixels;
        }
        int Diff(byte[] a, byte[] b) { var n = 0; for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) n++; return n; }

        Console.WriteLine("live island preview");
        var first = Frame();
        if (!lib.SetDataRoot(live.Directory)) { Console.WriteLine("couldn't point the renderer at the live folder"); return 2; }
        var again = Frame();
        Check("the same island renders identically twice", Diff(first, again) == 0, $"({Diff(first, again)} pixels differ)");

        var island = IslandFile.Load(Path.Combine(sandbox, "DESERT.ILE"));
        var region = new BrushRegion(8 * 64 + 32, 9 * 64 + 32, 28, 0.9);
        for (var i = 0; i < 30; i++) IslandOps.Raise(island, region, 60);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        live.WriteIsland(island.ToBytes());
        var raised = Frame();
        Console.WriteLine($"  (write + reload + render: {sw.ElapsedMilliseconds} ms)");
        var changed = Diff(first, raised);
        Check("the raised terrain is drawn after writing the island into the live folder", changed > 2000, $"({changed} pixels differ)");
        Check("the game folder's island is untouched", File.ReadAllBytes(Path.Combine(sandbox, "DESERT.ILE")).AsSpan().SequenceEqual(realBefore));

        live.WriteIsland(realBefore);
        var restored = Frame();
        Check("writing the original back restores the picture", Diff(first, restored) == 0, $"({Diff(first, restored)} pixels differ)");

        // the editor reloads the island for every preview refresh (about seven times a second while painting): many loads in a row
        var stress = System.Diagnostics.Stopwatch.StartNew();
        var loops = args.Length > 2 ? int.Parse(args[2]) : 60;
        for (var i = 0; i < loops; i++)
        {
            live.WriteIsland(i % 2 == 0 ? island.ToBytes() : realBefore);
            Frame();
        }
        Console.WriteLine($"  ({loops} write + reload + render cycles in {stress.ElapsedMilliseconds} ms)");
        Check($"{loops} island reloads in a row didn't fail", true);
        Console.WriteLine(failures == 0 ? "live island tests: all passed" : $"live island tests: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
