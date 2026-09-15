using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;

namespace LBA2LevelEditor;

public partial class MainWindow : Window
{
    private readonly string gameRoot = @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
    private readonly CommunityRendererBackend nativeRenderer = new();
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
    private double targetZ;
    private Point lastMousePosition;
    private bool orbiting;
    private bool panning;
    private double pendingDx;
    private double pendingDy;
    private bool nativeViewActive;
    private readonly TranslateTransform nativeDragTransform = new();
    private CancellationTokenSource? nativeRenderCancellation;
    private int nativeRenderRequest;
    private byte[] palette = Array.Empty<byte>();
    private byte[] shadeTable = Array.Empty<byte>();
    private int shadeLevel;

    public MainWindow()
    {
        InitializeComponent();
        TerrainViewport.RenderTransform = nativeDragTransform;
        Focusable = true;
        KeyDown += MainWindow_KeyDown;
        SeedFallbackMap();
        PopulateAssetLists();
        BuildPalette();
        LoadIsland(Path.Combine(gameRoot, activeFile));
    }

    private void PopulateAssetLists()
    {
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

            nativeViewActive = false;
            DocumentSummary.Text += " / software 3D";
            TerrainViewport.Source = currentIsland.CreatePreview();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(RenderSoftwareTerrain));
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

    private void Reset_Click(object sender, RoutedEventArgs e) => LoadIsland(Path.Combine(gameRoot, activeFile));
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { cameraDistance = Math.Max(12000, cameraDistance - 4000); RenderSoftwareTerrain(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { cameraDistance = Math.Min(120000, cameraDistance + 4000); RenderSoftwareTerrain(); }
    private void TerrainViewport_MouseDown(object sender, MouseButtonEventArgs e) { orbiting = e.ChangedButton == MouseButton.Left; panning = e.ChangedButton is MouseButton.Middle or MouseButton.Right; pendingDx = 0; pendingDy = 0; lastMousePosition = e.GetPosition(TerrainViewport); TerrainViewport.CaptureMouse(); }
    private void TerrainViewport_MouseMove(object sender, MouseEventArgs e) { if (!orbiting && !panning) return; var point = e.GetPosition(TerrainViewport); var dx = point.X - lastMousePosition.X; var dy = point.Y - lastMousePosition.Y; pendingDx += dx; pendingDy += dy; if (!nativeViewActive) { if (orbiting) cameraYaw += dx * .35; else { targetX -= dx * cameraDistance / 700; targetZ -= dy * cameraDistance / 700; } RenderSoftwareTerrain(); } else { nativeDragTransform.X += dx; nativeDragTransform.Y += dy; } lastMousePosition = point; }
    private void TerrainViewport_MouseUp(object sender, MouseButtonEventArgs e) { orbiting = false; panning = false; TerrainViewport.ReleaseMouseCapture(); if (nativeViewActive && (Math.Abs(pendingDx) > 1 || Math.Abs(pendingDy) > 1)) { nativeBeta += (int)Math.Clamp(pendingDx * 3, -900, 900); nativeAlpha += (int)Math.Clamp(pendingDy * 3, -900, 900); RenderNativeCamera(); } }
    private void TerrainViewport_MouseWheel(object sender, MouseWheelEventArgs e) { if (nativeViewActive) { nativeDistance = Math.Clamp(nativeDistance - (e.Delta > 0 ? 1200 : -1200), 3000, 50000); RenderNativeCamera(); } else { cameraDistance = Math.Clamp(cameraDistance - e.Delta * 40, 12000, 120000); RenderSoftwareTerrain(); } }
    private void RenderNativeCamera()
    {
        if (!nativeViewActive) return;
        nativeRenderCancellation?.Cancel();
        nativeRenderCancellation = new CancellationTokenSource();
        var request = Interlocked.Increment(ref nativeRenderRequest);
        var islandName = Path.GetFileNameWithoutExtension(activeFile);
        var token = nativeRenderCancellation.Token;
        _ = Task.Run(() => nativeRenderer.RenderIslandDirect(islandName, palette, nativeAlpha, nativeBeta, nativeGamma, nativeDistance), token).ContinueWith(task =>
        {
            if (task.IsCanceled || task.IsFaulted || token.IsCancellationRequested || request != nativeRenderRequest) return;
            Dispatcher.Invoke(() =>
            {
                if (task.Result is null || request != nativeRenderRequest) return;
                TerrainViewport.Source = task.Result;
                nativeDragTransform.X = 0;
                nativeDragTransform.Y = 0;
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void TerrainViewport_SizeChanged(object sender, SizeChangedEventArgs e) => RenderSoftwareTerrain();
    private void Window_Loaded(object sender, RoutedEventArgs e) { }
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        nativeRenderCancellation?.Cancel();
        nativeRenderer.ShutdownDirectRenderer();
    }
    private void MainWindow_KeyDown(object sender, KeyEventArgs e) { if (nativeViewActive) return; var step = cameraDistance * .04; if (e.Key == Key.Left) targetX -= step; else if (e.Key == Key.Right) targetX += step; else if (e.Key == Key.Up) targetZ -= step; else if (e.Key == Key.Down) targetZ += step; else return; e.Handled = true; }
    private void Palette_Click(object sender, RoutedEventArgs e) { selectedTerrain = (TerrainType)((Button)sender).Tag; SelectedLabel.Text = $"{selectedTerrain} / selected brush"; }
    private void IslandList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IslandList.SelectedItem is string file) LoadIsland(Path.Combine(gameRoot, file)); }
    private void SceneList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (SceneList.SelectedItem is string scene) { FileLabel.Text = $"●  {scene} / SCENE.HQR"; DocumentTitle.Text = scene; DocumentSummary.Text = "Native SCENE.HQR record / object and zone data"; } }
    private void Open_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "LBA2 islands (*.ILE)|*.ILE|All files (*.*)|*.*", InitialDirectory = gameRoot }; if (dialog.ShowDialog() == true) LoadIsland(dialog.FileName); }
    private void Save_Click(object sender, RoutedEventArgs e) => Export_Click(sender, e);
    private void Export_Click(object sender, RoutedEventArgs e) { var dialog = new SaveFileDialog { Filter = "JSON draft (*.json)|*.json", FileName = Path.GetFileNameWithoutExtension(activeFile) + ".json" }; if (dialog.ShowDialog() != true) return; var draft = new { format = "lba2-ile-draft", width = 16, height = 16, tiles = fallbackTiles }; File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true })); }

    private enum TerrainType { Grass, Sand, Water, Stone, Dirt }
}
