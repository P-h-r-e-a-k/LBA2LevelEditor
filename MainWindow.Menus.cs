using System.IO;
using System.Windows;
using System.Windows.Controls;
using LBA2LevelEditor.Lba1;

namespace LBA2LevelEditor;

// Choosing what is open through the menus: Scenes > LBA1 / LBA2 > Island > Area. An island's areas are its scenes (LBA2: the outdoor
// scenes, each one cube of the island, then its interiors; LBA1: the scenes, or the joined areas when that is ticked). The
// island / scene boxes of the top bar are still what does the opening (they stay in the window, hidden), so every path that
// changes the open scene works the same whether it came from a menu or from the code.
public partial class MainWindow
{
    private void GameMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;          // a nested submenu opening
        if (ReferenceEquals(sender, Lba1Menu)) BuildLba1Menu();
        else BuildLba2Menu();
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

    // The area's name without the island's own name in front ("38: White Leaf Desert, near the Camel" -> "38: near the Camel"): the island is
    // already what the area is listed under.
    private static string StripIsland(string display, string? island)
    {
        if (string.IsNullOrEmpty(island)) return display;
        var colon = display.IndexOf(": ", StringComparison.Ordinal);
        var head = colon is >= 0 and < 6 ? display[..(colon + 2)] : "";
        var text = display[head.Length..];
        if (text.StartsWith(island + ", ", StringComparison.OrdinalIgnoreCase)) text = text[(island.Length + 2)..];
        return head + text;
    }

    private Dictionary<string, string?> islandNameCache = new(StringComparer.OrdinalIgnoreCase);

    // What the game calls an island: the words most of its areas' descriptions start with ("White Leaf Desert", "Emerald Moon", ...).
    private string? Lba2IslandName(string? islandFile)
    {
        if (islandFile is null) return null;
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
        var openIsland = currentGame == GameKind.Lba2 ? Path.GetFileNameWithoutExtension(activeFile) : null;
        var openScene = currentGame == GameKind.Lba2 ? Lba2SceneToPlay() : -1;

        MenuItem AreaItem(SceneEntry scene, string label, string islandDisplay, bool ticked)
        {
            var index = scene.Option.Index;
            var item = new MenuItem { Header = label.Replace("_", "__"), IsChecked = ticked };
            item.Click += (_, _) => OpenLba2Area(islandDisplay, index);
            return item;
        }

        var files = Directory.EnumerateFiles(gameRoot, "*.ILE").Where(p => !Path.GetFileName(p).StartsWith("_", StringComparison.OrdinalIgnoreCase)).Order().ToList();
        var groups = files.Select(p => Path.GetFileNameWithoutExtension(p)).Select(name => (Name: name, Label: Lba2IslandLabel(name), Display: name + ".ILE",
            Scenes: entries.Where(s => string.Equals(s.IslandFile, name, StringComparison.OrdinalIgnoreCase) && !IsDemoScene(s)).ToList())).ToList();
        var others = entries.Where(s => s.IslandFile is null && !IsDemoScene(s)).ToList();
        if (others.Count > 0) groups.Add((OtherIslandLabel, OtherIslandLabel, OtherIslandLabel, others));

        foreach (var (name, label, display, scenes) in groups.OrderBy(g => g.Name == OtherIslandLabel).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase))
        {
            var island = new MenuItem { Header = label.Replace("_", "__"), ToolTip = display, IsChecked = string.Equals(openIsland, name, StringComparison.OrdinalIgnoreCase) };
            var whole = new MenuItem { Header = "The whole island" };
            whole.Click += (_, _) => OpenLba2Area(display, null);
            island.Items.Add(whole);
            var prefix = Lba2IslandName(name);
            void Add(IEnumerable<SceneEntry> list, string heading)
            {
                var chosen = list.ToList();
                if (chosen.Count == 0) return;
                island.Items.Add(new Separator());
                island.Items.Add(Note(heading));
                foreach (var scene in chosen)
                    island.Items.Add(AreaItem(scene, StripIsland(scene.Option.Display, prefix), display, scene.Option.Index == openScene && string.Equals(openIsland, name, StringComparison.OrdinalIgnoreCase)));
            }
            Add(scenes.Where(s => !s.IsInterior), "AREAS (OUTDOORS)");
            Add(scenes.Where(s => s.IsInterior), "INTERIORS");
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
                var shown = $"{scene.Option.Index}: " + (dash >= 0 ? description[(dash + 3)..] : description);
                demo.Items.Add(AreaItem(scene, shown, IslandDisplayOf(scene), scene.Option.Index == openScene));
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
        if (scene is { } wanted && IslandCombo.SelectedItem is FilterableComboBox.Option now && now.Index == island.Index && sceneOptions.FirstOrDefault(o => o.Index == wanted) is { } option)
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
        var openIsland = currentGame == GameKind.Lba1 && IslandCombo.SelectedItem is FilterableComboBox.Option selected ? selected.Index : -1;
        var openScene = currentGame == GameKind.Lba1 && SceneCombo.SelectedItem is FilterableComboBox.Option scene ? scene.Index : int.MinValue;
        foreach (var id in game.Scenes.Select(s => s.Island).Distinct().Order())
        {
            var name = Lba1Game.IslandNames.ElementAtOrDefault(id) ?? $"Island {id}";
            var island = new MenuItem { Header = name.Replace("_", "__"), IsChecked = openIsland == id };
            var islandId = id;
            foreach (var area in Lba1SceneOptionsFor(game, id))
            {
                var option = area.Index;
                var item = new MenuItem { Header = StripIsland(area.Display, name).Replace("_", "__"), IsChecked = openIsland == id && openScene == option };
                item.Click += (_, _) => OpenLba1Area(islandId, option);
                island.Items.Add(item);
            }
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

    // ---- where we are -----------------------------------------------------------------------------------------------------------------------

    // The top bar's label: the game, the island and the area that is open.
    private void UpdateLocation()
    {
        if (currentGame == GameKind.Lba1)
        {
            var island = IslandCombo.SelectedItem?.ToString() ?? "";
            LocationGame.Text = "LBA1  ›  " + island.ToUpperInvariant();
            LocationArea.Text = StripIsland(SceneCombo.SelectedItem?.ToString() ?? "", island);
            return;
        }
        var scene = Lba2SceneToPlay();
        var entry = allSceneEntries.FirstOrDefault(s => s.Option.Index == scene);
        if (entry is not null && IsDemoScene(entry))
        {
            LocationGame.Text = "LBA2  ›  " + DemoLabel.ToUpperInvariant();
            var description = DescriptionOf(entry.Option.Display);
            var dash = description.IndexOf(" - ", StringComparison.Ordinal);
            LocationArea.Text = $"{scene}: " + (dash >= 0 ? description[(dash + 3)..] : description);
            return;
        }
        var file = Path.GetFileNameWithoutExtension(activeFile);
        LocationGame.Text = "LBA2  ›  " + Lba2IslandLabel(file).ToUpperInvariant();
        LocationArea.Text = entry is null ? "" : StripIsland(entry.Option.Display, Lba2IslandName(entry.IslandFile));
    }
}
