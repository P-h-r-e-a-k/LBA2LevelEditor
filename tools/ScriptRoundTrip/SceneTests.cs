using LBA2LevelEditor;
using LBA2LevelEditor.LbaScript;

namespace ScriptRoundTrip;

// End-to-end tests of the scene-level pipeline against the real SCENE.HQR:
// SceneScripts (decompile / edit / recompile / re-point cross references),
// SceneRecord.Rebuild and HqrWriter.
internal static class SceneTests
{
    private static int failures;
    private static int checks;

    private static void Check(bool ok, string what)
    {
        checks++;
        if (ok) return;
        failures++;
        if (failures <= 25) Console.WriteLine($"  FAIL {what}");
    }

    public static int Run(HqrArchive archive)
    {
        int scenes = 0, editedScripts = 0, refsChecked = 0;

        foreach (var (scene, _) in Program.Scenes(archive))
        {
            scenes++;
            var original = archive.Read(scene + 1);

            // 1. no edits -> identical record
            var s0 = SceneScripts.Load(original, scene);
            var r0 = s0.Build();
            Check(r0.Ok && r0.Record!.AsSpan().SequenceEqual(original), $"scene {scene}: unedited Build() is not identical");

            // 2. force every script through decompile -> compile -> cross-ref resolution
            var s1 = SceneScripts.Load(original, scene);
            for (var a = 0; a < s1.ActorCount; a++)
            {
                s1.ForceRecompile(a, ScriptKind.Life);
                s1.ForceRecompile(a, ScriptKind.Track);
            }
            var r1 = s1.Build();
            Check(r1.Ok && r1.Record!.AsSpan().SequenceEqual(original),
                $"scene {scene}: full recompile differs ({(r1.Ok ? "bytes" : string.Join("; ", r1.Errors.Take(2)))})");
        }

        // 3. edits that move things other scripts point at
        foreach (var (scene, rec) in Program.Scenes(archive))
        {
            var original = archive.Read(scene + 1);
            var parsed = SceneRecord.Parse(original);

            for (var y = 0; y < parsed.Actors.Count; y++)
            {
                // 3a. a NOP at the front of actor y's track shifts every label by 1
                {
                    var s = SceneScripts.Load(original, scene);
                    var text = s.GetText(y, ScriptKind.Track);
                    s.SetText(y, ScriptKind.Track, "NOP();\n" + text);
                    editedScripts++;
                    var r = s.Build();
                    if (!r.Ok) { Check(false, $"scene {scene} actor {y}: track edit failed to build: {string.Join("; ", r.Errors.Take(2))}"); continue; }
                    var after = SceneRecord.Parse(r.Record!);
                    refsChecked += CheckTrackShift(scene, y, parsed, after);
                }

                // 3b. a NOP inside the first comportement of actor y's life shifts every later block by 1
                {
                    var s = SceneScripts.Load(original, scene);
                    var text = s.GetText(y, ScriptKind.Life);
                    var brace = text.IndexOf('{');
                    var edited = text.Contains("void ") && brace >= 0 ? text.Insert(brace + 1, " NOP();") : "NOP();\n" + text;
                    s.SetText(y, ScriptKind.Life, edited);
                    editedScripts++;
                    var r = s.Build();
                    if (!r.Ok) { Check(false, $"scene {scene} actor {y}: life edit failed to build: {string.Join("; ", r.Errors.Take(2))}"); continue; }
                    refsChecked += CheckLifeShift(scene, y, parsed, SceneRecord.Parse(r.Record!));
                }
            }
        }

        // 4. write back into an HQR copy
        HqrWriteBack(archive);

        // 5. error paths
        NegativeTests(archive);

        Console.WriteLine($"scene tests: {checks - failures}/{checks} checks passed ({editedScripts} edited scripts, {refsChecked} cross-references verified)");
        return failures == 0 ? 0 : 1;
    }

    // After inserting a 1-byte NOP at the start of actor y's track: y's track
    // is NOP + the old code (jumps +1); every reference to y's track anywhere in
    // the scene moved +1 too; nothing else changed.
    private static int CheckTrackShift(int scene, int y, SceneRecord before, SceneRecord after)
    {
        var refs = 0;
        Check(after.Actors.Count == before.Actors.Count, $"scene {scene} actor {y}: actor count changed");

        // y's track
        var oldTrack = Bytecode.DecodeTrack(before.Track(before.Actors[y]));
        var newTrack = Bytecode.DecodeTrack(after.Track(after.Actors[y]));
        var ok = newTrack.Count == oldTrack.Count + 1 + (oldTrack.Count > 0 && oldTrack[^1].Op == 0 ? 0 : 1) - (oldTrack.Count > 0 && oldTrack[^1].Op == 0 ? 0 : 1);
        Check(newTrack.Count > 0 && newTrack[0].Op == 1, $"scene {scene} actor {y}: track does not start with NOP");
        for (var k = 0; k < oldTrack.Count && k + 1 < newTrack.Count; k++)
        {
            var o = oldTrack[k]; var n = newTrack[k + 1];
            Check(o.Op == n.Op, $"scene {scene} actor {y}: track instruction {k} changed opcode");
            if (Bytecode.TryGetTarget(ScriptKind.Track, o, out var ot) && Bytecode.TryGetTarget(ScriptKind.Track, n, out var nt))
                Check(nt == ot + 1, $"scene {scene} actor {y}: track jump {k} not shifted (was {ot}, now {nt})");
        }

        for (var x = 0; x < before.Actors.Count; x++)
        {
            var bytesBefore = before.Life(before.Actors[x]).ToArray();
            var bytesAfter = after.Life(after.Actors[x]).ToArray();
            var insBefore = Bytecode.DecodeLife(bytesBefore);
            var insAfter = Bytecode.DecodeLife(bytesAfter);
            Check(insBefore.Count == insAfter.Count, $"scene {scene} actor {x}: life instruction count changed");
            for (var k = 0; k < insBefore.Count && k < insAfter.Count; k++)
            {
                var o = insBefore[k]; var n = insAfter[k];
                var pointsAtY = (o.Op == LifeText.OpSetTrack && x == y) || (o.Op == LifeText.OpSetTrackObj && (int)o.A[0] == y);
                if (pointsAtY)
                {
                    var ix = Opcodes.Life(o.Op)!.Args.Length - 1;
                    Check(n.A[ix] == o.A[ix] + 1, $"scene {scene} actor {x}: track ref at {o.Offset} not shifted (was {o.A[ix]}, now {n.A[ix]})");
                    refs++;
                }
                else Check(SameInstr(o, n), $"scene {scene} actor {x}: unrelated life instruction at {o.Offset} changed");
            }
            // track scripts of everyone else are untouched
            if (x != y) Check(before.Track(before.Actors[x]).SequenceEqual(after.Track(after.Actors[x])), $"scene {scene} actor {x}: unrelated track changed");
        }
        return refs;
    }

    // After inserting a 1-byte NOP into actor y's first comportement (or at the
    // start if it has none): every comportement of y that starts after offset 0
    // moved +1, and references to those from other actors followed.
    private static int CheckLifeShift(int scene, int y, SceneRecord before, SceneRecord after)
    {
        var refs = 0;
        var oldY = Bytecode.DecodeLife(before.Life(before.Actors[y]));
        var newY = Bytecode.DecodeLife(after.Life(after.Actors[y]));
        Check(newY.Count == oldY.Count + 1, $"scene {scene} actor {y}: life script should have gained one instruction ({oldY.Count} -> {newY.Count})");

        for (var x = 0; x < before.Actors.Count; x++)
        {
            if (x == y) continue;
            var insBefore = Bytecode.DecodeLife(before.Life(before.Actors[x]));
            var insAfter = Bytecode.DecodeLife(after.Life(after.Actors[x]));
            Check(insBefore.Count == insAfter.Count, $"scene {scene} actor {x}: life instruction count changed");
            for (var k = 0; k < insBefore.Count && k < insAfter.Count; k++)
            {
                var o = insBefore[k]; var n = insAfter[k];
                if (o.Op == LifeText.OpSetComportementObj && (int)o.A[0] == y)
                {
                    var oldOff = (int)o.A[1];
                    var expected = oldOff > 0 ? oldOff + 1 : oldOff;
                    Check(n.A[1] == expected, $"scene {scene} actor {x}: comportement ref at {o.Offset} to actor {y} (was {oldOff}, now {n.A[1]}, expected {expected})");
                    refs++;
                }
                else Check(SameInstr(o, n), $"scene {scene} actor {x}: unrelated life instruction at {o.Offset} changed");
            }
        }
        return refs;
    }

    private static bool SameInstr(Instr a, Instr b) =>
        a.Op == b.Op && a.A.SequenceEqual(b.A) && a.Str == b.Str && a.Func == b.Func && a.FuncArg == b.FuncArg &&
        a.Test == b.Test && a.Value == b.Value && a.Target == b.Target;

    // Scene 2's actor 0 does SET_TRACK_OBJ(8, label_0). Renaming that label in
    // actor 8's track must be reported against actor 0, not silently mis-jump.
    private static void NegativeTests(HqrArchive archive)
    {
        var original = archive.Read(2 + 1);

        var s = SceneScripts.Load(original, 2);
        var track8 = s.GetText(8, ScriptKind.Track);
        Check(track8.Contains("LABEL(0);"), "negative: scene 2 actor 8 should have LABEL(0)");
        s.SetText(8, ScriptKind.Track, track8.Replace("LABEL(0);", "LABEL(77);"));
        var r = s.Build();
        Check(!r.Ok && r.Errors.Any(e => e.Actor == 0 && e.Kind == ScriptKind.Life && e.Message.Contains("label_0") && e.Message.Contains("actor 8")),
            $"negative: dangling SET_TRACK_OBJ not reported ({(r.Ok ? "build succeeded" : string.Join("; ", r.Errors))})");

        // a syntax error is attributed to the right actor/script/line
        s = SceneScripts.Load(original, 2);
        var life0 = s.GetText(0, ScriptKind.Life);
        s.SetText(0, ScriptKind.Life, life0 + "\nvoid broken() {\n  FROBNICATE(1);\n}\n");
        r = s.Build();
        Check(!r.Ok && r.Errors.Count == 1 && r.Errors[0].Actor == 0 && r.Errors[0].Kind == ScriptKind.Life
              && r.Errors[0].Message.Contains("Unknown life opcode") && r.Errors[0].Line == life0.Count(c => c == '\n') + 3,
            $"negative: syntax error mis-attributed ({(r.Ok ? "build succeeded" : string.Join("; ", r.Errors))})");

        // CheckText reports size / errors without touching stored state
        s = SceneScripts.Load(original, 2);
        var (size, err) = s.CheckText(0, ScriptKind.Life, life0);
        Check(err is null && size == SceneRecord.Parse(original).Life(SceneRecord.Parse(original).Actors[0]).Length, "negative: CheckText size of unchanged text");
        (_, err) = s.CheckText(0, ScriptKind.Life, "void a() { ANIM(70000); }");
        Check(err is not null && err.Message.Contains("out of range"), "negative: CheckText range error");
        Check(!s.IsEdited(0, ScriptKind.Life), "negative: CheckText must not mark a script edited");

        // typing the original text back is "no edit"
        s.SetText(0, ScriptKind.Life, life0 + "// note\n");
        Check(s.IsEdited(0, ScriptKind.Life), "negative: comment-only change counts as an edit");
        s.SetText(0, ScriptKind.Life, life0);
        Check(!s.IsEdited(0, ScriptKind.Life), "negative: restoring the original text clears the edit");
    }

    // Edits a scene, writes it into a copy of SCENE.HQR through HqrWriter, and
    // checks the copy: the edited entry reads back as the new record and every
    // other entry is unchanged.
    private static void HqrWriteBack(HqrArchive archive)
    {
        var path = Program.HqrPath;
        var hqrBytes = File.ReadAllBytes(path);
        var entries = HqrArchive.CountEntries(path);
        var tested = 0;

        foreach (var scene in new[] { 0, 5, 48, 100, 150 })
        {
            var entry = scene + 1;
            if (entry >= entries || !archive.IsValid(entry)) continue;
            var original = archive.Read(entry);
            var s = SceneScripts.Load(original, scene);
            var text = s.GetText(1, ScriptKind.Track);
            s.SetText(1, ScriptKind.Track, "NOP();\n" + text);
            var built = s.Build();
            if (!built.Ok) { Check(false, $"hqr writeback scene {scene}: build failed"); continue; }

            var newHqr = HqrWriter.ReplaceEntry(hqrBytes, entry, HqrWriter.StoredEntry(built.Record!));
            var tmp = Path.Combine(Path.GetTempPath(), $"scene_writeback_{Environment.ProcessId}.hqr");
            File.WriteAllBytes(tmp, newHqr);
            try
            {
                var reopened = HqrArchive.Open(tmp);
                Check(reopened.Read(entry).AsSpan().SequenceEqual(built.Record!), $"hqr writeback scene {scene}: edited entry does not read back");
                var others = 0;
                for (var i = 0; i < entries; i++)
                {
                    if (i == entry || !archive.IsValid(i)) continue;
                    Check(reopened.IsValid(i) && reopened.Read(i).AsSpan().SequenceEqual(archive.Read(i)), $"hqr writeback scene {scene}: entry {i} changed");
                    others++;
                }
                Check(HqrArchive.CountEntries(tmp) == entries, $"hqr writeback scene {scene}: entry count changed");
                tested++;
            }
            finally { File.Delete(tmp); }
        }

        // replacing an entry with its own bytes must reproduce the file exactly
        var probe = 10;
        var start = (int)BitConverter.ToUInt32(hqrBytes, probe * 4);
        var next = hqrBytes.Length;
        for (var i = 0; i < entries; i++)
        {
            var o = (int)BitConverter.ToUInt32(hqrBytes, i * 4);
            if (o > start && o < next) next = o;
        }
        var same = HqrWriter.ReplaceEntry(hqrBytes, probe, hqrBytes.AsSpan(start, next - start).ToArray());
        Check(same.AsSpan().SequenceEqual(hqrBytes), "hqr identity replace changed the file");
        Console.WriteLine($"hqr write-back: {tested} scenes written to a temp copy and re-read");
    }
}
