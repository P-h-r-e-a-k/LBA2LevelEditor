using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LBA2LevelEditor.Lba1;
using LBA2LevelEditor.Lba1.Runtime;
using LBA2LevelEditor.Scenes;

namespace LBA2LevelEditor;

// Edits one LBA1 scene as data (SceneDocument): its actors, zones and track points are drawn on the scene's map and can
// be picked, dragged, added and deleted, their numbers edited, and every step undone. Nothing reaches the game files
// until Save (which validates first); Play scene runs what is saved. Scripts and the full attribute list open in the main
// window's existing editors through `editInMain`.
public partial class Lba1SceneEditorWindow : Window
{
    private enum Kind { None, Actor, Zone, Point }

    private const int SnapUnit = 128;

    private readonly string directory;
    private readonly Lba1Game game;
    private readonly Lba1ActorImages images;
    private readonly SceneStore store;
    private readonly Action<int, int, bool>? editInMain;

    private SceneDocument doc = null!;
    private int sceneNumber = -1;
    private Lba1SceneImage? image;
    private Lba1Cube? cube;
    private Kind selKind = Kind.None;
    private int selIndex = -1;
    private bool loading;
    private bool saving;
    private Action<int, int, int>? pending;
    private string? pendingHint;

    private sealed record SceneItem(int Scene, string Label)
    {
        public override string ToString() => Label;
    }

    internal Lba1SceneEditorWindow(string directory, int scene, Action<int, int, bool>? editInMain)
    {
        InitializeComponent();
        this.directory = directory;
        this.editInMain = editInMain;
        store = new SceneStore(SceneGame.Lba1, directory);
        game = new Lba1Game(directory);
        images = new Lba1ActorImages(game);

        loading = true;
        for (var s = 0; s < store.SceneCount; s++)
            SceneCombo.Items.Add(new SceneItem(s, $"{s}: {game.Description(s) ?? Lba1Game.IslandNames.ElementAtOrDefault(game.LoadScene(s).Island) ?? "scene"}"));
        loading = false;

        SceneHistory.Changed += OnStoreChanged;
        Loaded += (_, _) => OpenScene(scene);
    }

    // ---- opening, saving ----

    private void OpenScene(int scene)
    {
        if (doc is not null && doc.IsDirty && !ConfirmLeave()) { SelectSceneItem(sceneNumber); return; }
        if (doc is not null) doc.Changed -= OnDocChanged;
        try
        {
            doc = SceneDocument.Open(store, scene, withGrid: true);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't read scene {scene}: {error.Message}", "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        sceneNumber = scene;
        doc.Changed += OnDocChanged;
        selKind = Kind.None; selIndex = -1; pending = null;
        SelectSceneItem(scene);
        RebuildMap();
        RefreshAll();
        BuildInspector();
        BuildHeader();
        SetStatus($"Scene {scene} opened.");
        if (image is not null) CenterOn(image.Project(doc.Scene.Hero.X, doc.Scene.Hero.Y, doc.Scene.Hero.Z));
    }

    private void SelectSceneItem(int scene)
    {
        loading = true;
        SceneCombo.SelectedItem = SceneCombo.Items.OfType<SceneItem>().FirstOrDefault(i => i.Scene == scene);
        loading = false;
    }

    private bool ConfirmLeave()
    {
        var answer = MessageBox.Show(this, $"Scene {sceneNumber} has changes that aren't saved. Save them?", "Scene editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        return answer != MessageBoxResult.Yes || TrySave();
    }

    private bool TrySave()
    {
        saving = true;
        try
        {
            var result = doc.Save();
            var warnings = result.Issues.Where(i => i.Severity == SceneIssueSeverity.Warning).ToList();
            SetStatus($"Saved scene {sceneNumber} ({result.RecordBytes} bytes)." + (warnings.Count > 0 ? $"  {warnings.Count} warning(s): {warnings[0]}" : ""));
            return true;
        }
        catch (SceneValidationException error)
        {
            MessageBox.Show(this, "Not saved. The engine would misread this scene:\n\n" + string.Join("\n", error.Issues.Where(i => i.Severity == SceneIssueSeverity.Error).Take(10)),
                "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Not saved: {error.Message}", "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            saving = false;
        }
    }

    // Another window (the script editor, the attributes dialog) saved into the same game files.
    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (saving) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (doc is null) return;
            if (doc.IsDirty) { SetStatus("The game files changed in another window. Saving here would overwrite that: Revert reloads the scene."); return; }
            try
            {
                doc.Revert();
                RebuildMap();
                RefreshAll();
                BuildInspector();
                BuildHeader();
                SetStatus("Reloaded: the scene was changed in another window.");
            }
            catch (Exception error) when (error is IOException or InvalidDataException) { SetStatus($"Couldn't reload: {error.Message}"); }
        });
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (doc is not null && doc.IsDirty && !ConfirmLeave()) e.Cancel = true;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        SceneHistory.Changed -= OnStoreChanged;
        if (doc is not null) doc.Changed -= OnDocChanged;
    }

    private void SetStatus(string text) => StatusText.Text = pendingHint ?? text;

    // ---- refreshing ----

    private void RebuildMap()
    {
        try
        {
            drawnGrid = doc.Grid;
            image = game.RenderGrid(sceneNumber, doc.Grid!);
            cube = new Lba1Cube(doc.Grid!, store.LoadLibrary(sceneNumber));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            image = null; cube = null;
            SetStatus($"Couldn't draw scene {sceneNumber}: {error.Message}");
            return;
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze();
        MapImage.Source = bitmap;
        MapImage.Width = image.Width; MapImage.Height = image.Height;
        OverlayCanvas.Width = image.Width; OverlayCanvas.Height = image.Height;
    }

    private byte[]? drawnGrid;

    private void OnDocChanged(object? sender, EventArgs e)
    {
        // the grid changed (a blank scene, an undo of one): redraw the map
        if (!ReferenceEquals(doc.Grid, drawnGrid)) RebuildMap();
        RefreshAll();
        RefreshInspectorValues();
    }

    private void RefreshAll()
    {
        // selection may point past the end after an undo
        var scene = doc.Scene;
        if (selKind == Kind.Actor && selIndex >= scene.Actors.Count) { selKind = Kind.None; selIndex = -1; BuildInspector(); }
        if (selKind == Kind.Zone && selIndex >= scene.Zones.Count) { selKind = Kind.None; selIndex = -1; BuildInspector(); }
        if (selKind == Kind.Point && selIndex >= scene.TrackPoints.Count) { selKind = Kind.None; selIndex = -1; BuildInspector(); }
        RefreshLists();
        DrawOverlay();
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        UndoButton.IsEnabled = doc.CanUndo;
        RedoButton.IsEnabled = doc.CanRedo;
        UndoButton.ToolTip = doc.UndoDescription is { } u ? $"Undo: {u}" : null;
        RedoButton.ToolTip = doc.RedoDescription is { } r ? $"Redo: {r}" : null;
        SaveButton.IsEnabled = doc.IsDirty;
        RevertButton.IsEnabled = doc.IsDirty;
        DuplicateButton.IsEnabled = DeleteButton.IsEnabled = selKind != Kind.None && !(selKind == Kind.Actor && selIndex == 0);
        Title = $"LBA1 - scene editor - scene {sceneNumber}{(doc.IsDirty ? " *" : "")}";
    }

    private string DescribeActor(int i, SceneActorModel a)
    {
        if (i == 0) return "Twinsen (start position)";
        if (a.IsSprite) return (a.Flags & 8) != 0 ? $"door, sprite {a.Sprite}" : $"sprite {a.Sprite}";
        var name = a.Entity >= 0 ? game.EntityName(a.Entity) : null;
        return name is not null ? $"{name} (entity {a.Entity})" : $"entity {a.Entity}";
    }

    private void RefreshLists()
    {
        loading = true;
        var scene = doc.Scene;
        ActorList.Items.Clear();
        for (var i = 0; i < scene.Actors.Count; i++) ActorList.Items.Add($"{i,3}: {DescribeActor(i, scene.Actors[i])}");
        ZoneList.Items.Clear();
        for (var i = 0; i < scene.Zones.Count; i++)
        {
            var z = scene.Zones[i];
            ZoneList.Items.Add($"{i,3}: {ZoneStyle.NameOf(z.Type)}{(z.Type == 0 ? $" -> scene {z.Info[0]}" : z.Type == 2 ? $" #{z.Info[0]}" : "")}");
        }
        PointList.Items.Clear();
        for (var i = 0; i < scene.TrackPoints.Count; i++)
        {
            var p = scene.TrackPoints[i];
            PointList.Items.Add($"{i,3}: ({p.X}, {p.Y}, {p.Z})");
        }
        ActorList.SelectedIndex = selKind == Kind.Actor ? selIndex : -1;
        ZoneList.SelectedIndex = selKind == Kind.Zone ? selIndex : -1;
        PointList.SelectedIndex = selKind == Kind.Point ? selIndex : -1;
        loading = false;
    }

    // ---- drawing ----

    private static Color ColourOf(int i) => Color.FromRgb((byte)(80 + i * 67 % 170), (byte)(120 + i * 131 % 130), (byte)(90 + i * 29 % 160));

    private void DrawOverlay()
    {
        var canvas = OverlayCanvas;
        canvas.Children.Clear();
        if (image is null) return;
        var scene = doc.Scene;

        if (ZonesCheck.IsChecked == true)
        {
            for (var i = 0; i < scene.Zones.Count; i++)
            {
                var z = scene.Zones[i];
                var corners = new[] { (z.X0, z.Z0), (z.X1, z.Z0), (z.X1, z.Z1), (z.X0, z.Z1) };
                var colour = ZoneStyle.ColorOf(z.Type);
                var selected = selKind == Kind.Zone && selIndex == i;
                var polygon = new Polygon
                {
                    Points = new PointCollection(corners.Select(c => image.Project(c.Item1, Math.Max(z.Y0, z.Y1), c.Item2))),
                    Stroke = selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(210, colour.R, colour.G, colour.B)),
                    StrokeThickness = selected ? 2.5 : 1,
                    Fill = new SolidColorBrush(Color.FromArgb(selected ? (byte)90 : (byte)28, colour.R, colour.G, colour.B)),
                    Cursor = Cursors.Hand,
                    Tag = (Kind.Zone, i),
                };
                polygon.MouseLeftButtonDown += Item_MouseLeftButtonDown;
                canvas.Children.Add(polygon);
                if (selected || z.Type != 1)
                    AddLabel(canvas, i.ToString(), image.Project((z.X0 + z.X1) / 2.0, Math.Max(z.Y0, z.Y1), (z.Z0 + z.Z1) / 2.0), new SolidColorBrush(colour));
            }
        }

        if (PointsCheck.IsChecked == true)
        {
            for (var i = 0; i < scene.TrackPoints.Count; i++)
            {
                var p = scene.TrackPoints[i];
                var at = image.Project(p.X, p.Y, p.Z);
                var selected = selKind == Kind.Point && selIndex == i;
                var diamond = new Polygon
                {
                    Points = new PointCollection { new(at.X, at.Y - 7), new(at.X + 7, at.Y), new(at.X, at.Y + 7), new(at.X - 7, at.Y) },
                    Fill = selected ? Brushes.White : Brushes.Gold, Stroke = Brushes.Black, StrokeThickness = 1, Cursor = Cursors.Hand,
                    Tag = (Kind.Point, i),
                };
                diamond.MouseLeftButtonDown += Item_MouseLeftButtonDown;
                canvas.Children.Add(diamond);
                AddLabel(canvas, i.ToString(), new Point(at.X + 8, at.Y - 16), Brushes.Gold);
            }
        }

        if (ActorsCheck.IsChecked == true)
        {
            foreach (var i in Enumerable.Range(0, scene.Actors.Count).OrderBy(i => scene.Actors[i].X + scene.Actors[i].Z))
            {
                var a = scene.Actors[i];
                var feet = image.Project(a.X, a.Y, a.Z);
                var selected = selKind == Kind.Actor && selIndex == i;
                int? bodyIndex = i == 0 ? game.BodyIndex(0, 0) : !a.IsSprite && a.Entity >= 0 ? game.BodyIndex(a.Entity, a.Body) : null;
                var marker = bodyIndex is { } b ? images.GetMarker(b) : null;
                double height = 28;
                if (marker is not null)
                {
                    height = Math.Clamp(marker.HeightUnits * 15 / 256, 14, 260);
                    var size = Math.Max(height / 0.8, 8);
                    var img = new Image { Source = marker.Image, Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
                    Canvas.SetLeft(img, feet.X - size / 2);
                    Canvas.SetTop(img, feet.Y - size * 0.9);
                    canvas.Children.Add(img);
                }
                else if (a.IsSprite)
                {
                    var door = (a.Flags & 8) != 0;
                    var box = new Rectangle
                    {
                        Width = door ? 26 : 18, Height = door ? 50 : 22,
                        Fill = new SolidColorBrush(door ? Color.FromArgb(150, 0xC8, 0x8A, 0x3C) : Color.FromArgb(130, 0x9E, 0xC7, 0x5F)),
                        Stroke = door ? Brushes.SandyBrown : Brushes.YellowGreen, StrokeThickness = 1, IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(box, feet.X - box.Width / 2); Canvas.SetTop(box, feet.Y - box.Height);
                    height = box.Height;
                    canvas.Children.Add(box);
                }
                else
                {
                    var dot = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(ColourOf(i)), IsHitTestVisible = false };
                    Canvas.SetLeft(dot, feet.X - 7); Canvas.SetTop(dot, feet.Y - 14);
                    canvas.Children.Add(dot);
                }

                // the click target: an ellipse around the body, carrying the actor's number
                var hitH = Math.Max(height, 24);
                var hit = new Ellipse
                {
                    Width = Math.Max(hitH * 0.6, 24), Height = hitH, Fill = Brushes.Transparent, Cursor = Cursors.Hand, Tag = (Kind.Actor, i),
                    Stroke = selected ? Brushes.Gold : null, StrokeThickness = selected ? 2 : 0,
                };
                hit.MouseLeftButtonDown += Item_MouseLeftButtonDown;
                Canvas.SetLeft(hit, feet.X - hit.Width / 2); Canvas.SetTop(hit, feet.Y - hitH);
                canvas.Children.Add(hit);
                AddLabel(canvas, i == 0 ? "start" : i.ToString(), new Point(feet.X + 6, feet.Y - hitH - 12), selected ? Brushes.White : new SolidColorBrush(ColourOf(i)));
            }
        }
    }

    private static void AddLabel(Canvas canvas, string text, Point at, Brush brush)
    {
        var label = new TextBlock { Text = text, Foreground = brush, FontSize = 10, FontFamily = new FontFamily("Consolas"), IsHitTestVisible = false };
        Canvas.SetLeft(label, at.X); Canvas.SetTop(label, at.Y);
        canvas.Children.Add(label);
    }

    // ---- picking a place on the map ----

    // The world position under a map point: the highest floor (a solid cell with an empty one above it) that the point
    // can be on, at the given snap. Falls back to the given layer when the column has no floor there.
    private (int X, int Y, int Z) PickWorld(Point p, int fallbackY)
    {
        if (image is null) return (0, 0, 0);
        (int X, int Z) At(int layer)
        {
            var u = (p.X - image.OriginX) * 512.0 / 24;
            var v = (p.Y - image.OriginY + layer * 15) * 512.0 / 12;
            return ((int)Math.Round((u + v) / 2), (int)Math.Round((v - u) / 2));
        }
        if (cube is not null)
        {
            for (var layer = Lba1Cube.SizeY - 1; layer >= 0; layer--)
            {
                var (x, z) = At(layer);
                int cx = (x + 256) >> 9, cz = (z + 256) >> 9;
                if (cx < 0 || cx >= 64 || cz < 0 || cz >= 64) continue;
                var below = layer == 0 || Solid(cx, layer - 1, cz);
                if (below && !Solid(cx, layer, cz)) return (Snap(x), layer * 256, Snap(z));
            }
        }
        var (fx, fz) = At(fallbackY / 256);
        return (Snap(fx), fallbackY, Snap(fz));
    }

    private bool Solid(int x, int y, int z)
    {
        var (block, second) = cube!.Cell(x, y, z);
        return cube.ShapeOf(block, second) != 0;
    }

    private static int Snap(int v) => (int)Math.Round(v / (double)SnapUnit) * SnapUnit;

    // ---- mouse ----

    private bool dragging;
    private (int X, int Z) dragOrigin;
    private SceneZoneModel? dragZoneStart;

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (pending is not null) return;
        if (((FrameworkElement)sender).Tag is not ValueTuple<Kind, int> tag) return;
        e.Handled = true;
        Select(tag.Item1, tag.Item2);
        if (e.ClickCount >= 2 && tag.Item1 == Kind.Actor && tag.Item2 > 0) { EditActorInMain(false); return; }
        dragging = true;
        var p = e.GetPosition(MapImage);
        var world = PickWorld(p, CurrentY());
        dragOrigin = (world.X, world.Z);
        dragZoneStart = selKind == Kind.Zone ? doc.Scene.Zones[selIndex].Clone() : null;
        MapHost.CaptureMouse();
    }

    private int CurrentY() => selKind switch
    {
        Kind.Actor when selIndex < doc.Scene.Actors.Count => doc.Scene.Actors[selIndex].Y,
        Kind.Point when selIndex < doc.Scene.TrackPoints.Count => doc.Scene.TrackPoints[selIndex].Y,
        Kind.Zone when selIndex < doc.Scene.Zones.Count => Math.Min(doc.Scene.Zones[selIndex].Y0, doc.Scene.Zones[selIndex].Y1),
        _ => 0,
    };

    private void Map_MouseMove(object sender, MouseEventArgs e)
    {
        if (image is null) return;
        var p = e.GetPosition(MapImage);
        if (!dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            if (pending is not null)
            {
                var w = PickWorld(p, 0);
                StatusText.Text = $"{pendingHint}   (x {w.X}, y {w.Y}, z {w.Z})";
            }
            return;
        }
        var world = PickWorld(p, CurrentY());
        var index = selIndex;
        switch (selKind)
        {
            case Kind.Actor:
                doc.Edit($"Move actor {index}", m => { var a = m.Actors[index]; a.X = world.X; a.Y = world.Y; a.Z = world.Z; }, $"drag:a{index}");
                break;
            case Kind.Point:
                doc.Edit($"Move track point {index}", m => m.TrackPoints[index] = new SceneTrackPoint(world.X, world.Y, world.Z), $"drag:p{index}");
                break;
            case Kind.Zone when dragZoneStart is { } start:
                int dx = world.X - dragOrigin.X, dz = world.Z - dragOrigin.Z;
                doc.Edit($"Move zone {index}", m =>
                {
                    var z = m.Zones[index];
                    z.X0 = start.X0 + dx; z.X1 = start.X1 + dx; z.Z0 = start.Z0 + dz; z.Z1 = start.Z1 + dz;
                }, $"drag:z{index}");
                break;
        }
    }

    private void Map_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        MapHost.ReleaseMouseCapture();
    }

    private void Map_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (image is null) return;
        if (pending is { } place)
        {
            var world = PickWorld(e.GetPosition(MapImage), 0);
            pending = null; pendingHint = null;
            place(world.X, world.Y, world.Z);
            return;
        }
        Select(Kind.None, -1);
    }

    // ---- scrolling ----

    private void CenterOn(Point at)
    {
        var scale = MapScale.ScaleX;
        // the scroll viewer only knows its size once it has been laid out
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            MapScroll.ScrollToHorizontalOffset(Math.Max(0, at.X * scale - MapScroll.ViewportWidth / 2));
            MapScroll.ScrollToVerticalOffset(Math.Max(0, at.Y * scale - MapScroll.ViewportHeight / 2));
        });
    }

    private void CenterOnSelection()
    {
        if (image is null) return;
        var scene = doc.Scene;
        switch (selKind)
        {
            case Kind.Actor when selIndex < scene.Actors.Count:
                var a = scene.Actors[selIndex];
                CenterOn(image.Project(a.X, a.Y, a.Z));
                break;
            case Kind.Zone when selIndex < scene.Zones.Count:
                var z = scene.Zones[selIndex];
                CenterOn(image.Project((z.X0 + z.X1) / 2.0, Math.Max(z.Y0, z.Y1), (z.Z0 + z.Z1) / 2.0));
                break;
            case Kind.Point when selIndex < scene.TrackPoints.Count:
                var p = scene.TrackPoints[selIndex];
                CenterOn(image.Project(p.X, p.Y, p.Z));
                break;
        }
    }

    // ---- selection ----

    private void Select(Kind kind, int index)
    {
        if (kind == selKind && index == selIndex) return;
        selKind = kind; selIndex = index;
        RefreshLists();
        DrawOverlay();
        UpdateToolbar();
        BuildInspector();
        switch (kind)
        {
            case Kind.Actor: ListTabs.SelectedIndex = 0; break;
            case Kind.Zone: ListTabs.SelectedIndex = 1; break;
            case Kind.Point: ListTabs.SelectedIndex = 2; break;
        }
    }

    private void ActorList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading && ActorList.SelectedIndex >= 0) { Select(Kind.Actor, ActorList.SelectedIndex); CenterOnSelection(); } }
    private void ZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading && ZoneList.SelectedIndex >= 0) { Select(Kind.Zone, ZoneList.SelectedIndex); CenterOnSelection(); } }
    private void PointList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading && PointList.SelectedIndex >= 0) { Select(Kind.Point, PointList.SelectedIndex); CenterOnSelection(); } }
    private void ListTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void ActorList_DoubleClick(object sender, MouseButtonEventArgs e) { if (selKind == Kind.Actor && selIndex > 0) EditActorInMain(false); }

    // ---- the inspector ----

    // The boxes of the inspector and of the Scene tab, with how to read what they show back from the document.
    private readonly List<(TextBox Box, Func<string> Get)> inspectorFields = new();
    private readonly List<(TextBox Box, Func<string> Get)> headerFields = new();
    private List<(TextBox Box, Func<string> Get)> liveFields = null!;

    private static Grid NewFieldGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private void AddRow(Grid grid, string label, Func<string> get, Action<string> commit, string? hint = null)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = grid.RowDefinitions.Count - 1;
        var caption = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0x6B, 0x8A)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var box = new TextBox { Text = get(), Margin = new Thickness(0, 1, 0, 1), ToolTip = hint };
        Grid.SetRow(caption, row); Grid.SetColumn(caption, 0);
        Grid.SetRow(box, row); Grid.SetColumn(box, 1);
        grid.Children.Add(caption); grid.Children.Add(box);
        void Commit()
        {
            if (box.Text == get()) return;
            try { commit(box.Text); }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentException or InvalidOperationException or IOException)
            {
                SetStatus($"{label}: {error.Message}");
                box.Text = get();
            }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); box.SelectAll(); e.Handled = true; } };
        liveFields.Add((box, get));
    }

    private void RefreshInspectorValues()
    {
        foreach (var (box, get) in inspectorFields.Concat(headerFields))
        {
            if (box.IsKeyboardFocused) continue;
            try { box.Text = get(); } catch (ArgumentOutOfRangeException) { }
        }
        InspectorTitle.Text = InspectorTitleText();
    }

    private string InspectorTitleText() => selKind switch
    {
        Kind.Actor when selIndex >= 0 && selIndex < doc.Scene.Actors.Count => selIndex == 0 ? "Twinsen (hero start)" : $"Actor {selIndex}: {DescribeActor(selIndex, doc.Scene.Actors[selIndex])}",
        Kind.Zone when selIndex >= 0 && selIndex < doc.Scene.Zones.Count => $"Zone {selIndex}: {ZoneStyle.NameOf(doc.Scene.Zones[selIndex].Type)}",
        Kind.Point when selIndex >= 0 && selIndex < doc.Scene.TrackPoints.Count => $"Track point {selIndex}",
        _ => "Nothing selected",
    };

    private static int Number(string text)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(text, CultureInfo.InvariantCulture);
    }

    private void BuildInspector()
    {
        inspectorFields.Clear();
        liveFields = inspectorFields;
        InspectorGrid.Children.Clear();
        InspectorGrid.RowDefinitions.Clear();
        InspectorGrid.ColumnDefinitions.Clear();
        InspectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        InspectorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        InspectorButtons.Children.Clear();
        InspectorTitle.Text = InspectorTitleText();
        InspectorNote.Text = "";
        var g = InspectorGrid;
        var scene = doc.Scene;
        var i = selIndex;

        switch (selKind)
        {
            case Kind.Actor when i >= 0 && i < scene.Actors.Count:
            {
                SceneActorModel A(SceneModel m) => m.Actors[i];
                void Num(string label, Func<SceneActorModel, int> get, Action<SceneActorModel, int> set, string? hint = null)
                    => AddRow(g, label, () => get(doc.Scene.Actors[i]).ToString(CultureInfo.InvariantCulture), text =>
                    {
                        var v = Number(text);
                        doc.Edit($"Set {label.ToLowerInvariant()} of actor {i}", m => set(A(m), v), $"a{i}:{label}");
                    }, hint);
                Num("X", a => a.X, (a, v) => a.X = v, "world position; 512 units = one cell");
                Num("Y", a => a.Y, (a, v) => a.Y = v, "256 units = one layer");
                Num("Z", a => a.Z, (a, v) => a.Z = v);
                if (i > 0)
                {
                    Num("Facing", a => a.Beta, (a, v) => a.Beta = v & 1023, "0..1023: 0 = +z, 256 = +x, 512 = -z, 768 = -x");
                    Num("Entity", a => a.Entity, (a, v) => a.Entity = v, "the FILE3D entity: which creature it is");
                    Num("Body", a => a.Body, (a, v) => a.Body = v, "the entity's body variant");
                    Num("Animation", a => a.Anim, (a, v) => a.Anim = v, "the entity's generic animation number");
                    Num("Sprite", a => a.Sprite, (a, v) => a.Sprite = v, "for sprite actors: the SPRITES.HQR picture");
                    AddRow(g, "Flags", () => "0x" + doc.Scene.Actors[i].Flags.ToString("X4"), text =>
                    {
                        var v = (uint)Number(text);
                        doc.Edit($"Set flags of actor {i}", m => A(m).Flags = v, $"a{i}:Flags");
                    }, "hex; 0x0400 = sprite, 0x0008 = sprite clip (door), 0x0004 = check zones ...");
                    Num("Move", a => a.Move, (a, v) => a.Move = v, "0 none, 1 manual, 2 follow, 3 track, 4 follow 2, 5 track+attack, 6 same xz, 7 random");
                    Num("Speed", a => a.SRot, (a, v) => a.SRot = v);
                    Num("Life", a => a.LifePoints, (a, v) => a.LifePoints = v);
                    Num("Armour", a => a.Armor, (a, v) => a.Armor = v);
                    Num("Hit force", a => a.HitForce, (a, v) => a.HitForce = v);
                    Num("Text colour", a => a.CoulObj, (a, v) => a.CoulObj = v, "palette family 0..15");
                    Num("Bonus option", a => a.OptionFlags, (a, v) => a.OptionFlags = v);
                    Num("Bonus count", a => a.NbBonus, (a, v) => a.NbBonus = v);
                    for (var k = 0; k < 4; k++)
                    {
                        var slot = k;
                        Num($"Info {k}", a => a.Info[slot], (a, v) => a.Info[slot] = v, "a door's screen clip rectangle; the target of a follow move");
                    }
                    var choose = new Button { Content = "Choose entity…" };
                    choose.Click += (_, _) => ChooseEntity();
                    var full = new Button { Content = "Full attributes…" };
                    full.Click += (_, _) => EditActorInMain(false);
                    var script = new Button { Content = "Edit script…" };
                    script.Click += (_, _) => EditActorInMain(true);
                    InspectorButtons.Children.Add(choose);
                    InspectorButtons.Children.Add(full);
                    InspectorButtons.Children.Add(script);
                    InspectorNote.Text = $"Life script {scene.Actors[i].Life.Length} bytes, track script {scene.Actors[i].Track.Length} bytes.";
                }
                else
                {
                    var script = new Button { Content = "Edit script…" };
                    script.Click += (_, _) => EditActorInMain(true);
                    InspectorButtons.Children.Add(script);
                    InspectorNote.Text = "Where Twinsen starts when the scene is entered without a cube-change zone.";
                }
                break;
            }

            case Kind.Zone when i >= 0 && i < scene.Zones.Count:
            {
                void Num(string label, Func<SceneZoneModel, int> get, Action<SceneZoneModel, int> set, string? hint = null)
                    => AddRow(g, label, () => get(doc.Scene.Zones[i]).ToString(CultureInfo.InvariantCulture), text =>
                    {
                        var v = Number(text);
                        doc.Edit($"Set {label.ToLowerInvariant()} of zone {i}", m => set(m.Zones[i], v), $"z{i}:{label}");
                    }, hint);
                var z = scene.Zones[i];
                AddRow(g, "Type", () => $"{doc.Scene.Zones[i].Type}", text =>
                {
                    var v = Number(text);
                    doc.Edit($"Set type of zone {i}", m => m.Zones[i].Type = v, $"z{i}:Type");
                    BuildInspector();
                }, string.Join(", ", ZoneStyle.TypeNames.Select((n, k) => $"{k} = {n}")));
                Num("X min", s => s.X0, (s, v) => s.X0 = v); Num("X max", s => s.X1, (s, v) => s.X1 = v);
                Num("Y min", s => s.Y0, (s, v) => s.Y0 = v); Num("Y max", s => s.Y1, (s, v) => s.Y1 = v);
                Num("Z min", s => s.Z0, (s, v) => s.Z0 = v); Num("Z max", s => s.Z1, (s, v) => s.Z1 = v);
                var fields = ZoneFields.For(new ZoneData { Game = 1, Scene = sceneNumber, Index = i, Type = z.Type, Info = new int[4] });
                for (var k = 0; k < 4; k++)
                {
                    var slot = k;
                    Num(fields[k].Label, s => s.Info[slot], (s, v) => s.Info[slot] = v, fields[k].Hint);
                }
                Num("Snap", s => s.Snap, (s, v) => s.Snap = v);
                if (z.Type == 0)
                {
                    var go = new Button { Content = "Open destination" };
                    go.Click += (_, _) => OpenScene(doc.Scene.Zones[i].Info[0]);
                    InspectorButtons.Children.Add(go);
                    InspectorNote.Text = "Twinsen arrives at the destination corner plus his offset inside this zone.";
                }
                break;
            }

            case Kind.Point when i >= 0 && i < scene.TrackPoints.Count:
            {
                void Num(string label, Func<SceneTrackPoint, int> get, Func<SceneTrackPoint, int, SceneTrackPoint> set)
                    => AddRow(g, label, () => get(doc.Scene.TrackPoints[i]).ToString(CultureInfo.InvariantCulture), text =>
                    {
                        var v = Number(text);
                        doc.Edit($"Set {label} of track point {i}", m => m.TrackPoints[i] = set(m.TrackPoints[i], v), $"p{i}:{label}");
                    });
                Num("X", p => p.X, (p, v) => p with { X = v });
                Num("Y", p => p.Y, (p, v) => p with { Y = v });
                Num("Z", p => p.Z, (p, v) => p with { Z = v });
                InspectorNote.Text = "Waypoints that actors' track scripts walk between (goto_point, pos_point ...).";
                break;
            }

            default:
                InspectorNote.Text = "Click an actor, a zone or a track point on the map to edit it; drag to move it. Use Add to place something new.";
                break;
        }
    }

    // The Scene tab: the header of the record.
    private void BuildHeader()
    {
        HeaderPanel.Children.Clear();
        headerFields.Clear();
        liveFields = headerFields;
        var g = NewFieldGrid();
        void Num(string label, Func<SceneModel, int> get, Action<SceneModel, int> set, string? hint = null)
            => AddRow(g, label, () => get(doc.Scene).ToString(CultureInfo.InvariantCulture), text =>
            {
                var v = Number(text);
                doc.Edit($"Set {label.ToLowerInvariant()} of scene {sceneNumber}", m => set(m, v), $"h:{label}");
            }, hint);
        Num("Island", m => m.Island, (m, v) => m.Island = v, string.Join(", ", Lba1Game.IslandNames.Select((n, k) => $"{k} = {n}")));
        Num("Game over scene", m => m.GameOverScene, (m, v) => m.GameOverScene = v, "the scene Twinsen wakes up in after dying without a clover leaf");
        Num("Light alpha", m => m.AlphaLight, (m, v) => m.AlphaLight = v);
        Num("Light beta", m => m.BetaLight, (m, v) => m.BetaLight = v);
        Num("Music", m => m.Music, (m, v) => m.Music = v, "the jingle number");
        Num("Ambient delay min", m => m.SecondMin, (m, v) => m.SecondMin = v);
        Num("Ambient delay range", m => m.SecondEcart, (m, v) => m.SecondEcart = v);
        for (var k = 0; k < 4; k++)
        {
            var n = k;
            Num($"Ambient {k} sample", m => m.Ambient[n].Sample, (m, v) => m.Ambient[n].Sample = v);
            Num($"Ambient {k} repeat", m => m.Ambient[n].Repeat, (m, v) => m.Ambient[n].Repeat = v);
            Num($"Ambient {k} round", m => m.Ambient[n].Round, (m, v) => m.Ambient[n].Round = v);
        }
        HeaderPanel.Children.Add(g);
        HeaderPanel.Children.Add(new TextBlock
        {
            Text = $"{doc.Scene.Actors.Count} actors, {doc.Scene.Zones.Count} zones, {doc.Scene.TrackPoints.Count} track points.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0x6B, 0x8A)), FontSize = 10.5, Margin = new Thickness(0, 10, 0, 0),
        });
    }

    // ---- toolbar ----

    private void SceneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || SceneCombo.SelectedItem is not SceneItem item || item.Scene == sceneNumber) return;
        OpenScene(item.Scene);
    }

    private void Save_Click(object sender, RoutedEventArgs e) { if (doc.IsDirty) TrySave(); }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (!doc.IsDirty) return;
        if (MessageBox.Show(this, "Throw away every change since the last save?", "Scene editor", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        doc.Revert();
        RebuildMap();
        RefreshAll();
        BuildInspector();
        BuildHeader();
        SetStatus("Changes discarded.");
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var what = doc.UndoDescription;
        if (doc.Undo()) { BuildInspector(); BuildHeader(); SetStatus($"Undid: {what}"); }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        var what = doc.RedoDescription;
        if (doc.Redo()) { BuildInspector(); BuildHeader(); SetStatus($"Redid: {what}"); }
    }

    private void View_Click(object sender, RoutedEventArgs e) => DrawOverlay();

    private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MapScale is not null) MapScale.ScaleX = MapScale.ScaleY = e.NewValue;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (doc.IsDirty)
        {
            var answer = MessageBox.Show(this, "Playing uses what is saved. Save scene " + sceneNumber + " first?", "Scene editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes && !TrySave()) return;
        }
        try
        {
            var played = new Lba1Game(directory);
            var playWindow = new Lba1PlayHostWindow(played, new Lba1ActorImages(played), directory, sceneNumber) { Owner = this };
            WindowLifecycle.Register(playWindow, "Lba1PlayHostWindow");
            playWindow.Show();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't start the scene: {error.Message}", "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- adding ----

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, Action click, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => click();
            return item;
        }
        menu.Items.Add(Item("New 3D actor…", () =>
        {
            var entity = PickEntity(0);
            if (entity is null) return;
            Place($"Click the map to place the new actor (entity {entity}); Esc cancels.", (x, y, z) => AddActor(SceneOps.BlankActor(SceneGame.Lba1, x, y, z, entity.Value), "Add actor"));
        }));
        menu.Items.Add(Item("Copy of the selected actor", () =>
        {
            var source = selIndex;
            Place($"Click the map to place the copy of actor {source}; Esc cancels.", (x, y, z) =>
            {
                var copy = doc.Scene.Actors[source].Clone();
                copy.X = x; copy.Y = y; copy.Z = z;
                AddActor(copy, $"Copy actor {source}");
            });
        }, selKind == Kind.Actor && selIndex > 0));
        menu.Items.Add(new Separator());
        foreach (var prefab in ActorPrefabs.For(SceneGame.Lba1))
        {
            var chosen = prefab;
            var item = Item(chosen.Name, () => Place($"Click the map to place: {chosen.Name}. {chosen.Description}", (x, y, z) =>
            {
                int index = -1;
                try
                {
                    doc.Edit($"Add {chosen.Name}", m => index = ActorPrefabs.Place(m, chosen, x, y, z));
                    Select(Kind.Actor, index);
                }
                catch (Exception error) when (error is InvalidOperationException or IOException) { MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
            }));
            item.ToolTip = chosen.Description;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var zones = new MenuItem { Header = "Zone" };
        for (var type = 0; type < ZoneStyle.TypeCount; type++)
        {
            var t = type;
            zones.Items.Add(Item(ZoneStyle.NameOf(t), () => Place($"Click the map to place the {ZoneStyle.NameOf(t)} zone; Esc cancels.", (x, y, z) => AddZone(t, x, y, z))));
        }
        menu.Items.Add(zones);
        menu.Items.Add(Item("Track point", () => Place("Click the map to place the track point; Esc cancels.", (x, y, z) =>
        {
            int index = -1;
            try
            {
                doc.Edit("Add track point", m => index = SceneOps.AddTrackPoint(m, new SceneTrackPoint(x, y, z)));
                Select(Kind.Point, index);
            }
            catch (SceneEditException error) { MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
        })));
        menu.IsOpen = true;
    }

    private void Place(string hint, Action<int, int, int> place)
    {
        pending = place;
        pendingHint = hint;
        StatusText.Text = hint;
    }

    private void CancelPlacement()
    {
        if (pending is null) return;
        pending = null; pendingHint = null;
        SetStatus("Cancelled.");
    }

    private void AddActor(SceneActorModel actor, string description)
    {
        int index = -1;
        try
        {
            doc.Edit(description, m => index = SceneOps.AddActor(m, actor));
            Select(Kind.Actor, index);
        }
        catch (SceneEditException error) { MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void AddZone(int type, int x, int y, int z)
    {
        // two cells square and two layers tall, standing on the clicked floor
        var zone = new SceneZoneModel { X0 = x - 512, X1 = x + 512, Y0 = y, Y1 = y + 512, Z0 = z - 512, Z1 = z + 512, Type = type };
        int index = -1;
        try
        {
            doc.Edit($"Add {ZoneStyle.NameOf(type)} zone", m => index = SceneOps.AddZone(m, zone));
            Select(Kind.Zone, index);
        }
        catch (SceneEditException error) { MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private int? PickEntity(int? current)
    {
        var items = Enumerable.Range(0, game.EntityCount).Select(k => (k, $"{k,3}  {game.EntityName(k) ?? "(unnamed)"}")).ToList();
        return ListPickWindow.Pick(this, "Choose an entity", items, current, "Entities are the creatures and objects of FILE3D.HQR. The new actor takes its first body and animation; change them in the inspector.");
    }

    private void ChooseEntity()
    {
        if (selKind != Kind.Actor || selIndex <= 0) return;
        var i = selIndex;
        if (PickEntity(doc.Scene.Actors[i].Entity) is not { } entity) return;
        doc.Edit($"Set entity of actor {i}", m => { m.Actors[i].Entity = entity; m.Actors[i].Body = 0; m.Actors[i].Anim = 0; });
        BuildInspector();
    }

    // ---- deleting, duplicating ----

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        var index = selIndex;
        try
        {
            switch (selKind)
            {
                case Kind.Actor when index > 0:
                    try { doc.Edit($"Delete actor {index}", m => SceneOps.DeleteActor(m, index)); }
                    catch (SceneEditException error) when (error.GetType() == typeof(SceneEditException))
                    {
                        if (MessageBox.Show(this, error.Message + "\n\nDelete it anyway? Those references will point at Twinsen (actor 0) instead.", "Delete actor",
                                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                        doc.Edit($"Delete actor {index}", m => SceneOps.DeleteActor(m, index, retarget: 0));
                    }
                    break;
                case Kind.Zone:
                    doc.Edit($"Delete zone {index}", m => SceneOps.DeleteZone(m, index));
                    break;
                case Kind.Point:
                    try { doc.Edit($"Delete track point {index}", m => SceneOps.DeleteTrackPoint(m, index)); }
                    catch (SceneEditException error) when (error.GetType() == typeof(SceneEditException))
                    {
                        if (MessageBox.Show(this, error.Message + "\n\nDelete it anyway? Those references will use track point 0 instead.", "Delete track point",
                                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                        doc.Edit($"Delete track point {index}", m => SceneOps.DeleteTrackPoint(m, index, retarget: index == 0 ? 1 : 0));
                    }
                    break;
                default:
                    return;
            }
        }
        catch (SceneEditException error)
        {
            MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        selKind = Kind.None; selIndex = -1;
        RefreshAll();
        BuildInspector();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var i = selIndex;
        try
        {
            switch (selKind)
            {
                case Kind.Actor when i > 0:
                {
                    int index = -1;
                    doc.Edit($"Duplicate actor {i}", m => { var copy = m.Actors[i].Clone(); copy.X += 512; index = SceneOps.AddActor(m, copy); });
                    Select(Kind.Actor, index);
                    break;
                }
                case Kind.Zone:
                {
                    int index = -1;
                    doc.Edit($"Duplicate zone {i}", m => { var copy = m.Zones[i].Clone(); copy.X0 += 512; copy.X1 += 512; index = SceneOps.AddZone(m, copy); });
                    Select(Kind.Zone, index);
                    break;
                }
                case Kind.Point:
                {
                    int index = -1;
                    doc.Edit($"Duplicate track point {i}", m => index = SceneOps.AddTrackPoint(m, m.TrackPoints[i] with { X = m.TrackPoints[i].X + 512 }));
                    Select(Kind.Point, index);
                    break;
                }
            }
        }
        catch (SceneEditException error) { MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ---- the scene menu ----

    private void ScenePlus_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ScenePlusButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var check = new MenuItem { Header = "Check the scene against the engine's rules…" };
        check.Click += (_, _) => ShowIssues();
        var saveAs = new MenuItem { Header = "Save this scene into another slot…" };
        saveAs.Click += (_, _) => SaveAsSlot();
        var blank = new MenuItem { Header = "Make this scene blank (a flat floor and Twinsen)…" };
        blank.Click += (_, _) => MakeBlank();
        var map = new MenuItem { Header = "Edit this scene's map in the grid editor…" };
        map.Click += (_, _) => OpenMapEditor();
        menu.Items.Add(check);
        menu.Items.Add(saveAs);
        menu.Items.Add(map);
        menu.Items.Add(new Separator());
        menu.Items.Add(blank);
        menu.IsOpen = true;
    }

    // The scene's grid in the grid editor (paint blocks, edit the library). The grid editor writes LBA_GRI.HQR itself, so unsaved changes here are saved first.
    private void OpenMapEditor()
    {
        if (doc.IsDirty)
        {
            if (MessageBox.Show(this, "Save this scene first? The grid editor works on the saved grid.", "Grid editor", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            try { doc.Save(); }
            catch (Exception error) when (error is SceneValidationException or IOException or InvalidDataException) { MessageBox.Show(this, error.Message, "Grid editor", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        new GridEditorWindow(directory, null, null, null, sceneNumber) { Owner = this }.Show();
    }

    // Replaces the scene's contents with an empty world: a flat floor built from the slot's own floor block, and Twinsen on
    // it. Undoable, and nothing is written until Save. The slot keeps its island, music, light and block library.
    private void MakeBlank()
    {
        if (MessageBox.Show(this, $"Replace everything in scene {sceneNumber} (its actors, zones, track points and map) with a blank scene?\n\nNothing is written until you Save, and Undo brings it back.",
                "Blank scene", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            var (blank, grid) = Lba1BlankScene.Create(store, sceneNumber);
            doc.EditBoth($"Make scene {sceneNumber} blank", m =>
            {
                m.Island = blank.Island; m.GameOverScene = blank.GameOverScene; m.AlphaLight = blank.AlphaLight; m.BetaLight = blank.BetaLight;
                m.Music = blank.Music; m.SecondMin = blank.SecondMin; m.SecondEcart = blank.SecondEcart; m.Ambient = blank.Ambient;
                m.Actors.Clear(); m.Actors.AddRange(blank.Actors);
                m.Zones.Clear(); m.Zones.AddRange(blank.Zones);
                m.TrackPoints.Clear(); m.TrackPoints.AddRange(blank.TrackPoints);
            }, _ => grid);
        }
        catch (Exception error) when (error is SceneEditException or IOException or InvalidDataException or InvalidOperationException)
        {
            MessageBox.Show(this, error.Message, "Blank scene", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        selKind = Kind.None; selIndex = -1;
        RefreshAll();
        BuildInspector();
        BuildHeader();
        CenterOn(image!.Project(doc.Scene.Hero.X, doc.Scene.Hero.Y, doc.Scene.Hero.Z));
        SetStatus("The scene is blank. Add actors and zones with Add; Save when you are ready, then Play scene.");
    }

    private void ShowIssues()
    {
        var issues = store.Validate(sceneNumber, doc.Scene, doc.Grid);
        MessageBox.Show(this, issues.Count == 0 ? "No problems found." : string.Join("\n", issues.Take(25).Select(i => i.ToString())) + (issues.Count > 25 ? $"\n... {issues.Count - 25} more" : ""),
            "Scene check", MessageBoxButton.OK, issues.Any(i => i.Severity == SceneIssueSeverity.Error) ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    // The game has a fixed table of scenes, so a new scene replaces one of the existing ones.
    private void SaveAsSlot()
    {
        var items = Enumerable.Range(0, store.SceneCount).Select(k => (k, $"{k,3}  {game.Description(k) ?? "scene"}")).ToList();
        if (ListPickWindow.Pick(this, "Save into which scene?", items, sceneNumber,
                "The game's scene table is fixed, so this replaces the chosen scene (its original stays in the .bak file until you undo). The scene keeps its own grid and block library only if you choose the same slot; the grid saved is this scene's.") is not { } slot) return;
        if (slot != sceneNumber && MessageBox.Show(this, $"Replace scene {slot} ({game.Description(slot)}) with this scene?", "Save as", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        saving = true;
        try
        {
            var result = doc.SaveAs(slot);
            SetStatus($"Saved into scene {slot} ({result.RecordBytes} bytes).");
        }
        catch (Exception error) when (error is SceneEditException or IOException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Not saved: {error.Message}", "Save as", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { saving = false; }
    }

    // ---- other windows ----

    private void EditActorInMain(bool script)
    {
        if (selKind != Kind.Actor || selIndex < 0 || editInMain is null) return;
        if (doc.IsDirty)
        {
            var answer = MessageBox.Show(this, "The full editors work on the saved scene. Save scene " + sceneNumber + " first?", "Scene editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes || !TrySave()) return;
        }
        editInMain(sceneNumber, selIndex, script);
    }

    // ---- keys ----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.S) { if (doc.IsDirty) TrySave(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Escape) { CancelPlacement(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo_Click(this, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
    }
}
