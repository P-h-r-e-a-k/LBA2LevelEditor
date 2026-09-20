using LBA2LevelEditor;
using LBA2LevelEditor.LbaScript;

namespace ScriptRoundTrip;

// LBA1 scripts against the retail SCENE.HQR: every life/track script must decode to exactly its length with
// every jump landing on an instruction boundary, re-encode byte-exact, and survive C text -> bytes.
internal static class Lba1Tests
{
    private const string DefaultPath = @"E:\GOG Games\Little Big Adventure\SCENE.HQR";


    // Hand-written LBA1 C: AND chains, OR chains, while, and what LBA1 doesn't have.
    public static int SelfTest()
    {
        using var scope = Opcodes.Use(Opcodes.Lba1);
        var failures = 0;
        void Check(string what, bool ok) { if (!ok) { failures++; Console.WriteLine("FAIL: " + what); } else Console.WriteLine("ok:   " + what); }

        byte[] Compile(string text) { var c = LifeText.Compile(text, 0, NoSymbols.Instance); c.ResolveExternals(_ => 0); return c.Bytes; }
        bool Fails(string text) { try { Compile(text); return false; } catch (ScriptCompileException) { return true; } }

        var and = Compile("void comportement_0()\n{\n    if (1 == zone() && 2 == col())\n    {\n        anim(3);\n    }\n}\n");
        var ops = Bytecode.DecodeLife(and).Select(i => i.Op).ToArray();
        Check("a && b compiles to IF, IF, ANIM, END_COMPORTEMENT, END", ops.SequenceEqual(new byte[] { 12, 12, 19, 35, 0 }));
        var text = LifeText.Decompile(and, 0, NoSymbols.Instance);
        Check("...and reads back as nested ifs that recompile to the same bytes", Compile(text).AsSpan().SequenceEqual(and));

        var or = Compile("void comportement_0()\n{\n    if (1 == zone() || 2 == col())\n    {\n        anim(3);\n    }\n}\n");
        Check("a || b compiles to OR_IF, IF", Bytecode.DecodeLife(or).Select(i => i.Op).Take(2).SequenceEqual(new byte[] { 55, 12 }));
        Check("while round-trips", Compile(LifeText.Decompile(Compile("void comportement_0()\n{\n    while (0 != zone())\n    {\n        anim(1);\n    }\n}\n"), 0, NoSymbols.Instance)) is { Length: > 0 });
        Check("switch is refused", Fails("void comportement_0()\n{\n    switch (zone())\n    {\n        case 1:\n            anim(1);\n            break;\n    }\n}\n"));
        Check("an LBA2-only opcode is refused", Fails("void comportement_0()\n{\n    set_frame(1);\n}\n"));
        var dir = Bytecode.DecodeLife(Compile("void comportement_0()\n{\n    set_dir(MOVE_FOLLOW, 3);\n    set_dir(MOVE_SAME_XZ);\n}\n"));
        Check("SET_DIR: 3 bytes for FOLLOW, 2 for SAME_XZ", dir[0].Length == 3 && dir[1].Length == 2);

        var track = TrackText.Compile("label(1);\nanim(5);\nwait_nb_second(2);\ngoto(@-1);\n").Bytes;
        Check("track: hidden timer bytes are synthesised, goto @-1 is legal", track.Length == 2 + 2 + 6 + 3 + 1 && TrackText.Compile(TrackText.Decompile(track)).Bytes.AsSpan().SequenceEqual(track));
        Console.WriteLine(failures == 0 ? "lba1 selftest: all passed" : $"lba1 selftest: {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    public static int Run(string[] args)
    {
        var path = Environment.GetEnvironmentVariable("LBA1_SCENE_HQR") ?? DefaultPath;
        var archive = HqrArchive.Open(path);
        var entries = HqrArchive.CountEntries(path);
        var mode = args.Length > 1 ? args[1] : "all";
        if (mode == "selftest") return SelfTest();

        if (mode == "show")
        {
            var sc = int.Parse(args[2]);
            var s1 = SceneScripts.Load(archive.Read(sc), sc, null, lba1: true);
            var ac = int.Parse(args[3]);
            var kd = args.Length > 4 && args[4] == "track" ? ScriptKind.Track : ScriptKind.Life;
            var bytes = SceneRecord.ParseLba1(archive.Read(sc));
            var blob = (kd == ScriptKind.Life ? bytes.Life(bytes.Actors[ac]) : bytes.Track(bytes.Actors[ac])).ToArray();
            Console.WriteLine(string.Join(" ", blob.Select(b => b.ToString("X2"))));
            Console.WriteLine(s1.GetText(ac, kd));
            return 0;
        }
        int scenes = 0, actors = 0, decodeFail = 0, badJump = 0, reencodeFail = 0, textFail = 0, buildFail = 0, flatBlocks = 0;
        long lifeBytes = 0, trackBytes = 0;
        var lifeOps = new SortedDictionary<int, int>();
        var trackOps = new SortedDictionary<int, int>();
        var failures = new List<string>();

        for (var scene = 0; scene < entries; scene++)
        {
            if (!archive.IsValid(scene)) continue;
            var raw = archive.Read(scene);
            SceneRecord rec;
            try { rec = SceneRecord.ParseLba1(raw); }
            catch (Exception e) { failures.Add($"scene {scene}: record parse failed: {e.Message}"); continue; }
            scenes++;

            using var scope = Opcodes.Use(Opcodes.Lba1);
            foreach (var a in rec.Actors)
            {
                actors++;
                foreach (var kind in new[] { ScriptKind.Life, ScriptKind.Track })
                {
                    var code = (kind == ScriptKind.Life ? rec.Life(a) : rec.Track(a)).ToArray();
                    if (kind == ScriptKind.Life) lifeBytes += code.Length; else trackBytes += code.Length;
                    ScriptFormatException? error;
                    var list = kind == ScriptKind.Life ? Bytecode.DecodeLife(code, out error) : Bytecode.DecodeTrack(code, out error);
                    var who = $"scene {scene} actor {a.Index} {kind}";
                    if (error is not null) { decodeFail++; failures.Add($"{who}: {error.Message}"); continue; }
                    foreach (var i in list)
                    {
                        var ops = kind == ScriptKind.Life ? lifeOps : trackOps;
                        ops[i.Op] = ops.GetValueOrDefault(i.Op) + 1;
                    }

                    var starts = list.Select(i => i.Offset).ToHashSet();
                    starts.Add(code.Length);
                    foreach (var i in list)
                        if (Bytecode.TryGetTarget(kind, i, out var t) && t != 0xFFFF && t != -1 && !starts.Contains(t))
                        {
                            badJump++;
                            failures.Add($"{who}: {(kind == ScriptKind.Life ? Opcodes.Life(i.Op)!.Name : Opcodes.Track(i.Op)!.Name)} at {i.Offset} jumps to {t}, not an instruction start");
                            break;
                        }

                    var again = kind == ScriptKind.Life ? Bytecode.EncodeLife(list) : Bytecode.EncodeTrack(list);
                    if (!again.AsSpan().SequenceEqual(code)) { reencodeFail++; failures.Add($"{who}: re-encoded bytes differ"); }
                }
            }
            if (mode == "decode") continue;

            // text round trip through the whole scene layer: every script compiled from its own text must give the original record.
            try
            {
                var s = SceneScripts.Load(raw, scene, null, lba1: true);
                for (var actor = 0; actor < s.ActorCount; actor++)
                    foreach (var kind in new[] { ScriptKind.Life, ScriptKind.Track })
                    {
                        var text = s.GetText(actor, kind);
                        var (size, error, _) = s.CheckTextFull(actor, kind, text);
                        if (error is not null) { textFail++; failures.Add($"scene {scene} actor {actor} {kind}: own text does not compile: {error}"); }
                        else if (size != s.OriginalSize(actor, kind)) { textFail++; failures.Add($"scene {scene} actor {actor} {kind}: text compiles to {size} bytes, stored {s.OriginalSize(actor, kind)}"); }
                        if (kind == ScriptKind.Life)
                            flatBlocks += text.Split('\n').Count(l => l.TrimStart().StartsWith("IF(", StringComparison.Ordinal) || l.TrimStart().StartsWith("goto ", StringComparison.Ordinal));
                        s.ForceRecompile(actor, kind);
                    }
                var built = s.Build();
                if (!built.Ok) { buildFail++; failures.Add($"scene {scene}: build failed: {built.Errors.FirstOrDefault()}"); }
                else if (!built.Record!.AsSpan().SequenceEqual(raw)) { buildFail++; failures.Add($"scene {scene}: rebuilt record differs from the original ({built.Record.Length} vs {raw.Length} bytes)"); }
            }
            catch (Exception e)
            {
                textFail++;
                failures.Add($"scene {scene}: {e.GetType().Name}: {e.Message}");
            }
        }

        Console.WriteLine($"LBA1: {scenes} scenes, {actors} actors, life {lifeBytes} bytes, track {trackBytes} bytes");
        Console.WriteLine($"decode failures {decodeFail}, bad jumps {badJump}, re-encode mismatches {reencodeFail}, text failures {textFail}, build mismatches {buildFail}, flat statements {flatBlocks}");
        Console.WriteLine("life opcodes used: " + string.Join(" ", lifeOps.Select(p => $"{p.Key}:{p.Value}")));
        Console.WriteLine("track opcodes used: " + string.Join(" ", trackOps.Select(p => $"{p.Key}:{p.Value}")));
        foreach (var f in failures.Take(40)) Console.WriteLine("  " + f);
        if (failures.Count > 40) Console.WriteLine($"  ... and {failures.Count - 40} more");
        return failures.Count == 0 ? 0 : 1;
    }
}
