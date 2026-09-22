using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LBAAssembler.Lba1;

namespace LBAAssembler;

// The LBA1 counterpart of ActorAttributesWindow: position, facing, entity / body / animation (with names),
// life, armour, hit force, move type and flags of one scene actor, a rotating preview of its body, and a way
// into the script editor. LBA1 has no live renderer to hold session edits, so Apply writes the actor's
// header in SCENE.HQR (the first save keeps SCENE.HQR.bak), like the zone inspector does.
public partial class Lba1ActorAttributesWindow : Window
{
    private sealed class FlagCheckItem
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public uint Bit { get; init; }
        public bool IsChecked { get; set; }
    }

    private readonly Lba1Game game;
    private readonly Lba1ActorImages images;
    private readonly int scene;
    private readonly int actorIndex;
    private readonly Func<Lba1ActorData, string?> apply;
    private readonly Action openScript;
    private readonly Lba1ActorData data;
    private readonly List<FlagCheckItem> flagItems = new();
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly DispatcherTimer resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private FilterableComboBox? entityFilter, bodyFilter, animFilter;
    private IReadOnlyList<FilterableComboBox.Option> entityOptions = Array.Empty<FilterableComboBox.Option>();
    private IReadOnlyList<FilterableComboBox.Option> bodyOptions = Array.Empty<FilterableComboBox.Option>();
    private IReadOnlyList<FilterableComboBox.Option> animOptions = Array.Empty<FilterableComboBox.Option>();
    private int entity;
    private int bodyId;
    private int animId;
    private int previewAngle;
    private int rotationSpeed = 24;
    private bool isDraggingPreview;
    private double dragLastX;
    private bool loading;

    // scene / actorIndex locate the actor; apply saves the edited attributes (returning an error message,
    // or null on success); openScript opens the script editor for this actor.
    internal Lba1ActorAttributesWindow(Lba1Game game, Lba1ActorImages images, int scene, int actorIndex, string sceneTitle,
        Func<Lba1ActorData, string?> apply, Action openScript)
    {
        InitializeComponent();
        this.game = game;
        this.images = images;
        this.scene = scene;
        this.actorIndex = actorIndex;
        this.apply = apply;
        this.openScript = openScript;

        Title = actorIndex == 0 ? $"Twinsen - scene {scene}" : $"Actor {actorIndex} Attributes";
        TitleLabel.Text = (actorIndex == 0 ? "Twinsen (start position)" : $"Actor {actorIndex}") + $"   ·   {sceneTitle}";

        data = Lba1ActorRecord.Read(SceneZones.ReadRecord(1, scene), actorIndex);
        entity = data.Entity;
        bodyId = data.Body;
        animId = data.Anim;

        PositionXBox.Text = data.X.ToString();
        PositionYBox.Text = data.Y.ToString();
        PositionZBox.Text = data.Z.ToString();
        BetaBox.Text = data.Angle.ToString();
        LifePointBox.Text = data.LifePoints.ToString();
        ArmourBox.Text = data.Armor.ToString();
        HitForceBox.Text = data.HitForce.ToString();
        MoveBox.Text = data.Move.ToString();

        BuildOptions();
        entityFilter = new FilterableComboBox(EntityCombo, () => entityOptions);
        bodyFilter = new FilterableComboBox(BodyCombo, () => bodyOptions);
        animFilter = new FilterableComboBox(AnimCombo, () => animOptions);
        entityFilter.Committed += EntityCommitted;
        bodyFilter.Committed += () => { bodyId = ParseIndex(BodyCombo.Text, bodyId); BodyChanged(); };
        animFilter.Committed += () => { animId = ParseIndex(AnimCombo.Text, animId); RefreshPreviewTarget(); };
        foreach (var (combo, ident) in new[] { (EntityCombo, 0), (BodyCombo, 1), (AnimCombo, 2) })
            combo.LostFocus += (_, _) => CommitTyped(ident);

        loading = true;
        entityFilter.Refresh(); bodyFilter.Refresh(); animFilter.Refresh();
        SetComboText();
        loading = false;

        flagItems.AddRange(ActorFlags.All.Where(f => f.Bit <= 0x8000).Select(f => new FlagCheckItem
        {
            Name = f.Name,
            Description = f.Description,
            Bit = f.Bit,
            IsChecked = (data.Flags & f.Bit) != 0,
        }));
        var half = (flagItems.Count + 1) / 2;
        FlagsListLeft.ItemsSource = flagItems.Take(half).ToList();
        FlagsListRight.ItemsSource = flagItems.Skip(half).ToList();

        var track = Lba1TrackScript.Points(game.LoadScene(scene).Actors.ElementAtOrDefault(actorIndex)?.TrackScript ?? Array.Empty<byte>());
        WaypointsLabel.Text = actorIndex == 0 || track.Count == 0 ? "" : $"{track.Count} waypoint{(track.Count == 1 ? "" : "s")} on this actor's track script";

        ApplyActorKindLimits();

        // A brand new preview target, drawn as soon as the layout knows the panel's size.
        Loaded += (_, _) => { RefreshPreviewTarget(); previewTimer.Start(); };
        Closed += (_, _) => { previewTimer.Stop(); resizeTimer.Stop(); };
        previewTimer.Tick += (_, _) => TickPreview();
        resizeTimer.Tick += (_, _) => { resizeTimer.Stop(); RenderPreviewFrame(); };
        PreviewBorder.SizeChanged += (_, _) => { resizeTimer.Stop(); resizeTimer.Start(); };
    }

    // ---- option lists -----------------------------------------------------------

    private void BuildOptions()
    {
        var entityCount = Math.Max(game.EntityCount, entity + 1);
        entityOptions = Enumerable.Range(0, entityCount)
            .Select(i => new FilterableComboBox.Option(i, game.EntityName(i) is { } n ? $"{i}: {n}" : $"{i}"))
            .ToList();
        BuildBodyAndAnimOptions();
    }

    private void BuildBodyAndAnimOptions()
    {
        var bodies = game.EntityBodies(entity);
        var options = new List<FilterableComboBox.Option> { new(-1, "-1: no body") };
        options.AddRange(bodies.OrderBy(b => b.Key).Select(b => new FilterableComboBox.Option(b.Key, $"{b.Key}: {game.BodyName(b.Value) ?? $"BODY.HQR #{b.Value}"}")));
        // A body id the entity doesn't define still shows as itself.
        if (bodyId >= 0 && !bodies.ContainsKey(bodyId)) options.Add(new FilterableComboBox.Option(bodyId, $"{bodyId}"));
        bodyOptions = options;

        var anims = game.EntityAnims(entity);
        // (an animation with fewer bones than the chosen body can't move it: the preview shows the body still, so the list says so)
        var animList = anims.OrderBy(a => a.Key).Select(a => new FilterableComboBox.Option(a.Key, $"{a.Key}: {game.AnimName(a.Value) ?? $"ANIM.HQR #{a.Value}"}{(FitsBody(a.Key) ? "" : "  (not for this body: too few bones)")}")).ToList();
        if (!anims.ContainsKey(animId)) animList.Add(new FilterableComboBox.Option(animId, $"{animId}"));
        animOptions = animList;
    }

    // Whether the entity's animation `anim` can play on the chosen body (it moves at least as many bones as the body has: what the preview needs); true when that can't be told.
    private bool FitsBody(int anim)
    {
        if (data.IsHero || data.IsSprite || bodyId < 0) return true;
        var body = game.BodyIndex(entity, bodyId) is { } index ? images.Body(index) : null;
        return body is null || game.AnimIndex(entity, anim) is not { } animIndex || game.Animation(animIndex) is not { } animation || animation.BoneCount >= body.Bones.Count;
    }

    // Another body of the entity was picked: an animation that can't move it gives way to the entity's standing one (generic animation 0) or the first that can, and
    // the list marks the ones that can't.
    private void BodyChanged()
    {
        var anims = game.EntityAnims(entity);
        if (!FitsBody(animId))
        {
            var playable = anims.Keys.Order().Where(FitsBody).ToList();
            if (playable.Count > 0)
            {
                var was = animId;
                animId = playable.Contains(0) ? 0 : playable[0];
                StatusLabel.Text = $"Animation {was} has too few bones for this body; using {animId}.";
            }
        }
        BuildBodyAndAnimOptions();
        loading = true;
        animFilter?.Refresh();
        SetComboText();
        loading = false;
        RefreshPreviewTarget();
    }

    private void SetComboText()
    {
        EntityCombo.Text = entityOptions.FirstOrDefault(o => o.Index == entity)?.Display ?? entity.ToString();
        BodyCombo.Text = bodyOptions.FirstOrDefault(o => o.Index == bodyId)?.Display ?? bodyId.ToString();
        AnimCombo.Text = animOptions.FirstOrDefault(o => o.Index == animId)?.Display ?? animId.ToString();
    }

    private static string LeadingIndex(string text)
    {
        var colon = text.IndexOf(':');
        return (colon >= 0 ? text[..colon] : text).Trim();
    }

    private static int ParseIndex(string text, int fallback) => int.TryParse(LeadingIndex(text), out var value) ? value : fallback;

    // Typed-in numbers count when the box loses focus; anything unusable reverts to the current value.
    private void CommitTyped(int which)
    {
        if (loading) return;
        var combo = which switch { 0 => EntityCombo, 1 => BodyCombo, _ => AnimCombo };
        if (!int.TryParse(LeadingIndex(combo.Text), out var value))
        {
            SetComboText();
            return;
        }
        if (which == 0)
        {
            if (value != entity) { entity = Math.Max(0, value); EntityChanged(); }
            else SetComboText();
        }
        else if (which == 1) { bodyId = value; BodyChanged(); }
        else { animId = value; RefreshPreviewTarget(); SetComboText(); }
    }

    private void EntityCommitted()
    {
        var value = ParseIndex(EntityCombo.Text, entity);
        if (value == entity) return;
        entity = Math.Max(0, value);
        EntityChanged();
    }

    // A different entity has its own bodies and animations: keep the ids when it has them, else take its first.
    private void EntityChanged()
    {
        var bodies = game.EntityBodies(entity);
        var anims = game.EntityAnims(entity);
        if (bodyId >= 0 && !bodies.ContainsKey(bodyId) && bodies.Count > 0) bodyId = bodies.Keys.Min();
        if (!anims.ContainsKey(animId) && anims.Count > 0) animId = anims.Keys.Min();
        BuildBodyAndAnimOptions();
        loading = true;
        bodyFilter?.Refresh();
        loading = false;
        BodyChanged();      // (the animation has to fit the entity's body too; this also refreshes the animation list, the boxes and the preview)
    }

    // ---- kinds of actor ---------------------------------------------------------

    private void ApplyActorKindLimits()
    {
        if (data.IsHero)
        {
            foreach (var box in new Control[] { BetaBox, LifePointBox, ArmourBox, HitForceBox, MoveBox, EntityCombo, BodyCombo, AnimCombo }) box.IsEnabled = false;
            FlagsListLeft.IsEnabled = FlagsListRight.IsEnabled = false;
            EntityLabel.Text = "Body and behaviour";
            EntityCombo.Text = "set by the game (Twinsen's outfit depends on his behaviour)";
            BodyCombo.Text = AnimCombo.Text = "-";
            StatusLabel.Text = "Only the start position is stored in the scene for Twinsen.";
        }
        else if (data.IsSprite)
        {
            foreach (var box in new Control[] { EntityCombo, BodyCombo, AnimCombo }) box.IsEnabled = false;
            EntityLabel.Text = "Sprite";
            EntityCombo.Text = $"sprite {data.Sprite} (a sprite actor draws an image, not a body)";
            BodyCombo.Text = AnimCombo.Text = "-";
        }
    }

    // ---- preview ----------------------------------------------------------------

    private int? previewBodyIndex;
    private LbaBodyStudio.Body? previewBody;
    private Lba1Animation.Player? player;
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private long lastTick;
    private bool paused;

    private void RefreshPreviewTarget()
    {
        if (loading) return;
        previewBodyIndex = data.IsHero ? game.BodyIndex(0, 0)
            : data.IsSprite || bodyId < 0 ? null
            : game.BodyIndex(entity, bodyId);
        previewBody = previewBodyIndex is { } b ? images.Body(b) : null;

        // The chosen animation, when it fits this body's bones.
        player = null;
        if (previewBody is not null && !data.IsHero && !data.IsSprite
            && game.AnimIndex(entity, animId) is { } animIndex
            && game.Animation(animIndex) is { } animation && animation.BoneCount >= previewBody.Bones.Count)
            player = new Lba1Animation.Player(animation);
        lastTick = clock.ElapsedMilliseconds;
        RenderPreviewFrame();
    }

    private void TickPreview()
    {
        var now = clock.ElapsedMilliseconds;
        if (!paused) player?.Advance(now - lastTick);
        lastTick = now;
        if (!isDraggingPreview) previewAngle = (previewAngle + rotationSpeed) % 4096;
        RenderPreviewFrame();
    }

    private void RenderPreviewFrame()
    {
        if (previewBodyIndex is not { } body)
        {
            BodyPreviewImage.Source = null;
            BodyPreviewFallbackLabel.Visibility = Visibility.Visible;
            BodyPreviewFallbackLabel.Text = data.IsSprite ? $"sprite {data.Sprite}" : bodyId < 0 ? "this actor has no body" : "no body found for this entity";
            return;
        }
        var w = (int)Math.Max(80, PreviewBorder.ActualWidth - 2);
        var h = (int)Math.Max(120, PreviewBorder.ActualHeight - 2);
        if (images.RenderPreview(body, w, h, previewAngle * (float)(Math.PI * 2 / 4096), Pose()) is { } source)
        {
            BodyPreviewImage.Source = source;
            BodyPreviewFallbackLabel.Visibility = Visibility.Collapsed;
        }
        else
        {
            BodyPreviewImage.Source = null;
            BodyPreviewFallbackLabel.Visibility = Visibility.Visible;
            BodyPreviewFallbackLabel.Text = "couldn't render this body";
        }
    }

    private System.Numerics.Vector3[]? Pose() => player is not null && previewBody is not null ? Lba1Pose.World(previewBody, player.Current()) : null;

    private void PauseAnimationCheck_Changed(object sender, RoutedEventArgs e)
    {
        paused = PauseAnimationCheck.IsChecked == true;
        RenderPreviewFrame();
    }

    private void RotationSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => rotationSpeed = (int)RotationSpeedSlider.Value;

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

    // ---- buttons ----------------------------------------------------------------

    // Explore mode looks but doesn't change: the fields stay readable, the preview still plays, and Apply is off.
    public void MakeViewOnly()
    {
        ApplyButton.IsEnabled = false;
        ApplyButton.ToolTip = "Explore mode only looks; switch to Build mode to change the actor.";
        Title += " (view only)";
    }
    // Ctrl+S applies, the same as every other window's own "save" shortcut.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Key != Key.S || !ApplyButton.IsEnabled) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        Apply_Click(sender, e);
        e.Handled = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        using var busy = UiBusy.Cursor();
        var edited = data.Clone();
        bool Int(TextBox box, string what, out int value)
        {
            if (int.TryParse(box.Text.Trim(), out value)) return true;
            StatusLabel.Text = $"“{what}” must be a whole number.";
            return false;
        }
        if (!Int(PositionXBox, "X", out var x) || !Int(PositionYBox, "Y", out var y) || !Int(PositionZBox, "Z", out var z)) return;
        edited.X = x; edited.Y = y; edited.Z = z;
        if (!data.IsHero)
        {
            if (!Int(BetaBox, "Facing", out var angle) || !Int(LifePointBox, "Life points", out var life) || !Int(ArmourBox, "Armour", out var armor)
                || !Int(HitForceBox, "Hit force", out var hit) || !Int(MoveBox, "Move type", out var move)) return;
            edited.Angle = angle; edited.LifePoints = life; edited.Armor = armor; edited.HitForce = hit; edited.Move = move;
            if (!data.IsSprite)
            {
                edited.Entity = entity;
                edited.Body = bodyId;
                edited.Anim = animId;
            }
            var knownMask = flagItems.Aggregate(0u, (mask, item) => mask | item.Bit);
            var flags = ((uint)data.Flags & ~knownMask) | flagItems.Where(i => i.IsChecked).Aggregate(0u, (mask, item) => mask | item.Bit);
            edited.Flags = (int)flags;
        }

        var error = apply(edited);
        if (error is not null) { StatusLabel.Text = error; return; }
        StatusLabel.Text = "Saved to SCENE.HQR (the first save keeps SCENE.HQR.bak).";
        CopyFrom(edited);
    }

    // What was saved is the new baseline.
    private void CopyFrom(Lba1ActorData saved)
    {
        data.X = saved.X; data.Y = saved.Y; data.Z = saved.Z; data.Angle = saved.Angle; data.LifePoints = saved.LifePoints;
        data.Armor = saved.Armor; data.HitForce = saved.HitForce; data.Move = saved.Move; data.Entity = saved.Entity;
        data.Body = saved.Body; data.Anim = saved.Anim; data.Flags = saved.Flags;
    }

    private void EditScript_Click(object sender, RoutedEventArgs e) => openScript();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
