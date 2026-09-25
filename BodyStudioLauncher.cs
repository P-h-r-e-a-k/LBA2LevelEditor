namespace LBAAssembler;

// Launches LBA Body Studio's window (BodyStudio/BodyStudioWindow.cs, absorbed from the standalone
// LbaBodyStudio project) as its own top-level WPF window. Was a WinForms Form until the Interface
// Audit's WPF-port item; see BodyStudioWindow.cs's own comment. One instance at a time (re-activates the
// existing one rather than opening a second) -- unchanged from before the port, not something the audit
// asked to change -- but now registered with WindowLifecycle like every other secondary window, closing
// the "different lifecycle... than every other window's per-instance position memory" half of that
// finding too, not just the WinForms-vs-WPF half.
internal static class BodyStudioLauncher
{
    private static LbaBodyStudio.BodyStudioWindow? openWindow;

    public static void Show(System.Windows.Window owner)
    {
        if (openWindow is not null)
        {
            if (openWindow.WindowState == System.Windows.WindowState.Minimized) openWindow.WindowState = System.Windows.WindowState.Normal;
            openWindow.Activate();
            return;
        }

        openWindow = new LbaBodyStudio.BodyStudioWindow { Owner = owner };
        openWindow.Closed += (_, _) => openWindow = null;
        WindowLifecycle.Register(openWindow, "BodyStudioWindow");
        openWindow.Show();
    }

    // BodyStudioWindow.Closing already asks about unsaved changes; if the user cancels, the window (and
    // openWindow) simply stays as it is, same as clicking its own close button would.
    public static void Close() => openWindow?.Close();
}
