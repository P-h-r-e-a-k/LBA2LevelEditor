using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LBA2LevelEditor;

// Two tiers of "this will take a moment" feedback, for wrapping the operations in this app that are slow
// enough to be worth saying so (nearly everything else already feels instant, so most calls need neither):
// a short one just changes the mouse cursor; a longer one (or one whose duration isn't predictable up front)
// also shows a small "<label>…" indeterminate progress strip, wherever the caller has one to show. Both return
// an IDisposable so a `using` block covers exactly the operation's own duration and can't be left showing by
// an early return or an exception partway through.
//
// One shared, static implementation (not a copy per window) since the app is otherwise all on the UI thread:
// loading a scene, rendering a joined map, saving an actor edit, and everything else this wraps runs
// synchronously, so the only way the cursor/progress strip actually get a chance to paint before the blocking
// call starts is to force one dispatcher frame through first -- Dispatcher.Invoke at Render priority does
// nothing itself, but doesn't return until WPF's own next render pass (which now includes the cursor and the
// progress strip) has actually happened. Scopes nest correctly (an inner one changes nothing when an outer one
// is already active) so a wrapped call is free to call another wrapped call.
internal static class UiBusy
{
    private static int depth;
    private static Cursor? previousCursor;

    // A short operation: just the wait cursor.
    public static IDisposable Cursor() => new Scope(null);

    // A longer, or unpredictable-duration, operation: the wait cursor plus `text…` in the given progress strip
    // (a FrameworkElement to show/hide and the TextBlock inside it to set the label on -- MainWindow's own
    // BusyPanel/BusyLabel, say). Pass null for panel/label to fall back to the cursor alone (e.g. a window with
    // nowhere to put a progress strip).
    public static IDisposable Progress(FrameworkElement? panel, TextBlock? label, string text) => new Scope(panel is null || label is null ? null : (panel, label, text));

    private sealed class Scope : IDisposable
    {
        private readonly (FrameworkElement Panel, TextBlock Label, string Text)? progress;

        public Scope((FrameworkElement Panel, TextBlock Label, string Text)? progress)
        {
            this.progress = progress;
            if (depth == 0) previousCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;
            depth++;
            if (progress is { } p)
            {
                p.Label.Text = p.Text;
                p.Panel.Visibility = Visibility.Visible;
            }
            // Flush: paints the cursor (and the progress strip, if any) before the caller's own blocking work runs.
            (progress?.Panel.Dispatcher ?? Application.Current?.Dispatcher)?.Invoke(() => { }, DispatcherPriority.Render);
        }

        public void Dispose()
        {
            depth = Math.Max(0, depth - 1);
            if (progress is { } p) p.Panel.Visibility = Visibility.Collapsed;
            if (depth == 0) Mouse.OverrideCursor = previousCursor;
        }
    }
}
