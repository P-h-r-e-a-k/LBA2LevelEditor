using LBAAssembler;
using LBAAssembler.LbaScript;

namespace ScriptRoundTrip;

internal static class Program
{
    private const string DefaultSceneHqr = @"E:\GOG Games\Little Big Adventure 2 - Level viewer\SCENE.HQR";

    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "stats";
        HqrPath = Environment.GetEnvironmentVariable("LBA2_SCENE_HQR") ?? DefaultSceneHqr;
        var archive = HqrArchive.Open(HqrPath);

        return command switch
        {
            "stats" => Stats(archive),
            "dump" => Dump(archive, args),
            "facts" => Facts.Run(archive),
            "odd" => Odd.Run(archive),
            "show" => Show.Run(archive, args),
            "selftest" => SelfTest.Run(),
            "nativemap" => NativeMap.Run(),
            "scenetests" => SceneTests.Run(archive),
            "commenttests" => CommentTests.Run(archive),
            "commentdemo" => CommentTests.Demo(archive),
            "roundtrip" => RoundTrip.Run(archive, args.Length > 1 ? args[1] : "all"),
            "lba1" => Lba1Tests.Run(args),
            "foundation" => FoundationTests.Run(args),
            "store" => StoreTests.Run(args),
            "runtime" => RuntimeTests.Run(args),
            "iso" => IsoProbe.Run(args),
            "lba2play" => Lba2PlayTests.Run(args),
            "lba2playall" => Lba2PlayAllTest.Run(args),
            "island" => IslandTests.Run(args),
            "assets" => AssetTests.Run(args),
            "gridlayout" => GridLayoutStudy.Run(),
            "grids" => GridTests.Run(args),
            "gridengine" => GridEngineTest.Run(args),
            "blankengine" => GridEngineTest.Blank(),
            "newbrick" => NewBrickTest.Run(),
            "rebuild" => RebuildTests.Run(args),
            "lightfit" => IslandLightFit.Run(args),
            "islandengine" => IslandEngineTest.Run(args),
            "liveisland" => LiveIslandTest.Run(args),
            "interiorstress" => InteriorStressTest.Run(args),
            "interiorall" => InteriorAllTest.Run(args),
            "audio" => AudioTests.Run(args),
            "decorprobe" => DecorProbe.Run(args),
            "footprints" => IslandFootprintStudy.Run(args),
            "islandrender" => IslandRenderCommand.Run(args),
            "patches" => PatchStudy.Run(),
            "validate" => ValidatorStudy.Run(),
            "emptypos" => EmptyPosStudy.Run(),
            "griddiff" => GridDiffStudy.Run(args),
            "hqrcmp" => HqrCompare.Run(args),
            "gridvalidate" => GridValidatorStudy.Run(),
            "doorstudy" => DoorStudy.Run(args),
            "doorrender" => DoorRender.Run(args),
            "blocksheet" => BlockSheet.Run(args),
            "blockinfo" => BlockInfo.Run(args),
            "doorvariant" => DoorVariant.Run(args),
            "surpriseapply" => SurpriseCommands.Apply(args),
            "scenetext" => SurpriseCommands.Text(args),
            "doorfloor" => DoorFloor.Run(args),
            "stacks" => StackDump.Run(args),
            "fishtrace" => FishTrace.Run(args),
            "animsteps" => AnimSteps.Run(args),
            "scenemap" => SceneMap.Run(args),
            "areas" => AreaStudy.Run(args),
            "lampdebug" => LampDebug.Run(args),
            "blockdump" => BlockDump.Run(args),
            "toprare" => TopRare.Run(args),
            "blockcells" => BlockCells.Run(args),
            "blockuse" => BlockUse.Run(args),
            "areatests" => AreaTests.Run(args),
            "overlaps" => OverlapStudy.Run(args),
            "tippetfit" => TippetFit.Run(args),
            "separate" => SeparateStudy.Run(args),
            "lba2links" => Lba2LinkStudy.Run(args),
            "lba2areas" => Lba2AreaStudy.Areas(args),
            "lba2overlaps" => Lba2AreaStudy.OverlapsCommand(args),
            "lba2separate" => Lba2AreaStudy.Separate(args),
            "lba2render" => Lba2AreaStudy.Render(args),
            "lba2pitch" => Lba2PitchStudy.Run(args),
            "lba2bodies" => Lba2BodyStudy.Run(args),
            "lba2actors" => Lba2ActorStudy.Run(args),
            "lba2footprint" => Lba2FootprintStudy.Run(args),
            "lba2plan" => Lba2PlanStudy.Run(args),
            "lba1plan" => Lba1PlanStudy.Run(args),
            "lba1onebone" => Lba1OneBone.Run(args),
            "lba1entities" => Lba1EntityScan.Run(args),
            "lba1entity" => Lba1EntityDump.Run(args),
            "lba1textchars" => Lba1TextChars.Run(args),
            "doorlanes" => DoorLanes.Run(args),
            "lba2actorlocate" => Lba2ActorLocateStudy.Run(args),
            "lba2entity2" => Lba2EntityDump2.Run(args),
            "lba2actordump" => Lba2ActorDump.Run(args),
            "lba2bodyanim" => Lba2BodyAnimStudy.Run(args),
            "lba1bodyanim" => Lba1BodyAnimStudy.Run(args),
            "jumpheight" => JumpHeight.Run(args),
            "buttonjump2" => ButtonJump2.Run(args),
            "floorat" => FloorAt.Run(args),
            "buttonjump" => ButtonJump.Run(args),
            "lba2screenlift" => Lba2ScreenLiftStudy.Run(args),
            "lba2lift" => Lba2LiftStudy.Run(args),
            "lba2stack" => Lba2StackStudy.Run(args),
            "dummybody" => DummyBodyStudy.Run(args),
            "actordump" => ActorDump.Run(args),
            "scriptgrep" => ScriptGrep.Run(args),
            "doortrace" => DoorTrace.Run(args),
            "scenezones" => SceneZoneDump.Run(args),
            "doorwalk" => DoorWalk.Run(args),
            "elfxliff" => ElfXliff.Run(args),
            "mushroom" => MushroomStudy.Run(args),
            "lba1bodywinding" => Lba1BodyWinding.Run(args),
            "lba1sprites" => Lba1SpriteSheet.Run(args),
            "lba2cubes" => Lba2CubeStudy.Run(args),
            "lba2groups" => Lba2GroupStudy.Run(args),
            "lba2names" => Lba2NameStudy.Run(args),
            "links" => LinkStudy.Run(args),
            "edges" => EdgeStudy.Run(args),
            "arearender" => AreaRender.Run(args),
            "doorapron" => DoorApron.Run(args),
            "gameflags" => GameFlagMining.Run(args),
            "pickupflags" => PickupFlagScan.Run(args),
            "fixscripts" => FixScriptsTest.Run(args),
            "horn" => GameFlagMining.RunHorn(args),
            "horndump" => GameFlagMining.RunHornDump(args),
            "horntext" => GameFlagMining.RunHornText(args),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.WriteLine("usage: ScriptRoundTrip stats | dump <scene> <actor> [life|track]");
        return 2;
    }

    // dump <scene> <actor> [life|track]: raw hex, then the linear decode with
    // byte offsets (stopping, and saying so, at the first decode error).
    private static int Dump(HqrArchive archive, string[] args)
    {
        var scene = int.Parse(args[1]);
        var actor = int.Parse(args[2]);
        var kind = args.Length > 3 && args[3] == "track" ? ScriptKind.Track : ScriptKind.Life;
        var rec = SceneRecord.Parse(archive.Read(scene + 1));
        var a = rec.Actors[actor];
        var code = (kind == ScriptKind.Life ? rec.Life(a) : rec.Track(a)).ToArray();
        Console.WriteLine($"scene {scene} actor {actor} {kind}: {code.Length} bytes");
        for (var i = 0; i < code.Length; i += 16)
            Console.WriteLine($"{i,5}: " + string.Join(" ", code.Skip(i).Take(16).Select(b => b.ToString("X2"))));
        Console.WriteLine();

        ScriptFormatException? error;
        var list = kind == ScriptKind.Life ? Bytecode.DecodeLife(code, out error) : Bytecode.DecodeTrack(code, out error);
        foreach (var ins in list) Console.WriteLine($"{ins.Offset,5}: {Format(ins, kind)}");
        if (error is not null) Console.WriteLine($"  !! {error.Message}");
        return 0;
    }

    private static string Format(Instr i, ScriptKind kind)
    {
        string Args(ArgDef[] defs) => string.Join(", ", defs.Select((d, k) => d.Type == ArgType.CStr ? $"\"{i.Str}\"" : $"{d.Name}={i.A[k]}"));
        string Fn(CondDef cd) => cd.OperandName is null ? cd.Name : $"{cd.Name}({i.FuncArg})";

        if (kind == ScriptKind.Track)
        {
            var d = Opcodes.Track(i.Op)!;
            return d.Args.Length == 0 ? d.Name : $"{d.Name}({Args(d.Args)})";
        }
        var def = Opcodes.Life(i.Op)!;
        return def.Form switch
        {
            LifeForm.Cond => $"{def.Name} {Fn(Opcodes.Cond(i.Func)!)} {Opcodes.TestSymbols[i.Test]} {i.Value} -> {i.Target}",
            LifeForm.Switch => $"SWITCH {Fn(Opcodes.Cond(i.Func)!)}",
            LifeForm.Case => $"{def.Name} {Opcodes.TestSymbols[i.Test]} {i.Value} -> {i.Target}",
            _ => def.Args.Length == 0 ? def.Name : $"{def.Name}({Args(def.Args)})",
        };
    }

    // Every real scene as (numscene, record). HQR entry 0 is not a scene (see
    // MainWindow.BuildSceneEntries), so numscene = entry - 1. The archive's
    // own Count property runs ~4x too high (documented in HqrArchive), so the
    // real entry count comes from the header via HqrArchive.CountEntries.
    internal static IEnumerable<(int Scene, SceneRecord Record)> Scenes(HqrArchive archive)
    {
        var entries = HqrArchive.CountEntries(HqrPath);
        for (var i = 1; i < entries; i++)
        {
            if (!archive.IsValid(i)) continue;
            SceneRecord rec;
            try { rec = SceneRecord.Parse(archive.Read(i)); }
            catch (Exception e) { Console.WriteLine($"scene {i - 1}: PARSE FAILED: {e.Message}"); continue; }
            yield return (i - 1, rec);
        }
    }

    internal static string HqrPath = DefaultSceneHqr;

    private static int Stats(HqrArchive archive)
    {
        int scenes = 0, actors = 0, lifeScripts = 0, trackScripts = 0;
        int lifeFail = 0, trackFail = 0, badJump = 0, mismatch = 0;
        long lifeBytes = 0, trackBytes = 0;
        var lifeOps = new SortedDictionary<int, int>();
        var trackOps = new SortedDictionary<int, int>();
        var failures = new List<string>();

        foreach (var (scene, rec) in Scenes(archive))
        {
            scenes++;
            foreach (var a in rec.Actors)
            {
                actors++;
                Check(ScriptKind.Life, rec.Life(a).ToArray(), $"scene {scene} actor {a.Index} life", lifeOps, ref lifeScripts, ref lifeFail, ref lifeBytes);
                Check(ScriptKind.Track, rec.Track(a).ToArray(), $"scene {scene} actor {a.Index} track", trackOps, ref trackScripts, ref trackFail, ref trackBytes);
            }
        }

        void Check(ScriptKind kind, byte[] code, string who, SortedDictionary<int, int> ops, ref int count, ref int fail, ref long bytes)
        {
            if (code.Length == 0) return;
            count++;
            bytes += code.Length;
            List<Instr> ins;
            try { ins = kind == ScriptKind.Life ? Bytecode.DecodeLife(code) : Bytecode.DecodeTrack(code); }
            catch (ScriptFormatException e) { fail++; failures.Add($"{who}: {e.Message}"); return; }

            var starts = new HashSet<int>(ins.Select(i => i.Offset)) { code.Length };
            foreach (var i in ins)
            {
                ops[i.Op] = ops.GetValueOrDefault(i.Op) + 1;
                if (Bytecode.TryGetTarget(kind, i, out var t) && !starts.Contains(t))
                {
                    badJump++;
                    if (failures.Count < 40) failures.Add($"{who}: jump at {i.Offset} (op {i.Op}) targets {t}, not an instruction boundary");
                }
            }

            var re = kind == ScriptKind.Life ? Bytecode.EncodeLife(ins) : Bytecode.EncodeTrack(ins);
            if (!re.AsSpan().SequenceEqual(code))
            {
                mismatch++;
                if (failures.Count < 40) failures.Add($"{who}: re-encode differs from original");
            }
        }

        Console.WriteLine($"scenes={scenes} actors={actors}");
        Console.WriteLine($"life:  scripts={lifeScripts} bytes={lifeBytes} decodeFailures={lifeFail}");
        Console.WriteLine($"track: scripts={trackScripts} bytes={trackBytes} decodeFailures={trackFail}");
        Console.WriteLine($"jumps to non-boundary={badJump}  re-encode mismatches={mismatch}");
        foreach (var f in failures.Take(40)) Console.WriteLine("  " + f);

        Console.WriteLine("life opcode histogram:");
        foreach (var (op, n) in lifeOps) Console.WriteLine($"  {op,3} {Opcodes.Life((byte)op)?.Name,-26} {n}");
        Console.WriteLine("track opcode histogram:");
        foreach (var (op, n) in trackOps) Console.WriteLine($"  {op,3} {Opcodes.Track((byte)op)?.Name,-26} {n}");
        return lifeFail + trackFail + badJump + mismatch == 0 ? 0 : 1;
    }
}

// Track / life round-trip: decompile every script to C text, recompile the
// text, and require the exact original bytes.
internal static class RoundTrip
{
    public static int Run(HqrArchive archive, string mode)
    {
        int total = 0, ok = 0, flatScripts = 0;
        var reasons = new SortedDictionary<string, int>();
        var fails = new List<string>();
        foreach (var (scene, rec) in Program.Scenes(archive))
        {
            var syms = new SceneSymbols(rec);
            foreach (var a in rec.Actors)
            {
                if (mode is "life" or "all")
                {
                    total++;
                    var bytes = rec.Life(a).ToArray();
                    try
                    {
                        var text = LifeText.Decompile(bytes, a.Index, syms, null, m => { if (Environment.GetEnvironmentVariable("RT_VERBOSE") == "1") Console.WriteLine($"  scene {scene} actor {a.Index}: {m}"); reasons[m.Split(":").Last().Trim().Split(" at ")[0]] = reasons.GetValueOrDefault(m.Split(":").Last().Trim().Split(" at ")[0]) + 1; });
                        var compiled = LifeText.Compile(text, a.Index, syms);
                        compiled.ResolveExternals(r => LifeText.ResolveExternal(r, a.Index, syms, compiled));
                        if (compiled.Bytes.AsSpan().SequenceEqual(bytes)) ok++;
                        else fails.Add($"life scene {scene} actor {a.Index}: bytes differ");
                        if (text.Contains("goto ") || text.Contains("IF(") || text.Contains("ELSE(")) flatScripts++;
                    }
                    catch (Exception e) { fails.Add($"life scene {scene} actor {a.Index}: {e.GetType().Name}: {e.Message}"); }
                }
                if (mode is "track" or "all")
                {
                    total++;
                    var bytes = rec.Track(a).ToArray();
                    try
                    {
                        var text = TrackText.Decompile(bytes);
                        var back = TrackText.Compile(text).Bytes;
                        if (back.AsSpan().SequenceEqual(bytes)) ok++;
                        else fails.Add($"track scene {scene} actor {a.Index}: bytes differ ({bytes.Length} vs {back.Length})");
                    }
                    catch (Exception e) { fails.Add($"track scene {scene} actor {a.Index}: {e.Message}"); }
                }
            }
        }
        Console.WriteLine($"{mode}: {ok}/{total} scripts round-trip byte-exact; {flatScripts} life scripts needed some flat (goto/label) output");
        foreach (var f in fails.Take(25)) Console.WriteLine("  " + f);
        foreach (var (why, n) in reasons.OrderByDescending(p => p.Value)) Console.WriteLine($"  {n,5}  flat because: {why}");
        return ok == total ? 0 : 1;
    }
}

internal static class Show
{
    public static int Run(HqrArchive archive, string[] args)
    {
        var scene = int.Parse(args[1]);
        var actor = int.Parse(args[2]);
        var kind = args.Length > 3 ? args[3] : "track";
        var rec = SceneRecord.Parse(archive.Read(scene + 1));
        var a = rec.Actors[actor];
        var syms = new SceneSymbols(rec);
        if (kind == "track") Console.Write(TrackText.Decompile(rec.Track(a).ToArray(), $"scene {scene}, actor {actor} - track script"));
        else Console.Write(LifeText.Decompile(rec.Life(a).ToArray(), actor, syms, $"scene {scene}, actor {actor} - life script"));
        return 0;
    }
}
