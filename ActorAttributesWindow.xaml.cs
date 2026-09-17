using System.Windows;
using System.Windows.Controls;

namespace LBA2LevelEditor;

// Non-modal window for editing one actor's attributes -- position, facing,
// body/animation, and combat stats -- plus a jumping-off point to that
// actor's script editor. Edits apply immediately (SetActorAttributes/
// SetActorPosition are session-only: they update the live renderer and
// survive panning, per RENDERER_API.H's own doc comment on those calls, but
// aren't written to the game's SCENE.HQR yet -- that's a separate,
// not-yet-built save path). MainWindow opens one of these per actor, each
// its own taskbar entry, so several can be open side by side.
public partial class ActorAttributesWindow : Window
{
    public event Action<int>? OpenScriptRequested;

    private readonly CommunityRendererBackend nativeRenderer;
    private readonly int actorIndex;

    internal ActorAttributesWindow(CommunityRendererBackend nativeRenderer, int actorIndex)
    {
        InitializeComponent();
        this.nativeRenderer = nativeRenderer;
        this.actorIndex = actorIndex;
        Title = $"Actor {actorIndex} Attributes";
        TitleLabel.Text = $"Actor {actorIndex}";
        LoadCurrentValues();
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

        PositionXBox.Text = x.ToString();
        PositionYBox.Text = y.ToString();
        PositionZBox.Text = z.ToString();
        BetaBox.Text = beta.ToString();
        BodyBox.Text = body.ToString();
        AnimBox.Text = anim.ToString();
        LifePointBox.Text = lifePoint.ToString();
        ArmorBox.Text = armor.ToString();
        HitForceBox.Text = hitForce.ToString();
        MoveBox.Text = move.ToString();
        WaypointsLabel.Text = $"{waypointCount} waypoint{(waypointCount == 1 ? "" : "s")} on this actor's track script";
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // No body-name table exists anywhere in this project yet (see
        // Lba2ScriptOpcodes's own scope notes) -- numeric index is all
        // there is to show until one gets built.
        BodyHintLabel.Text = int.TryParse(BodyBox.Text, out var n) ? $"body #{n}" : "";
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

        if (!TryParseAll(PositionXBox.Text, PositionYBox.Text, PositionZBox.Text, BetaBox.Text, BodyBox.Text, AnimBox.Text,
                LifePointBox.Text, ArmorBox.Text, HitForceBox.Text, MoveBox.Text,
                out var x, out var y, out var z, out var beta, out var body, out var anim,
                out var lifePoint, out var armor, out var hitForce, out var move))
        {
            StatusLabel.Text = "One or more fields aren't valid whole numbers.";
            return;
        }

        var okAttrs = library.SetActorAttributes(actorIndex, beta, body, anim, lifePoint, armor, hitForce, move);
        var okPos = library.SetActorPosition(actorIndex, x, y, z);
        StatusLabel.Text = okAttrs && okPos
            ? "Applied for this session. Not yet saved to the game's files."
            : "Failed to apply -- this actor may no longer be valid (e.g. after switching islands).";
    }

    private void EditScript_Click(object sender, RoutedEventArgs e) => OpenScriptRequested?.Invoke(actorIndex);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
