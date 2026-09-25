using System.Windows;
using System.Windows.Controls;

namespace LBAAssembler;

// Assembles the main window's side panels (see MainWindow.xaml's PanelContentPool and empty LayoutHost)
// into DockGroups -- a small, purpose-built replacement for AvalonDock. AvalonDock was dropped after an
// investigation found this app only ever used a narrow slice of it: a fixed split layout with no runtime
// rearranging or saved layout between sessions, one 5-way tabbed group, programmatic show/hide/activate
// (SetPanelVisible/ActivatePanel, used from Modes.cs/Zones.cs/Play.cs/Placement.cs), and one "tab
// selected" event (Details, to refresh the zone list). Floating and auto-hide had no code driving them
// -- they worked purely as AvalonDock's own stock chrome -- but real users relied on them, so DockGroup
// reimplements both (see its own comment) rather than dropping them.
public partial class MainWindow
{
    private DockItem LocationAnchorable = null!;
    private DockItem ModeAnchorable = null!;
    private DockItem ZonesTab = null!;
    private DockItem ZoneDetailsTab = null!;
    private DockItem BuildTab = null!;
    private DockItem PlayTab = null!;
    private DockItem ScriptTab = null!;
    private DockItem MinimapAnchorable = null!;

    private void BuildDockLayout()
    {
        var locationGroup = new DockGroup { NormalSize = 66, Height = 66 };
        LocationAnchorable = locationGroup.AddItem("Location", "Location", LocationPanelContent, canClose: false);

        var viewportGroup = new DockGroup { AllowPin = false };
        viewportGroup.AddItem("Viewport", "Viewport", ViewportPanelContent, canClose: false, canFloat: false);

        var modeGroup = new DockGroup { NormalSize = 76, Height = 76 };
        ModeAnchorable = modeGroup.AddItem("Mode", "Mode", ModePanelContent, canClose: false);

        var tabGroup = new DockGroup { NormalSize = 300 };
        ZonesTab = tabGroup.AddItem("Zones", "Zones", ZonesPanelContent);
        ZoneDetailsTab = tabGroup.AddItem("Details", "Zone details", DetailsPanelContent);
        BuildTab = tabGroup.AddItem("Build", "Build", BuildPanelContent);
        PlayTab = tabGroup.AddItem("Play", "Play", PlayPanelContent);
        ScriptTab = tabGroup.AddItem("Script", "Script", ScriptPanelContent);
        tabGroup.Activate("Zones");   // AddItem's own "first item added becomes active" already lands here; explicit for clarity
        tabGroup.ActiveItemChanged += (_, item) => { if (item == ZoneDetailsTab) RefreshZoneList(); };

        var minimapGroup = new DockGroup { NormalSize = 270, Height = 270 };
        MinimapAnchorable = minimapGroup.AddItem("Minimap", "Minimap", MinimapPanelContent);

        // Right column: Mode (top, fixed height) / the tab group (fills what's left) / Minimap (bottom, fixed
        // height), each independently resizable against its neighbour via a DockSplitter.
        var rightColumn = new DockPanel { LastChildFill = true, Width = 330 };
        DockPanel.SetDock(modeGroup, Dock.Top);
        rightColumn.Children.Add(modeGroup);
        rightColumn.Children.Add(Splitter(modeGroup, resizesWidth: false, sizeSign: +1, Dock.Top));
        DockPanel.SetDock(minimapGroup, Dock.Bottom);
        rightColumn.Children.Add(minimapGroup);
        rightColumn.Children.Add(Splitter(minimapGroup, resizesWidth: false, sizeSign: -1, Dock.Bottom));
        rightColumn.Children.Add(tabGroup);   // last child: fills whatever height is left

        // Middle row: the viewport fills what's left of the width once the right column (fixed width) is placed.
        var middleRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(rightColumn, Dock.Right);
        middleRow.Children.Add(rightColumn);
        middleRow.Children.Add(Splitter(rightColumn, resizesWidth: true, sizeSign: -1, Dock.Right));
        middleRow.Children.Add(viewportGroup);   // last child: fills whatever width is left

        // Root: Location on top (fixed height), everything else below it.
        DockPanel.SetDock(locationGroup, Dock.Top);
        LayoutHost.Children.Add(locationGroup);
        LayoutHost.Children.Add(Splitter(locationGroup, resizesWidth: false, sizeSign: +1, Dock.Top));
        LayoutHost.Children.Add(middleRow);   // last child: fills whatever height is left
    }

    private static DockSplitter Splitter(FrameworkElement target, bool resizesWidth, int sizeSign, Dock dock)
    {
        var splitter = new DockSplitter(target, resizesWidth, sizeSign);
        DockPanel.SetDock(splitter, dock);
        return splitter;
    }
}
