using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LBAAssembler.Lba1;

namespace LBAAssembler;

// LBA2 interiors that continue into one another (a factory's rooms, a control tower and the palace behind it), drawn as one map (Lba2Areas): the
// scenes' grids are drawn by the managed renderer at their offsets in one picture, with a marker per actor and every zone on top, like the LBA1
// joined maps. The map is for looking at: modes other than Explore are off in it, and double-clicking an actor opens the actor's scene on its
// own (through the native engine, where it can be edited). The "Join connected areas" box turns the joining on and off for both games.
public partial class MainWindow
{
    private Lba2Interiors? lba2Interiors;
    private Lba1ActorImages? lba2Images;      // (the actors' bodies, drawn as LBA1's are)
    private List<Lba1Area>? lba2Areas;
    private bool lba2JoinedView;
    private string? lba2AreaName;
    private IReadOnlyList<Lba1AreaTile>? lba2CurrentTiles;

    // Scene-list entries for a joined LBA2 map carry the map's number as -(map + 1), like the LBA1 ones.
    private static int Lba2AreaOption(int area) => -(area + 1);

    private void ResetLba2Areas()
    {
        lba2Interiors = null;
        lba2Areas = null;
        lba2Images = null;
    }

    private List<Lba1Area> Lba2AreaList()
    {
        if (lba2Areas is not null) return lba2Areas;
        try
        {
            lba2Interiors = new Lba2Interiors(gameRoot);
            lba2Areas = Lba2Areas.Find(lba2Interiors.LoadScene);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            DebugLog.Log($"MainWindow: LBA2 joined maps unavailable: {error.Message}");
            lba2Areas = new List<Lba1Area>();
        }
        return lba2Areas;
    }

    // The .ILE (file name without extension) an area's scenes belong to.
    private static string? Lba2AreaIslandFile(Lba1Area area)
        => area.Island >= 0 && area.Island < IslandNameByRawSceneId.Length ? IslandNameByRawSceneId[area.Island] : null;

    // The joined maps of an island (or of no island: the "Other" group), in the order of the list, with their numbers; none when joining is off.
    private List<(Lba1Area Area, int Index)> Lba2AreasOfIsland(string? islandFile)
        => !lba1JoinAreas ? new() : Lba2AreaList().Select((area, index) => (Area: area, Index: index))
            .Where(x => string.Equals(Lba2AreaIslandFile(x.Area), islandFile, StringComparison.OrdinalIgnoreCase)).ToList();

    // The scene-list entry that shows `scene`: the scene itself, or the joined map it is part of.
    private FilterableComboBox.Option? Lba2OptionForScene(int scene)
    {
        if (sceneOptions.FirstOrDefault(o => o.Index == scene) is { } direct) return direct;
        var areas = Lba2AreaList();
        for (var i = 0; i < areas.Count; i++)
            if (areas[i].Tiles.Any(t => t.Scene == scene)) return sceneOptions.FirstOrDefault(o => o.Index == Lba2AreaOption(i));
        return null;
    }

    // The scene list of an island: its joined maps first, then the scenes that are not part of one.
    private List<FilterableComboBox.Option> Lba2SceneOptions(IReadOnlyList<SceneEntry> scenes, string? islandFile)
    {
        var areas = Lba2AreasOfIsland(islandFile);
        var inArea = areas.SelectMany(a => a.Area.Tiles.Select(t => t.Scene)).ToHashSet();
        return areas.Select(a => new FilterableComboBox.Option(Lba2AreaOption(a.Index), a.Area.Name))
            .Concat(scenes.Where(s => !inArea.Contains(s.Option.Index)).Select(s => s.Option)).ToList();
    }

    // "Join connected areas" was ticked or unticked while LBA2 is open: the menu and the scene list change, and an interior on screen becomes the joined map it
    // belongs to (or, joining off, its first scene on its own).
    private void RefreshLba2AfterJoinToggle()
    {
        var current = selectedLba2Scene;
        BuildLba2Menu();
        RefreshSceneOptionsForSelectedIsland();
        if (!interiorSceneActive || current is not int scene) return;
        var option = lba1JoinAreas ? Lba2OptionForScene(scene) : sceneOptions.FirstOrDefault(o => o.Index == scene);
        if (option is null) return;
        SceneCombo.SelectedItem = null;
        SceneCombo.SelectedItem = option;
    }

    private void ShowLba2Area(int areaIndex)
    {
        var areas = Lba2AreaList();
        if (areaIndex < 0 || areaIndex >= areas.Count) return;
        selectedLba2Scene = areas[areaIndex].Tiles[0].Scene;      // Play starts the map's first scene
        ShowLba2Tiles(areas[areaIndex].Tiles, areas[areaIndex].Name, Lba2AreaIslandFile(areas[areaIndex]));
    }

    private void ShowLba2Tiles(IReadOnlyList<Lba1AreaTile> tiles, string areaName, string? islandFile)
    {
        if (lba2Interiors is null) Lba2AreaList();
        if (lba2Interiors is null) return;
        using var busy = UiBusy.Progress(BusyPanel, BusyLabel, $"Drawing {areaName}…");
        Lba1SceneImage image;
        var scenes = new Dictionary<int, Scenes.SceneModel>();
        try
        {
            foreach (var t in tiles) scenes[t.Scene] = lba2Interiors.LoadScene(t.Scene) ?? throw new InvalidDataException($"Scene {t.Scene} isn't in SCENE.HQR.");
            image = lba2Interiors.RenderArea(tiles);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or KeyNotFoundException)
        {
            DebugLog.Log($"MainWindow: LBA2 joined scenes {string.Join(",", tiles.Select(t => t.Scene))} failed: {error}");
            DocumentSummary.Text = $"Couldn't draw the map: {error.Message}";
            return;
        }

        CloseLba1ActorWindows();
        nativeRenderer.ReleaseInterior();
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze();

        // A body per actor where it has one (the entity's body in the neutral pose, through the same renderer as the LBA1 markers), a dummy marker where
        // it hasn't (the scenes' first actor is where Twinsen starts: left out), and every zone; the key is scene * 1000 + the actor's place in its scene.
        lba2Images ??= new Lba1ActorImages(lba2Interiors.Palette, lba2Interiors.ReadBody, 2);
        lba1ActorMarkers.Clear();
        lba1ViewScenes.Clear();
        var actors = new List<(int Index, int X, int Y, int HalfWidth, int HalfHeight, bool Marker)>();
        var zones = new List<ProjectedZone>();
        var zoneNumber = 0;
        foreach (var tile in tiles)
        {
            var scene = scenes[tile.Scene];
            Point At(double x, double y, double z) => image.Project(x + tile.OffsetX, y + tile.OffsetY, z + tile.OffsetZ);
            for (var i = 1; i < scene.Actors.Count; i++)
            {
                var actor = scene.Actors[i];
                if (!tile.HoldsPoint(actor.X, actor.Z)) continue;      // (a scene drawn in two pieces: each actor goes with the piece it stands in)
                var key = tile.Scene * 1000 + i;
                var feet = At(actor.X, actor.Y, actor.Z);
                var bodyIndex = actor.IsSprite ? null : lba2Interiors.BodyIndex(actor.Entity, actor.Body);
                var marker = bodyIndex is { } b ? lba2Images.GetMarker(b) : null;
                if (marker is not null)
                {
                    var height = Math.Clamp(marker.HeightUnits * 15 / 256, 14, 260);
                    lba1ActorMarkers[key] = (marker, height / 2);
                    actors.Add((key, (int)feet.X, (int)(feet.Y - height / 2), (int)Math.Max(10, height * .3), (int)(height / 2), false));
                }
                else
                {
                    var half = DummyBodyPreview.MarkerHalfHeight;      // (the placeholder body at its real size, like every other body)
                    actors.Add((key, (int)feet.X, (int)feet.Y - half, 10, half, true));
                }
            }
            for (var i = 0; i < scene.Zones.Count; i++)
            {
                var z = scene.Zones[i];
                if (!tile.HoldsPoint((z.X0 + z.X1) / 2, (z.Z0 + z.Z1) / 2)) continue;
                zones.Add(new ProjectedZone(z.Type, zoneNumber++, ZoneStyle.Corners(z.X0, z.Y0, z.Z0, z.X1, z.Y1, z.Z1).Select(c => At(c.X, c.Y, c.Z)).ToArray(), new ZoneRef(2, tile.Scene, i)));
            }
        }
        interiorActors = actors;
        interiorOverlay = new InteriorOverlay(new List<(int ActorIndex, List<Point> Points)>(), zones);

        // Every interior cube uses RESS_XPL00 regardless of which exterior island it's on (LoadInteriorPalette) --
        // this joined view draws with its own lba2Interiors.Palette above, but MainWindow.palette still needs to be
        // right for any actor attributes window opened while it's showing (ActorAttributesWindow's body preview).
        LoadInteriorPalette();

        lba2JoinedView = true;
        lba2AreaName = areaName;
        lba2CurrentTiles = tiles;
        SetMode(EditMode.Explore);
        interiorContent = new Rect(0, 0, image.Width, image.Height);
        nativeViewActive = false;
        interiorSceneActive = true;
        interiorSceneNumber = tiles[0].Scene;
        InteriorViewImage.Source = bitmap;
        InteriorHost.Visibility = Visibility.Visible;
        TerrainViewport.Visibility = Visibility.Collapsed;
        interiorZoom = 0;
        interiorCenter = new Point(image.Width / 2.0, image.Height / 2.0);
        selectedActorIndex = null;
        SelectZone(null, showTab: false);

        var island = islandFile is null ? "LBA2" : Lba2IslandLabel(islandFile);
        DocumentTitle.Text = $"{island} — {areaName}";
        DocumentSummary.Text = $"{scenes.Count} scenes / {scenes.Values.Sum(s => s.Actors.Count - 1)} actors / {scenes.Values.Sum(s => s.Zones.Count)} zones";
        FileLabel.Text = $"●  LBA2  ·  joined scenes {string.Join(", ", tiles.Select(t => t.Scene).Distinct())}  ·  view only: double-click an actor to open its scene";
        BuildSceneMinimap(bitmap, interiorContent);
        ApplyInteriorView();
        RefreshZoneListIfVisible();
        ApplyMode();
        UpdateLocation();
    }

    // In a joined map an actor is picked; double-clicking it opens its scene on its own, with the actor selected.
    private void JoinedLba2Actor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not int key) return;
        e.Handled = true;
        selectedActorIndex = key;
        RefreshActorOverlayForSelection();
        if (e.ClickCount < 2) return;
        var scene = key / 1000;
        var actor = key % 1000;
        selectedLba2Scene = scene;
        if (allSceneEntries.FirstOrDefault(s => s.Option.Index == scene) is { } entry)
        {
            DocumentTitle.Text = entry.Option.Display;
            FileLabel.Text = $"●  {entry.Option.Display} / SCENE.HQR";
        }
        ShowInteriorScene(scene);
        selectedActorIndex = actor - 1;
        RefreshActorOverlayForSelection();
        UpdateLocation();
    }
}
