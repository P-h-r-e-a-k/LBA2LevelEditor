using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LBA2LevelEditor;

// A small modal list with a filter box, for choosing one thing out of many (an entity, a scene slot ...). Built in code.
internal sealed class ListPickWindow : Window
{
    private readonly ListBox list = new();
    private readonly TextBox filter = new();
    private readonly IReadOnlyList<(int Id, string Label)> items;
    public int? Chosen { get; private set; }

    public ListPickWindow(string title, IReadOnlyList<(int Id, string Label)> items, int? selected = null, string? note = null)
    {
        this.items = items;
        Title = title;
        Width = 460; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFA));
        Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x24, 0x3E));
        ShowInTaskbar = false;

        var panel = new DockPanel { Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        if (note is not null)
        {
            var text = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0x6B, 0x8A)) };
            DockPanel.SetDock(text, Dock.Top);
            panel.Children.Add(text);
        }
        DockPanel.SetDock(filter, Dock.Top);
        filter.Margin = new Thickness(0, 0, 0, 8);
        filter.Padding = new Thickness(4, 3, 4, 3);
        panel.Children.Add(filter);

        list.FontFamily = new FontFamily("Consolas");
        list.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        list.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x24, 0x3E));
        panel.Children.Add(list);
        Content = panel;

        void Fill()
        {
            var text = filter.Text.Trim();
            list.Items.Clear();
            foreach (var item in items.Where(i => text.Length == 0 || i.Label.Contains(text, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(new Entry(item.Id, item.Label));
            if (selected is { } s && list.Items.OfType<Entry>().FirstOrDefault(e => e.Id == s) is { } current) { list.SelectedItem = current; list.ScrollIntoView(current); }
            else if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        filter.TextChanged += (_, _) => { selected = null; Fill(); };
        void Accept() { if (list.SelectedItem is Entry e) { Chosen = e.Id; DialogResult = true; } }
        ok.Click += (_, _) => Accept();
        list.MouseDoubleClick += (_, _) => Accept();
        filter.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list.SelectedIndex < list.Items.Count - 1) { list.SelectedIndex++; e.Handled = true; }
            if (e.Key == Key.Up && list.SelectedIndex > 0) { list.SelectedIndex--; e.Handled = true; }
        };
        Fill();
        Loaded += (_, _) => filter.Focus();
    }

    private sealed record Entry(int Id, string Label)
    {
        public override string ToString() => Label;
    }

    public static int? Pick(Window owner, string title, IReadOnlyList<(int Id, string Label)> items, int? selected = null, string? note = null)
    {
        var window = new ListPickWindow(title, items, selected, note) { Owner = owner };
        return window.ShowDialog() == true ? window.Chosen : null;
    }
}
