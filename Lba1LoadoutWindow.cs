using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LBA2LevelEditor.Lba1.Runtime;

namespace LBA2LevelEditor;

// What Twinsen has when a scene is played: a test scene is entered without the save game that would say. Items (the
// inventory flags), magic, money, keys, clover, life, the chapter, and any other game flag by number.
internal sealed class Lba1LoadoutWindow : Window
{
    private readonly Lba1Loadout loadout;
    private readonly List<CheckBox> items = new();
    private readonly Dictionary<string, TextBox> numbers = new();
    private readonly TextBox flags = new() { AcceptsReturn = false, Height = 24 };

    public Lba1LoadoutWindow(Lba1Loadout loadout, Func<int, string> itemName)
    {
        this.loadout = loadout;
        Title = "Twinsen's state";
        Width = 560; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var back = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFA));
        var fore = new SolidColorBrush(Color.FromRgb(0x10, 0x24, 0x3E));
        Background = back; Foreground = fore;

        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        Button Make(string text, Action click, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault, IsCancel = isCancel };
            b.Click += (_, _) => click();
            return b;
        }
        buttons.Children.Add(Make("New game", () => Fill(new Lba1Loadout())));
        buttons.Children.Add(Make("Everything", () => Fill(Lba1Loadout.Everything())));
        buttons.Children.Add(Make("OK", Accept, isDefault: true));
        buttons.Children.Add(Make("Cancel", () => DialogResult = false, isCancel: true));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Inventory", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0x6B, 0x8A)), Margin = new Thickness(0, 0, 0, 4) });
        var wrap = new WrapPanel();
        for (var i = 0; i < Lba1Const.MaxInventory; i++)
        {
            if (i == 26) continue;    // the list of clover places is a note, not an item
            var name = itemName(i);
            var box = new CheckBox { Content = name, Tag = i, Width = 168, Margin = new Thickness(0, 2, 8, 2), Foreground = fore, ToolTip = $"game flag {i}" };
            items.Add(box);
            wrap.Children.Add(box);
        }
        panel.Children.Add(wrap);

        panel.Children.Add(new TextBlock { Text = "State", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0x6B, 0x8A)), Margin = new Thickness(0, 12, 0, 4) });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string key, string label, string? hint = null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = fore };
            var box = new TextBox { Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(3, 2, 3, 2), ToolTip = hint };
            Grid.SetRow(text, grid.RowDefinitions.Count - 1); Grid.SetRow(box, grid.RowDefinitions.Count - 1); Grid.SetColumn(box, 1);
            grid.Children.Add(text); grid.Children.Add(box);
            numbers[key] = box;
        }
        Row("life", "Life (1-50)");
        Row("magicLevel", "Magic level (0-4)", "0 = no magic yet; each level adds 20 magic points");
        Row("magicPoint", "Magic points");
        Row("gold", "Kashes (0-999)");
        Row("keys", "Keys");
        Row("boxes", "Clover boxes (0-10)");
        Row("leaves", "Clover leaves");
        Row("chapter", "Chapter", "what the story scripts test with CHAPTER");
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock { Text = "Other game flags  (number=value, comma separated)", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0x6B, 0x8A)), Margin = new Thickness(0, 12, 0, 4) });
        flags.Padding = new Thickness(3, 2, 3, 2);
        flags.ToolTip = "Scripts read these with VAR_GAME; 70 is the 'instructions' flag that blocks item use.";
        panel.Children.Add(flags);
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Fill(loadout);
    }

    private void Fill(Lba1Loadout l)
    {
        foreach (var box in items) box.IsChecked = l.Items.Contains((int)box.Tag);
        numbers["life"].Text = l.Life.ToString(CultureInfo.InvariantCulture);
        numbers["magicLevel"].Text = l.MagicLevel.ToString(CultureInfo.InvariantCulture);
        numbers["magicPoint"].Text = l.MagicPoint.ToString(CultureInfo.InvariantCulture);
        numbers["gold"].Text = l.Gold.ToString(CultureInfo.InvariantCulture);
        numbers["keys"].Text = l.Keys.ToString(CultureInfo.InvariantCulture);
        numbers["boxes"].Text = l.CloverBoxes.ToString(CultureInfo.InvariantCulture);
        numbers["leaves"].Text = l.CloverLeaves.ToString(CultureInfo.InvariantCulture);
        numbers["chapter"].Text = l.Chapter.ToString(CultureInfo.InvariantCulture);
        flags.Text = string.Join(", ", l.Flags.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"));
    }

    private void Accept()
    {
        try
        {
            int N(string key) => int.Parse(numbers[key].Text.Trim(), CultureInfo.InvariantCulture);
            var extra = new Dictionary<int, int>();
            foreach (var part in flags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=');
                extra[int.Parse(kv[0], CultureInfo.InvariantCulture)] = int.Parse(kv[1], CultureInfo.InvariantCulture);
            }
            loadout.Items = items.Where(b => b.IsChecked == true).Select(b => (int)b.Tag).ToHashSet();
            loadout.Flags = extra;
            loadout.Life = N("life"); loadout.MagicLevel = N("magicLevel"); loadout.MagicPoint = N("magicPoint"); loadout.Gold = N("gold");
            loadout.Keys = N("keys"); loadout.CloverBoxes = N("boxes"); loadout.CloverLeaves = N("leaves"); loadout.Chapter = N("chapter");
            DialogResult = true;
        }
        catch (Exception error) when (error is FormatException or OverflowException or IndexOutOfRangeException)
        {
            MessageBox.Show(this, "Every number must be a whole number, and flags written as number=value.\n\n" + error.Message, "Twinsen's state", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
