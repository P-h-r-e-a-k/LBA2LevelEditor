using Forms = System.Windows.Forms;

namespace LBAAssembler;

// EnableVisualStyles()/SetCompatibleTextRenderingDefault() must run exactly once per process, before
// any WinForms Control is ever created, or the second call throws InvalidOperationException. Shared
// by every WinForms-window launcher in this WPF app (BodyStudioLauncher, AnimationStudioLauncher) so
// opening more than one of them in the same session doesn't double-call this -- a flag kept
// per-launcher-class instead of here would miss that a DIFFERENT launcher already did it first.
internal static class WinFormsCompat
{
    private static bool initialized;

    public static void EnsureInitialized()
    {
        if (initialized) return;
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);
        initialized = true;
    }
}
