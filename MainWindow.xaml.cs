using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;

namespace LBA2LevelEditor;

public partial class MainWindow : Window
{
    private string gameRoot = EditorSettings.Current.GameDirectory;
    private readonly CommunityRendererBackend nativeRenderer;
    private readonly TerrainType[] fallbackTiles = new TerrainType[16 * 16];
    private IslandDocument? currentIsland;
    private string activeFile = "DESERT.ILE";
    private TerrainType selectedTerrain = TerrainType.Grass;
    private double cameraYaw = 45;
    private double cameraDistance = 30000;
    private int nativeAlpha = 240;
    private int nativeBeta = -256;
    private int nativeGamma = 0;
    private int nativeDistance = 30000;
    private double targetX;
    private double targetY = 10000;
    private double targetZ;
    private Point lastMousePosition;
    private bool orbiting;
    private bool nativeViewActive;
    private CancellationTokenSource? nativeRenderCancellation;
    private readonly object nativeRenderGate = new();
    private bool nativeRenderInFlight;
    private bool nativeRenderDirty;
    private bool desiredSkyEnabled = true;
    private int minimapRequest;
    private byte[] palette = Array.Empty<byte>();
    private byte[] shadeTable = Array.Empty<byte>();
    private int shadeLevel;

    public MainWindow()
    {
        nativeRenderer = new CommunityRendererBackend(gameRoot);
        InitializeComponent();
        Focusable = true;
        KeyDown += MainWindow_KeyDown;
        SeedFallbackMap();
        BuildPalette();
        if (Directory.Exists(gameRoot))
        {
            PopulateAssetLists();
            // PopulateAssetLists() already selects activeFile in the list,
            // which fires IslandList_SelectionChanged -> LoadIsland() -- if
            // that succeeded (activeFile was actually in the list), calling
            // LoadIsland() again here would load the same island a second
            // time back to back. That redundant second load isn't just
            // wasted work: its own RegenerateMinimap() call raced behind the
            // first load's main-view render (once it turned into the very
            // first wideRadius>=1 wide render of the session) and came back
            // with land/sea tiles corrupted, then *won* over the first
            // load's correct minimap via RegenerateMinimap's own
            // stale-request check, since "more recent" isn't the same as
            // "correct" here. Only fall back to an explicit call if the
            // selection didn't already cover it (e.g. activeFile no longer
            // exists in gameRoot).
            if (IslandList.SelectedItem is null) LoadIsland(Path.Combine(gameRoot, activeFile));
        }
        else
        {
            DocumentSummary.Text = "Game folder not found — set it under Settings.";
            Settings_Click(this, new RoutedEventArgs());
        }
    }

    private void PopulateAssetLists()
    {
        IslandList.Items.Clear();
        SceneList.Items.Clear();
        if (!Directory.Exists(gameRoot)) return;
        foreach (var path in Directory.EnumerateFiles(gameRoot, "*.ILE").Where(path => !Path.GetFileName(path).StartsWith("_", StringComparison.OrdinalIgnoreCase)))
            IslandList.Items.Add(Path.GetFileName(path));
        IslandList.SelectedItem = activeFile;
        var scenePath = Path.Combine(gameRoot, "SCENE.HQR");
        if (File.Exists(scenePath))
        {
            var scenes = HqrArchive.Open(scenePath);
            foreach (var index in scenes.ValidIndices.Where(index => index > 0)) SceneList.Items.Add($"SCENE {index:000}");
        }
    }

    private void LoadIsland(string path)
    {
        try
        {
            LoadIslandPalette(path);
            currentIsland = IslandDocument.Open(path, palette, shadeTable, shadeLevel);
            activeFile = Path.GetFileName(path);
            FileLabel.Text = $"●  {activeFile}";
            DocumentTitle.Text = Path.GetFileNameWithoutExtension(path);
            DocumentSummary.Text = $"Native ILE / 16 x 16 cubes / {currentIsland.CubeCount} present / Y {currentIsland.MinHeight}..{currentIsland.MaxHeight}";
            targetX = 8 * 32768 + 16384;
            targetZ = 9 * 32768 + 16384;
            targetY = 10000;
            if (!IsWorldPositionOnIsland(targetX, targetZ) && FindFirstPresentCube() is (int cubeX, int cubeY))
            {
                targetX = cubeX * 32768 + 16384;
                targetZ = cubeY * 32768 + 16384;
            }

            // Scrollbar range matches the island's actual present-cube bounds,
            // not the full 16x16 grid: most islands only occupy a fraction of
            // it, so a fixed full-grid range left most of each scrollbar's
            // travel mapped to open sea the camera can never actually reach --
            // dragging to the visible end of the track landed on an invalid
            // cube and snapped back well short of the real edge.
            var (presentMinX, presentMinY, presentMaxX, presentMaxY) = currentIsland.PresentCubeBounds;
            PanHorizontalScrollBar.Minimum = presentMinX * 32768;
            PanHorizontalScrollBar.Maximum = (presentMaxX + 1) * 32768 - 1;
            PanVerticalScrollBar.Minimum = presentMinY * 32768;
            PanVerticalScrollBar.Maximum = (presentMaxY + 1) * 32768 - 1;
            SyncPanScrollBars();

            var preview = currentIsland.CreatePreview();
            TerrainViewport.Source = preview;
            selectedActorIndex = null;
            ActorInspectorPanel.Visibility = Visibility.Collapsed;
            ActorMarkerCanvas.Children.Clear();
            lastNativeActorScreens = null;
            lastNativeActorRoutes = null;
            RegenerateMinimap();
            if (nativeRenderer.DirectRendererReady)
            {
                nativeViewActive = true;
                nativeAlpha = 240; nativeBeta = -256; nativeGamma = 0; nativeDistance = 30000;
                DocumentSummary.Text += " / native 3D";
                RenderNativeCamera();
            }
            else
            {
                nativeViewActive = false;
                cameraDistance = DefaultCameraDistance;
                DocumentSummary.Text += " / software 3D";
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RenderSoftwareTerrain));
            }
            UpdateZoomLabel();
        }
        catch (Exception error)
        {
            nativeViewActive = false;
            MessageBox.Show(this, error.Message, "Unable to open island", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private byte[] LoadIslandPalette(string islandPath)
    {
        var name = Path.GetFileNameWithoutExtension(islandPath).ToUpperInvariant();
        var paletteIndex = 27;
        if (name == "CITABAU") paletteIndex = 42;
        else if (name == "DESERT") paletteIndex = 29;
        else if (name == "EMERAUDE") paletteIndex = 30;
        else if (name == "OTRINGAL") paletteIndex = 31;
        else if (name == "CELEBRAT" || name == "CELEBRA2") paletteIndex = 32;
        else if (name == "PLATFORM") paletteIndex = 33;
        else if (name == "MOSQUIBE") paletteIndex = 34;
        else if (name == "KNARTAS") paletteIndex = 35;
        else if (name == "ILOTCX") paletteIndex = 36;
        else if (name == "ASCENCE") paletteIndex = 37;
        var xpl = HqrArchive.Open(Path.Combine(gameRoot, "RESS.HQR")).Read(paletteIndex);
        var paletteOffset = BitConverter.ToInt32(xpl, 4);
        var fogOffset = BitConverter.ToInt32(xpl, 12);
        shadeLevel = xpl.Length >= 24 ? BitConverter.ToInt32(xpl, 20) : 0;
        palette = paletteOffset >= 0 && paletteOffset <= xpl.Length - 768 ? xpl[paletteOffset..(paletteOffset + 768)] : Array.Empty<byte>();
        shadeTable = fogOffset >= 0 && fogOffset <= xpl.Length - 4096 ? xpl[fogOffset..(fogOffset + 4096)] : Array.Empty<byte>();
        return palette;
    }

    private void SeedFallbackMap()
    {
        for (var index = 0; index < fallbackTiles.Length; index++)
        {
            var row = index / 16;
            var column = index % 16;
            fallbackTiles[index] = row < 2 || row > 13 || column < 2 || column > 13 ? TerrainType.Water : TerrainType.Grass;
        }
    }

    private void BuildPalette()
    {
        foreach (var terrain in Enum.GetValues<TerrainType>())
        {
            var button = new Button { Content = terrain.ToString(), Tag = terrain, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 5), Padding = new Thickness(10, 7, 10, 7), Background = new SolidColorBrush(Color.FromRgb(28, 37, 33)), Foreground = Brushes.LightGray, BorderBrush = new SolidColorBrush(Color.FromRgb(58, 71, 64)) };
            button.Click += Palette_Click;
            Palette.Items.Add(button);
        }
    }

    private void RenderSoftwareTerrain()
    {
        if (nativeViewActive || currentIsland is null || TerrainViewport.ActualWidth < 1 || TerrainViewport.ActualHeight < 1) return;
        try
        {
            TerrainViewport.Source = SoftwareTerrainRenderer.Render(currentIsland, (int)TerrainViewport.ActualWidth, (int)TerrainViewport.ActualHeight, cameraYaw, 38, cameraDistance, targetX, targetZ);
            UpdateActorMarkersOverlay();
        }
        catch
        {
            TerrainViewport.Source = currentIsland.CreatePreview();
        }
    }

    // Simple-marker actor overlay for the main 3D view: projects each actor's
    // world position with the exact camera SoftwareTerrainRenderer just used
    // (see SoftwareTerrainRenderer.TryProjectWorldPoint) and drops a clickable
    // dot on top of the rendered frame. Only meaningful while the software
    // camera is active -- the native renderer's camera/perspective isn't
    // reproduced in C#, so markers are cleared instead of drawn at a wrong
    // position while nativeViewActive. Body-mesh rendering and full script
    // editing are intentionally out of scope for this first pass; clicking a
    // marker opens a read-only attributes + script panel (see
    // ShowActorInspector) instead.
    private int? selectedActorIndex;

    // Software view only: no real body-mesh rendering exists on this path
    // (SoftwareTerrainRenderer is a from-scratch CPU rasterizer, not the
    // community engine), so a dot is the only representation available.
    // Native view uses DrawNativeActorOverlay() instead -- see its own
    // comment for why.
    private void UpdateActorMarkersOverlay()
    {
        ActorMarkerCanvas.Children.Clear();
        if (currentIsland is null || nativeViewActive) return;
        var library = nativeRenderer.RendererLibrary;
        if (library is null) return;
        var width = (int)TerrainViewport.ActualWidth;
        var height = (int)TerrainViewport.ActualHeight;
        if (width < 1 || height < 1) return;

        var count = library.GetActorCount();
        for (var i = 0; i < count; i++)
        {
            if (!library.GetActor(i, out var x, out var y, out var z, out var waypointCount)) continue;
            var world = new System.Windows.Media.Media3D.Point3D(x, y, z);
            if (!SoftwareTerrainRenderer.TryProjectWorldPoint(width, height, cameraYaw, 38, cameraDistance, targetX, targetZ, world, out var screenX, out var screenY)) continue;

            if (waypointCount > 0)
            {
                var brush = new SolidColorBrush(ActorRouteColors[i % ActorRouteColors.Length]);
                var points = new PointCollection { new Point(screenX, screenY) };
                for (var w = 0; w < waypointCount; w++)
                {
                    if (!library.GetActorWaypoint(i, w, out var wx, out var wy, out var wz)) continue;
                    var waypointWorld = new System.Windows.Media.Media3D.Point3D(wx, wy, wz);
                    if (!SoftwareTerrainRenderer.TryProjectWorldPoint(width, height, cameraYaw, 38, cameraDistance, targetX, targetZ, waypointWorld, out var wsx, out var wsy)) continue;
                    points.Add(new Point(wsx, wsy));
                }
                if (points.Count > 1)
                {
                    var route = new System.Windows.Shapes.Polyline
                    {
                        Points = points,
                        Stroke = brush,
                        StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 3, 3 },
                        Opacity = 0.85,
                        IsHitTestVisible = false,
                    };
                    ActorMarkerCanvas.Children.Add(route);
                    for (var w = 1; w < points.Count; w++)
                        ActorMarkerCanvas.Children.Add(CreateRouteFlag(points[w].X, points[w].Y, brush));
                }
            }

            if (screenX < -20 || screenX > width + 20 || screenY < -20 || screenY > height + 20) continue;

            var selected = selectedActorIndex == i;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = selected ? 14 : 10,
                Height = selected ? 14 : 10,
                Fill = selected ? Brushes.Yellow : Brushes.Red,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Cursor = Cursors.Hand,
                Tag = i,
            };
            dot.MouseLeftButtonDown += ActorMarker_MouseLeftButtonDown;
            Canvas.SetLeft(dot, screenX - dot.Width / 2);
            Canvas.SetTop(dot, screenY - dot.Height / 2);
            ActorMarkerCanvas.Children.Add(dot);
        }
    }

    // Native view: actors render with their real bodies as part of the
    // native bitmap itself (EXTFUNC.CPP's AffichageActorsZBuf(), called from
    // AffGrilleExt() during lba2_renderer_render_frame()), so no dot is
    // drawn here -- only an invisible click target per actor (so the
    // inspector panel still works) plus a highlight ring around whichever
    // actor is currently selected.
    //
    // The screen positions come from RenderNativeCamera()'s render call,
    // computed via lba2_renderer_project_point() *while still holding the
    // native renderer's lock*, immediately after the frame that they match
    // was drawn (see CommunityRendererBackend.RenderIslandDirect's
    // afterRenderBeforeUnlock parameter). Computing them here instead, after
    // the fact, was the original bug: with rapid panning, a newer in-flight
    // render could already have moved the shared native camera state before
    // this ran, so the markers projected against a different camera than
    // the one that actually produced the displayed frame -- visible as the
    // markers "swimming" a few pixels relative to the terrain.
    private List<(int Index, double ScreenX, double ScreenY)>? lastNativeActorScreens;
    private List<(int ActorIndex, List<Point> ScreenPoints)>? lastNativeActorRoutes;

    private void DrawNativeActorOverlay()
    {
        ActorMarkerCanvas.Children.Clear();
        if (!nativeViewActive || lastNativeActorScreens is null) return;
        var library = nativeRenderer.RendererLibrary;
        if (library is null) return;
        var width = (int)TerrainViewport.ActualWidth;
        var height = (int)TerrainViewport.ActualHeight;
        if (width < 1 || height < 1) return;
        var fbPtr = library.GetFramebuffer(out var fbWidth, out var fbHeight, out _);
        if (fbPtr == IntPtr.Zero || fbWidth < 1 || fbHeight < 1) return;
        var scaleX = width / (double)fbWidth;
        var scaleY = height / (double)fbHeight;

        if (lastNativeActorRoutes is not null)
        {
            foreach (var (actorIndex, screenPoints) in lastNativeActorRoutes)
            {
                var brush = new SolidColorBrush(ActorRouteColors[actorIndex % ActorRouteColors.Length]);
                var points = new PointCollection(screenPoints.Select(p => new Point(p.X * scaleX, p.Y * scaleY)));
                var route = new System.Windows.Shapes.Polyline
                {
                    Points = points,
                    Stroke = brush,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 3, 3 },
                    Opacity = 0.85,
                    IsHitTestVisible = false,
                };
                ActorMarkerCanvas.Children.Add(route);
                for (var w = 1; w < points.Count; w++)
                    ActorMarkerCanvas.Children.Add(CreateRouteFlag(points[w].X, points[w].Y, brush));
            }
        }

        foreach (var (index, sx, sy) in lastNativeActorScreens)
        {
            var screenX = sx * scaleX;
            var screenY = sy * scaleY;
            if (screenX < -20 || screenX > width + 20 || screenY < -20 || screenY > height + 20) continue;

            var selected = selectedActorIndex == index;
            var hit = new System.Windows.Shapes.Ellipse
            {
                Width = 24,
                Height = 24,
                Fill = Brushes.Transparent,
                Stroke = selected ? Brushes.Yellow : null,
                StrokeThickness = 2,
                Cursor = Cursors.Hand,
                Tag = index,
            };
            hit.MouseLeftButtonDown += ActorMarker_MouseLeftButtonDown;
            Canvas.SetLeft(hit, screenX - hit.Width / 2);
            Canvas.SetTop(hit, screenY - hit.Height / 2);
            ActorMarkerCanvas.Children.Add(hit);
        }
    }

    // Re-draws whichever overlay is active for the current view (dots for
    // software, invisible hit targets + selection ring for native) after a
    // selection change -- native re-projects nothing new here, it just
    // redraws from the last camera-accurate projection already cached in
    // lastNativeActorScreens.
    private void RefreshActorOverlayForSelection()
    {
        if (nativeViewActive) DrawNativeActorOverlay(); else UpdateActorMarkersOverlay();
    }

    private void ActorMarker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not int index) return;
        e.Handled = true;
        selectedActorIndex = index;
        RefreshActorOverlayForSelection();
        ShowActorInspector(index);
    }

    private void ShowActorInspector(int index)
    {
        var library = nativeRenderer.RendererLibrary;
        if (library is null) return;
        if (!library.GetActor(index, out var x, out var y, out var z, out var waypointCount)) return;
        library.GetActorAttributes(index, out var beta, out var body, out var anim, out var lifePoint, out var armor, out var hitForce, out var move);

        ActorTitleLabel.Text = $"Actor {index} ({waypointCount} waypoint{(waypointCount == 1 ? "" : "s")})";
        ActorPositionBox.Text = $"{x}, {y}, {z}";
        ActorFacingBox.Text = $"beta={beta}  body={body}  anim={anim}";
        ActorStatsBox.Text = $"life={lifePoint}  armor={armor}  hit={hitForce}  move={move}";
        ActorScriptBox.Text = library.GetActorScript(index);
        ActorInspectorPanel.Visibility = Visibility.Visible;
    }

    private void CloseActorInspector_Click(object sender, RoutedEventArgs e)
    {
        selectedActorIndex = null;
        ActorInspectorPanel.Visibility = Visibility.Collapsed;
        RefreshActorOverlayForSelection();
    }

    // Islands are a 16x16 grid of cubes, each spanning 32768 world units
    // (matches HOLO.H's SCE constant on the native side); a cube whose
    // top byte (CubeAt & 0x7F) is zero has no loaded data, so panning must
    // stop there instead of handing the renderer a position it can't load.
    private bool IsWorldPositionOnIsland(double worldX, double worldZ)
    {
        if (currentIsland is null) return true;
        var cubeX = (int)Math.Floor(worldX / 32768.0);
        var cubeZ = (int)Math.Floor(worldZ / 32768.0);
        if (cubeX < 0 || cubeX > 15 || cubeZ < 0 || cubeZ > 15) return false;
        return (currentIsland.CubeAt(cubeX, cubeZ) & 0x7F) != 0;
    }

    private (int, int)? FindFirstPresentCube()
    {
        if (currentIsland is null) return null;
        for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
                if ((currentIsland.CubeAt(x, y) & 0x7F) != 0) return (x, y);
        return null;
    }

    // Applies each axis independently so a diagonal drag that would leave
    // the mapped island still slides cleanly along whichever axis remains
    // valid, instead of the whole pan getting stuck at the boundary.
    private void TryPan(double dx, double dz)
    {
        var newX = targetX + dx;
        var newZ = targetZ + dz;
        if (IsWorldPositionOnIsland(newX, targetZ)) targetX = newX;
        if (IsWorldPositionOnIsland(targetX, newZ)) targetZ = newZ;
        UpdateMinimapMarker();
        SyncPanScrollBars();
    }

    // The pan scrollbars use a fixed 0..16*32768 range (XAML) since every
    // island is a 16x16 grid of 32768-unit cubes (HOLO.H's SCE). Scroll (not
    // ValueChanged) is used on the scrollbars so setting .Value here to
    // reflect a pan that came from elsewhere (mouse drag, keyboard, minimap
    // click) doesn't loop back into the scrollbar's own drag handler.
    private void SyncPanScrollBars()
    {
        PanHorizontalScrollBar.Value = targetX;
        PanVerticalScrollBar.Value = targetZ;
    }

    private void PanHorizontalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (currentIsland is null) return;
        if (IsWorldPositionOnIsland(e.NewValue, targetZ)) targetX = e.NewValue;
        else PanHorizontalScrollBar.Value = targetX;
        UpdateMinimapMarker();
        if (nativeViewActive) RenderNativeCamera(); else RenderSoftwareTerrain();
    }

    private void PanVerticalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (currentIsland is null) return;
        if (IsWorldPositionOnIsland(targetX, e.NewValue)) targetZ = e.NewValue;
        else PanVerticalScrollBar.Value = targetZ;
        UpdateMinimapMarker();
        if (nativeViewActive) RenderNativeCamera(); else RenderSoftwareTerrain();
    }

    // TopDownMapRenderer draws at this many image pixels per 512-unit terrain
    // cell (see TopDownMapRenderer.CellsPerCube); the minimap crops to the
    // present-cube bounding box (plus one cube of padding) instead of the
    // full 16x16 grid, so most of the displayed area is actual island
    // instead of empty sea. These fields describe that crop in the same
    // pixel space so marker placement and click-to-jump can convert between
    // world units and minimap pixel coordinates.
    private const int MinimapPixelsPerCell = 4;
    private const double MinimapWorldUnitsPerPixel = 512.0 / MinimapPixelsPerCell;
    private int minimapCropOffsetXPixels;
    private int minimapCropOffsetYPixels;

    private void UpdateMinimapMarker()
    {
        MinimapMarkerCanvas.Children.Clear();
        if (currentIsland is null || MinimapImage.Source is null) return;
        var px = targetX / MinimapWorldUnitsPerPixel - minimapCropOffsetXPixels;
        var pz = targetZ / MinimapWorldUnitsPerPixel - minimapCropOffsetYPixels;
        const double markerSize = 10;
        var marker = new System.Windows.Shapes.Ellipse
        {
            Width = markerSize,
            Height = markerSize,
            Fill = Brushes.Yellow,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
        };
        Canvas.SetLeft(marker, px - markerSize / 2);
        Canvas.SetTop(marker, pz - markerSize / 2);
        MinimapMarkerCanvas.Children.Add(marker);
    }

    // The minimap crops to the island's bounding box but that can still be
    // larger than the small on-screen viewport (hence the scrollbars); center
    // the view on the current camera position so opening an island or
    // jumping via a minimap click doesn't leave the marker scrolled off screen.
    private void CenterMinimapOnMarker()
    {
        if (MinimapImage.Source is null) return;
        var px = targetX / MinimapWorldUnitsPerPixel - minimapCropOffsetXPixels;
        var pz = targetZ / MinimapWorldUnitsPerPixel - minimapCropOffsetYPixels;
        MinimapScrollViewer.ScrollToHorizontalOffset(px - MinimapScrollViewer.ViewportWidth / 2);
        MinimapScrollViewer.ScrollToVerticalOffset(pz - MinimapScrollViewer.ViewportHeight / 2);
    }

    // Renders the island top-down through the community engine itself
    // (CommunityRendererBackend.RenderIslandTopDown -- a per-cube stitch
    // through the same texture/lighting pipeline the main 3D view uses,
    // since the classic exterior renderer only ever has one cube's terrain
    // loaded at a time and so can't do it in a single wide shot) off the UI
    // thread, crops to the island's present-cube bounding box (so the
    // minimap shows the island zoomed in instead of mostly empty sea), and
    // swaps it into the minimap once ready. Falls back to the from-scratch
    // CPU rasterizer (TopDownMapRenderer) only if the native renderer isn't
    // available at all. Called after every island load; also the hook to
    // call again once terrain painting actually mutates currentIsland's
    // data, so the minimap can be refreshed live instead of only reflecting
    // what was true at load time.
    private void RegenerateMinimap()
    {
        if (currentIsland is null) return;
        var island = currentIsland;
        var request = Interlocked.Increment(ref minimapRequest);
        var islandName = Path.GetFileNameWithoutExtension(activeFile);
        var useNative = nativeRenderer.DirectRendererReady;

        var (minX, minY, maxX, maxY) = island.PresentCubeBounds;
        const int padding = 1;
        minX = Math.Max(0, minX - padding);
        minY = Math.Max(0, minY - padding);
        maxX = Math.Min(15, maxX + padding);
        maxY = Math.Min(15, maxY + padding);
        var offsetX = minX * TopDownMapRenderer.CellsPerCube * MinimapPixelsPerCell;
        var offsetY = minY * TopDownMapRenderer.CellsPerCube * MinimapPixelsPerCell;

        Task.Run(() =>
        {
            if (useNative)
            {
                try
                {
                    var presentCubes = new List<(int CubeX, int CubeY)>();
                    for (var cy = minY; cy <= maxY; cy++)
                    for (var cx = minX; cx <= maxX; cx++)
                        if ((island.CubeAt(cx, cy) & 0x7F) != 0)
                            presentCubes.Add((cx, cy));
                    var native = nativeRenderer.RenderIslandTopDown(islandName, palette, presentCubes, minX, minY, maxX - minX + 1, maxY - minY + 1);
                    if (native is not null) return native;
                }
                catch
                {
                    // Fall through to the CPU rasterizer below. A thrown
                    // exception here (as opposed to RenderIslandTopDown's own
                    // null-on-failure paths) previously faulted this whole
                    // Task, which this same method's ContinueWith treats as
                    // "leave the minimap as whatever it last showed" -- on a
                    // fresh island load with nothing shown yet, that reads as
                    // the minimap going solid black instead of falling back.
                }
            }

            // Fallback: no native renderer available (this island failed to
            // render through it, or it threw) -- the CPU rasterizer instead
            // of leaving the minimap blank.
            var full = TopDownMapRenderer.Render(island, MinimapPixelsPerCell);
            var cropWidth = (maxX - minX + 1) * TopDownMapRenderer.CellsPerCube * MinimapPixelsPerCell;
            var cropHeight = (maxY - minY + 1) * TopDownMapRenderer.CellsPerCube * MinimapPixelsPerCell;
            var cropped = new CroppedBitmap(full, new Int32Rect(offsetX, offsetY, cropWidth, cropHeight));
            cropped.Freeze();
            return (BitmapSource)cropped;
        }).ContinueWith(task =>
        {
            if (task.IsCanceled || request != minimapRequest) return;
            if (task.IsFaulted) return;
            Dispatcher.Invoke(() =>
            {
                if (request != minimapRequest) return;
                minimapCropOffsetXPixels = offsetX;
                minimapCropOffsetYPixels = offsetY;
                MinimapImage.Source = task.Result;
                UpdateMinimapMarker();
                CenterMinimapOnMarker();
                // Re-draw with the fresh crop offsets in case this finished
                // after RenderNativeCamera() already drew actors using the
                // previous island's (now stale) offsets.
                if (actorMarkersIsland == Path.GetFileNameWithoutExtension(activeFile)) DrawActorMarkers();
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void SkyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = SkyCheckBox.IsChecked == true;
        nativeRenderer.RendererLibrary?.SetDrawSky(enabled);
        if (nativeViewActive) RenderNativeCamera();
    }

    private void Minimap_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (currentIsland is null || MinimapImage.Source is null) return;
        var grid = (Grid)sender;
        var point = e.GetPosition(grid);
        var worldX = (minimapCropOffsetXPixels + point.X) * MinimapWorldUnitsPerPixel;
        var worldZ = (minimapCropOffsetYPixels + point.Y) * MinimapWorldUnitsPerPixel;
        if (!IsWorldPositionOnIsland(worldX, worldZ)) return;
        targetX = worldX;
        targetZ = worldZ;
        UpdateMinimapMarker();
        SyncPanScrollBars();
        if (nativeViewActive) RenderNativeCamera(); else RenderSoftwareTerrain();
    }

    // 100% is each view's own default distance (both happen to default to
    // 30000) so switching between native and software mid-session doesn't
    // jump the displayed percentage; zooming in (smaller distance) reads as
    // >100%, matching how "zoom" reads on a camera or a document viewer.
    private const double DefaultCameraDistance = 30000;
    private void UpdateZoomLabel()
    {
        var distance = nativeViewActive ? nativeDistance : cameraDistance;
        ZoomLabel.Text = $"{Math.Round(DefaultCameraDistance / distance * 100)}%";
    }

    private void ZoomLabel_GotFocus(object sender, RoutedEventArgs e) => ZoomLabel.SelectAll();

    private void ZoomLabel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyZoomFromTextBox();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ZoomLabel_LostFocus(object sender, RoutedEventArgs e) => ApplyZoomFromTextBox();

    private void ApplyZoomFromTextBox()
    {
        var text = ZoomLabel.Text.Trim().TrimEnd('%');
        if (!double.TryParse(text, out var percent) || percent <= 0)
        {
            UpdateZoomLabel();
            return;
        }
        var distance = DefaultCameraDistance / (percent / 100.0);
        if (nativeViewActive)
        {
            nativeDistance = (int)Math.Clamp(distance, 3000, 50000);
            RenderNativeCamera();
        }
        else
        {
            cameraDistance = Math.Clamp(distance, 12000, 120000);
            RenderSoftwareTerrain();
        }
        UpdateZoomLabel();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => LoadIsland(Path.Combine(gameRoot, activeFile));
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { if (nativeViewActive) { nativeDistance = Math.Max(3000, nativeDistance - 4000); RenderNativeCamera(); } else { cameraDistance = Math.Max(12000, cameraDistance - 4000); RenderSoftwareTerrain(); } UpdateZoomLabel(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (nativeViewActive) { nativeDistance = Math.Min(50000, nativeDistance + 4000); RenderNativeCamera(); } else { cameraDistance = Math.Min(120000, cameraDistance + 4000); RenderSoftwareTerrain(); } UpdateZoomLabel(); }
    private void TerrainViewport_MouseDown(object sender, MouseButtonEventArgs e) { orbiting = e.ChangedButton == MouseButton.Left; lastMousePosition = e.GetPosition(TerrainViewport); TerrainViewport.CaptureMouse(); }
    private void TerrainViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!orbiting) return;
        var point = e.GetPosition(TerrainViewport);
        var dx = point.X - lastMousePosition.X;
        var dy = point.Y - lastMousePosition.Y;
        lastMousePosition = point;
        if (nativeViewActive)
        {
            nativeBeta += (int)Math.Clamp(dx * 3, -900, 900); nativeAlpha += (int)Math.Clamp(dy * 3, -900, 900);
            RenderNativeCamera();
        }
        else
        {
            cameraYaw += dx * .35;
            RenderSoftwareTerrain();
        }
    }
    private void TerrainViewport_MouseUp(object sender, MouseButtonEventArgs e) { orbiting = false; TerrainViewport.ReleaseMouseCapture(); }
    private void TerrainViewport_MouseWheel(object sender, MouseWheelEventArgs e) { if (nativeViewActive) { nativeDistance = Math.Clamp(nativeDistance - (e.Delta > 0 ? 1200 : -1200), 3000, 50000); RenderNativeCamera(); } else { cameraDistance = Math.Clamp(cameraDistance - e.Delta * 40, 12000, 120000); RenderSoftwareTerrain(); } UpdateZoomLabel(); }
    private void RenderNativeCamera()
    {
        if (!nativeViewActive) return;
        // RenderIslandTopDown() (the minimap) turns sky off natively for its
        // own straight-down snapshots and has no reason to turn it back on
        // afterward -- it doesn't know what the checkbox says. Read it here,
        // on the UI thread (the render loop below runs on a background
        // thread and can't touch a WPF control directly), so every render
        // reasserts the checkbox's actual state instead of trusting
        // whatever the native flag happened to be left at.
        desiredSkyEnabled = SkyCheckBox.IsChecked == true;
        nativeRenderCancellation ??= new CancellationTokenSource();
        var token = nativeRenderCancellation.Token;
        lock (nativeRenderGate)
        {
            nativeRenderDirty = true;
            if (nativeRenderInFlight) return;
            nativeRenderInFlight = true;
        }
        // Runs as a loop rather than spawning one task per call: mouse-drag
        // and wheel events fire dozens of times a second, and at
        // wideRadius>=1 every render reloads/redraws up to 25 cubes
        // (AffGrilleExtWide) through the single shared directRenderLock --
        // spawning a full render per event just queued them up behind that
        // lock, so the view kept grinding through already-superseded frames
        // long after the mouse had moved on (reported as "slow to react" at
        // zoom <=150%, i.e. wideRadius>=1). Only one render is ever in
        // flight; a call that arrives mid-render just sets nativeRenderDirty
        // so the loop goes around again with the latest field values,
        // instead of a second task queuing its own redundant pass.
        _ = Task.Run(() => RunNativeRenderLoop(token), token);
    }

    private void RunNativeRenderLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            lock (nativeRenderGate) { nativeRenderDirty = false; }

            var islandName = Path.GetFileNameWithoutExtension(activeFile);
            var library = nativeRenderer.RendererLibrary;
            library?.SetDrawSky(desiredSkyEnabled);
            // Same reasoning as SetDrawSky just above: the minimap turns sea
            // off for its own top-down snapshots and never turns it back on,
            // since it has no reason to know the main view wants it --
            // reassert on every render so a minimap regeneration can't leave
            // the main view's sea silently disabled.
            library?.SetDrawSea(true);
            // Past a certain zoom-out level, a single cube's terrain visibly
            // runs out before the horizon does -- AffGrilleExtWide loads and
            // draws neighboring cubes into the same frame to cover that, at
            // the cost of roughly (2*radius+1)^2 cube loads instead of 1, so
            // it's only worth asking for once the camera is far back enough
            // to need it. The actor/waypoint overlay filter below uses this
            // same radius so it shows exactly the actors sitting on terrain
            // this frame actually drew, whichever cube each one happens to
            // be in.
            var wideRadius = nativeDistance < 20000 ? 0 : nativeDistance < 35000 ? 1 : 2;
            var currentCubeX = (int)Math.Floor(targetX / 32768.0);
            var currentCubeY = (int)Math.Floor(targetZ / 32768.0);
            List<(int, double, double)>? projected = null;
            List<(int ActorIndex, List<Point> ScreenPoints)>? projectedRoutes = null;

            var bitmap = nativeRenderer.RenderIslandDirect(islandName, palette, (int)targetX, (int)targetY, (int)targetZ, nativeAlpha, nativeBeta, nativeGamma, nativeDistance,
                wideRadiusCubes: wideRadius,
                afterRenderBeforeUnlock: () =>
                {
                    if (library is null) return;
                    var count = library.GetActorCount();
                    var list = new List<(int, double, double)>(count);
                    var routes = new List<(int, List<Point>)>();
                    for (var i = 0; i < count; i++)
                    {
                        if (!library.GetActor(i, out var x, out var y, out var z, out var waypointCount)) continue;
                        if (Math.Abs((int)Math.Floor(x / 32768.0) - currentCubeX) > wideRadius || Math.Abs((int)Math.Floor(z / 32768.0) - currentCubeY) > wideRadius) continue;
                        if (!library.ProjectPoint(x, y, z, out var sx, out var sy)) continue;
                        list.Add((i, sx, sy));

                        if (waypointCount <= 0) continue;
                        var points = new List<Point> { new(sx, sy) };
                        for (var w = 0; w < waypointCount; w++)
                        {
                            if (!library.GetActorWaypoint(i, w, out var wx, out var wy, out var wz)) continue;
                            // A waypoint outside the drawn radius would be just
                            // as un-pinned as an actor would be -- truncate the
                            // route there rather than drawing a segment into
                            // empty space.
                            if (Math.Abs((int)Math.Floor(wx / 32768.0) - currentCubeX) > wideRadius || Math.Abs((int)Math.Floor(wz / 32768.0) - currentCubeY) > wideRadius) break;
                            if (!library.ProjectPoint(wx, wy, wz, out var wsx, out var wsy)) continue;
                            points.Add(new Point(wsx, wsy));
                        }
                        if (points.Count > 1) routes.Add((i, points));
                    }
                    projected = list;
                    projectedRoutes = routes;
                });

            var stopLoop = false;
            if (!token.IsCancellationRequested)
            {
                Dispatcher.Invoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (bitmap is null)
                    {
                        // The current world position has no loaded cube data
                        // (should only happen if a caller bypasses TryPan's
                        // clamping). Fall back to the movable CPU rasterizer
                        // instead of leaving a frozen frame on screen, and
                        // stop this loop -- nativeViewActive is now false, so
                        // there's nothing left for it to render.
                        nativeViewActive = false;
                        DocumentSummary.Text = DocumentSummary.Text.Replace("native 3D", "software 3D (native unavailable)");
                        RenderSoftwareTerrain();
                        stopLoop = true;
                        return;
                    }
                    TerrainViewport.Source = bitmap;
                    if (actorMarkersIsland != islandName)
                    {
                        actorMarkersIsland = islandName;
                        DrawActorMarkers();
                    }
                    lastNativeActorScreens = projected;
                    lastNativeActorRoutes = projectedRoutes;
                    DrawNativeActorOverlay();
                });
            }

            lock (nativeRenderGate)
            {
                if (stopLoop || !nativeRenderDirty || token.IsCancellationRequested)
                {
                    nativeRenderInFlight = false;
                    return;
                }
            }
        }

        lock (nativeRenderGate) { nativeRenderInFlight = false; }
    }

    // Actors are scanned natively once per lba2_renderer_load_island() call
    // (RENDERER_ACTORS.CPP walks every exterior scene on the island); redrawn
    // here only when the island actually changes, not on every camera pan,
    // since the underlying data can't have changed either.
    private string? actorMarkersIsland;

    // A fixed, hand-picked palette of visually distinct hues (avoiding the
    // minimap's own greens/browns/blues) so two actors whose routes cross or
    // run parallel stay tellable apart -- a single uniform cyan for every
    // route (the previous behavior) made that impossible whenever more than
    // one NPC patrolled the same area. Cycles for islands with more actors
    // than colors; two actors then share a color but that's still far
    // better than every actor sharing one.
    private static readonly Color[] ActorRouteColors =
    {
        Colors.Red, Colors.Orange, Colors.Yellow, Colors.Magenta,
        Colors.DeepSkyBlue, Colors.Lime, Colors.HotPink, Colors.White,
        Colors.Violet, Colors.Gold, Colors.Cyan, Colors.OrangeRed,
    };

    private void DrawActorMarkers()
    {
        MinimapActorCanvas.Children.Clear();
        var library = nativeRenderer.RendererLibrary;
        if (library is null || MinimapImage.Source is null) return;
        var count = library.GetActorCount();
        for (var i = 0; i < count; i++)
        {
            if (!library.GetActor(i, out var x, out _, out var z, out var waypointCount)) continue;
            var px = x / MinimapWorldUnitsPerPixel - minimapCropOffsetXPixels;
            var pz = z / MinimapWorldUnitsPerPixel - minimapCropOffsetYPixels;
            var brush = new SolidColorBrush(ActorRouteColors[i % ActorRouteColors.Length]);

            if (waypointCount > 0)
            {
                var points = new PointCollection { new Point(px, pz) };
                for (var w = 0; w < waypointCount; w++)
                {
                    if (!library.GetActorWaypoint(i, w, out var wx, out _, out var wz)) continue;
                    var wpx = wx / MinimapWorldUnitsPerPixel - minimapCropOffsetXPixels;
                    var wpz = wz / MinimapWorldUnitsPerPixel - minimapCropOffsetYPixels;
                    points.Add(new Point(wpx, wpz));
                    MinimapActorCanvas.Children.Add(CreateRouteFlag(wpx, wpz, brush));
                }
                var route = new System.Windows.Shapes.Polyline
                {
                    Points = points,
                    Stroke = brush,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    Opacity = 0.85,
                };
                MinimapActorCanvas.Children.Add(route);
            }

            var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = brush, Stroke = Brushes.Black, StrokeThickness = 0.5 };
            Canvas.SetLeft(dot, px - 2.5);
            Canvas.SetTop(dot, pz - 2.5);
            MinimapActorCanvas.Children.Add(dot);
        }
    }

    // A small pole-and-pennant flag glyph (not a literal render of the
    // actor's body -- there's no cheap way to shrink the native 3D body
    // renderer added for the main view down to a minimap-scale icon) marking
    // one track waypoint, in the same color as that actor's route line and
    // start dot so a glance at a cluster of flags says which NPC's patrol
    // they belong to.
    private static System.Windows.Shapes.Path CreateRouteFlag(double x, double y, Brush brush)
    {
        const double poleHeight = 8, pennantWidth = 5, pennantHeight = 4;
        var geometry = new PathGeometry();
        var pole = new LineSegment(new Point(x, y - poleHeight), true);
        var poleFigure = new PathFigure(new Point(x, y), new PathSegment[] { pole }, false);
        geometry.Figures.Add(poleFigure);
        var pennantFigure = new PathFigure(
            new Point(x, y - poleHeight),
            new PathSegment[]
            {
                new LineSegment(new Point(x + pennantWidth, y - poleHeight + pennantHeight / 2), true),
                new LineSegment(new Point(x, y - poleHeight + pennantHeight), true),
            },
            true);
        geometry.Figures.Add(pennantFigure);
        return new System.Windows.Shapes.Path { Data = geometry, Stroke = Brushes.Black, StrokeThickness = 0.5, Fill = brush };
    }
    private void TerrainViewport_SizeChanged(object sender, SizeChangedEventArgs e) { if (nativeViewActive) DrawNativeActorOverlay(); else RenderSoftwareTerrain(); }
    private void Window_Loaded(object sender, RoutedEventArgs e) { }
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        nativeRenderCancellation?.Cancel();
        nativeRenderer.ShutdownDirectRenderer();
    }
    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        var step = (nativeViewActive ? nativeDistance : cameraDistance) * .04;
        double dx = 0, dz = 0;
        if (e.Key == Key.Left) dx = -step;
        else if (e.Key == Key.Right) dx = step;
        else if (e.Key == Key.Up) dz = -step;
        else if (e.Key == Key.Down) dz = step;
        else return;
        TryPan(dx, dz);
        if (nativeViewActive) RenderNativeCamera(); else RenderSoftwareTerrain();
        e.Handled = true;
    }
    private void Palette_Click(object sender, RoutedEventArgs e) { selectedTerrain = (TerrainType)((Button)sender).Tag; SelectedLabel.Text = $"{selectedTerrain} / selected brush"; }
    private void IslandList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IslandList.SelectedItem is string file) LoadIsland(Path.Combine(gameRoot, file)); }
    private void SceneList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (SceneList.SelectedItem is string scene) { FileLabel.Text = $"●  {scene} / SCENE.HQR"; DocumentTitle.Text = scene; DocumentSummary.Text = "Native SCENE.HQR record / object and zone data"; } }
    private void Open_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "LBA2 islands (*.ILE)|*.ILE|All files (*.*)|*.*", InitialDirectory = gameRoot }; if (dialog.ShowDialog() == true) LoadIsland(dialog.FileName); }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this };
        if (dialog.ShowDialog() != true || !dialog.GameDirectoryChanged) return;

        gameRoot = EditorSettings.Current.GameDirectory;
        nativeRenderer.SetGameDirectory(gameRoot);
        actorMarkersIsland = null;
        PopulateAssetLists();
        if (Directory.Exists(gameRoot)) LoadIsland(Path.Combine(gameRoot, activeFile));
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Export_Click(sender, e);
    private void Export_Click(object sender, RoutedEventArgs e) { var dialog = new SaveFileDialog { Filter = "JSON draft (*.json)|*.json", FileName = Path.GetFileNameWithoutExtension(activeFile) + ".json" }; if (dialog.ShowDialog() != true) return; var draft = new { format = "lba2-ile-draft", width = 16, height = 16, tiles = fallbackTiles }; File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true })); }

    private enum TerrainType { Grass, Sand, Water, Stone, Dirt }
}
