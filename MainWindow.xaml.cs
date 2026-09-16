using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private bool panning;
    private bool nativeViewActive;
    private CancellationTokenSource? nativeRenderCancellation;
    private int nativeRenderRequest;
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
            LoadIsland(Path.Combine(gameRoot, activeFile));
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

            var preview = currentIsland.CreatePreview();
            TerrainViewport.Source = preview;
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
                DocumentSummary.Text += " / software 3D";
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RenderSoftwareTerrain));
            }
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
        }
        catch
        {
            TerrainViewport.Source = currentIsland.CreatePreview();
        }
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

    // Renders the full-resolution top-down map (TopDownMapRenderer) off the UI
    // thread, crops it to the island's present-cube bounding box (so the
    // minimap shows the island zoomed in instead of mostly empty sea), and
    // swaps it into the minimap once ready. Called after every island load;
    // also the hook to call again once terrain painting actually mutates
    // currentIsland's data, so the minimap can be refreshed live instead of
    // only reflecting what was true at load time.
    private void RegenerateMinimap()
    {
        if (currentIsland is null) return;
        var island = currentIsland;
        var request = Interlocked.Increment(ref minimapRequest);
        Task.Run(() =>
        {
            var full = TopDownMapRenderer.Render(island, MinimapPixelsPerCell);
            var (minX, minY, maxX, maxY) = island.PresentCubeBounds;
            const int padding = 1;
            minX = Math.Max(0, minX - padding);
            minY = Math.Max(0, minY - padding);
            maxX = Math.Min(15, maxX + padding);
            maxY = Math.Min(15, maxY + padding);
            var offsetX = minX * TopDownMapRenderer.CellsPerCube * MinimapPixelsPerCell;
            var offsetY = minY * TopDownMapRenderer.CellsPerCube * MinimapPixelsPerCell;
            var cropWidth = (maxX - minX + 1) * TopDownMapRenderer.CellsPerCube * MinimapPixelsPerCell;
            var cropHeight = (maxY - minY + 1) * TopDownMapRenderer.CellsPerCube * MinimapPixelsPerCell;
            var cropped = new CroppedBitmap(full, new Int32Rect(offsetX, offsetY, cropWidth, cropHeight));
            cropped.Freeze();
            return (Bitmap: (BitmapSource)cropped, offsetX, offsetY);
        }).ContinueWith(task =>
        {
            if (task.IsCanceled || request != minimapRequest) return;
            if (task.IsFaulted) return;
            Dispatcher.Invoke(() =>
            {
                if (request != minimapRequest) return;
                minimapCropOffsetXPixels = task.Result.offsetX;
                minimapCropOffsetYPixels = task.Result.offsetY;
                MinimapImage.Source = task.Result.Bitmap;
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
        if (nativeViewActive) RenderNativeCamera(); else RenderSoftwareTerrain();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => LoadIsland(Path.Combine(gameRoot, activeFile));
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { if (nativeViewActive) { nativeDistance = Math.Max(3000, nativeDistance - 4000); RenderNativeCamera(); } else { cameraDistance = Math.Max(12000, cameraDistance - 4000); RenderSoftwareTerrain(); } }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (nativeViewActive) { nativeDistance = Math.Min(50000, nativeDistance + 4000); RenderNativeCamera(); } else { cameraDistance = Math.Min(120000, cameraDistance + 4000); RenderSoftwareTerrain(); } }
    private void TerrainViewport_MouseDown(object sender, MouseButtonEventArgs e) { orbiting = e.ChangedButton == MouseButton.Left; panning = e.ChangedButton is MouseButton.Middle or MouseButton.Right; lastMousePosition = e.GetPosition(TerrainViewport); TerrainViewport.CaptureMouse(); }
    private void TerrainViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!orbiting && !panning) return;
        var point = e.GetPosition(TerrainViewport);
        var dx = point.X - lastMousePosition.X;
        var dy = point.Y - lastMousePosition.Y;
        lastMousePosition = point;
        if (nativeViewActive)
        {
            if (orbiting) { nativeBeta += (int)Math.Clamp(dx * 3, -900, 900); nativeAlpha += (int)Math.Clamp(dy * 3, -900, 900); }
            else TryPan(-dx * nativeDistance / 700, -dy * nativeDistance / 700);
            RenderNativeCamera();
        }
        else
        {
            if (orbiting) cameraYaw += dx * .35;
            else TryPan(-dx * cameraDistance / 700, -dy * cameraDistance / 700);
            RenderSoftwareTerrain();
        }
    }
    private void TerrainViewport_MouseUp(object sender, MouseButtonEventArgs e) { orbiting = false; panning = false; TerrainViewport.ReleaseMouseCapture(); }
    private void TerrainViewport_MouseWheel(object sender, MouseWheelEventArgs e) { if (nativeViewActive) { nativeDistance = Math.Clamp(nativeDistance - (e.Delta > 0 ? 1200 : -1200), 3000, 50000); RenderNativeCamera(); } else { cameraDistance = Math.Clamp(cameraDistance - e.Delta * 40, 12000, 120000); RenderSoftwareTerrain(); } }
    private void RenderNativeCamera()
    {
        if (!nativeViewActive) return;
        nativeRenderCancellation?.Cancel();
        nativeRenderCancellation = new CancellationTokenSource();
        var request = Interlocked.Increment(ref nativeRenderRequest);
        var islandName = Path.GetFileNameWithoutExtension(activeFile);
        var token = nativeRenderCancellation.Token;
        _ = Task.Run(() => nativeRenderer.RenderIslandDirect(islandName, palette, (int)targetX, (int)targetY, (int)targetZ, nativeAlpha, nativeBeta, nativeGamma, nativeDistance), token).ContinueWith(task =>
        {
            if (task.IsCanceled || task.IsFaulted || token.IsCancellationRequested || request != nativeRenderRequest) return;
            Dispatcher.Invoke(() =>
            {
                if (request != nativeRenderRequest) return;
                if (task.Result is null)
                {
                    // The current world position has no loaded cube data (should
                    // only happen if a caller bypasses TryPan's clamping). Fall
                    // back to the movable CPU rasterizer instead of leaving a
                    // frozen frame on screen.
                    nativeViewActive = false;
                    DocumentSummary.Text = DocumentSummary.Text.Replace("native 3D", "software 3D (native unavailable)");
                    RenderSoftwareTerrain();
                    return;
                }
                TerrainViewport.Source = task.Result;
                if (actorMarkersIsland != islandName)
                {
                    actorMarkersIsland = islandName;
                    DrawActorMarkers();
                }
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Actors are scanned natively once per lba2_renderer_load_island() call
    // (RENDERER_ACTORS.CPP walks every exterior scene on the island); redrawn
    // here only when the island actually changes, not on every camera pan,
    // since the underlying data can't have changed either.
    private string? actorMarkersIsland;

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

            if (waypointCount > 0)
            {
                var points = new PointCollection { new Point(px, pz) };
                for (var w = 0; w < waypointCount; w++)
                {
                    if (!library.GetActorWaypoint(i, w, out var wx, out _, out var wz)) continue;
                    points.Add(new Point(wx / MinimapWorldUnitsPerPixel - minimapCropOffsetXPixels, wz / MinimapWorldUnitsPerPixel - minimapCropOffsetYPixels));
                }
                var route = new System.Windows.Shapes.Polyline
                {
                    Points = points,
                    Stroke = Brushes.Cyan,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    Opacity = 0.8,
                };
                MinimapActorCanvas.Children.Add(route);
            }

            var dot = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = Brushes.Red };
            Canvas.SetLeft(dot, px - 2);
            Canvas.SetTop(dot, pz - 2);
            MinimapActorCanvas.Children.Add(dot);
        }
    }
    private void TerrainViewport_SizeChanged(object sender, SizeChangedEventArgs e) => RenderSoftwareTerrain();
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
