using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

    // Per-window (not shared/cached like cachedAnimOptions): this actor's
    // own animation list with its native moveset -- see
    // BuildAnimOptionsForActor -- sorted to the top, ahead of a separator,
    // ahead of everything else. Falls back to cachedAnimOptions unchanged
    // (no separator) when the native lookup finds nothing, e.g. this
    // actor's scene isn't the one currently loaded.
    private IReadOnlyList<NamedOption> animOptionsForActor = Array.Empty<NamedOption>();

    // Debounces CommitPreviewChange() while typing a numeric index directly
    // (rather than picking from the dropdown, which commits instantly via
    // SelectionChanged below) -- committing on every keystroke would
    // recalibrate against "1", then "12", then "123" as the user types a
    // 3-digit index, each a valid-but-throwaway intermediate value.
    private DispatcherTimer? textCommitTimer;
    private DispatcherTimer? resizeTimer;

    // previewAngle still advances by this many of the engine's 4096-per-turn
    // units each 60ms tick -- 0 stops the turntable dead (matching the
    // slider's own "0 is stopped" label) without needing a separate pause
    // flag for rotation specifically. Click-and-drag (below) always
    // overrides this while the mouse button is held, regardless of its
    // value, then rotation resumes at this rate on release.
    private int rotationSpeed = 24;
    private bool isDraggingPreview;
    private double dragLastX;

    // Mirrors PauseAnimationCheck -- re-sent to the native side on every
    // single render (RenderPreviewFrame), not just when the checkbox
    // changes: lba2_renderer_set_body_preview_animation_paused's own flag is
    // a single global in the native library, shared by every open Attributes
    // window's preview (there's only one scratch preview object -- see
    // AffichageBodyPreview's own comment). Re-asserting this window's own
    // preference immediately before each of its own renders means whichever
    // window rendered most recently always gets its own pause state applied
    // correctly, rather than one window's checkbox leaking into another's.
    private bool pauseAnimation;

    internal ActorAttributesWindow(CommunityRendererBackend nativeRenderer, byte[] palette, int actorIndex)
    {
        InitializeComponent();
        DebugLog.Log($"ActorAttributesWindow[{actorIndex}]: constructing");
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
        cachedAnimOptions ??= LoadOptions("ANIM2.HQD", null);
        animOptionsForActor = BuildAnimOptionsForActor(cachedAnimOptions);
        AnimCombo.ItemsSource = animOptionsForActor;

        textCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        textCommitTimer.Tick += (_, _) => { textCommitTimer!.Stop(); CommitPreviewChange(); };
        resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        resizeTimer.Tick += (_, _) => { resizeTimer!.Stop(); RecalibratePreview(); };

        LoadCurrentValues();
        if (cachedBodyWarning is not null) StatusLabel.Text = cachedBodyWarning;

        // Forces a layout pass before the window is even shown, so
        // PreviewBorder.ActualWidth/ActualHeight (read by CommitPreviewChange
        // via GetPreviewAspect) reflect the real panel size for the very
        // first render instead of the pre-layout default of 0.
        UpdateLayout();

        // Commits the just-loaded body/anim and calibrates+renders once,
        // synchronously, before the window is even shown -- previously the
        // first frame only appeared once the timer's own first tick landed
        // (and only if the combo boxes' text happened to still be
        // parseable at that moment), which is what "doesn't display when
        // initially loaded" was.
        CommitPreviewChange();

        // The panel's aspect ratio only really changes on a window resize
        // (the Grid column/row split is otherwise fixed) -- re-running the
        // full 8-angle calibration on every SizeChanged during an active
        // drag would be wasteful, so this debounces to once the user
        // settles, same pattern as textCommitTimer.
        PreviewBorder.SizeChanged += (_, _) => { resizeTimer!.Stop(); resizeTimer.Start(); };

        Closed += (_, _) => previewTimer?.Stop();
        previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        previewTimer.Tick += (_, _) => TickPreview();
        previewTimer.Start();
    }

    // hqrFileName is null for a list that shouldn't be cross-validated/
    // padded against any HQR archive's own entry count (see the ANIM case
    // above) -- LoadResult then sizes purely from the HQD file's own line
    // count. HqrArchive.Count/ValidIndices are deliberately not used for
    // the cross-validated (body) case -- see HqrArchive.CountEntries's own
    // comment for why -- ValidIndices' bounds-check filtering also happened
    // to let exactly one bogus entry at the boundary through for BODY.HQR.
    private static IReadOnlyList<NamedOption> LoadOptions(string hqdFileName, string? hqrFileName)
    {
        var hqrCount = 0;
        if (hqrFileName is not null)
        {
            try { hqrCount = HqrArchive.CountEntries(Path.Combine(EditorSettings.Current.GameDirectory, hqrFileName)); }
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

    // Surfaces this actor's own real moveset (lba2_renderer_get_actor_native_
    // anims -- raw ANIM.HQR indices straight from its own PtrFile3D
    // character-fiche table, the same one SearchAnim() resolves scripted
    // GetAnim() calls through) at the top of the dropdown, ahead of a
    // separator, ahead of every other archive animation -- rather than
    // making the user hunt through ~90 alphabetically-unrelated entries for
    // the handful this character can actually play. Falls back to the
    // unmodified full list (no separator) when the native lookup finds
    // nothing, e.g. this actor's scene isn't the one currently loaded (see
    // that API's own doc comment) -- the dropdown still works, just
    // unsorted, rather than showing an empty or broken list.
    private IReadOnlyList<NamedOption> BuildAnimOptionsForActor(IReadOnlyList<NamedOption> all)
    {
        var native = nativeRenderer.RendererLibrary?.GetActorNativeAnims(actorIndex) ?? Array.Empty<int>();
        if (native.Count == 0) return all;

        // native holds raw ANIM.HQR indices across the archive's full ~2000+
        // range; all only has entries for ANIM2.HQD's own much smaller
        // named range (deliberately -- see LoadOptions's own comment on why
        // it doesn't inflate to the full archive). A character's real
        // moveset routinely references anims outside that named range (most
        // non-Twinsen characters' own animations live well past index ~90),
        // so a native entry with no matching named option still needs its
        // own bare-number entry here -- matching the "$" no-name fallback
        // LoadOptions itself already uses -- rather than being silently
        // dropped, which is what made this look like it was doing nothing
        // for every actor except when its moveset happened to overlap
        // Twinsen's own low-index range.
        var byIndex = all.ToDictionary(o => o.Index);
        var seen = new HashSet<int>();
        var natural = new List<NamedOption>(native.Count);
        foreach (var index in native)
        {
            if (!seen.Add(index)) continue; // a fiche can list the same generic anim more than once
            natural.Add(byIndex.TryGetValue(index, out var named) ? named : new NamedOption(index, index.ToString()));
        }
        if (natural.Count == 0) return all;
        var other = all.Where(o => !seen.Contains(o.Index)).ToList();

        var merged = new List<NamedOption>(natural.Count + other.Count + 1);
        merged.AddRange(natural);
        merged.Add(new NamedOption(-1, "--- Other compatible animations ---"));
        merged.AddRange(other);
        return merged;
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
        ArmourBox.Text = armor.ToString();
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
        // Split into two side-by-side lists rather than one long column --
        // right half first item starts at the halfway point, left gets the
        // extra one when the count is odd.
        var half = (flagItems.Count + 1) / 2;
        FlagsListLeft.ItemsSource = flagItems.Take(half).ToList();
        FlagsListRight.ItemsSource = flagItems.Skip(half).ToList();
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

    // Real user typing (not one of FilterCombo/ResetComboFilter's own
    // suppressed Text assignments) also schedules a debounced commit -- see
    // textCommitTimer's own comment for why debounced rather than instant.
    private void BodyCombo_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterCombo(BodyCombo, cachedBodyOptions!, ref suppressBodyTextChanged);
        if (!suppressBodyTextChanged) { textCommitTimer!.Stop(); textCommitTimer.Start(); }
    }
    private void AnimCombo_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterCombo(AnimCombo, animOptionsForActor, ref suppressAnimTextChanged);
        if (!suppressAnimTextChanged) { textCommitTimer!.Stop(); textCommitTimer.Start(); }
    }
    private void BodyCombo_GotFocus(object sender, RoutedEventArgs e) => ResetComboFilter(BodyCombo, cachedBodyOptions!, ref suppressBodyTextChanged);
    private void AnimCombo_GotFocus(object sender, RoutedEventArgs e) => ResetComboFilter(AnimCombo, animOptionsForActor, ref suppressAnimTextChanged);

    // Picking an item from the dropdown updates the ComboBox's own Text to
    // match it, which fires the *same* TextChangedEvent typing does (see
    // that handler's own comment on why it's wired this way) -- so without
    // this, FilterCombo immediately narrowed ItemsSource down to just the
    // one just-picked entry, leaving every subsequent dropdown open showing
    // only that one item until the text was cleared by hand. Restoring the
    // full list right after (suppressed, so it doesn't re-fire filtering)
    // undoes that narrowing regardless of whether it happened, since a
    // selection is never itself a filter request -- only typing is.
    // Re-running ResetComboFilter a second time, deferred to Background
    // priority, is what actually fixes the dropdown-sticks-to-one-entry
    // regression: an editable ComboBox syncs its own Text to match the
    // newly-picked item as part of the very same selection operation, which
    // fires the *same* TextChanged event typing does (see FilterCombo's own
    // comment) -- but WPF doesn't guarantee that sync happens before this
    // SelectionChanged handler runs. When it lands after (which real mouse
    // clicks on a dropdown popup item appear to do, unlike keyboard Down+
    // Enter selection -- the two weren't actually equivalent, despite
    // looking that way when this was first "fixed" and verified only via
    // keyboard selection), the immediate ResetComboFilter below runs too
    // early: it restores the full list, then the late TextChanged fires and
    // FilterCombo narrows it right back down to just the picked entry,
    // which is exactly the regression reported. Re-running ResetComboFilter
    // once more after every dispatcher-queued work from this same selection
    // (including that late TextChanged) has already run undoes that,
    // regardless of which order this particular selection happened to use --
    // it's a no-op when the immediate call above already had the last word.
    // Also fixes a second, related bug found while cleaning up the main
    // window's own new Island/Scene pickers (same underlying mechanism,
    // just never previously exercised here): picking an item from an
    // already-*filtered* list (type a few letters, then click one of the
    // narrowed-down matches) left the text box blank instead of showing the
    // picked item's text -- WPF's own automatic selection-to-text sync for
    // an editable ComboBox apparently loses track of what to display when
    // ItemsSource gets swapped back to the full list (by ResetComboFilter)
    // while that sync is still in-flight. Setting Text ourselves from the
    // actual selected item, rather than trusting that sync to happen at
    // all, sidesteps it entirely instead of chasing WPF's own internal
    // timing.
    private void BodyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressBodyTextChanged) return;
        DebugLog.Log($"ActorAttributesWindow[{actorIndex}]: BodyCombo selection changed, text='{BodyCombo.Text}'");
        if (BodyCombo.SelectedItem is NamedOption selected) { suppressBodyTextChanged = true; BodyCombo.Text = selected.Display; suppressBodyTextChanged = false; }
        CommitPreviewChange();
        ResetComboFilter(BodyCombo, cachedBodyOptions!, ref suppressBodyTextChanged);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            ResetComboFilter(BodyCombo, cachedBodyOptions!, ref suppressBodyTextChanged)));
    }
    private void AnimCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressAnimTextChanged) return;
        DebugLog.Log($"ActorAttributesWindow[{actorIndex}]: AnimCombo selection changed, text='{AnimCombo.Text}'");
        if (AnimCombo.SelectedItem is NamedOption selected) { suppressAnimTextChanged = true; AnimCombo.Text = selected.Display; suppressAnimTextChanged = false; }
        CommitPreviewChange();
        ResetComboFilter(AnimCombo, animOptionsForActor, ref suppressAnimTextChanged);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            ResetComboFilter(AnimCombo, animOptionsForActor, ref suppressAnimTextChanged)));
    }
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
        Recalibrate();
    }

    // The preview panel's own width/height ratio, so CalibrateBodyPreviewDistance
    // can crop to match it instead of forcing a square that then letterboxes
    // inside a panel noticeably taller than it is wide (Stretch="Uniform"
    // fits to whichever dimension the crop's own aspect ratio binds first).
    // 1.0 (square) before the window's first layout pass has run -- see the
    // constructor's own UpdateLayout() call, which makes that the rare case
    // rather than the first frame's own case.
    private double GetPreviewAspect()
        => PreviewBorder.ActualWidth > 0 && PreviewBorder.ActualHeight > 0
            ? PreviewBorder.ActualWidth / PreviewBorder.ActualHeight
            : 1.0;

    private void Recalibrate()
    {
        DebugLog.Log($"ActorAttributesWindow[{actorIndex}]: recalibrating preview body={previewBody} anim={previewAnim}");
        previewCalibration = nativeRenderer.CalibrateBodyPreviewDistance(previewBody, previewAnim, palette, targetAspect: GetPreviewAspect())
            ?? new CommunityRendererBackend.BodyPreviewCalibration(5000, new Int32Rect(0, 0, 640, 480));
        RenderPreviewFrame();
    }

    // Re-fits the crop to the panel's current aspect ratio without treating
    // it as a body/anim change (unlike CommitPreviewChange, which no-ops
    // when body/anim haven't changed) -- called (debounced) from a window
    // resize, since the panel's own proportions are the only thing that
    // makes GetPreviewAspect() return something different.
    private void RecalibratePreview() => Recalibrate();

    private void TickPreview()
    {
        // A manual drag (below) owns previewAngle exclusively while active --
        // the auto-rotation resumes, at whatever rotationSpeed currently is,
        // the instant the mouse button is released.
        if (!isDraggingPreview)
            previewAngle = (previewAngle + rotationSpeed) % 4096; // engine's angle unit is 4096 per full turn (COMMON.H's MAX_ANGLE)
        RenderPreviewFrame();
    }

    private void RenderPreviewFrame()
    {
        if (previewCalibration is not { } calibration)
        {
            ShowPreviewFallback("enter a body index to preview");
            return;
        }
        nativeRenderer.RendererLibrary?.SetBodyPreviewAnimationPaused(pauseAnimation);
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
        DebugLog.Log($"ActorAttributesWindow[{actorIndex}]: Apply clicked");
        var library = nativeRenderer.RendererLibrary;
        if (library is null) { StatusLabel.Text = "Renderer unavailable."; return; }

        if (!TryParseAll(PositionXBox.Text, PositionYBox.Text, PositionZBox.Text, BetaBox.Text,
                ParseLeadingIndex(BodyCombo.Text), ParseLeadingIndex(AnimCombo.Text),
                LifePointBox.Text, ArmourBox.Text, HitForceBox.Text, MoveBox.Text,
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

    private void PauseAnimationCheck_Changed(object sender, RoutedEventArgs e)
    {
        pauseAnimation = PauseAnimationCheck.IsChecked == true;
        RenderPreviewFrame(); // instant feedback rather than waiting for the next 60ms tick
    }

    private void RotationSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => rotationSpeed = (int)RotationSpeedSlider.Value;

    // Degrees-per-pixel-of-drag, in the engine's 4096-per-turn angle unit --
    // chosen as a fixed screen-space rate (not proportional to the preview's
    // own pixel width) so dragging feels the same regardless of how the
    // panel happens to be sized. ~512px of drag makes a full turn.
    private const int DragUnitsPerPixel = 8;

    private void BodyPreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        isDraggingPreview = true;
        dragLastX = e.GetPosition(BodyPreviewImage).X;
        BodyPreviewImage.CaptureMouse();
    }

    private void BodyPreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!isDraggingPreview) return;
        var x = e.GetPosition(BodyPreviewImage).X;
        var deltaX = x - dragLastX;
        dragLastX = x;
        previewAngle = ((previewAngle + (int)(deltaX * DragUnitsPerPixel)) % 4096 + 4096) % 4096;
        RenderPreviewFrame();
    }

    private void BodyPreviewImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        isDraggingPreview = false;
        BodyPreviewImage.ReleaseMouseCapture();
    }
}
