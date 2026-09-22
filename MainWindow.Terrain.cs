using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LBAAssembler;

// Editing an island's terrain in the main window's own 3D view, live. The mouse position is turned into a point on the ground
// through NativeCameraModel (fitted from the native renderer's projection after every frame), fed to the terrain editor
// (IslandEditorView), and every change is written to a preview copy of the island (LiveDataRoot) that the native renderer reads
// instead of the game folder, so the 3D view shows the unsaved edits as they are made. The game folder is only written by Save.
public partial class MainWindow
{
    private NativeCameraModel? cameraModel;              // written by the render thread after each frame
    private bool paintingTerrain;
    private (double Gx, double Gz)? hoverCell;
    private LiveDataRoot? live;
    private DispatcherTimer? liveTimer;
    private bool liveDirty;
    private bool terrainToolsActive;                     // Build mode on an island with the terrain tools: the terrain editor is open
    private bool terrainMapWanted;                       // the top-down map (instead of the 3D view) was ticked in the tool panel

    // A terrain tool (not "Move the view") is chosen: the left button paints on the 3D view instead of orbiting it.
    private bool TerrainPaintActive => terrainToolsActive && !terrainShown && nativeViewActive && !interiorSceneActive && terrainEditor is { NavigateTool: false };

    // ---- live preview ------------------------------------------------------------------------------------------------------------------------

    private void UpdateLive()
    {
        var needed = currentGame == GameKind.Lba2 && !interiorSceneActive && terrainEditor is { CurrentName: not null } && (terrainToolsActive || terrainEditor.Dirty);
        if (needed && live is null) StartLive();
        else if (!needed && live is not null) StopLive();
    }

    private void StartLive()
    {
        if (terrainEditor?.CurrentName is not { } name || !nativeRenderer.DirectRendererReady) return;
        live = LiveDataRoot.Create(gameRoot, name);
        if (live is null)
        {
            FileLabel.Text = "Couldn't make the live preview folder, so the 3D view will only show your edits once they are saved.";
            return;
        }
        if (terrainEditor.Dirty) PushLive();
        nativeRenderer.BeginLive(live.Directory);
        if (nativeViewActive) RenderNativeCamera();
    }

    private void StopLive()
    {
        liveTimer?.Stop();
        liveDirty = false;
        if (live is null) return;
        nativeRenderer.EndLive();
        live.Dispose();
        live = null;
        if (nativeViewActive && !interiorSceneActive) RenderNativeCamera();
    }

    // A stroke tick / commit / selection change in the terrain editor.
    private void OnTerrainEdited()
    {
        DrawTerrainOverlay();
        if (live is null || terrainEditor is null || !(terrainEditor.Busy || terrainEditor.Dirty)) return;
        liveDirty = true;
        if (liveTimer is null)
        {
            liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
            liveTimer.Tick += (_, _) =>
            {
                if (!liveDirty) { liveTimer!.Stop(); return; }
                liveDirty = false;
                PushLive();
            };
        }
        if (!liveTimer.IsEnabled) liveTimer.Start();
    }

    // Writes the island as it is now into the preview copy and has the renderer read it.
    private void PushLive()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (live is null || terrainEditor?.ToBytes() is not { } bytes) return;
        var serialised = clock.ElapsedMilliseconds;
        try { live.WriteIsland(bytes); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DebugLog.Log($"MainWindow: live preview write failed: {error.Message}");
            return;
        }
        nativeRenderer.ReloadCubes();
        if (nativeViewActive) RenderNativeCamera();
        DebugLog.Log($"MainWindow: live preview pushed ({bytes.Length} bytes, serialised in {serialised} ms, written by {clock.ElapsedMilliseconds} ms)");
    }

    // The native renderer's island cache is dropped (SCENE.HQR or the island changed on disk); the preview folder follows.
    private void InvalidateNativeIsland()
    {
        try { live?.Sync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { DebugLog.Log($"MainWindow: live preview sync failed: {error.Message}"); }
        nativeRenderer.InvalidateLoadedIsland();
    }

    // ---- from the mouse to the ground ----------------------------------------------------------------------------------------------------

    // The ground point under a point of the 3D view (island cell units): the ray of the pixel is followed down from above the island
    // and stops where it first goes below the terrain.
    private bool PickGround(Point widgetPoint, out double gx, out double gz)
    {
        gx = gz = 0;
        if (terrainEditor is null) return false;
        var (lo, hi) = terrainEditor.HeightRange;
        if (!PickGroundWith(widgetPoint, lo, hi, terrainEditor.GroundAltitude, out var x, out var z)) return false;
        gx = x / 512; gz = z / 512;
        return true;
    }

    // The same with the ground given as a function (world x, z -> altitude, NaN off the island): the world point the pixel sees.
    private bool PickGroundWith(Point widgetPoint, double lo, double hi, Func<double, double, double> altitude, out double wx, out double wz)
    {
        wx = wz = 0;
        var model = cameraModel;
        if (model is null) return false;
        var w = TerrainViewport.ActualWidth; var h = TerrainViewport.ActualHeight;
        if (w < 1 || h < 1) return false;
        double u = widgetPoint.X * model.FrameWidth / w, v = widgetPoint.Y * model.FrameHeight / h;
        double top = hi + 2500, bottom = Math.Min(lo, 0) - 800;

        bool Below(double height, out double x, out double z, out double difference)
        {
            difference = 0;
            if (!model.RayToPlane(u, v, height, out x, out z)) return false;
            var ground = altitude(x, z);
            difference = height - (double.IsNaN(ground) ? 0 : ground);
            return true;
        }

        double px = 0, pz = 0, previous = top;
        if (!Below(top, out px, out pz, out var previousDifference) || previousDifference <= 0) return false;
        const int steps = 96;
        for (var i = 1; i <= steps; i++)
        {
            var height = top - (top - bottom) * i / steps;
            if (!Below(height, out px, out pz, out var difference)) return false;
            if (difference <= 0)
            {
                double a = previous, b = height;
                for (var k = 0; k < 22; k++)
                {
                    var mid = (a + b) / 2;
                    if (!Below(mid, out px, out pz, out var d)) return false;
                    if (d > 0) a = mid; else b = mid;
                }
                if (!Below(b, out px, out pz, out _)) return false;
                if (double.IsNaN(altitude(px, pz))) return false;      // the ray ends in the sea, off the island's terrain
                wx = px; wz = pz;
                return true;
            }
            previous = height;
        }
        return false;
    }

    // How many cells 12 pixels are on the ground here: how near a click must be to an object to pick it.
    private double PickToleranceCells(Point widgetPoint)
    {
        return PickGround(new Point(widgetPoint.X + 12, widgetPoint.Y), out var x2, out var z2) && PickGround(widgetPoint, out var x1, out var z1)
            ? Math.Max(1.5, Math.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1)))
            : 3;
    }

    private Point ToWidget(NativeCameraModel model, double u, double v) => new(u * TerrainViewport.ActualWidth / model.FrameWidth, v * TerrainViewport.ActualHeight / model.FrameHeight);

    // The brush ring on the ground and the selected object, drawn over the 3D view.
    private void DrawTerrainOverlay()
    {
        BrushOverlayCanvas.Children.Clear();
        var model = cameraModel;
        if (!terrainToolsActive || terrainShown || model is null || terrainEditor is null || !nativeViewActive || interiorSceneActive) return;

        if (hoverCell is { } cell && terrainEditor.BrushTool && TerrainPaintActive)
        {
            var radius = terrainEditor.BrushRadius;
            var ring = new PointCollection();
            for (var i = 0; i <= 48; i++)
            {
                var angle = i * Math.PI * 2 / 48;
                double wx = (cell.Gx + Math.Cos(angle) * radius) * 512, wz = (cell.Gz + Math.Sin(angle) * radius) * 512;
                var y = terrainEditor.GroundAltitude(wx, wz);
                if (double.IsNaN(y)) continue;
                if (model.Project(wx, y + 40, wz, out var u, out var v)) ring.Add(ToWidget(model, u, v));
            }
            if (ring.Count > 3)
            {
                BrushOverlayCanvas.Children.Add(new Polyline { Points = ring, Stroke = Brushes.Black, StrokeThickness = 3.5, Opacity = 0.55, IsHitTestVisible = false });
                BrushOverlayCanvas.Children.Add(new Polyline { Points = ring, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false });
            }
        }

        if (terrainEditor.SelectedObjectWorld is { } o && model.Project(o.X, o.Y, o.Z, out var ou, out var ov))
        {
            var p = ToWidget(model, ou, ov);
            var ring = new Ellipse { Width = 26, Height = 26, Stroke = Brushes.Yellow, StrokeThickness = 2.5, IsHitTestVisible = false };
            Canvas.SetLeft(ring, p.X - 13); Canvas.SetTop(ring, p.Y - 13);
            BrushOverlayCanvas.Children.Add(ring);
        }
    }

    private void Viewport_MouseLeave(object sender, MouseEventArgs e)
    {
        if (paintingTerrain) return;
        hoverCell = null;
        DrawTerrainOverlay();
    }

    // ---- dragging the ground with the middle button (any mode) -------------------------------------------------------------------------------

    private bool panning3D;
    private NativeCameraModel? panModel;
    private double panAnchorX, panAnchorZ, panStartTargetX, panStartTargetZ, panPlane;

    private bool BeginPan3D(Point at)
    {
        var model = cameraModel;
        if (model is null || !nativeViewActive || interiorSceneActive) { DebugLog.Log($"MainWindow: pan3D refused (model {(model is null ? "none" : "ok")}, native {nativeViewActive}, interior {interiorSceneActive})"); return false; }
        var w = TerrainViewport.ActualWidth; var h = TerrainViewport.ActualHeight;
        if (w < 1 || h < 1) return false;
        panPlane = targetY;
        if (!model.RayToPlane(at.X * model.FrameWidth / w, at.Y * model.FrameHeight / h, panPlane, out panAnchorX, out panAnchorZ)) return false;
        panModel = model; panStartTargetX = targetX; panStartTargetZ = targetZ;
        panning3D = true;
        TerrainViewport.CaptureMouse();
        return true;
    }

    private void MovePan3D(Point at)
    {
        if (panModel is not { } model) return;
        var w = TerrainViewport.ActualWidth; var h = TerrainViewport.ActualHeight;
        if (w < 1 || h < 1 || !model.RayToPlane(at.X * model.FrameWidth / w, at.Y * model.FrameHeight / h, panPlane, out var x, out var z)) return;
        TryPan(panStartTargetX + (panAnchorX - x) - targetX, panStartTargetZ + (panAnchorZ - z) - targetZ);
        RenderNativeCamera();
    }
}
