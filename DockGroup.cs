using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LBAAssembler;

// One item hosted in a DockGroup: a title, its content, and what the user is allowed to do to it.
// Mirrors the handful of AvalonDock LayoutAnchorable/LayoutDocument members this app actually used
// (Title, IsVisible, CanClose, CanFloat) -- see the DockGroup class comment for why AvalonDock was
// replaced. SetVisible/Activate just forward to the owning group, so the MainWindow.xaml.cs fields that
// used to be LayoutAnchorable (ZoneDetailsTab, PlayTab, ...) can stay DockItem and keep every existing
// call site (Modes.cs/Zones.cs/Play.cs/Placement.cs) unchanged.
internal sealed class DockItem
{
    public readonly string Key;
    public readonly string Title;
    public readonly FrameworkElement Content;
    public readonly bool CanClose;
    public readonly bool CanFloat;
    public bool IsVisible = true;   // false: closed (not shown as a tab), brought back via SetVisible(true)
    public bool Floating;           // true: living in its own Window right now, not shown as a tab at all
    // false: still shown at its normal tab-strip position (unlike IsVisible=false, which removes the tab
    // entirely and lets its neighbours shift into the gap) but greyed out and not clickable -- for a tab
    // that only applies in some modes (MainWindow.Modes.cs's Details/Build/Script), so the strip's order
    // stays stable and Ctrl+1/2/3 muscle memory keeps landing in the same place regardless of which mode
    // was active before.
    public bool IsEnabled = true;
    internal DockGroup? Owner;
    internal DockFloatWindow? FloatWindow;

    public DockItem(string key, string title, FrameworkElement content, bool canClose, bool canFloat)
    {
        Key = key; Title = title; Content = content; CanClose = canClose; CanFloat = canFloat;
    }

    public void SetVisible(bool visible) => Owner?.SetVisible(Key, visible);
    public void SetEnabled(bool enabled) => Owner?.SetEnabled(Key, enabled);
    public void Activate() => Owner?.Activate(Key);
    // AvalonDock had two names for "the shown tab in its pane" (IsActive: DockingManager-wide;
    // IsSelected: within its own pane); this app only ever has one DockingManager, so both meant the
    // same thing at every call site -- kept as two names so neither call site needed to change.
    public bool IsActive => Owner?.IsItemActive(this) ?? false;
    public bool IsSelected => IsActive;
}

// A dockable pane: one or more DockItems sharing a header (a tab strip once there's more than one),
// with a pin (auto-hide -- collapse to a thin strip, peek via a flyout) and, per item, close and float
// (pop out to its own Window; closing that window docks it back). This is a small, purpose-built
// replacement for AvalonDock (removed because the whole app only ever used a handful of its features --
// a fixed split layout with no runtime rearranging or saved layout, one tabbed group, show/hide, activate,
// and one "tab selected" event -- see the commit that introduced this file for the fuller inventory).
// Only float and auto-hide have no prior code driving them; everything else mirrors the exact API surface
// MainWindow.xaml.cs's own AvalonDock wrapper region already had (SetPanelVisible/ActivatePanel), so the
// call sites in MainWindow.Modes.cs/Zones.cs/Play.cs/Placement.cs did not need to change.
internal sealed class DockGroup : Grid
{
    private readonly List<DockItem> items = new();
    private readonly StackPanel tabStrip = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock soloTitle = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 10 };
    private readonly ToggleButton pinButton = new() { Width = 22, Height = 22, Margin = new Thickness(0, 0, 2, 0), ToolTip = "Auto-hide (collapse to a strip; click a tab to peek)" };
    private readonly Button floatButton = new() { Width = 22, Height = 22, Margin = new Thickness(0, 0, 2, 0), Content = "◱", ToolTip = "Float (pop out to its own window)" };
    private readonly ContentControl body = new();
    private readonly Border headerBorder;
    private readonly RowDefinition bodyRow = new() { Height = new GridLength(1, GridUnitType.Star) };
    private Popup? flyout;
    private readonly ContentControl flyoutHost = new();
    private DockItem? active;
    private bool pinned;

    public double NormalSize = 200;              // the row/column height or width this group asks for while docked normally
    private const double CollapsedStripSize = 26; // header-only height/width while pinned off

    public event EventHandler<DockItem>? ActiveItemChanged;

    public DockGroup()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(bodyRow);

        var header = new DockPanel { LastChildFill = true, Height = 26 };
        header.SetResourceReference(Panel.BackgroundProperty, "ThemeHeaderBrush");
        Grid.SetRow(header, 0);
        DockPanel.SetDock(soloTitle, Dock.Left);
        tabStrip.Margin = new Thickness(2, 0, 0, 0);
        // A solo pane (one item, no tab strip) has nothing else to click to peek at it while pinned off --
        // reads `active` live (not captured), since RebuildHeader never re-subscribes this.
        soloTitle.MouseLeftButtonDown += (_, e) => { if (pinned && active is not null) ShowFlyout(active); e.Handled = true; };

        pinButton.Content = "\U0001F4CC";
        pinButton.Click += (_, _) => SetPinned(!pinned);
        floatButton.Click += (_, _) => { if (active is { CanFloat: true } a) Float(a); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
        buttons.Children.Add(pinButton);
        buttons.Children.Add(floatButton);
        DockPanel.SetDock(buttons, Dock.Right);

        header.Children.Add(buttons);
        header.Children.Add(soloTitle);
        header.Children.Add(tabStrip);
        headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.SetResourceReference(Border.BorderBrushProperty, "ThemeBorderBrush");
        Grid.SetRow(headerBorder, 0);
        Children.Add(headerBorder);

        Grid.SetRow(body, 1);
        body.SetResourceReference(Control.BackgroundProperty, "ThemeWindowBrush");
        Children.Add(body);
    }

    public DockItem AddItem(string key, string title, FrameworkElement content, bool canClose = true, bool canFloat = true)
    {
        if (content.Parent is Panel oldParent) oldParent.Children.Remove(content);   // e.g. MainWindow.xaml's hidden content pool
        var item = new DockItem(key, title, content, canClose, canFloat) { Owner = this };
        items.Add(item);
        RebuildHeader();
        if (active is null) Activate(key);
        return item;
    }

    // Documents (this app's one Viewport) don't auto-hide the way anchorables do -- AvalonDock didn't
    // offer it for LayoutDocumentPane either.
    public bool AllowPin { set => pinButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    public DockItem? Find(string key) => items.FirstOrDefault(i => i.Key == key);
    internal bool IsItemActive(DockItem item) => active == item;

    // Shows or hides a closed panel -- the "Panels" menu's per-name reopen entries. Floating items are
    // left alone (visible means "back on the tab strip", not "stop floating"; SetVisible(false) on a
    // floating item just marks it non-visible for later).
    public void SetVisible(string key, bool visible)
    {
        var item = Find(key);
        if (item is null || item.IsVisible == visible) return;
        item.IsVisible = visible;
        RebuildHeader();
        if (visible) Activate(key);
        else if (active == item) Activate(items.FirstOrDefault(i => i.IsVisible && i.IsEnabled && !i.Floating)?.Key ?? "");
    }

    // Modes.cs's per-mode tab availability: keeps the tab in the strip at its normal position, greyed
    // out and unclickable, instead of removing it (see IsEnabled's own comment on DockItem).
    public void SetEnabled(string key, bool enabled)
    {
        var item = Find(key);
        if (item is null || item.IsEnabled == enabled) return;
        item.IsEnabled = enabled;
        if (!enabled && active == item)
        {
            var fallback = items.FirstOrDefault(i => i.IsVisible && i.IsEnabled && !i.Floating);
            if (fallback is not null) SetActive(fallback);
        }
        RebuildHeader();
    }

    // Shows (if closed) and selects a panel -- "bring this to the front," used when a zone is clicked,
    // Play mode starts, etc. Un-floats it first if it was floating, matching what a user would expect
    // clicking "Play" in the menu to do even if Play is currently sitting in its own window. A disabled
    // tab (wrong mode for it right now) can't be activated -- callers that want to jump to one of these
    // switch mode first (MainWindow.xaml.cs's ShowPanel_Click).
    public void Activate(string key)
    {
        var item = Find(key);
        if (item is null || !item.IsEnabled) return;
        if (item.Floating) Unfloat(item);
        if (!item.IsVisible) { item.IsVisible = true; RebuildHeader(); }
        if (pinned) { ShowFlyout(item); return; }
        SetActive(item);
    }

    private void SetActive(DockItem item)
    {
        active = item;
        Detach(item.Content);
        body.Content = item.Content;
        RebuildHeader();
        ActiveItemChanged?.Invoke(this, item);
    }

    // body and flyoutHost are two separate ContentControls that can each hold at most one of these items'
    // Content at a time; WPF throws ("must disconnect... before attaching to new parent") if the same
    // element is still sitting as the other one's Content when it's assigned here. Pinning doesn't clear
    // body.Content (only Visibility), and activating while pinned goes to flyoutHost instead of body -- so
    // without this, unpinning (or activating a second item while pinned, then coming back to the first)
    // reuses a Content value the other host never let go of.
    private void Detach(FrameworkElement content)
    {
        if (ReferenceEquals(body.Content, content)) body.Content = null;
        if (ReferenceEquals(flyoutHost.Content, content)) flyoutHost.Content = null;
    }

    public void SetPinned(bool value)
    {
        pinned = value;
        pinButton.SetResourceReference(Control.ForegroundProperty, pinned ? "ThemeAccentBrush" : "ThemeTextMutedBrush");
        bodyRow.Height = pinned ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        body.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        Height = pinned ? CollapsedStripSize : double.NaN;
        flyout?.SetCurrentValue(Popup.IsOpenProperty, false);
    }

    // While pinned off, clicking a tab peeks at its content in a popup anchored under the header
    // instead of expanding the pane -- closes itself on any outside click (Popup's own StaysOpen=False).
    private void ShowFlyout(DockItem item)
    {
        if (flyout is null)
        {
            flyout = new Popup { PlacementTarget = headerBorder, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
            var border = new Border { BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BackgroundProperty, "ThemeWindowBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "ThemeBorderStrongBrush");
            border.Child = flyoutHost;
            flyout.Child = border;
        }
        flyoutHost.Width = ActualWidth > 0 ? ActualWidth : NormalSize;
        flyoutHost.Height = NormalSize;
        Detach(item.Content);
        flyoutHost.Content = item.Content;
        active = item;
        RebuildHeader();
        // Deferred: a StaysOpen=False popup opened synchronously from inside a mouse-down handler closes
        // itself right back on that same click's mouse-up (WPF treats the still-in-progress click as an
        // outside interaction before focus finishes moving to the popup). Opening it on the next dispatcher
        // pass, after the click has fully finished, avoids that.
        Dispatcher.BeginInvoke(() => flyout!.IsOpen = true, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Float(DockItem item)
    {
        if (!item.CanFloat || item.Floating) return;
        item.Floating = true;
        Detach(item.Content);   // might be sitting in body (active, docked) or flyoutHost (active, peeked while pinned)
        var window = new DockFloatWindow(item, this);
        item.FloatWindow = window;
        RebuildHeader();
        if (active == item) Activate(items.FirstOrDefault(i => i.IsVisible && !i.Floating)?.Key ?? "");
        window.Show();
    }

    // Called by DockFloatWindow when its own window closes (the redock button, or the window X either way).
    internal void Unfloat(DockItem item)
    {
        if (!item.Floating) return;
        item.Floating = false;
        item.FloatWindow = null;
        RebuildHeader();
    }

    private void RebuildHeader()
    {
        var visible = items.Where(i => i.IsVisible && !i.Floating).ToList();
        tabStrip.Children.Clear();
        soloTitle.Visibility = Visibility.Collapsed;
        tabStrip.Visibility = Visibility.Collapsed;

        // Nothing left to show here (its one item floated out, or every item got closed): give the space
        // back rather than reserving a blank slot -- DockSplitter.Min/Max clamps normal-sized siblings, but
        // an empty group's own explicit size otherwise just sits there empty.
        Visibility = visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (visible.Count == 0) { pinButton.IsEnabled = floatButton.IsEnabled = false; return; }
        if (visible.Count == 1)
        {
            soloTitle.Visibility = Visibility.Visible;
            soloTitle.Text = visible[0].Title;
            soloTitle.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextMutedBrush");
        }
        else
        {
            tabStrip.Visibility = Visibility.Visible;
            foreach (var item in visible) tabStrip.Children.Add(BuildTab(item));
        }
        pinButton.IsEnabled = true;
        floatButton.IsEnabled = active?.CanFloat ?? false;
        floatButton.Visibility = (active?.CanFloat ?? false) ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildTab(DockItem item)
    {
        var isActive = item == active;
        var text = new TextBlock { Text = item.Title, FontFamily = new FontFamily("Consolas"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(text);
        if (item.CanClose && item.IsEnabled)
        {
            var close = new Button
            {
                Content = "×", Width = 16, Height = 16, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            };
            close.SetResourceReference(Control.ForegroundProperty, "ThemeTextMutedBrush");
            close.Click += (_, e) => { e.Handled = true; SetVisible(item.Key, false); };
            row.Children.Add(close);
        }
        var tab = new Border
        {
            Child = row, Padding = new Thickness(10, 4, 8, 4), Margin = new Thickness(0, 0, 2, 0), Cursor = item.IsEnabled ? Cursors.Hand : Cursors.Arrow,
            BorderThickness = new Thickness(1, 1, 1, 0),
            // Matches IslandEditorView's own disabled-control opacity (0.45) rather than inventing a second convention.
            Opacity = item.IsEnabled ? 1.0 : 0.45,
        };
        tab.SetResourceReference(Border.BackgroundProperty, isActive ? "ThemeWindowBrush" : "ThemeRaisedBrush");
        tab.SetResourceReference(Border.BorderBrushProperty, "ThemeBorderBrush");
        text.SetResourceReference(TextBlock.ForegroundProperty, isActive ? "ThemeTextBrush" : "ThemeTextMutedBrush");
        if (item.IsEnabled) tab.MouseLeftButtonDown += (_, e) => { if (pinned) ShowFlyout(item); else SetActive(item); e.Handled = true; };
        else tab.ToolTip = $"{item.Title} isn't available in the current mode";
        return tab;
    }
}
