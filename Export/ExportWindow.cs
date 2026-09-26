using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LBAAssembler.Export;

// Tools > Export 3D models: pick what to export (islands with their objects and race track, buildings and props, interiors, every actor and
// body, furniture and items), a format (glTF / OBJ / PLY / STL) and a folder. The files are for other 3D tools, not for the game.
internal sealed class ExportWindow : Window
{
    private readonly ExportCatalog catalog;
    private readonly ComboBox categoryBox = new() { MinWidth = 330 };
    private readonly ComboBox formatBox = new() { MinWidth = 130 };
    private readonly TextBox filterBox = new() { Padding = new Thickness(3), ToolTip = "Filter the list" };
    private readonly ListBox list = new() { SelectionMode = SelectionMode.Extended, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    private readonly TextBox folderBox = new() { Padding = new Thickness(3) };
    private readonly TextBox scaleBox = new() { Text = "0.001", Width = 70, Padding = new Thickness(3), ToolTip = "File units per game unit. 0.001 makes 1000 game units (about Twinsen's height) one unit, i.e. roughly a metre" };
    private readonly CheckBox terrainBox = new() { Content = "Islands: ground", IsChecked = true };
    private readonly CheckBox objectsBox = new() { Content = "Islands: objects on it", IsChecked = true };
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly TextBlock countText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock formatText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button exportButton = new() { Content = "Export", Padding = new Thickness(18, 5, 18, 5), IsDefault = true };
    private readonly Button cancelButton = new() { Content = "Stop", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false };
    private readonly ProgressBar progressBar = new() { Height = 8, Margin = new Thickness(0, 8, 0, 6) };
    private readonly TextBox log = new() { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 11, Height = 150 };

    private List<ExportItem> items = new();
    private CancellationTokenSource? running;
    private readonly string? preselectFile;

    public ExportWindow(ExportCatalog catalog, string? categoryHint = null, string? preselectFile = null)
    {
        this.catalog = catalog;
        this.preselectFile = preselectFile;
        Title = "Export 3D models";
        Width = 940; Height = 760;
        MinWidth = 720; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ThemeWindowBrush");
        SetResourceReference(ForegroundProperty, "ThemeTextBrush");
        BuildLayout();
        foreach (var c in catalog.Categories) categoryBox.Items.Add(c.Title);
        foreach (var f in new[] { ExportFormat.Glb, ExportFormat.Obj, ExportFormat.Ply, ExportFormat.Stl }) formatBox.Items.Add(new ComboBoxItem { Content = SceneWriters.Extension(f).TrimStart('.').ToUpperInvariant(), Tag = f });
        formatBox.SelectedIndex = 0;
        formatBox.SelectionChanged += (_, _) => formatText.Text = SceneWriters.Describe(SelectedFormat);
        formatText.Text = SceneWriters.Describe(SelectedFormat);
        folderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LBA Assembler 3D export");
        categoryBox.SelectionChanged += (_, _) => LoadCategory();
        filterBox.TextChanged += (_, _) => FillList();
        list.SelectionChanged += (_, _) => UpdateCount();
        Loaded += (_, _) =>
        {
            if (catalog.Categories.Count == 0) { log.Text = "No LBA1 or LBA2 game folder is set (File > Settings)."; return; }
            var start = categoryHint is null ? -1 : catalog.Categories.ToList().FindIndex(c => c.Title.Contains(categoryHint, StringComparison.OrdinalIgnoreCase));
            categoryBox.SelectedIndex = Math.Max(0, start);
        };
        Closing += (_, e) => { if (running is not null) { running.Cancel(); } };
    }

    private ExportFormat SelectedFormat => formatBox.SelectedItem is ComboBoxItem { Tag: ExportFormat f } ? f : ExportFormat.Glb;

    private void BuildLayout()
    {
        foreach (var t in new TextBlock[] { description, countText, formatText }) t.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextBrush");
        foreach (var box in new TextBox[] { filterBox, folderBox, scaleBox, log })
        {
            box.SetResourceReference(BackgroundProperty, "ThemeFieldBrush");
            box.SetResourceReference(ForegroundProperty, "ThemeTextBrush");
            box.SetResourceReference(BorderBrushProperty, "ThemeBorderBrush");
        }
        list.SetResourceReference(BackgroundProperty, "ThemeFieldBrush");
        list.SetResourceReference(ForegroundProperty, "ThemeTextBrush");
        foreach (var check in new CheckBox[] { terrainBox, objectsBox }) check.SetResourceReference(ForegroundProperty, "ThemeTextBrush");

        Label Caption(string text) { var l = new Label { Content = text, Padding = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; l.SetResourceReference(ForegroundProperty, "ThemeTextMutedBrush"); return l; }

        var root = new DockPanel { Margin = new Thickness(14) };

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(top, Dock.Top);
        var row1 = new StackPanel { Orientation = Orientation.Horizontal };
        row1.Children.Add(Caption("What")); row1.Children.Add(categoryBox);
        row1.Children.Add(new Label { Width = 14 });
        row1.Children.Add(Caption("Format")); row1.Children.Add(formatBox);
        row1.Children.Add(new Label { Width = 14 });
        row1.Children.Add(Caption("Scale")); row1.Children.Add(scaleBox);
        top.Children.Add(row1);
        top.Children.Add(description);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row2.Children.Add(terrainBox); row2.Children.Add(new Label { Width = 16 }); row2.Children.Add(objectsBox);
        top.Children.Add(row2);
        root.Children.Add(top);

        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        var folderRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) => BrowseFolder();
        DockPanel.SetDock(browse, Dock.Right);
        var folderCaption = Caption("Save into");
        DockPanel.SetDock(folderCaption, Dock.Left);
        folderRow.Children.Add(folderCaption); folderRow.Children.Add(browse); folderRow.Children.Add(folderBox);
        bottom.Children.Add(folderRow);
        bottom.Children.Add(formatText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        exportButton.Click += (_, _) => StartExport();
        cancelButton.Click += (_, _) => running?.Cancel();
        buttons.Children.Add(exportButton); buttons.Children.Add(new Label { Width = 8 }); buttons.Children.Add(cancelButton); buttons.Children.Add(countText);
        bottom.Children.Add(buttons);
        bottom.Children.Add(progressBar);
        bottom.Children.Add(log);
        root.Children.Add(bottom);

        var selectRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(selectRow, Dock.Top);
        selectRow.Children.Add(Caption("Filter")); filterBox.Width = 240; selectRow.Children.Add(filterBox);
        foreach (var (text, action) in new (string, Action)[] { ("Select all", () => list.SelectAll()), ("Select none", () => list.UnselectAll()) })
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
            var a = action; b.Click += (_, _) => { a(); list.Focus(); };
            selectRow.Children.Add(b);
        }
        root.Children.Add(selectRow);
        root.Children.Add(list);
        Content = root;
    }

    private void LoadCategory()
    {
        if (categoryBox.SelectedIndex < 0) return;
        var category = catalog.Categories[categoryBox.SelectedIndex];
        description.Text = category.Description;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            items = category.Load();
            terrainBox.IsEnabled = objectsBox.IsEnabled = category.Title.Contains("islands", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            items = new();
            log.Text = $"Couldn't read {category.Title}: {error.Message}";
        }
        finally { Mouse.OverrideCursor = null; }
        filterBox.Clear();
        FillList();
        if (preselectFile is not null && items.FirstOrDefault(i => i.FileName.Equals(preselectFile, StringComparison.OrdinalIgnoreCase)) is { } wanted)
        {
            list.SelectedItem = list.Items.OfType<ItemRow>().FirstOrDefault(r => r.Item == wanted);
            if (list.SelectedItem is not null) list.ScrollIntoView(list.SelectedItem);
        }
    }

    private sealed record ItemRow(ExportItem Item) { public override string ToString() => Item.Label; }

    private void FillList()
    {
        var text = filterBox.Text.Trim();
        var keep = list.SelectedItems.OfType<ItemRow>().Select(r => r.Item).ToHashSet();
        list.Items.Clear();
        foreach (var item in items.Where(i => text.Length == 0 || i.Label.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new ItemRow(item);
            list.Items.Add(row);
            if (keep.Contains(item)) list.SelectedItems.Add(row);
        }
        UpdateCount();
    }

    private void UpdateCount() => countText.Text = $"{list.SelectedItems.Count} selected of {items.Count}";

    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Where to save the 3D files", InitialDirectory = Directory.Exists(folderBox.Text) ? folderBox.Text : null };
        if (dialog.ShowDialog(this) == true) folderBox.Text = dialog.FolderName;
    }

    private async void StartExport()
    {
        var chosen = list.SelectedItems.OfType<ItemRow>().Select(r => r.Item).ToList();
        if (chosen.Count == 0) { log.Text = "Select at least one item in the list (Select all does every one)."; return; }
        if (!float.TryParse(scaleBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) || scale <= 0)
        { log.Text = "The scale must be a positive number (0.001 is the default)."; return; }
        var folder = folderBox.Text.Trim();
        if (folder.Length == 0) { log.Text = "Choose a folder to save into."; return; }
        var format = SelectedFormat;
        try { Directory.CreateDirectory(folder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { log.Text = $"Can't use that folder: {error.Message}"; return; }

        exportButton.IsEnabled = false; cancelButton.IsEnabled = true;
        progressBar.Maximum = chosen.Count; progressBar.Value = 0;
        log.Clear();
        running = new CancellationTokenSource();
        var token = running.Token;
        var extraLog = new Progress<string>(message => Append("  " + message));
        var options = new ExportOptions { IslandTerrain = terrainBox.IsChecked == true, IslandObjects = objectsBox.IsChecked == true, Log = message => ((IProgress<string>)extraLog).Report(message) };
        var progress = new Progress<(int Index, string Message)>(report => { progressBar.Value = Math.Min(chosen.Count, report.Index + 1); Append(report.Message); });
        (int Done, int Failed, int Skipped) result = default;
        try { result = await Task.Run(() => ExportRunner.Run(chosen, format, scale, folder, options, progress, token)); }
        catch (Exception error) { Append("Export stopped: " + error.Message); DebugLog.Log($"Export window: {error}"); }
        var cancelled = token.IsCancellationRequested;
        running = null;
        exportButton.IsEnabled = true; cancelButton.IsEnabled = false;
        Append($"{(cancelled ? "Stopped: " : "Done: ")}{result.Done} exported{(result.Failed > 0 ? $", {result.Failed} failed" : "")}{(result.Skipped > 0 ? $", {result.Skipped} empty in the game's data" : "")}. Files are in {folder}");
    }

    private void Append(string message)
    {
        log.AppendText(message + Environment.NewLine);
        log.ScrollToEnd();
    }

    // Opens the window from another one (or the main window).
    public static void Show(Window owner, string? lba1Directory, string? lba2Directory, string? categoryHint = null, string? preselectFile = null)
    {
        var catalog = new ExportCatalog(lba1Directory, lba2Directory);
        if (catalog.Categories.Count == 0)
        {
            MessageBox.Show(owner, "Neither game folder is set. Choose them under File > Settings.", "Export 3D models", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var window = new ExportWindow(catalog, categoryHint, preselectFile) { Owner = owner };
        WindowLifecycle.Register(window, "ExportWindow");
        window.Show();
    }
}
