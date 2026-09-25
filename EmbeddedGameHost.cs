using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LBAAssembler;

// Shows the LBA2 engine (lba2cc.exe, a separate process with its own SDL window) inside the main window: the engine's window is
// taken over as a child of this control's own window (style stripped to a bare child, sized to fill the space FitSize() was
// called against), so playing a scene happens in the editor's view and not in a window of its own. Keyboard focus goes to the
// engine window like it would to a child control; the game keeps its own input handling.
//
// No more forced 4:3: the community engine's own renderer computes its camera projection/FOV from the actual --resolution it's
// launched with (SetProjection in EXTFUNC.CPP, not a hardcoded 320x240/4:3 assumption -- fixed and documented as such in the
// engine's own docs/WIDESCREEN.md), and its HUD/menus were re-anchored off fixed 640x480 pixel coordinates for the same reason.
// Forcing a 4:3 box here was this host's own artificial constraint, not something the engine needed -- it just produced large
// black bars whenever the available space wasn't 4:3, which is what FitSize() now avoids by requesting the actual available
// size (clamped to what the engine's own --resolution validation accepts) instead.
internal sealed class EmbeddedGameHost : HwndHost
{
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_POPUP = unchecked((int)0x80000000), WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000;
    private const int WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_BORDER = 0x00800000, WS_DLGFRAME = 0x00400000;
    private const int WS_EX_APPWINDOW = 0x00040000, WS_EX_WINDOWEDGE = 0x00000100, WS_EX_CLIENTEDGE = 0x00000200, WS_EX_DLGMODALFRAME = 0x00000001, WS_EX_TOOLWINDOW = 0x00000080;
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20, SWP_NOACTIVATE = 0x10;
    private const int SW_SHOW = 5, SW_HIDE = 0;
    private const int VK_LBUTTON = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WNDCLASS windowClass);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName;
    }

    // A window class that paints itself black (the game's 4:3 picture sits in the middle of the control with black around it).
    private static string HostClass()
    {
        const string name = "LbaEditorGameHost";
        if (!classRegistered)
        {
            var windowClass = new WNDCLASS
            {
                lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
                hInstance = GetModuleHandle(null), hbrBackground = GetStockObject(4 /* BLACK_BRUSH */), lpszClassName = name,
            };
            RegisterClassW(ref windowClass);
            classRegistered = true;
        }
        return name;
    }
    private static bool classRegistered;

    private IntPtr host;
    private IntPtr game;
    private Process? process;
    private readonly DispatcherTimer focusTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };

    public event Action? GameExited;

    public bool Running => process is { HasExited: false } && game != IntPtr.Zero;
    public int ProcessId => process?.Id ?? 0;

    // The size in device pixels the game should be launched at: this control's own actual size, clamped to
    // what the engine's own --resolution validation accepts (Res_ValidateDimensions: width 320-1920 and a
    // multiple of 8, height 200-1024). No aspect-ratio constraint of its own any more -- see this class's
    // own comment for why that's safe. Cached (launchWidth/Height) so Fit() can keep centering the game at
    // the size it actually believes it's rendering at, not recompute a different "best fit now" later.
    public (int Width, int Height) FitSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = Math.Clamp((int)(ActualWidth * dpi.DpiScaleX), 320, 1920);
        var h = Math.Clamp((int)(ActualHeight * dpi.DpiScaleY), 200, 1024);
        w -= w % 8;
        launchWidth = w; launchHeight = h;
        return (w, h);
    }

    private int launchWidth, launchHeight;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        host = CreateWindowEx(0, HostClass(), "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS , 0, 0, 100, 100, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, host);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Stop();
        DestroyWindow(hwnd.Handle);
    }

    // Takes over the window of an engine process that was just started: waits for it to appear, then embeds it. False when it never did.
    // The wait is a tight loop on its own thread and the window is hidden and re-parented the moment it exists, so it is never seen
    // as a window of its own.
    public async Task<bool> AttachAsync(Process engine, int timeoutMilliseconds = 30000)
    {
        process = engine;
        engine.EnableRaisingEvents = true;
        engine.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(process, engine)) { Release(); GameExited?.Invoke(); } }));
        var parent = host;
        var found = await Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMilliseconds && !engine.HasExited)
            {
                var window = FindEngineWindow(engine.Id);
                if (window != IntPtr.Zero)
                {
                    ShowWindow(window, SW_HIDE);
                    var style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
                    style &= ~(long)(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_BORDER | WS_DLGFRAME);
                    style |= WS_CHILD | WS_CLIPSIBLINGS;
                    SetWindowLongPtr(window, GWL_STYLE, new IntPtr(style));
                    var ex = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64();
                    ex &= ~(long)(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_TOOLWINDOW);
                    SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr(ex));
                    SetParent(window, parent);
                    return window;
                }
                Thread.Sleep(2);
            }
            return IntPtr.Zero;
        });
        if (found == IntPtr.Zero || engine.HasExited) return false;

        game = found;
        SetWindowPos(game, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);
        Fit();
        ShowWindow(game, SW_SHOW);
        SetFocus(game);
        focusTimer.Tick -= FocusTick;
        focusTimer.Tick += FocusTick;
        focusTimer.Start();
        return true;
    }

    private static IntPtr FindEngineWindow(int processId)
    {
        IntPtr best = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != processId || GetParent(hwnd) != IntPtr.Zero) return true;      // (it may still be hidden: it is shown once embedded)
            var name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            // the engine's window is an SDL one; any other visible top-level window of the process (a console, an error box) is not it
            if (name.ToString().StartsWith("SDL", StringComparison.OrdinalIgnoreCase)) { best = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // Centres the game at the resolution it was actually launched with (FitSize's own cached result), not a
    // freshly recomputed "best fit now": the engine's own internal rendering resolution is fixed for the
    // life of the process (--resolution is a launch-time argument only, there is no live equivalent this
    // host can safely drive -- the engine's own console `resolution` command exists but is documented as
    // leaving the scene rendered into the corner unless something also re-runs Init3DView, which nothing
    // reachable from here does). Recomputing a different size and just Win32-resizing the child window
    // would only move the black bars from this host's own paint into SDL's own internal logical-
    // presentation letterboxing instead, with no actual FOV gain -- the two would disagree about what
    // "the current size" even is. If the control is later resized smaller than the launch size, SDL scales
    // its own framebuffer down to fit (the same SDL_LOGICAL_PRESENTATION_LETTERBOX mechanism that already
    // exists for exactly this); if resized larger, this simply centres the game in the extra space, the
    // same as before FitSize() stopped forcing 4:3, just against the real launch size instead of a fresh
    // 4:3 guess.
    private void Fit()
    {
        if (game == IntPtr.Zero || host == IntPtr.Zero || !GetClientRect(host, out var rect)) return;
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        if (w < 16 || h < 16) return;
        var fitW = launchWidth > 0 ? Math.Min(w, launchWidth) : w;
        var fitH = launchHeight > 0 ? Math.Min(h, launchHeight) : h;
        MoveWindow(game, (w - fitW) / 2, (h - fitH) / 2, fitW, fitH, true);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        Fit();
    }

    // Clicking on the game gives it the keyboard (a click on a foreign child window doesn't move the focus by itself).
    private void FocusTick(object? sender, EventArgs e)
    {
        if (game == IntPtr.Zero || GetFocus() == game || (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) return;
        if (GetCursorPos(out var p) && GetWindowRect(game, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) SetFocus(game);
    }

    public void FocusGame()
    {
        if (game != IntPtr.Zero) SetFocus(game);
    }

    private void Release()
    {
        focusTimer.Stop();
        game = IntPtr.Zero;
    }

    // Ends the game (a running one is closed the hard way: it is a test session, nothing of it is kept).
    public void Stop()
    {
        var engine = process;
        process = null;
        Release();
        if (engine is null) return;
        try
        {
            if (!engine.HasExited)
            {
                engine.Kill(entireProcessTree: true);
                engine.WaitForExit(2000);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { DebugLog.Log($"EmbeddedGameHost: stopping the engine: {error.Message}"); }
        finally { engine.Dispose(); }
    }
}
