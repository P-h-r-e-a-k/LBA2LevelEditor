using System.IO;
using System.Windows;
using System.Windows.Controls;
using LBAAssembler.Lba1;

namespace LBAAssembler;

// Choosing what is open through the menus: Scenes > LBA1 / LBA2 > Island > Area. An island's areas are its scenes (LBA2: the outdoor
// scenes, each one cube of the island, then its interiors; LBA1: the connected outside maps first (as joined maps, or with joining off as
// their scenes), then the other scenes). Names are the game's descriptions without numbers or island names, capitalised (SceneMenuNames);
// nothing in a scene list is ticked (the top bar says where you are). The island / scene boxes of the top bar are the same
// Game/Island/Scene combos a user can drive directly, so every path that changes the open scene works the same whether it
// came from a menu, from typing into a combo, or from the code.
public partial class MainWindow
{
    private void GameMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;          // a nested submenu opening
        if (ReferenceEquals(sender, Lba1Menu)) BuildLba1Menu();
        else BuildLba2Menu();
    }

    // Disables the Tools entries a missing game folder would otherwise pop a MessageBox for on click --
    // the Scenes menu already shows this as a disabled note instead of letting the user click through to a
    // popup (see BuildLba1Menu/BuildLba2Menu's own Note() calls); this brings Tools in line with it. The
    // handlers keep their own guard too, since Editors items are also reachable from the Build tab's panel.
    private void ToolsMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        var eitherConfigured = Lba1Configured || Lba2Configured;
        ObjectBrowserMenuItem.IsEnabled = eitherConfigured;
        AssetEditorMenuItem.IsEnabled = eitherConfigured;
        GridEditorMenuItem.IsEnabled = eitherConfigured;
        ExportMenuItem.IsEnabled = eitherConfigured;
        Lba1EditorMenuItem.IsEnabled = Lba1Configured;
        Lba2EditorMenuItem.IsEnabled = Lba2Configured;
        IslandEditorMenuItem.IsEnabled = Lba2Configured;
        Lba1PlayMenuItem.IsEnabled = Lba1Configured;
        Lba2PlayMenuItem.IsEnabled = Lba2Configured;
        Lba2FixScriptingMenuItem.IsEnabled = Lba2Configured;
        Lba1SurpriseMenuItem.IsEnabled = Lba1Configured;
        TestEditsStartMenuItem.IsEnabled = eitherConfigured && !TestEditsActive;
        TestEditsCommitMenuItem.IsEnabled = TestEditsActive;
        TestEditsDiscardMenuItem.IsEnabled = TestEditsActive;
    }

    private static MenuItem Note(string text) => new() { Header = text, IsEnabled = false };

    // ---- LBA2 ------------------------------------------------------------------------------------------------------------------------------

    private const string DemoLabel = "Demo";

    // "12: Temple of Bú, 1st scene" -> "Temple of Bú, 1st scene" (the number and colon are not part of the description)
    private static string DescriptionOf(string display)
    {
        var colon = display.IndexOf(": ", StringComparison.Ordinal);
        return colon is >= 0 and < 6 ? display[(colon + 2)..] : display;
    }

    private static bool IsDemoScene(SceneEntry scene) => DescriptionOf(scene.Option.Display).StartsWith("Demo Scene", StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, string?> islandNameCache = new(StringComparer.OrdinalIgnoreCase);

    // What the game calls an island: the words most of its areas' descriptions start with ("White Leaf Desert", "Emerald Moon", ...).
    // (islands whose scenes' descriptions would give the wrong name: Celebration Island's interiors are mostly the Dark Monk's statue)
    // (and the desert: its scenes are described as "White Leaf Desert, ..." but the game itself calls the island Desert Island)
    private static readonly Dictionary<string, string> Lba2IslandNames = new(StringComparer.OrdinalIgnoreCase) { ["CELEBRAT"] = "Celebration Island", ["DESERT"] = "Desert Island" };

    private string? Lba2IslandName(string? islandFile)
    {
        if (islandFile is null) return null;
        if (Lba2IslandNames.TryGetValue(islandFile, out var fixedName)) return fixedName;
        if (islandNameCache.TryGetValue(islandFile, out var known)) return known;
        var scenes = allSceneEntries.Where(s => string.Equals(s.IslandFile, islandFile, StringComparison.OrdinalIgnoreCase) && !IsDemoScene(s)).ToList();
        string? name = null;
        if (scenes.Count > 0)
        {
            var top = scenes.Select(s => DescriptionOf(s.Option.Display)).Select(d => d.Split(", ")[0]).GroupBy(p => p).OrderByDescending(g => g.Count()).First();
            if (top.Count() * 2 >= scenes.Count) name = top.Key;
        }
        islandNameCache[islandFile] = name;
        return name;
    }

    private string Lba2IslandLabel(string islandFile) => Lba2IslandName(islandFile) ?? (islandFile.Length > 0 ? char.ToUpperInvariant(islandFile[0]) + islandFile[1..].ToLowerInvariant() : islandFile);

    // The island box entry (an .ILE file name, or the "Other" group) an area is opened through.
    private static string IslandDisplayOf(SceneEntry scene) => scene.IslandFile is null ? OtherIslandLabel : scene.IslandFile + ".ILE";

    private void BuildLba2Menu()
    {
        Lba2Menu.Items.Clear();
        if (!Lba2Configured) { Lba2Menu.Items.Add(Note("The LBA2 game folder isn't set (File > Settings)")); return; }
        var entries = allSceneEntries.Count > 0 ? allSceneEntries : BuildSceneEntries();
        if (allSceneEntries.Count == 0) allSceneEntries = entries;
        islandNameCache.Clear();

        MenuItem AreaItem(SceneEntry scene, string label, string islandDisplay)
        {
            var index = scene.Option.Index;
            var item = new MenuItem { Header = label.Replace("_", "__") };
            item.Click += (_, _) => OpenLba2Area(islandDisplay, index);
            return item;
        }

        var files = Directory.EnumerateFiles(gameRoot, "*.ILE").Where(p => !Path.GetFileName(p).StartsWith("_", StringComparison.OrdinalIgnoreCase)).Order().ToList();
        var groups = files.Select(p => Path.GetFileNameWithoutExtension(p)).Select(name => (Name: name, Label: Lba2IslandLabel(name), Display: name + ".ILE",
            Scenes: entries.Where(s => string.Equals(s.IslandFile, name, StringComparison.OrdinalIgnoreCase) && !IsDemoScene(s)).ToList())).ToList();
        var others = entries.Where(s => s.IslandFile is null && !IsDemoScene(s)).ToList();
        if (others.Count > 0) groups.Add((OtherIslandLabel, OtherIslandLabel, OtherIslandLabel, others));

        var islandNames = groups.Select(g => Lba2IslandName(g.Name)).Where(n => n is not null).Select(n => n!).ToList();
        foreach (var (name, label, display, scenes) in groups.OrderBy(g => g.Name == OtherIslandLabel).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase))
        {
            var island = new MenuItem { Header = label.Replace("_", "__"), ToolTip = display };
            var whole = new MenuItem { Header = "The whole island" };
            whole.Click += (_, _) => OpenLba2Area(display, null);
            island.Items.Add(whole);
            void Add(IEnumerable<SceneEntry> list, string heading)
            {
                var chosen = list.ToList();
                if (chosen.Count == 0) return;
                island.Items.Add(new Separator());
                island.Items.Add(Note(heading));
                foreach (var scene in chosen)
                    island.Items.Add(AreaItem(scene, SceneMenuNames.Clean(scene.Option.Display, islandNames), display));
            }
            // the joined maps of interiors (Lba2Areas) come first, and their scenes are not listed again
            var joined = Lba2AreasOfIsland(name == OtherIslandLabel ? null : name);
            if (joined.Count > 0)
            {
                island.Items.Add(new Separator());
                island.Items.Add(Note("Connected interiors"));
                foreach (var (area, areaIndex) in joined)
                {
                    var joinedItem = new MenuItem { Header = area.Name.Replace("_", "__") };
                    var option = Lba2AreaOption(areaIndex);
                    joinedItem.Click += (_, _) => OpenLba2Area(display, option);
                    island.Items.Add(joinedItem);
                }
            }
            var joinedScenes = joined.SelectMany(j => j.Area.Tiles.Select(t => t.Scene)).ToHashSet();
            Add(scenes.Where(s => !s.IsInterior), "Areas (outdoors)");
            Add(scenes.Where(s => s.IsInterior && !joinedScenes.Contains(s.Option.Index)), "Interiors");
            Lba2Menu.Items.Add(island);
        }

        // the demo reel's scenes, listed apart (they belong to the islands they show, but are not part of them)
        var demos = entries.Where(IsDemoScene).ToList();
        if (demos.Count > 0)
        {
            var demo = new MenuItem { Header = DemoLabel, ToolTip = "The scenes of the game's demo reel" };
            foreach (var scene in demos)
            {
                var description = DescriptionOf(scene.Option.Display);
                var dash = description.IndexOf(" - ", StringComparison.Ordinal);
                demo.Items.Add(AreaItem(scene, SceneMenuNames.Clean(dash >= 0 ? description[(dash + 3)..] : description, islandNames), IslandDisplayOf(scene)));
            }
            Lba2Menu.Items.Add(demo);
        }
        if (groups.Count == 0 && demos.Count == 0) Lba2Menu.Items.Add(Note("No islands found in the LBA2 folder"));
    }

    // Opens an island (an .ILE file name, or the "Other" group) and, when given, one of its areas; the game plays on from here if it was playing.
    private void OpenLba2Area(string islandDisplay, int? scene)
    {
        if (placing) CancelPlacement();
        var wasPlaying = playing;
        if (wasPlaying) StopPlay();
        if (currentGame != GameKind.Lba2)
        {
            SwitchGame(GameKind.Lba2);
            if (currentGame != GameKind.Lba2) return;
        }
        var island = islandOptions.FirstOrDefault(o => string.Equals(o.Display, islandDisplay, StringComparison.OrdinalIgnoreCase));
        if (island is null) return;
        var already = IslandCombo.SelectedItem is FilterableComboBox.Option current && current.Index == island.Index;
        if (!already) IslandCombo.SelectedItem = island;          // (unsaved terrain edits may keep it where it was)
        else if (scene is null && island.Display != OtherIslandLabel) LoadIsland(Path.Combine(gameRoot, island.Display));
        if (scene is { } wanted && IslandCombo.SelectedItem is FilterableComboBox.Option now && now.Index == island.Index && (sceneOptions.FirstOrDefault(o => o.Index == wanted) ?? Lba2OptionForScene(wanted)) is { } option)
        {
            SceneCombo.SelectedItem = null;                        // so choosing the same scene again shows it again
            SceneCombo.SelectedItem = option;
        }
        if (wasPlaying) StartPlay(GameKind.Lba2);
    }

    // ---- LBA1 ------------------------------------------------------------------------------------------------------------------------------

    private void BuildLba1Menu()
    {
        Lba1Menu.Items.Clear();
        var directory = EditorSettings.Current.Lba1Directory;
        if (!Lba1Game.IsInstalled(directory)) { Lba1Menu.Items.Add(Note("The LBA1 game folder isn't set (File > Settings)")); return; }
        try { lba1Game ??= new Lba1Game(directory); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Lba1Menu.Items.Add(Note($"Couldn't read the LBA1 data: {error.Message}"));
            return;
        }
        var game = lba1Game;
        foreach (var id in game.Scenes.Select(s => s.Island).Distinct().Order())
        {
            var name = Lba1Game.IslandNames.ElementAtOrDefault(id) ?? $"Island {id}";
            var island = new MenuItem { Header = name.Replace("_", "__") };
            var islandId = id;
            var (connected, others) = Lba1SceneGroups(game, id);
            void Add(IEnumerable<Lba1SceneEntry> list)
            {
                foreach (var entry in list)
                {
                    var option = entry.Option.Index;
                    var item = new MenuItem { Header = entry.MenuName.Replace("_", "__") };
                    item.Click += (_, _) => OpenLba1Area(islandId, option);
                    island.Items.Add(item);
                }
            }
            Add(connected);
            if (connected.Count > 0 && others.Count > 0) island.Items.Add(new Separator());
            Add(others);
            Lba1Menu.Items.Add(island);
        }
    }

    private void OpenLba1Area(int islandId, int option)
    {
        if (placing) CancelPlacement();
        var wasPlaying = playing;
        if (wasPlaying) StopPlay();
        if (currentGame != GameKind.Lba1)
        {
            SwitchGame(GameKind.Lba1);
            if (currentGame != GameKind.Lba1) return;
        }
        var island = islandOptions.FirstOrDefault(o => o.Index == islandId);
        if (island is null) return;
        if (!(IslandCombo.SelectedItem is FilterableComboBox.Option current && current.Index == islandId)) IslandCombo.SelectedItem = island;    // lists its scenes and opens the first
        if (sceneOptions.FirstOrDefault(o => o.Index == option) is { } scene)
        {
            SceneCombo.SelectedItem = null;
            SceneCombo.SelectedItem = scene;
        }
        if (wasPlaying) StartPlay(GameKind.Lba1);
    }
}
