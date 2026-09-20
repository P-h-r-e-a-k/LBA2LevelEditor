using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LBA2LevelEditor.Scenes;

namespace LBA2LevelEditor;

// Edits one LBA2 scene as data (SceneDocument), on a plan of the scene seen from above: its actors, zones and track points
// are drawn where they are, and can be picked, dragged, added, deleted and duplicated, their numbers edited, and every
// step undone. Nothing reaches the game files until Save (which validates first and fixes the patch table and the size of
// the engine's scene buffer). Play scene runs the full LBA2 engine on what is saved. Scripts open in the script editor.
public partial class Lba2SceneEditorWindow : Window
{
    private enum Kind { None, Actor, Zone, Point }

    private const double PixelsPerUnit = 1 / 32.0;      // the plan: 32 world units to a pixel
    private const int SnapUnit = 128;

    private readonly string directory;
    private readonly SceneStore store;
    private readonly Action<int, int>? editScript;
    private readonly List<(int Id, string Label)> scenes;
    private readonly IReadOnlyList<string?> bodyNames;

    private SceneDocument doc = null!;
    private int sceneNumber = -1;
    private Kind selKind = Kind.None;
    private int selIndex = -1;
    private bool loading, saving, dragging;
    private Action<int, int, int>? pending;
    private string? pendingHint;
    private (int X, int Z) dragOrigin;
    private SceneZoneModel? dragZoneStart;

    private sealed record SceneItem(int Scene, string Label)
    {
        public override string ToString() => Label;
    }

    internal Lba2SceneEditorWindow(string directory, int scene, Action<int, int>? editScript)
    {
        InitializeComponent();
        this.directory = directory;
        this.editScript = editScript;
        store = new SceneStore(SceneGame.Lba2, directory);
        scenes = Lba2SceneList.Load(directory);
        var bodyPath = System.IO.Path.Combine(directory, "BODY.HQR");
        bodyNames = File.Exists(bodyPath) ? HqdDescriptions.Load("BODY2.HQD", HqrArchive.CountEntries(bodyPath)).Names : Array.Empty<string?>();

        loading = true;
        foreach (var s in scenes) SceneCombo.Items.Add(new SceneItem(s.Id, s.Label));
        loading = false;
        SceneHistory.Changed += OnStoreChanged;
        Loaded += (_, _) => OpenScene(scene);
    }

    // ---- opening, saving ----

    private void OpenScene(int scene)
    {
        if (doc is not null && doc.IsDirty && !ConfirmLeave()) { SelectSceneItem(sceneNumber); return; }
        if (doc is not null) doc.Changed -= OnDocChanged;
        try { doc = SceneDocument.Open(store, scene); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or SceneEditException)
        {
            MessageBox.Show(this, $"Couldn't read scene {scene}: {error.Message}", "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        sceneNumber = scene;
        doc.Changed += OnDocChanged;
        selKind = Kind.None; selIndex = -1; pending = null; pendingHint = null;
        SelectSceneItem(scene);
        RefreshAll();
        BuildInspector();
        BuildHeader();
        SetStatus($"Scene {scene} opened.");
        var hero = doc.Scene.Hero;
        CenterOn(new Point(hero.X * PixelsPerUnit, hero.Z * PixelsPerUnit));
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
        finally { saving = false; }
    }

    // Another window (the script editor) saved into the same game files.
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
                RefreshAll(); BuildInspector(); BuildHeader();
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

    private void OnDocChanged(object? sender, EventArgs e) { RefreshAll(); RefreshInspectorValues(); }

    private void RefreshAll()
    {
        var scene = doc.Scene;
        if ((selKind == Kind.Actor && selIndex >= scene.Actors.Count) || (selKind == Kind.Zone && selIndex >= scene.Zones.Count) || (selKind == Kind.Point && selIndex >= scene.TrackPoints.Count))
        {
            selKind = Kind.None; selIndex = -1; BuildInspector();
        }
        RefreshLists();
        DrawPlan();
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        UndoButton.IsEnabled = doc.CanUndo; RedoButton.IsEnabled = doc.CanRedo;
        UndoButton.ToolTip = doc.UndoDescription is { } u ? $"Undo: {u}" : null;
        RedoButton.ToolTip = doc.RedoDescription is { } r ? $"Redo: {r}" : null;
        SaveButton.IsEnabled = RevertButton.IsEnabled = doc.IsDirty;
        DuplicateButton.IsEnabled = DeleteButton.IsEnabled = selKind != Kind.None && !(selKind == Kind.Actor && selIndex == 0);
        Title = $"LBA2 - scene editor - scene {sceneNumber}{(doc.IsDirty ? " *" : "")}";
    }

    private string BodyName(int body) => body >= 0 && body < bodyNames.Count && bodyNames[body] is { } name ? name : $"body {body}";

    private string DescribeActor(int i, SceneActorModel a)
        => i == 0 ? "Twinsen (start position)" : a.IsSprite ? $"sprite {a.Sprite}" : a.Body < 0 ? "(no body)" : BodyName(a.Body);

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
            ZoneList.Items.Add($"{i,3}: {ZoneStyle.NameOf(z.Type)}{(z.Type == 0 ? $" -> scene {z.Num}" : z.Type == 2 ? $" #{z.Num}" : "")}");
        }
        PointList.Items.Clear();
        for (var i = 0; i < scene.TrackPoints.Count; i++) PointList.Items.Add($"{i,3}: ({scene.TrackPoints[i].X}, {scene.TrackPoints[i].Y}, {scene.TrackPoints[i].Z})");
        ActorList.SelectedIndex = selKind == Kind.Actor ? selIndex : -1;
        ZoneList.SelectedIndex = selKind == Kind.Zone ? selIndex : -1;
        PointList.SelectedIndex = selKind == Kind.Point ? selIndex : -1;
        loading = false;
    }

    // ---- the plan ----

    private static Color ColourOf(int i) => Color.FromRgb((byte)(80 + i * 67 % 170), (byte)(120 + i * 131 % 130), (byte)(90 + i * 29 % 160));
    private static Point At(double x, double z) => new(Math.Max(0, x) * PixelsPerUnit, Math.Max(0, z) * PixelsPerUnit);

    private void DrawPlan()
    {
        var canvas = PlanCanvas;
        canvas.Children.Clear();
        var scene = doc.Scene;

        // the extent: at least a whole 64 x 64 cell area, more when something lies outside it
        double maxX = 32768, maxZ = 32768;
        foreach (var a in scene.Actors) { maxX = Math.Max(maxX, a.X + 1024); maxZ = Math.Max(maxZ, a.Z + 1024); }
        foreach (var z in scene.Zones) { maxX = Math.Max(maxX, Math.Max(z.X0, z.X1) + 1024); maxZ = Math.Max(maxZ, Math.Max(z.Z0, z.Z1) + 1024); }
        foreach (var p in scene.TrackPoints) { maxX = Math.Max(maxX, p.X + 1024); maxZ = Math.Max(maxZ, p.Z + 1024); }
        canvas.Width = maxX * PixelsPerUnit; canvas.Height = maxZ * PixelsPerUnit;

        // a grid of cells (512 units), heavier every 8 cells
        var fine = new StreamGeometry();
        var heavy = new StreamGeometry();
        using (var f = fine.Open())
        using (var h = heavy.Open())
        {
            for (var cell = 0; cell * 512 <= Math.Max(maxX, maxZ); cell++)
            {
                var ctx = cell % 8 == 0 ? h : f;
                var v = cell * 512 * PixelsPerUnit;
                ctx.BeginFigure(new Point(v, 0), false, false); ctx.LineTo(new Point(v, canvas.Height), true, false);
                ctx.BeginFigure(new Point(0, v), false, false); ctx.LineTo(new Point(canvas.Width, v), true, false);
            }
        }
        fine.Freeze(); heavy.Freeze();
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = fine, Stroke = new SolidColorBrush(Color.FromArgb(40, 137, 149, 139)), StrokeThickness = 0.5, IsHitTestVisible = false });
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = heavy, Stroke = new SolidColorBrush(Color.FromArgb(90, 137, 149, 139)), StrokeThickness = 1, IsHitTestVisible = false });

        if (ZonesCheck.IsChecked == true)
        {
            for (var i = 0; i < scene.Zones.Count; i++)
            {
                var z = scene.Zones[i];
                var colour = ZoneStyle.ColorOf(z.Type);
                var selected = selKind == Kind.Zone && selIndex == i;
                var a = At(Math.Min(z.X0, z.X1), Math.Min(z.Z0, z.Z1));
                var b = At(Math.Max(z.X0, z.X1), Math.Max(z.Z0, z.Z1));
                var rect = new Rectangle
                {
                    Width = Math.Max(3, b.X - a.X), Height = Math.Max(3, b.Y - a.Y),
                    Stroke = selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(210, colour.R, colour.G, colour.B)),
                    StrokeThickness = selected ? 2.5 : 1,
                    Fill = new SolidColorBrush(Color.FromArgb(selected ? (byte)90 : (byte)26, colour.R, colour.G, colour.B)),
                    Cursor = Cursors.Hand, Tag = (Kind.Zone, i),
                };
                rect.MouseLeftButtonDown += Item_MouseLeftButtonDown;
                Canvas.SetLeft(rect, a.X); Canvas.SetTop(rect, a.Y);
                canvas.Children.Add(rect);
                if (selected || z.Type != 1) AddLabel(i.ToString(), a.X + 2, a.Y + 1, new SolidColorBrush(colour));
            }
        }

        if (PointsCheck.IsChecked == true)
        {
            for (var i = 0; i < scene.TrackPoints.Count; i++)
            {
                var p = At(scene.TrackPoints[i].X, scene.TrackPoints[i].Z);
                var selected = selKind == Kind.Point && selIndex == i;
                var diamond = new Polygon
                {
                    Points = new PointCollection { new(p.X, p.Y - 6), new(p.X + 6, p.Y), new(p.X, p.Y + 6), new(p.X - 6, p.Y) },
                    Fill = selected ? Brushes.White : Brushes.Gold, Stroke = Brushes.Black, StrokeThickness = 1, Cursor = Cursors.Hand, Tag = (Kind.Point, i),
                };
                diamond.MouseLeftButtonDown += Item_MouseLeftButtonDown;
                canvas.Children.Add(diamond);
                AddLabel(i.ToString(), p.X + 7, p.Y - 14, Brushes.Gold);
            }
        }

        if (ActorsCheck.IsChecked == true)
        {
            for (var i = 0; i < scene.Actors.Count; i++)
            {
                var a = scene.Actors[i];
                var p = At(a.X, a.Z);
                var selected = selKind == Kind.Actor && selIndex == i;
                var size = i == 0 ? 18 : 14;
                var dot = new Ellipse
                {
                    Width = size, Height = size, Cursor = Cursors.Hand, Tag = (Kind.Actor, i),
                    Fill = i == 0 ? Brushes.Gold : new SolidColorBrush(ColourOf(i)),
                    Stroke = selected ? Brushes.White : Brushes.Black, StrokeThickness = selected ? 2.5 : 1,
                };
                dot.MouseLeftButtonDown += Item_MouseLeftButtonDown;
                Canvas.SetLeft(dot, p.X - size / 2.0); Canvas.SetTop(dot, p.Y - size / 2.0);
                // facing: LBA2 angles are 4096 to a turn, 0 = +z
                var turn = (a.Beta & 4095) * Math.PI * 2 / 4096;
                canvas.Children.Add(new Line { X1 = p.X, Y1 = p.Y, X2 = p.X + Math.Sin(turn) * 18, Y2 = p.Y + Math.Cos(turn) * 18, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false });
                canvas.Children.Add(dot);
                AddLabel(i == 0 ? "start" : i.ToString(), p.X + 8, p.Y - 16, selected ? Brushes.White : new SolidColorBrush(ColourOf(i)));
            }
        }
    }

    private void AddLabel(string text, double x, double y, Brush brush)
    {
        var label = new TextBlock { Text = text, Foreground = brush, FontSize = 10, FontFamily = new FontFamily("Consolas"), IsHitTestVisible = false };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y);
        PlanCanvas.Children.Add(label);
    }

    private void CenterOn(Point at)
    {
        var scale = MapScale.ScaleX;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            MapScroll.ScrollToHorizontalOffset(Math.Max(0, at.X * scale - MapScroll.ViewportWidth / 2));
            MapScroll.ScrollToVerticalOffset(Math.Max(0, at.Y * scale - MapScroll.ViewportHeight / 2));
        });
    }

    private void CenterOnSelection()
    {
        var scene = doc.Scene;
        switch (selKind)
        {
            case Kind.Actor when selIndex < scene.Actors.Count: CenterOn(At(scene.Actors[selIndex].X, scene.Actors[selIndex].Z)); break;
            case Kind.Zone when selIndex < scene.Zones.Count: var z = scene.Zones[selIndex]; CenterOn(At((z.X0 + z.X1) / 2.0, (z.Z0 + z.Z1) / 2.0)); break;
            case Kind.Point when selIndex < scene.TrackPoints.Count: CenterOn(At(scene.TrackPoints[selIndex].X, scene.TrackPoints[selIndex].Z)); break;
        }
    }

    // ---- mouse ----

    private static int Snap(double v) => (int)Math.Round(v / SnapUnit) * SnapUnit;
    private (int X, int Z) World(MouseEventArgs e) { var p = e.GetPosition(PlanCanvas); return (Snap(p.X / PixelsPerUnit), Snap(p.Y / PixelsPerUnit)); }

    private int CurrentY() => selKind switch
    {
        Kind.Actor when selIndex < doc.Scene.Actors.Count => doc.Scene.Actors[selIndex].Y,
        Kind.Point when selIndex < doc.Scene.TrackPoints.Count => doc.Scene.TrackPoints[selIndex].Y,
        Kind.Zone when selIndex < doc.Scene.Zones.Count => Math.Min(doc.Scene.Zones[selIndex].Y0, doc.Scene.Zones[selIndex].Y1),
        _ => doc.Scene.Hero.Y,
    };

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (pending is not null) return;
        if (((FrameworkElement)sender).Tag is not ValueTuple<Kind, int> tag) return;
        e.Handled = true;
        Select(tag.Item1, tag.Item2);
        if (e.ClickCount >= 2 && tag.Item1 == Kind.Actor) { EditScriptOfSelection(); return; }
        dragging = true;
        dragOrigin = World(e);
        dragZoneStart = selKind == Kind.Zone ? doc.Scene.Zones[selIndex].Clone() : null;
        MapHost.CaptureMouse();
    }

    private void Map_MouseMove(object sender, MouseEventArgs e)
    {
        var world = World(e);
        if (!dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            if (pending is not null) StatusText.Text = $"{pendingHint}   (x {world.X}, z {world.Z})";
            return;
        }
        var index = selIndex;
        var y = CurrentY();
        switch (selKind)
        {
            case Kind.Actor: doc.Edit($"Move actor {index}", m => { var a = m.Actors[index]; a.X = world.X; a.Z = world.Z; }, $"drag:a{index}"); break;
            case Kind.Point: doc.Edit($"Move track point {index}", m => m.TrackPoints[index] = m.TrackPoints[index] with { X = world.X, Z = world.Z }, $"drag:p{index}"); break;
            case Kind.Zone when dragZoneStart is { } start:
                int dx = world.X - dragOrigin.X, dz = world.Z - dragOrigin.Z;
                doc.Edit($"Move zone {index}", m => { var z = m.Zones[index]; z.X0 = start.X0 + dx; z.X1 = start.X1 + dx; z.Z0 = start.Z0 + dz; z.Z1 = start.Z1 + dz; }, $"drag:z{index}");
                break;
        }
        _ = y;
    }

    private void Map_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        MapHost.ReleaseMouseCapture();
    }

    private void Map_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (pending is { } place)
        {
            var world = World(e);
            pending = null; pendingHint = null;
            place(world.X, doc.Scene.Hero.Y, world.Z);
            return;
        }
        Select(Kind.None, -1);
    }

    private void Map_MouseRightButtonDown(object sender, MouseButtonEventArgs e) => CancelPlacement();

    // ---- selection ----

    private void Select(Kind kind, int index)
    {
        if (kind == selKind && index == selIndex) return;
        selKind = kind; selIndex = index;
        RefreshLists(); DrawPlan(); UpdateToolbar(); BuildInspector();
        ListTabs.SelectedIndex = kind switch { Kind.Zone => 1, Kind.Point => 2, _ => 0 };
    }

    private void ActorList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading && ActorList.SelectedIndex >= 0) { Select(Kind.Actor, ActorList.SelectedIndex); CenterOnSelection(); } }
    private void ZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading && ZoneList.SelectedIndex >= 0) { Select(Kind.Zone, ZoneList.SelectedIndex); CenterOnSelection(); } }
    private void PointList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading && PointList.SelectedIndex >= 0) { Select(Kind.Point, PointList.SelectedIndex); CenterOnSelection(); } }

    // ---- the inspector ----

    private readonly List<(TextBox Box, Func<string> Get)> inspectorFields = new();
    private readonly List<(TextBox Box, Func<string> Get)> headerFields = new();
    private List<(TextBox Box, Func<string> Get)> liveFields = null!;

    private static Grid NewFieldGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private void AddRow(Grid grid, string label, Func<string> get, Action<string> commit, string? hint = null)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var row = grid.RowDefinitions.Count - 1;
        var caption = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0x95, 0x8B)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
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
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(text, CultureInfo.InvariantCulture);
    }

    private static ZoneData ZoneDataOf(SceneZoneModel z, int scene, int index)
        => new() { Game = 2, Scene = scene, Index = index, Type = z.Type, Num = z.Num, Info = (int[])z.Info.Clone(), Snap = z.Snap, X0 = z.X0, Y0 = z.Y0, Z0 = z.Z0, X1 = z.X1, Y1 = z.Y1, Z1 = z.Z1 };

    private void BuildInspector()
    {
        inspectorFields.Clear();
        liveFields = inspectorFields;
        var g = InspectorGrid;
        g.Children.Clear(); g.RowDefinitions.Clear(); g.ColumnDefinitions.Clear();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        InspectorButtons.Children.Clear();
        InspectorTitle.Text = InspectorTitleText();
        InspectorNote.Text = "";
        var scene = doc.Scene;
        var i = selIndex;

        switch (selKind)
        {
            case Kind.Actor when i >= 0 && i < scene.Actors.Count:
            {
                void Num(string label, Func<SceneActorModel, int> get, Action<SceneActorModel, int> set, string? hint = null)
                    => AddRow(g, label, () => get(doc.Scene.Actors[i]).ToString(CultureInfo.InvariantCulture), text =>
                    {
                        var v = Number(text);
                        doc.Edit($"Set {label.ToLowerInvariant()} of actor {i}", m => set(m.Actors[i], v), $"a{i}:{label}");
                    }, hint);
                Num("X", a => a.X, (a, v) => a.X = v, "world position; 512 units = one cell");
                Num("Y", a => a.Y, (a, v) => a.Y = v);
                Num("Z", a => a.Z, (a, v) => a.Z = v);
                if (i > 0)
                {
                    Num("Facing", a => a.Beta, (a, v) => a.Beta = v & 4095, "0..4095: 0 = +z, 1024 = +x, 2048 = -z, 3072 = -x");
                    Num("Body", a => a.Body, (a, v) => a.Body = v, "an entry of BODY.HQR; -1 = none");
                    Num("Animation", a => a.Anim, (a, v) => a.Anim = v, "an entry of ANIM.HQR");
                    Num("Sprite", a => a.Sprite, (a, v) => a.Sprite = v, "for sprite actors");
                    AddRow(g, "Flags", () => "0x" + doc.Scene.Actors[i].Flags.ToString("X8"), text => { var v = (uint)Number(text); doc.Edit($"Set flags of actor {i}", m => m.Actors[i].Flags = v, $"a{i}:Flags"); }, "hex");
                    Num("Move", a => a.Move, (a, v) => a.Move = v, "the control mode (0 none, 1 manual, 2 follow, 3 track ...)");
                    Num("Speed", a => a.SRot, (a, v) => a.SRot = v);
                    Num("Life", a => a.LifePoints, (a, v) => a.LifePoints = v);
                    Num("Armour", a => a.Armor, (a, v) => a.Armor = v);
                    Num("Hit force", a => a.HitForce, (a, v) => a.HitForce = v);
                    Num("Text colour", a => a.CoulObj, (a, v) => a.CoulObj = v);
                    Num("Bonus option", a => a.OptionFlags, (a, v) => a.OptionFlags = v);
                    Num("Bonus count", a => a.NbBonus, (a, v) => a.NbBonus = v);
                    for (var k = 0; k < 4; k++) { var slot = k; Num($"Info {k}", a => a.Info[slot], (a, v) => a.Info[slot] = v, "parameters some moves use"); }
                    var choose = new Button { Content = "Choose body…" };
                    choose.Click += (_, _) => ChooseBody();
                    InspectorButtons.Children.Add(choose);
                }
                var script = new Button { Content = "Edit script…" };
                script.Click += (_, _) => EditScriptOfSelection();
                InspectorButtons.Children.Add(script);
                InspectorNote.Text = i == 0
                    ? "Where Twinsen starts when the scene is entered without a cube-change zone."
                    : $"Life script {scene.Actors[i].Life.Length} bytes, track script {scene.Actors[i].Track.Length} bytes.";
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
                AddRow(g, "Type", () => $"{doc.Scene.Zones[i].Type}", text =>
                {
                    var v = Number(text);
                    doc.Edit($"Set type of zone {i}", m => m.Zones[i].Type = v, $"z{i}:Type");
                    BuildInspector();
                }, string.Join(", ", ZoneStyle.TypeNames.Select((n, k) => $"{k} = {n}")));
                Num("X min", s => s.X0, (s, v) => s.X0 = v); Num("X max", s => s.X1, (s, v) => s.X1 = v);
                Num("Y min", s => s.Y0, (s, v) => s.Y0 = v); Num("Y max", s => s.Y1, (s, v) => s.Y1 = v);
                Num("Z min", s => s.Z0, (s, v) => s.Z0 = v); Num("Z max", s => s.Z1, (s, v) => s.Z1 = v);
                foreach (var field in ZoneFields.For(ZoneDataOf(scene.Zones[i], sceneNumber, i)))
                {
                    var f = field;
                    AddRow(g, f.Label, () => f.Get(ZoneDataOf(doc.Scene.Zones[i], sceneNumber, i)).ToString(CultureInfo.InvariantCulture), text =>
                    {
                        var v = Number(text);
                        doc.Edit($"Set {f.Label.ToLowerInvariant()} of zone {i}", m =>
                        {
                            var data = ZoneDataOf(m.Zones[i], sceneNumber, i);
                            f.Set(data, v);
                            m.Zones[i].Num = data.Num;
                            m.Zones[i].Info = data.Info;
                        }, $"z{i}:{f.Label}");
                    }, f.Hint);
                }
                if (scene.Zones[i].Type == 0)
                {
                    var go = new Button { Content = "Open destination" };
                    go.Click += (_, _) => OpenScene(doc.Scene.Zones[i].Num);
                    InspectorButtons.Children.Add(go);
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
                InspectorNote.Text = "Waypoints that actors' track scripts walk between.";
                break;
            }

            default:
                InspectorNote.Text = "Click an actor, a zone or a track point on the plan to edit it; drag to move it. Use Add to place something new.";
                break;
        }
    }

    private void BuildHeader()
    {
        headerFields.Clear();
        liveFields = headerFields;
        HeaderPanel.Children.Clear();
        var g = NewFieldGrid();
        void Num(string label, Func<SceneModel, int> get, Action<SceneModel, int> set, string? hint = null)
            => AddRow(g, label, () => get(doc.Scene).ToString(CultureInfo.InvariantCulture), text =>
            {
                var v = Number(text);
                doc.Edit($"Set {label.ToLowerInvariant()} of scene {sceneNumber}", m => set(m, v), $"h:{label}");
            }, hint);
        Num("Island", m => m.Island, (m, v) => m.Island = v);
        Num("Cube mode", m => m.CubeMode, (m, v) => m.CubeMode = v, "0 = an interior, 1 = an exterior (a cube of an island)");
        Num("Cube X", m => m.CubeX, (m, v) => m.CubeX = v, "island cube (exteriors)");
        Num("Cube Y", m => m.CubeY, (m, v) => m.CubeY = v);
        Num("Light alpha", m => m.AlphaLight, (m, v) => m.AlphaLight = v);
        Num("Light beta", m => m.BetaLight, (m, v) => m.BetaLight = v);
        Num("Shadow level", m => m.ShadowLevel, (m, v) => m.ShadowLevel = v);
        Num("Labyrinth mode", m => m.LabyrinthMode, (m, v) => m.LabyrinthMode = v);
        Num("Music", m => m.Music, (m, v) => m.Music = v, "the jingle number");
        Num("Ambient delay min", m => m.SecondMin, (m, v) => m.SecondMin = v);
        Num("Ambient delay range", m => m.SecondEcart, (m, v) => m.SecondEcart = v);
        for (var k = 0; k < 4; k++)
        {
            var n = k;
            Num($"Ambient {k} sample", m => m.Ambient[n].Sample, (m, v) => m.Ambient[n].Sample = v);
            Num($"Ambient {k} repeat", m => m.Ambient[n].Repeat, (m, v) => m.Ambient[n].Repeat = v);
            Num($"Ambient {k} round", m => m.Ambient[n].Round, (m, v) => m.Ambient[n].Round = v);
            Num($"Ambient {k} frequency", m => m.Ambient[n].Frequency, (m, v) => m.Ambient[n].Frequency = v);
            Num($"Ambient {k} volume", m => m.Ambient[n].Volume, (m, v) => m.Ambient[n].Volume = v);
        }
        HeaderPanel.Children.Add(g);
        HeaderPanel.Children.Add(new TextBlock
        {
            Text = $"{doc.Scene.Actors.Count} actors, {doc.Scene.Zones.Count} zones, {doc.Scene.TrackPoints.Count} track points.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0x95, 0x8B)), FontSize = 10.5, Margin = new Thickness(0, 10, 0, 0),
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
        RefreshAll(); BuildInspector(); BuildHeader();
        SetStatus("Changes discarded.");
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { var what = doc.UndoDescription; if (doc.Undo()) { BuildInspector(); BuildHeader(); SetStatus($"Undid: {what}"); } }
    private void Redo_Click(object sender, RoutedEventArgs e) { var what = doc.RedoDescription; if (doc.Redo()) { BuildInspector(); BuildHeader(); SetStatus($"Redid: {what}"); } }
    private void View_Click(object sender, RoutedEventArgs e) => DrawPlan();
    private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (MapScale is not null) MapScale.ScaleX = MapScale.ScaleY = e.NewValue; }

    // Runs the full LBA2 engine on the saved scene, starting the way the last "LBA2: play scene" did.
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (doc.IsDirty)
        {
            var answer = MessageBox.Show(this, $"Playing uses what is saved. Save scene {sceneNumber} first?", "Scene editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes && !TrySave()) return;
        }
        var options = Lba2Play.LastOptions is { } last ? last.WithScene(sceneNumber) : new Lba2PlayOptions { Scene = sceneNumber };
        if (Lba2Play.Launch(directory, options, out var problem) is null) MessageBox.Show(this, problem ?? "The game didn't start.", "LBA2: play scene", MessageBoxButton.OK, MessageBoxImage.Warning);
        else SetStatus($"The LBA2 engine is starting scene {sceneNumber}.");
    }

    // ---- adding ----

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string header, Action click, bool enabled = true) { var item = new MenuItem { Header = header, IsEnabled = enabled }; item.Click += (_, _) => click(); return item; }
        menu.Items.Add(Item("New 3D actor…", () =>
        {
            var body = PickBody(0);
            if (body is null) return;
            Place($"Click the plan to place the new actor (body {body}); Esc or right-click cancels.", (x, y, z) =>
            {
                var actor = SceneOps.BlankActor(SceneGame.Lba2, x, y, z);
                actor.Body = body.Value;
                AddActor(actor, "Add actor");
            });
        }));
        menu.Items.Add(Item("Copy of the selected actor", () =>
        {
            var source = selIndex;
            Place($"Click the plan to place the copy of actor {source}; Esc cancels.", (x, y, z) => { var copy = doc.Scene.Actors[source].Clone(); copy.X = x; copy.Y = y; copy.Z = z; AddActor(copy, $"Copy actor {source}"); });
        }, selKind == Kind.Actor && selIndex > 0));
        menu.Items.Add(new Separator());
        var zones = new MenuItem { Header = "Zone" };
        for (var type = 0; type < ZoneStyle.TypeCount; type++)
        {
            var t = type;
            zones.Items.Add(Item(ZoneStyle.NameOf(t), () => Place($"Click the plan to place the {ZoneStyle.NameOf(t)} zone; Esc cancels.", (x, y, z) => AddZone(t, x, y, z))));
        }
        menu.Items.Add(zones);
        menu.Items.Add(Item("Track point", () => Place("Click the plan to place the track point; Esc cancels.", (x, y, z) =>
        {
            int index = -1;
            try { doc.Edit("Add track point", m => index = SceneOps.AddTrackPoint(m, new SceneTrackPoint(x, y, z))); Select(Kind.Point, index); }
            catch (SceneEditException error) { MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
        })));
        menu.IsOpen = true;
    }

    private void Place(string hint, Action<int, int, int> place) { pending = place; pendingHint = hint; StatusText.Text = hint; }

    private void CancelPlacement()
    {
        if (pending is null) return;
        pending = null; pendingHint = null;
        SetStatus("Cancelled.");
    }

    private void AddActor(SceneActorModel actor, string description)
    {
        int index = -1;
        try { doc.Edit(description, m => index = SceneOps.AddActor(m, actor)); Select(Kind.Actor, index); }
        catch (SceneEditException error) { MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void AddZone(int type, int x, int y, int z)
    {
        // an LBA2 zone has eight info values
        var zone = new SceneZoneModel { X0 = x - 512, X1 = x + 512, Y0 = y, Y1 = y + 1024, Z0 = z - 512, Z1 = z + 512, Type = type, Info = new int[8] };
        int index = -1;
        try { doc.Edit($"Add {ZoneStyle.NameOf(type)} zone", m => index = SceneOps.AddZone(m, zone)); Select(Kind.Zone, index); }
        catch (SceneEditException error) { MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private int? PickBody(int? current)
    {
        var bodies = System.IO.Path.Combine(directory, "BODY.HQR");
        var count = File.Exists(bodies) ? HqrArchive.CountEntries(bodies) : 0;
        var items = Enumerable.Range(0, count).Select(k => (k, $"{k,4}  {(k < bodyNames.Count ? bodyNames[k] : null) ?? "(unnamed)"}")).ToList();
        return ListPickWindow.Pick(this, "Choose a body", items, current, "Bodies are the models of BODY.HQR. The new actor is still until you give it an animation and scripts.");
    }

    private void ChooseBody()
    {
        if (selKind != Kind.Actor || selIndex <= 0) return;
        var i = selIndex;
        if (PickBody(doc.Scene.Actors[i].Body) is not { } body) return;
        doc.Edit($"Set body of actor {i}", m => m.Actors[i].Body = body);
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
                        if (MessageBox.Show(this, error.Message + "\n\nDelete it anyway? Those references will point at Twinsen (actor 0) instead.", "Delete actor", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
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
                        if (MessageBox.Show(this, error.Message + "\n\nDelete it anyway? Those references will use another track point instead.", "Delete track point", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                        doc.Edit($"Delete track point {index}", m => SceneOps.DeleteTrackPoint(m, index, retarget: index == 0 ? 1 : 0));
                    }
                    break;
                default: return;
            }
        }
        catch (SceneEditException error) { MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        selKind = Kind.None; selIndex = -1;
        RefreshAll(); BuildInspector();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var i = selIndex;
        try
        {
            int index = -1;
            switch (selKind)
            {
                case Kind.Actor when i > 0:
                    doc.Edit($"Duplicate actor {i}", m => { var copy = m.Actors[i].Clone(); copy.X += 512; index = SceneOps.AddActor(m, copy); });
                    Select(Kind.Actor, index);
                    break;
                case Kind.Zone:
                    doc.Edit($"Duplicate zone {i}", m => { var copy = m.Zones[i].Clone(); copy.X0 += 512; copy.X1 += 512; index = SceneOps.AddZone(m, copy); });
                    Select(Kind.Zone, index);
                    break;
                case Kind.Point:
                    doc.Edit($"Duplicate track point {i}", m => index = SceneOps.AddTrackPoint(m, m.TrackPoints[i] with { X = m.TrackPoints[i].X + 512 }));
                    Select(Kind.Point, index);
                    break;
            }
        }
        catch (SceneEditException error) { MessageBox.Show(this, error.Message, "Scene editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ---- the scene menu ----

    private void ScenePlus_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ScenePlusButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var check = new MenuItem { Header = "Check the scene against the engine's rules…" };
        check.Click += (_, _) =>
        {
            var issues = store.Validate(sceneNumber, doc.Scene, null);
            MessageBox.Show(this, issues.Count == 0 ? "No problems found." : string.Join("\n", issues.Take(25).Select(i => i.ToString())) + (issues.Count > 25 ? $"\n... {issues.Count - 25} more" : ""),
                "Scene check", MessageBoxButton.OK, issues.Any(i => i.Severity == SceneIssueSeverity.Error) ? MessageBoxImage.Warning : MessageBoxImage.Information);
        };
        var saveAs = new MenuItem { Header = "Save this scene into another slot…" };
        saveAs.Click += (_, _) => SaveAsSlot();
        menu.Items.Add(check);
        menu.Items.Add(saveAs);
        menu.IsOpen = true;
    }

    // The game's scene table is fixed, so a "new" scene replaces one of the existing ones.
    private void SaveAsSlot()
    {
        if (ListPickWindow.Pick(this, "Save into which scene?", scenes, sceneNumber, "The scene table is fixed, so this replaces the chosen scene (its original stays in the .bak file until you undo).") is not { } slot) return;
        if (slot != sceneNumber && MessageBox.Show(this, $"Replace scene {slot} with this scene?", "Save as", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        saving = true;
        try { var result = doc.SaveAs(slot); SetStatus($"Saved into scene {slot} ({result.RecordBytes} bytes)."); }
        catch (Exception error) when (error is SceneEditException or IOException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Not saved: {error.Message}", "Save as", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { saving = false; }
    }

    private void EditScriptOfSelection()
    {
        if (selKind != Kind.Actor || selIndex < 0 || editScript is null) return;
        if (doc.IsDirty)
        {
            var answer = MessageBox.Show(this, $"The script editor works on the saved scene. Save scene {sceneNumber} first?", "Scene editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes || !TrySave()) return;
        }
        editScript(sceneNumber, selIndex);
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
