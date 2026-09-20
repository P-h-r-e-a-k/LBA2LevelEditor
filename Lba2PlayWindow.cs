using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LBA2LevelEditor;

// The scenes of an LBA2 game folder as (number, "number: description") for pickers. Scene N is SCENE.HQR entry N + 1;
// entry 0 holds the size of the largest scene.
internal static class Lba2SceneList
{
    public static List<(int Id, string Label)> Load(string directory)
    {
        var path = Path.Combine(directory, "SCENE.HQR");
        var result = new List<(int, string)>();
        if (!File.Exists(path)) return result;
        var count = HqrArchive.CountEntries(path);
        var names = HqdDescriptions.Load("SCENE2.HQD", count).Names;
        var archive = HqrArchive.Open(path);
        for (var entry = 1; entry < count; entry++)
        {
            if (!archive.IsValid(entry)) continue;
            var name = entry < names.Count ? names[entry] : null;
            result.Add((entry - 1, name is null ? $"{entry - 1}" : $"{entry - 1}: {name}"));
        }
        return result;
    }
}

// "LBA2: play scene": pick a scene and how to start the game (the full LBA2 engine, on the game folder the editor edits).
internal sealed class Lba2PlayWindow : Window
{
    private readonly TextBox filter = new();
    private readonly ListBox list = new();
    private readonly ComboBox resolution = new();
    private readonly CheckBox sound = new() { Content = "Sound", IsChecked = true };
    private readonly CheckBox keepFocus = new() { Content = "Keep running when the game window isn't in front" };
    private readonly TextBox commands = new() { AcceptsReturn = true, Height = 92, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly List<(int Id, string Label)> scenes;
    public Lba2PlayOptions? Options { get; private set; }

    public Lba2PlayWindow(List<(int Id, string Label)> scenes, int selected)
    {
        this.scenes = scenes;
        Title = "LBA2: play scene";
        Width = 720; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x19));
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xDA));
        var muted = new SolidColorBrush(Color.FromRgb(0x89, 0x95, 0x8B));

        var root = new Grid { Margin = new Thickness(14) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var left = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
        left.Children.Add(new TextBlock { Text = "SCENE", FontSize = 10, Foreground = muted, Margin = new Thickness(0, 0, 0, 4) });
        DockPanel.SetDock(left.Children[0], Dock.Top);
        DockPanel.SetDock(filter, Dock.Top);
        filter.Margin = new Thickness(0, 0, 0, 6); filter.Padding = new Thickness(4, 3, 4, 3);
        left.Children.Add(filter);
        list.FontFamily = new FontFamily("Consolas");
        list.Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x13, 0x11));
        list.Foreground = new SolidColorBrush(Color.FromRgb(0xC5, 0xC6, 0xB9));
        left.Children.Add(list);
        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        var right = new StackPanel();
        void Caption(string text) => right.Children.Add(new TextBlock { Text = text, FontSize = 10, Foreground = muted, Margin = new Thickness(0, 10, 0, 4) });
        Caption("WINDOW");
        foreach (var size in new[] { "640x480", "800x600", "1024x768", "1280x960", "1600x1200", "1280x720", "1920x1080" }) resolution.Items.Add(size);
        resolution.SelectedItem = "1280x960";
        right.Children.Add(resolution);
        Caption("OPTIONS");
        sound.Foreground = keepFocus.Foreground = Foreground;
        right.Children.Add(sound);
        keepFocus.Margin = new Thickness(0, 4, 0, 0);
        right.Children.Add(keepFocus);
        Caption("CONSOLE COMMANDS, run once the scene has loaded");
        commands.Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x13, 0x11));
        commands.Foreground = Foreground;
        commands.FontFamily = new FontFamily("Consolas");
        commands.ToolTip = "One per line or separated by ';'. The engine's own console: give <item>, vargame <n> <value>, behaviour <0..4>, teleport <x> <y> <z>, weapon <n> ...";
        right.Children.Add(commands);
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var everyItem = new Button { Content = "Every item", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Sets the inventory flags 0..40 (holomap, magic ball, sword, keys ...) with vargame" };
        everyItem.Click += (_, _) =>
        {
            // the inventory is game variables 0..40; money (8) is a count, not an item
            var lines = Enumerable.Range(0, 41).Where(i => i != 8).Select(i => $"vargame {i} 1");
            commands.Text = string.Join(";", lines) + (commands.Text.Trim().Length > 0 ? ";" + commands.Text.Trim() : "");
        };
        var clear = new Button { Content = "Clear", Padding = new Thickness(10, 3, 10, 3) };
        clear.Click += (_, _) => commands.Clear();
        presets.Children.Add(everyItem); presets.Children.Add(clear);
        right.Children.Add(presets);
        right.Children.Add(new TextBlock
        {
            Text = "The engine opens at its menu and jumps straight into the scene (the `cube` command). Things like `give`, `vargame 94 1`, `behaviour 2` or `teleport 24000 4096 6000` set Twinsen up. Nothing is saved: autosave is off, and its saves live in a folder of their own.",
            TextWrapping = TextWrapping.Wrap, Foreground = muted, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var play = new Button { Content = "Play", Padding = new Thickness(22, 5, 22, 5), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
        play.Click += (_, _) => Accept();
        buttons.Children.Add(play); buttons.Children.Add(cancel);
        right.Children.Add(buttons);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        Content = root;

        void Fill()
        {
            var text = filter.Text.Trim();
            list.Items.Clear();
            foreach (var s in scenes.Where(s => text.Length == 0 || s.Label.Contains(text, StringComparison.OrdinalIgnoreCase))) list.Items.Add(new Entry(s.Id, s.Label));
            if (list.Items.OfType<Entry>().FirstOrDefault(e => e.Id == selected) is { } current) { list.SelectedItem = current; list.ScrollIntoView(current); }
            else if (list.Items.Count > 0) list.SelectedIndex = 0;
            selected = -1;
        }
        filter.TextChanged += (_, _) => Fill();
        list.MouseDoubleClick += (_, _) => Accept();
        Fill();
        Loaded += (_, _) => filter.Focus();
    }

    private sealed record Entry(int Id, string Label)
    {
        public override string ToString() => Label;
    }

    private void Accept()
    {
        if (list.SelectedItem is not Entry scene) return;
        var size = ((string)resolution.SelectedItem).Split('x');
        Options = new Lba2PlayOptions
        {
            Scene = scene.Id,
            Width = int.Parse(size[0], CultureInfo.InvariantCulture), Height = int.Parse(size[1], CultureInfo.InvariantCulture),
            Sound = sound.IsChecked == true, KeepFocus = keepFocus.IsChecked == true, Commands = commands.Text,
        };
        DialogResult = true;
    }
}
