using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LBA2LevelEditor;

// Non-modal window for editing one actor's attributes -- position, facing,
// body/animation (with a filterable name-lookup dropdown), collision/
// physics flags, and combat stats -- plus a rotating live preview of the
// selected body and a jumping-off point to that actor's script editor.
// Edits apply immediately (SetActorAttributes/SetActorPosition/
// SetActorFlags are session-only: they update the live renderer and
// survive panning, per RENDERER_API.H's own doc comments on those calls,
// but aren't written to the game's SCENE.HQR yet -- that's a separate,
// not-yet-built save path). MainWindow opens one of these per actor, each
// its own taskbar entry, so several can be open side by side.
public partial class ActorAttributesWindow : Window
{
    public event Action<int>? OpenScriptRequested;

    private sealed record NamedOption(int Index, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed class FlagCheckItem
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public uint Bit { get; init; }
        public bool IsChecked { get; set; }
    }

    private readonly CommunityRendererBackend nativeRenderer;
    private readonly byte[] palette;
    private readonly int actorIndex;
    private List<FlagCheckItem> flagItems = new();
    private DispatcherTimer? previewTimer;
    private int previewAngle;

    // What TickPreview() actually renders -- set explicitly by
    // CommitPreviewChange() (on a committed body/anim edit, or once up
    // front from the constructor) rather than re-parsed from whatever the
    // combo boxes' live text happens to contain on every 60ms tick, which
    // was fragile: mid-typing or mid-filter text is often unparseable, and
    // there was no guarantee a freshly-selected value would still be
    // sitting in .Text by the time the next tick ran. previewCalibration is
    // null until the first calibration completes, which TickPreview()
    // treats as "nothing to draw yet" rather than falling back to a guess.
    private int previewBody;
    private int previewAnim;
    private CommunityRendererBackend.BodyPreviewCalibration? previewCalibration;

    // Body/anim name lists are read from disk (an HQD text file, plus --
    // for body only -- an HQR entry count) and don't change during the
    // app's lifetime -- cached once per option kind across every
    // attributes window instance rather than re-read from disk each time
    // one opens.
    private static IReadOnlyList<NamedOption>? cachedBodyOptions;
    private static IReadOnlyList<NamedOption>? cachedAnimOptions;
    private static string? cachedBodyWarning;

    private bool suppressBodyTextChanged;
    private bool suppressAnimTextChanged;

    internal ActorAttributesWindow(CommunityRendererBackend nativeRenderer, byte[] palette, int actorIndex)
    {
        InitializeComponent();
        this.nativeRenderer = nativeRenderer;
        this.palette = palette;
        this.actorIndex = actorIndex;
        Title = $"Actor {actorIndex} Attributes";
        TitleLabel.Text = $"Actor {actorIndex}";

        // ComboBox doesn't expose TextChanged as its own CLR event (only
        // TextBox does) -- for an editable ComboBox it's raised by the
        // internal TextBox template part and bubbles up as a routed event,
        // so it has to be picked up via AddHandler instead of a plain XAML
        // attribute.
        BodyCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(BodyCombo_TextChanged));
        AnimCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(AnimCombo_TextChanged));

        BodyCombo.ItemsSource = cachedBodyOptions ??= LoadOptions("BODY2.HQD", "BODY.HQR");
        // GenAnim (like GenBody) indexes a small per-actor "generic" table
        // (SearchAnim(), FICHE.CPP), not ANIM.HQR's own much larger raw
        // animation-data archive directly -- passing no HQR file here means
        // LoadOptions sizes the list from ANIM2.HQD's own line count alone
        // (~87 entries) instead of inflating it to ANIM.HQR's 2000+.
        AnimCombo.ItemsSource = cachedAnimOptions ??= LoadOptions("ANIM2.HQD", null);

        LoadCurrentValues();
        if (cachedBodyWarning is not null) StatusLabel.Text = cachedBodyWarning;

        // Commits the just-loaded body/anim and calibrates+renders once,
        // synchronously, before the window is even shown -- previously the
        // first frame only appeared once the timer's own first tick landed
        // (and only if the combo boxes' text happened to still be
        // parseable at that moment), which is what "doesn't display when
        // initially loaded" was.
        CommitPreviewChange();

        Closed += (_, _) => previewTimer?.Stop();
        previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        previewTimer.Tick += (_, _) => TickPreview();
        previewTimer.Start();
    }

    // hqrFileName is null for a list that shouldn't be cross-validated/
    // padded against any HQR archive's own entry count (see the ANIM case
    // above) -- LoadResult then sizes purely from the HQD file's own line
    // count. HqrArchive.Count/ValidIndices are deliberately not used for
    // the cross-validated (body) case: HqrArchive.Open() treats the raw
    // offset-table-length header value as the slot count directly instead
    // of dividing by 4 first, so its own Count runs ~4x too high, and
    // ValidIndices' bounds-check filtering happened to let exactly one
    // bogus entry at the boundary through for BODY.HQR -- reading the
    // header and applying the real formula (matching the native engine's
    // own HQF_NbRes()) here avoids both problems without touching that
    // shared reader (used elsewhere for scene enumeration) under time
    // pressure.
    private static int CountHqrEntries(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4) return 0;
        var tableBytes = BitConverter.ToUInt32(bytes, 0);
        var slots = (int)(tableBytes / 4);
        return Math.Max(0, slots - 1);
    }

    private static IReadOnlyList<NamedOption> LoadOptions(string hqdFileName, string? hqrFileName)
    {
        var hqrCount = 0;
        if (hqrFileName is not null)
        {
            try { hqrCount = CountHqrEntries(Path.Combine(EditorSettings.Current.GameDirectory, hqrFileName)); }
            catch
            {
                // No game directory / unreadable HQR yet -- names list
                // still works, just without the cross-validated count as a
                // floor.
            }
        }

        var result = HqdDescriptions.Load(hqdFileName, hqrCount);
        if (hqdFileName.StartsWith("BODY", StringComparison.OrdinalIgnoreCase)) cachedBodyWarning = result.ValidationWarning;

        var options = new List<NamedOption>(result.Names.Count);
        for (var i = 0; i < result.Names.Count; i++)
            options.Add(new NamedOption(i, result.Names[i] is { } name ? $"{i}: {name}" : $"{i}"));
        return options;
    }

    private static string ParseLeadingIndex(string text)
    {
        var colon = text.IndexOf(':');
        return (colon >= 0 ? text[..colon] : text).Trim();
    }

    private void LoadCurrentValues()
    {
        var library = nativeRenderer.RendererLibrary;
        if (library is null || !library.GetActor(actorIndex, out var x, out var y, out var z, out var waypointCount))
        {
            StatusLabel.Text = "Actor data unavailable.";
            return;
        }
        library.GetActorAttributes(actorIndex, out var beta, out var body, out var anim, out var lifePoint, out var armor, out var hitForce, out var move);
        library.GetActorFlags(actorIndex, out var flags);

        PositionXBox.Text = x.ToString();
        PositionYBox.Text = y.ToString();
        PositionZBox.Text = z.ToString();
        BetaBox.Text = beta.ToString();
        // Suppressed: setting .Text here would otherwise run it through the
        // same live-filter TextChanged handler typing does, narrowing
        // BodyCombo/AnimCombo's dropdown to just whatever matches the
        // current numeric value before the user has ever opened it.
        suppressBodyTextChanged = true;
        BodyCombo.Text = body.ToString();
        suppressBodyTextChanged = false;
        suppressAnimTextChanged = true;
        AnimCombo.Text = anim.ToString();
        suppressAnimTextChanged = false;
        LifePointBox.Text = lifePoint.ToString();
        ArmorBox.Text = armor.ToString();
        HitForceBox.Text = hitForce.ToString();
        MoveBox.Text = move.ToString();
        WaypointsLabel.Text = $"{waypointCount} waypoint{(waypointCount == 1 ? "" : "s")} on this actor's track script";

        flagItems = ActorFlags.All.Select(f => new FlagCheckItem
        {
            Name = f.Name,
            Description = f.Description,
            Bit = f.Bit,
            IsChecked = (flags & f.Bit) != 0,
        }).ToList();
        FlagsList.ItemsSource = flagItems;
    }

    // CaretIndex/SelectAll live on the ComboBox's internal editable TextBox
    // template part, not on ComboBox itself.
    private static TextBox? GetEditableTextBox(ComboBox combo)
    {
        combo.ApplyTemplate();
        return combo.Template?.FindName("PART_EditableTextBox", combo) as TextBox;
    }

    // Live-filters a combo's dropdown to items whose display text contains
    // what's typed so far (case-insensitive) -- with 400+ body names and
    // ~90 animation names, scrolling to find one by eye is slow; typing
    // part of a name narrows the list immediately. Only ItemsSource is
    // touched, never SelectedItem/SelectedIndex, so the user's own typed
    // text and caret position come through untouched.
    private static void FilterCombo(ComboBox combo, IReadOnlyList<NamedOption> allOptions, ref bool suppress)
    {
        if (suppress) return;
        var text = combo.Text;
        var caret = GetEditableTextBox(combo)?.CaretIndex ?? text.Length;
        var filtered = string.IsNullOrWhiteSpace(text)
            ? allOptions
            : allOptions.Where(o => o.Display.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        suppress = true;
        combo.ItemsSource = filtered;
        combo.Text = text;
        var editBox = GetEditableTextBox(combo);
        if (editBox is not null) editBox.CaretIndex = caret;
        combo.IsDropDownOpen = combo.IsKeyboardFocused && filtered.Count > 0 && filtered.Count < allOptions.Count;
        suppress = false;
    }

    // Restores the full, unfiltered list on refocusing the combo (e.g. the
    // user filtered down to one entry, picked it, then clicks back in to
    // pick a different one -- without this they'd only ever see that one
    // leftover match), and selects the current text so the very next
    // keystroke starts a fresh filter instead of appending to it.
    private static void ResetComboFilter(ComboBox combo, IReadOnlyList<NamedOption> allOptions, ref bool suppress)
    {
        suppress = true;
        var text = combo.Text;
        combo.ItemsSource = allOptions;
        combo.Text = text;
        suppress = false;
        GetEditableTextBox(combo)?.SelectAll();
    }

    private void BodyCombo_TextChanged(object sender, TextChangedEventArgs e) => FilterCombo(BodyCombo, cachedBodyOptions!, ref suppressBodyTextChanged);
    private void AnimCombo_TextChanged(object sender, TextChangedEventArgs e) => FilterCombo(AnimCombo, cachedAnimOptions!, ref suppressAnimTextChanged);
    private void BodyCombo_GotFocus(object sender, RoutedEventArgs e) => ResetComboFilter(BodyCombo, cachedBodyOptions!, ref suppressBodyTextChanged);
    private void AnimCombo_GotFocus(object sender, RoutedEventArgs e) => ResetComboFilter(AnimCombo, cachedAnimOptions!, ref suppressAnimTextChanged);
    private void BodyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => CommitPreviewChange();
    private void AnimCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => CommitPreviewChange();
    private void BodyCombo_LostFocus(object sender, RoutedEventArgs e) => CommitPreviewChange();
    private void AnimCombo_LostFocus(object sender, RoutedEventArgs e) => CommitPreviewChange();

    // Runs whenever the body/animation selection is actually committed
    // (picked from the dropdown, or the box loses focus after typing) --
    // not on every keystroke, since that would recalibrate and re-render
    // against unparseable mid-typing text constantly. Recalibrating only
    // here, rather than on every rotation tick, also means the (two-render)
    // calibration cost is paid once per body/anim change, not 16-17 times
    // a second.
    private void CommitPreviewChange()
    {
        if (!int.TryParse(ParseLeadingIndex(BodyCombo.Text), out var body)) return;
        var anim = int.TryParse(ParseLeadingIndex(AnimCombo.Text), out var parsedAnim) ? parsedAnim : 0;
        if (previewCalibration.HasValue && body == previewBody && anim == previewAnim) return; // no real change

        previewBody = body;
        previewAnim = anim;
        previewCalibration = nativeRenderer.CalibrateBodyPreviewDistance(previewBody, previewAnim, palette)
            ?? new CommunityRendererBackend.BodyPreviewCalibration(5000, new Int32Rect(0, 0, 640, 480));
        RenderPreviewFrame();
    }

    private void TickPreview()
    {
        previewAngle = (previewAngle + 24) % 4096; // engine's angle unit is 4096 per full turn (COMMON.H's MAX_ANGLE)
        RenderPreviewFrame();
    }

    private void RenderPreviewFrame()
    {
        if (previewCalibration is not { } calibration)
        {
            ShowPreviewFallback("enter a body index to preview");
            return;
        }
        var bitmap = nativeRenderer.RenderBodyPreview(previewBody, previewAnim, previewAngle, calibration.Distance, palette);
        if (bitmap is null)
        {
            ShowPreviewFallback("no body to preview for this index");
            return;
        }
        // Crops to the region CalibrateBodyPreviewDistance measured the
        // body to actually occupy at this distance (which can be smaller
        // than the calibration's own target fraction whenever the near-clip
        // floor in AffichageBodyPreview forced the distance higher than
        // ideal for a small body) and lets the Image element's own
        // Stretch="Uniform" scale that crop back up to fill the panel --
        // CroppedBitmap is a cheap view over the existing frame, not a copy.
        BodyPreviewImage.Source = new CroppedBitmap(bitmap, calibration.CropRect);
        BodyPreviewFallbackLabel.Visibility = Visibility.Collapsed;
    }

    private void ShowPreviewFallback(string message)
    {
        BodyPreviewImage.Source = null;
        BodyPreviewFallbackLabel.Visibility = Visibility.Visible;
        BodyPreviewFallbackLabel.Text = message;
    }

    private static bool TryParseAll(
        string x, string y, string z, string beta, string body, string anim,
        string life, string armor, string hit, string move,
        out int ix, out int iy, out int iz, out int ibeta, out int ibody, out int ianim,
        out int ilife, out int iarmor, out int ihit, out int imove)
    {
        ix = iy = iz = ibeta = ibody = ianim = ilife = iarmor = ihit = imove = 0;
        return int.TryParse(x, out ix) && int.TryParse(y, out iy) && int.TryParse(z, out iz)
            && int.TryParse(beta, out ibeta) && int.TryParse(body, out ibody) && int.TryParse(anim, out ianim)
            && int.TryParse(life, out ilife) && int.TryParse(armor, out iarmor) && int.TryParse(hit, out ihit)
            && int.TryParse(move, out imove);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var library = nativeRenderer.RendererLibrary;
        if (library is null) { StatusLabel.Text = "Renderer unavailable."; return; }

        if (!TryParseAll(PositionXBox.Text, PositionYBox.Text, PositionZBox.Text, BetaBox.Text,
                ParseLeadingIndex(BodyCombo.Text), ParseLeadingIndex(AnimCombo.Text),
                LifePointBox.Text, ArmorBox.Text, HitForceBox.Text, MoveBox.Text,
                out var x, out var y, out var z, out var beta, out var body, out var anim,
                out var lifePoint, out var armor, out var hitForce, out var move))
        {
            StatusLabel.Text = "One or more fields aren't valid whole numbers.";
            return;
        }

        var okAttrs = library.SetActorAttributes(actorIndex, beta, body, anim, lifePoint, armor, hitForce, move);
        var okPos = library.SetActorPosition(actorIndex, x, y, z);

        // Only the flags this window actually exposes are touched -- start
        // from whatever the actor's flags currently are (which may include
        // bits this editor deliberately doesn't show, e.g. SPRITE_3D) and
        // replace just the exposed ones, rather than reconstructing the
        // whole word from only what's checked here.
        var okFlags = false;
        if (library.GetActorFlags(actorIndex, out var currentFlags))
        {
            var knownMask = flagItems.Aggregate(0u, (mask, item) => mask | item.Bit);
            var newFlags = (currentFlags & ~knownMask) | flagItems.Where(i => i.IsChecked).Aggregate(0u, (mask, item) => mask | item.Bit);
            okFlags = library.SetActorFlags(actorIndex, newFlags);
        }

        StatusLabel.Text = okAttrs && okPos && okFlags
            ? "Applied for this session. Not yet saved to the game's files."
            : "Failed to apply -- this actor may no longer be valid (e.g. after switching islands).";
    }

    private void EditScript_Click(object sender, RoutedEventArgs e) => OpenScriptRequested?.Invoke(actorIndex);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
