using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LBAAssembler.Grids;
using LBAAssembler.Lba1;
using LBAAssembler.Scenes;

namespace LBAAssembler;

// Tools > LBA1 / LBA2: interior grid editor. The isometric map of an interior (64 x 25 x 64 cells of blocks) for both games: a plan of one layer to paint
// on with the blocks of the grid's library, the isometric picture beside it, undo / redo, and the library itself (a block's bricks, new blocks).
// LBA1 grids are scene grids of LBA_GRI.HQR, LBA2's are the grids of LBA_BKG.HQR. Saves go through FileTransaction (a .bak of the archive).
internal sealed class GridEditorWindow : Window
{
    private const int Cell = 9;

    private readonly List<IGridBackend> backends = new();
    private readonly ComboBox backendBox = new() { Width = 90 };
    private readonly TextBox filter = new() { Padding = new Thickness(3), ToolTip = "Filter the grids" };
    private readonly ListBox grids = new() { Width = 190, FontFamily = new FontFamily("Consolas") };
    private readonly ListBox blocks = new() { Width = 200 };
    private readonly Image plan = new() { Width = 64 * Cell, Height = 64 * Cell, Cursor = Cursors.Cross };
    private readonly Canvas planHost = new() { Width = 64 * Cell, Height = 64 * Cell };
    private readonly System.Windows.Shapes.Rectangle cursor = new() { Stroke = Brushes.Yellow, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Image iso = new() { Stretch = Stretch.Uniform };
    private readonly Slider layer = new() { Minimum = 0, Maximum = 24, Value = 0, Width = 200, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly TextBlock layerText = new() { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock cellInfo = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock status = new() { Margin = new Thickness(8, 3, 8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button saveButton = new() { Content = "Save" }, undoButton = new() { Content = "Undo" }, redoButton = new() { Content = "Redo" };
    private readonly RadioButton paintTool = new() { Content = "Paint", IsChecked = true, GroupName = "gt" }, eraseTool = new() { Content = "Erase", GroupName = "gt" }, fillTool = new() { Content = "Fill rectangle", GroupName = "gt" }, moveTool = new() { Content = "Move", GroupName = "gt" };
    private (int Block, int X, int Y, int Z, int GrabX, int GrabZ)? moving;      // a block picked up with the Move tool: its origin, and where in it the pointer holds it
    private readonly StackPanel libraryPanel = new();
    private readonly DispatcherTimer isoTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private IGridBackend? backend;
    private int gridId = -1;
    private byte[] grid = Array.Empty<byte>();
    private byte[] library = Array.Empty<byte>();
    private bool libraryChanged, dirty;
    private int block = 1;
    private readonly Stack<byte[]> undo = new(), redo = new();
    private readonly Dictionary<int, byte[]?> brickCache = new();
    private readonly Dictionary<int, (byte R, byte G, byte B)> brickColour = new();
    private (int X, int Z)? dragStart;
    private WriteableBitmap? planBitmap;
    private byte[] planPixels = new byte[64 * Cell * 64 * Cell * 4];
    private Lba1SceneImage? isoImage;

    public GridEditorWindow(string? lba1Directory, string? lba2Directory, Func<int, string?>? describe1 = null, int? startLba2Grid = null, int? startLba1Grid = null)
    {
        Title = "Interior grid editor";
        Width = 1500; Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(Control.BackgroundProperty, "ThemeWindowBrush");
        SetResourceReference(Control.ForegroundProperty, "ThemeTextBrush");
        BuildLayout();
        try
        {
            if (lba1Directory is not null && Lba1Game.IsInstalled(lba1Directory)) backends.Add(new Lba1GridBackend(lba1Directory, describe1));
            if (lba2Directory is not null && File.Exists(System.IO.Path.Combine(lba2Directory, "LBA_BKG.HQR"))) backends.Add(new Lba2GridBackend(lba2Directory));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { status.Text = "Couldn't read a game folder: " + e.Message; }
        foreach (var b in backends) backendBox.Items.Add(b.Title);
        backendBox.SelectionChanged += (_, _) => { if (backendBox.SelectedIndex >= 0 && ConfirmDiscard()) OpenBackend(backends[backendBox.SelectedIndex]); };
        isoTimer.Tick += (_, _) => { isoTimer.Stop(); RenderIso(); };
        Closing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };
        Loaded += (_, _) =>
        {
            if (backends.Count == 0) { status.Text = "No LBA1 or LBA2 game folder is set (File > Settings)."; return; }
            var start = startLba2Grid is not null ? backends.FindIndex(b => b is Lba2GridBackend) : startLba1Grid is not null ? backends.FindIndex(b => b is Lba1GridBackend) : 0;
            backendBox.SelectedIndex = Math.Max(0, start);
            var startId = startLba2Grid ?? startLba1Grid;
            if (startId is { } id && grids.Items.OfType<GridItem>().FirstOrDefault(g => g.Id == id) is { } item) { grids.SelectedItem = item; grids.ScrollIntoView(item); }
        };
    }

    // ---- layout ---------------------------------------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        grids.SetResourceReference(Control.BackgroundProperty, "ThemeFieldBrush");
        grids.SetResourceReference(Control.ForegroundProperty, "ThemeTextBrush");
        blocks.SetResourceReference(Control.BackgroundProperty, "ThemeFieldBrush");
        blocks.SetResourceReference(Control.ForegroundProperty, "ThemeTextBrush");
        planHost.SetResourceReference(Panel.BackgroundProperty, "ThemeFieldBrush");
        layerText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextBrush");
        cellInfo.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextBrush");
        status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextBrush");
        var root = new DockPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        DockPanel.SetDock(top, Dock.Top);
        var gameLabel = new TextBlock { Text = "Game", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        gameLabel.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextMutedBrush");
        top.Children.Add(gameLabel);
        top.Children.Add(backendBox); backendBox.Margin = new Thickness(0, 0, 14, 0);
        foreach (var t in new[] { paintTool, moveTool, eraseTool, fillTool }) { t.SetResourceReference(Control.ForegroundProperty, "ThemeTextBrush"); t.Margin = new Thickness(0, 0, 10, 0); t.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(t); }
        paintTool.ToolTip = "Click / drag: places the selected block, its origin at the cell";
        eraseTool.ToolTip = "Click: removes the whole block under the pointer";
        moveTool.ToolTip = "Press on a building, wall or other block and drag: the whole block moves with the pointer on this layer (hold Ctrl to leave a copy behind). Undo puts it back";
        fillTool.ToolTip = "Drag a rectangle: fills the layer with the block, stepping by its size";
        var layerLabel = new TextBlock { Text = "Layer", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) };
        layerLabel.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextMutedBrush");
        top.Children.Add(layerLabel);
        top.Children.Add(layer); top.Children.Add(layerText);
        layer.ValueChanged += (_, _) => { layerText.Text = $"y = {(int)layer.Value}"; RedrawPlan(); };
        foreach (var (b, handler) in new (Button, RoutedEventHandler)[] { (undoButton, (_, _) => Undo()), (redoButton, (_, _) => Redo()), (saveButton, (_, _) => Save()) })
        { b.Padding = new Thickness(12, 3, 12, 3); b.Margin = new Thickness(14, 0, 0, 0); b.Click += handler; top.Children.Add(b); }
        root.Children.Add(top);
        var bottom = new Border { Child = status };
        bottom.SetResourceReference(Border.BackgroundProperty, "ThemeFieldBrush");
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var left = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(filter, Dock.Top); filter.Margin = new Thickness(0, 0, 0, 6);
        left.Children.Add(filter); left.Children.Add(grids);
        filter.TextChanged += (_, _) => FillGrids();
        grids.SelectionChanged += (_, _) => { if (grids.SelectedItem is GridItem g && g.Id != gridId && ConfirmDiscard()) OpenGrid(g.Id); };
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);

        var right = new DockPanel { Margin = new Thickness(8), Width = 250 };
        blocks.SelectionChanged += (_, _) => { if (blocks.SelectedItem is BlockItem b) { block = b.Number; ShowLibrary(); } };
        var rightTop = new StackPanel();
        var blocksHeading = new TextBlock { Text = "Blocks of the library", FontSize = 10, Margin = new Thickness(0, 0, 0, 4) };
        blocksHeading.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextMutedBrush");
        rightTop.Children.Add(blocksHeading);
        DockPanel.SetDock(rightTop, Dock.Top);
        right.Children.Add(rightTop);
        DockPanel.SetDock(libraryPanel, Dock.Bottom);
        libraryPanel.Margin = new Thickness(0, 8, 0, 0);
        right.Children.Add(libraryPanel);
        DockPanel.SetDock(cellInfo, Dock.Bottom);
        right.Children.Add(cellInfo);
        right.Children.Add(blocks);
        DockPanel.SetDock(right, Dock.Right);
        root.Children.Add(right);

        planHost.Children.Add(plan); planHost.Children.Add(cursor);
        planHost.MouseLeftButtonDown += PlanDown; planHost.MouseMove += PlanMove; planHost.MouseLeftButtonUp += PlanUp;
        planHost.MouseRightButtonDown += (_, e) => PickAt(e.GetPosition(planHost));
        RenderOptions.SetBitmapScalingMode(plan, BitmapScalingMode.NearestNeighbor);
        var centre = new Grid();
        centre.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        centre.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var planScroll = new ScrollViewer { Content = planHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 64 * Cell + 24 };
        planScroll.SetResourceReference(Control.BackgroundProperty, "ThemeFieldBrush");
        Grid.SetColumn(planScroll, 0); centre.Children.Add(planScroll);
        var isoHost = new Border { Child = iso, Margin = new Thickness(8, 0, 0, 0) };
        isoHost.SetResourceReference(Border.BackgroundProperty, "ThemeFieldBrush");
        Grid.SetColumn(isoHost, 1); centre.Children.Add(isoHost);
        root.Children.Add(centre);
        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is TextBox) return;
            var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
            else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { Save(); e.Handled = true; }
            else if (e.Key == Key.PageUp) { layer.Value = Math.Min(24, layer.Value + 1); e.Handled = true; }
            else if (e.Key == Key.PageDown) { layer.Value = Math.Max(0, layer.Value - 1); e.Handled = true; }
        };
        layerText.Text = "y = 0";
    }

    private sealed record GridItem(int Id, string Label) { public override string ToString() => Label; }
    private sealed record BlockItem(int Number, string Text, ImageSource? Thumb);

    // ---- opening / saving ----------------------------------------------------------------------------------------------------------

    private bool ConfirmDiscard()
    {
        if (!dirty && !libraryChanged) return true;
        var answer = MessageBox.Show(this, "Save the changes to this grid first?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) { RestoreSelection(); return false; }
        if (answer == MessageBoxResult.Yes) Save();
        return !(dirty || libraryChanged);
    }

    private void RestoreSelection()
    {
        if (grids.Items.OfType<GridItem>().FirstOrDefault(g => g.Id == gridId) is { } current) { grids.SelectionChanged -= null; grids.SelectedItem = current; }
    }

    private void OpenBackend(IGridBackend b)
    {
        backend = b; brickCache.Clear(); brickColour.Clear();
        gridId = -1;                     // grid 0 of one game is not grid 0 of the other
        FillGrids();
        Title = $"Interior grid editor - {b.Title}  [{b.Folder}]";
        if (grids.Items.Count > 0) grids.SelectedIndex = 0;
    }

    private void FillGrids()
    {
        if (backend is null) return;
        var text = filter.Text.Trim();
        grids.Items.Clear();
        foreach (var (id, label) in backend.Grids.Where(g => text.Length == 0 || g.Label.Contains(text, StringComparison.OrdinalIgnoreCase))) grids.Items.Add(new GridItem(id, label));
    }

    private void OpenGrid(int id)
    {
        if (backend is null) return;
        try
        {
            gridId = id;
            grid = backend.LoadGrid(id);
            library = backend.LoadLibrary(id);
            undo.Clear(); redo.Clear(); dirty = false; libraryChanged = false;
            FillBlocks();
            var users = backend.LibraryUsers(id);
            SetStatus($"{backend.Title} grid {id}: {GridPaint.BlockCount(library)} blocks in its library" + (users.Count > 1 ? $" (shared with {users.Count - 1} other grid{(users.Count == 2 ? "" : "s")}: library edits change them too)" : "") + ". Left button paints, right button picks the block under the pointer, PgUp / PgDn change the layer.");
            UpdateButtons();
            RedrawPlan();
            RenderIso();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            MessageBox.Show(this, $"Couldn't open grid {id}: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Save()
    {
        if (backend is null || gridId < 0) return;
        try
        {
            if (libraryChanged) backend.SaveLibrary(gridId, library);
            if (dirty) backend.SaveGrid(gridId, grid);
            dirty = false; libraryChanged = false;
            UpdateButtons();
            SetStatus($"Saved (the original archive is kept as .bak).");
        }
        catch (SceneValidationException e)
        {
            MessageBox.Show(this, "Not saved. The game would misread this map:\n\n" + string.Join("\n", e.Issues.Where(i => i.Severity == SceneIssueSeverity.Error).Take(10)), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't save: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetStatus(string text) => status.Text = text;
    private void UpdateButtons()
    {
        saveButton.Content = dirty || libraryChanged ? "Save *" : "Save";
        undoButton.IsEnabled = undo.Count > 0; redoButton.IsEnabled = redo.Count > 0;
    }

    // ---- bricks, blocks -------------------------------------------------------------------------------------------------------------

    private byte[]? Brick(int number)
    {
        if (backend is null) return null;
        if (!brickCache.TryGetValue(number, out var data)) brickCache[number] = data = backend.Brick(number);
        return data;
    }

    private (byte R, byte G, byte B) BrickColour(int number)
    {
        if (brickColour.TryGetValue(number, out var known)) return known;
        var data = Brick(number);
        long r = 0, g = 0, b = 0, n = 0;
        if (data is { Length: >= 4 } && backend is not null)
        {
            var pixels = new byte[data[0] * data[1] * 4];
            Lba1GridRenderer.Blit(pixels, data[0], data[1], data, 0, 0, backend.Palette);
            for (var i = 0; i < pixels.Length; i += 4) if (pixels[i + 3] != 0) { b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; n++; }
        }
        return brickColour[number] = n == 0 ? ((byte)40, (byte)40, (byte)40) : ((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private void FillBlocks()
    {
        blocks.Items.Clear();
        var count = GridPaint.BlockCount(library);
        for (var n = 1; n <= count; n++)
        {
            if (GridPaint.Info(library, n) is not { } info) continue;
            blocks.Items.Add(new BlockItem(n, $"{n}  {info.Dx}x{info.Dy}x{info.Dz}", Thumbnail(n)));
        }
        blocks.ItemTemplate = new DataTemplate();
        var stack = new FrameworkElementFactory(typeof(StackPanel)); stack.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("Thumb")); image.SetValue(FrameworkElement.WidthProperty, 44.0); image.SetValue(FrameworkElement.HeightProperty, 34.0); image.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text")); text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center); text.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextBrush");
        stack.AppendChild(image); stack.AppendChild(text);
        blocks.ItemTemplate.VisualTree = stack;
        if (blocks.Items.Count > 0) { block = Math.Clamp(block, 1, count); blocks.SelectedIndex = block - 1; }
    }

    private ImageSource? Thumbnail(int number)
    {
        if (backend is null) return null;
        var placements = new List<Lba1Placement>();
        foreach (var (pos, _, _, brick) in GridPaint.Entries(library, number))
        {
            if (brick < 0 || GridPaint.Info(library, number) is not { } info) continue;
            placements.Add(new Lba1Placement(pos % Math.Max(1, info.Dx), pos / Math.Max(1, info.Dx) % Math.Max(1, info.Dy), pos / Math.Max(1, info.Dx * info.Dy), brick));
        }
        if (placements.Count == 0) return null;
        try
        {
            var image = Lba1GridRenderer.Render(new[] { new Lba1Tile(placements, 0, 0, 0) }, Brick, backend.Palette);
            var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { return null; }
    }

    // ---- library editing --------------------------------------------------------------------------------------------------------------

    private void ShowLibrary()
    {
        libraryPanel.Children.Clear();
        if (GridPaint.Info(library, block) is not { } info) return;
        var blockHeading = new TextBlock { Text = $"Block {block}: {info.Dx} x {info.Dy} x {info.Dz}", FontSize = 10, Margin = new Thickness(0, 0, 0, 4) };
        blockHeading.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextMutedBrush");
        libraryPanel.Children.Add(blockHeading);
        var entryBox = new TextBox { Text = "0", Width = 44, Padding = new Thickness(2) }; var brickBox = new TextBox { Padding = new Thickness(2), Width = 60 }; var shapeBox = new TextBox { Padding = new Thickness(2), Width = 44 };
        var entries = GridPaint.Entries(library, block).ToList();
        void Load() { if (int.TryParse(entryBox.Text, out var p) && entries.FirstOrDefault(e => e.Pos == p) is { } e) { brickBox.Text = e.Brick.ToString(); shapeBox.Text = e.Shape.ToString(); } }
        entryBox.TextChanged += (_, _) => Load();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var (label, box) in new[] { ("entry", entryBox), ("brick", brickBox), ("shape", shapeBox) })
        {
            var labelText = new TextBlock { Text = label, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            labelText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextMutedBrush");
            row.Children.Add(labelText); box.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(box);
        }
        libraryPanel.Children.Add(row);
        Load();
        var apply = new Button { Content = "Set entry", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 6, 0), ToolTip = "Sets the brick and shape of one cell of the block (0 = a floor-like solid shape; the game uses the shape for collisions)" };
        apply.Click += (_, _) =>
        {
            if (!int.TryParse(entryBox.Text, out var p) || !int.TryParse(brickBox.Text, out var b) || !int.TryParse(shapeBox.Text, out var s) || p < 0 || p >= entries.Count) return;
            library = GridPaint.SetEntry(library, block, p, b, s); libraryChanged = true; brickCache.Remove(b);
            UpdateButtons(); FillBlocksKeepSelection(); DirtyIso();
            SetStatus($"Block {block} entry {p} now shows brick {b}. This library is shared by the grids listed in the status line when the game reuses it.");
        };
        var add = new Button { Content = "New block…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 0, 0), ToolTip = "Adds a block of the size given, all cells showing one brick (edit the entries afterwards)" };
        add.Click += (_, _) => NewBlock();
        var buttons = new WrapPanel(); buttons.Children.Add(apply); buttons.Children.Add(add);
        libraryPanel.Children.Add(buttons);
    }

    private void NewBlock()
    {
        var dialog = new Window { Title = "New block", Width = 300, Height = 200, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        dialog.SetResourceReference(Control.BackgroundProperty, "ThemeWindowBrush");
        dialog.SetResourceReference(Control.ForegroundProperty, "ThemeTextBrush");
        var dx = new TextBox { Text = "1" }; var dy = new TextBox { Text = "1" }; var dz = new TextBox { Text = "1" }; var brick = new TextBox { Text = "0" };
        var panel = new StackPanel { Margin = new Thickness(12) };
        foreach (var (label, box) in new[] { ("size x", dx), ("size y", dy), ("size z", dz), ("brick (0-based)", brick) })
        {
            var r = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var labelText = new TextBlock { Text = label, Width = 110 };
            labelText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextBrush");
            r.Children.Add(labelText); r.Children.Add(box); panel.Children.Add(r);
        }
        var ok = new Button { Content = "Add", Padding = new Thickness(16, 3, 16, 3), IsDefault = true, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(ok); dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        if (!int.TryParse(dx.Text, out var x) || !int.TryParse(dy.Text, out var y) || !int.TryParse(dz.Text, out var z) || !int.TryParse(brick.Text, out var b) || x < 1 || y < 1 || z < 1 || x * y * z > 200 || b < 0) { SetStatus("A block is 1..? cells each way (at most 200 cells in all) with a brick number."); return; }
        library = GridPaint.AppendBlock(library, x, y, z, b); libraryChanged = true;
        FillBlocks(); block = GridPaint.BlockCount(library); blocks.SelectedIndex = block - 1; blocks.ScrollIntoView(blocks.SelectedItem);
        UpdateButtons();
        SetStatus($"Block {block} added ({x}x{y}x{z}, brick {b}). Paint with it; edit its entries to use different bricks.");
    }

    private void FillBlocksKeepSelection() { var keep = block; FillBlocks(); block = keep; if (keep >= 1 && keep <= blocks.Items.Count) blocks.SelectedIndex = keep - 1; }

    // ---- painting ---------------------------------------------------------------------------------------------------------------------

    private (int X, int Z)? CellAt(Point p)
    {
        int x = (int)(p.X / Cell), z = (int)(p.Y / Cell);
        return x >= 0 && z >= 0 && x < 64 && z < 64 ? (x, z) : null;
    }

    private void PlanDown(object sender, MouseButtonEventArgs e)
    {
        if (CellAt(e.GetPosition(planHost)) is not { } c) return;
        if (moveTool.IsChecked == true)
        {
            if (GridPaint.OriginOf(grid, library, c.X, (int)layer.Value, c.Z) is not { } origin) { SetStatus("No block there on this layer: pick the layer the block is on."); return; }
            moving = (origin.Block, origin.X, origin.Y, origin.Z, c.X - origin.X, c.Z - origin.Z);
            planHost.CaptureMouse();
            dragStart = c;
            SetStatus($"Moving block {origin.Block}: drop it where it should go" + (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? " (a copy is left behind)." : "."));
            return;
        }
        planHost.CaptureMouse();
        dragStart = c;
        if (fillTool.IsChecked != true) Apply(c);
    }

    private void PlanMove(object sender, MouseEventArgs e)
    {
        if (CellAt(e.GetPosition(planHost)) is not { } c) return;
        Canvas.SetLeft(cursor, c.X * Cell); Canvas.SetTop(cursor, c.Z * Cell);
        if (moving is { } held)
        {
            // the picked block's outline follows the pointer, held where it was picked up
            var size = GridPaint.Info(library, held.Block) is { } picked ? (picked.Dx, picked.Dz) : (1, 1);
            Canvas.SetLeft(cursor, (c.X - held.GrabX) * Cell); Canvas.SetTop(cursor, (c.Z - held.GrabZ) * Cell);
            cursor.Width = size.Item1 * Cell; cursor.Height = size.Item2 * Cell;
            return;
        }
        var span = GridPaint.Info(library, block) is { } info && paintTool.IsChecked == true ? (info.Dx, info.Dz) : (1, 1);
        cursor.Width = span.Item1 * Cell; cursor.Height = span.Item2 * Cell;
        var y = (int)layer.Value;
        var cell = grid.Length > 0 ? Lba1GridEdit.Get(grid, c.X, y, c.Z) : default;
        cellInfo.Text = $"x {c.X}  y {y}  z {c.Z}\nblock {cell.Block}  position {cell.Pos}";
        if (e.LeftButton == MouseButtonState.Pressed && planHost.IsMouseCaptured && fillTool.IsChecked != true && dragStart is { } start)
        {
            // dragging paints again, stepping by the block's size so a big block is not laid over itself
            var step = paintTool.IsChecked == true && GridPaint.Info(library, block) is { } bi ? (Math.Max(1, bi.Dx), Math.Max(1, bi.Dz)) : (1, 1);
            if ((c.X - start.X) % step.Item1 == 0 && (c.Z - start.Z) % step.Item2 == 0) Apply(c);
        }
    }

    private void PlanUp(object sender, MouseButtonEventArgs e)
    {
        if (!planHost.IsMouseCaptured) return;
        planHost.ReleaseMouseCapture();
        if (moving is { } held)
        {
            moving = null;
            if (CellAt(e.GetPosition(planHost)) is { } drop) MoveBlock(held, drop.X - held.GrabX, drop.Z - held.GrabZ, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            dragStart = null;
            return;
        }
        if (fillTool.IsChecked == true && dragStart is { } a && CellAt(e.GetPosition(planHost)) is { } b)
            Edit("fill", g => GridPaint.FillRectangle(g, library, block, a.X, a.Z, b.X, b.Z, (int)layer.Value));
        dragStart = null;
    }

    // The Move tool's drop: the block's cells are cleared (unless copying) and it is placed again with its origin at (x, z), on the same layer.
    private void MoveBlock((int Block, int X, int Y, int Z, int GrabX, int GrabZ) held, int x, int z, bool copy)
    {
        if (x == held.X && z == held.Z) { SetStatus("The block is where it was."); return; }
        Edit(copy ? "copy block" : "move block", g =>
        {
            var cells = GridPaint.PlaceCells(library, held.Block, x, held.Y, z) ?? throw new ArgumentException("The block doesn't fit there: it would stick out of the map.");
            return Lba1GridEdit.SetCells(copy ? g : GridPaint.Erase(g, library, held.X, held.Y, held.Z), cells);
        });
        if (status.Text.StartsWith("Can't do that")) return;
        SetStatus(copy ? $"Copied block {held.Block} to x {x}, z {z}." : $"Moved block {held.Block} to x {x}, z {z}.");
    }

    private void PickAt(Point p)
    {
        if (CellAt(p) is not { } c || Lba1GridEdit.Get(grid, c.X, (int)layer.Value, c.Z) is not { Block: > 0 } cell) return;
        block = cell.Block; blocks.SelectedIndex = block - 1; blocks.ScrollIntoView(blocks.SelectedItem);
        SetStatus($"Picked block {block}.");
    }

    private void Apply((int X, int Z) c)
    {
        var y = (int)layer.Value;
        if (eraseTool.IsChecked == true) Edit("erase", g => GridPaint.Erase(g, library, c.X, y, c.Z));
        else Edit("paint", g => GridPaint.PlaceCells(library, block, c.X, y, c.Z) is { } cells ? Lba1GridEdit.SetCells(g, cells) : g);
    }

    private void Edit(string label, Func<byte[], byte[]> change)
    {
        byte[] next;
        try { next = change(grid); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException) { SetStatus("Can't do that: " + e.Message); return; }
        if (ReferenceEquals(next, grid) || next.AsSpan().SequenceEqual(grid)) return;
        undo.Push(grid); redo.Clear();
        grid = next; dirty = true;
        UpdateButtons(); RedrawPlan(); DirtyIso();
    }

    private void Undo() { if (undo.Count == 0) return; redo.Push(grid); grid = undo.Pop(); dirty = true; UpdateButtons(); RedrawPlan(); DirtyIso(); }
    private void Redo() { if (redo.Count == 0) return; undo.Push(grid); grid = redo.Pop(); dirty = true; UpdateButtons(); RedrawPlan(); DirtyIso(); }

    // ---- drawing ------------------------------------------------------------------------------------------------------------------------

    private void RedrawPlan()
    {
        if (backend is null || grid.Length == 0) return;
        var cells = Lba1GridCodec.Decode(grid);
        var y0 = (int)layer.Value;
        for (var z = 0; z < 64; z++)
            for (var x = 0; x < 64; x++)
            {
                // the highest drawn cell at or below the layer: bright when it is on the layer, dim when it lies below
                byte r = 243, g = 248, b = 255; var dim = 1.0; var empty = true;   // (an empty cell: the scheme's surface colour, with grid lines in its border colour)
                for (var y = y0; y >= 0; y--)
                {
                    var at = ((z * 64 + x) * 25 + y) * 2;
                    int blk = cells[at], pos = cells[at + 1];
                    if (blk == 0) continue;
                    if (GridPaint.Info(library, blk) is { } info && pos < info.Dx * info.Dy * info.Dz)
                    {
                        var entry = GridPaint.Entries(library, blk).ElementAtOrDefault(pos);
                        if (entry.Brick >= 0) { (r, g, b) = BrickColour(entry.Brick); dim = y == y0 ? 1.0 : 0.45; empty = false; break; }
                    }
                }
                var edge = (x & 7) == 0 || (z & 7) == 0;
                for (var py = 0; py < Cell; py++)
                    for (var px = 0; px < Cell; px++)
                    {
                        var line = px == 0 || py == 0;
                        var o = ((z * Cell + py) * 64 * Cell + x * Cell + px) * 4;
                        var k = line ? (edge ? 1.5 : 1.15) : 1.0;
                        if (empty) { (byte, byte, byte) c = line ? (edge ? ((byte)0xA9, (byte)0xC3, (byte)0xE0) : ((byte)0xCB, (byte)0xDD, (byte)0xF0)) : (r, g, b); planPixels[o] = c.Item3; planPixels[o + 1] = c.Item2; planPixels[o + 2] = c.Item1; planPixels[o + 3] = 255; continue; }
                        planPixels[o] = (byte)Math.Min(255, b * dim * k); planPixels[o + 1] = (byte)Math.Min(255, g * dim * k); planPixels[o + 2] = (byte)Math.Min(255, r * dim * k); planPixels[o + 3] = 255;
                    }
            }
        planBitmap ??= new WriteableBitmap(64 * Cell, 64 * Cell, 96, 96, PixelFormats.Bgra32, null);
        planBitmap.WritePixels(new Int32Rect(0, 0, 64 * Cell, 64 * Cell), planPixels, 64 * Cell * 4, 0);
        plan.Source = planBitmap;
    }

    private void DirtyIso() { isoTimer.Stop(); isoTimer.Start(); }

    private void RenderIso()
    {
        if (backend is null || grid.Length == 0) return;
        try
        {
            isoImage = Lba1GridRenderer.Render(grid, library, Brick, backend.Palette);
            var bmp = BitmapSource.Create(isoImage.Width, isoImage.Height, 96, 96, PixelFormats.Bgra32, null, isoImage.Bgra, isoImage.Width * 4);
            bmp.Freeze();
            iso.Source = bmp;
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { SetStatus("Couldn't draw the map: " + e.Message); }
    }
}
