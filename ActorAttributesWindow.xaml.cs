using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LBAAssembler;

// Non-modal window for editing one actor's attributes -- position, facing,
// body/animation (with a filterable name-lookup dropdown), collision/
// physics flags, and combat stats -- plus a rotating live preview of the
// selected body and a jumping-off point to that actor's script editor.
// Apply always updates the live native session first (SetActorAttributes/
// SetActorPosition/SetActorFlags), then also writes the edit into the
// actor's own scene record in SCENE.HQR -- see TrySaveToFile and
// Lba2ActorPersistence -- for every field except a body/animation the
// actor's own kind of actor (FILE3D entity) doesn't already offer, which
// stays session-only, with the status line saying so. An actor added this
// session (Add Actor Here) has no kind of actor yet either: EntityPanel/
// EntityCombo (shown only for one of these) let a "kind of actor" be picked
// first, narrowing Body/Anim to what it offers, so Apply can add it to the
// scene file too instead of leaving it session-only forever. MainWindow
// opens one of these per actor, each its own taskbar entry, so several can
// be open side by side; each remembers its own kind's last screen position
// (WindowPlacement) and closes automatically if the main window does.
public partial class ActorAttributesWindow : Window
{
    public event Action<int>? OpenScriptRequested;
    // Hands a raw body payload (an entry read from a project test archive, e.g. mario.hqr) to MainWindow, which
    // installs it into a throwaway preview copy of BODY.HQR (see MainWindow.BodyDebugPreview.cs) and returns its
    // new index there -- or null if a live preview couldn't be started (most likely a terrain edit already has
    // the native renderer's one live-preview slot).
    public event Func<byte[], int?>? LoadDebugBodyRequested;
    // Same idea, for a raw animation payload (an ANIM.HQR entry, e.g. from AnimGenerator's own
    // walk/run/idle/jump output) -- installed into a throwaway preview copy of ANIM.HQR instead
    // (MainWindow.BodyDebugPreview.cs's own LoadDebugAnim). Requires a debug body already active.
    public event Func<byte[], int?>? LoadDebugAnimRequested;

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
    // A loaded debug body's own colours are only correct under the specific palette Body Studio itself assumes
    // when generating/previewing one (RESS.HQR entry 0's raw 256-colour table -- see Generation.Palette in
    // BodyStudio/Generation.cs) -- NOT whatever island palette the live scene happens to have loaded, which is
    // what `palette` above otherwise always is. The native renderer's own framebuffer is just palette indices,
    // palette-agnostic at that level (the RGB conversion happens entirely client-side, same mechanism as the
    // interior-palette fix elsewhere in this session) -- so swapping only this managed-side table, only while
    // previewing the debug body itself, is enough to make it render in its own real colours. Confirmed this
    // wasn't specific to debug bodies: a real, unmodified retail body (index 468) manually selected into
    // BodyCombo -- outside its own native island's context -- showed the exact same wrong-palette black
    // silhouette, proving this is a pre-existing characteristic of previewing any body out of its own island
    // context, not a bug in the debug-body reload itself.
    private byte[]? debugBodyPalette;
    private int debugBodyIndex = -1;
    private byte[] EffectivePalette => previewBody == debugBodyIndex && debugBodyPalette is { } p ? p : palette;
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
    // True while a call into the native renderer's own body/animation preview is in flight
    // (CalibrateBodyPreviewDistance, RenderBodyPreview, RenderSpritePreview). Confirmed via a real crash
    // report (0xc0000005 inside CommunityRendererBackend.RenderBodyPreview) that WPF's message pump can
    // re-enter while one of these calls is still running -- the crash's own managed stack showed the 60ms
    // preview timer's own TickPreview firing, and reaching a second, nested RenderBodyPreview call, while
    // a mouse-wheel-triggered ComboBox change's own Recalibrate -> CalibrateBodyPreviewDistance call was
    // still on the stack beneath it. The native renderer's own body-preview state (a single shared scratch
    // object, BODY_PREVIEW_OBJ) isn't reentrant-safe, so two such calls in flight at once corrupt it.
    // static, not per-instance: MainWindow can have several ActorAttributesWindow instances open side by
    // side (its own constructor comment), each with its own independent 60ms timer, but they all still
    // share the SAME native BODY_PREVIEW_OBJ scratch slot -- an instance field would only guard a single
    // window against itself and miss the cross-window case. Every method that calls into the native
    // preview renderer checks this first and skips its own call (rather than reentering) when another is
    // already in flight -- always safe to skip, since every caller either retries automatically (the
    // timer, 60ms later) or simply leaves the preview exactly as correct as it already was until the next
    // real trigger.
    private static bool nativeRenderBusy;

    // Body/anim name lists are read from disk (an HQD text file, plus --
    // for body only -- an HQR entry count) and don't change during the
    // app's lifetime -- cached once per option kind across every
    // attributes window instance rather than re-read from disk each time
    // one opens.
    private static IReadOnlyList<FilterableComboBox.Option>? cachedBodyOptions;
    private static IReadOnlyList<FilterableComboBox.Option>? cachedAnimOptions;
    private static string? cachedBodyWarning;

    // Same filterable dropdown as every other picker in the app (the main window's Island/Scene boxes, LBA1's
    // own actor attributes window) instead of a second, separately-maintained copy of the same type-to-filter
    // behaviour -- see FilterableComboBox's own comment for the WPF quirks it already works around.
    private FilterableComboBox? bodyFilter, animFilter, entityFilter;

    // The Entity picker's own options (every entity in the game's FILE3D table, named after its first body's
    // own BODY2.HQD name when it has one) -- only shown/used for a brand-new actor (showingDummyBody), which has
    // no "kind of actor" yet; an existing actor's entity is already fixed by the file, so it isn't editable here.
    private IReadOnlyList<FilterableComboBox.Option> entityOptions = Array.Empty<FilterableComboBox.Option>();
    // Body's own option list, narrowed to the chosen entity's own bodies once one is picked for a new actor
    // (EntityChanged); the full cachedBodyOptions otherwise, exactly as before this feature existed.
    private IReadOnlyList<FilterableComboBox.Option> bodyOptionsForActor = Array.Empty<FilterableComboBox.Option>();
    // The entity chosen in EntityCombo for a brand-new actor -- null until one is picked, and irrelevant (never
    // read) once the actor is no longer a placeholder. Threaded through to Lba2ActorPersistence.Save so it can
    // add the actor to the scene file instead of refusing.
    private int? newActorEntity;

    // Per-window (not shared/cached like cachedAnimOptions): this actor's
    // own animation list with its native moveset -- see
    // BuildAnimOptionsForActor -- sorted to the top, ahead of a separator,
    // ahead of everything else. Falls back to cachedAnimOptions unchanged
    // (no separator) when the native lookup finds nothing, e.g. this
    // actor's scene isn't the one currently loaded.
    private IReadOnlyList<FilterableComboBox.Option> animOptionsForActor = Array.Empty<FilterableComboBox.Option>();

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

    // True while this actor is still a native placeholder (RendererLibrary.
    // IsActorPlaceholder -- added by RendererAddActor, cleared once Apply
    // actually sets real attributes) with no real body/anim chosen. Set once
    // from LoadCurrentValues() at construction and cleared the moment Apply
    // succeeds; RenderPreviewFrame renders Assets/DummyBody.lm2 in this
    // window's own preview panel instead of the usual native body-preview
    // render while it's true, so a brand new actor's dialog doesn't open on
    // a blank/failed preview before the user has picked a real body.
    private bool showingDummyBody;
    // SPRITE_3D actors (keys, coins, chests...) have a sprite instead of a body; -1 for real bodies.
    private int spriteId = -1;
    private BitmapSource? spritePreview;

    internal ActorAttributesWindow(CommunityRendererBackend nativeRenderer, byte[] palette, int actorIndex)
    {
        InitializeComponent();
        DebugLog.Log($"ActorAttributesWindow[{actorIndex}]: constructing");
        this.nativeRenderer = nativeRenderer;
        this.palette = palette;
        this.actorIndex = actorIndex;
        Title = $"Actor {actorIndex} Attributes";
        TitleLabel.Text = $"Actor {actorIndex}";

        cachedBodyOptions ??= LoadOptions("BODY2.HQD", "BODY.HQR");
        // GenAnim (like GenBody) indexes a small per-actor "generic" table
        // (SearchAnim(), FICHE.CPP), not ANIM.HQR's own much larger raw
        // animation-data archive directly -- passing no HQR file here means
        // LoadOptions sizes the list from ANIM2.HQD's own line count alone
        // (~87 entries) instead of inflating it to ANIM.HQR's 2000+.
        cachedAnimOptions ??= LoadOptions("ANIM2.HQD", null);
        animOptionsForActor = BuildAnimOptionsForActor(cachedAnimOptions);

        bodyOptionsForActor = cachedBodyOptions!;
        bodyFilter = new FilterableComboBox(BodyCombo, () => bodyOptionsForActor);
        animFilter = new FilterableComboBox(AnimCombo, () => animOptionsForActor);
        entityFilter = new FilterableComboBox(EntityCombo, () => entityOptions);
        bodyFilter.Committed += CommitPreviewChange;
        animFilter.Committed += CommitPreviewChange;
        entityFilter.Committed += CommitEntityChange;
        EntityCombo.LostFocus += (_, _) => CommitEntityChange();
        // A typed number (rather than a picked entry, which the Committed events above already cover)
        // commits when focus leaves the box -- not on every keystroke, which would recalibrate and re-render
        // against unparseable mid-typing text constantly.
        BodyCombo.LostFocus += (_, _) => { RevertIfInvalid(BodyCombo, cachedBodyOptions!, previewBody); CommitPreviewChange(); };
        AnimCombo.LostFocus += (_, _) => { RevertIfInvalid(AnimCombo, animOptionsForActor, previewAnim); CommitPreviewChange(); };
        // Re-fetches this actor's own native moveset every time the dropdown is actually opened, rather than
        // only once at construction -- the native lookup only succeeds while this actor's scene happens to be
        // the one currently loaded (see BuildAnimOptionsForActor's own comment), which frequently isn't true
        // yet here but may become true by the time the user actually opens this dropdown. Skipped once
        // SyncAnimationsToBody has set a body-specific list (animOptionsAreBodySpecific) -- otherwise, opening
        // the dropdown right after picking a different body silently threw that correct, body-scoped list away
        // and replaced it with the ORIGINAL actor's own native moveset (built from actorIndex's live, not-yet-
        // Applied body), which is what "changing the body doesn't correctly update the animation dropdown" was.
        AnimCombo.GotFocus += (_, _) => { if (!animOptionsAreBodySpecific) { animOptionsForActor = BuildAnimOptionsForActor(cachedAnimOptions!); animFilter!.Refresh(); } };
        bodyFilter.Refresh();
        animFilter.Refresh();
        entityFilter.Refresh();

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

        // Undoes RendererAddActor for a brand-new actor the user opened this
        // window for but never clicked Apply on -- RemoveActor itself is a
        // no-op (returns 0, harmlessly ignored here) for any actor that
        // isn't still a native placeholder, which covers every ordinary
        // "Edit Attributes..." open of an existing actor, so this is safe to
        // call unconditionally on every close rather than tracking "was this
        // window opened for a freshly-added actor" separately in C#.
        Closed += (_, _) => nativeRenderer.RendererLibrary?.RemoveActor(actorIndex);
        Closed += (_, _) => previewTimer?.Stop();
        // Explicit Normal priority, not the parameterless constructor's default (Background): confirmed this
        // round that a plain Background timer can go quiet for minutes at a time under heavy external UI
        // Automation traffic against this window (its own COM/RPC property queries appear to keep outrunning
        // Background-priority work indefinitely) -- reproduced with zero stall once automation activity
        // stopped entirely (40s of continuous ticking, passively observed), so this genuinely looks like
        // priority starvation rather than a stuck/corrupted state. Not Render (build-and-ui-testing's own
        // memory already documents Render starving key input elsewhere in this app) -- Normal is the least
        // risky step up that still meaningfully outranks whatever's crowding Background out.
        previewTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(60) };
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
    private static IReadOnlyList<FilterableComboBox.Option> LoadOptions(string hqdFileName, string? hqrFileName)
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

        var options = new List<FilterableComboBox.Option>(result.Names.Count);
        for (var i = 0; i < result.Names.Count; i++)
            options.Add(new FilterableComboBox.Option(i, result.Names[i] is { } name ? $"{i}: {name}" : $"{i}"));
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
    private IReadOnlyList<FilterableComboBox.Option> BuildAnimOptionsForActor(IReadOnlyList<FilterableComboBox.Option> all)
        => BuildAnimOptions(nativeRenderer.RendererLibrary?.GetActorNativeAnims(actorIndex) ?? Array.Empty<int>(), all, null);

    // The list for a set of "natural" animations (raw ANIM.HQR indices): those first, then a separator, then the other named animations (only those `otherFits`
    // accepts, when given).
    private static IReadOnlyList<FilterableComboBox.Option> BuildAnimOptions(IReadOnlyList<int> native, IReadOnlyList<FilterableComboBox.Option> all, Func<int, bool>? otherFits)
    {
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
        var natural = new List<FilterableComboBox.Option>(native.Count);
        foreach (var index in native)
        {
            if (!seen.Add(index)) continue; // a fiche can list the same generic anim more than once
            natural.Add(byIndex.TryGetValue(index, out var named) ? named : new FilterableComboBox.Option(index, index.ToString()));
        }
        if (natural.Count == 0) return all;
        var other = all.Where(o => !seen.Contains(o.Index) && (otherFits is null || otherFits(o.Index))).ToList();

        var merged = new List<FilterableComboBox.Option>(natural.Count + other.Count + 1);
        merged.AddRange(natural);
        if (other.Count > 0)
        {
            merged.Add(new FilterableComboBox.Option(-1, "--- Other compatible animations ---"));
            merged.AddRange(other);
        }
        return merged;
    }

    // ---- a body of its own kind of actor ------------------------------------------------------------------------------------------------------------
    // An animation moves the bones of the body it was made for: an entity (a kind of actor: RESS.HQR entry 44) lists its bodies and its animations, and where it has
    // bodies with different skeletons (bone counts) each has animations with as many groups. The list the actor was opened with is its own entity's; when another
    // body is picked the list has to become that body's entity's (and an animation that isn't one of them, which the preview can't play or plays wrongly, gives
    // way to that body's standing animation).

    private Lba2EntityTable? entityTable;
    private bool entityTableRead;
    private HqrArchive? bodyArchive, animArchive;
    private readonly Dictionary<int, int?> boneCounts = new(), groupCounts = new();
    private int? syncedBody;
    // True once SyncAnimationsToBody has set animOptionsForActor to a body-specific list -- see AnimCombo's
    // own GotFocus handler, which must not clobber it back to the actor's original native moveset.
    private bool animOptionsAreBodySpecific;
    // Bodies Body.Validate() itself rejects as exceeding the classic engine's own hard, UNCONDITIONAL point/
    // bone limits (Vertices.Count/Bones.Count -- both rotated/projected or matrix-computed for every point/
    // bone regardless of camera angle, so there's no way for the native renderer to ever safely fit more than
    // that). This is now a narrower set than it once was: the combined Faces+Lines+Spheres primitive-count
    // check Body.Validate() also has is STRICT-only (see its own comment) and no longer applies to a body read
    // this way (BodyBones/Read use strict:false) -- that count was never a hard per-body limit, only a proxy
    // for the native renderer's own PER-FRAME VISIBLE primitive cap, which the native side now enforces
    // directly and safely (AFF_OBJ.CPP's own Nb_Sort < MAX_NB_POLYS guard). BodyBones already caught and
    // logged the exception this HashSet tracks, but only for the caller's OWN "unknown bone count" fallback --
    // nothing stopped the native preview from being attempted anyway for a body that fails even the loosened
    // check, which would crash with an access violation (confirmed via a real Windows Event Log crash report,
    // for a since-fixed animation/body group-count mismatch -- see AnimFitsBody's own comment, EXTFUNC.CPP).
    // Recalibrate checks this set before making any native call and shows a clear fallback message instead.
    private readonly HashSet<int> unsafeToPreviewBodies = new();

    private static string GameDirectory => EditorSettings.Current.GameDirectory;

    private int? BodyBones(int body)
    {
        if (boneCounts.TryGetValue(body, out var known)) return known;
        int? count = null;
        try
        {
            bodyArchive ??= HqrArchive.Open(Path.Combine(GameDirectory, "BODY.HQR"));
            if (bodyArchive.IsValid(body)) count = LbaBodyStudio.Body.Read(bodyArchive.Read(body), 2).Bones.Count;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            DebugLog.Log($"ActorAttributesWindow: body {body}: {error.Message}");
            // InvalidDataException specifically means Body.Read got real data and Body.Validate() itself
            // rejected it (too big, not just "this archive slot is empty/unreadable") -- see
            // unsafeToPreviewBodies's own comment for why that's a hard "never attempt to render this" signal,
            // not just "bone count unknown for animation-list filtering purposes" like the other two.
            if (error is InvalidDataException) unsafeToPreviewBodies.Add(body);
        }
        boneCounts[body] = count;
        return count;
    }

    // How many groups (bones) an ANIM.HQR entry moves: the second U16 of the entry (what ObjectInitAnim reads).
    private int? AnimGroups(int anim)
    {
        if (groupCounts.TryGetValue(anim, out var known)) return known;
        int? count = null;
        try
        {
            animArchive ??= HqrArchive.Open(Path.Combine(GameDirectory, "ANIM.HQR"));
            if (animArchive.IsValid(anim) && animArchive.Read(anim) is { Length: >= 4 } bytes) count = BitConverter.ToUInt16(bytes, 2);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { DebugLog.Log($"ActorAttributesWindow: animation {anim}: {error.Message}"); }
        groupCounts[anim] = count;
        return count;
    }

    // Makes the animation list and the chosen animation agree with `body`; returns the animation to use (the current one when it belongs to the body).
    private int SyncAnimationsToBody(int body, int currentAnim)
    {
        if (body < 0) return currentAnim;
        if (!entityTableRead) { entityTable = Lba2EntityTable.Load(GameDirectory); entityTableRead = true; }
        var bones = BodyBones(body);
        // (a body that no entity has: the list stays as it is)
        if (entityTable?.ChooseAnimations(body, bones, AnimGroups, currentAnim) is not { } choice) return currentAnim;

        Func<int, bool>? otherFits = bones is { } b ? anim => AnimGroups(anim) == b : null;
        var list = BuildAnimOptions(choice.Natural, cachedAnimOptions!, otherFits);
        animOptionsForActor = list;
        animOptionsAreBodySpecific = true;
        animFilter!.Refresh();
        AnimCombo.Text = list.FirstOrDefault(o => o.Index == choice.Chosen)?.Display ?? choice.Chosen.ToString();
        StatusLabel.Text = choice.Chosen == currentAnim
            ? $"Animation list: the animations of this body's kind of actor ({choice.Natural.Count})."
            : $"Animation {currentAnim} doesn't belong to this body; using {choice.Chosen}. The list is now this body's kind of actor's animations ({choice.Natural.Count}).";
        return choice.Chosen;
    }

    // ---- a kind of actor, for a brand-new one (Add Actor Here) --------------------------------------------------------------------------------------
    // A brand-new actor has no entity yet: the file needs one to be given a body/animation at all (they're small ids relative to an entity, not raw
    // archive indices -- see Lba2ActorPersistence's own comment), so EntityPanel/EntityCombo (shown only while showingDummyBody) let the user pick one
    // before Apply can save it. Picking an entity narrows BodyCombo to that entity's own bodies and re-syncs the animation the same way changing an
    // existing actor's body already does (SyncAnimationsToBody), so Apply always sees a body/anim pair the chosen entity actually offers.

    // Every entity in the game's FILE3D table, named after its first body's own BODY2.HQD name when it has one (there is no per-entity name list to
    // read instead -- unlike BODY2.HQD/ANIM2.HQD, LBAPackageManager ships no LBA2 entity descriptions).
    private static IReadOnlyList<FilterableComboBox.Option> BuildEntityOptions(Lba2EntityTable? table, IReadOnlyList<FilterableComboBox.Option> bodyOptions)
    {
        if (table is null) return Array.Empty<FilterableComboBox.Option>();
        var bodyNames = bodyOptions.ToDictionary(o => o.Index, o => o.Display);
        static string NameOnly(string display) { var colon = display.IndexOf(':'); return colon >= 0 ? display[(colon + 2)..] : display; }
        return table.Entities.Select(e =>
        {
            var name = e.Bodies.Count > 0 && bodyNames.TryGetValue(e.Bodies[0].Body, out var display) ? NameOnly(display) : null;
            return new FilterableComboBox.Option(e.Id, name is not null ? $"{e.Id}: {name}" : $"{e.Id}");
        }).ToList();
    }

    private void CommitEntityChange()
    {
        if (int.TryParse(ParseLeadingIndex(EntityCombo.Text), out var id)) EntityChanged(id);
    }

    private void EntityChanged(int entityId)
    {
        if (!entityTableRead) { entityTable = Lba2EntityTable.Load(GameDirectory); entityTableRead = true; }
        var entity = entityTable?.Entities.FirstOrDefault(e => e.Id == entityId);
        newActorEntity = entityId;
        if (entity is null)
        {
            StatusLabel.Text = $"Entity {entityId} isn't in this game's kind-of-actor table.";
            return;
        }
        var bodyIds = entity.Bodies.Select(b => b.Body).Distinct().ToList();
        bodyOptionsForActor = bodyIds.Count > 0 ? cachedBodyOptions!.Where(o => bodyIds.Contains(o.Index)).ToList() : cachedBodyOptions!;
        bodyFilter!.Refresh();
        var defaultBody = entity.Bodies.Count > 0 ? entity.Bodies.OrderBy(b => b.Generic).First().Body : 0;
        BodyCombo.Text = bodyOptionsForActor.FirstOrDefault(o => o.Index == defaultBody)?.Display ?? cachedBodyOptions!.FirstOrDefault(o => o.Index == defaultBody)?.Display ?? defaultBody.ToString();
        CommitPreviewChange();
        StatusLabel.Text = $"New actor: Apply adds it to this scene as entity {entityId}; Close discards it.";
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
        showingDummyBody = library.IsActorPlaceholder(actorIndex);
        spriteId = library.GetActorSprite(actorIndex);

        PositionXBox.Text = x.ToString();
        PositionYBox.Text = y.ToString();
        PositionZBox.Text = z.ToString();
        BetaBox.Text = beta.ToString();
        syncedBody = body;
        BodyCombo.Text = cachedBodyOptions!.FirstOrDefault(o => o.Index == body)?.Display ?? body.ToString();
        AnimCombo.Text = animOptionsForActor.FirstOrDefault(o => o.Index == anim)?.Display ?? anim.ToString();
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

        // What Apply compares against to work out which fields actually changed (see Lba2ActorPersistence.Save's
        // own comment on why only changed fields are written back to the file).
        savedSnapshot = new Lba2ActorPersistence.Snapshot(x, y, z, beta, body, anim, lifePoint, armor, hitForce, move, flags);

        // A brand-new actor needs a "kind of actor" chosen before it can be saved to the game files at all (see
        // EntityChanged's own comment) -- shown only here, not for an actor the file already has one for.
        if (showingDummyBody)
        {
            if (!entityTableRead) { entityTable = Lba2EntityTable.Load(GameDirectory); entityTableRead = true; }
            entityOptions = BuildEntityOptions(entityTable, cachedBodyOptions!);
            entityFilter!.Refresh();
            EntityPanel.Visibility = Visibility.Visible;
            if (entityTable?.Entities.FirstOrDefault() is { } defaultEntity)
            {
                EntityCombo.Text = entityOptions.FirstOrDefault(o => o.Index == defaultEntity.Id)?.Display ?? defaultEntity.Id.ToString();
                EntityChanged(defaultEntity.Id);
            }
            else
            {
                EntityCombo.Text = "no kind-of-actor data for this game -- can't be saved to the game files yet";
                newActorEntity = null;
            }
        }
        else
        {
            EntityPanel.Visibility = Visibility.Collapsed;
            bodyOptionsForActor = cachedBodyOptions!;
            newActorEntity = null;
        }
    }

    // What was last either loaded from, or successfully saved to, the scene file -- null until the first
    // successful load (LoadCurrentValues failing early, above, leaves it null; Apply then knows there is nothing
    // to safely compare against and skips trying to persist at all rather than guessing every field "changed").
    private Lba2ActorPersistence.Snapshot? savedSnapshot;

    // On losing focus, text that doesn't parse to a whole number at all reverts to whatever is already
    // committed (CommitPreviewChange itself only ever silently no-ops on unparseable text; it never reverts
    // the display). A number that isn't one of the dropdown's own named entries is left as typed -- Apply
    // accepts any whole number for body/animation regardless of whether the picker happens to have a name
    // for it, so the box does too.
    private static void RevertIfInvalid(ComboBox combo, IReadOnlyList<FilterableComboBox.Option> options, int committedValue)
    {
        if (int.TryParse(ParseLeadingIndex(combo.Text), out _)) return;
        combo.Text = options.FirstOrDefault(o => o.Index == committedValue)?.Display ?? committedValue.ToString();
    }

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
        // a different body: its own kind of actor's animations, and an animation that belongs to it
        if (syncedBody is { } before && body != before) anim = SyncAnimationsToBody(body, anim);
        syncedBody = body;
        // A brand-new actor (Add Actor Here) starts with showingDummyBody true (see RenderPreviewFrame's own
        // gate) so its preview shows a generic placeholder before it has any real body at all -- but the
        // moment the user picks any real, valid body index here (typing one, choosing from the dropdown, or
        // via LoadDebugBody_Click, which already relied on this same reasoning before this general form of
        // it existed), that's exactly as real a body choice as a successful Apply, and the placeholder should
        // stop showing immediately rather than only once Apply is clicked -- otherwise every such pick before
        // the first Apply silently kept rendering the dummy instead of the body actually selected.
        if (body >= 0) showingDummyBody = false;
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
        // No body (-1): nothing native to calibrate against; the dummy body / sprite is drawn instead.
        if (previewBody < 0)
        {
            previewCalibration = null;
            RenderPreviewFrame();
            return;
        }
        // A body Body.Validate() itself rejects (too big for the classic engine's own fixed-size rendering
        // buffers, see unsafeToPreviewBodies's own comment) crashes the native renderer outright if attempted
        // -- BodyBones already ran (via SyncAnimationsToBody, CommitPreviewChange) for any ordinary body pick,
        // but call it again here too (cheap: boneCounts/unsafeToPreviewBodies both cache) so this check always
        // runs regardless of which path set previewBody, not just the common one.
        BodyBones(previewBody);
        if (unsafeToPreviewBodies.Contains(previewBody))
        {
            previewCalibration = null;
            ShowPreviewFallback("this body is too large for the classic engine to preview (exceeds its point/primitive limit)");
            return;
        }
        if (nativeRenderBusy) return; // see nativeRenderBusy's own comment -- another native preview call is already in flight
        nativeRenderBusy = true;
        try
        {
            previewCalibration = nativeRenderer.CalibrateBodyPreviewDistance(previewBody, previewAnim, EffectivePalette, targetAspect: GetPreviewAspect())
                ?? new CommunityRendererBackend.BodyPreviewCalibration(5000, new Int32Rect(0, 0, 640, 480));
        }
        finally { nativeRenderBusy = false; }
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
        if (nativeRenderBusy) return; // see nativeRenderBusy's own comment -- another native preview call is already in flight
        nativeRenderBusy = true;
        try
        {
            // A sprite actor with no body: show its sprite.
            if (previewBody < 0 && spriteId >= 0)
            {
                spritePreview ??= nativeRenderer.RenderSpritePreview(spriteId, palette);
                if (spritePreview is not null)
                {
                    RenderOptions.SetBitmapScalingMode(BodyPreviewImage, BitmapScalingMode.NearestNeighbor);
                    BodyPreviewImage.Source = spritePreview;
                    BodyPreviewFallbackLabel.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            RenderOptions.SetBitmapScalingMode(BodyPreviewImage, BitmapScalingMode.Unspecified);

            // No body at all (index -1): the dummy body stands in, same as for a brand-new actor.
            if (showingDummyBody || previewBody < 0)
            {
                var yaw = previewAngle * (float)(Math.PI * 2 / 4096); // engine's angle unit, see TickPreview's own comment
                var dummy = DummyBodyPreview.Render((int)PreviewBorder.ActualWidth, (int)PreviewBorder.ActualHeight, yaw);
                if (dummy is null) { ShowPreviewFallback(previewBody < 0 ? "this actor has no body" : "no body chosen yet for this new actor"); return; }
                BodyPreviewImage.Source = dummy;
                BodyPreviewFallbackLabel.Visibility = Visibility.Collapsed;
                return;
            }

            // See Recalibrate's own matching check -- previewCalibration stays null for one of these bodies
            // (Recalibrate returns before setting it), so without this check every subsequent 60ms timer tick
            // would overwrite Recalibrate's own specific message with the generic "enter a body index" one
            // below, right after showing it once.
            if (unsafeToPreviewBodies.Contains(previewBody))
            {
                ShowPreviewFallback("this body is too large for the classic engine to preview (exceeds its point/primitive limit)");
                return;
            }

            if (previewCalibration is not { } calibration)
            {
                ShowPreviewFallback("enter a body index to preview");
                return;
            }
            nativeRenderer.RendererLibrary?.SetBodyPreviewAnimationPaused(pauseAnimation);
            var bitmap = nativeRenderer.RenderBodyPreview(previewBody, previewAnim, previewAngle, calibration.Distance, EffectivePalette);
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
        finally { nativeRenderBusy = false; }
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

    // Explore mode looks but doesn't change: the fields stay readable, the preview still plays, and Apply is off.
    public void MakeViewOnly()
    {
        ApplyButton.IsEnabled = false;
        ApplyButton.ToolTip = "Explore mode only looks; switch to Build mode to change the actor.";
        Title += " (view only)";
    }
    // Ctrl+S applies, the same as every other window's own "save" shortcut (ActorScriptWindow, the scene
    // editors, ...) -- except when a text box has focus, so it does not fight that box's own text-editing
    // undo/selection or, for BodyCombo/AnimCombo, its live filtering.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Key != Key.S || !ApplyButton.IsEnabled) return;
        if (Keyboard.FocusedElement is TextBoxBase) return;
        Apply_Click(sender, e);
        e.Handled = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        using var busy = UiBusy.Cursor();
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
        var newFlags = 0u;
        if (library.GetActorFlags(actorIndex, out var currentFlags))
        {
            var knownMask = flagItems.Aggregate(0u, (mask, item) => mask | item.Bit);
            newFlags = (currentFlags & ~knownMask) | flagItems.Where(i => i.IsChecked).Aggregate(0u, (mask, item) => mask | item.Bit);
            okFlags = library.SetActorFlags(actorIndex, newFlags);
        }

        if (!okAttrs || !okPos || !okFlags)
        {
            StatusLabel.Text = "Failed to apply -- this actor may no longer be valid (e.g. after switching islands).";
        }
        else
        {
            showingDummyBody = false;
            RenderPreviewFrame();
            StatusLabel.Text = TrySaveToFile(x, y, z, beta, body, anim, lifePoint, armor, hitForce, move, newFlags);
        }
    }

    // Writes the edit into the actor's own scene record too, so it survives closing the app -- not just the
    // live session SetActorAttributes/SetActorPosition/SetActorFlags above already updated. Returns the status
    // line to show (never throws: every failure here is reported, not fatal, since the session-only apply above
    // already succeeded regardless of whether this does).
    private string TrySaveToFile(int x, int y, int z, int beta, int body, int anim, int lifePoint, int armor, int hitForce, int move, uint flags)
    {
        var library = nativeRenderer.RendererLibrary;
        if (savedSnapshot is not { } original || library is null) return "Applied for this session. Not yet saved to the game's files.";
        if (Lba2ActorPersistence.Locate(library, actorIndex) is not { } located)
            return "Applied for this session, but couldn't be saved to the game files (this actor's scene isn't the one currently loaded right now).";

        var updated = new Lba2ActorPersistence.Snapshot(x, y, z, beta, body, anim, lifePoint, armor, hitForce, move, flags);
        var error = Lba2ActorPersistence.Save(GameDirectory, located.Scene, located.IndexInScene, original, updated, out var bodyAnimNote, newActorEntity);
        if (error is not null) return $"Applied for this session, but not saved to the game files: {error}";

        // The actor is now in the scene file: it isn't "new" any more, so the Entity picker (only ever offered
        // for one that wasn't yet) has nothing further to do, and a later Apply falls into the ordinary
        // existing-actor path above on its own (indexInScene now points at a real Actors[] entry).
        if (newActorEntity is not null) { newActorEntity = null; EntityPanel.Visibility = Visibility.Collapsed; }

        savedSnapshot = updated with
        {
            // The two fields Save may have refused (bodyAnimNote set): keep the snapshot at what the file still
            // actually has, so a later Apply that leaves the box exactly as it is now doesn't count as "unchanged"
            // and skip retrying it -- e.g. once the entity table gains that body some other way.
            Body = bodyAnimNote?.Contains("body", StringComparison.Ordinal) == true ? original.Body : updated.Body,
            Anim = bodyAnimNote?.Contains("animation", StringComparison.Ordinal) == true ? original.Anim : updated.Anim,
        };
        return bodyAnimNote is null ? "Saved to the game's files." : $"Saved to the game's files, except: {bodyAnimNote} (stays session-only).";
    }

    private void EditScript_Click(object sender, RoutedEventArgs e) => OpenScriptRequested?.Invoke(actorIndex);

    private void CreateNewBody_Click(object sender, RoutedEventArgs e) => BodyStudioLauncher.Show(this);

    // A development checkout only: walks up from the running exe looking for the project's own
    // BodyStudio/TestBodies folder, the same way Lba2Engine.Find() locates the native engine build.
    private static string? FindTestArchive(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "BodyStudio", "TestBodies", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // Debug-only: preview a body from a project test archive (mario.hqr and friends) through the real native
    // renderer, including animation, without ever touching the user's actual game files -- see
    // MainWindow.BodyDebugPreview.cs's own comment for how the throwaway preview copy works.
    private void LoadDebugBody_Click(object sender, RoutedEventArgs e)
    {
        var path = FindTestArchive("mario.hqr");
        if (path is null) { StatusLabel.Text = "mario.hqr not found -- this debug feature only works from a development checkout."; return; }
        LbaBodyStudio.Hqr archive;
        try { archive = new LbaBodyStudio.Hqr(path); }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            DebugLog.Log($"ActorAttributesWindow: couldn't open {path}: {error.Message}");
            StatusLabel.Text = $"Couldn't open {Path.GetFileName(path)}: {error.Message}";
            return;
        }
        var items = new List<(int Id, string Label)>();
        for (var i = 0; i < archive.Count; i++)
        {
            try { items.Add((i, $"{i}: {archive.Read(i).Length} bytes")); }
            catch (InvalidDataException) { /* an empty/unused slot */ }
        }
        if (items.Count == 0) { StatusLabel.Text = $"{Path.GetFileName(path)} has no readable entries."; return; }
        if (ListPickWindow.Pick(this, "Load debug body", items,
                note: $"From {Path.GetFileName(path)}, installed into a throwaway preview copy of BODY.HQR. Your real game files are never modified.") is not { } chosen) return;

        byte[] entry;
        try { entry = archive.Read(chosen); }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException)
        {
            DebugLog.Log($"ActorAttributesWindow: couldn't read {path} entry {chosen}: {error.Message}");
            StatusLabel.Text = $"Couldn't read entry {chosen}: {error.Message}";
            return;
        }
        if (LoadDebugBodyRequested?.Invoke(entry) is not { } newIndex)
        {
            StatusLabel.Text = "Couldn't start the debug body preview -- a terrain edit may already be using the live 3D view.";
            return;
        }
        debugBodyIndex = newIndex;
        debugBodyPalette ??= LoadDebugBodyPalette();
        // A brand-new actor (Add Actor Here) starts as a placeholder with no
        // real body -- RenderPreviewFrame() shows DummyBodyPreview instead of
        // ever calling into the real native renderer while this stays true
        // (only Apply's own success path clears it, for the "the user saved
        // a real body" case). Loading a debug body is exactly as real a body
        // choice as that, so it needs to clear this too -- without it, every
        // debug-body preview silently rendered the generic placeholder the
        // whole time, never the body actually being tested (confirmed via
        // RenderPreviewFrame's own call log: previewBody was already 468,
        // showingDummyBody was still true, and CommunityRendererBackend.
        // RenderBodyPreview -- the only path that would show 468's real
        // pixels -- was never once called).
        showingDummyBody = false;
        BodyCombo.Text = bodyOptionsForActor.FirstOrDefault(o => o.Index == newIndex)?.Display ?? newIndex.ToString();
        CommitPreviewChange();
        // CommitPreviewChange's own SyncAnimationsToBody just picked an animation for whichever real,
        // on-disk entity actually owns index `newIndex` in the retail archive -- it has no idea that
        // index now holds a swapped-in debug body instead, so its choice generally belongs to a
        // different skeleton than the debug body's and renders garbled. Rather than fall back to
        // Twinsen's own archive animation 0 (compatible by bone count, but not really THIS body's own
        // animation), generate a real standing/idle clip for this body's own bone count and load it the
        // same way "Load Debug Anim..." would -- every custom body always needs *some* default
        // animation to preview with, so generating one here rather than leaving it on a borrowed
        // Twinsen clip is the same reasoning AnimGenerator/Anim.Write already exist for (see
        // BodyStudio/AnimGenerator.cs). mario.hqr's own bodies are always authored for game 2 (see
        // BodyPipeline's hqrbody/*custom commands), so that's what both the body parse and the
        // generated clip's own angle-unit scale use here.
        var idleAnimIndex = (int?)null;
        try
        {
            var body = LbaBodyStudio.Body.Read(entry, 2);
            var idleBytes = LbaBodyStudio.AnimGenerator.Idle(2, body.Bones.Count).Write();
            idleAnimIndex = LoadDebugAnimRequested?.Invoke(idleBytes);
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            DebugLog.Log($"ActorAttributesWindow: couldn't generate a default standing animation for the debug body: {error.Message}");
        }
        if (idleAnimIndex is { } newAnimIndex)
        {
            AnimCombo.Text = newAnimIndex.ToString();
            CommitPreviewChange();
            StatusLabel.Text = $"Loaded {Path.GetFileName(path)} entry {chosen} as body {newIndex}, with a generated standing animation (preview only, not saved).";
        }
        else
        {
            // Twinsen's own animation 0 -- already proven safe by every brand-new actor defaulting to
            // it, and compatible by bone count with any Body Studio "New humanoid" body -- as a
            // fallback if generating/installing the standing clip above didn't work out.
            AnimCombo.Text = "0";
            CommitPreviewChange();
            StatusLabel.Text = $"Loaded {Path.GetFileName(path)} entry {chosen} as body {newIndex} (preview only, not saved).";
        }
    }

    // Same idea as LoadDebugBody_Click, for a project test animation archive (e.g. testanims.hqr,
    // AnimGenerator's own walk/run/idle/jump output) instead of a body -- swapped into a throwaway
    // preview copy of ANIM.HQR. Requires a debug body already loaded (LoadDebugAnimRequested's own
    // handler, MainWindow.BodyDebugPreview.cs's LoadDebugAnim, shares that body's live-preview
    // folder rather than starting a second one).
    private void LoadDebugAnim_Click(object sender, RoutedEventArgs e)
    {
        var path = FindTestArchive("testanims.hqr");
        if (path is null) { StatusLabel.Text = "testanims.hqr not found -- this debug feature only works from a development checkout."; return; }
        LbaBodyStudio.Hqr archive;
        try { archive = new LbaBodyStudio.Hqr(path); }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            DebugLog.Log($"ActorAttributesWindow: couldn't open {path}: {error.Message}");
            StatusLabel.Text = $"Couldn't open {Path.GetFileName(path)}: {error.Message}";
            return;
        }
        var items = new List<(int Id, string Label)>();
        for (var i = 0; i < archive.Count; i++)
        {
            try { items.Add((i, $"{i}: {archive.Read(i).Length} bytes")); }
            catch (InvalidDataException) { /* an empty/unused slot */ }
        }
        if (items.Count == 0) { StatusLabel.Text = $"{Path.GetFileName(path)} has no readable entries."; return; }
        if (ListPickWindow.Pick(this, "Load debug anim", items,
                note: $"From {Path.GetFileName(path)}, installed into a throwaway preview copy of ANIM.HQR. Your real game files are never modified.") is not { } chosen) return;

        byte[] entry;
        try { entry = archive.Read(chosen); }
        catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException)
        {
            DebugLog.Log($"ActorAttributesWindow: couldn't read {path} entry {chosen}: {error.Message}");
            StatusLabel.Text = $"Couldn't read entry {chosen}: {error.Message}";
            return;
        }
        if (LoadDebugAnimRequested?.Invoke(entry) is not { } newIndex)
        {
            StatusLabel.Text = "Couldn't start the debug anim preview -- load a debug body first, or a terrain edit may already be using the live 3D view.";
            return;
        }
        AnimCombo.Text = newIndex.ToString();
        CommitPreviewChange();
        StatusLabel.Text = $"Loaded {Path.GetFileName(path)} entry {chosen} as anim {newIndex} (preview only, not saved).";
    }

    // RESS.HQR entry 0: a plain 256-colour RGB table, not one of the structured per-island XPL records
    // MainWindow's own LoadPaletteEntry parses -- exactly what Generation.Palette (BodyStudio/Generation.cs)
    // reads when generating or previewing a body, so this is the one palette every Body Studio body's own
    // ramp-start colour indices are actually correct under. See EffectivePalette's own comment for why this
    // needs to be a different table from the live scene's island palette at all.
    private byte[]? LoadDebugBodyPalette()
    {
        try
        {
            var bytes = HqrArchive.Open(Path.Combine(GameDirectory, "RESS.HQR")).Read(0);
            return bytes.Length == 768 ? bytes : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            DebugLog.Log($"ActorAttributesWindow: couldn't load the debug body's own palette: {e.Message}");
            return null;
        }
    }

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
