using System.Text;
using System.Text.RegularExpressions;
using LBA2LevelEditor;
using LBA2LevelEditor.LbaScript;

namespace ScriptRoundTrip;

// Tests of the comment layer: comments are extracted from user text, stored apart
// from the bytecode, and woven back into the text regenerated from the bytes.
internal static class CommentTests
{
    private static int checks;
    private static int failures;

    private static void Check(bool ok, string what)
    {
        checks++;
        if (ok) return;
        failures++;
        if (failures <= 20) Console.WriteLine($"  FAIL {what}");
    }

    private static void CheckText(string actual, string expected, string what)
    {
        checks++;
        if (actual == expected) return;
        failures++;
        if (failures > 20) return;
        Console.WriteLine($"  FAIL {what}\n    --- expected ---\n{Indent(expected)}    --- actual ---\n{Indent(actual)}");
    }

    private static string Indent(string s) => string.Concat(s.Split('\n').Select(l => "    | " + l + "\n"));

    public static int Run(HqrArchive archive)
    {
        UnitCases();
        Aligner();
        CorpusIdentity(archive);
        EndToEnd(archive);
        Console.WriteLine($"comment tests: {checks - failures}/{checks} checks passed");
        return failures == 0 ? 0 : 1;
    }

    // `commentdemo`: writes a real sidecar for a tiny edit and prints it, to see the file format.
    public static int Demo(HqrArchive archive)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lba_comment_demo_{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Program.HqrPath, Path.Combine(dir, "SCENE.HQR"), overwrite: true);
            var sidecar = Path.Combine(dir, "SCENE.HQR.comments.json");
            var session = new ScriptSession(() => dir);
            var s = session.GetScene(0)!;
            var text = s.GetText(2, ScriptKind.Life);
            Console.WriteLine("---- text before ----\n" + text);
            text = text.Replace("void comportement_0() {\n", "// what the door does when Twinsen is inside\nvoid comportement_0() {\n")
                       .Replace("        SET_TRACK(label_100);\n", "        SET_TRACK(label_100);  // stop\n");
            s.SetText(2, ScriptKind.Life, text);
            Console.WriteLine(session.SaveAll().Message + "\n");
            Console.WriteLine("---- " + Path.GetFileName(sidecar) + " ----\n" + File.ReadAllText(sidecar));
            Console.WriteLine("---- text after reload ----\n" + session.GetScene(0)!.GetText(2, ScriptKind.Life));
        }
        finally { Directory.Delete(dir, recursive: true); }
        return 0;
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    // source text -> compile -> extract comments -> regenerate from the bytes -> weave.
    private static string Roundtrip(string source, ScriptKind kind, string? header = null)
    {
        var syms = NoSymbols.Instance;
        CompiledScript c;
        if (kind == ScriptKind.Life) { c = LifeText.Compile(source, 0, syms); c.ResolveExternals(_ => 0); }
        else c = TrackText.Compile(source);
        var set = CommentExtractor.Extract(source, header, c.Asm.Code, c.Blocks, kind);
        var d = kind == ScriptKind.Life ? LifeText.DecompileMapped(c.Bytes, 0, syms, header) : TrackText.DecompileMapped(c.Bytes, header);
        return CommentWeaver.Apply(d, set, kind).Text;
    }

    // ---------------------------------------------------------------------
    // unit cases
    // ---------------------------------------------------------------------

    private static void UnitCases()
    {
        // leading / trailing / end-of-block / after-the-function comments
        CheckText(Roundtrip(
            "// guard\nvoid comportement_0() {\n    // check the door\n    if (ZONE() == 1) { // inside\n        BODY(2);\n    } else {\n        BODY(3); // other\n    }\n    // end of body\n}\n// after\n",
            ScriptKind.Life),
            "// guard\nvoid comportement_0() {\n    // check the door\n    if (ZONE() == 1) {  // inside\n        BODY(2);\n    } else {\n        BODY(3);  // other\n    }\n    // end of body\n}\n\n// after\n",
            "leading/trailing/end comments");

        // "//" inside a string is not a comment; block comments become // lines
        CheckText(Roundtrip(
            "void comportement_0() {\n    PLAY_ACF(\"x//y\"); /* keep */\n    /* block\n       spanning */\n    BODY(1);\n}\n", ScriptKind.Life),
            "void comportement_0() {\n    PLAY_ACF(\"x//y\");  // keep\n    // block\n    // spanning\n    BODY(1);\n}\n",
            "string contents and block comments");

        // a comment inside an empty block stays inside it
        CheckText(Roundtrip("void comportement_0() {\n    if (ZONE() == 1) {\n        // nothing yet\n    }\n}\n", ScriptKind.Life),
            "void comportement_0() {\n    if (ZONE() == 1) {\n        // nothing yet\n    }\n}\n", "empty block");

        // else-if / while / switch, comments before case and default
        const string control =
            "void comportement_0() {\n    // pick\n    if (ZONE() == 1) {\n        BODY(1);\n    } else if (ZONE() == 2) {  // second\n        BODY(2);\n    } else {\n        BODY(3);\n    }\n" +
            "    while (ACTION() == 0) {\n        // spin\n        NOP();\n    }\n    switch (RND(3)) {\n    // zero\n    case 0:\n        BODY(4);\n        break;\n    // fallback\n    default:\n        BODY(5);\n    }\n}\n";
        CheckText(Roundtrip(control, ScriptKind.Life), control, "else-if / while / switch");

        // statements outside any function, with a comment at the end
        CheckText(Roundtrip("// note about tail\nSUICIDE();\n// trailing thought\n", ScriptKind.Life),
            "// note about tail\nSUICIDE();\n\n// trailing thought\n", "tail statements");

        // the generated header line is never stored (it is regenerated on load)
        {
            const string header = "scene 2, actor 5 - life script";
            var text = $"// {header}\n\nvoid comportement_0() {{\n    BODY(1);\n}}\n";
            var syms = NoSymbols.Instance;
            var c = LifeText.Compile(text, 0, syms);
            var set = CommentExtractor.Extract(text, header, c.Asm.Code, c.Blocks, ScriptKind.Life);
            Check(set.IsEmpty, "header comment must not be stored as a user comment");
            var edited = text.Replace("BODY(1);", "BODY(1); // mine");
            c = LifeText.Compile(edited, 0, syms);
            set = CommentExtractor.Extract(edited, header, c.Asm.Code, c.Blocks, ScriptKind.Life);
            Check(set.Comments.Count == 1 && set.Comments[0].Place == CommentPlace.Trailing, "only the user comment is stored next to the header");
        }

        // track scripts
        CheckText(Roundtrip("// go\nLABEL(0);\nSAMPLE(1); // ping\nWAIT_NB_SECOND(2);\n// loop back\nGOTO(label_0);\n", ScriptKind.Track),
            "// go\nLABEL(0);\nSAMPLE(1);  // ping\nWAIT_NB_SECOND(2);\n// loop back\nGOTO(label_0);\n", "track comments");

        // a text with no comments is left exactly as decompiled
        CheckText(Roundtrip("void comportement_0() {\n    BODY(1);\n}\n", ScriptKind.Life), "void comportement_0() {\n    BODY(1);\n}\n", "no comments");
    }

    private static void Aligner()
    {
        static string[] F(string s) => s.Split(' ').Select(x => x + "00000000").ToArray();   // "01" = opcode 01

        var map = CommentAligner.Align(F("01 02 03"), F("01 02 03"));
        Check(map.SequenceEqual(new[] { 0, 1, 2 }), "align identical");

        map = CommentAligner.Align(F("01 02 03 04"), F("01 03 04"));           // 02 deleted
        Check(map.SequenceEqual(new[] { 0, -1, 1, 2 }), "align deletion");

        map = CommentAligner.Align(F("01 02 03"), F("05 01 02 03"));           // inserted at the front
        Check(map.SequenceEqual(new[] { 1, 2, 3 }), "align insertion at front");

        map = CommentAligner.Align(F("01 02 03"), F("01 02 06 03"));           // inserted in the middle
        Check(map.SequenceEqual(new[] { 0, 1, 3 }), "align insertion in the middle");

        var a = new[] { "0A11111111", "0B22222222", "0C33333333" };
        var b = new[] { "0A11111111", "0B99999999", "0C33333333" };            // 0B edited in place (same opcode, new operands)
        Check(CommentAligner.Align(a, b).SequenceEqual(new[] { 0, 1, 2 }), "align in-place edit");

        var c2 = new[] { "0A11111111", "0B22222222", "0D44444444", "0C33333333" };
        var d2 = new[] { "0A11111111", "0B99999999", "0C33333333" };            // gap 2 -> 1: pair by opcode
        Check(CommentAligner.Align(c2, d2).SequenceEqual(new[] { 0, 1, -1, 2 }), "align gap of different size pairs by opcode");

        Check(CommentAligner.Align(Array.Empty<string>(), F("01")).Length == 0, "align empty old");
        Check(CommentAligner.Align(F("01 02"), Array.Empty<string>()).SequenceEqual(new[] { -1, -1 }), "align empty new");

        // fingerprints ignore offsets but not real operands
        var ins1 = new Instr { Op = 23, A = new long[] { 10 } };   // SET_TRACK(offset 10)
        var ins2 = new Instr { Op = 23, A = new long[] { 99 } };   // same op, other offset -> same fingerprint
        Check(Fingerprint.Of(ins1, ScriptKind.Life) == Fingerprint.Of(ins2, ScriptKind.Life), "fingerprint ignores SET_TRACK offset");
        var m1 = new Instr { Op = 25, A = new long[] { 5 } };      // MESSAGE 5
        var m2 = new Instr { Op = 25, A = new long[] { 6 } };
        Check(Fingerprint.Of(m1, ScriptKind.Life) != Fingerprint.Of(m2, ScriptKind.Life), "fingerprint sees a changed message number");
    }

    // ---------------------------------------------------------------------
    // Every script in the game: comments injected at every eligible position must
    // survive extract -> regenerate-from-bytes -> weave with the text unchanged.
    // ---------------------------------------------------------------------

    private static readonly Regex LabelOnly = new(@"^L\d+:$");

    // Adds a comment above every statement line, a trailing comment on every line that
    // carries an instruction, a comment at the end of every block (above each closing
    // brace) and one at the end of the script -- all in the positions the weaver writes.
    private static string Inject(string canonical)
    {
        var lines = DecompiledScript.Split(canonical);
        var sb = new StringBuilder();
        var n = 0;
        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            var code = line.Trim();
            var indent = line.Length - line.TrimStart().Length;

            if (li == 0 && code.StartsWith("//")) { sb.Append(line).Append('\n'); continue; }   // generated header
            if (code.Length == 0) { sb.Append('\n'); continue; }

            if (code.StartsWith('}'))
            {
                sb.Append(' ', indent + 4).Append("// c").Append(++n).Append('\n').Append(line).Append('\n');
                continue;
            }
            if (code == ";") { sb.Append(line).Append('\n'); continue; }    // empty statement: no instruction to attach to

            sb.Append(' ', indent).Append("// c").Append(++n).Append('\n').Append(line);
            if (!LabelOnly.IsMatch(code)) sb.Append("  // t").Append(++n);
            sb.Append('\n');
        }
        sb.Append('\n').Append("// end\n");
        return sb.ToString();
    }

    private static void CorpusIdentity(HqrArchive archive)
    {
        int scripts = 0, comments = 0;
        foreach (var (scene, rec) in Program.Scenes(archive))
        {
            var syms = new SceneSymbols(rec);
            foreach (var a in rec.Actors)
            {
                foreach (var kind in new[] { ScriptKind.Life, ScriptKind.Track })
                {
                    var bytes = (kind == ScriptKind.Life ? rec.Life(a) : rec.Track(a)).ToArray();
                    var who = $"scene {scene} actor {a.Index} {kind}";
                    var header = $"scene {scene}, actor {a.Index} - {kind.ToString().ToLowerInvariant()} script";
                    try
                    {
                        var d = kind == ScriptKind.Life ? LifeText.DecompileMapped(bytes, a.Index, syms, header) : TrackText.DecompileMapped(bytes, header);
                        var injected = Inject(d.Text);

                        CompiledScript c;
                        if (kind == ScriptKind.Life)
                        {
                            c = LifeText.Compile(injected, a.Index, syms);
                            c.ResolveExternals(r => LifeText.ResolveExternal(r, a.Index, syms, c));
                        }
                        else c = TrackText.Compile(injected);
                        if (!c.Bytes.AsSpan().SequenceEqual(bytes)) { Check(false, $"{who}: text with comments no longer compiles to the same bytes"); continue; }

                        var set = CommentExtractor.Extract(injected, header, c.Asm.Code, c.Blocks, kind);
                        var d2 = kind == ScriptKind.Life ? LifeText.DecompileMapped(c.Bytes, a.Index, syms, header) : TrackText.DecompileMapped(c.Bytes, header);
                        var woven = CommentWeaver.Apply(d2, set, kind);
                        scripts++;
                        comments += set.Comments.Sum(x => x.Lines.Count);
                        if (woven.Text == injected && woven.Detached == 0) { checks++; continue; }

                        Check(false, $"{who}: comments did not round-trip (first difference: {FirstDiff(injected, woven.Text)})");
                    }
                    catch (Exception e) { Check(false, $"{who}: {e.GetType().Name}: {e.Message}"); }
                }
            }
        }
        Console.WriteLine($"corpus identity: {scripts} scripts, {comments} comments injected and recovered in place");
    }

    private static string FirstDiff(string expected, string actual)
    {
        var e = expected.Split('\n'); var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var x = i < e.Length ? e[i] : "<none>"; var y = i < a.Length ? a[i] : "<none>";
            if (x != y) return $"line {i + 1}: expected \"{x}\" but got \"{y}\"";
        }
        return "?";
    }

    // ---------------------------------------------------------------------
    // Real files: a temp copy of SCENE.HQR plus a real sidecar.
    // ---------------------------------------------------------------------

    private static void EndToEnd(HqrArchive archive)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lba_comments_{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        try
        {
            var hqr = Path.Combine(dir, "SCENE.HQR");
            File.Copy(Program.HqrPath, hqr, overwrite: true);
            var original = File.ReadAllBytes(hqr);
            var sidecar = Path.Combine(dir, "comments.json");
            ScriptSession Session(string comments) => new(() => dir, comments);

            const int scene = 2, lifeActor = 0, trackActor = 5;

            // 1. add comments to a life and a track script and save: SCENE.HQR must not change
            var a = Session(sidecar);
            var sa = a.GetScene(scene)!;
            var life = Inject(sa.GetText(lifeActor, ScriptKind.Life));
            var trk = Inject(sa.GetText(trackActor, ScriptKind.Track));
            sa.SetText(lifeActor, ScriptKind.Life, life);
            sa.SetText(trackActor, ScriptKind.Track, trk);
            var saved = a.SaveAll();
            Check(saved.Ok, $"e2e: comment-only save failed: {saved.Message}");
            Check(File.Exists(sidecar), "e2e: sidecar file was not written");
            Check(File.ReadAllBytes(hqr).AsSpan().SequenceEqual(original), "e2e: a comment-only save must leave SCENE.HQR byte-identical");
            Check(!File.Exists(hqr + ".bak"), "e2e: a comment-only save must not create SCENE.HQR.bak");

            // 2. a brand new session (a "restart") sees the same text, and nothing counts as edited
            var b = Session(sidecar);
            var sb = b.GetScene(scene)!;
            CheckText(sb.GetText(lifeActor, ScriptKind.Life), life, "e2e: life script text after reload");
            CheckText(sb.GetText(trackActor, ScriptKind.Track), trk, "e2e: track script text after reload");
            Check(!sb.IsEdited(lifeActor, ScriptKind.Life) && !sb.IsEdited(trackActor, ScriptKind.Track), "e2e: freshly loaded scripts must not look edited");
            Check(sb.DetachedComments(lifeActor, ScriptKind.Life) == 0 && sb.DetachedComments(trackActor, ScriptKind.Track) == 0, "e2e: nothing should be detached after a clean reload");

            // 3. edit the code (keeping the comments) and save: the bytes change, the comments follow
            var headerLine = life.Split('\n').First(l => l.StartsWith("void comportement_0()"));
            var edited = life.Replace(headerLine + "\n", headerLine + "\n    NOP();\n");
            Check(edited != life, "e2e: test setup could not find the first function header");
            sb.SetText(lifeActor, ScriptKind.Life, edited);
            saved = b.SaveAll();
            Check(saved.Ok, $"e2e: code edit save failed: {saved.Message}");
            Check(!File.ReadAllBytes(hqr).AsSpan().SequenceEqual(original), "e2e: a code edit must change SCENE.HQR");
            Check(File.Exists(hqr + ".bak") && File.ReadAllBytes(hqr + ".bak").AsSpan().SequenceEqual(original), "e2e: backup must hold the untouched original");

            // the same session, reloading right after Save (what the editor window does)
            CheckText(b.GetScene(scene)!.GetText(lifeActor, ScriptKind.Life), edited, "e2e: same-session reload after save");

            var c = Session(sidecar);
            var sc = c.GetScene(scene)!;
            CheckText(sc.GetText(lifeActor, ScriptKind.Life), edited, "e2e: life text after editing code around comments");
            CheckText(sc.GetText(trackActor, ScriptKind.Track), trk, "e2e: unrelated track comments untouched");

            // 4. the script changes underneath the stored comments (another tool / editor session
            //    with a different sidecar): comments realign to their statements by fingerprint
            var d = Session(Path.Combine(dir, "other.json"));
            var sd = d.GetScene(scene)!;
            var plain = sd.GetText(lifeActor, ScriptKind.Life);
            var plainHeader = plain.Split('\n').First(l => l.StartsWith("void comportement_0()"));
            sd.SetText(lifeActor, ScriptKind.Life, plain.Replace(plainHeader + "\n", plainHeader + "\n    SET_VAR_CUBE(9, 9);\n"));
            Check(d.SaveAll().Ok, "e2e: external edit failed to save");
            Check(!File.Exists(Path.Combine(dir, "other.json")), "e2e: a session without comments must not create a sidecar");

            var e = Session(sidecar);
            var se = e.GetScene(scene)!;
            var realigned = se.GetText(lifeActor, ScriptKind.Life);
            CheckText(realigned, edited.Replace(headerLine + "\n", headerLine + "\n    SET_VAR_CUBE(9, 9);\n"), "e2e: comments must follow their statements after an external change");
            Check(se.DetachedComments(lifeActor, ScriptKind.Life) == 0, "e2e: realigned comments are not detached");

            // 5. a commented statement disappears: its comments are kept, visibly, not dropped
            var target = realigned.Split('\n').First(l => Regex.IsMatch(l, @"^\s+[A-Z_]+\(.*\);  // t\d+$") && !l.Contains("SET_VAR_CUBE(9, 9)"));
            var targetCode = Regex.Match(target, @"^(\s+[A-Z_]+\(.*\);)  // t\d+$").Groups[1].Value;
            var trailingWord = Regex.Match(target, @"// (t\d+)$").Groups[1].Value;
            var f = Session(Path.Combine(dir, "third.json"));
            var sf = f.GetScene(scene)!;
            var fText = sf.GetText(lifeActor, ScriptKind.Life);
            var fLines = fText.Split('\n').ToList();
            var removeAt = fLines.IndexOf(targetCode);
            Check(removeAt >= 0, $"e2e: test setup could not find statement '{targetCode.Trim()}' in the plain text");
            if (removeAt >= 0)
            {
                fLines.RemoveAt(removeAt);
                sf.SetText(lifeActor, ScriptKind.Life, string.Join('\n', fLines));
                var fs = f.SaveAll();
                Check(fs.Ok, $"e2e: deleting a statement failed to save: {fs.Message}");

                var g = Session(sidecar);
                var sg = g.GetScene(scene)!;
                var gText = sg.GetText(lifeActor, ScriptKind.Life);
                Check(sg.DetachedComments(lifeActor, ScriptKind.Life) >= 1, "e2e: the deleted statement's comments must be reported as detached");
                Check(gText.Contains(CommentWeaver.DetachedMarker.Trim()), "e2e: detached comments must be listed under a marker in the text");
                Check(gText.Contains("//" + " " + trailingWord), "e2e: the deleted statement's own comment text must still be in the script");
                Check(Regex.Matches(gText, @"// [ct]\d+").Count == Regex.Matches(realigned, @"// [ct]\d+").Count, "e2e: no comment may be lost when a statement is deleted");
            }

            // 6. sidecar file behaviour
            var text1 = File.ReadAllText(sidecar);
            var copy = Path.Combine(dir, "copy.json");
            File.WriteAllText(copy, text1);
            var reload = CommentStore.Load(copy);
            Check(reload.LoadError is null && reload.Count == 2, $"e2e: sidecar should hold 2 scripts, has {reload.Count} (error: {reload.LoadError})");
            reload.Set(scene, lifeActor, ScriptKind.Life, reload.Get(scene, lifeActor, ScriptKind.Life));
            reload.Save();
            CheckText(File.ReadAllText(copy), text1, "e2e: saving an unchanged store must reproduce the same file");

            var corrupt = Path.Combine(dir, "corrupt.json");
            File.WriteAllText(corrupt, "this is not json");
            var h = Session(corrupt);
            Check(h.CommentsLoadError is not null, "e2e: a corrupt sidecar must be reported");
            var sh = h.GetScene(scene)!;
            Check(sh.GetText(trackActor, ScriptKind.Track).Length > 0, "e2e: a corrupt sidecar must not stop scripts loading");
            sh.SetText(trackActor, ScriptKind.Track, sh.GetText(trackActor, ScriptKind.Track) + "// fresh\n");
            Check(h.SaveAll().Ok, "e2e: saving over a corrupt sidecar failed");
            Check(File.Exists(corrupt + ".bad") && File.ReadAllText(corrupt + ".bad") == "this is not json", "e2e: the unreadable sidecar must be kept as .bad, not destroyed");
            Check(CommentStore.Load(corrupt).LoadError is null, "e2e: the new sidecar must be valid");

            // 7. removing every comment removes the stored entry (and the file when it is the last)
            var only = Path.Combine(dir, "only.json");
            var p = Session(only);
            var sp = p.GetScene(scene)!;
            sp.SetText(trackActor, ScriptKind.Track, sp.GetText(trackActor, ScriptKind.Track) + "// only\n");
            Check(p.SaveAll().Ok && File.Exists(only), "e2e: single-comment save");
            var q = Session(only);
            var sq = q.GetScene(scene)!;
            Check(sq.GetText(trackActor, ScriptKind.Track).Contains("// only"), "e2e: single comment reloaded");
            sq.SetText(trackActor, ScriptKind.Track, sq.OriginalText(trackActor, ScriptKind.Track).Replace("// only\n", ""));
            Check(q.SaveAll().Ok && !File.Exists(only), "e2e: deleting the last comment removes the sidecar file");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
