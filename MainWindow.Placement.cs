using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LBAAssembler.Lba1;
using LBAAssembler.Scenes;
using LBAAssembler.Terrain;

namespace LBAAssembler;

// Choosing where Twinsen starts before a scene is played: pressing Play first shows the scene with Twinsen on it as a marker, which
// is dragged to the spot wanted; letting go starts the game there (the engine's `teleport` puts the hero there once the scene runs).
// LBA2 outdoors the drop point is found on the island's terrain with the 3D view's camera model, and dropping him in another cube of
// the island plays that cube's scene; indoors the view is the fixed isometric picture and he is dropped on the floor under the pointer.
public partial class MainWindow
{
    private bool placing;
    private double placeBaseY;                              // the scene's own start height: interior drops try floors up to a little above it
    private (double X, double Y, double Z) placeWorld;      // absolute island coordinates outdoors, the scene's own indoors
    private int placeScene;
    private IslandFile? placementGround;
    private Border? placementMarker;
    private bool draggingHero;
    private bool placeManaged;                             // the marker moves on a managed isometric picture: any LBA1 map, or an LBA2 joined map
    private Lba1SceneImage? placeImage;
    private IReadOnlyList<Lba1AreaTile>? placeTiles;
    private Lba1AreaTile? placeTile;                        // the tile of placeScene: placeWorld is in that scene's own coordinates
    private Func<Lba1AreaTile, int[]>? placeTopsOf;         // per tile: the highest drawn layer of each grid column (-1 empty), index x + z * 64
    private readonly Dictionary<Lba1AreaTile, int[]> placeTops = new();
    private Vector grabOffset;                              // pointer minus the pin tip when the marker was picked up: the marker moves with the pointer, not to it

    // Whether a placement has been started here (false: nothing to place him on, so the game just starts).
    private bool BeginLba2Placement()
    {
        // A joined map has no single engine picture to drop Twinsen on (it's several scenes stitched into one
        // composite image) -- unlike the other refusals below, this one used to be silent (no message at all),
        // which for a scene only ever reachable through a join (e.g. 79, joined only under "Imperial Hotel" --
        // see Lba2Areas.Links) meant the checkbox looked broken with no explanation. Play still starts the
        // scene normally (LaunchPlay falls through to that below); only the drag-to-place step is skipped.
        if (currentGame != GameKind.Lba2 || terrainShown || !Lba2Configured) return false;
        placeManaged = false;
        if (lba2JoinedView) return BeginLba2JoinedPlacement();
        var scene = Lba2SceneToPlay();
        SceneModel model;
        try { model = new SceneStore(SceneGame.Lba2, gameRoot).Load(scene); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            DebugLog.Log($"MainWindow: placement: couldn't read scene {scene}: {error.Message}");
            return false;
        }
        var hero = model.Hero;
        // Which branch to take is the target scene's own CubeMode (0 = interior, 1 = exterior), not whatever
        // view the editor already happens to be showing -- picking a scene from the Scene box doesn't itself
        // navigate the 3D view there, so `interiorSceneActive`/`interiorSceneNumber` reflect whatever the user
        // was last looking at, not `scene`. Reading it off `model` (already loaded above) and, for an interior
        // scene not already open, actually opening it (ShowInteriorScene -- the same call the Scene box's own
        // selection uses) instead of silently refusing is the fix for "the option to choose where Twinsen
        // starts never appears" when Play is pressed for an interior scene that isn't the one on screen.
        if (model.CubeMode == 0)
        {
            if (interiorSceneNumber != scene) ShowInteriorScene(scene);
            if (interiorSceneNumber != scene) return false; // ShowInteriorScene itself failed; it already reported why (DocumentSummary.Text)
            placeWorld = (hero.X, hero.Y, hero.Z);
            placeBaseY = hero.Y;
            placementGround = null;
        }
        else
        {
            if (!nativeViewActive || currentIsland is null) return false;
            try { placementGround = IslandFile.Load(System.IO.Path.Combine(gameRoot, activeFile)); }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
            {
                DebugLog.Log($"MainWindow: placement: couldn't read {activeFile}: {error.Message}");
                return false;
            }
            placeWorld = (model.CubeX * 32768.0 + hero.X, hero.Y, model.CubeY * 32768.0 + hero.Z);
            targetX = placeWorld.X; targetZ = placeWorld.Z;
            if (!IsWorldPositionOnIsland(targetX, targetZ)) { targetX = model.CubeX * 32768.0 + 16384; targetZ = model.CubeY * 32768.0 + 16384; }
            SyncPanScrollBars();
            RenderNativeCamera();
        }
        placeScene = scene;
        ShowPlacementUi();
        return true;
    }

    private void ShowPlacementUi()
    {
        placing = true;
        PlayButton.Content = "▶  Start here";
        PlayButton.ToolTip = "Start the game with Twinsen where he is now (or drop him to start at once)";
        StopPlayButton.Content = "✕  Cancel";
        RestartPlayButton.Visibility = Visibility.Collapsed;
        PlayRunningPanel.Visibility = Visibility.Visible; PlayRunningPanel.Margin = new Thickness(0, 8, 0, 0);
        PlayStatus.Text = "Drag Twinsen to where the game should start, and let go. Or press Start here for his own spot. Esc cancels.";
        ActivatePanel(PlayTab);
        BuildPlacementMarker();
        UpdatePlacementMarker();
    }

    private void BuildPlacementMarker()
    {
        PlacementCanvas.Children.Clear();
        var grid = new Grid { Width = 46, Height = 62, Cursor = Cursors.SizeAll, ToolTip = "Twinsen: drag me where the game should start" };
        var body = new Ellipse { Width = 34, Height = 34, Fill = new SolidColorBrush(Color.FromRgb(0x4A, 0x8A, 0xE0)), Stroke = Brushes.White, StrokeThickness = 2.5, VerticalAlignment = VerticalAlignment.Top };
        var pin = new Polygon { Points = new PointCollection { new Point(13, 30), new Point(33, 30), new Point(23, 58) }, Fill = Brushes.White, Opacity = 0.9 };
        var letter = new TextBlock { Text = "T", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0), IsHitTestVisible = false };
        grid.Children.Add(pin); grid.Children.Add(body); grid.Children.Add(letter);
        var marker = new Border { Child = grid, Background = Brushes.Transparent };
        marker.MouseLeftButtonDown += PlacementMarker_MouseDown;
        marker.MouseMove += PlacementMarker_MouseMove;
        marker.MouseLeftButtonUp += PlacementMarker_MouseUp;
        placementMarker = marker;
        PlacementCanvas.Children.Add(marker);
    }

    // Puts the marker's pin tip on the ground point of Twinsen in the picture on screen.
    private void UpdatePlacementMarker()
    {
        if (!placing || placementMarker is null) return;
        Point? at = null;
        if (placeManaged)
        {
            if (placeImage is { } shown && placeTile is { } tile) at = InteriorCanvasToView(shown.Project(placeWorld.X + tile.OffsetX, placeWorld.Y + tile.OffsetY, placeWorld.Z + tile.OffsetZ));
        }
        else if (interiorSceneActive)
        {
            var library = nativeRenderer.RendererLibrary;
            if (library is not null && library.ProjectInteriorPoint((int)placeWorld.X, (int)placeWorld.Y, (int)placeWorld.Z, out var cx, out var cy))
                at = InteriorCanvasToView(new Point(cx, cy));
        }
        else if (cameraModel is { } model && model.Project(placeWorld.X, placeWorld.Y, placeWorld.Z, out var u, out var v))
            at = ToWidget(model, u, v);
        placementMarker.Visibility = at is null ? Visibility.Collapsed : Visibility.Visible;
        if (at is not { } p) return;
        Canvas.SetLeft(placementMarker, p.X - 23);
        Canvas.SetTop(placementMarker, p.Y - 60);
    }

    private Point InteriorCanvasToView(Point canvas)
        => new((canvas.X - interiorCenter.X) * interiorZoom + ViewportHost.ActualWidth / 2, (canvas.Y - interiorCenter.Y) * interiorZoom + ViewportHost.ActualHeight / 2);

    private void PlacementMarker_MouseDown(object sender, MouseButtonEventArgs e)
    {
        draggingHero = true;
        grabOffset = placementMarker is null ? new Vector() : e.GetPosition(ViewportHost) - new Point(Canvas.GetLeft(placementMarker) + 23, Canvas.GetTop(placementMarker) + 58);
        placementMarker?.CaptureMouse();
        e.Handled = true;
    }

    private void PlacementMarker_MouseMove(object sender, MouseEventArgs e)
    {
        if (!draggingHero) return;
        var at = e.GetPosition(ViewportHost) - grabOffset;
        if (placeManaged) MovePlacementManaged(at);
        else if (interiorSceneActive) MovePlacementInterior(at);
        else MovePlacementOutdoors(e.GetPosition(TerrainViewport) - grabOffset);
        UpdatePlacementMarker();
    }

    private void PlacementMarker_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!draggingHero) return;
        draggingHero = false;
        placementMarker?.ReleaseMouseCapture();
        e.Handled = true;
        CompletePlacement();          // letting go starts the game there
    }

    private void MovePlacementOutdoors(Point at)
    {
        if (placementGround is not { } ground) return;
        var (lo, hi) = IslandOps.HeightRange(ground);
        if (!PickGroundWith(at, lo, hi, (x, z) => IslandOps.Altitude(ground, x, z) ?? double.NaN, out var wx, out var wz)) return;
        var altitude = IslandOps.Altitude(ground, wx, wz);
        if (altitude is null) return;
        placeWorld = (wx, altitude.Value, wz);
    }

    // The isometric view is affine: at each floor height the ground point that projects under the pointer is found. Heights are tried from
    // the top down (higher = nearer the viewer = what is drawn on top), and Twinsen goes to the first height where a floor really is there: a
    // solid brick with headroom above it. A floor has to be at least a 2 x 2 cells patch at that height, because the top of a wall is a floor
    // to the engine too, and the near walls of an interior aren't drawn (the picture cuts them away).
    // Off every floor (outside the room, or over a wall) he stays where he was.
    private void MovePlacementInterior(Point at)
    {
        var library = nativeRenderer.RendererLibrary;
        if (library is null || interiorZoom <= 0) return;
        var canvas = new Point((at.X - ViewportHost.ActualWidth / 2) / interiorZoom + interiorCenter.X, (at.Y - ViewportHost.ActualHeight / 2) / interiorZoom + interiorCenter.Y);
        const int headroom = 2 * 256;
        var top = 24;
        bool Floor(int x, int z, int y) => library.InteriorFloorY(x, y + headroom - 1, z) == y;
        for (var level = Math.Max(1, top); level >= 1; level--)
        {
            var y = level * 256;
            if (!SolveInteriorGround(library, canvas, y, out var x, out var z)) return;
            if (!Floor(x, z, y)) continue;
            var patch = false;
            foreach (var (dx, dz) in new[] { (512, 512), (-512, 512), (512, -512), (-512, -512) })
                if (Floor(x + dx, z, y) && Floor(x, z + dz, y) && Floor(x + dx, z + dz, y)) { patch = true; break; }
            if (patch) { placeWorld = (x, y, z); return; }
        }
    }
    // The scene-local (x, z) at height y whose picture is `canvas`.
    private static bool SolveInteriorGround(RendererLibraryApi library, Point canvas, int y, out int x, out int z)
    {
        x = 8192; z = 8192;
        for (var pass = 0; pass < 2; pass++)
        {
            if (!library.ProjectInteriorPoint(x, y, z, out var c0x, out var c0y)
                || !library.ProjectInteriorPoint(x + 512, y, z, out var cxx, out var cxy)
                || !library.ProjectInteriorPoint(x, y, z + 512, out var czx, out var czy)) return false;
            // canvas offset = a * (1, x-step) + b * (1, z-step): solve for the steps a, b (in units of 512)
            double ax = cxx - c0x, ay = cxy - c0y, bx = czx - c0x, by = czy - c0y;
            var det = ax * by - ay * bx;
            if (Math.Abs(det) < 1e-6) return false;
            double dx = canvas.X - c0x, dy = canvas.Y - c0y;
            x += (int)Math.Round((dx * by - dy * bx) / det * 512);
            z += (int)Math.Round((ax * dy - ay * dx) / det * 512);
        }
        return true;
    }

    private void CancelPlacement()
    {
        if (!placing) return;
        EndPlacement();
        UpdatePlayButton();
        FileLabel.Text = "Play cancelled.";
    }

    private void EndPlacement()
    {
        placing = false;
        draggingHero = false;
        placementGround = null;
        placeManaged = false; placeImage = null; placeTiles = null; placeTile = null; placeTopsOf = null; placeTops.Clear();
        PlacementCanvas.Children.Clear();
        placementMarker = null;
        StopPlayButton.Content = "■  Stop";
        RestartPlayButton.Visibility = Visibility.Visible;
        PlayRunningPanel.Visibility = Visibility.Collapsed; PlayRunningPanel.Margin = new Thickness(0);
        PlayButton.Visibility = Visibility.Visible;
    }

    // Starts the game with Twinsen where he was put.
    private void CompletePlacement()
    {
        if (!placing) return;
        var world = placeWorld;
        var scene = placeScene;
        var spawn = (X: 0, Y: 0, Z: 0);
        if (placeManaged)
        {
            // (world is in the coordinates of the scene the drop landed in, which is the one played)
            var game = currentGame;
            EndPlacement();
            LaunchPlay(game, ((int)world.X, (int)world.Y, (int)world.Z), scene);
            return;
        }
        if (interiorSceneActive) spawn = ((int)world.X, (int)world.Y, (int)world.Z);
        else
        {
            // outdoors each cube of the island has its own scene: dropped in another cube, that cube's scene is played
            var cubeX = Math.Clamp((int)Math.Floor(world.X / 32768.0), 0, 15);
            var cubeZ = Math.Clamp((int)Math.Floor(world.Z / 32768.0), 0, 15);
            var island = System.IO.Path.GetFileNameWithoutExtension(activeFile);
            var there = allSceneEntries.FirstOrDefault(s => !s.IsInterior && s.CubeX == cubeX && s.CubeY == cubeZ && string.Equals(s.IslandFile, island, StringComparison.OrdinalIgnoreCase));
            if (there is not null) scene = there.Option.Index;
            else (cubeX, cubeZ) = allSceneEntries.Where(s => s.Option.Index == scene).Select(s => (s.CubeX, s.CubeY)).FirstOrDefault();
            spawn = (Math.Clamp((int)(world.X - cubeX * 32768.0), 0, 32767), (int)world.Y, Math.Clamp((int)(world.Z - cubeZ * 32768.0), 0, 32767));
        }
        EndPlacement();
        LaunchPlay(GameKind.Lba2, spawn, scene);
    }


    // ---- LBA1 maps and LBA2 joined maps: the same, on the managed isometric picture -------------------------------------------------------

    private Lba1SceneImage? lba1ShownImage;      // the picture of the scene on screen (its projection is what the marker uses)
    private Lba1SceneImage? lba2JoinedImage;     // the picture of the LBA2 joined map on screen

    private bool BeginLba1Placement()
    {
        if (currentGame != GameKind.Lba1 || lba1Game is null || lba1ShownImage is null || lba1CurrentTiles is not { Count: > 0 } tiles || !interiorSceneActive) return false;
        var game = lba1Game;
        Lba1Scene scene;
        try { scene = game.LoadScene(tiles[0].Scene); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            DebugLog.Log($"MainWindow: placement: couldn't read LBA1 scene {tiles[0].Scene}: {error.Message}");
            return false;
        }
        if (scene.Actors.Count == 0) return false;
        var hero = scene.Actors[0];
        return BeginManagedPlacement(lba1ShownImage, tiles, t => game.ColumnTops(t.Scene).TopY, tiles[0].Scene, (hero.X, hero.Y, hero.Z));
    }

    private bool BeginLba2JoinedPlacement()
    {
        if (lba2JoinedImage is not { } image || lba2CurrentTiles is not { Count: > 0 } tiles || lba2Interiors is not { } interiors) return false;
        var scene = tiles[0].Scene;
        if (interiors.LoadScene(scene) is not { } model) return false;
        return BeginManagedPlacement(image, tiles, t => TopsOf(interiors.Placements(t)), scene, (model.Hero.X, model.Hero.Y, model.Hero.Z));
    }

    private bool BeginManagedPlacement(Lba1SceneImage image, IReadOnlyList<Lba1AreaTile> tiles, Func<Lba1AreaTile, int[]> topsOf, int scene, (double X, double Y, double Z) hero)
    {
        placeManaged = true;
        placeImage = image; placeTiles = tiles; placeTopsOf = topsOf; placeTops.Clear();
        placeTile = tiles.FirstOrDefault(t => t.Scene == scene) ?? tiles[0];
        placeScene = scene;
        placeWorld = hero;
        placementGround = null;
        ShowPlacementUi();
        return true;
    }

    private static int[] TopsOf(IEnumerable<Lba1Placement> cells)
    {
        var top = new int[64 * 64];
        Array.Fill(top, -1);
        foreach (var p in cells)
            if ((uint)p.X < 64 && (uint)p.Z < 64 && p.Y > top[p.X + p.Z * 64]) top[p.X + p.Z * 64] = p.Y;
        return top;
    }

    private void MovePlacementManaged(Point at)
    {
        if (interiorZoom <= 0) return;
        var canvas = new Point((at.X - ViewportHost.ActualWidth / 2) / interiorZoom + interiorCenter.X, (at.Y - ViewportHost.ActualHeight / 2) / interiorZoom + interiorCenter.Y);
        if (PickManagedGround(canvas) is not { } hit) return;      // (off the map: he stays where he was)
        placeTile = hit.Tile; placeScene = hit.Tile.Scene;
        placeWorld = (hit.X, hit.Y, hit.Z);
    }

    // What the picture shows under a canvas point: the ray through it is walked from high above down to the first grid column whose top it
    // is at or below (higher on the ray = nearer the viewer = drawn on top). Twinsen goes on the top of that column, in the coordinates of the
    // scene whose tile it is (tiles carry offsets in the shared map).
    private (Lba1AreaTile Tile, double X, double Y, double Z)? PickManagedGround(Point canvas)
    {
        if (placeImage is not { } image || placeTiles is not { } tiles || placeTopsOf is not { } topsOf) return null;
        double u = (canvas.X - image.OriginX) * 512 / 24;                     // = x - z, in the shared map's units
        (double X, double Z) GroundAt(double y) { var v = (canvas.Y - image.OriginY + y * 15 / 256) * 512 / 12; return ((u + v) / 2, (v - u) / 2); }
        int lowest = tiles.Min(t => t.OffsetY), highest = tiles.Max(t => t.OffsetY) + 25 * 256;
        for (var y = highest; y >= lowest; y -= 64)
        {
            var (xa, za) = GroundAt(y);
            foreach (var tile in tiles)
            {
                int cx = (int)Math.Floor((xa - tile.OffsetX + 256) / 512), cz = (int)Math.Floor((za - tile.OffsetZ + 256) / 512);
                if ((uint)cx >= 64 || (uint)cz >= 64 || !tile.Holds(cx, cz)) continue;
                if (!placeTops.TryGetValue(tile, out var tops)) placeTops[tile] = tops = topsOf(tile);
                var top = tops[cz * 64 + cx];
                if (top < 0) continue;
                var floor = (top + 1) * 256;
                if (y - tile.OffsetY > floor) continue;
                // the point of that column's top face under the pointer (the column's middle when a side face was what was hit)
                var (fx, fz) = GroundAt(tile.OffsetY + floor);
                double sx = fx - tile.OffsetX, sz = fz - tile.OffsetZ;
                if (Math.Floor((sx + 256) / 512) != cx || Math.Floor((sz + 256) / 512) != cz) { sx = cx * 512; sz = cz * 512; }
                return (tile, sx, floor, sz);
            }
        }
        return null;
    }
}
