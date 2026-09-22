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
using LBA2LevelEditor.Lba1;
using LBA2LevelEditor.LbaScript;

namespace LBA2LevelEditor;

public partial class MainWindow : Window
{
    private string gameRoot = EditorSettings.Current.GameDirectory;
    private readonly CommunityRendererBackend nativeRenderer;
    private readonly TerrainType[] fallbackTiles = new TerrainType[16 * 16];
    private IslandDocument? currentIsland;
    private string activeFile = "DESERT.ILE";
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
    // True while TerrainViewport is showing an interior/indoor scene
    // (ShowInteriorScene) rather than an outdoor island -- a fixed-camera
    // snapshot, not a live loop like nativeViewActive's. Guards
    // RenderSoftwareTerrain (the one place every zoom/pan/resize handler's
    // "not native" branch funnels through) so none of them silently paints
    // over the interior view with a re-rendered outdoor fallback, and gates
    // TerrainViewport_MouseDown so the free-orbit drag never applies to a
    // scene that's only ever meant to be viewed from one fixed angle.
    private bool interiorSceneActive;
    // Interior view state. The native side renders the whole scene once into
    // one big canvas bitmap (see RenderInteriorFullDirect); zoom and pan are
    // then purely a WPF transform of that bitmap, so zooming out to fit the
    // whole scene costs nothing. interiorCenter is the canvas pixel shown at
    // the middle of the viewport; interiorZoom is viewport pixels per canvas
    // pixel (1.0 == "100%").
    private int interiorSceneNumber = -1;
    private Rect interiorContent;
    private double interiorZoom = 1;
    private Point interiorCenter;
    private List<(int Index, int X, int Y, int HalfWidth, int HalfHeight, bool Marker)> interiorActors = new();
    private const double InteriorMaxZoom = 4;
    private CancellationTokenSource? nativeRenderCancellation;
    private readonly object nativeRenderGate = new();
    private bool nativeRenderInFlight;
    private bool nativeRenderDirty;
    private bool desiredSkyEnabled = true;
    private int minimapRequest;
    private byte[] palette = Array.Empty<byte>();
    private byte[] shadeTable = Array.Empty<byte>();
    private int shadeLevel;
    private IReadOnlyList<FilterableComboBox.Option> islandOptions = Array.Empty<FilterableComboBox.Option>();
    private IReadOnlyList<FilterableComboBox.Option> sceneOptions = Array.Empty<FilterableComboBox.Option>();
    private FilterableComboBox? islandFilter;
    private FilterableComboBox? sceneFilter;

    // IslandFile is the base .ILE filename (no extension, e.g. "DESERT")
    // this scene's own data says it belongs to -- see ResolveSceneIsland's
    // own comment -- or null when that byte doesn't resolve to any known
    // island, shown under the synthetic OtherIslandLabel island instead.
    // IsInterior comes from the scene's own on-disk CubeMode byte (see its
    // own comment in BuildSceneEntries) -- LBA2's indoor/building scenes use
    // a completely different, fixed-camera isometric renderer from every
    // outdoor island cube, wired up in ShowInteriorScene.
    private sealed record SceneEntry(string? IslandFile, bool IsInterior, FilterableComboBox.Option Option, int CubeX = 0, int CubeY = 0);
    private List<SceneEntry> allSceneEntries = new();
    private const string OtherIslandLabel = "Other";

    // Scripts edited in the actor script windows live here (as C text, per
    // scene) until saved back to SCENE.HQR; shared by every script window.
    private readonly ScriptSession scriptSession;
    private readonly ScriptSession lba1Session = new(() => EditorSettings.Current.Lba1Directory, lba1: true);

    public MainWindow()
    {
        scriptSession = new ScriptSession(() => gameRoot);
        nativeRenderer = new CommunityRendererBackend(gameRoot);
        InitializeComponent();
        WindowPlacement.Attach(this, "MainWindow");
        Scenes.SceneHistory.PersistPath = Path.Combine(AppContext.BaseDirectory, "undo_history.dat");
        Scenes.SceneHistory.ConfirmClearWhenFull = (used, limit) => MessageBox.Show(this,
            $"The undo cache is full ({used / (1024.0 * 1024.0):0.0} MB of a {limit / (1024.0 * 1024.0):0.0} MB limit).\n\nClear it to make room for this change? Choosing No just drops the oldest steps instead.",
            "Undo cache full", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        Scenes.SceneHistory.Load();
        modeReady = true;                 // the mode buttons raise Checked while the XAML loads; only real clicks count
        Focusable = true;
        JoinAreasCheck.IsChecked = lba1JoinAreas;
        HighlightCheck.IsChecked = highlightSelection;
        KeyDown += MainWindow_KeyDown;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += (_, e) => { if (playing) lba1Play?.ForwardKey(e, false); };
        SeedFallbackMap();
        BuildZoneList();

        islandFilter = new FilterableComboBox(IslandCombo, () => islandOptions);
        islandFilter.Committed += () =>
        {
            if (restoringIsland || IslandCombo.SelectedItem is not FilterableComboBox.Option o) return;
            if (currentGame == GameKind.Lba1) { RefreshLba1SceneOptions(o.Index); return; }
            // "Other" is a synthetic entry for scenes whose own data doesn't
            // resolve to any real island (see ResolveSceneIsland) -- there's
            // no actual .ILE file to load for it, just a different filter
            // over the same Scene dropdown.
            if (o.Display != OtherIslandLabel)
            {
                // Unsaved terrain edits of the island being left: save, discard, or stay on it.
                if (!string.Equals(o.Display, activeFile, StringComparison.OrdinalIgnoreCase) && !ConfirmTerrainDiscard())
                {
                    restoringIsland = true;
                    try { IslandCombo.SelectedItem = islandOptions.FirstOrDefault(x => x.Display == activeFile); }
                    finally { restoringIsland = false; }
                    return;
                }
                LoadIsland(Path.Combine(gameRoot, o.Display));
            }
            RefreshSceneOptionsForSelectedIsland();
        };
        sceneFilter = new FilterableComboBox(SceneCombo, () => sceneOptions);
        sceneFilter.Committed += () =>
        {
            if (SceneCombo.SelectedItem is not FilterableComboBox.Option o) return;
            if (currentGame == GameKind.Lba1) { ShowLba1Scene(o.Index); UpdatePlayButton(); return; }
            if (o.Index < 0) { ShowLba2Area(-o.Index - 1); UpdatePlayButton(); return; }      // a joined map of interiors
            selectedLba2Scene = o.Index;      // Play starts this scene
            FileLabel.Text = $"●  {o.Display} / SCENE.HQR";
            DocumentTitle.Text = o.Display;
            DocumentSummary.Text = "Native SCENE.HQR record / object and zone data";
            // o.Index is the numscene this option was built from (see
            // BuildSceneEntries) -- only interior scenes actually change
            // what's on screen here for now; picking an exterior scene still
            // just updates the labels above, matching this combo's existing
            // (pre-interior-support) behaviour rather than growing a second,
            // unrelated "jump the outdoor camera to an arbitrary scene"
            // feature nobody asked for yet.
            var entry = allSceneEntries.FirstOrDefault(e => e.Option.Index == o.Index);
            if (entry is { IsInterior: true }) ShowInteriorScene(o.Index);
            else if (entry is not null) FocusExteriorScene(entry);
            UpdatePlayButton();
        };

        gameComboReady = true;
        // Start in whichever game has a folder set (LBA2 first); with neither, open empty.
        SwitchGame(Lba2Configured ? GameKind.Lba2 : Lba1Configured ? GameKind.Lba1 : GameKind.Lba2);
    }

    // A game counts as configured when its folder holds the data the editor reads.
    private bool Lba2Configured => Lba2Folder.IsValid(gameRoot);
    private bool Lba1Configured => Lba1Game.IsInstalled(EditorSettings.Current.Lba1Directory);

    private void ShowEmptyState(string message)
    {
        islandOptions = Array.Empty<FilterableComboBox.Option>();
        sceneOptions = Array.Empty<FilterableComboBox.Option>();
        IslandCombo.Text = "";
        SceneCombo.Text = "";
        islandFilter?.Refresh();
        sceneFilter?.Refresh();
        DocumentTitle.Text = "";
        DocumentSummary.Text = message;
        FileLabel.Text = "";
    }

    private void PopulateAssetLists()
    {
        var islands = new List<FilterableComboBox.Option>();
        if (Directory.Exists(gameRoot))
        {
            var i = 0;
            foreach (var path in Directory.EnumerateFiles(gameRoot, "*.ILE").Where(path => !Path.GetFileName(path).StartsWith("_", StringComparison.OrdinalIgnoreCase)))
                islands.Add(new FilterableComboBox.Option(i++, Path.GetFileName(path)));
        }

        ResetLba2Areas();
        allSceneEntries = BuildSceneEntries();
        // Only add the synthetic "Other" island if there's actually at
        // least one scene that needs it -- no point offering an island
        // that would always show an empty Scene list.
        if (allSceneEntries.Any(e => e.IslandFile is null))
            islands.Add(new FilterableComboBox.Option(islands.Count, OtherIslandLabel));

        islandOptions = islands;
        islandFilter?.Refresh();
        var activeOption = islands.FirstOrDefault(o => o.Display == activeFile);
        // An install without DESERT.ILE (or a different set of islands) opens on its first island.
        if (activeOption is null && islands.Count > 0 && islands[0].Display != OtherIslandLabel)
        {
            activeOption = islands[0];
            activeFile = activeOption.Display;
        }
        if (activeOption is not null) IslandCombo.SelectedItem = activeOption; else IslandCombo.Text = "";

        RefreshSceneOptionsForSelectedIsland();
    }

    // SCENE.HQR's own entry 0 isn't a scene at all -- DISKFUNC.CPP's
    // LoadScene() reads its own scene data from HQR entry `numscene + 1`,
    // with the comment "numscene+1 car en 0 se trouve SizeCube.MAX" (entry
    // 0 holds the largest .SCC's size, not scene data) -- so real scenes
    // start at HQR entry 1, i.e. numscene 0. SCENE2.HQD (LBAPackageManager's
    // own text descriptions) agrees exactly: after its own file-header line,
    // the first *described* entry (index 0, matching HqdDescriptions' own
    // "line 2 -> entry 0" convention) is "Count of all entries and count of
    // outside scenes" -- the same metadata slot, not a real scene -- with
    // real scene descriptions starting only from described entry 1. Showing
    // `numscene = hqrIndex - 1` here (rather than the raw HQR index) means
    // this list already uses the same numbering LoadScene(numscene) expects,
    // ready for whenever scene selection is wired to actually load one.
    private List<SceneEntry> BuildSceneEntries()
    {
        var scenePath = Path.Combine(gameRoot, "SCENE.HQR");
        if (!File.Exists(scenePath)) return new List<SceneEntry>();

        var hqrCount = HqrArchive.CountEntries(scenePath);
        var descriptions = HqdDescriptions.Load("SCENE2.HQD", hqrCount);
        var archive = HqrArchive.Open(scenePath);

        var entries = new List<SceneEntry>();
        for (var hqrIndex = 1; hqrIndex < hqrCount; hqrIndex++)
        {
            if (!archive.IsValid(hqrIndex)) continue;
            var numscene = hqrIndex - 1;
            // (the descriptions say "White Leaf Desert"; LBA2 itself calls that island Desert Island)
            var name = (hqrIndex < descriptions.Names.Count ? descriptions.Names[hqrIndex] : null)?.Replace("White Leaf Desert", "Desert Island");
            var isInterior = IsInteriorScene(archive, hqrIndex);
            var option = new FilterableComboBox.Option(numscene, name is null ? $"{numscene}" : $"{numscene}: {name}");
            var header = archive.Read(hqrIndex);
            entries.Add(new SceneEntry(ResolveSceneIsland(archive, hqrIndex), isInterior, option, header.Length > 2 ? header[1] : 0, header.Length > 2 ? header[2] : 0));
        }
        return entries;
    }

    // Every scene record's own first byte is its Island ID (DISKFUNC.CPP's
    // LoadScene: "Island = GET_S8;", read right after the HQR record loads)
    // -- the exact same field RENDERER_ACTORS.CPP's PeekSceneCube already
    // reads (as p[0]) to match a scene against a target island index when
    // scanning cubes. RENDERER_API.CPP's own kIslandNames[] gives the order
    // that ID indexes into (0=citadel, 1=sendell [MOON.ILE's internal/lore
    // name], 2=desert, 3=emeraude, 4=otringal, 5=celebrat, 6=platform,
    // 7=mosquibe, 8=knartas, 9=ilotcx, 10=ascence, 11=souscelb) -- mirrored
    // here rather than exported from native, since it's plain static data
    // and every byte needed to compute it (SCENE.HQR itself) is already
    // read from C# via HqrArchive. Two of that table's own real-world
    // limitations carry over as-is rather than being papered over: CITABAU
    // has no ordinary index at all in the native table (interior-only), so
    // its scenes always fall under the synthetic "Other" island; and
    // CELEBRA2 is aliased to the same index 5 as CELEBRAT there, so a scene
    // with Island==5 is genuinely ambiguous between the two files and is
    // just attributed to CELEBRAT -- CELEBRA2's own scene list will read
    // empty rather than guess.
    private static readonly string[] IslandNameByRawSceneId =
    {
        "CITADEL", "MOON", "DESERT", "EMERAUDE", "OTRINGAL",
        "CELEBRAT", "PLATFORM", "MOSQUIBE", "KNARTAS", "ILOTCX",
        "ASCENCE", "SOUSCELB",
    };

    private static string? ResolveSceneIsland(HqrArchive archive, int hqrIndex)
    {
        var bytes = archive.Read(hqrIndex);
        if (bytes.Length < 1) return null;
        var islandId = bytes[0];
        return islandId < IslandNameByRawSceneId.Length ? IslandNameByRawSceneId[islandId] : null;
    }

    // DISKFUNC.CPP's LoadScene() reads, in this exact order right off the
    // scene record's own header: Island (byte 0, see ResolveSceneIsland),
    // CurrentCubeX (1), CurrentCubeY (2), ShadowLevel (3), ModeLabyrinthe
    // (4), CubeMode (5, 0=CUBE_INTERIEUR / 1=CUBE_EXTERIEUR). Reading it
    // straight from the same raw bytes here (rather than adding a native
    // API for one byte this editor already has in hand) is how the Scene
    // dropdown decides whether picking a given scene should hand off to
    // ShowInteriorScene's fixed-camera isometric renderer instead of the
    // ordinary outdoor island view.
    private static bool IsInteriorScene(HqrArchive archive, int hqrIndex)
    {
        var bytes = archive.Read(hqrIndex);
        return bytes.Length > 5 && bytes[5] == 0;
    }

    // Narrows the Scene dropdown to only the scenes belonging to whichever
    // island is currently selected (matched by base filename, e.g.
    // "DESERT" for DESERT.ILE) -- or, for the synthetic OtherIslandLabel
    // entry, the scenes whose own data didn't resolve to any known island
    // at all. No island selected (or an island with no resolvable scenes,
    // e.g. CELEBRA2 -- see ResolveSceneIsland's own comment) simply shows
    // an empty Scene list rather than falling back to showing everything.
    private void RefreshSceneOptionsForSelectedIsland()
    {
        var selected = IslandCombo.SelectedItem as FilterableComboBox.Option;
        IEnumerable<SceneEntry> matching;
        if (selected is null) matching = Enumerable.Empty<SceneEntry>();
        else if (selected.Display == OtherIslandLabel) matching = allSceneEntries.Where(e => e.IslandFile is null);
        else
        {
            var islandName = Path.GetFileNameWithoutExtension(selected.Display);
            matching = allSceneEntries.Where(e => e.IslandFile is not null && string.Equals(e.IslandFile, islandName, StringComparison.OrdinalIgnoreCase));
        }

        var matchingScenes = matching.ToList();
        sceneOptions = currentGame == GameKind.Lba2 && selected is not null
            ? Lba2SceneOptions(matchingScenes, selected.Display == OtherIslandLabel ? null : Path.GetFileNameWithoutExtension(selected.Display))
            : matchingScenes.Select(e => e.Option).ToList();
        sceneFilter?.Refresh();
        SceneCombo.Text = "";
    }

    private void LoadIsland(string path)
    {
        DebugLog.Log($"MainWindow: LoadIsland {Path.GetFileName(path)}");
        using var busy = UiBusy.Progress(BusyPanel, BusyLabel, $"Loading {Path.GetFileNameWithoutExtension(path)}…");
        try
        {
            LoadIslandPalette(path);
            currentIsland = IslandDocument.Open(path, palette, shadeTable, shadeLevel);
            if (!string.Equals(activeFile, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)) selectedLba2Scene = null;   // a scene of the island left isn't the selection any more
            sceneViewStale = false;
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

            ExitInteriorView();
            var preview = currentIsland.CreatePreview();
            TerrainViewport.Source = preview;
            selectedActorIndex = null;
            SelectZone(null, showTab: false);
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
            ApplyMode();
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

    private void RenderSoftwareTerrain()
    {
        if (nativeViewActive || interiorSceneActive || currentIsland is null || TerrainViewport.ActualWidth < 1 || TerrainViewport.ActualHeight < 1) return;
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

    // LBA2's indoor/building scenes: a completely different, fixed-camera
    // isometric brick-grid renderer from every outdoor island view above --
    // see CommunityRendererBackend.RenderInteriorSceneDirect's own comment.
    // Reuses whichever island's own palette is already loaded (the exact
    // per-scene palette an interior uses in retail depends on its own
    // Island/CubeMode -- not modelled here yet) since it's already on hand
    // and close enough to be readable; a scene that actually needs a
    // different one will just look off-colour rather than fail to render.
    // keepView: re-render after an actor edit without disturbing zoom/pan.
    // The native engine can pump Windows messages during a scene load, which
    // lets a queued keystroke (e.g. stepping through the scene combo quickly)
    // re-enter this method mid-load and interleave two scenes' native state
    // (seen as one scene rendering with the other's canvas and no actors).
    // A request that arrives while one is running is deferred until it ends.
    private bool interiorSceneBusy;
    private (int Scene, bool KeepView)? pendingInteriorScene;

    private void ShowInteriorScene(int numscene, bool keepView = false)
    {
        if (interiorSceneBusy)
        {
            pendingInteriorScene = (numscene, keepView);
            return;
        }
        interiorSceneBusy = true;
        try
        {
            ShowInteriorSceneCore(numscene, keepView);
            while (pendingInteriorScene is { } next)
            {
                pendingInteriorScene = null;
                ShowInteriorSceneCore(next.Scene, next.KeepView);
            }
            ApplyMode();
        }
        finally { interiorSceneBusy = false; pendingInteriorScene = null; }
    }

    private void ShowInteriorSceneCore(int numscene, bool keepView)
    {
        lba2JoinedView = false;
        if (!nativeRenderer.DirectRendererReady)
        {
            DocumentSummary.Text = "Interior scenes need the native renderer, which isn't available.";
            return;
        }

        // Loads the scene (and resets the native camera/projection, which a
        // body preview may have changed) before the full stitched render.
        var canvas = nativeRenderer.RenderInteriorSceneFullDirect(numscene, palette, out interiorActors, out interiorOverlay);
        if (canvas is null)
        {
            DocumentSummary.Text = "Couldn't render this interior scene.";
            return;
        }

        var width = CommunityRendererBackend.InteriorCanvasWidth;
        var height = CommunityRendererBackend.InteriorCanvasHeight;
        var pixels = new byte[width * height];
        canvas.CopyPixels(pixels, width, 0);
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                if (pixels[row + x] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        DebugLog.Log($"MainWindow: interior scene {numscene} canvas content x={minX}..{maxX} y={minY}..{maxY}, {interiorActors.Count} actors");
        const int pad = 16;
        interiorContent = maxX < 0
            ? new Rect(0, 0, width, height)
            : Rect.Intersect(new Rect(0, 0, width, height), new Rect(minX - pad, minY - pad, maxX - minX + 1 + pad * 2, maxY - minY + 1 + pad * 2));

        nativeViewActive = false;
        interiorSceneActive = true;
        interiorSceneNumber = numscene;
        selectedLba2Scene = numscene;
        InteriorViewImage.Source = canvas;
        InteriorHost.Visibility = Visibility.Visible;
        TerrainViewport.Visibility = Visibility.Collapsed;
        if (!keepView)
        {
            interiorZoom = 0; // clamped up to the fit zoom by ApplyInteriorView
            interiorCenter = new Point(interiorContent.X + interiorContent.Width / 2, interiorContent.Y + interiorContent.Height / 2);
            selectedActorIndex = null;
            SelectZone(null, showTab: false);
        }
        lastNativeActorScreens = null;
        lastNativeActorRoutes = null;
        DocumentSummary.Text = $"Native interior scene {numscene} / fixed isometric view";
        BuildSceneMinimap(canvas, interiorContent);
        ApplyInteriorView();
        RefreshZoneListIfVisible();
    }

    private void ExitInteriorView()
    {
        lba2JoinedView = false;
        lba2AreaName = null;
        nativeRenderer.ReleaseInterior();
        interiorSceneActive = false;
        interiorSceneNumber = -1;
        RestoreIslandMinimap();
        InteriorHost.Visibility = Visibility.Collapsed;
        TerrainViewport.Visibility = Visibility.Visible;
        PanHorizontalScrollBar.ViewportSize = 0;
        PanVerticalScrollBar.ViewportSize = 0;
        PanHorizontalScrollBar.SmallChange = PanVerticalScrollBar.SmallChange = 8192;
        PanHorizontalScrollBar.LargeChange = PanVerticalScrollBar.LargeChange = 65536;
    }

    private double InteriorFitZoom()
    {
        var vw = ViewportHost.ActualWidth;
        var vh = ViewportHost.ActualHeight;
        if (vw < 1 || vh < 1 || interiorContent.Width < 1 || interiorContent.Height < 1) return 1;
        return Math.Min(Math.Min(vw / interiorContent.Width, vh / interiorContent.Height), InteriorMaxZoom);
    }

    // Applies interiorZoom/interiorCenter (clamped) to the canvas image, the
    // pan scrollbars and the actor hit targets. Zoom bottoms out at the fit
    // zoom, where the whole occupied part of the scene fills the viewport.
    private void ApplyInteriorView()
    {
        if (!interiorSceneActive || InteriorViewImage.Source is null) return;
        var vw = ViewportHost.ActualWidth;
        var vh = ViewportHost.ActualHeight;
        if (vw < 1 || vh < 1) return;

        interiorZoom = Math.Clamp(interiorZoom, InteriorFitZoom(), InteriorMaxZoom);
        var cx = interiorCenter.X;
        var cy = interiorCenter.Y;
        cx = vw / interiorZoom >= interiorContent.Width ? interiorContent.X + interiorContent.Width / 2 : Math.Clamp(cx, interiorContent.Left, interiorContent.Right);
        cy = vh / interiorZoom >= interiorContent.Height ? interiorContent.Y + interiorContent.Height / 2 : Math.Clamp(cy, interiorContent.Top, interiorContent.Bottom);
        interiorCenter = new Point(cx, cy);

        InteriorViewImage.RenderTransform = new MatrixTransform(interiorZoom, 0, 0, interiorZoom, vw / 2 - cx * interiorZoom, vh / 2 - cy * interiorZoom);
        RenderOptions.SetBitmapScalingMode(InteriorViewImage, interiorZoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        PanHorizontalScrollBar.Minimum = interiorContent.Left;
        PanHorizontalScrollBar.Maximum = Math.Max(interiorContent.Left, interiorContent.Right - vw / interiorZoom);
        PanHorizontalScrollBar.ViewportSize = vw / interiorZoom;
        PanHorizontalScrollBar.SmallChange = 48;
        PanHorizontalScrollBar.LargeChange = vw / interiorZoom * .8;
        PanHorizontalScrollBar.Value = cx - vw / interiorZoom / 2;
        PanVerticalScrollBar.Minimum = interiorContent.Top;
        PanVerticalScrollBar.Maximum = Math.Max(interiorContent.Top, interiorContent.Bottom - vh / interiorZoom);
        PanVerticalScrollBar.ViewportSize = vh / interiorZoom;
        PanVerticalScrollBar.SmallChange = 48;
        PanVerticalScrollBar.LargeChange = vh / interiorZoom * .8;
        PanVerticalScrollBar.Value = cy - vh / interiorZoom / 2;

        DrawInteriorActorOverlay();
        UpdateZoomLabel();
        UpdateSceneMinimapViewport();
    }

    // Paints the dummy body centred at (cx, cy), `height` px tall, on the
    // actor overlay: how invisible / body-less actors (sound emitters, zone
    // triggers, ...) show where they are. Not hit-testable; the actor's usual
    // click target sits on top.
    private void AddDummyMarker(double cx, double cy, double height)
    {
        var source = DummyBodyPreview.RenderMarker();
        if (source is null) return;
        // (framed like every body marker: the body fills 80% of the square image's height, feet 90% down, so it stands 'height' tall on the point half of it below (cx, cy))
        height = Math.Clamp(height, 18, 500);
        var size = height / 0.8;
        var image = new Image { Source = source, Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        // (the dummy body is dark and the map behind it black: a pale glow keeps it visible)
        image.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.White, BlurRadius = 4, ShadowDepth = 0, Opacity = 0.9 };
        Canvas.SetLeft(image, cx - size / 2);
        Canvas.SetTop(image, cy + height / 2 - size * 0.9);
        ActorMarkerCanvas.Children.Add(image);
    }

    // A pre-rendered body (Lba1ActorImages.Marker: the body fills 80% of the square image's
    // height, feet 90% of the way down) drawn so it is 2 * halfHeight tall and stands on the
    // point halfHeight below (cx, cy).
    private void AddBodyMarker(BitmapSource image, double cx, double cy, double halfHeight)
    {
        var size = Math.Max(2 * halfHeight / 0.8, 8);
        var marker = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        Canvas.SetLeft(marker, cx - size / 2);
        Canvas.SetTop(marker, cy + halfHeight - size * 0.9);
        ActorMarkerCanvas.Children.Add(marker);
    }

    // ---- LBA1 ----------------------------------------------------------------
    // LBA1 scenes are shown in the same viewport as LBA2's interiors: one isometric bitmap
    // (Lba1GridRenderer) with actor markers, routes and zones drawn over it. None of it goes
    // through the native LBA2 engine.
    private enum GameKind { Lba1, Lba2 }
    private GameKind currentGame = GameKind.Lba2;
    private bool gameComboReady;
    private bool switchingGame;
    private Lba1Game? lba1Game;
    private Lba1ActorImages? lba1Images;
    // The scenes on screen (one, or the tiles of a joined area), and where each sits in the view.
    // Actor keys are sceneIndex * 1000 + actorIndex.
    private readonly Dictionary<int, Lba1Scene> lba1ViewScenes = new();
    private readonly Dictionary<int, (Lba1ActorImages.Marker Marker, double HalfHeight)> lba1ActorMarkers = new();
    private readonly Dictionary<int, Lba1ActorAttributesWindow> openLba1ActorWindows = new();
    private bool lba1JoinAreas = EditorSettings.Current.Lba1JoinConnectedAreas;

    // While an interior / LBA1 scene is on screen the minimap shows that scene instead of the
    // outdoor island: a thumbnail of the scene's content with a box for the part in view.
    private bool sceneMinimapActive;
    private Brush? savedMinimapBackground;
    private ImageSource? savedIslandMinimap;
    private double sceneMinimapScale = 1;
    private Point sceneMinimapOrigin;

    private void GameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!gameComboReady) return;
        var wanted = GameCombo.SelectedItem is ComboBoxItem { Tag: "1" } ? GameKind.Lba1 : GameKind.Lba2;
        if (wanted != currentGame) SwitchGame(wanted);
        // A combo that keeps keyboard focus flips games on a stray arrow key.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => Keyboard.Focus(this)));
    }

    private void SetGameSelection(GameKind game)
    {
        gameComboReady = false;
        GameCombo.SelectedIndex = game == GameKind.Lba1 ? 0 : 1;
        gameComboReady = true;
    }

    private void SwitchGame(GameKind game)
    {
        if (!ConfirmTerrainDiscard())
        {
            SetGameSelection(currentGame);
            return;
        }
        StopLive();
        if (live is null && Directory.Exists(gameRoot)) LiveDataRoot.CleanStale(gameRoot);      // a preview folder a crashed session left behind
        selectedLba2Scene = null;
        SwitchGameCore(game);
        ApplyMode();
    }

    private void SwitchGameCore(GameKind game)
    {
        // A game whose folder isn't set (or can't be read) just opens empty, with a note saying why.
        string? unavailable = null;
        if (game == GameKind.Lba1)
        {
            var directory = EditorSettings.Current.Lba1Directory;
            if (!Lba1Game.IsInstalled(directory)) unavailable = "The LBA1 game folder isn't set. Choose it under File > Settings.";
            else
            {
                try
                {
                    lba1Game ??= new Lba1Game(directory);
                    lba1Images ??= new Lba1ActorImages(lba1Game);
                }
                catch (Exception error)
                {
                    DebugLog.Log($"MainWindow: LBA1 load failed: {error}");
                    lba1Game = null;
                    lba1Images = null;
                    unavailable = $"Couldn't read the LBA1 data: {error.Message}";
                }
            }
        }
        else if (!Lba2Configured) unavailable = "The LBA2 game folder isn't set. Choose it under File > Settings.";

        switchingGame = true;
        try { CloseActorWindows(); }
        finally { switchingGame = false; }

        // Stop anything the other game's view was still doing, and blank what it left behind.
        nativeRenderCancellation?.Cancel();
        nativeRenderCancellation = null;
        nativeViewActive = false;
        ExitInteriorView();
        TerrainViewport.Source = null;
        InteriorViewImage.Source = null;
        Interlocked.Increment(ref minimapRequest);
        MinimapImage.Source = null;
        MinimapActorCanvas.Children.Clear();
        MinimapMarkerCanvas.Children.Clear();
        ActorMarkerCanvas.Children.Clear();
        selectedActorIndex = null;
        SelectZone(null, showTab: false);
        lastNativeActorScreens = null;
        lastNativeActorRoutes = null;
        lastNativeZones = null;
        lastNativeInvisibleActors = null;
        interiorActors = new();
        interiorOverlay = InteriorOverlay.Empty;
        lba1ActorMarkers.Clear();
        lba1ViewScenes.Clear();
        actorMarkersIsland = null;
        currentGame = game;
        SetGameSelection(game);
        lba2JoinedView = false;
        JoinAreasCheck.Visibility = Visibility.Visible;

        if (unavailable is not null)
        {
            var other = game == GameKind.Lba1 ? Lba2Configured : Lba1Configured;
            ShowEmptyState(other ? unavailable : "No game folders are set yet. Choose your LBA1 and/or LBA2 folder under File > Settings.");
            return;
        }
        if (game == GameKind.Lba1)
        {
            PopulateLba1Lists();
            return;
        }

        PopulateAssetLists();
        // PopulateAssetLists() selects the island, which loads it; only load here if that didn't happen
        // (a second load races the first one's minimap).
        if (IslandCombo.SelectedItem is null && File.Exists(Path.Combine(gameRoot, activeFile))) LoadIsland(Path.Combine(gameRoot, activeFile));
    }

    private void CloseActorWindows()
    {
        foreach (var window in openAttributesWindows.Values.ToList()) window.Close();
        foreach (var window in openScriptWindows.Values.ToList()) window.Close();
        CloseLba1ActorWindows();
    }

    private void CloseLba1ActorWindows()
    {
        foreach (var window in openLba1ActorWindows.Values.ToList()) window.Close();
        openLba1ActorWindows.Clear();
    }

    private void PopulateLba1Lists()
    {
        var game = lba1Game!;
        islandOptions = game.Scenes.Select(s => s.Island).Distinct().Order()
            .Select(id => new FilterableComboBox.Option(id, Lba1Game.IslandNames.ElementAtOrDefault(id) ?? $"Island {id}"))
            .ToList();
        sceneOptions = Array.Empty<FilterableComboBox.Option>();
        IslandCombo.Text = "";
        SceneCombo.Text = "";
        islandFilter?.Refresh();
        sceneFilter?.Refresh();
        DocumentTitle.Text = "Little Big Adventure";
        DocumentSummary.Text = $"{game.Scenes.Count} scenes on {islandOptions.Count} islands";
        FileLabel.Text = "LBA1";
        // Picking the first island lists its scenes and opens the first one.
        if (islandOptions.Count > 0) IslandCombo.SelectedItem = islandOptions[0];
    }

    // Scene-list entries for a joined map carry the area's number as -(area + 1).
    private static int AreaOption(int area) => -(area + 1);

    // One entry of the scene list (and of the Scenes menu): the scene-box option, and the name the menu shows for it.
    private sealed record Lba1SceneEntry(FilterableComboBox.Option Option, string MenuName);

    // The scenes (and joined areas) of one LBA1 island: first everything that belongs to a connected outside map (the joined maps, biggest
    // "Outside" first, or with joining off their scenes one after the other), then the rest.
    private (List<Lba1SceneEntry> Connected, List<Lba1SceneEntry> Others) Lba1SceneGroups(Lba1Game game, int island)
    {
        var connected = new List<Lba1SceneEntry>();
        var others = new List<Lba1SceneEntry>();
        var inArea = new HashSet<int>();
        Lba1SceneEntry SceneEntryOf(Lba1SceneInfo s)
        {
            var description = game.Description(s.Index);
            return new Lba1SceneEntry(new FilterableComboBox.Option(s.Index, description is null
                    ? $"{s.Index}: {s.ActorCount} actors" + (s.Exits.Count > 0 ? $", exits to {string.Join(", ", s.Exits)}" : "")
                    : $"{s.Index}: {description}"),
                description is null ? $"Scene {s.Index}" : SceneMenuNames.Clean(description));
        }
        foreach (var (area, a) in game.Areas.Select((area, a) => (area, a)).Where(x => x.area.Island == island)
                     .OrderByDescending(x => x.area.Name == "Outside").ThenByDescending(x => x.area.Tiles.Count).ThenBy(x => x.a))
        {
            foreach (var tile in area.Tiles) inArea.Add(tile.Scene);
            if (lba1JoinAreas) connected.Add(new Lba1SceneEntry(new FilterableComboBox.Option(AreaOption(a), area.Label), area.Name));
            else connected.AddRange(area.Tiles.Select(t => SceneEntryOf(game.Scenes.First(s => s.Index == t.Scene))));
        }
        others.AddRange(game.Scenes.Where(s => s.Island == island && !inArea.Contains(s.Index)).Select(SceneEntryOf));
        return (connected, others);
    }

    // The island's scenes and joined areas as scene-box options (in the order of Lba1SceneGroups).
    private List<FilterableComboBox.Option> Lba1SceneOptionsFor(Lba1Game game, int island)
    {
        var (connected, others) = Lba1SceneGroups(game, island);
        return connected.Concat(others).Select(e => e.Option).ToList();
    }

    // The entry of the current list that shows `scene`: the scene itself, or the joined map it is part of.
    private FilterableComboBox.Option? Lba1OptionForScene(int scene)
    {
        if (sceneOptions.FirstOrDefault(o => o.Index == scene) is { } direct) return direct;
        var area = lba1Game?.Areas.Select((a, i) => (a, i)).FirstOrDefault(x => x.a.Tiles.Any(t => t.Scene == scene));
        return area?.a is null ? null : sceneOptions.FirstOrDefault(o => o.Index == AreaOption(area.Value.i));
    }

    // What to open for an island when nothing more specific was asked: its main outside map, or (joining off) the scene in the middle of it;
    // an island with no joined map opens its first scene.
    private FilterableComboBox.Option? DefaultLba1Option(int island)
    {
        if (lba1Game is not null && Lba1Areas.MainArea(lba1Game.Areas.Where(a => a.Island == island)) is { } main)
        {
            var pick = lba1JoinAreas
                ? sceneOptions.FirstOrDefault(o => o.Index == AreaOption(lba1Game.Areas.ToList().IndexOf(main)))
                : sceneOptions.FirstOrDefault(o => o.Index == Lba1Areas.CentralScene(main));
            if (pick is not null) return pick;
        }
        return sceneOptions.FirstOrDefault();
    }

    // Lists the island's scenes and opens `preferScene` (its own entry, or its joined map's) or, without one, the island's default.
    private void RefreshLba1SceneOptions(int island, int? preferScene = null)
    {
        if (lba1Game is null) return;
        sceneOptions = Lba1SceneOptionsFor(lba1Game, island);
        sceneFilter?.Refresh();
        SceneCombo.Text = "";
        var pick = (preferScene is int scene ? Lba1OptionForScene(scene) : null) ?? DefaultLba1Option(island);
        if (pick is not null) SceneCombo.SelectedItem = pick;
    }

    // The scene the open map is looking at: the only one when a single scene is open, else the one with the most ground in the middle of the
    // view (zoomed right in on one scene that is the one; zoomed out, the most central; equally central: either).
    private int? Lba1FocusScene()
    {
        if (currentGame != GameKind.Lba1 || lba1Game is null || lba1CurrentTiles is not { Count: > 0 } tiles) return null;
        if (tiles.Count == 1 || lba1ShownImage is not { } image) return tiles[0].Scene;
        var zoom = interiorZoom > 0 ? interiorZoom : 1;
        double width = ViewportHost.ActualWidth / zoom, height = ViewportHost.ActualHeight / zoom;
        return Lba1Areas.FocusScene(tiles, scene => lba1Game.ColumnTops(scene).TopY, (x, y, z) => { var p = image.Project(x, y, z); return (p.X, p.Y); },
            interiorCenter.X, interiorCenter.Y, width / 4, height / 4);
    }

    private void JoinAreas_Click(object sender, RoutedEventArgs e)
    {
        var focus = Lba1FocusScene();          // (what is on screen, before the list changes under it)
        lba1JoinAreas = JoinAreasCheck.IsChecked == true;
        EditorSettings.Current.Lba1JoinConnectedAreas = lba1JoinAreas;
        try { EditorSettings.Current.Save(); } catch (Exception error) { DebugLog.Log($"MainWindow: settings save failed: {error.Message}"); }
        if (currentGame == GameKind.Lba1 && IslandCombo.SelectedItem is FilterableComboBox.Option island) RefreshLba1SceneOptions(island.Index, focus);
        else if (currentGame == GameKind.Lba2) RefreshLba2AfterJoinToggle();
    }

    // `option` is a scene number, or AreaOption(area) for a joined map.
    private void ShowLba1Scene(int option)
    {
        if (lba1Game is null) return;
        IReadOnlyList<Lba1AreaTile> tiles;
        if (option < 0)
        {
            var areaIndex = -option - 1;
            if (areaIndex >= lba1Game.Areas.Count) return;
            tiles = lba1Game.Areas[areaIndex].Tiles;
        }
        else tiles = new[] { new Lba1AreaTile(option, 0, 0, 0) };
        ShowLba1Tiles(tiles);
    }

    private IReadOnlyList<Lba1AreaTile>? lba1CurrentTiles;

    private void ShowLba1Tiles(IReadOnlyList<Lba1AreaTile> tiles, bool keepView = false)
    {
        if (lba1Game is null || lba1Images is null) return;
        var single = tiles.Count == 1;
        using var busy = single ? UiBusy.Cursor() : UiBusy.Progress(BusyPanel, BusyLabel, "Drawing the map…");
        Lba1SceneImage image;
        var scenes = new Dictionary<int, Lba1Scene>();
        try
        {
            foreach (var t in tiles) scenes[t.Scene] = lba1Game.LoadScene(t.Scene);
            image = single ? lba1Game.RenderScene(tiles[0].Scene) : lba1Game.RenderArea(new Lba1Area(scenes[tiles[0].Scene].Island, tiles));
        }
        catch (Exception error)
        {
            DebugLog.Log($"MainWindow: LBA1 scene(s) {string.Join(",", tiles.Select(t => t.Scene))} failed: {error}");
            DocumentSummary.Text = $"Couldn't draw the map: {error.Message}";
            return;
        }

        if (!keepView) CloseLba1ActorWindows();
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze();

        // Actors: a rendered body where the entity has one, else the dummy marker. In a joined
        // map the per-scene hero start positions are left out.
        lba1ViewScenes.Clear();
        lba1ActorMarkers.Clear();
        var actors = new List<(int Index, int X, int Y, int HalfWidth, int HalfHeight, bool Marker)>();
        var zones = new List<ProjectedZone>();
        var routes = new List<(int ActorIndex, List<Point> Points)>();
        var zoneNumber = 0;
        foreach (var tile in tiles)
        {
            var scene = scenes[tile.Scene];
            lba1ViewScenes[tile.Scene] = scene;
            Point At(double x, double y, double z) => image.Project(x + tile.OffsetX, y + tile.OffsetY, z + tile.OffsetZ);

            foreach (var actor in scene.Actors)
            {
                if (!single && actor.Index == 0) continue;
                var key = tile.Scene * 1000 + actor.Index;
                var feet = At(actor.X, actor.Y, actor.Z);
                int? bodyIndex = actor.Index == 0 ? lba1Game.BodyIndex(0, 0) : actor.HasEntity ? lba1Game.BodyIndex(actor.Entity, actor.Body) : null;
                var marker = bodyIndex is { } b ? lba1Images.GetMarker(b) : null;
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

                var track = Lba1TrackScript.Points(actor.TrackScript).Where(p => p < scene.Tracks.Count).ToList();
                if (track.Count == 0) continue;
                var points = new List<Point> { feet };
                points.AddRange(track.Select(p => At(scene.Tracks[p].X, scene.Tracks[p].Y, scene.Tracks[p].Z)));
                routes.Add((key, points));
            }

            foreach (var z in scene.Zones)
                zones.Add(new ProjectedZone(z.Type, single ? z.Num : zoneNumber++,
                    ZoneStyle.Corners(z.X0, z.Y0, z.Z0, z.X1, z.Y1, z.Z1).Select(c => At(c.X, c.Y, c.Z)).ToArray(),
                    new ZoneRef(1, tile.Scene, z.Num)));
        }
        // Hit targets are stacked in list order: body-less markers underneath, then big before small.
        interiorActors = actors.OrderBy(a => a.Marker ? 0 : 1).ThenByDescending(a => a.HalfWidth * a.HalfHeight).ToList();
        interiorOverlay = new InteriorOverlay(routes, zones);

        interiorContent = new Rect(0, 0, image.Width, image.Height);
        lba1ShownImage = image;
        nativeViewActive = false;
        interiorSceneActive = true;
        interiorSceneNumber = tiles[0].Scene;
        InteriorViewImage.Source = bitmap;
        InteriorHost.Visibility = Visibility.Visible;
        TerrainViewport.Visibility = Visibility.Collapsed;
        lba1CurrentTiles = tiles;
        if (!keepView)
        {
            interiorZoom = 0;
            interiorCenter = new Point(image.Width / 2.0, image.Height / 2.0);
            selectedActorIndex = null;
            SelectZone(null, showTab: false);
        }

        var first = scenes[tiles[0].Scene];
        var island = Lba1Game.IslandNames.ElementAtOrDefault(first.Island) ?? $"Island {first.Island}";
        var actorCount = scenes.Values.Sum(s => s.Actors.Count - 1);
        var zoneCount = scenes.Values.Sum(s => s.Zones.Count);
        if (single)
        {
            DocumentTitle.Text = $"Scene {tiles[0].Scene}";
            DocumentSummary.Text = $"{lba1Game.Description(tiles[0].Scene) ?? island} / {actorCount} actors / {zoneCount} zones";
            FileLabel.Text = $"LBA1  ·  SCENE.HQR #{tiles[0].Scene}  ·  grid {tiles[0].Scene}";
        }
        else
        {
            var areaName = lba1Game.Areas.FirstOrDefault(a => a.Tiles.Any(t => t.Scene == tiles[0].Scene))?.Name ?? "Joined map";
            DocumentTitle.Text = $"{island} — {areaName}";
            DocumentSummary.Text = $"{tiles.Count} scenes / {actorCount} actors / {zoneCount} zones";
            FileLabel.Text = $"LBA1  ·  joined scenes {string.Join(", ", tiles.Select(t => t.Scene))}";
        }
        BuildSceneMinimap(bitmap, interiorContent);
        ApplyInteriorView();
        RefreshZoneListIfVisible();
    }

    // The scene minimap: a thumbnail of `content` (canvas pixels), a dot per actor, and a box
    // for the part in view (see UpdateSceneMinimapViewport). Clicking it recentres the view.
    private void BuildSceneMinimap(BitmapSource canvas, Rect content)
    {
        Interlocked.Increment(ref minimapRequest);
        if (!sceneMinimapActive)
        {
            savedIslandMinimap = MinimapImage.Source;
            sceneMinimapActive = true;
            savedMinimapBackground = MinimapBody.Background;
            MinimapBody.Background = Brushes.Black;      // (a scene's picture is on black, like the view itself)
        }
        sceneMinimapOrigin = content.TopLeft;
        sceneMinimapScale = Math.Min(1.0, 214.0 / Math.Max(content.Width, content.Height));
        BitmapSource cropped = new CroppedBitmap(canvas, new Int32Rect((int)content.X, (int)content.Y, (int)content.Width, (int)content.Height));
        var thumbnail = new TransformedBitmap(cropped, new ScaleTransform(sceneMinimapScale, sceneMinimapScale));
        thumbnail.Freeze();
        MinimapImage.Source = thumbnail;
        MinimapActorCanvas.Children.Clear();
        foreach (var (_, x, y, _, _, _) in interiorActors)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = Brushes.Yellow, Stroke = Brushes.Black, StrokeThickness = 0.5 };
            Canvas.SetLeft(dot, (x - sceneMinimapOrigin.X) * sceneMinimapScale - 2);
            Canvas.SetTop(dot, (y - sceneMinimapOrigin.Y) * sceneMinimapScale - 2);
            MinimapActorCanvas.Children.Add(dot);
        }
        MinimapScrollViewer.ScrollToHorizontalOffset(0);
        MinimapScrollViewer.ScrollToVerticalOffset(0);
    }

    private void UpdateSceneMinimapViewport()
    {
        MinimapMarkerCanvas.Children.Clear();
        if (!sceneMinimapActive || MinimapImage.Source is null || interiorZoom <= 0) return;
        var w = ViewportHost.ActualWidth / interiorZoom;
        var h = ViewportHost.ActualHeight / interiorZoom;
        var scale = sceneMinimapScale;
        var view = new Rect((interiorCenter.X - w / 2 - sceneMinimapOrigin.X) * scale, (interiorCenter.Y - h / 2 - sceneMinimapOrigin.Y) * scale, w * scale, h * scale);
        view.Intersect(new Rect(1, 1, interiorContent.Width * scale - 2, interiorContent.Height * scale - 2));
        if (view.IsEmpty) return;
        var box = new System.Windows.Shapes.Rectangle { Width = Math.Max(4, view.Width), Height = Math.Max(4, view.Height), Stroke = Brushes.Yellow, StrokeThickness = 1.5 };
        Canvas.SetLeft(box, view.X);
        Canvas.SetTop(box, view.Y);
        MinimapMarkerCanvas.Children.Add(box);
    }

    // Puts the outdoor island's minimap back when leaving a scene view.
    private void RestoreIslandMinimap()
    {
        if (!sceneMinimapActive) return;
        sceneMinimapActive = false;
        if (savedMinimapBackground is not null) MinimapBody.Background = savedMinimapBackground;
        MinimapImage.Source = savedIslandMinimap;
        savedIslandMinimap = null;
        MinimapActorCanvas.Children.Clear();
        MinimapMarkerCanvas.Children.Clear();
        // The island's actor dots are redrawn by the next native render.
        actorMarkersIsland = null;
    }

    // `key` is sceneIndex * 1000 + actorIndex.
    private void OpenLba1ActorWindow(int key, bool edit = false)
    {
        if (lba1Game is null || lba1Images is null) return;
        if (!lba1ViewScenes.TryGetValue(key / 1000, out var scene)) return;
        var actor = scene.Actors.FirstOrDefault(a => a.Index == key % 1000);
        if (actor is null) return;
        if (openLba1ActorWindows.TryGetValue(key, out var existing))
        {
            existing.Activate();
            return;
        }
        var sceneNumber = key / 1000;
        var title = $"scene {sceneNumber}, {lba1Game.Description(sceneNumber) ?? Lba1Game.IslandNames.ElementAtOrDefault(scene.Island) ?? $"island {scene.Island}"}";
        Lba1ActorAttributesWindow window;
        try
        {
            window = new Lba1ActorAttributesWindow(lba1Game, lba1Images, sceneNumber, actor.Index, title,
                edited => SaveLba1Actor(sceneNumber, edited), () => OpenLba1ScriptWindow(key)) { Owner = this };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't read scene {sceneNumber}: {error.Message}", "LBA1 actor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        WindowLifecycle.Register(window, "ActorAttributesWindow");    // shares a key (and so a remembered spot) with LBA2's own actor window: the same kind of thing
        window.Closed += (_, _) => openLba1ActorWindows.Remove(key);
        openLba1ActorWindows[key] = window;
        if (!edit && editMode == EditMode.Explore) window.MakeViewOnly();
        window.Show();
    }

    // Writes an actor's edited attributes into the scene record, then redraws the scene from the new file.
    private string? SaveLba1Actor(int scene, Lba1ActorData edited)
    {
        if (lba1Session.EditedScenes.Contains(scene))
            return "This scene has unsaved script edits. Save or discard them first, so the attribute change isn't overwritten.";
        try
        {
            SceneZones.SaveRecord(1, scene, record => Lba1ActorRecord.Patch(record, edited), $"Edit actor {edited.Index} of scene {scene}");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Not saved: {error.Message}";
        }
        zoneCache.Clear();
        lba1Session.ForgetScene(scene);
        ReloadLba1AfterEdit();
        return null;
    }

    private static ActorSource? ResolveLba1Source(int key) => new(key / 1000, key % 1000);

    private (string Stats, string Disassembly)? DescribeLba1Actor(int key)
    {
        if (lba1Game is null) return null;
        try
        {
            var scene = lba1Game.LoadScene(key / 1000);
            var actor = scene.Actors.FirstOrDefault(a => a.Index == key % 1000);
            if (actor is null) return null;
            var stats = actor.Index == 0
                ? $"start pos ({actor.X}, {actor.Y}, {actor.Z})"
                : $"pos ({actor.X}, {actor.Y}, {actor.Z})   angle={actor.Angle} entity={actor.Entity} body={actor.Body} anim={actor.Anim}   life={actor.LifePoints} armor={actor.Armor} move={actor.ControlMode}   waypoints={Lba1TrackScript.Points(actor.TrackScript).Count}";
            return (stats, Disassembly.Text(actor.LifeScript, actor.TrackScript, Opcodes.Lba1));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            DebugLog.Log($"MainWindow: LBA1 actor {key} not described: {error.Message}");
            return null;
        }
    }

    private void OpenLba1ScriptWindow(int key)
    {
        if (openScriptWindows.TryGetValue(key, out var existing))
        {
            existing.ShowActor(key);
            existing.Activate();
            return;
        }
        var window = new ActorScriptWindow(null, lba1Session, ResolveLba1Source, DescribeLba1Actor, () => { zoneCache.Clear(); ReloadLba1AfterEdit(); }) { Owner = this };
        WindowLifecycle.Register(window, "ActorScriptWindow");
        window.Closed += (_, _) => openScriptWindows.Remove(key);
        openScriptWindows[key] = window;
        window.ShowActor(key);
    }

    // ---- zones ---------------------------------------------------------------
    // Scene trigger boxes drawn as coloured wireframes, in both the outdoor and the
    // indoor view. The Zones tab toggles them (all, or per type).
    private bool zonesVisible = true;
    // Cube-change and camera zones are large and numerous (whole cube edges, whole rooms) and bury
    // the view, so they start hidden; the Zones tab turns them on.
    private readonly bool[] zoneTypeVisible = Enumerable.Range(0, ZoneStyle.TypeCount).Select(t => t > 1).ToArray();
    private InteriorOverlay interiorOverlay = InteriorOverlay.Empty;
    private List<ProjectedZone>? lastNativeZones;

    private bool ZoneShown(int type) => zonesVisible && (uint)type < zoneTypeVisible.Length && zoneTypeVisible[type];

    // The Zones tab: one checkbox per zone type, tinted with that type's colour.
    private void BuildZoneList()
    {
        for (var type = 0; type < ZoneStyle.TypeCount; type++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 10, Height = 10, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(ZoneStyle.ColorOf(type)),
            });
            row.Children.Add(new TextBlock { Text = ZoneStyle.NameOf(type), Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x24, 0x3E)) });
            var check = new CheckBox { Content = row, Tag = type, IsChecked = zoneTypeVisible[type], Margin = new Thickness(0, 0, 0, 8) };
            check.Click += ZoneType_Click;
            ZoneTypeList.Children.Add(check);
        }
    }

    // Actor patrol routes (dashed lines with a flag per waypoint) in the main views and on the minimap.
    private bool pathsVisible = true;

    private void Paths_Click(object sender, RoutedEventArgs e)
    {
        pathsVisible = PathsCheck.IsChecked == true;
        RefreshActorOverlayForSelection();
        if (!sceneMinimapActive && nativeViewActive) DrawActorMarkers();
        SyncPlayOverlay();
    }

    // The selected actor's yellow ring and the selected zone's thick white outline, in every view.
    private bool highlightSelection = EditorSettings.Current.HighlightSelection;

    private void Highlight_Click(object sender, RoutedEventArgs e)
    {
        highlightSelection = HighlightCheck.IsChecked == true;
        EditorSettings.Current.HighlightSelection = highlightSelection;
        try { EditorSettings.Current.Save(); } catch (Exception error) { DebugLog.Log($"MainWindow: settings save failed: {error.Message}"); }
        RefreshActorOverlayForSelection();
    }

    private void AddSelectionRing(double centerX, double centerY, double width, double height)
    {
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = width + 6,
            Height = height + 6,
            Stroke = Brushes.Yellow,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(ring, centerX - ring.Width / 2);
        Canvas.SetTop(ring, centerY - ring.Height / 2);
        ActorMarkerCanvas.Children.Add(ring);
    }

    private void ZoneMaster_Click(object sender, RoutedEventArgs e)
    {
        zonesVisible = ZoneMasterCheck.IsChecked == true;
        RefreshActorOverlayForSelection();
        RefreshZoneListIfVisible();
        SyncPlayOverlay();
    }

    private void ZoneType_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: int type } check && (uint)type < zoneTypeVisible.Length) zoneTypeVisible[type] = check.IsChecked == true;
        RefreshActorOverlayForSelection();
        RefreshZoneListIfVisible();
        SyncPlayOverlay();
    }

    private bool minimapCollapsed;

    private void MinimapToggle_Click(object sender, RoutedEventArgs e)
    {
        minimapCollapsed = !minimapCollapsed;
        MinimapBody.Visibility = minimapCollapsed ? Visibility.Collapsed : Visibility.Visible;
        MinimapToggle.Content = minimapCollapsed ? "▴" : "▾";
        MinimapToggle.ToolTip = minimapCollapsed ? "Expand minimap" : "Collapse minimap";
    }

    // Draws `zones` (corners run through `map` into overlay coordinates; the view is
    // width x height). Zones sit under the actors' click targets; clicking a zone's outline
    // selects it for the DETAILS tab, and the selected zone is drawn thick and white.
    private void AddZoneShapes(IEnumerable<ProjectedZone> zones, Func<Point, Point> map, double width, double height)
    {
        foreach (var zone in zones)
        {
            var selected = highlightSelection && zone.Ref is not null && zone.Ref == selectedZoneRef;
            if (!ZoneShown(zone.Type) && !selected) continue;
            var pts = zone.Corners.Select(map).ToArray();
            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            if (maxX < -20 || minX > width + 20 || maxY < -20 || minY > height + 20) continue;
            if (maxX - minX > width * 6 || maxY - minY > height * 6) continue;   // camera-plane blow-up: not worth drawing

            var color = ZoneStyle.ColorOf(zone.Type);
            Brush brush = selected ? Brushes.White : new SolidColorBrush(color);
            var geometry = ZoneStyle.Wireframe(new ProjectedZone(zone.Type, zone.Num, pts), p => p);
            ActorMarkerCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geometry,
                Stroke = brush,
                StrokeThickness = selected ? 2.8 : 1.3,
                Opacity = selected ? 1 : 0.9,
                IsHitTestVisible = false,
            });

            // A fat invisible stroke over the same edges is the click target (the inside stays free for panning).
            if (zone.Ref is not null)
            {
                var hit = new System.Windows.Shapes.Path
                {
                    Data = geometry,
                    Stroke = Brushes.Transparent,
                    StrokeThickness = 9,
                    Cursor = Cursors.Hand,
                    Tag = zone.Ref,
                };
                hit.MouseLeftButtonDown += ZoneShape_MouseLeftButtonDown;
                ActorMarkerCanvas.Children.Add(hit);
            }

            if (maxX - minX >= 46)
            {
                var label = new TextBlock
                {
                    Text = $"{ZoneStyle.NameOf(zone.Type)} {zone.Num}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    Foreground = brush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(label, (pts[2].X + pts[3].X + pts[6].X + pts[7].X) / 4 - 24);
                Canvas.SetTop(label, (pts[2].Y + pts[3].Y + pts[6].Y + pts[7].Y) / 4 - 7);
                ActorMarkerCanvas.Children.Add(label);
            }
        }
    }

    // ---- Edit > Undo / Redo: the application-wide log of changes saved to the game files (Scenes.SceneHistory) ----

    private void EditMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        // With the terrain editor on screen Edit > Undo / Redo are its own history.
        var undo = terrainToolsActive && terrainEditor is not null ? terrainEditor.UndoLabel : Scenes.SceneHistory.UndoDescription;
        var redo = terrainToolsActive && terrainEditor is not null ? terrainEditor.RedoLabel : Scenes.SceneHistory.RedoDescription;
        UndoMenuItem.Header = undo is null ? "_Undo" : $"_Undo {undo}";
        UndoMenuItem.IsEnabled = undo is not null;
        RedoMenuItem.Header = redo is null ? "_Redo" : $"_Redo {redo}";
        RedoMenuItem.IsEnabled = redo is not null;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { if (terrainToolsActive && terrainEditor is not null) terrainEditor.Undo(); else RunHistoryStep(undo: true); }

    private void Redo_Click(object sender, RoutedEventArgs e) { if (terrainToolsActive && terrainEditor is not null) terrainEditor.Redo(); else RunHistoryStep(undo: false); }

    private void RunHistoryStep(bool undo)
    {
        var next = undo ? Scenes.SceneHistory.NextUndo : Scenes.SceneHistory.NextRedo;
        if (next is null) return;
        var scenes = next.After.Select(s => s.Scene).ToList();

        // Same guards as saving a zone: an unsaved script edit or an open LBA2 actor window would be overwritten.
        var pendingScripts = next.Game == Scenes.SceneGame.Lba1 ? lba1Session.EditedScenes : scriptSession.EditedScenes;
        if (scenes.Any(pendingScripts.Contains))
        {
            MessageBox.Show(this, "That scene has unsaved script edits. Save or discard them first, so they aren't overwritten.", undo ? "Undo" : "Redo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (next.Game == Scenes.SceneGame.Lba2 && !interiorSceneActive && openAttributesWindows.Count > 0)
        {
            MessageBox.Show(this, "Close the actor windows first: this reloads the island, which drops unsaved actor edits.", undo ? "Undo" : "Redo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if ((undo ? Scenes.SceneHistory.Undo() : Scenes.SceneHistory.Redo()) is null) return;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Nothing was changed: {error.Message}", undo ? "Undo" : "Redo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        zoneCache.Clear();
        if (next.Game == Scenes.SceneGame.Lba1)
        {
            foreach (var scene in scenes) lba1Session.ForgetScene(scene);
            if (currentGame == GameKind.Lba1 && lba1CurrentTiles is not null) ReloadLba1AfterEdit();
            else { lba1Game = null; lba1Images = null; }
        }
        else
        {
            foreach (var scene in scenes) scriptSession.ForgetScene(scene);
            if (currentGame == GameKind.Lba2)
            {
                if (interiorSceneActive) ShowInteriorScene(interiorSceneNumber, keepView: true);
                else
                {
                    InvalidateNativeIsland();
                    if (nativeViewActive) RenderNativeCamera();
                }
            }
        }
        RefreshZoneListIfVisible();
        FileLabel.Text = $"{(undo ? "Undid" : "Redid")}: {next.Description}";
    }

    // Tools > LBA1: Make surprise changes: connects the bedroom (scene 61) to Lupin Burg and adds the pink elf to it
    // (see Lba1SurpriseChanges).
    private void Lba1SurpriseChanges_Click(object sender, RoutedEventArgs e)
    {
        var directory = EditorSettings.Current.Lba1Directory;
        if (!Lba1Game.IsInstalled(directory))
        {
            MessageBox.Show(this, "The LBA1 game folder isn't set. Choose it under File > Settings.", "LBA1", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (lba1Session.EditedScenes.Any(s => s is 13 or 61))
        {
            MessageBox.Show(this, "Scenes 13 or 61 have unsaved script edits. Save or discard them first.", "LBA1", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // (deliberately says nothing about what the changes are: they are a surprise)
        var confirm = MessageBox.Show(this,
            $"Make the surprise changes to the LBA1 game files?\n\nFolder: {directory}\n\nThe original files are kept as .bak copies.", Lba1SurpriseChanges.Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            var result = Lba1SurpriseChanges.Apply(directory);
            MessageBox.Show(this, result.Changed ? "Done. The surprise changes are made." : "The surprise changes are already made; nothing to change.",
                Lba1SurpriseChanges.Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Nothing was changed: {error.Message}", Lba1SurpriseChanges.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        zoneCache.Clear();
        lba1Session.ForgetScene(13);
        lba1Session.ForgetScene(61);
        if (currentGame == GameKind.Lba1 && lba1CurrentTiles is not null) ReloadLba1AfterEdit();
        else { lba1Game = null; lba1Images = null; }
    }

    // Tools > LBA2: Fix scripting errors: patches the known retail SCENE.HQR script bugs (see Lba2ScriptFixes).
    private void Lba2FixScriptingErrors_Click(object sender, RoutedEventArgs e)
    {
        if (!Lba2Engine.IsGameFolder(gameRoot))
        {
            MessageBox.Show(this, "The LBA2 game folder isn't set. Choose it under File > Settings.", Lba2ScriptFixes.Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (scriptSession.EditedScenes.Any(s => Lba2ScriptFixes.TargetScenes.Contains(s)))
        {
            MessageBox.Show(this, "Scenes 36, 100 or 180 have unsaved script edits. Save or discard them first.", Lba2ScriptFixes.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirm = MessageBox.Show(this,
            "Fix three known retail script bugs?\n\n" +
            "- White Leaf Desert Bazaar (scene 36): buying the holomap never actually unlocks it.\n" +
            "- Wannies Island mine (scene 100): the Dino-Fly clover box can be farmed repeatedly.\n" +
            "- Island CX (scene 180): its clover box shares a flag with scene 129's, so collecting one breaks the other.\n\n" +
            $"Folder: {gameRoot}\n\nThe original SCENE.HQR is kept as a .bak copy.",
            Lba2ScriptFixes.Title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        Lba2ScriptFixes.FixResult result;
        using (UiBusy.Cursor())
        {
            try { result = Lba2ScriptFixes.Apply(scriptSession); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show(this, $"Nothing was changed: {error.Message}", Lba2ScriptFixes.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        MessageBox.Show(this, result.Message, Lba2ScriptFixes.Title, MessageBoxButton.OK, result.Changed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (!result.Changed) return;

        zoneCache.Clear();
        foreach (var scene in result.TouchedScenes) scriptSession.ForgetScene(scene);
        if (currentGame == GameKind.Lba2)
        {
            if (interiorSceneActive && result.TouchedScenes.Contains(interiorSceneNumber)) ShowInteriorScene(interiorSceneNumber, keepView: true);
            else if (!interiorSceneActive) { InvalidateNativeIsland(); if (nativeViewActive) RenderNativeCamera(); }
        }
        RefreshZoneListIfVisible();
    }

    // Tools > LBA2: edit a scene as data on a plan of it (Lba2SceneEditorWindow).
    private void Lba2Editor_Click(object sender, RoutedEventArgs e)
    {
        if (!Lba2Engine.IsGameFolder(gameRoot))
        {
            MessageBox.Show(this, "The LBA2 game folder isn't set. Choose it under File > Settings.", "LBA2", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var start = Lba2SceneToPlay();
        try { var w = new Lba2SceneEditorWindow(gameRoot, start, EditLba2ScriptFromEditor) { Owner = this }; WindowLifecycle.Register(w, "Lba2SceneEditorWindow"); w.Show(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            DebugLog.Log($"MainWindow: LBA2 scene editor failed: {error}");
            MessageBox.Show(this, $"Couldn't open the scene editor: {error.Message}", "LBA2: scene editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Tools > interior grid editor: the isometric maps of both games' interiors (paint blocks, edit the block library).
    private void GridEditor_Click(object sender, RoutedEventArgs e)
    {
        var lba1 = EditorSettings.Current.Lba1Directory;
        var lba2 = File.Exists(Path.Combine(gameRoot, "LBA_BKG.HQR")) ? gameRoot : null;
        if (!Lba1Game.IsInstalled(lba1) && lba2 is null)
        {
            MessageBox.Show(this, "Neither game folder is set. Choose them under File > Settings.", "Interior grid editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Func<int, string?>? describe = null;
            if (Lba1Game.IsInstalled(lba1)) { var game = new Lba1Game(lba1); describe = game.Description; }
            var gridWindow = new GridEditorWindow(Lba1Game.IsInstalled(lba1) ? lba1 : null, lba2, describe) { Owner = this };
            WindowLifecycle.Register(gridWindow, "GridEditorWindow");
            gridWindow.Show();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't open the grid editor: {error.Message}", "Interior grid editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Tools > objects and bodies: every body of BODY.HQR and LBA2's OBJFIX.HQR, drawn with textures and lighting.
    private void ObjectBrowser_Click(object sender, RoutedEventArgs e)
    {
        var lba1 = EditorSettings.Current.Lba1Directory;
        var lba2 = File.Exists(Path.Combine(gameRoot, "BODY.HQR")) ? gameRoot : null;
        if (!Lba1Game.IsInstalled(lba1) && lba2 is null)
        {
            MessageBox.Show(this, "Neither game folder is set. Choose them under File > Settings.", "Objects and bodies", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { var w = new ObjectBrowserWindow(lba1, lba2) { Owner = this }; WindowLifecycle.Register(w, "ObjectBrowserWindow"); w.Show(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't open the object browser: {error.Message}", "Objects and bodies", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Tools > bricks and sprites: the game's run-length pictures with a pixel editor, PNG import / export.
    private void AssetEditor_Click(object sender, RoutedEventArgs e)
    {
        var lba1 = EditorSettings.Current.Lba1Directory;
        var lba2 = Lba2Engine.IsGameFolder(gameRoot) || File.Exists(Path.Combine(gameRoot, "LBA_BKG.HQR")) ? gameRoot : null;
        if (!Lba1Game.IsInstalled(lba1) && lba2 is null)
        {
            MessageBox.Show(this, "Neither game folder is set. Choose them under File > Settings.", "Bricks and sprites", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { var w = new AssetEditorWindow(lba1, lba2) { Owner = this }; WindowLifecycle.Register(w, "AssetEditorWindow"); w.Show(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't open the picture editor: {error.Message}", "Bricks and sprites", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // The scene editor hands an actor's scripts to the script editor (which saves through the scene store).
    private void EditLba2ScriptFromEditor(int scene, int actor)
    {
        var key = 1_000_000 + scene * 1000 + actor;        // its own key range: the LBA2 editor windows don't collide with the LBA1 ones
        if (openScriptWindows.TryGetValue(key, out var existing)) { existing.ShowActor(key); existing.Activate(); return; }
        var window = new ActorScriptWindow(null, scriptSession, k => new ActorSource((k - 1_000_000) / 1000, (k - 1_000_000) % 1000)) { Owner = this };
        window.Closed += (_, _) => openScriptWindows.Remove(key);
        openScriptWindows[key] = window;
        window.ShowActor(key);
    }

    // Tools > LBA1: edit a scene as data (Lba1SceneEditorWindow): actors, zones and track points on its map.
    private void Lba1Editor_Click(object sender, RoutedEventArgs e)
    {
        var directory = EditorSettings.Current.Lba1Directory;
        if (!Lba1Game.IsInstalled(directory))
        {
            MessageBox.Show(this, "The LBA1 game folder isn't set. Choose it under File > Settings.", "LBA1", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var start = currentGame == GameKind.Lba1 && lba1CurrentTiles is { Count: > 0 } tiles ? tiles[0].Scene : 0;
        try
        {
            var sceneWindow = new Lba1SceneEditorWindow(directory, start, EditLba1ActorFromEditor) { Owner = this };
            WindowLifecycle.Register(sceneWindow, "Lba1SceneEditorWindow");
            sceneWindow.Show();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            DebugLog.Log($"MainWindow: LBA1 scene editor failed: {error}");
            MessageBox.Show(this, $"Couldn't open the scene editor: {error.Message}", "LBA1: scene editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // The scene editor hands an actor over to the full attribute dialog or the script editor, which work on the scene shown here.
    private void EditLba1ActorFromEditor(int scene, int actor, bool script)
    {
        if (currentGame != GameKind.Lba1 || lba1Game is null)
        {
            MessageBox.Show(this, "Switch the main window to LBA1 (the Game selector) to use the full editors.", "LBA1", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        zoneCache.Clear();
        lba1Session.ForgetScene(scene);
        ReloadLba1AfterEdit();
        var result = GoToLba1Scene(scene);
        DebugLog.Log($"MainWindow: editor asked for scene {scene} actor {actor}: {result}");
        if (script) OpenLba1ScriptWindow(scene * 1000 + actor);
        else OpenLba1ActorWindow(scene * 1000 + actor, edit: true);
    }

    // After a zone edit was saved: read the LBA1 files again and redraw the same scene(s).
    private void ReloadLba1AfterEdit()
    {
        var tiles = lba1CurrentTiles;
        if (tiles is null) return;
        try
        {
            lba1Game = new Lba1Game(EditorSettings.Current.Lba1Directory);
            lba1Images = new Lba1ActorImages(lba1Game);
        }
        catch (Exception error)
        {
            DebugLog.Log($"MainWindow: LBA1 reload failed: {error}");
            return;
        }
        ShowLba1Tiles(tiles, keepView: true);
    }

    private void DrawInteriorActorOverlay()
    {
        ActorMarkerCanvas.Children.Clear();
        if (!interiorSceneActive) return;
        var vw = ViewportHost.ActualWidth;
        var vh = ViewportHost.ActualHeight;
        Point ToView(Point p) => new((p.X - interiorCenter.X) * interiorZoom + vw / 2, (p.Y - interiorCenter.Y) * interiorZoom + vh / 2);

        AddZoneShapes(interiorOverlay.Zones, ToView, vw, vh);

        // Patrol routes, drawn the same way as outdoors: a dashed line through the
        // actor's track waypoints with a flag on each.
        foreach (var (actorIndex, canvasPoints) in pathsVisible ? interiorOverlay.Routes : new())
        {
            var brush = new SolidColorBrush(ActorRouteColors[actorIndex % ActorRouteColors.Length]);
            var points = new PointCollection(canvasPoints.Select(ToView));
            ActorMarkerCanvas.Children.Add(new System.Windows.Shapes.Polyline
            {
                Points = points,
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Opacity = 0.85,
                IsHitTestVisible = false,
            });
            for (var w = 1; w < points.Count; w++)
                ActorMarkerCanvas.Children.Add(CreateRouteFlag(points[w].X, points[w].Y, brush));
        }

        foreach (var (index, x, y, halfWidth, halfHeight, isMarker) in interiorActors)
        {
            var sx = (x - interiorCenter.X) * interiorZoom + vw / 2;
            var sy = (y - interiorCenter.Y) * interiorZoom + vh / 2;
            if (sx < -40 || sx > vw + 40 || sy < -40 || sy > vh + 40) continue;
            if (isMarker) AddDummyMarker(sx, sy, halfHeight * 2 * interiorZoom);
            else if (lba1ActorMarkers.TryGetValue(index, out var body)) AddBodyMarker(body.Marker.Image, sx, sy, halfHeight * interiorZoom);
            var hit = new System.Windows.Shapes.Ellipse
            {
                Width = Math.Max(halfWidth * 2 * interiorZoom, 20),
                Height = Math.Max(halfHeight * 2 * interiorZoom, 20),
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = index,
            };
            if (highlightSelection && selectedActorIndex == index) AddSelectionRing(sx, sy, hit.Width, hit.Height);
            if (lba2JoinedView) hit.MouseLeftButtonDown += JoinedLba2Actor_MouseLeftButtonDown;
            else
            {
                hit.MouseLeftButtonDown += ActorMarker_MouseLeftButtonDown;
                hit.MouseRightButtonDown += ActorMarker_MouseRightButtonDown;
            }
            Canvas.SetLeft(hit, sx - hit.Width / 2);
            Canvas.SetTop(hit, sy - hit.Height / 2);
            ActorMarkerCanvas.Children.Add(hit);
        }
        UpdatePlacementMarker();
    }

    // Zooms by `factor`, keeping the canvas point under `anchor` (viewport
    // coordinates) fixed.
    private void ZoomInterior(double factor, Point? anchor = null)
    {
        if (interiorZoom <= 0) return;
        var vw = ViewportHost.ActualWidth;
        var vh = ViewportHost.ActualHeight;
        var a = anchor ?? new Point(vw / 2, vh / 2);
        var before = new Point((a.X - vw / 2) / interiorZoom + interiorCenter.X, (a.Y - vh / 2) / interiorZoom + interiorCenter.Y);
        interiorZoom = Math.Clamp(interiorZoom * factor, InteriorFitZoom(), InteriorMaxZoom);
        interiorCenter = new Point(before.X - (a.X - vw / 2) / interiorZoom, before.Y - (a.Y - vh / 2) / interiorZoom);
        ApplyInteriorView();
    }

    // Simple-marker actor overlay for the main 3D view: projects each actor's
    // world position with the exact camera SoftwareTerrainRenderer just used
    // (see SoftwareTerrainRenderer.TryProjectWorldPoint) and drops a clickable
    // dot on top of the rendered frame. Only meaningful while the software
    // camera is active -- the native renderer's camera/perspective isn't
    // reproduced in C#, so markers are cleared instead of drawn at a wrong
    // position while nativeViewActive. Body-mesh rendering is intentionally
    // out of scope for this first pass; double-clicking a marker (or its
    // context menu) opens the full ActorAttributesWindow/ActorScriptWindow
    // instead -- a single click only selects/highlights it.
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

            if (pathsVisible && waypointCount > 0)
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

            var selected = highlightSelection && selectedActorIndex == i;
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
            dot.MouseRightButtonDown += ActorMarker_MouseRightButtonDown;
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
    private List<(int Index, double ScreenX, double ScreenY, double HitHalfWidth, double HitHalfHeight)>? lastNativeActorScreens;
    private HashSet<int>? lastNativeInvisibleActors;
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
        if (lastNativeZones is not null)
            AddZoneShapes(lastNativeZones, p => new Point(p.X * scaleX, p.Y * scaleY), width, height);

        if (pathsVisible && lastNativeActorRoutes is not null)
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

        foreach (var (index, sx, sy, hitHalfWidth, hitHalfHeight) in lastNativeActorScreens)
        {
            var screenX = sx * scaleX;
            var screenY = sy * scaleY;
            if (screenX < -20 || screenX > width + 20 || screenY < -20 || screenY > height + 20) continue;

            // Sized to the actor's own real body bounds (projected alongside
            // its position in the same locked render pass -- see where
            // lastNativeActorScreens gets built) instead of a fixed 24x24,
            // so a large/close body is fully clickable and a small/far one
            // doesn't get an oversized target -- both were reported as hard
            // to click reliably before this.
            var hitWidth = Math.Max(hitHalfWidth * 2 * scaleX, 20);
            var hitHeight = Math.Max(hitHalfHeight * 2 * scaleY, 20);
            if (lastNativeInvisibleActors?.Contains(index) == true) AddDummyMarker(screenX, screenY, hitHeight);
            if (highlightSelection && selectedActorIndex == index) AddSelectionRing(screenX, screenY, hitWidth, hitHeight);
            var hit = new System.Windows.Shapes.Ellipse
            {
                Width = hitWidth,
                Height = hitHeight,
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = index,
            };
            if (lba2JoinedView) hit.MouseLeftButtonDown += JoinedLba2Actor_MouseLeftButtonDown;
            else
            {
                hit.MouseLeftButtonDown += ActorMarker_MouseLeftButtonDown;
                hit.MouseRightButtonDown += ActorMarker_MouseRightButtonDown;
            }
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
        if (interiorSceneActive) DrawInteriorActorOverlay(); else if (nativeViewActive) DrawNativeActorOverlay(); else UpdateActorMarkersOverlay();
    }

    // Right-clicking empty terrain (anywhere that isn't an actor marker --
    // ActorMarker_MouseRightButtonDown handles those and marks the event
    // Handled, which stops it bubbling up to this container handler) offers
    // adding a new actor. It spawns at the current camera target rather than
    // the exact clicked point -- this editor has no screen-to-world terrain
    // raycast today, only the camera-target/pan math already used elsewhere
    // -- so the new actor lands where the camera is looking, with the
    // attributes window open right away to reposition it precisely.
    private void TerrainViewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (currentIsland is null || !nativeViewActive || editMode != EditMode.Build || TerrainPaintActive) return;
        var library = nativeRenderer.RendererLibrary;
        if (library is null) return;
        e.Handled = true;

        var menu = new ContextMenu();
        var addHere = new MenuItem { Header = "Add Actor Here" };
        addHere.Click += (_, _) =>
        {
            DebugLog.Log($"MainWindow: Add Actor Here clicked at world=({targetX},{targetY},{targetZ})");
            var index = library.AddActor((int)targetX, (int)targetY, (int)targetZ, 0, 0, 0, 255, 0, 0, 0);
            if (index < 0) { DebugLog.Log("MainWindow: AddActor rejected (cube at actor limit)"); MessageBox.Show(this, "Couldn't add an actor here -- this cube may already be at its 100-actor limit.", "Add Actor", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            DebugLog.Log($"MainWindow: AddActor returned index={index}");
            selectedActorIndex = index;
            RenderNativeCamera();
            OpenActorAttributesWindow(index);
        };
        menu.Items.Add(addHere);
        ((FrameworkElement)sender).ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ActorMarker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not int index) return;
        e.Handled = true;
        selectedActorIndex = index;
        RefreshActorOverlayForSelection();
        // Keep an already-open script window in sync with whichever actor is
        // now selected, but don't pop one open on every click -- only
        // explicit actions (double-click, context menu) do that.
        if (openScriptWindows.TryGetValue(index, out var openScript)) openScript.ShowActor(index);
        // Script opens the script at once; a double-click opens the actor's attributes (Explore: view only, Build: to edit).
        if (editMode == EditMode.Script) OpenActorScriptWindow(index);
        else if (e.ClickCount >= 2)
        {
            // (in Explore mode the window opens too, view only)
            if (currentGame == GameKind.Lba1) OpenLba1ActorWindow(index);
            else OpenActorAttributesWindow(index);
        }
        if (editMode == EditMode.Script && ScriptTab.IsSelected) SyncScriptListSelection(index);
    }

    // One independent, non-modal window per actor per kind (script/
    // attributes), each with its own taskbar entry -- keyed by actor index so
    // double-clicking (or right-clicking) the same actor again re-activates
    // its existing window instead of spawning a duplicate, while a different
    // actor still gets its own. Entries are removed as soon as their window
    // actually closes (Closed, not just hidden), so re-opening the same
    // actor later creates a fresh window rather than resurrecting a stale one.
    private readonly Dictionary<int, ActorScriptWindow> openScriptWindows = new();
    private readonly Dictionary<int, ActorAttributesWindow> openAttributesWindows = new();

    // Which SCENE.HQR scene / object slot a viewer actor index came from, so the
    // script window can show and edit its real scripts. Interior scenes list
    // their objects 1..N-1 in order; an island lists every exterior scene's
    // objects in scene order (the same walk RendererScanIslandActors does), and
    // actors added in the editor sit after those and have no record (null).
    private ActorSource? ResolveActorSource(int actorIndex)
    {
        if (interiorSceneActive) return interiorSceneNumber >= 0 ? new ActorSource(interiorSceneNumber, actorIndex + 1) : null;
        var island = Array.IndexOf(IslandNameByRawSceneId, Path.GetFileNameWithoutExtension(activeFile));
        return scriptSession.ExteriorActor(island, actorIndex);
    }

    private void OpenActorScriptWindow(int index)
    {
        if (currentGame == GameKind.Lba1) { OpenLba1ScriptWindow(index); return; }
        if (openScriptWindows.TryGetValue(index, out var existing))
        {
            existing.ShowActor(index);
            existing.Activate();
            return;
        }
        var window = new ActorScriptWindow(nativeRenderer.RendererLibrary, scriptSession, ResolveActorSource) { Owner = this };
        WindowLifecycle.Register(window, "ActorScriptWindow");
        window.Closed += (_, _) => openScriptWindows.Remove(index);
        openScriptWindows[index] = window;
        window.ShowActor(index);
    }

    private void OpenActorAttributesWindow(int index, bool edit = false)
    {
        if (openAttributesWindows.TryGetValue(index, out var existing))
        {
            existing.Activate();
            return;
        }
        DebugLog.Log($"MainWindow: opening ActorAttributesWindow for index={index}");
        var window = new ActorAttributesWindow(nativeRenderer, palette, index) { Owner = this };
        WindowLifecycle.Register(window, "ActorAttributesWindow");
        window.OpenScriptRequested += OpenActorScriptWindow;
        // The window's own Closed handler undoes RendererAddActor if this
        // was a freshly-added actor never applied -- refresh here in case
        // that just happened, so its marker doesn't linger on screen until
        // some unrelated redraw happens to come along.
        window.Closed += (_, _) =>
        {
            openAttributesWindows.Remove(index);
            if (switchingGame) return;
            if (selectedActorIndex == index) selectedActorIndex = null;
            if (interiorSceneActive) ShowInteriorScene(interiorSceneNumber, keepView: true);
            else RenderNativeCamera();
        };
        openAttributesWindows[index] = window;
        if (!edit && editMode == EditMode.Explore) window.MakeViewOnly();
        window.Show();
    }

    private void ActorMarker_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not int index) return;
        e.Handled = true;
        selectedActorIndex = index;
        RefreshActorOverlayForSelection();
        // Explore changes nothing, so it has no menu. Build edits the actor (its script is a mode away); Script only has scripts.
        if (editMode == EditMode.Explore) return;

        var menu = new ContextMenu();
        var lba1 = currentGame == GameKind.Lba1;
        if (editMode == EditMode.Build)
        {
            var editAttributes = new MenuItem { Header = "Edit Attributes…" };
            editAttributes.Click += (_, _) => { if (lba1) OpenLba1ActorWindow(index, edit: true); else OpenActorAttributesWindow(index, edit: true); };
            menu.Items.Add(editAttributes);
        }
        var editScript = new MenuItem { Header = editMode == EditMode.Build ? "Edit Script… (switches to Script mode)" : "Edit Script…" };
        editScript.Click += (_, _) => { SetMode(EditMode.Script); OpenActorScriptWindow(index); };
        menu.Items.Add(editScript);
        ((FrameworkElement)sender).ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void SyncScriptListSelection(int index)
    {
        scriptListSyncing = true;
        try { ScriptActorList.SelectedItem = ScriptActorList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is int t && t == index); }
        finally { scriptListSyncing = false; }
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
        if (interiorSceneActive) { interiorCenter = new Point(e.NewValue + ViewportHost.ActualWidth / interiorZoom / 2, interiorCenter.Y); ApplyInteriorView(); return; }
        if (currentIsland is null) return;
        if (IsWorldPositionOnIsland(e.NewValue, targetZ)) targetX = e.NewValue;
        else PanHorizontalScrollBar.Value = targetX;
        UpdateMinimapMarker();
        if (nativeViewActive) RenderNativeCamera(); else RenderSoftwareTerrain();
    }

    private void PanVerticalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (interiorSceneActive) { interiorCenter = new Point(interiorCenter.X, e.NewValue + ViewportHost.ActualHeight / interiorZoom / 2); ApplyInteriorView(); return; }
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
        if (sceneMinimapActive) return;
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
                    DebugLog.Log($"MainWindow: native minimap for {islandName} returned null ({nativeRenderer.DirectFailure})");
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
                // A scene is on screen: keep the island's minimap for when it is left.
                if (sceneMinimapActive) { savedIslandMinimap = task.Result; return; }
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
        // RenderNativeCamera reads the checkbox and applies it inside the render lock.
        if (nativeViewActive) RenderNativeCamera();
    }

    private void Minimap_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Same outdoor-coordinate-space assumption as MainWindow_KeyDown's
        // own guard above -- the minimap always shows the current outdoor
        // island regardless of whether an interior scene is on screen, and
        // clicking it while one is would otherwise silently overwrite the
        // pan scrollbars' interior-mode range/value with stale outdoor
        // coordinates without actually switching the view back.
        if (sceneMinimapActive)
        {
            if (MinimapImage.Source is null || !interiorSceneActive) return;
            var spot = e.GetPosition((Grid)sender);
            interiorCenter = new Point(spot.X / sceneMinimapScale + sceneMinimapOrigin.X, spot.Y / sceneMinimapScale + sceneMinimapOrigin.Y);
            ApplyInteriorView();
            return;
        }
        if (interiorSceneActive || currentIsland is null || MinimapImage.Source is null) return;
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
        if (interiorSceneActive) { ZoomLabel.Text = $"{Math.Round(interiorZoom * 100)}%"; return; }
        var distance = nativeViewActive ? nativeDistance : cameraDistance;
        ZoomLabel.Text =$"{Math.Round(DefaultCameraDistance / distance * 100)}%";
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
        if (interiorSceneActive)
        {
            interiorZoom = percent / 100.0;
            ApplyInteriorView();
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

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (currentGame == GameKind.Lba1) { FitInteriorView(); return; }
        if (!Lba2Configured) return;
        LoadIsland(Path.Combine(gameRoot, activeFile));
    }

    private void FitInteriorView()
    {
        interiorZoom = 0; // clamped up to the fit zoom
        interiorCenter = new Point(interiorContent.X + interiorContent.Width / 2, interiorContent.Y + interiorContent.Height / 2);
        ApplyInteriorView();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void BodyStudio_Click(object sender, RoutedEventArgs e) => BodyStudioLauncher.Show(this);
    private void ViewFit_Click(object sender, RoutedEventArgs e)
    {
        if (terrainShown && terrainEditor is not null) terrainEditor.Fit();
        else if (interiorSceneActive) FitInteriorView();
        else Reset_Click(sender, e);
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { if (terrainShown) { terrainEditor?.ZoomBy(1.25); return; } if (interiorSceneActive) { ZoomInterior(1.25); return; } if (nativeViewActive) { nativeDistance = Math.Max(3000, nativeDistance - 4000); RenderNativeCamera(); } else { cameraDistance = Math.Max(12000, cameraDistance - 4000); RenderSoftwareTerrain(); } UpdateZoomLabel(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (terrainShown) { terrainEditor?.ZoomBy(1 / 1.25); return; } if (interiorSceneActive) { ZoomInterior(1 / 1.25); return; } if (nativeViewActive) { nativeDistance = Math.Min(50000, nativeDistance + 4000); RenderNativeCamera(); } else { cameraDistance = Math.Min(120000, cameraDistance + 4000); RenderSoftwareTerrain(); } UpdateZoomLabel(); }
    // Gated on RotateViewCheckBox so the left mouse button can be freed up
    // for other uses (actor placement/selection, etc.) without it always
    // spinning the camera underneath whatever else is being clicked.
    private void TerrainViewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Interior scenes are only ever meant to be viewed from the one
        // fixed isometric angle Init3DView/CameraCenter set up natively --
        // rotate/tilt-with-mouse only ever makes sense for the outdoor 3D
        // view's own free-orbiting camera.
        if (interiorSceneActive)
        {
            // Left-drag pans the interior canvas.
            if (e.ChangedButton != MouseButton.Left) return;
            interiorPanning = true;
            lastMousePosition = e.GetPosition(ViewportHost);
            InteriorHost.CaptureMouse();
            return;
        }
        // A terrain tool is chosen (Build): the left button paints on the ground under the pointer, the right button orbits.
        var paint = TerrainPaintActive;
        if (paint && e.ChangedButton == MouseButton.Left)
        {
            var at = e.GetPosition(TerrainViewport);
            if (PickGround(at, out var gx, out var gz))
            {
                hoverCell = (gx, gz);
                if (terrainEditor!.PointerDown(gx, gz, PickToleranceCells(at))) { paintingTerrain = true; TerrainViewport.CaptureMouse(); }
                DrawTerrainOverlay();
            }
            else FileLabel.Text = "That point isn't on the island's ground (or the view is still drawing): point at the terrain.";
            e.Handled = true;
            return;
        }
        // The middle button drags the ground along, in every mode.
        if (e.ChangedButton == MouseButton.Middle && BeginPan3D(e.GetPosition(TerrainViewport))) { e.Handled = true; return; }
        orbiting = (e.ChangedButton == MouseButton.Left && !paint && RotateViewCheckBox.IsChecked == true) || (paint && e.ChangedButton == MouseButton.Right);
        if (!orbiting) return;
        lastMousePosition = e.GetPosition(TerrainViewport);
        TerrainViewport.CaptureMouse();
    }
    private bool interiorPanning;
    private void TerrainViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (interiorPanning)
        {
            var p = e.GetPosition(ViewportHost);
            interiorCenter = new Point(interiorCenter.X - (p.X - lastMousePosition.X) / interiorZoom, interiorCenter.Y - (p.Y - lastMousePosition.Y) / interiorZoom);
            lastMousePosition = p;
            ApplyInteriorView();
            return;
        }
        if (panning3D) { MovePan3D(e.GetPosition(TerrainViewport)); return; }
        if (paintingTerrain || (TerrainPaintActive && !orbiting))
        {
            if (PickGround(e.GetPosition(TerrainViewport), out var gx, out var gz))
            {
                hoverCell = (gx, gz);
                if (paintingTerrain) terrainEditor!.PointerMove(gx, gz);
                DrawTerrainOverlay();
            }
            if (paintingTerrain) return;
        }
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
    private void TerrainViewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (paintingTerrain && e.ChangedButton == MouseButton.Left) { terrainEditor?.PointerUp(); paintingTerrain = false; DrawTerrainOverlay(); }
        if (panning3D && e.ChangedButton == MouseButton.Middle) panning3D = false;
        orbiting = false; interiorPanning = false;
        if (!paintingTerrain && !panning3D) Mouse.Capture(null);
    }
    private void TerrainViewport_MouseWheel(object sender, MouseWheelEventArgs e) { if (interiorSceneActive) { ZoomInterior(e.Delta > 0 ? 1.15 : 1 / 1.15, e.GetPosition(ViewportHost)); return; } if (nativeViewActive) { nativeDistance = Math.Clamp(nativeDistance - (e.Delta > 0 ? 1200 : -1200), 3000, 50000); RenderNativeCamera(); } else { cameraDistance = Math.Clamp(cameraDistance - e.Delta * 40, 12000, 120000); RenderSoftwareTerrain(); } UpdateZoomLabel(); }
    private void RenderNativeCamera()
    {
        if (!nativeViewActive) return;
        UpdatePlayButton();
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
        _ = Task.Run(() =>
        {
            try { RunNativeRenderLoop(token); }
            catch (Exception error)
            {
                // An escaped exception would leave nativeRenderInFlight stuck
                // true, silently ignoring every later zoom/pan request.
                DebugLog.Log($"MainWindow: native render loop crashed: {error}");
                lock (nativeRenderGate) { nativeRenderInFlight = false; }
            }
        }, token);
    }

    private void RunNativeRenderLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            lock (nativeRenderGate) { nativeRenderDirty = false; }

            var islandName = Path.GetFileNameWithoutExtension(activeFile);
            var library = nativeRenderer.RendererLibrary;
            // Sky and sea are reasserted inside RenderIslandDirect's render lock (drawSky here):
            // the minimap turns them off for its own top-down snapshots and never turns them
            // back on, and setting them out here could land mid-minimap-render.
            // Always radius 2 (5x5 cubes): AffGrilleExtWide loads and draws
            // neighboring cubes into the same frame. Radius 1 used to be
            // chosen below 35000 camera distance (86%+ zoom), but the terrain
            // and sea of the ring-2 cubes are still in view at those
            // distances -- they visibly vanished at exactly that threshold.
            // The actor/waypoint overlay filter below uses this same radius
            // so it shows exactly the actors sitting on terrain this frame
            // drew, whichever cube each one is in.
            //
            // Never 0: the plain single-cube render path (RenderFrame()/
            // AffGrilleExt()) is broken at close zoom (washed-out, actors
            // vanishing) -- see lba2_renderer_render_frame's own comment
            // (RENDERER_API.CPP).
            var wideRadius = 2;
            var currentCubeX = (int)Math.Floor(targetX / 32768.0);
            var currentCubeY = (int)Math.Floor(targetZ / 32768.0);
            List<(int, double, double, double, double)>? projected = null;
            List<(int ActorIndex, List<Point> ScreenPoints)>? projectedRoutes = null;
            HashSet<int>? projectedInvisible = null;
            List<ProjectedZone>? projectedZones = null;

            int camX = (int)targetX, camY = (int)targetY, camZ = (int)targetZ, camDistance = nativeDistance;
            var bitmap = nativeRenderer.RenderIslandDirect(islandName, palette, camX, camY, camZ, nativeAlpha, nativeBeta, nativeGamma, camDistance,
                wideRadiusCubes: wideRadius,
                drawSky: desiredSkyEnabled,
                afterRenderBeforeUnlock: () =>
                {
                    if (library is null) return;
                    // The camera as a pinhole model, for turning mouse positions into ground points (see NativeCameraModel).
                    if (library.GetFramebuffer(out var fbWidth, out var fbHeight, out _) != IntPtr.Zero && fbWidth > 0 && fbHeight > 0)
                        cameraModel = NativeCameraModel.Fit((px, py, pz) => library.ProjectPoint(px, py, pz, out var psx, out var psy) ? (psx, psy) : null, camX, camY, camZ, camDistance, fbWidth, fbHeight);
                    var count = library.GetActorCount();
                    var list = new List<(int, double, double, double, double)>(count);
                    var hasBodyFlags = new List<bool>(count);
                    var invisibleSet = new HashSet<int>();
                    var routes = new List<(int, List<Point>)>();
                    for (var i = 0; i < count; i++)
                    {
                        if (!library.GetActor(i, out var x, out var y, out var z, out var waypointCount)) continue;
                        if (Math.Abs((int)Math.Floor(x / 32768.0) - currentCubeX) > wideRadius || Math.Abs((int)Math.Floor(z / 32768.0) - currentCubeY) > wideRadius) continue;
                        if (!library.ProjectPoint(x, y, z, out var sx, out var sy)) continue;

                        // Click target centred on the body's vertical middle
                        // (the projected point above is the actor's feet) and
                        // sized from its real body bounds -- cached natively
                        // per scene, so it is available for actors in any
                        // loaded cube, not just the live one. Projected in
                        // this same locked pass so it uses the identical
                        // camera as the position above (see the comment on
                        // DrawNativeActorOverlay). Actors without bounds
                        // (NO_BODY) fall back to a nominal ~2000-unit-tall
                        // body so a distant actor still gets a proportionate
                        // target.
                        double hitCenterY = sy, hitHalfW = 12, hitHalfH = 12;
                        var hasBounds = library.GetActorBounds(i, out var xMin, out var xMax, out var yMin, out var yMax, out var zMin, out var zMax);
                        var bodyBottom = hasBounds ? yMin : 0;
                        var bodyTop = hasBounds ? yMax : 2000;
                        if (library.ProjectPoint(x, y + bodyBottom, z, out var bx, out var by) && library.ProjectPoint(x, y + bodyTop, z, out var tx, out var ty))
                        {
                            hitCenterY = (by + ty) / 2.0;
                            var height = Math.Abs(by - ty);
                            var width = height;
                            if (hasBounds
                                && library.ProjectPoint(x + xMin, y + bodyTop, z + zMin, out var cx1, out var cy1)
                                && library.ProjectPoint(x + xMax, y + bodyTop, z + zMax, out var cx2, out var cy2))
                                width = Math.Max(width, Math.Max(Math.Abs(cx2 - cx1), Math.Abs(cy2 - cy1)));
                            hitHalfH = Math.Max(height, 12) / 2;
                            hitHalfW = Math.Max(Math.Max(width, height * .6), 12) / 2;
                        }
                        list.Add((i, sx, hitCenterY, hitHalfW, hitHalfH));
                        hasBodyFlags.Add(hasBounds);
                        if (!hasBounds) invisibleSet.Add(i); // no body: show the dummy body at its position

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
                    // Hit targets are stacked in list order, so body-less actors
                    // (invisible sound emitters, triggers) go underneath and
                    // visible bodies on top, biggest first so a small actor in
                    // front of a large one stays clickable. Otherwise an
                    // invisible actor's 20px target sitting over a car won't let
                    // you click the car.
                    projected = list
                        .Select((item, n) => (item, hasBody: hasBodyFlags[n]))
                        .OrderBy(e => e.hasBody ? 1 : 0)
                        .ThenByDescending(e => e.item.Item4 * e.item.Item5)
                        .Select(e => e.item)
                        .ToList();
                    projectedRoutes = routes;
                    projectedInvisible = invisibleSet;

                    // Scene trigger boxes of every cube in the drawn radius, projected with the same
                    // camera as the frame (like the actors above).
                    var zoneList = new List<ProjectedZone>();
                    var zoneCount = library.GetZoneCount();
                    var zoneOrdinals = new Dictionary<int, int>();
                    for (var zi = 0; zi < zoneCount; zi++)
                    {
                        // Position inside its scene's zone list, counted before any zone is skipped.
                        var zoneScene = library.GetZoneScene(zi);
                        var zoneIndex = zoneOrdinals.GetValueOrDefault(zoneScene);
                        zoneOrdinals[zoneScene] = zoneIndex + 1;
                        if (!library.GetZone(zi, out var zx0, out var zy0, out var zz0, out var zx1, out var zy1, out var zz1, out var ztype, out var znum)) continue;
                        var zcubeX = (int)Math.Floor((zx0 + zx1) / 2 / 32768.0);
                        var zcubeZ = (int)Math.Floor((zz0 + zz1) / 2 / 32768.0);
                        if (Math.Abs(zcubeX - currentCubeX) > wideRadius || Math.Abs(zcubeZ - currentCubeY) > wideRadius) continue;
                        var world = ZoneStyle.Corners(zx0, zy0, zz0, zx1, zy1, zz1);
                        var corners = new Point[8];
                        var visible = true;
                        for (var c = 0; c < 8 && visible; c++)
                        {
                            visible = library.ProjectPoint(world[c].X, world[c].Y, world[c].Z, out var cpx, out var cpy);
                            corners[c] = new Point(cpx, cpy);
                        }
                        if (visible) zoneList.Add(new ProjectedZone(ztype, znum, corners, zoneScene >= 0 ? new ZoneRef(2, zoneScene, zoneIndex) : null));
                    }
                    projectedZones = zoneList;
                });

            var stopLoop = false;
            if (!token.IsCancellationRequested)
            {
                Dispatcher.Invoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (interiorSceneActive || nativeRenderer.InteriorLoaded)
                    {
                        // An interior scene was chosen while this frame was rendering (or queued): the exterior view is gone.
                        stopLoop = true;
                        return;
                    }
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
                    lastNativeInvisibleActors = projectedInvisible;
                    lastNativeZones = projectedZones;
                    lastNativeActorRoutes = projectedRoutes;
                    DrawNativeActorOverlay();
                    DrawTerrainOverlay();
                    UpdatePlacementMarker();
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

            if (pathsVisible && waypointCount > 0)
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
    private void ViewportHost_SizeChanged(object sender, SizeChangedEventArgs e) { if (interiorSceneActive) ApplyInteriorView(); }
    private void TerrainViewport_SizeChanged(object sender, SizeChangedEventArgs e) { if (interiorSceneActive) return; if (nativeViewActive) DrawNativeActorOverlay(); else RenderSoftwareTerrain(); }
    private void Window_Loaded(object sender, RoutedEventArgs e) { }
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopPlay();
        SaveAudioIfDirty();
        if (!ConfirmTerrainDiscard()) { e.Cancel = true; return; }
        // Every other open window (actor attributes, script editors, the grid/object/asset editors, ...)
        // closes too, each running its own Closing handler (an unsaved script edit still asks first) -- if
        // any of them refuses, the main window's own close is cancelled rather than leaving them orphaned.
        if (!WindowLifecycle.CloseAll(except: this)) { e.Cancel = true; return; }
        StopLive();                       // the preview folder is removed and the process leaves it
        nativeRenderCancellation?.Cancel();
        nativeRenderer.ShutdownDirectRenderer();
    }
    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (playing) return;
        // Ctrl+Z / Ctrl+Y undo and redo saved scene changes; text boxes keep their own undo.
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Z or Key.Y && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            RunHistoryStep(undo: e.Key == Key.Z);
            e.Handled = true;
            return;
        }
        // Ctrl+S / Ctrl+O: the same File > Save / File > Open the menu offers (every other window that has
        // its own meaningful "save" already binds Ctrl+S the same way, to its own Save/Apply -- see
        // ActorScriptWindow, Lba1SceneEditorWindow, Lba2SceneEditorWindow, GridEditorWindow, AssetEditorWindow).
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.S or Key.O && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            if (e.Key == Key.S) Save_Click(sender, e); else Open_Click(sender, e);
            e.Handled = true;
            return;
        }
        // TryPan below assumes the outdoor island's own coordinate space
        // (IsWorldPositionOnIsland, SyncPanScrollBars writing targetX/Z) --
        // arrow-key panning isn't wired up for interior scenes (only the
        // scrollbars are, via RenderInteriorPan), and letting this run
        // anyway would silently overwrite the pan scrollbars' interior-mode
        // range/value with stale outdoor coordinates.
        if (interiorSceneActive || terrainShown) return;
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
    private void Open_Click(object sender, RoutedEventArgs e) { if (currentGame == GameKind.Lba1) return; var dialog = new OpenFileDialog { Filter = "LBA2 islands (*.ILE)|*.ILE|All files (*.*)|*.*", InitialDirectory = gameRoot }; if (dialog.ShowDialog() == true) LoadIsland(dialog.FileName); }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this };
        WindowPlacement.Attach(dialog, "SettingsWindow");
        if (dialog.ShowDialog() != true) return;
        if (dialog.ScriptNamesChanged) RefreshScriptStyle();
        if (dialog.Lba1DirectoryChanged)
        {
            lba1Game = null;
            lba1Images = null;
        }
        if (dialog.GameDirectoryChanged)
        {
            gameRoot = EditorSettings.Current.GameDirectory;
            nativeRenderer.SetGameDirectory(gameRoot);
            actorMarkersIsland = null;
        }
        if (dialog.GameDirectoryChanged || dialog.Lba1DirectoryChanged) DummyBodyPreview.Reset();

        // Reload the game on screen if its folder changed, and move to the other game when the
        // current one no longer has a folder (or nothing was open and only the other one is set now).
        var next = currentGame;
        if (currentGame == GameKind.Lba2 && !Lba2Configured && Lba1Configured) next = GameKind.Lba1;
        else if (currentGame == GameKind.Lba1 && !Lba1Configured && Lba2Configured) next = GameKind.Lba2;
        var changed = currentGame == GameKind.Lba2 ? dialog.GameDirectoryChanged : dialog.Lba1DirectoryChanged;
        if (next != currentGame || changed) SwitchGame(next);
    }
    // The function-name case setting changed: reprint every open script window's untouched scripts.
    private void RefreshScriptStyle()
    {
        var windows = openScriptWindows.Values.ToList();
        foreach (var w in windows) w.CommitForRestyle();
        scriptSession.RefreshStyle();
        lba1Session.RefreshStyle();
        foreach (var w in windows) w.ReloadForRestyle();
    }

    // File > Save writes the island the terrain editor changed; with nothing of that pending it is the old JSON draft export.
    private void Save_Click(object sender, RoutedEventArgs e) { if (terrainEditor is { Dirty: true } editor) editor.Save(); else Export_Click(sender, e); }
    private void Export_Click(object sender, RoutedEventArgs e) { var dialog = new SaveFileDialog { Filter = "JSON draft (*.json)|*.json", FileName = Path.GetFileNameWithoutExtension(activeFile) + ".json" }; if (dialog.ShowDialog() != true) return; var draft = new { format = "lba2-ile-draft", width = 16, height = 16, tiles = fallbackTiles }; File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true })); }

    private enum TerrainType { Grass, Sand, Water, Stone, Dirt }
}
