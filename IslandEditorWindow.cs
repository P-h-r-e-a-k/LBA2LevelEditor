using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LBA2LevelEditor.Terrain;

namespace LBA2LevelEditor;

// Tools > LBA2: island terrain editor. An island (.ILE) from above, edited with brushes: heights (with levelling tools for
// uneven islands), the baked light and shadows, the ground texture / game codes / water depth, and the decor objects. Works
// on IslandFile (Terrain/), saves through it (a .bak of the original, only changed records rewritten).
internal sealed class IslandEditorWindow : Window
{
    private enum Tool
    {
        Raise, Lower, Smooth, Flatten, LevelPlane, Ramp, Terrace, Relief,
        PaintLight, Darken, Lighten, RemoveShadows, CastShadows, BlobShadow, WaterDepth,
        Eyedropper, PaintTile, PaintTexture, PaintCode, FixDiagonals,
        SelectDecor, AddDecor, ObjectShadow, ClearObjectShadow,
    }

    private static readonly (string Group, (Tool Tool, string Name, string Tip)[] Items)[] ToolGroups =
    {
        ("HEIGHT", new[]
        {
            (Tool.Raise, "Raise", "Hold to build the ground up under the brush"),
            (Tool.Lower, "Lower", "Hold to dig the ground down"),
            (Tool.Smooth, "Smooth", "Blends each vertex towards its neighbours"),
            (Tool.Flatten, "Flatten to level", "Pulls the ground to the Level value (Alt-click reads the level from the ground under the pointer)"),
            (Tool.LevelPlane, "Level to plane", "Fits a plane to the ground under the brush when the stroke starts and pulls the stroke onto it: removes the bumps and keeps the slope (tick 'Horizontal' to flatten it too)"),
            (Tool.Ramp, "Ramp", "Click the two ends: the ground between them becomes a straight ramp, as wide as the brush"),
            (Tool.Terrace, "Terrace", "Snaps heights to multiples of the Terrace step"),
            (Tool.Relief, "Relief x", "Exaggerates (>1) or flattens (<1) the relief under the brush around its mean height"),
        }),
        ("LIGHT & SHADOW", new[]
        {
            (Tool.PaintLight, "Set light", "Paints the brightness (0-15) given by the Light value"),
            (Tool.Darken, "Add shadow", "Hold to darken: paint a shadow"),
            (Tool.Lighten, "Lighten", "Hold to brighten"),
            (Tool.RemoveShadows, "Remove shadows", "Lifts vertices that are darker than the terrain's plain lighting back up to it (only brightens)"),
            (Tool.CastShadows, "Cast shadows", "Adds the shadows the terrain (and decors, if ticked) cast for the light angle in the bake settings (only darkens)"),
            (Tool.BlobShadow, "Blob shadow", "Click to drop a round shadow (the Light value sets its depth)"),
            (Tool.WaterDepth, "Water depth", "Sets how far Twinsen sinks on water and marsh polygons (0-15 steps of 200 units)"),
        }),
        ("GROUND", new[]
        {
            (Tool.Eyedropper, "Pick triangle", "Click to take the texture, game code and diagonal of a triangle"),
            (Tool.PaintTexture, "Paint picked", "Paints the picked triangle's texture (and diagonal if ticked) onto the cells"),
            (Tool.PaintTile, "Paint atlas tile", "Paints the tile selected on the ground texture atlas (drag a square on it) onto the cells"),
            (Tool.PaintCode, "Paint game code", "Sets what the ground does (water, lava, electric, conveyor ...) without touching the picture"),
            (Tool.FixDiagonals, "Fix diagonals", "Re-cuts the cells under the brush along their flatter diagonal (after big height edits)"),
        }),
        ("OBJECTS", new[]
        {
            (Tool.SelectDecor, "Select / move", "Click an object to select it, drag to move it, Delete removes it"),
            (Tool.AddDecor, "Add object", "Click to place a new object (the Body number of the island's OBL file)"),
            (Tool.ObjectShadow, "Shadow under object", "Click an object to bake a shadow under its bounding box, as the retail islands have under buildings (depth = Shadow depth)"),
            (Tool.ClearObjectShadow, "Clear object shadow", "Click an object to lift the baked shadow under it back to plain lighting"),
        }),
    };

    private readonly string gameDirectory;
    private IslandFile? island;
    private IslandHistory? history;
    private IslandMapRenderer? renderer;
    private WriteableBitmap? bitmap;
    private byte[] palette = Array.Empty<byte>();
    private MapView view = MapView.Terrain;
    private Tool tool = Tool.Raise;

    // view
    private double zoom = 1;
    private Point pan;
    private bool panning; private Point panStart; private Point panOrigin;

    // stroke state
    private bool stroking;
    private (double Gx, double Gz) pointer;
    private (double A, double B, double C)? strokePlane;
    private IslandOps.DecorFollow? follow;
    private (double Gx, double Gz, double H)? rampStart;
    private IslandGround.Sample? picked;
    private (IslandCube Cube, IslandDecor Decor)? selected;
    private bool draggingDecor;
    private (int X, int Y, int W, int H)? tile;
    private readonly DispatcherTimer strokeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    // controls
    private readonly ComboBox islandBox = new();
    private readonly ComboBox viewBox = new();
    private readonly Canvas world = new();
    private readonly Image mapImage = new();
    private readonly Canvas overlay = new();
    private readonly Grid viewport = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Ellipse brushCircle = new() { Stroke = Brushes.White, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Slider radius = new() { Minimum = 1, Maximum = 60, Value = 8, Width = 200 };
    private readonly Slider hardness = new() { Minimum = 0, Maximum = 0.95, Value = 0.4, Width = 200 };
    private readonly Slider strength = new() { Minimum = 1, Maximum = 100, Value = 40, Width = 200 };
    private readonly TextBox levelBox = new() { Text = "1000" };
    private readonly TextBox lightBox = new() { Text = "9" };
    private readonly TextBox stepBox = new() { Text = "400" };
    private readonly TextBox reliefBox = new() { Text = "0.8" };
    private readonly TextBox bodyBox = new() { Text = "0" };
    private readonly ComboBox codeBox = new();
    private readonly CheckBox horizontalBox = new() { Content = "Horizontal (flatten the slope too)" };
    private readonly CheckBox followBox = new() { Content = "Objects follow the ground", IsChecked = true };
    private readonly CheckBox diagonalBox = new() { Content = "Also copy the diagonal" };
    private readonly TextBox azimuthBox = new() { Text = "0" };
    private readonly TextBox elevationBox = new() { Text = "45" };
    private readonly TextBox gainBox = new() { Text = "11" };
    private readonly TextBox offsetBox = new() { Text = "1.2" };
    private readonly TextBox shadowLevelBox = new() { Text = "3" };
    private readonly TextBox shadowDepthBox = new() { Text = "5" };
    private readonly CheckBox terrainShadowBox = new() { Content = "Terrain casts shadows", IsChecked = true };
    private readonly CheckBox decorShadowBox = new() { Content = "Objects cast shadows" };
    private readonly TextBlock status = new() { Foreground = Brushes.Gainsboro, Margin = new Thickness(8, 3, 8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock info = new() { Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    private readonly Canvas profile = new() { Height = 96, Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x13, 0x11)), ClipToBounds = true };
    private readonly Image atlasImage = new() { Width = 256, Height = 256, Stretch = Stretch.Fill };
    private readonly Canvas atlasCanvas = new() { Width = 256, Height = 256 };
    private readonly Rectangle atlasSelection = new() { Stroke = Brushes.Yellow, StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Button undoButton = new() { Content = "Undo" };
    private readonly Button redoButton = new() { Content = "Redo" };
    private readonly Button saveButton = new() { Content = "Save" };
    private readonly Dictionary<Tool, RadioButton> toolButtons = new();
    private readonly TextBox[] decorFields = Enumerable.Range(0, 8).Select(_ => new TextBox { Padding = new Thickness(2) }).ToArray();
    private readonly StackPanel decorPanel = new();
    private bool loadingFields, switching;
    private string? currentName;

    public IslandEditorWindow(string gameDirectory, string? startIsland = null)
    {
        this.gameDirectory = gameDirectory;
        Title = "LBA2: island terrain editor";
        Width = 1500; Height = 950;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x19));
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xDA));
        BuildLayout();

        foreach (var path in Directory.EnumerateFiles(gameDirectory, "*.ILE").Where(p => !System.IO.Path.GetFileName(p).StartsWith("_", StringComparison.Ordinal)).OrderBy(p => p))
            islandBox.Items.Add(System.IO.Path.GetFileName(path));
        islandBox.SelectionChanged += (_, _) => { if (!switching && islandBox.SelectedItem is string name) TryOpen(name); };
        var start = startIsland is not null && islandBox.Items.Contains(startIsland) ? startIsland : islandBox.Items.Contains("DESERT.ILE") ? "DESERT.ILE" : islandBox.Items.OfType<string>().FirstOrDefault();
        strokeTimer.Tick += (_, _) => { if (stroking) StrokeTick(); };
        Closing += OnClosing;
        Loaded += (_, _) => { if (start is not null) islandBox.SelectedItem = start; };
    }

    // ---- layout ---------------------------------------------------------------------------------------------------------------------------

    private static Brush Muted => new SolidColorBrush(Color.FromRgb(0x89, 0x95, 0x8B));

    private void BuildLayout()
    {
        var root = new DockPanel();

        // top bar
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(Label("Island"));
        islandBox.Width = 150; islandBox.Margin = new Thickness(0, 0, 12, 0);
        top.Children.Add(islandBox);
        top.Children.Add(Label("View"));
        foreach (var (v, name) in new[] { (MapView.Terrain, "Terrain (as lit)"), (MapView.Height, "Height"), (MapView.Light, "Baked light"), (MapView.Shadows, "Baked shadows (vs plain light)"), (MapView.GameCode, "Game codes"), (MapView.WaterDepth, "Water depth") })
            viewBox.Items.Add(new ComboBoxItem { Content = name, Tag = v });
        viewBox.SelectedIndex = 0; viewBox.Width = 210; viewBox.Margin = new Thickness(0, 0, 12, 0);
        viewBox.SelectionChanged += (_, _) => { if (viewBox.SelectedItem is ComboBoxItem { Tag: MapView v }) { view = v; RedrawAll(); } };
        top.Children.Add(viewBox);
        foreach (var (button, handler) in new (Button, RoutedEventHandler)[]
        {
            (undoButton, (_, _) => DoUndo()), (redoButton, (_, _) => DoRedo()), (saveButton, (_, _) => Save()),
            (new Button { Content = "Play a scene…" }, (_, _) => Play()), (new Button { Content = "Fit" }, (_, _) => FitView()),
            (new Button { Content = "Weld cube borders" }, (_, _) => Weld()), (new Button { Content = "Check" }, (_, _) => Check()),
        })
        {
            button.Padding = new Thickness(12, 3, 12, 3); button.Margin = new Thickness(0, 0, 6, 0);
            button.Click += handler;
            top.Children.Add(button);
        }
        root.Children.Add(top);

        var bottom = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x13, 0x11)), Child = status };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // left: tools and options
        var left = new StackPanel { Margin = new Thickness(8) };
        foreach (var (group, items) in ToolGroups)
        {
            left.Children.Add(Heading(group));
            foreach (var (t, name, tip) in items)
            {
                var button = new RadioButton { Content = name, GroupName = "tool", ToolTip = tip, Margin = new Thickness(0, 1, 0, 1), Foreground = Foreground };
                var captured = t;
                button.Checked += (_, _) => SelectTool(captured);
                toolButtons[t] = button;
                left.Children.Add(button);
            }
        }
        left.Children.Add(Heading("BRUSH"));
        left.Children.Add(SliderRow("Radius", radius, "vertices (cells); [ and ] change it"));
        left.Children.Add(SliderRow("Hardness", hardness, "how much of the radius is full strength"));
        left.Children.Add(SliderRow("Strength", strength, "rate while held"));
        left.Children.Add(Heading("TOOL SETTINGS"));
        left.Children.Add(FieldRow("Level", levelBox, "world units; the height Flatten pulls to"));
        left.Children.Add(FieldRow("Light value", lightBox, "0-15: Set light / Blob depth / Water depth"));
        left.Children.Add(FieldRow("Terrace step", stepBox, "world units"));
        left.Children.Add(FieldRow("Relief factor", reliefBox, "<1 flatten, >1 exaggerate"));
        for (var i = 0; i < 16; i++) codeBox.Items.Add($"{i}: {IslandPolygon.CodeJeuNames[i]}");
        codeBox.SelectedIndex = 1;
        var codeRow = FieldRow("Game code", codeBox, "what the ground does");
        left.Children.Add(codeRow);
        left.Children.Add(FieldRow("Object body", bodyBox, "Body number in the island's OBL for Add object"));
        horizontalBox.Foreground = followBox.Foreground = diagonalBox.Foreground = Foreground;
        terrainShadowBox.Foreground = decorShadowBox.Foreground = Foreground;
        left.Children.Add(horizontalBox); left.Children.Add(followBox); left.Children.Add(diagonalBox);
        var scroll = new ScrollViewer { Width = 270, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = left };
        DockPanel.SetDock(scroll, Dock.Left);
        root.Children.Add(scroll);

        // right: info, object, bake, atlas
        var right = new StackPanel { Margin = new Thickness(8) };
        right.Children.Add(Heading("UNDER THE POINTER"));
        right.Children.Add(info);
        right.Children.Add(Heading("SELECTED OBJECT"));
        BuildDecorPanel();
        right.Children.Add(decorPanel);
        right.Children.Add(Heading("BAKE SETTINGS"));
        right.Children.Add(FieldRow("Azimuth °", azimuthBox, "direction the light comes from (the cube's BetaLight is 360 - azimuth)"));
        right.Children.Add(FieldRow("Elevation °", elevationBox, "height of the light above the horizon"));
        right.Children.Add(FieldRow("Gain", gainBox, "brightness = offset + gain x (normal . light)"));
        right.Children.Add(FieldRow("Offset", offsetBox, ""));
        right.Children.Add(FieldRow("Shadow level", shadowLevelBox, "the brightest a shadowed vertex may be (0-15)"));
        right.Children.Add(FieldRow("Shadow depth", shadowDepthBox, "levels a footprint shadow under an object darkens by (retail buildings: about 5)"));
        right.Children.Add(terrainShadowBox); right.Children.Add(decorShadowBox);
        var bakeButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void BakeButton(string text, string tip, RoutedEventHandler handler)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += handler; bakeButtons.Children.Add(b);
        }
        BakeButton("Use the cube's light", "Reads the azimuth and elevation from the island's first cube", (_, _) => LoadBakeFromCube());
        BakeButton("Bake all light", "Recomputes the whole island's brightness from its terrain (replaces the stored light, shadows included)", (_, _) => BakeWhole(false));
        BakeButton("Add cast shadows", "Darkens vertices the terrain / objects shade, everywhere", (_, _) => BakeWhole(true));
        BakeButton("Shadows under objects", "Bakes a footprint shadow under every object at least 2 cells across (like the retail buildings)", (_, _) => FootprintsAll(false));
        BakeButton("Remove all shadows", "Lifts every shadowed vertex back to the plain lighting", (_, _) => RemoveAllShadows());
        right.Children.Add(bakeButtons);
        right.Children.Add(Heading("GROUND ATLAS (drag a square for Paint atlas tile)"));
        atlasCanvas.Children.Add(atlasImage); atlasCanvas.Children.Add(atlasSelection);
        atlasCanvas.MouseLeftButtonDown += AtlasDown; atlasCanvas.MouseMove += AtlasMove; atlasCanvas.MouseLeftButtonUp += (_, _) => atlasCanvas.ReleaseMouseCapture();
        right.Children.Add(new Border { BorderBrush = Muted, BorderThickness = new Thickness(1), Child = atlasCanvas, HorizontalAlignment = HorizontalAlignment.Left });
        var rightScroll = new ScrollViewer { Width = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = right };
        DockPanel.SetDock(rightScroll, Dock.Right);
        root.Children.Add(rightScroll);

        // centre: the map and the profile strip
        DockPanel.SetDock(profile, Dock.Bottom);
        var centre = new DockPanel();
        centre.Children.Add(profile);
        RenderOptions.SetBitmapScalingMode(mapImage, BitmapScalingMode.NearestNeighbor);
        world.Children.Add(mapImage); world.Children.Add(overlay); world.Children.Add(brushCircle);
        world.RenderTransformOrigin = new Point(0, 0);
        viewport.Children.Add(world);
        viewport.MouseWheel += (_, e) => ZoomAt(e.GetPosition(viewport), e.Delta > 0 ? 1.2 : 1 / 1.2);
        viewport.MouseLeftButtonDown += ViewDown; viewport.MouseLeftButtonUp += ViewUp;
        viewport.MouseMove += ViewMove; viewport.MouseLeave += (_, _) => brushCircle.Visibility = Visibility.Collapsed;
        viewport.MouseRightButtonDown += (_, e) => { panning = true; panStart = e.GetPosition(viewport); panOrigin = pan; viewport.CaptureMouse(); };
        viewport.MouseRightButtonUp += (_, _) => { panning = false; viewport.ReleaseMouseCapture(); };
        viewport.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { panning = true; panStart = e.GetPosition(viewport); panOrigin = pan; viewport.CaptureMouse(); } };
        viewport.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { panning = false; viewport.ReleaseMouseCapture(); } };
        viewport.SizeChanged += (_, _) => { if (renderer is not null && zoom == 1 && pan == default) FitView(); };
        centre.Children.Add(viewport);
        root.Children.Add(centre);
        Content = root;

        PreviewKeyDown += OnKey;
        toolButtons[Tool.Raise].IsChecked = true;
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 10, Foreground = Muted, Margin = new Thickness(0, 12, 0, 4) };

    private static UIElement FieldRow(string label, Control box, string tip)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = Label(label); text.ToolTip = tip;
        grid.Children.Add(text);
        box.ToolTip = tip; box.Padding = new Thickness(3);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    private static UIElement SliderRow(string label, Slider slider, string tip)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = Label(label); text.ToolTip = tip;
        grid.Children.Add(text);
        slider.Width = double.NaN; slider.ToolTip = tip;
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        return grid;
    }

    private void BuildDecorPanel()
    {
        var names = new[] { "Body", "X", "Y", "Z", "Angle °", "Game code", "Hide var", "Cube" };
        for (var i = 0; i < names.Length; i++) decorPanel.Children.Add(FieldRow(names[i], decorFields[i], i == 6 ? "A game variable that hides the object while set (negative: while clear); 0 = always shown" : ""));
        decorFields[7].IsReadOnly = true;
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void B(string text, string tip, RoutedEventHandler handler)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += handler; buttons.Children.Add(b);
        }
        B("Apply", "Writes the fields to the object (the ZV moves with it)", (_, _) => ApplyDecorFields());
        B("Drop to ground", "", (_, _) => EditSelected("drop object", (c, d) => IslandDecors.DropToGround(island!, c, d)));
        B("Shadow under", "Bakes a footprint shadow under this object", (_, _) => FootprintSelected(false));
        B("Clear shadow", "Lifts the shadow under this object back to plain lighting", (_, _) => FootprintSelected(true));
        B("Duplicate", "", (_, _) => DuplicateSelected());
        B("Delete", "", (_, _) => DeleteSelected());
        decorPanel.Children.Add(buttons);
        decorPanel.IsEnabled = false;
    }

    // ---- opening, saving --------------------------------------------------------------------------------------------------------------------

    private void TryOpen(string name)
    {
        if (!ConfirmDiscard())
        {
            switching = true; islandBox.SelectedItem = currentName; switching = false;
            return;
        }
        currentName = name;
        try
        {
            var path = System.IO.Path.Combine(gameDirectory, name);
            island = IslandFile.Load(path);
            history = new IslandHistory(island);
            history.Changed += () => { RedrawAll(); UpdateButtons(); };
            palette = IslandMapRenderer.LoadPalette(gameDirectory, System.IO.Path.GetFileNameWithoutExtension(name));
            var (mapCells, _) = (Math.Max(island.PresentBounds().MaxX - island.PresentBounds().MinX + 1, island.PresentBounds().MaxZ - island.PresentBounds().MinZ + 1) * 64, 0);
            renderer = new IslandMapRenderer(island, palette, Math.Clamp(1800 / mapCells, 2, 8));
            bitmap = new WriteableBitmap(renderer.PixelWidth, renderer.PixelHeight, 96, 96, PixelFormats.Bgra32, null);
            mapImage.Source = bitmap; mapImage.Width = renderer.PixelWidth; mapImage.Height = renderer.PixelHeight;
            overlay.Width = world.Width = renderer.PixelWidth; overlay.Height = world.Height = renderer.PixelHeight;
            LoadBakeFromCube();
            atlasImage.Source = AtlasBitmap();
            selected = null; rampStart = null; picked = null; tile = null; atlasSelection.Visibility = Visibility.Collapsed;
            decorPanel.IsEnabled = false;
            RedrawAll(); FitView(); UpdateButtons();
            var (lo, hi) = IslandOps.HeightRange(island);
            SetStatus($"{name}: {island.Cubes.Count} cubes, height {lo}..{hi}, {island.Cubes.Values.Sum(c => c.Decors.Count)} objects. Left button edits, right/middle drag pans, wheel zooms.");
            Title = $"LBA2: island terrain editor - {name}  [{gameDirectory}]";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't open {name}: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private BitmapSource AtlasBitmap()
    {
        var sixBit = palette.Take(768).Max() <= 63;
        var pixels = new byte[256 * 256 * 4];
        for (var i = 0; i < 256 * 256; i++)
        {
            var p = island!.GroundTexture[i] * 3;
            pixels[i * 4] = (byte)Math.Min(255, palette[p + 2] * (sixBit ? 4 : 1));
            pixels[i * 4 + 1] = (byte)Math.Min(255, palette[p + 1] * (sixBit ? 4 : 1));
            pixels[i * 4 + 2] = (byte)Math.Min(255, palette[p] * (sixBit ? 4 : 1));
            pixels[i * 4 + 3] = 255;
        }
        var bmp = BitmapSource.Create(256, 256, 96, 96, PixelFormats.Bgra32, null, pixels, 256 * 4);
        bmp.Freeze();
        return bmp;
    }

    private bool ConfirmDiscard()
    {
        if (history is not { Dirty: true }) return true;
        var answer = MessageBox.Show(this, "Save the changes to this island first?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return Save();
        return true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    private bool Save()
    {
        if (island is null || history is null) return true;
        var problems = IslandValidator.Validate(island).Where(p => p.IsError).ToList();
        if (problems.Count > 0 && MessageBox.Show(this, "The island has problems the game may not like:\n\n" + string.Join("\n", problems.Take(8).Select(p => "• " + p.Message)) + "\n\nSave anyway?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        try
        {
            island.Save();
            history.MarkSaved();
            UpdateButtons();
            SetStatus($"Saved {System.IO.Path.GetFileName(island.Path)} (the original is kept as .bak).");
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't save: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Play()
    {
        if (island is null) return;
        if (history is { Dirty: true } && MessageBox.Show(this, "The game plays what is saved on disk. Save the island first?", Title, MessageBoxButton.YesNo) == MessageBoxResult.Yes && !Save()) return;
        var scenes = Lba2SceneList.Load(gameDirectory);
        var dialog = new Lba2PlayWindow(scenes, Lba2Play.LastOptions?.Scene ?? 0) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Options is not { } options) return;
        if (Lba2Play.Launch(gameDirectory, options, out var problem) is null) MessageBox.Show(this, problem ?? "The game didn't start.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else SetStatus($"Started LBA2 in scene {options.Scene}.");
    }

    private void Check()
    {
        if (island is null) return;
        var problems = IslandValidator.Validate(island);
        MessageBox.Show(this, problems.Count == 0 ? "No problems found." : string.Join("\n", problems.Take(30).Select(p => (p.IsError ? "ERROR  " : "note   ") + p.Message)), "Island check", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Weld()
    {
        if (island is null || history is null) return;
        history.Begin();
        var fixedCount = IslandOps.WeldBorders(island);
        history.Commit("weld borders");
        RedrawAll();
        SetStatus($"Welded {fixedCount} vertices on cube borders.");
    }

    private void UpdateButtons()
    {
        undoButton.IsEnabled = history?.CanUndo == true; redoButton.IsEnabled = history?.CanRedo == true;
        undoButton.Content = history?.UndoLabel is { } u ? $"Undo {u}" : "Undo";
        redoButton.Content = history?.RedoLabel is { } r ? $"Redo {r}" : "Redo";
        saveButton.Content = history?.Dirty == true ? "Save *" : "Save";
    }

    private void DoUndo() { if (history?.Undo() == true) { ReselectAfterHistory(); } }
    private void DoRedo() { if (history?.Redo() == true) { ReselectAfterHistory(); } }
    private void ReselectAfterHistory() { selected = null; decorPanel.IsEnabled = false; RedrawAll(); UpdateButtons(); }

    // ---- drawing --------------------------------------------------------------------------------------------------------------------------

    private void SetStatus(string text) => status.Text = text;

    private void RedrawAll()
    {
        if (renderer is null || bitmap is null || island is null) return;
        var (lo, hi) = IslandOps.HeightRange(island);
        renderer.HeightMin = lo; renderer.HeightMax = Math.Max(lo + 1, hi);
        renderer.RenderAll(view);
        bitmap.WritePixels(new Int32Rect(0, 0, renderer.PixelWidth, renderer.PixelHeight), renderer.Pixels, renderer.PixelWidth * 4, 0);
        RebuildOverlay();
        UpdateButtons();
        DrawProfile();
    }

    // Redraws the cells around a brush stamp.
    private void RedrawAround(double gx, double gz, double reach)
    {
        if (renderer is null || bitmap is null) return;
        var x0 = (int)Math.Floor(gx - reach) - 2; var x1 = (int)Math.Ceiling(gx + reach) + 1;
        var z0 = (int)Math.Floor(gz - reach) - 2; var z1 = (int)Math.Ceiling(gz + reach) + 1;
        x0 = Math.Max(x0, renderer.OriginX); z0 = Math.Max(z0, renderer.OriginZ);
        x1 = Math.Min(x1, renderer.OriginX + renderer.CellsX - 1); z1 = Math.Min(z1, renderer.OriginZ + renderer.CellsZ - 1);
        if (x1 < x0 || z1 < z0) return;
        renderer.Render(view, x0, z0, x1, z1);
        var rect = new Int32Rect((x0 - renderer.OriginX) * renderer.Scale, (z0 - renderer.OriginZ) * renderer.Scale, (x1 - x0 + 1) * renderer.Scale, (z1 - z0 + 1) * renderer.Scale);
        bitmap.WritePixels(rect, renderer.Pixels, renderer.PixelWidth * 4, rect.X, rect.Y);
    }

    private double CellToPixelX(double cellX) => (cellX - renderer!.OriginX) * renderer.Scale;
    private double CellToPixelZ(double cellZ) => (cellZ - renderer!.OriginZ) * renderer.Scale;

    private void RebuildOverlay()
    {
        overlay.Children.Clear();
        if (renderer is null || island is null) return;
        var thin = 1 / zoom;
        // cube borders
        var grid = new GeometryGroup();
        var (minX, minZ, maxX, maxZ) = island.PresentBounds();
        for (var cx = minX; cx <= maxX + 1; cx++) grid.Children.Add(new LineGeometry(new Point(CellToPixelX(cx * 64), 0), new Point(CellToPixelX(cx * 64), renderer.PixelHeight)));
        for (var cz = minZ; cz <= maxZ + 1; cz++) grid.Children.Add(new LineGeometry(new Point(0, CellToPixelZ(cz * 64)), new Point(renderer.PixelWidth, CellToPixelZ(cz * 64))));
        overlay.Children.Add(new System.Windows.Shapes.Path { Data = grid, Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), StrokeThickness = thin, IsHitTestVisible = false });

        // objects
        foreach (var (cx, cz, cube) in IslandOps.CubeCells(island))
            foreach (var d in cube.Decors)
            {
                double wx = (cx * IslandFile.CubeSize + d.X) / (double)IslandFile.CellSize, wz = (cz * IslandFile.CubeSize + d.Z) / (double)IslandFile.CellSize;
                var isSelected = selected is { } s && s.Decor == d;
                var box = new Rectangle
                {
                    Width = Math.Max(1, (d.XMax - d.XMin) / (double)IslandFile.CellSize * renderer.Scale), Height = Math.Max(1, (d.ZMax - d.ZMin) / (double)IslandFile.CellSize * renderer.Scale),
                    Stroke = isSelected ? Brushes.Yellow : new SolidColorBrush(Color.FromArgb(200, 255, 140, 60)), StrokeThickness = isSelected ? thin * 2 : thin, IsHitTestVisible = false,
                };
                Canvas.SetLeft(box, CellToPixelX((cx * IslandFile.CubeSize + d.XMin) / (double)IslandFile.CellSize)); Canvas.SetTop(box, CellToPixelZ((cz * IslandFile.CubeSize + d.ZMin) / (double)IslandFile.CellSize));
                overlay.Children.Add(box);
                var dot = new Ellipse { Width = 5 * thin, Height = 5 * thin, Fill = isSelected ? Brushes.Yellow : Brushes.OrangeRed, IsHitTestVisible = false };
                Canvas.SetLeft(dot, CellToPixelX(wx) - 2.5 * thin); Canvas.SetTop(dot, CellToPixelZ(wz) - 2.5 * thin);
                overlay.Children.Add(dot);
            }

        if (rampStart is { } r)
        {
            var mark = new Ellipse { Width = 8 * thin, Height = 8 * thin, Fill = Brushes.Cyan, IsHitTestVisible = false };
            Canvas.SetLeft(mark, CellToPixelX(r.Gx) - 4 * thin); Canvas.SetTop(mark, CellToPixelZ(r.Gz) - 4 * thin);
            overlay.Children.Add(mark);
        }
        brushCircle.StrokeThickness = thin;
    }

    private void ApplyTransform()
    {
        world.RenderTransform = new MatrixTransform(zoom, 0, 0, zoom, pan.X, pan.Y);
        RebuildOverlay();
    }

    private void FitView()
    {
        if (renderer is null || viewport.ActualWidth < 10) return;
        zoom = Math.Min(viewport.ActualWidth / renderer.PixelWidth, viewport.ActualHeight / renderer.PixelHeight) * 0.98;
        pan = new Point((viewport.ActualWidth - renderer.PixelWidth * zoom) / 2, (viewport.ActualHeight - renderer.PixelHeight * zoom) / 2);
        ApplyTransform();
    }

    private void ZoomAt(Point at, double factor)
    {
        var next = Math.Clamp(zoom * factor, 0.1, 40);
        factor = next / zoom;
        pan = new Point(at.X - (at.X - pan.X) * factor, at.Y - (at.Y - pan.Y) * factor);
        zoom = next;
        ApplyTransform();
    }

    private (double Gx, double Gz) ToCell(Point screen)
    {
        var px = (screen.X - pan.X) / zoom; var pz = (screen.Y - pan.Y) / zoom;
        return (px / renderer!.Scale + renderer.OriginX, pz / renderer.Scale + renderer.OriginZ);
    }

    private void DrawProfile()
    {
        profile.Children.Clear();
        if (island is null || renderer is null || profile.ActualWidth < 10) return;
        var gz = (int)Math.Round(pointer.Gz);
        var w = profile.ActualWidth; var h = profile.ActualHeight;
        var (lo, hi) = IslandOps.HeightRange(island);
        var span = Math.Max(1, hi - lo);
        var points = new PointCollection();
        for (var gx = renderer.OriginX; gx <= renderer.OriginX + renderer.CellsX; gx++)
        {
            if (island.HeightAt(gx, gz) is not { } height) continue;
            points.Add(new Point((gx - renderer.OriginX) / (double)renderer.CellsX * w, h - 6 - (height - lo) / span * (h - 12)));
        }
        if (points.Count > 1) profile.Children.Add(new Polyline { Points = points, Stroke = Brushes.LightGreen, StrokeThickness = 1.2 });
        if (double.TryParse(levelBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var level))
        {
            var y = h - 6 - (level - lo) / span * (h - 12);
            profile.Children.Add(new Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = Brushes.Orange, StrokeDashArray = new DoubleCollection { 4, 4 }, StrokeThickness = 1 });
        }
        var px = (pointer.Gx - renderer.OriginX) / renderer.CellsX * w;
        profile.Children.Add(new Line { X1 = px, X2 = px, Y1 = 0, Y2 = h, Stroke = Brushes.White, StrokeThickness = 1, Opacity = 0.6 });
        profile.Children.Add(new TextBlock { Text = $"height along row {gz}   ({lo}..{hi}, orange = Level)", Foreground = Muted, FontSize = 10, Margin = new Thickness(4, 1, 0, 0) });
    }

    // ---- tools ----------------------------------------------------------------------------------------------------------------------------

    private void SelectTool(Tool t)
    {
        tool = t; rampStart = null;
        if (renderer is not null) RebuildOverlay();
        brushCircle.Visibility = t is Tool.SelectDecor or Tool.AddDecor or Tool.ObjectShadow or Tool.ClearObjectShadow ? Visibility.Collapsed : brushCircle.Visibility;
        var tip = ToolGroups.SelectMany(g => g.Items).First(i => i.Tool == t).Tip;
        SetStatus(tip);
    }

    private static double Number(TextBox box, double fallback) => double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private BakeOptions BakeSettings() => new()
    {
        Azimuth = Number(azimuthBox, 0), Elevation = Math.Clamp(Number(elevationBox, 45), 1, 89), Gain = Number(gainBox, 11), Offset = Number(offsetBox, 1.2),
        ShadowLevel = (int)Math.Clamp(Number(shadowLevelBox, 3), 0, 15), TerrainShadows = terrainShadowBox.IsChecked == true, DecorShadows = decorShadowBox.IsChecked == true,
    };

    private void LoadBakeFromCube()
    {
        if (island is null || island.Cubes.Count == 0) return;
        var o = BakeOptions.For(island.Cubes.Values.First());
        azimuthBox.Text = o.Azimuth.ToString("F1", CultureInfo.InvariantCulture); elevationBox.Text = o.Elevation.ToString("F1", CultureInfo.InvariantCulture);
        gainBox.Text = o.Gain.ToString(CultureInfo.InvariantCulture); offsetBox.Text = o.Offset.ToString(CultureInfo.InvariantCulture);
        if (renderer is not null) { renderer.Baseline = BakeSettings(); if (view is MapView.Shadows or MapView.Height) RedrawAll(); }
    }

    private void Whole(string label, Func<int> action)
    {
        if (island is null || history is null) return;
        Cursor = Cursors.Wait;
        try
        {
            history.Begin();
            var n = action();
            history.Commit(label);
            SetStatus($"{label}: {n} vertices changed.");
        }
        finally { Cursor = null; }
        RedrawAll();
    }

    private void BakeWhole(bool shadowsOnly)
    {
        if (island is null) return;
        var options = BakeSettings();
        renderer!.Baseline = options;
        Whole(shadowsOnly ? "cast shadows" : "bake light", () => shadowsOnly ? IslandBake.CastShadows(island, new WholeIslandRegion(), options) : IslandBake.Bake(island, new WholeIslandRegion(), options));
    }

    private int ShadowDepth => (int)Math.Clamp(Number(shadowDepthBox, 5), 1, 15);

    private void FootprintsAll(bool remove)
    {
        if (island is null) return;
        var options = BakeSettings();
        Whole(remove ? "clear object shadows" : "shadows under objects", () => IslandBake.FootprintShadowAll(island, options, ShadowDepth, remove));
    }

    private void FootprintSelected(bool remove)
    {
        if (selected is not { } s || island is null || history is null) return;
        var cell = island.CellsOf(s.Cube.Id).FirstOrDefault();
        history.Begin();
        var n = IslandBake.FootprintShadow(island, cell.X, cell.Z, s.Decor, BakeSettings(), ShadowDepth, remove);
        history.Commit(remove ? "clear object shadow" : "object shadow");
        RedrawAll();
        SetStatus($"{(remove ? "Cleared" : "Added")} the shadow under the object: {n} vertices changed.");
    }

    private void RemoveAllShadows()
    {
        if (island is null) return;
        var options = BakeSettings();
        Whole("remove shadows", () => IslandBake.LiftShadows(island, new WholeIslandRegion(), options));
    }

    private BrushRegion Brush() => new(pointer.Gx, pointer.Gz, radius.Value, hardness.Value);

    private void ViewDown(object sender, MouseButtonEventArgs e)
    {
        if (island is null || history is null || renderer is null) return;
        viewport.Focus();
        pointer = ToCell(e.GetPosition(viewport));
        if (Keyboard.Modifiers == ModifierKeys.Alt) { SampleLevel(); return; }
        switch (tool)
        {
            case Tool.Eyedropper: Pick(); return;
            case Tool.Ramp: RampClick(); return;
            case Tool.SelectDecor: SelectAt(); return;
            case Tool.AddDecor: AddAt(); return;
            case Tool.ObjectShadow or Tool.ClearObjectShadow:
                selected = DecorAt(pointer.Gx, pointer.Gz);
                decorPanel.IsEnabled = selected is not null;
                LoadDecorFields();
                if (selected is null) SetStatus("No object under the pointer.");
                else FootprintSelected(tool == Tool.ClearObjectShadow);
                RebuildOverlay();
                return;
            case Tool.BlobShadow: RunOnce("blob shadow", () => IslandLightOps.BlobShadow(island, pointer.Gx, pointer.Gz, radius.Value, Math.Clamp(Number(lightBox, 6), 1, 15))); return;
        }
        stroking = true;
        history.Begin();
        strokePlane = null;
        follow = tool is Tool.Raise or Tool.Lower or Tool.Smooth or Tool.Flatten or Tool.LevelPlane or Tool.Terrace or Tool.Relief && followBox.IsChecked == true ? new IslandOps.DecorFollow(island) : null;
        if (tool == Tool.LevelPlane) strokePlane = IslandOps.FitPlane(island, Brush());
        viewport.CaptureMouse();
        StrokeTick();
        strokeTimer.Start();
    }

    private void ViewUp(object sender, MouseButtonEventArgs e)
    {
        if (draggingDecor) { draggingDecor = false; history?.Commit("move object"); viewport.ReleaseMouseCapture(); RebuildOverlay(); UpdateButtons(); return; }
        if (!stroking) return;
        stroking = false;
        strokeTimer.Stop();
        viewport.ReleaseMouseCapture();
        var label = ToolGroups.SelectMany(g => g.Items).First(i => i.Tool == tool).Name.ToLowerInvariant();
        if (follow is not null) follow.Apply();
        history!.Commit(label);
        follow = null;
        RebuildOverlay();
        UpdateButtons(); DrawProfile();
        if (view is MapView.Shadows or MapView.Height) RedrawAll();
    }

    private void ViewMove(object sender, MouseEventArgs e)
    {
        var screen = e.GetPosition(viewport);
        if (panning)
        {
            pan = new Point(panOrigin.X + screen.X - panStart.X, panOrigin.Y + screen.Y - panStart.Y);
            ApplyTransform();
            return;
        }
        if (renderer is null || island is null) return;
        pointer = ToCell(screen);
        var r = radius.Value * renderer.Scale;
        brushCircle.Visibility = tool is Tool.SelectDecor or Tool.AddDecor or Tool.ObjectShadow or Tool.ClearObjectShadow ? Visibility.Collapsed : Visibility.Visible;
        brushCircle.Width = brushCircle.Height = r * 2;
        Canvas.SetLeft(brushCircle, CellToPixelX(pointer.Gx) - r); Canvas.SetTop(brushCircle, CellToPixelZ(pointer.Gz) - r);
        if (draggingDecor && selected is { } s) MoveDecorTo(s);
        UpdateInfo();
        if (!stroking) DrawProfile();
    }

    private void StrokeTick()
    {
        if (island is null || renderer is null) return;
        var region = Brush();
        var s = strength.Value;
        var n = 0;
        switch (tool)
        {
            case Tool.Raise: n = IslandOps.Raise(island, region, s * 0.25); break;
            case Tool.Lower: n = IslandOps.Raise(island, region, -s * 0.25); break;
            case Tool.Smooth: n = IslandOps.Smooth(island, region, s / 100 * 0.6); break;
            case Tool.Flatten: n = IslandOps.FlattenTo(island, region, Number(levelBox, 1000), Math.Min(1, s / 100 + 0.2)); break;
            case Tool.LevelPlane: n = strokePlane is { } plane ? IslandOps.LevelToPlane(island, region, plane, Math.Min(1, s / 100 + 0.2), horizontalBox.IsChecked == true ? 1 : 0) : 0; break;
            case Tool.Terrace: n = IslandOps.Terrace(island, region, Math.Max(1, Number(stepBox, 400)), s / 100); break;
            case Tool.Relief: n = IslandOps.ScaleRelief(island, region, 1 + (Number(reliefBox, 0.8) - 1) * s / 100); break;
            case Tool.PaintLight: n = IslandLightOps.Paint(island, region, IslandLightOps.Mode.Set, Math.Clamp(Number(lightBox, 9), 0, 15)); break;
            case Tool.Darken: n = IslandLightOps.Paint(island, region, IslandLightOps.Mode.Darken, 1 + s / 34); break;
            case Tool.Lighten: n = IslandLightOps.Paint(island, region, IslandLightOps.Mode.Lighten, 1 + s / 34); break;
            case Tool.RemoveShadows: n = IslandBake.LiftShadows(island, region, BakeSettings(), 1); break;
            case Tool.CastShadows: n = IslandBake.CastShadows(island, region, BakeSettings()); break;
            case Tool.WaterDepth: n = IslandLightOps.PaintWaterDepth(island, region, (int)Math.Clamp(Number(lightBox, 3), 0, 15)); break;
            case Tool.PaintTexture:
                if (picked is null) { SetStatus("Pick a triangle first (Pick triangle), then paint it."); return; }
                n = IslandGround.Paint(island, region, picked, PolygonFields.Texture | (diagonalBox.IsChecked == true ? PolygonFields.Diagonal : PolygonFields.None)); break;
            case Tool.PaintTile:
                if (tile is not { } t) { SetStatus("Drag a square on the ground atlas (right panel) to choose the tile."); return; }
                n = IslandGround.PaintTile(island, region, t.X, t.Y, t.W, t.H); break;
            case Tool.PaintCode: n = IslandGround.PaintGameCode(island, region, codeBox.SelectedIndex); break;
            case Tool.FixDiagonals: n = IslandGround.OptimiseDiagonals(island, region); break;
        }
        if (follow is not null && n > 0) follow.Apply();
        if (n > 0) RedrawAround(pointer.Gx, pointer.Gz, radius.Value + 1);
        if (view is MapView.Shadows or MapView.Height && n > 0) RedrawAround(pointer.Gx, pointer.Gz, radius.Value + 3);
        if (follow is not null && n > 0) RebuildOverlay();
        UpdateInfo();
    }

    // A single action bracketed as one undo step.
    private void RunOnce(string label, Func<int> action)
    {
        if (island is null || history is null) return;
        history.Begin();
        var n = action();
        history.Commit(label);
        RedrawAround(pointer.Gx, pointer.Gz, radius.Value + 2);
        UpdateButtons();
        SetStatus($"{label}: {n} vertices changed.");
    }

    private void SampleLevel()
    {
        if (island is null) return;
        var h = IslandOps.Altitude(island, pointer.Gx * 512, pointer.Gz * 512);
        if (h is null) return;
        levelBox.Text = Math.Round(h.Value).ToString(CultureInfo.InvariantCulture);
        SetStatus($"Level set to {levelBox.Text} from the ground under the pointer.");
        DrawProfile();
    }

    private void Pick()
    {
        if (island is null) return;
        var gx = (int)Math.Floor(pointer.Gx); var gz = (int)Math.Floor(pointer.Gz);
        var u = pointer.Gx - gx; var v = pointer.Gz - gz;
        var cube = island.CubeAt(gx / 64, gz / 64);
        var diagonal = cube is { HasPolygons: true } && new IslandPolygon(cube.Polygon(gx % 64, gz % 64, 0)).Diagonal;
        var half = diagonal ? (u + v < 1 ? 0 : 1) : (u < v ? 0 : 1);
        picked = IslandGround.Pick(island, gx, gz, half);
        if (picked is null) { SetStatus("Nothing to pick there."); return; }
        SetStatus($"Picked: texture {picked.Polygon.TextureIndex}, flags tex {picked.Polygon.TexFlag} poly {picked.Polygon.PolyFlag}, game code {picked.Polygon.CodeJeu} ({IslandPolygon.CodeJeuNames[picked.Polygon.CodeJeu]}), diagonal {(picked.Polygon.Diagonal ? "1-3" : "0-2")}. Choose Paint picked to use it.");
        codeBox.SelectedIndex = picked.Polygon.CodeJeu;
        if (picked.Texture is { } t)
        {
            var xs = new[] { t[0], t[2], t[4] }.Select(x => x / 256.0); var ys = new[] { t[1], t[3], t[5] }.Select(y => y / 256.0);
            Canvas.SetLeft(atlasSelection, xs.Min()); Canvas.SetTop(atlasSelection, ys.Min());
            atlasSelection.Width = Math.Max(1, xs.Max() - xs.Min()); atlasSelection.Height = Math.Max(1, ys.Max() - ys.Min());
            atlasSelection.Stroke = Brushes.Cyan; atlasSelection.Visibility = Visibility.Visible;
        }
    }

    private void RampClick()
    {
        if (island is null || history is null) return;
        var h = IslandOps.Altitude(island, pointer.Gx * 512, pointer.Gz * 512);
        if (h is null) return;
        if (rampStart is null) { rampStart = (pointer.Gx, pointer.Gz, h.Value); RebuildOverlay(); SetStatus($"Ramp starts at height {h.Value:F0}. Click where it ends."); return; }
        var from = rampStart.Value;
        rampStart = null;
        RunOnce("ramp", () =>
        {
            var f = followBox.IsChecked == true ? new IslandOps.DecorFollow(island) : null;
            var n = IslandOps.Ramp(island, from, (pointer.Gx, pointer.Gz, h.Value), Math.Max(1, radius.Value * hardness.Value + 0.5), Math.Max(1, radius.Value * (1 - hardness.Value)));
            f?.Apply();
            return n;
        });
        RedrawAll();
    }

    // ---- objects ----------------------------------------------------------------------------------------------------------------------------

    private (IslandCube Cube, IslandDecor Decor)? DecorAt(double gx, double gz)
    {
        if (island is null) return null;
        (IslandCube, IslandDecor)? best = null; var bestDistance = 1e9;
        foreach (var (cx, cz, cube) in IslandOps.CubeCells(island))
            foreach (var d in cube.Decors)
            {
                double wx = (cx * IslandFile.CubeSize + d.X) / (double)IslandFile.CellSize, wz = (cz * IslandFile.CubeSize + d.Z) / (double)IslandFile.CellSize;
                var distance = Math.Sqrt((wx - gx) * (wx - gx) + (wz - gz) * (wz - gz));
                if (distance < bestDistance) { bestDistance = distance; best = (cube, d); }
            }
        return bestDistance <= Math.Max(3, 12 / (zoom * renderer!.Scale)) ? best : null;
    }

    private void SelectAt()
    {
        if (island is null || history is null) return;
        selected = DecorAt(pointer.Gx, pointer.Gz);
        decorPanel.IsEnabled = selected is not null;
        LoadDecorFields();
        RebuildOverlay();
        if (selected is { } s)
        {
            history.Begin();
            draggingDecor = true;
            viewport.CaptureMouse();
        }
    }

    private void MoveDecorTo((IslandCube Cube, IslandDecor Decor) s)
    {
        if (island is null) return;
        var wx = pointer.Gx * 512; var wz = pointer.Gz * 512;
        var y = (int)Math.Round(IslandOps.Altitude(island, wx, wz) ?? s.Decor.Y);
        if (IslandDecors.Move(island, s.Cube, s.Decor, wx, wz, followBox.IsChecked == true ? y : null))
        {
            // the object may have changed cube
            var cube = island.Cubes.Values.First(c => c.Decors.Contains(s.Decor));
            selected = (cube, s.Decor);
            RebuildOverlay(); LoadDecorFields();
        }
    }

    private void AddAt()
    {
        if (island is null || history is null) return;
        var body = (int)Number(bodyBox, 0);
        history.Begin();
        var like = selected is { } s && (s.Decor.Body & 0xFFFF) == body ? s.Decor : island.Cubes.Values.SelectMany(c => c.Decors).FirstOrDefault(d => (d.Body & 0xFFFF) == body);
        var added = IslandDecors.Add(island, body, pointer.Gx * 512, pointer.Gz * 512, null, like);
        if (added is null) { history.Cancel(); SetStatus("Can't add an object there (off the island, or the cube already holds 200)."); return; }
        history.Commit("add object");
        selected = added; decorPanel.IsEnabled = true;
        LoadDecorFields(); RebuildOverlay(); UpdateButtons();
        SetStatus(like is null ? "Object added with a default 1x1 cell bounding box; adjust it in the game or copy an existing object of the same body." : "Object added (its bounding box copied from another object of the same body).");
    }

    private void LoadDecorFields()
    {
        loadingFields = true;
        if (selected is { } s)
        {
            var d = s.Decor;
            var values = new[] { (d.Body & 0xFFFF).ToString(), d.X.ToString(), d.Y.ToString(), d.Z.ToString(), Math.Round((d.Beta & 0xFFFF) * 360.0 / 4096).ToString(CultureInfo.InvariantCulture), d.CodeJeu.ToString(), (d.Beta >> 16).ToString(), $"{s.Cube.Id} ({string.Join(", ", island!.CellsOf(s.Cube.Id).Select(c => $"{c.X},{c.Z}"))})" };
            for (var i = 0; i < values.Length; i++) decorFields[i].Text = values[i];
        }
        else foreach (var f in decorFields) f.Text = "";
        loadingFields = false;
    }

    private void ApplyDecorFields()
    {
        if (selected is not { } s || island is null || history is null) return;
        history.Begin();
        var d = s.Decor;
        d.Body = (d.Body & ~0xFFFF) | ((int)Number(decorFields[0], d.Body & 0xFFFF) & 0xFFFF);
        d.MoveTo((int)Number(decorFields[1], d.X), (int)Number(decorFields[2], d.Y), (int)Number(decorFields[3], d.Z));
        var angle = ((int)Math.Round(Number(decorFields[4], 0) * 4096 / 360) % 4096 + 4096) % 4096;
        d.Beta = (d.Beta & ~0xFFFF) | angle;
        d.CodeJeu = (int)Number(decorFields[5], d.CodeJeu);
        d.Beta = (d.Beta & 0xFFFF) | ((int)Number(decorFields[6], d.Beta >> 16) << 16);
        history.Commit("edit object");
        RebuildOverlay(); UpdateButtons();
    }

    private void EditSelected(string label, Action<IslandCube, IslandDecor> action)
    {
        if (selected is not { } s || history is null) return;
        history.Begin(); action(s.Cube, s.Decor); history.Commit(label);
        LoadDecorFields(); RebuildOverlay(); UpdateButtons();
    }

    private void DuplicateSelected()
    {
        if (selected is not { } s || island is null || history is null) return;
        history.Begin();
        var copy = s.Decor.Clone();
        if (s.Cube.Decors.Count >= IslandDecors.MaxPerCube) { history.Cancel(); SetStatus("The cube already holds 200 objects."); return; }
        copy.MoveTo(Math.Min(32767, copy.X + 512), copy.Y, Math.Min(32767, copy.Z + 512));
        s.Cube.Decors.Add(copy);
        history.Commit("duplicate object");
        selected = (s.Cube, copy);
        LoadDecorFields(); RebuildOverlay(); UpdateButtons();
    }

    private void DeleteSelected()
    {
        if (selected is not { } s || history is null) return;
        history.Begin();
        IslandDecors.Remove(s.Cube, s.Decor);
        history.Commit("delete object");
        selected = null; decorPanel.IsEnabled = false; LoadDecorFields();
        RebuildOverlay(); UpdateButtons();
    }

    // ---- atlas ------------------------------------------------------------------------------------------------------------------------------

    private Point atlasStart;
    private void AtlasDown(object sender, MouseButtonEventArgs e)
    {
        atlasStart = e.GetPosition(atlasCanvas);
        atlasCanvas.CaptureMouse();
        UpdateAtlasSelection(atlasStart);
    }

    private void AtlasMove(object sender, MouseEventArgs e)
    {
        if (atlasCanvas.IsMouseCaptured) UpdateAtlasSelection(e.GetPosition(atlasCanvas));
    }

    private void UpdateAtlasSelection(Point now)
    {
        // a square from the press point towards the pointer
        var size = (int)Math.Clamp(Math.Max(Math.Abs(now.X - atlasStart.X), Math.Abs(now.Y - atlasStart.Y)), 4, 128);
        var x = (int)Math.Clamp(now.X < atlasStart.X ? atlasStart.X - size : atlasStart.X, 0, 256 - size);
        var y = (int)Math.Clamp(now.Y < atlasStart.Y ? atlasStart.Y - size : atlasStart.Y, 0, 256 - size);
        tile = (x, y, size, size);
        Canvas.SetLeft(atlasSelection, x); Canvas.SetTop(atlasSelection, y);
        atlasSelection.Width = size; atlasSelection.Height = size;
        atlasSelection.Stroke = Brushes.Yellow; atlasSelection.Visibility = Visibility.Visible;
        SetStatus($"Atlas tile {size}x{size} at ({x}, {y}). Choose Paint atlas tile and paint.");
        toolButtons[Tool.PaintTile].IsChecked = true;
    }

    // ---- keyboard, info ---------------------------------------------------------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.Z) { DoUndo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { DoRedo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { Save(); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.OemOpenBrackets) { radius.Value = Math.Max(1, radius.Value - 1); e.Handled = true; }
        else if (e.Key == Key.OemCloseBrackets) { radius.Value = Math.Min(60, radius.Value + 1); e.Handled = true; }
    }

    private void UpdateInfo()
    {
        if (island is null) { info.Text = ""; return; }
        var gx = (int)Math.Round(pointer.Gx); var gz = (int)Math.Round(pointer.Gz);
        var cx = Math.Clamp((int)Math.Floor(pointer.Gx) / 64, 0, 15); var cz = Math.Clamp((int)Math.Floor(pointer.Gz) / 64, 0, 15);
        var cube = island.CubeAt(cx, cz);
        if (cube is null || island.HeightAt(gx, gz) is not { } h) { info.Text = $"vertex {gx}, {gz}\noff the island"; return; }
        var ground = IslandOps.Altitude(island, pointer.Gx * 512, pointer.Gz * 512);
        var cell = IslandGround.Pick(island, Math.Min((int)Math.Floor(pointer.Gx), IslandFile.GridSize - 1), Math.Min((int)Math.Floor(pointer.Gz), IslandFile.GridSize - 1), 0);
        var light = island.LightAt(gx, gz);
        info.Text = $"vertex {gx}, {gz}   cube {cube.Id} ({cx},{cz})\nworld X {gx * 512}  Z {gz * 512}\nheight {h}  (ground {ground:F0})\nlight {light}  water depth {IslandLightOps.WaterDepthAt(island, gx, gz)}\n"
            + (cell is null ? "" : $"code {cell.Polygon.CodeJeu} {IslandPolygon.CodeJeuNames[cell.Polygon.CodeJeu]}  tex {cell.Polygon.TextureIndex}  diag {(cell.Polygon.Diagonal ? "1-3" : "0-2")}");
    }
}
