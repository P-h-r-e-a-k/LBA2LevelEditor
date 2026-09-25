using LBAAssembler;

namespace ScriptRoundTrip;

// Exercises the "test edits" safety feature (MainWindow.TestEdits.cs) at the file-system level, without
// the WPF UI: all the actual safety logic (hard-link mirror, detach-on-save, ChangedFiles() diff, copy-back
// on commit) lives in LiveDataRoot itself and plain File calls, not in MainWindow -- so exercising it here
// proves the mechanism whether MainWindow drives it or this does. Uses a sandbox copy of the real game
// folder, never the real one (see the feedback_ui_test_game_folder memory: a past UI test once wrote into
// the real install because its sandbox settings silently failed to load).
//
// The outer sandbox below is a PLAIN COPY of `real`, not a hard link, even though LiveDataRoot's own inner
// mirror uses hard links freely -- on 2026-09-25, an earlier version of this file hard-linked the outer
// sandbox too (to skip the copy cost) and that corrupted the real GOG install's SCENE.HQR: step 5's commit
// simulation does `File.Copy(..., overwrite: true)` onto the sandbox's own file to mimic what
// ExitTestEdits(commit: true) does onto a user's real game folder, and Windows' File.Copy overwrites an
// existing destination's data in place rather than detaching it first -- safe for ExitTestEdits itself
// (a user's real game-folder file is never itself a hard link to begin with, so there's nothing for that
// overwrite to leak through to), but not safe here, where the sandbox file WAS still hard-linked to the
// real one at that point. A plain copy for the outer sandbox removes the shared data entirely, so the same
// mistake can't happen again even if this file's commit step is later changed to look more like the real
// one. (The corruption was fully recovered -- it only ever touched 64 bytes this test itself had just
// written, XORed with a known, self-inverse value -- but the fix belongs here, not just in the incident.)
internal static class TestEditsSessionTest
{
    public static int Run(string[] args)
    {
        var real = args.Length > 1 ? args[1] : @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
        var sandbox = args.Length > 2 ? args[2] : @"E:\dump\_lba2isl_testedits";
        if (!File.Exists(Path.Combine(real, "SCENE.HQR"))) { Console.WriteLine($"no real game folder at {real}"); return 2; }

        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
        Directory.CreateDirectory(sandbox);
        foreach (var source in Directory.EnumerateFiles(real))
            File.Copy(source, Path.Combine(sandbox, Path.GetFileName(source)));

        var failures = 0;
        void Check(string what, bool ok, string detail = "") { Console.WriteLine($"  {(ok ? "ok    " : "FAILED")} {what} {detail}"); if (!ok) failures++; }

        const string targetFile = "SCENE.HQR";
        var sandboxTargetPath = Path.Combine(sandbox, targetFile);
        var realBefore = File.ReadAllBytes(sandboxTargetPath);

        Console.WriteLine("test-edits session (file-system level)");

        // 1. Enter test mode: create the shadow mirror, exactly as MainWindow.TestEdits.EnterTestEdits does.
        LiveDataRoot.CleanStale(sandbox, LiveDataRoot.TestEditsFolderName);
        var shadow = LiveDataRoot.Create(sandbox, null, LiveDataRoot.TestEditsFolderName);
        if (shadow is null) { Console.WriteLine("couldn't create the shadow mirror"); return 2; }
        Check("the shadow mirror starts with every real file present", File.Exists(Path.Combine(shadow.Directory, targetFile)));
        Check("ChangedFiles() is empty before any edit", shadow.ChangedFiles().Count == 0, $"({shadow.ChangedFiles().Count} files)");

        // 2. "Save" an edit the way FileTransaction/HqrEntryStore actually write a file: a new temp file, renamed over the target.
        var edited = (byte[])realBefore.Clone();
        for (var i = 0; i < Math.Min(64, edited.Length); i++) edited[i] ^= 0xFF;
        var shadowTargetPath = Path.Combine(shadow.Directory, targetFile);
        var tempPath = shadowTargetPath + ".tmp";
        File.WriteAllBytes(tempPath, edited);
        File.Move(tempPath, shadowTargetPath, overwrite: true);

        // 3. The real sandbox file must be completely untouched.
        Check("the real (sandbox) file is untouched by the shadow edit", File.ReadAllBytes(sandboxTargetPath).AsSpan().SequenceEqual(realBefore));
        Check("the shadow file has the edit", File.ReadAllBytes(shadowTargetPath).AsSpan().SequenceEqual(edited));

        // 4. ChangedFiles() must report exactly the one touched file.
        var changed = shadow.ChangedFiles();
        Check("ChangedFiles() reports exactly the edited file", changed.Count == 1 && changed[0].Equals(targetFile, StringComparison.OrdinalIgnoreCase), $"({string.Join(", ", changed)})");

        // 5. Commit: copy every changed file back over the real ones, exactly as MainWindow.TestEdits.ExitTestEdits(commit: true) does.
        foreach (var name in shadow.ChangedFiles()) File.Copy(Path.Combine(shadow.Directory, name), Path.Combine(sandbox, name), overwrite: true);
        Check("committing copies the edit onto the real (sandbox) file", File.ReadAllBytes(sandboxTargetPath).AsSpan().SequenceEqual(edited));

        // 6. A second, un-edited file must never have been touched at all -- still hard-linked, same content.
        var otherFile = Directory.EnumerateFiles(real).Select(Path.GetFileName)
            .First(n => n != targetFile && !n!.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) && !n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))!;
        Check("an untouched file was never copied (commit only touches ChangedFiles())",
            File.ReadAllBytes(Path.Combine(sandbox, otherFile)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(real, otherFile))));

        shadow.Dispose();
        Check("disposing after commit removes the shadow folder", !Directory.Exists(Path.Combine(sandbox, LiveDataRoot.TestEditsFolderName)));

        // 7. Discard path: a second shadow session, edited, then simply disposed without copying anything back.
        using (var discardShadow = LiveDataRoot.Create(sandbox, null, LiveDataRoot.TestEditsFolderName))
        {
            if (discardShadow is null) { Console.WriteLine("couldn't create the second shadow mirror"); failures++; }
            else
            {
                var beforeDiscard = File.ReadAllBytes(sandboxTargetPath);
                var discardTemp = Path.Combine(discardShadow.Directory, targetFile) + ".tmp";
                File.WriteAllBytes(discardTemp, realBefore);        // some other edit, never committed
                File.Move(discardTemp, Path.Combine(discardShadow.Directory, targetFile), overwrite: true);
                Check("a discarded shadow edit never reaches the real file while the session is still open", File.ReadAllBytes(sandboxTargetPath).AsSpan().SequenceEqual(beforeDiscard));
            }
        }
        Check("disposing the shadow (discard) removes its folder", !Directory.Exists(Path.Combine(sandbox, LiveDataRoot.TestEditsFolderName)));
        Check("discarding never touched the real file", File.ReadAllBytes(sandboxTargetPath).AsSpan().SequenceEqual(edited));

        Directory.Delete(sandbox, true);
        Console.WriteLine(failures == 0 ? "test-edits session tests: all passed" : $"test-edits session tests: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
