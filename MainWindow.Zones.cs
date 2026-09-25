using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LBAAssembler;

// The zone inspector (DETAILS tab): pick a zone from the map or the list, see and edit its bounds,
// its size and its type-specific data (for a cube change, the scene it leads to), and write the
// result back into SCENE.HQR.
public partial class MainWindow
{
    private ZoneRef? selectedZoneRef;
    private ZoneData? zoneOriginal;
    private readonly Dictionary<(int Game, int Scene), List<ZoneData>?> zoneCache = new();
    private readonly List<(ZoneField Field, TextBox Box)> zoneFieldBoxes = new();
    private bool zoneFormLoading;
    private bool zoneListSyncing;
    private const int ZoneListLimit = 600;

    private IReadOnlyList<ProjectedZone> ZonesInView
        => interiorSceneActive ? interiorOverlay.Zones : (IReadOnlyList<ProjectedZone>?)lastNativeZones ?? Array.Empty<ProjectedZone>();

    private ZoneData? LookupZone(ZoneRef r)
    {
        var key = (r.Game, r.Scene);
        if (!zoneCache.TryGetValue(key, out var list))
        {
            try { list = SceneZones.Parse(r.Game, r.Scene, SceneZones.ReadRecord(r.Game, r.Scene)); }
            catch (Exception error)
            {
                DebugLog.Log($"MainWindow: couldn't read the zones of game {r.Game} scene {r.Scene}: {error.Message}");
                list = null;
            }
            zoneCache[key] = list;
        }
        return list is not null && r.Index < list.Count ? list[r.Index] : null;
    }

    private string? SceneDescription(int game, int scene)
        => game == 1
            ? lba1Game?.Description(scene)
            : StripSceneNumber(allSceneEntries.FirstOrDefault(e => e.Option.Index == scene)?.Option.Display);

    // The LBA2 scene list shows "56: White Leaf Desert, ..."; the inspector adds its own scene number.
    private static string? StripSceneNumber(string? display)
        => display is null ? null : System.Text.RegularExpressions.Regex.Replace(display, @"^\d+:\s*", "");

    // ---- selection ------------------------------------------------------------

    private void ZoneShape_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ZoneRef zone) return;
        e.Handled = true;
        SelectZone(zone, showTab: true);
    }

    private void SelectZone(ZoneRef? zone, bool showTab, bool center = false)
    {
        selectedZoneRef = zone;
        zoneOriginal = zone is null ? null : LookupZone(zone);
        LoadZoneForm();
        SyncZoneListSelection();
        if (showTab && zone is not null && ZoneDetailsTab.IsEnabled) ActivatePanel(ZoneDetailsTab);
        RefreshActorOverlayForSelection();
        if (center && zone is not null && interiorSceneActive) CenterOnZone(zone);
    }

    private void CenterOnZone(ZoneRef zone)
    {
        var shape = ZonesInView.FirstOrDefault(z => z.Ref == zone);
        if (shape is null) return;
        interiorCenter = new Point(shape.Corners.Average(p => p.X), shape.Corners.Average(p => p.Y));
        ApplyInteriorView();
    }

    private void RefreshZoneListIfVisible()
    {
        if (ZoneDetailsTab.IsSelected) RefreshZoneList();
    }

    private void ZoneListRefresh_Click(object sender, RoutedEventArgs e) => RefreshZoneList();

    private void RefreshZoneList()
    {
        var zones = ZonesInView.Where(z => z.Ref is not null && ZoneShown(z.Type)).ToList();
        var multiScene = zones.Select(z => z.Ref!.Scene).Distinct().Count() > 1;
        zoneListSyncing = true;
        try
        {
            ZoneListBox.Items.Clear();
            foreach (var zone in zones.Take(ZoneListLimit))
            {
                var data = LookupZone(zone.Ref!);
                var text = $"{(multiScene ? $"{zone.Ref!.Scene}: " : "")}{ZoneStyle.NameOf(zone.Type)} #{zone.Ref!.Index}";
                if (zone.Type == 0 && data is not null) text += $" → scene {data.Destination}";
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(ZoneStyle.ColorOf(zone.Type)),
                });
                row.Children.Add(new TextBlock { Text = text });
                ZoneListBox.Items.Add(new ListBoxItem { Content = row, Tag = zone.Ref });
            }
            ZoneListHeader.Text = zones.Count > ZoneListLimit ? $"Zones in view ({ZoneListLimit} of {zones.Count})" : $"Zones in view ({zones.Count})";
        }
        finally { zoneListSyncing = false; }
        SyncZoneListSelection();
    }

    private void SyncZoneListSelection()
    {
        zoneListSyncing = true;
        try
        {
            ZoneListBox.SelectedItem = selectedZoneRef is null ? null : ZoneListBox.Items.OfType<ListBoxItem>().FirstOrDefault(i => (ZoneRef?)i.Tag == selectedZoneRef);
            if (ZoneListBox.SelectedItem is not null) ZoneListBox.ScrollIntoView(ZoneListBox.SelectedItem);
        }
        finally { zoneListSyncing = false; }
    }

    private void ZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (zoneListSyncing) return;
        if (ZoneListBox.SelectedItem is ListBoxItem { Tag: ZoneRef zone }) SelectZone(zone, showTab: false, center: true);
    }

    // ---- the form -------------------------------------------------------------

    private void LoadZoneForm()
    {
        zoneFormLoading = true;
        try
        {
            ZoneFieldsPanel.Children.Clear();
            zoneFieldBoxes.Clear();
            ZoneStatus.Text = "";
            if (zoneOriginal is null)
            {
                ZoneNoSelection.Text = selectedZoneRef is null
                    ? "Click a zone's outline in the map, or pick one from the list, to see and change its data."
                    : "This zone's data couldn't be read from SCENE.HQR.";
                ZoneNoSelection.Visibility = Visibility.Visible;
                ZoneDetails.Visibility = Visibility.Collapsed;
                return;
            }

            var zone = zoneOriginal;
            ZoneNoSelection.Visibility = Visibility.Collapsed;
            ZoneDetails.Visibility = Visibility.Visible;
            ZoneTitle.Text = $"{ZoneStyle.NameOf(zone.Type)} · zone #{zone.Index}";
            var description = SceneDescription(zone.Game, zone.Scene);
            ZoneSubtitle.Text = $"LBA{zone.Game} scene {zone.Scene}" + (string.IsNullOrEmpty(description) ? "" : $" — {description}");

            MinXBox.Text = Math.Min(zone.X0, zone.X1).ToString(); MaxXBox.Text = Math.Max(zone.X0, zone.X1).ToString();
            MinYBox.Text = Math.Min(zone.Y0, zone.Y1).ToString(); MaxYBox.Text = Math.Max(zone.Y0, zone.Y1).ToString();
            MinZBox.Text = Math.Min(zone.Z0, zone.Z1).ToString(); MaxZBox.Text = Math.Max(zone.Z0, zone.Z1).ToString();

            foreach (var field in ZoneFields.For(zone))
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(126) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var label = new TextBlock { Text = field.Label, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x24, 0x3E)), FontSize = 11, ToolTip = field.Hint };
                var box = new TextBox
                {
                    Text = field.Get(zone).ToString(), Padding = new Thickness(4, 3, 4, 3), ToolTip = field.Hint,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x24, 0x3E)), Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFA)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0xC3, 0xE0)),
                };
                System.Windows.Automation.AutomationProperties.SetName(box, field.Label);
                box.TextChanged += ZoneBounds_TextChanged;
                Grid.SetColumn(box, 1);
                row.Children.Add(label);
                row.Children.Add(box);
                ZoneFieldsPanel.Children.Add(row);
                zoneFieldBoxes.Add((field, box));
            }
            ZoneGotoButton.Visibility = zone.Type == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { zoneFormLoading = false; }
        UpdateZoneDerived();
        UpdateZoneEditability();
    }

    private void ZoneBounds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!zoneFormLoading) UpdateZoneDerived();
    }

    private static bool TryInt(TextBox box, out int value) => int.TryParse(box.Text.Trim(), out value);

    // Size of the box and, for a cube change, where it leads, from whatever is typed right now.
    private void UpdateZoneDerived()
    {
        if (zoneOriginal is null) return;
        if (TryInt(MinXBox, out var x0) && TryInt(MaxXBox, out var x1) && TryInt(MinYBox, out var y0) && TryInt(MaxYBox, out var y1)
            && TryInt(MinZBox, out var z0) && TryInt(MaxZBox, out var z1))
        {
            int w = x1 - x0, h = y1 - y0, d = z1 - z0;
            ZoneSizeText.Text = $"Size  {w} × {h} × {d}  (X × Y × Z)\n"
                + $"      ≈ {w / 512.0:0.#} × {h / 256.0:0.#} × {d / 512.0:0.#} cells\n"
                + $"Centre  {(x0 + x1) / 2}, {(y0 + y1) / 2}, {(z0 + z1) / 2}";
        }
        else ZoneSizeText.Text = "Size  (enter whole numbers for the bounds)";

        ZoneDestinationText.Text = "";
        if (zoneOriginal.Type == 0)
        {
            var destination = zoneFieldBoxes.FirstOrDefault(f => f.Field.Label == "Destination scene").Box;
            if (destination is not null && TryInt(destination, out var scene))
            {
                var description = SceneDescription(zoneOriginal.Game, scene);
                ZoneDestinationText.Text = string.IsNullOrEmpty(description) ? $"→ scene {scene}" : $"→ scene {scene}: {description}";
            }
        }
    }

    private void ZoneRevert_Click(object sender, RoutedEventArgs e) => LoadZoneForm();

    private void ZoneApply_Click(object sender, RoutedEventArgs e)
    {
        if (zoneOriginal is null || editMode != EditMode.Build) return;
        var edited = zoneOriginal.Clone();

        // A bound is only rewritten when it was changed, so a zone stored "backwards" keeps its order.
        static string? Axis(string name, TextBox min, TextBox max, ref int v0, ref int v1)
        {
            if (!TryInt(min, out var lo) || !TryInt(max, out var hi)) return "The bounds must be whole numbers.";
            if (lo > hi) return $"{name}: MIN must not be above MAX.";
            if (lo == Math.Min(v0, v1) && hi == Math.Max(v0, v1)) return null;
            v0 = lo; v1 = hi;
            return null;
        }
        int ax0 = edited.X0, ax1 = edited.X1, ay0 = edited.Y0, ay1 = edited.Y1, az0 = edited.Z0, az1 = edited.Z1;
        var boundsError = Axis("X", MinXBox, MaxXBox, ref ax0, ref ax1) ?? Axis("Y", MinYBox, MaxYBox, ref ay0, ref ay1) ?? Axis("Z", MinZBox, MaxZBox, ref az0, ref az1);
        if (boundsError is not null)
        {
            ZoneStatus.Text = boundsError;
            return;
        }
        edited.X0 = ax0; edited.X1 = ax1; edited.Y0 = ay0; edited.Y1 = ay1; edited.Z0 = az0; edited.Z1 = az1;

        foreach (var (field, box) in zoneFieldBoxes)
        {
            if (!TryInt(box, out var value)) { ZoneStatus.Text = $"“{field.Label}” must be a whole number."; return; }
            field.Set(edited, value);
        }

        if (edited.Type == 0)
        {
            var known = edited.Game == 1
                ? lba1Game?.Scenes.Any(s => s.Index == edited.Destination) == true
                : allSceneEntries.Any(s => s.Option.Index == edited.Destination);
            if (!known) { ZoneStatus.Text = $"There is no scene {edited.Destination} to lead to."; return; }
        }

        if (edited.Game == 1 && lba1Session.EditedScenes.Contains(edited.Scene))
        {
            ZoneStatus.Text = "This scene has unsaved script edits. Save or discard them first, so the zone change isn't overwritten.";
            return;
        }
        if (edited.Game == 2)
        {
            if (scriptSession.EditedScenes.Contains(edited.Scene))
            {
                ZoneStatus.Text = "This scene has unsaved script edits. Save or discard them first, so the zone change isn't overwritten.";
                return;
            }
            if (!interiorSceneActive && openAttributesWindows.Count > 0)
            {
                ZoneStatus.Text = "Close the actor windows first: saving reloads the island, which drops unsaved actor edits.";
                return;
            }
        }

        try
        {
            SceneZones.Save(edited);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            ZoneStatus.Text = $"Not saved: {error.Message}";
            return;
        }

        zoneCache.Clear();
        var reference = edited.Ref;
        if (edited.Game == 1) { lba1Session.ForgetScene(edited.Scene); ReloadLba1AfterEdit(); }
        else
        {
            scriptSession.ForgetScene(edited.Scene);
            if (interiorSceneActive) ShowInteriorScene(interiorSceneNumber, keepView: true);
            else
            {
                InvalidateNativeIsland();
                if (nativeViewActive) RenderNativeCamera();
            }
        }
        SelectZone(reference, showTab: false);
        RefreshZoneListIfVisible();
        ZoneStatus.Text = "Saved to SCENE.HQR (the first save keeps SCENE.HQR.bak).";
    }

    private void ZoneGoto_Click(object sender, RoutedEventArgs e)
    {
        if (zoneOriginal is not { Type: 0 } zone) return;
        var destination = zoneFieldBoxes.FirstOrDefault(f => f.Field.Label == "Destination scene").Box;
        var scene = destination is not null && TryInt(destination, out var typed) ? typed : zone.Destination;
        ZoneStatus.Text = zone.Game == 1 ? GoToLba1Scene(scene) : GoToLba2Scene(scene);
    }

    private string GoToLba1Scene(int scene)
    {
        var info = lba1Game?.Scenes.FirstOrDefault(s => s.Index == scene);
        if (info is null) return $"There is no scene {scene}.";
        var island = islandOptions.FirstOrDefault(o => o.Index == info.Island);
        if (island is null) return $"Scene {scene}'s island isn't in the list.";
        if (!Equals(IslandCombo.SelectedItem, island)) IslandCombo.SelectedItem = island;
        // A scene inside a joined map is reached through the map's entry.
        var option = sceneOptions.FirstOrDefault(o => o.Index == scene);
        if (option is null && lba1JoinAreas)
        {
            var area = lba1Game!.Areas.Select((a, i) => (a, i)).FirstOrDefault(x => x.a.Tiles.Any(t => t.Scene == scene));
            if (area.a is not null) option = sceneOptions.FirstOrDefault(o => o.Index == AreaOption(area.i));
        }
        if (option is null) return $"Scene {scene} isn't in the scene list.";
        if (!Equals(SceneCombo.SelectedItem, option)) SceneCombo.SelectedItem = option;
        return $"Opened scene {scene}.";
    }

    private string GoToLba2Scene(int scene)
    {
        var entry = allSceneEntries.FirstOrDefault(e => e.Option.Index == scene);
        if (entry is null) return $"There is no scene {scene}.";
        if (!entry.IsInterior)
            return $"Scene {scene} is an outdoor scene ({entry.IslandFile ?? "other island"}): choose that island to see it.";
        ShowInteriorScene(scene);
        DocumentTitle.Text = entry.Option.Display;
        FileLabel.Text = $"●  {entry.Option.Display} / SCENE.HQR";
        return $"Opened scene {scene}.";
    }
}
