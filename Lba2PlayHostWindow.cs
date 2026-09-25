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
    private readonly EmbeddedGameHost host = new();
    private readonly TextBlock status;

    public Lba2PlayHostWindow(string gameDirectory, Lba2PlayOptions options)
    {
        Title = "LBA2 - play scene";
        Width = 1180; Height = 780; MinWidth = 760; MinHeight = 480;
        Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        status = new TextBlock { Margin = new Thickness(10, 6, 10, 6), FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0x6B, 0x8A)) };
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        root.Children.Add(host);
        Content = root;
        host.GameExited += () => Dispatcher.Invoke(Close);
        Closed += (_, _) => host.Stop();
        Loaded += async (_, _) => await StartAsync(gameDirectory, options);
    }

    private async Task StartAsync(string gameDirectory, Lba2PlayOptions options)
    {
        status.Text = "Starting the scene ...";
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        (options.Width, options.Height) = host.FitSize();
        string? problem = null;
        var process = await Task.Run(() => Lba2Play.Launch(gameDirectory, options, out problem, embedded: true));
        if (process is null) { status.Text = problem ?? "The game didn't start."; return; }
        if (!await host.AttachAsync(process)) { status.Text = "The game started but its window didn't appear here."; return; }
        status.Text = "Playing. It plays what is saved on disk. Click the game to give it the keyboard.";
    }
}
