using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LBAAssembler;

// The three modes of the main window.
//   Explore  the scene is only looked at: the camera, zoom, layers, selecting actors and zones. Nothing can be changed.
//   Build    every change to the scene's content: for an LBA2 island the terrain editor takes over the view (heights,
//            levelling, baked light and shadows, ground, objects: IslandEditorView), or the ordinary view with adding and
//            editing of actors and zones switched on; the BUILD tab also opens the other editors (interior map, bricks ...).
//   Script   the actors' scripts: click an actor (or pick it from the SCRIPT tab) to open its script.
// It also keeps track of which scene is selected, so Play starts that scene and not scene 0.
public partial class MainWindow
{
    private enum EditMode { Explore, Build, Script }

    private EditMode editMode = EditMode.Explore;
    private bool modeReady;
    private bool modeSyncing;
    private IslandEditorView? terrainEditor;
    private bool terrainShown;                 // the top-down terrain map is on screen instead of the 3D view
    private bool buildTerrainView = true;      // Build on an island: the terrain tools (true) or the actors-and-zones tools
    private bool sceneViewStale;               // the island was saved from the terrain editor; the 3D view still shows the old file
    private bool restoringIsland;

    // The scene picked in the Scene box (or last shown as an interior); null until one is. Play starts this scene.
    private int? selectedLba2Scene;

    // ---- modes -----------------------------------------------------------------------------------------------------------------------------

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!modeReady || modeSyncing || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var value)) return;
        SetMode((EditMode)value);
    }

    private void ModeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out var value)) SetMode((EditMode)value);
    }

    private void BuildView_Changed(object sender, RoutedEventArgs e)
    {
        if (!modeReady) return;
        buildTerrainView = BuildTerrainRadio.IsChecked == true;
        ApplyMode();
    }

    private void SetMode(EditMode next)
    {
        if (lba2JoinedView && next != EditMode.Explore)
        {
            next = EditMode.Explore;      // (a joined LBA2 map is drawn by the editor, not the engine: nothing in it can be edited)
            FileLabel.Text = "A joined map is view only. Double-click an actor to open its scene on its own, or pick a single scene, to edit.";
        }
        var changed = next != editMode;
        editMode = next;
        modeSyncing = true;
        try
        {
            ModeExploreButton.IsChecked = next == EditMode.Explore;
            ModeBuildButton.IsChecked = next == EditMode.Build;
            ModeScriptButton.IsChecked = next == EditMode.Script;
        }
        finally { modeSyncing = false; }
        ApplyMode(selectTab: changed);
        if (changed) FileLabel.Text = next switch
        {
            EditMode.Explore => "Explore: move around the scene. Nothing is changed in this mode.",
            EditMode.Build => terrainToolsActive ? "Build: pick a terrain tool in the Build tab and paint on the view; the view shows your edits live. Right drag orbits while a tool is chosen, middle drag pans." : "Build: right-click the view to add an actor, double-click an actor to edit it, change zones under Details.",
            _ => "Script: click an actor (or pick one in the Script tab) to open its script.",
        };
        Keyboard.Focus(this);
    }

    private bool TerrainEditable => currentGame == GameKind.Lba2 && !interiorSceneActive && currentIsland is not null && Lba2Engine.IsGameFolder(gameRoot);

    // Brings the window in line with the mode, the game and what is showing: which view is on screen, which side tabs are
    // there, what may be edited.
    private void ApplyMode(bool selectTab = false)
    {
        if (!modeReady) return;
        // The game folder in the title: the editor saves into it, so any test or user can see which one is in use.
        Title = $"LBA Assembler  -  {(currentGame == GameKind.Lba1 ? EditorSettings.Current.Lba1Directory : gameRoot)}";
        BuildViewBar.Visibility = TerrainEditable ? Visibility.Visible : Visibility.Collapsed;
        var wantTerrain = editMode == EditMode.Build && buildTerrainView && TerrainEditable && ShowTerrainEditor();
        terrainToolsActive = wantTerrain;
        SetTerrainShown(wantTerrain && terrainMapWanted);
        UpdateLive();
        if (!wantTerrain) { paintingTerrain = false; hoverCell = null; DrawTerrainOverlay(); }
        TerrainPanelHost.Visibility = wantTerrain ? Visibility.Visible : Visibility.Collapsed;
        BuildScenePanel.Visibility = wantTerrain ? Visibility.Collapsed : Visibility.Visible;
        if (wantTerrain) OnTerrainStateChanged();
        BuildSceneHelp.Text = BuildHelpText();

        SetPanelVisible(ZoneDetailsTab, editMode != EditMode.Script);
        SetPanelVisible(BuildTab, editMode == EditMode.Build);
        SetPanelVisible(ScriptTab, editMode == EditMode.Script);
        SetPanelVisible(PlayTab, true);
        var home = editMode switch { EditMode.Build => BuildTab, EditMode.Script => ScriptTab, _ => ZonesTab };
        var currentlyShown = new[] { ZonesTab, ZoneDetailsTab, BuildTab, ScriptTab, PlayTab }.FirstOrDefault(t => t.IsActive);
        if (selectTab || currentlyShown is not { IsVisible: true }) ActivatePanel(home);

        UpdateZoneEditability();
        SyncAudioControls();
        if (editMode == EditMode.Script) RefreshScriptActors();
        UpdatePlayButton();
    }

    private string BuildHelpText()
    {
        if (currentGame == GameKind.Lba1)
            return "Right-click an actor for its attributes; double-click it to edit. Change zones under Details. The scene editor opens the scene as data (add, move, delete, duplicate, undo, save). Scripts are edited in Script mode.";
        if (interiorSceneActive)
            return "Right-click the view to add an actor, double-click an actor to edit it, change zones under Details. The interior's map (its bricks and blocks) is edited in the grid editor. Scripts are edited in Script mode.";
        return "Right-click the view and choose Add Actor Here to place an actor; double-click an actor to change it; change zones under Details. Scripts are edited in Script mode. Pick 'Terrain' above to sculpt the island itself.";
    }

    // ---- the terrain editor, hosted -------------------------------------------------------------------------------------------------------------

    private bool ShowTerrainEditor()
    {
        if (terrainEditor is null)
        {
            terrainEditor = new IslandEditorView(gameRoot);
            terrainEditor.StatusChanged += text => FileLabel.Text = text;
            terrainEditor.Saved += OnTerrainSaved;
            terrainEditor.StateChanged += OnTerrainStateChanged;
            terrainEditor.Edited += OnTerrainEdited;
            terrainEditor.MapRequested += on => { terrainMapWanted = on; ApplyMode(); };
            TerrainPanelHost.Content = terrainEditor.Panel;
            TerrainMapHost.Child = terrainEditor.MapArea;
        }
        terrainEditor.GameDirectory = gameRoot;
        // Ask for the island file's own name: activeFile is what the island box shows.
        if (terrainEditor.Open(activeFile)) return true;
        FileLabel.Text = "The terrain editor couldn't open " + activeFile + ".";
        return false;
    }

    // The top-down map instead of the 3D view (an option of the terrain tools).
    private void SetTerrainShown(bool show)
    {
        if (terrainEditor is not null) terrainEditor.MapActive = show;
        if (show == terrainShown) return;
        terrainShown = show;
        TerrainMapHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SceneViewBorder.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SetPanelVisible(MinimapAnchorable, !show);
        if (show) return;
        if (sceneViewStale) RefreshSceneViewIfStale();
        else if (interiorSceneActive) ApplyInteriorView();
        else if (nativeViewActive) RenderNativeCamera();
        else if (currentIsland is not null) RenderSoftwareTerrain();
    }

    // The island's name in the header shows whether there are unsaved terrain edits.
    private void OnTerrainStateChanged()
    {
        if (terrainEditor?.CurrentName is not { } name || !terrainToolsActive) return;
        DocumentTitle.Text = Path.GetFileNameWithoutExtension(name) + (terrainEditor.Dirty ? "   ● unsaved" : "");
    }

    // The island was written: the native 3D view reads the file again and the minimap / software view are rebuilt from it.
    private void OnTerrainSaved()
    {
        InvalidateNativeIsland();
        sceneViewStale = true;
        if (!terrainShown) RefreshSceneViewIfStale();
        OnTerrainStateChanged();
    }

    private void RefreshSceneViewIfStale()
    {
        if (!sceneViewStale || currentIsland is null || interiorSceneActive) return;
        sceneViewStale = false;
        try
        {
            var path = Path.Combine(gameRoot, activeFile);
            LoadIslandPalette(path);
            currentIsland = IslandDocument.Open(path, palette, shadeTable, shadeLevel);
            TerrainViewport.Source = currentIsland.CreatePreview();
            RegenerateMinimap();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"Saved, but couldn't reload the view: {error.Message}", StatusKind.Warning);
            DebugLog.Log($"MainWindow: reloading {activeFile} after a terrain save failed: {error.Message}");
        }
        if (nativeViewActive) RenderNativeCamera();
        else RenderSoftwareTerrain();
    }

    // Unsaved terrain edits stand in the way of leaving the island (or the game): ask, true = free to go on.
    private bool ConfirmTerrainDiscard() => terrainEditor is not { Dirty: true } editor || editor.ConfirmDiscard();

    // Tools > LBA2: island terrain editor: the same as Build mode on the island that is open.
    private void IslandEditor_Click(object sender, RoutedEventArgs e)
    {
        if (!Lba2Engine.IsGameFolder(gameRoot))
        {
            MessageBox.Show(this, "The LBA2 game folder isn't set. Choose it under File > Settings.", "LBA2", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (currentGame != GameKind.Lba2) SwitchGame(GameKind.Lba2);
        if (currentGame != GameKind.Lba2) return;
        if (interiorSceneActive && File.Exists(Path.Combine(gameRoot, activeFile))) LoadIsland(Path.Combine(gameRoot, activeFile));
        buildTerrainView = true;
        modeSyncing = true;
        try { BuildTerrainRadio.IsChecked = true; }
        finally { modeSyncing = false; }
        SetMode(EditMode.Build);
        if (!terrainToolsActive) MessageBox.Show(this, "There is no island open to edit. Pick one in the Island box first.", "LBA2: island terrain editor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- zones: editable only in Build mode ------------------------------------------------------------------------------------------------

    private void UpdateZoneEditability()
    {
        var edit = editMode == EditMode.Build;
        foreach (var box in new[] { MinXBox, MinYBox, MinZBox, MaxXBox, MaxYBox, MaxZBox }) box.IsEnabled = edit;
        foreach (var (_, box) in zoneFieldBoxes) box.IsEnabled = edit;
        ZoneApplyButton.IsEnabled = edit;
        ZoneRevertButton.IsEnabled = edit;
        if (!edit && ZoneDetails.Visibility == Visibility.Visible) ZoneStatus.Text = "Switch to Build mode (Ctrl+2) to change this zone.";
        else if (edit && ZoneStatus.Text.StartsWith("Switch to Build mode", StringComparison.Ordinal)) ZoneStatus.Text = "";
    }

    // ---- the SCRIPT tab: the actors in view -----------------------------------------------------------------------------------------------------

    private bool scriptListSyncing;

    private void ScriptActorsRefresh_Click(object sender, RoutedEventArgs e) => RefreshScriptActors();

    private void RefreshScriptActors()
    {
        var indexes = (interiorSceneActive
                ? interiorActors.Select(a => a.Index)
                : lastNativeActorScreens?.Select(a => a.Index) ?? Enumerable.Empty<int>())
            .Distinct().OrderBy(i => i).ToList();
        scriptListSyncing = true;
        try
        {
            ScriptActorList.Items.Clear();
            foreach (var index in indexes)
            {
                var text = currentGame == GameKind.Lba1 && index >= 1000 ? $"Scene {index / 1000}, actor #{index % 1000}" : $"Actor #{index}";
                ScriptActorList.Items.Add(new ListBoxItem { Content = text, Tag = index, IsSelected = selectedActorIndex == index });
            }
            ScriptActorsHeader.Text = $"Actors in view ({indexes.Count})";
        }
        finally { scriptListSyncing = false; }
    }

    private void ScriptActorList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ScriptActorList.SelectedItem is ListBoxItem { Tag: int index }) OpenActorScriptWindow(index);
    }

    private void ScriptActorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (scriptListSyncing || ScriptActorList.SelectedItem is not ListBoxItem { Tag: int index }) return;
        selectedActorIndex = index;
        RefreshActorOverlayForSelection();
    }

    // ---- Build tab buttons -----------------------------------------------------------------------------------------------------------------------

    private void BuildSceneEditor_Click(object sender, RoutedEventArgs e)
    {
        if (currentGame == GameKind.Lba1) Lba1Editor_Click(sender, e);
        else Lba2Editor_Click(sender, e);
    }

    // ---- keys -------------------------------------------------------------------------------------------------------------------------------------

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While a scene is played the keys belong to the game (the LBA1 play view gets them from here; LBA2's engine has its own window).
        if (placing && e.Key == Key.Escape) { CancelPlacement(); e.Handled = true; return; }
        if (playing) { lba1Play?.ForwardKey(e, true); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            EditMode? wanted = e.Key switch
            {
                Key.D1 or Key.NumPad1 => EditMode.Explore,
                Key.D2 or Key.NumPad2 => EditMode.Build,
                Key.D3 or Key.NumPad3 => EditMode.Script,
                _ => null,
            };
            if (wanted is { } mode) { SetMode(mode); e.Handled = true; return; }
        }
        if (terrainToolsActive && terrainEditor is not null && terrainEditor.HandleKey(e)) e.Handled = true;
    }

    // ---- play the selected scene ---------------------------------------------------------------------------------------------------------------

    // The scene Play should start: the scene that is open. An interior is its own scene; on an island it is the scene of the cube
    // the camera is over (each outdoor scene is one cube of its island), else the one picked in the Scene box, else the first
    // outdoor scene of the island (never just scene 0).
    private int Lba2SceneToPlay()
    {
        if (interiorSceneActive && interiorSceneNumber >= 0) return interiorSceneNumber;
        if (SceneUnderCamera() is { } here) return here.Option.Index;
        if (selectedLba2Scene is { } picked) return picked;
        var island = Path.GetFileNameWithoutExtension(activeFile);
        var first = allSceneEntries.FirstOrDefault(s => !s.IsInterior && string.Equals(s.IslandFile, island, StringComparison.OrdinalIgnoreCase));
        return first?.Option.Index ?? Lba2Play.LastOptions?.Scene ?? 0;
    }

    private SceneEntry? SceneUnderCamera()
    {
        if (currentGame != GameKind.Lba2 || interiorSceneActive || currentIsland is null) return null;
        var island = Path.GetFileNameWithoutExtension(activeFile);
        var cubeX = (int)Math.Floor(targetX / 32768.0);
        var cubeZ = (int)Math.Floor(targetZ / 32768.0);
        return allSceneEntries.FirstOrDefault(s => !s.IsInterior && s.CubeX == cubeX && s.CubeY == cubeZ && string.Equals(s.IslandFile, island, StringComparison.OrdinalIgnoreCase));
    }

    // Picking an outdoor scene in the Scene box moves the camera to its cube (an interior on screen goes back to the island first).
    private void FocusExteriorScene(SceneEntry entry)
    {
        if (currentGame != GameKind.Lba2 || entry.IsInterior || currentIsland is null) return;
        if (interiorSceneActive) LoadIsland(Path.Combine(gameRoot, activeFile));
        selectedLba2Scene = entry.Option.Index;
        var x = entry.CubeX * 32768.0 + 16384;
        var z = entry.CubeY * 32768.0 + 16384;
        if (!IsWorldPositionOnIsland(x, z)) return;
        targetX = x; targetZ = z;
        SyncPanScrollBars();
        UpdateMinimapMarker();
        if (nativeViewActive) RenderNativeCamera(); else RenderSoftwareTerrain();
    }

    // The Play button names the scene it will start.
    private void UpdatePlayButton()
    {
        if (placing) return;
        if (currentGame != GameKind.Lba2 || !Lba2Configured) { PlayButton.Content = "▶  Play scene"; PlayButton.ToolTip = "Play the selected scene in the LBA1 play mode"; return; }
        var scene = Lba2SceneToPlay();
        PlayButton.Content = $"▶  Play scene {scene}";
        PlayButton.ToolTip = (allSceneEntries.FirstOrDefault(s => s.Option.Index == scene)?.Option.Display ?? $"Scene {scene}") + "\nThe scene that is open (for an island: the cube the camera is over). Plays what is saved on disk.";
    }
}
