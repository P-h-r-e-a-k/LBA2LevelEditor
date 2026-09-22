using Forms = System.Windows.Forms;

namespace LBAAssembler;

// Launches LBA Body Studio's MainForm (BodyStudio/MainForm.cs, absorbed
// from the standalone LbaBodyStudio project) as its own top-level window
// from inside this WPF app. WinForms and WPF each pump the same underlying
// Win32 message loop, so a Form shown with plain Show() (not
// Application.Run) works fine alongside the WPF Dispatcher already running
// this app -- it just needs EnableVisualStyles/SetCompatibleTextRenderingDefault
// called once before the first Form/Control is ever created, which a WPF
// app's own startup never does on its own.
internal static class BodyStudioLauncher
{
    private static bool initialized;
    private static Forms.Form? openForm;

    public static void Show(System.Windows.Window owner)
    {
        if (!initialized)
        {
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            initialized = true;
        }

        if (openForm is { IsDisposed: false })
        {
            openForm.WindowState = Forms.FormWindowState.Normal;
            openForm.Activate();
            return;
        }

        openForm = new LbaBodyStudio.MainForm();
        openForm.FormClosed += (_, _) => openForm = null;
        openForm.Show(new Win32WindowHandle(owner));
    }

    // IWin32Window wrapper so Body Studio's own window comes up owned by the
    // actor attributes window that launched it (Show(IWin32Window), not the
    // owner-less Show()), matching this codebase's existing per-actor
    // non-modal window pattern rather than leaving it floating unowned.
    private sealed class Win32WindowHandle : System.Windows.Forms.IWin32Window
    {
        public nint Handle { get; }
        public Win32WindowHandle(System.Windows.Window window) => Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
    }
}
