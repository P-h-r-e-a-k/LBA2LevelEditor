using System.Windows;

namespace LBAAssembler;

// Two things every one of the app's secondary windows should do the same way, so there is one place that
// does them rather than each window repeating its own version:
//   * remember where it was last time and restore there (WindowPlacement), unless that spot is no longer on
//     any connected monitor, in which case it falls back to its ordinary centred placement;
//   * be closed automatically when the main window closes (WindowLifecycle.CloseAll), so nothing is left
//     running invisibly once the app's own window is gone.
// Call Register once, right after a window is constructed (before Show()); it does both.
internal static class WindowLifecycle
{
    private static readonly List<Window> Open = new();

    // Attaches remembered-position behaviour under `positionKey` and adds `window` to the list MainWindow's
    // own Closing handler walks. `positionKey` names the KIND of window ("ActorAttributesWindow", ...), not
    // the instance: see WindowPlacement's own comment for why several windows of one kind share a key.
    public static void Register(Window window, string positionKey)
    {
        WindowPlacement.Attach(window, positionKey);
        Open.Add(window);
        window.Closed += (_, _) => Open.Remove(window);
    }

    // Closes every registered window except `except` (MainWindow passes itself). Each window's own Closing
    // handler still runs exactly as it would from that window's own Close button or [X] -- an "unsaved
    // changes" prompt in a script editor, say, still asks first -- and if any one of them refuses (it is
    // still in the list afterwards, i.e. Closed never fired), this stops there and reports failure, leaving
    // whatever already closed, closed, so the caller can cancel ITS OWN close in turn rather than tear down
    // some windows and orphan the rest.
    public static bool CloseAll(Window? except = null)
    {
        foreach (var window in Open.Where(w => !ReferenceEquals(w, except)).ToList())
        {
            window.Close();
            if (Open.Contains(window)) return false;
        }
        return true;
    }
}

// Gives a window a remembered position (EditorSettings.WindowPositions), restored on show and saved on
// close, skipped in favour of the window's own ordinary startup placement when nothing is remembered yet or
// the remembered spot is no longer on any monitor currently connected (SystemInformation/Screen.AllScreens
// -- not just "somewhere on the combined virtual desktop", which would still say yes for a point that used
// to be on a second monitor now unplugged if it happens to fall inside the remaining monitor's rectangle by
// coincidence of the numbers, which a plain bounding-box check would miss).
internal static class WindowPlacement
{
    // How many WPF units a second, third, ... window of the same kind is offset from the first while more
    // than one is open at once, so opening an actor's attributes while another's is already up doesn't land
    // the new one exactly on top of it (both still start from the one remembered spot).
    private const double CascadeStep = 28;
    private const double MinVisible = 60;    // at least this many units of the window must land on a real monitor to count as "visible" (enough to reach the title bar)

    private static readonly Dictionary<string, int> OpenCount = new();

    public static void Attach(Window window, string key)
    {
        var ordinal = OpenCount.GetValueOrDefault(key);
        OpenCount[key] = ordinal + 1;
        window.Closed += (_, _) => OpenCount[key] = Math.Max(0, OpenCount.GetValueOrDefault(key) - 1);

        if (EditorSettings.Current.WindowPositions.TryGetValue(key, out var saved) && saved.Width > 0 && saved.Height > 0)
        {
            var left = saved.X + ordinal * CascadeStep;
            var top = saved.Y + ordinal * CascadeStep;
            // The cascade offset itself might have pushed an otherwise-fine remembered spot off screen (a
            // window remembered hard against a monitor's edge, with several more of its kind opened at
            // once); fall back to the un-offset spot rather than the window's own default placement, so it
            // still lands where the user last put windows of this kind instead of jumping back to centre.
            if (!IsVisible(left, top, saved.Width, saved.Height)) { left = saved.X; top = saved.Y; }
            if (IsVisible(left, top, saved.Width, saved.Height))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = left;
                window.Top = top;
                if (window.ResizeMode != ResizeMode.NoResize) { window.Width = saved.Width; window.Height = saved.Height; }
            }
        }

        window.Closed += (_, _) =>
        {
            // A minimized window's own Left/Top/Width/Height read back as its taskbar/tray position, not a
            // usable screen spot -- RestoreBounds is what WPF calls the last normal (non-minimized/maximized)
            // bounds regardless of the state it is actually in when closed, so that is what gets remembered.
            var bounds = window.RestoreBounds;
            if (bounds is { Width: > 0, Height: > 0 })
                EditorSettings.Current.WindowPositions[key] = new WindowBounds { X = bounds.Left, Y = bounds.Top, Width = bounds.Width, Height = bounds.Height };
            EditorSettings.Current.Save();
        };
    }

    // Whether a rectangle this size at this position has at least MinVisible units showing on some monitor
    // that is actually connected right now (not just within the combined virtual desktop's own bounding
    // box, which stays "big enough" even once a monitor that used to sit inside it is unplugged).
    private static bool IsVisible(double x, double y, double width, double height)
    {
        var rect = new System.Drawing.Rectangle((int)x, (int)y, (int)Math.Max(1, width), (int)Math.Max(1, height));
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var overlap = System.Drawing.Rectangle.Intersect(rect, screen.WorkingArea);
            if (overlap.Width >= MinVisible && overlap.Height >= MinVisible) return true;
        }
        return false;
    }
}
