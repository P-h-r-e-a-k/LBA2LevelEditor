using System.Runtime.InteropServices;
using LBAAssembler.Scenes;

namespace ScriptRoundTrip;

// lba2actorlocate <folder> <island name, e.g. CITADEL, or "interior" <scene>...>: loads the native renderer directly (no WPF, unlike
// RendererLibraryApi/CommunityRendererBackend, which this deliberately doesn't use) and checks the hypothesis
// Lba2ActorPersistence.Locate relies on: that the native actor scan's flat index, once you count how many
// earlier entries share an actor's own scene (lba2_renderer_get_actor_scene) and add one for the hero, gives
// exactly that actor's own position in SceneStore's own SceneModel.Actors list for that scene -- for EVERY
// actor of EVERY scene the island scan finds, not just a couple sampled by hand.
internal static class Lba2ActorLocateStudy
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetDataRootFn([MarshalAs(UnmanagedType.LPStr)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadIslandFn([MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorFn(int index, out int x, out int y, out int z, out int waypointCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorAttributesFn(int index, out int beta, out int body, out int anim, out int lifePoint, out int armor, out int hitForce, out int move);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorSceneFn(int index);

    public static int Run(string[] args)
    {
        var gameDir = args[1];
        var interior = string.Equals(args[2], "interior", StringComparison.OrdinalIgnoreCase);
        var dllPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "native", "lba2-classic-community", "out", "build", "windows_ucrt64_static", "SOURCES", "3DEXT", "liblba2_renderer.dll"));
        if (!File.Exists(dllPath)) { Console.WriteLine($"not found: {dllPath}"); return 1; }
        var handle = NativeLibrary.Load(dllPath);
        T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));

        var initialize = Get<IntFn>("lba2_renderer_initialize");
        var setDataRoot = Get<SetDataRootFn>("lba2_renderer_set_data_root");
        var loadIsland = Get<LoadIslandFn>("lba2_renderer_load_island");
        var loadInteriorScene = Get<IntArgFn>("lba2_renderer_load_interior_scene");
        var getActorCount = Get<IntFn>("lba2_renderer_get_actor_count");
        var getActor = Get<GetActorFn>("lba2_renderer_get_actor");
        var getActorAttributes = Get<GetActorAttributesFn>("lba2_renderer_get_actor_attributes");
        var getActorScene = Get<GetActorSceneFn>("lba2_renderer_get_actor_scene");

        if (setDataRoot(gameDir) != 1) { Console.WriteLine("set_data_root failed"); return 1; }
        if (initialize() != 1) { Console.WriteLine("initialize failed"); return 1; }

        int checkedTotal = 0, mismatchTotal = 0, offsetMismatchTotal = 0;
        if (interior)
        {
            foreach (var sceneArg in args.Skip(3))
            {
                var numscene = int.Parse(sceneArg);
                if (loadInteriorScene(numscene) != 1) { Console.WriteLine($"scene {numscene}: load_interior_scene failed"); continue; }
                var n = getActorCount();
                Console.WriteLine($"scene {numscene}: {n} actors");
                var (chk, mis, off) = CheckAll(n, gameDir, getActor, getActorAttributes, getActorScene);
                checkedTotal += chk; mismatchTotal += mis; offsetMismatchTotal += off;
            }
        }
        else
        {
            var island = args[2];
            if (loadIsland(island) != 1) { Console.WriteLine("load_island failed"); return 1; }
            var count = getActorCount();
            Console.WriteLine($"{count} actors scanned on {island}");
            (checkedTotal, mismatchTotal, offsetMismatchTotal) = CheckAll(count, gameDir, getActor, getActorAttributes, getActorScene);
        }
        Console.WriteLine($"TOTAL: checked {checkedTotal} actors, {mismatchTotal} field mismatches, {offsetMismatchTotal} X/Z offset mismatches");
        return 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntArgFn(int value);

    private static (int Checked, int Mismatches, int OffsetMismatches) CheckAll(int count, string gameDir, GetActorFn getActor, GetActorAttributesFn getActorAttributes, GetActorSceneFn getActorScene)
    {

        var store = new SceneStore(SceneGame.Lba2, gameDir);
        var sceneCache = new Dictionary<int, SceneModel>();
        var perScene = new Dictionary<int, int>();
        var sceneOffset = new Dictionary<int, (int Dx, int Dz)>();   // the constant (native - file) X/Z every actor of one scene should share (0 for an interior scene)
        int checkedCount = 0, mismatches = 0, offsetMismatches = 0;
        // sbyte fields (S8 in SceneSerializer) alias 128..255 to negative: the file's own byte for "255" reads back
        // as -1, so that is what a correct round trip has to compare against, not the native side's plain 0..255.
        static int Signed(int v) => unchecked((sbyte)v);
        for (var i = 0; i < count; i++)
        {
            var scene = getActorScene(i);
            if (scene < 0) { Console.WriteLine($"  actor {i}: no scene"); continue; }
            var ordinal = perScene.GetValueOrDefault(scene);
            perScene[scene] = ordinal + 1;

            if (!sceneCache.TryGetValue(scene, out var model))
            {
                try { model = store.Load(scene); }
                catch (Exception e) { Console.WriteLine($"  scene {scene}: couldn't load ({e.Message})"); continue; }
                sceneCache[scene] = model;
            }
            var actorPos = ordinal + 1;   // the hero is Actors[0]
            checkedCount++;
            if (actorPos >= model.Actors.Count)
            {
                Console.WriteLine($"  actor {i} (scene {scene}, ordinal {ordinal}): out of range (scene has {model.Actors.Count - 1} real actors)");
                mismatches++;
                continue;
            }
            var fileActor = model.Actors[actorPos];
            getActor(i, out var nx, out var ny, out var nz, out _);
            getActorAttributes(i, out var nbeta, out _, out _, out var nlife, out var narmor, out var nhit, out var nmove);

            var offset = (Dx: nx - fileActor.X, Dz: nz - fileActor.Z);
            if (!sceneOffset.TryGetValue(scene, out var expectedOffset)) sceneOffset[scene] = offset;
            else if (offset != expectedOffset)
            {
                offsetMismatches++;
                if (offsetMismatches <= 10) Console.WriteLine($"  OFFSET MISMATCH scene {scene} actor {actorPos}: expected {expectedOffset}, got {offset}");
            }

            var ok = fileActor.Y == ny && fileActor.Beta == nbeta
                     && fileActor.LifePoints == Signed(nlife) && fileActor.Armor == Signed(narmor) && fileActor.HitForce == Signed(nhit) && fileActor.Move == Signed(nmove);
            if (!ok)
            {
                mismatches++;
                if (mismatches <= 15) Console.WriteLine($"  MISMATCH actor {i} -> scene {scene} actor {actorPos}: file(Y={fileActor.Y},beta={fileActor.Beta},life={fileActor.LifePoints},armor={fileActor.Armor},hit={fileActor.HitForce},move={fileActor.Move}) native(Y={ny},beta={nbeta},life={nlife}->{Signed(nlife)},armor={narmor}->{Signed(narmor)},hit={nhit}->{Signed(nhit)},move={nmove}->{Signed(nmove)})");
            }
        }
        Console.WriteLine($"checked {checkedCount} of {count} actors across {perScene.Count} scenes: {mismatches} field mismatches, {offsetMismatches} X/Z cube-offset mismatches");
        Console.WriteLine("per-scene (native - file) X/Z offset: " + string.Join(", ", sceneOffset.OrderBy(e => e.Key).Select(e => $"{e.Key}:({e.Value.Dx},{e.Value.Dz})")));
        return (checkedCount, mismatches, offsetMismatches);
    }
}
