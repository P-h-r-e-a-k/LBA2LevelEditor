using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LBAAssembler;

// A real top-level window for playing an LBA2 scene from outside the main window (the scene editor's own
// Play button) -- LBA1's equivalent (Lba1PlayView.Lba1PlayHostWindow) has had one all along, since its
// play view is a plain UserControl anyone can re-host; LBA2 plays through a real child process instead
// (EmbeddedGameHost), and the scene editor used to launch it with no `embedded:` flag at all -- a bare
// native window with no WPF owner whatsoever, closable and positioned independently of everything else,
// the only play path in the app contained in nothing. This gives it the same owned-window treatment every
// other secondary window gets.
internal sealed class Lba2PlayHostWindow : Window
{
    // Its own dedicated port, distinct from MainWindow.Play.cs's Lba2BreakpointsPort (27015): this window is
    // a second, independent way to start an LBA2 Play session (the scene editor's own Play button vs. the
    // main window's Play tab), so in principle both could be live at once -- two engine processes trying to
    // bind the same --listen port would collide. This window doesn't use the socket for breakpoints (that
    // machinery is MainWindow.Play.cs-only), only to drive a live resize the same way.
    private const int ListenPort = 27016;

    private readonly EmbeddedGameHost host = new();
    private readonly TextBlock status;
    private Lba2ControlClient? control;

    public Lba2PlayHostWindow(string gameDirectory, Lba2PlayOptions options)
    {
        Title = "LBA2 - play scene";
        Width = 1180; Height = 780; MinWidth = 760; MinHeight = 480;
        SetResourceReference(Control.BackgroundProperty, "ThemeFieldBrush");
        status = new TextBlock { Margin = new Thickness(10, 6, 10, 6), FontFamily = new FontFamily("Consolas"), FontSize = 11 };
        status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextMutedBrush");
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        root.Children.Add(host);
        Content = root;
        host.GameExited += () => Dispatcher.Invoke(Close);
        host.WantsResize += (w, h) => _ = OnWantsResize(w, h);
        Closed += (_, _) => { control?.Dispose(); host.Stop(); };
        Loaded += async (_, _) => await StartAsync(gameDirectory, options);
    }

    private async Task StartAsync(string gameDirectory, Lba2PlayOptions options)
    {
        status.Text = "Starting the scene ...";
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        (options.Width, options.Height) = host.FitSize();
        options.ListenPort = ListenPort;
        string? problem = null;
        var process = await Task.Run(() => Lba2Play.Launch(gameDirectory, options, out problem, embedded: true));
        if (process is null) { status.Text = problem ?? "The game didn't start."; return; }
        if (!await host.AttachAsync(process)) { status.Text = "The game started but its window didn't appear here."; return; }
        status.Text = "Playing. It plays what is saved on disk. Click the game to give it the keyboard.";
        control = await Lba2ControlClient.ConnectAsync(ListenPort, CancellationToken.None);
        if (control is null) { DebugLog.Log("Lba2PlayHostWindow: control socket didn't come up; resizing this window won't resize the game."); return; }
        host.CheckSizeNow();   // catches drift between FitSize()'s sample and today's real area (Launch takes "a few seconds")
    }

    // See MainWindow.Play.cs's own OnGameWantsResize for the full explanation of this round trip; this is the
    // same thing without that file's multi-session bookkeeping, since this window only ever has one host.
    private async Task OnWantsResize(int w, int h)
    {
        if (control is not { } client) return;
        try
        {
            var response = await client.SendAsync($"resolution {w}x{h}");
            if (control != client) return;
            if (!response.Contains($"Resolution: {w}x{h}", StringComparison.Ordinal)) return;
            await client.SendAsync("key enter 90 3");
            if (control == client) host.ConfirmResize(w, h);
        }
        catch (IOException) { }
    }
}
