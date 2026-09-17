using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LBA2LevelEditor;

// Non-modal window for editing one actor's attributes -- position, facing,
// body/animation (with a name-lookup dropdown), collision/physics flags,
// and combat stats -- plus a rotating live preview of the selected body and
// a jumping-off point to that actor's script editor. Edits apply
// immediately (SetActorAttributes/SetActorPosition/SetActorFlags are
// session-only: they update the live renderer and survive panning, per
// RENDERER_API.H's own doc comments on those calls, but aren't written to
// the game's SCENE.HQR yet -- that's a separate, not-yet-built save path).
// MainWindow opens one of these per actor, each its own taskbar entry, so
// several can be open side by side.
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

    // Body/anim name lists are read from disk (an HQD text file plus an HQR
    // entry count) and don't change during the app's lifetime -- cached once
    // per option kind across every attributes window instance rather than
    // re-read from disk each time one opens.
    private static IReadOnlyList<NamedOption>? cachedBodyOptions;
    private static IReadOnlyList<NamedOption>? cachedAnimOptions;
    private static string? cachedBodyWarning;

    internal ActorAttributesWindow(CommunityRendererBackend nativeRenderer, byte[] palette, int actorIndex)
    {
        InitializeComponent();
        this.nativeRenderer = nativeRenderer;
        this.palette = palette;
        this.actorIndex = actorIndex;
        Title = $"Actor {actorIndex} Attributes";
        TitleLabel.Text = $"Actor {actorIndex}";

        BodyCombo.ItemsSource = cachedBodyOptions ??= LoadOptions("BODY.HQR", "BODY2.HQD");
        AnimCombo.ItemsSource = cachedAnimOptions ??= LoadOptions("ANIM.HQR", "ANIM2.HQD");

        LoadCurrentValues();
        if (cachedBodyWarning is not null) StatusLabel.Text = cachedBodyWarning;

        Closed += (_, _) => previewTimer?.Stop();
        previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        previewTimer.Tick += (_, _) => TickPreview();
        previewTimer.Start();
    }

    // BODY2.HQD/ANIM2.HQD -- this editor only ever works with LBA2 data (the
    // "1" variants describe LBA1's), see HqdDescriptions's own comment.
    private static IReadOnlyList<NamedOption> LoadOptions(string hqrFileName, string hqdFileName)
    {
        var hqrCount = 0;
        try
        {
            var hqrPath = Path.Combine(EditorSettings.Current.GameDirectory, hqrFileName);
            hqrCount = HqrArchive.Open(hqrPath).ValidIndices.Count();
        }
        catch
        {
            // No game directory / unreadable HQR yet -- names list still
            // works, just without the cross-validated count as a floor.
        }

        var result = HqdDescriptions.Load(hqdFileName, hqrCount);
        // ANIM2.HQD deliberately does not line up with ANIM.HQR's raw entry
        // count -- GenAnim (like GenBody) is an index into a small per-actor
        // "generic" table (SearchAnim(), FICHE.CPP), not a direct index into
        // the much larger raw animation-data archive, so this mismatch is
        // expected and not surfaced as a warning the way BODY2.HQD's (which
        // does roughly track BODY.HQR) is.
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
        BodyCombo.Text = body.ToString();
        AnimCombo.Text = anim.ToString();
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

    private void TickPreview()
    {
        if (!int.TryParse(ParseLeadingIndex(BodyCombo.Text), out var body))
        {
            BodyPreviewImage.Source = null;
            BodyPreviewFallbackLabel.Visibility = Visibility.Visible;
            BodyPreviewFallbackLabel.Text = "enter a body index to preview";
            return;
        }
        var anim = int.TryParse(ParseLeadingIndex(AnimCombo.Text), out var parsedAnim) ? parsedAnim : 0;
        previewAngle = (previewAngle + 24) % 4096; // engine's angle unit is 4096 per full turn (COMMON.H's MAX_ANGLE)

        var bitmap = nativeRenderer.RenderBodyPreview(body, anim, previewAngle, palette);
        if (bitmap is null)
        {
            BodyPreviewImage.Source = null;
            BodyPreviewFallbackLabel.Visibility = Visibility.Visible;
            BodyPreviewFallbackLabel.Text = "no body to preview for this index";
        }
        else
        {
            BodyPreviewImage.Source = bitmap;
            BodyPreviewFallbackLabel.Visibility = Visibility.Collapsed;
        }
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
