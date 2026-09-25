using Forms = System.Windows.Forms;

namespace LBAAssembler;

// Launches Animation Studio (BodyStudio/AnimationStudioForm.cs) as its own top-level window, the
// exact same way BodyStudioLauncher.cs launches Body Studio's MainForm -- see that file's own
// comment for why a plain Show() (not Application.Run) works fine alongside WPF's own Dispatcher.
internal static class AnimationStudioLauncher
{
    private static Forms.Form? openForm;

    public static void Show(System.Windows.Window owner)
    {
        // Shared with BodyStudioLauncher's own flag, not a separate one: EnableVisualStyles() throws
        // if called again after any WinForms control already exists, which opening Body Studio first
        // in the same session would have already created -- a per-launcher-class flag would miss that.
        WinFormsCompat.EnsureInitialized();

        if (openForm is { IsDisposed: false })
        {
            openForm.WindowState = Forms.FormWindowState.Normal;
            openForm.Activate();
            return;
        }

        openForm = new LbaBodyStudio.AnimationStudioForm();
        openForm.FormClosed += (_, _) => openForm = null;
        openForm.Show(new Win32WindowHandle(owner));
    }

    // Unlike BodyStudioLauncher.Close(), AnimationStudioForm has no unsaved-changes prompt of its own yet
    // (Body Studio's was added separately) -- this closes it exactly as its own window-close button already
    // does today, with no new data-loss risk beyond that existing gap.
    public static void Close() => openForm?.Close();

    private sealed class Win32WindowHandle : System.Windows.Forms.IWin32Window
    {
        public nint Handle { get; }
        public Win32WindowHandle(System.Windows.Window window) => Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
    }
}
