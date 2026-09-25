using System.IO;
using System.Windows;

namespace LBAAssembler;

// "Test edits": every editor in this app (zones, actors, scripts, the grid/brick/sprite/object libraries,
// LBA1's content-mod tools) writes straight to the real game folder's HQR files the moment you click
// Save/Apply -- there was no way to try a change and see it actually play before it permanently lands
// there. This redirects EditorSettings.GameDirectory/Lba1Directory (which is what every one of those
// editors ultimately reads -- some through MainWindow's own `gameRoot`, most read the settings directly,
// see EditorSettings.TestModeActive's own comment) at a scratch, hard-linked mirror of the real folder
// (LiveDataRoot.CreateFullMirror) for the rest of the session, so every "Save" during that time writes to
// the mirror instead. Nothing needed to change in any of those editors themselves: they already all follow
// whichever folder Settings says to use, because that's the same mechanism a user switching to a different
// real game folder already relies on.
public partial class MainWindow
{
    private LiveDataRoot? testLba2Root;
    private LiveDataRoot? testLba1Root;
    private string? realGameDirectory;
    private string? realLba1Directory;

    private bool TestEditsActive => testLba2Root is not null || testLba1Root is not null;

    // Every one of these (unlike actor/script windows, which SwitchGameCore's own CloseActorWindows already
    // closes) captures its game directory once, in its constructor or on first open, and keeps using that
    // same string for as long as it stays open -- so one left open across a test-edits switch would keep
    // writing to whichever folder (real or scratch) was current when it was opened, silently ignoring the
    // switch. Closing them (each one's own Closing handler still asks about unsaved changes first) forces a
    // reopen, which picks up the directory that is current by then. Body Studio / Animation Studio are
    // WinForms, so they never show up in OwnedWindows at all -- closed through their own launchers instead.
    private void CloseDirectoryScopedEditors()
    {
        foreach (var window in OwnedWindows.OfType<Window>()
                     .Where(w => w is Lba2SceneEditorWindow or Lba1SceneEditorWindow or GridEditorWindow or ObjectBrowserWindow or AssetEditorWindow).ToList())
            window.Close();
        BodyStudioLauncher.Close();
        AnimationStudioLauncher.Close();
    }

    private void EnterTestEdits()
    {
        if (TestEditsActive) return;
        if (!Lba2Configured && !Lba1Configured) { SetStatus("No game folder is set to test edits against.", StatusKind.Warning); return; }
        if (!ConfirmTerrainDiscard()) return;      // same check SwitchGame itself makes; done here, once, instead of letting SwitchGameCore's own caller repeat it below

        StopLive();
        EndBodyPreviewLive();
        CloseDirectoryScopedEditors();

        realGameDirectory = EditorSettings.Current.GameDirectory;
        realLba1Directory = EditorSettings.Current.Lba1Directory;

        if (Lba2Configured)
        {
            LiveDataRoot.CleanStale(realGameDirectory, LiveDataRoot.TestEditsFolderName);
            testLba2Root = LiveDataRoot.Create(realGameDirectory, null, LiveDataRoot.TestEditsFolderName);
            if (testLba2Root is null) { SetStatus("Couldn't create a test copy of the LBA2 game folder.", StatusKind.Warning); return; }
        }
        if (Lba1Configured)
        {
            LiveDataRoot.CleanStale(realLba1Directory, LiveDataRoot.TestEditsFolderName);
            testLba1Root = LiveDataRoot.Create(realLba1Directory, null, LiveDataRoot.TestEditsFolderName);
            if (testLba1Root is null)
            {
                SetStatus("Couldn't create a test copy of the LBA1 game folder.", StatusKind.Warning);
                testLba2Root?.Dispose(); testLba2Root = null;
                return;
            }
        }

        // Set before anything below can possibly call EditorSettings.Save() -- see its own comment.
        EditorSettings.TestModeActive = true;
        if (testLba2Root is not null) EditorSettings.Current.GameDirectory = testLba2Root.Directory;
        if (testLba1Root is not null) EditorSettings.Current.Lba1Directory = testLba1Root.Directory;

        gameRoot = EditorSettings.Current.GameDirectory;
        nativeRenderer.SetGameDirectory(gameRoot);
        lba1Game = null; lba1Images = null;
        selectedLba2Scene = null;
        SwitchGameCore(currentGame);
        ApplyMode();
        SetStatus("Testing edits: nothing you save now touches your real game files until you commit them.", StatusKind.Info);
    }

    private void ExitTestEdits(bool commit)
    {
        if (!TestEditsActive) return;
        if (!ConfirmTerrainDiscard()) return;
        CloseDirectoryScopedEditors();

        if (commit)
        {
            var copied = 0;
            try
            {
                if (testLba2Root is not null) foreach (var name in testLba2Root.ChangedFiles()) { File.Copy(Path.Combine(testLba2Root.Directory, name), Path.Combine(realGameDirectory!, name), overwrite: true); copied++; }
                if (testLba1Root is not null) foreach (var name in testLba1Root.ChangedFiles()) { File.Copy(Path.Combine(testLba1Root.Directory, name), Path.Combine(realLba1Directory!, name), overwrite: true); copied++; }
                SetStatus(copied == 0 ? "No changed files to commit." : $"Committed {copied} changed file(s) to your real game folder.", StatusKind.Success);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                SetStatus($"Couldn't commit test edits: {error.Message}. Your real game files may be partly updated -- check before testing again.", StatusKind.Warning);
            }
        }
        else SetStatus("Test edits discarded -- your real game files were never touched.", StatusKind.Info);

        EditorSettings.TestModeActive = false;
        EditorSettings.Current.GameDirectory = realGameDirectory!;
        EditorSettings.Current.Lba1Directory = realLba1Directory!;
        testLba2Root?.Dispose(); testLba2Root = null;
        testLba1Root?.Dispose(); testLba1Root = null;
        realGameDirectory = null; realLba1Directory = null;

        gameRoot = EditorSettings.Current.GameDirectory;
        nativeRenderer.SetGameDirectory(gameRoot);
        lba1Game = null; lba1Images = null;
        selectedLba2Scene = null;
        SwitchGameCore(currentGame);
        ApplyMode();
    }

    private void TestEditsStart_Click(object sender, RoutedEventArgs e) => EnterTestEdits();
    private void TestEditsCommit_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Copy every file your test edits changed over your real game files? This cannot be undone by leaving test mode -- use the normal Undo/Redo afterward if needed.",
                "Commit test edits", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            ExitTestEdits(commit: true);
    }
    private void TestEditsDiscard_Click(object sender, RoutedEventArgs e) => ExitTestEdits(commit: false);
}
