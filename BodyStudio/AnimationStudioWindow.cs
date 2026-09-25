using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using LBAAssembler.Lba1;
using DColor = System.Drawing.Color;

namespace LbaBodyStudio;

// WPF port of Animation Studio (formerly AnimationStudioForm.cs, a WinForms Form) -- the "full animation
// suite" companion to BodyStudioWindow.cs's own Body Studio: picks a roster character (MarioCustom.cs and
// its own siblings) and an AnimGenerator clip (walk/run/idle/jump), exposes the generator's own tunable
// parameters as live sliders, plays the result back on a real posed preview (Lba1Pose.World), and exports
// the generated clip to a real ANIM.HQR-format archive. See BodyStudioWindow.cs's own comment for why this
// was ported (Interface Audit finding) and what changed (control types + theme only, same behaviour).
internal sealed class AnimationStudioWindow : Window
{
    private static SolidColorBrush B(DColor c) { var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)); b.Freeze(); return b; }
    private static readonly Brush PanelBg = B(Renderer.PanelBackground), FieldBg = B(Renderer.FieldBackground), ButtonBg = B(Renderer.ButtonBackground),
        ButtonBorder = B(Renderer.ButtonBorder), Accent = B(Renderer.Accent), TextBrush = B(Renderer.Text), TextMuted = B(Renderer.TextMuted);

    private static ComboBox Combo(params string[] items) { var c = new ComboBox(); foreach (var i in items) c.Items.Add(i); c.SelectedIndex = 0; return c; }
    private static NumberBox Number(int min, int max, int value) => new(min, max, value);

    private readonly ComboBox character = Combo("Mario", "Luigi", "Peach", "Toad", "Bowser", "Yoshi");
    private readonly ComboBox game = Combo("LBA2", "LBA1");
    private readonly ComboBox animType = Combo("Walk", "Run", "Idle", "Jump");
    private readonly NumberBox swingDeg = Number(0, 90, 22), kneeDeg = Number(0, 90, 35), armDeg = Number(0, 90, 18), leanDeg = Number(-20, 20, 0);
    private readonly NumberBox frameCount = Number(2, 24, 8), frameTicks = Number(1, 40, 9);
    private readonly ModelView preview = new();
    private readonly TextBlock status = new() { Padding = new Thickness(16, 8, 16, 8), Text = "Choose a character and clip, then Regenerate.", TextWrapping = TextWrapping.Wrap };
    private readonly Button regenerate = new() { Content = "Regenerate" }, export = new() { Content = "Export…", IsEnabled = false };
    private readonly CheckBox playing = new() { Content = "Play", IsChecked = true };
    private readonly DispatcherTimer playTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private Anim? currentAnim;
    private Body? currentBody;
    private DColor[]? currentPalette;
    private int currentFrame;
    private double elapsedTicks;

    public AnimationStudioWindow()
    {
        Title = "LBA Assembler — Animation Studio";
        Width = 1200; Height = 930; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var main = new Grid();
        // 390/360, matching Body Studio's own panel sizing -- 320/280 clipped several of this form's own
        // longer field labels/combo text at the panel edge (confirmed live 2026-09-23, in the original
        // WinForms version -- unchanged here).
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(main, 0);
        root.Children.Add(main);

        var left = new Grid { Background = PanelBg };
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(left, 0);
        main.Children.Add(left);

        var fieldsPanel = new StackPanel { Margin = new Thickness(18) };
        var fields = new ScrollViewer { Content = fieldsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(fields, 0);
        left.Children.Add(fields);

        void Add(UIElement c) { if (c is FrameworkElement f) f.Margin = new Thickness(0, 0, 0, 10); fieldsPanel.Children.Add(c); }
        void Label(string text) => Add(new TextBlock { Text = text, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Foreground = TextBrush });
        void Note(string text) => Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 255, Foreground = TextMuted });
        void Field(string text, UIElement c) { Add(new TextBlock { Text = text, Margin = new Thickness(0, 2, 0, 3), Foreground = TextBrush }); Add(c); }

        Label("ANIMATION STUDIO");
        Note("Generates walk/run/idle/jump clips procedurally (AnimGenerator.cs) for any roster character -- the same 19-bone rig every custom body shares. Adjust the joint-angle knobs below and Regenerate to preview.");
        Field("Character", character); Field("Game (colours + angle scale)", game); Field("Clip", animType);
        Label("Walk / run knobs");
        Field("Leg swing (degrees)", swingDeg); Field("Knee bend (degrees)", kneeDeg); Field("Arm swing (degrees)", armDeg); Field("Forward lean (degrees, run only)", leanDeg);
        Field("Keyframes per cycle", frameCount); Field("Ticks between keyframes (lower = faster)", frameTicks);
        Note("Idle and jump use their own fixed shape (AnimGenerator.Idle/Jump) -- these knobs only affect Walk/Run.");
        Add(regenerate); Add(playing);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 0, 8) };
        buttons.Children.Add(export);
        Grid.SetRow(buttons, 1);
        left.Children.Add(buttons);

        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = B(Renderer.Border) };
        Grid.SetColumn(splitter, 1);
        main.Children.Add(splitter);

        Grid.SetColumn(preview, 2);
        main.Children.Add(preview);

        var statusBar = new Border { Background = PanelBg, Child = status };
        Grid.SetRow(statusBar, 1);
        root.Children.Add(statusBar);

        regenerate.Click += (_, _) => Regenerate();
        export.Click += (_, _) => Export();
        playing.Checked += (_, _) => playTimer.Start(); playing.Unchecked += (_, _) => playTimer.Stop();
        playTimer.Tick += (_, _) => Advance();
        foreach (var c in new UIElement[] { character, game, animType, swingDeg, kneeDeg, armDeg, leanDeg, frameCount, frameTicks })
        {
            if (c is ComboBox cb) cb.SelectionChanged += (_, _) => export.IsEnabled = false;
            if (c is NumberBox n) n.ValueChanged += () => export.IsEnabled = false;
        }
        ApplyTheme();
        playTimer.Start();
        Closed += (_, _) => playTimer.Stop();
    }

    private void ApplyTheme()
    {
        foreach (var c in Descendants((DependencyObject)Content))
        {
            switch (c)
            {
                case NumberBox nb: nb.Theme(FieldBg, TextBrush, ButtonBorder); break;
                case Button b when b != export: b.Background = ButtonBg; b.Foreground = TextBrush; b.BorderBrush = ButtonBorder; break;
                case CheckBox chk: chk.Foreground = TextBrush; break;
            }
        }
        export.Background = Accent; export.Foreground = Brushes.White; export.FontWeight = FontWeights.Bold;
        status.Foreground = TextBrush;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private void Regenerate()
    {
        try
        {
            var gameIndex = game.SelectedIndex == 0 ? 2 : 1; // combo is "LBA2","LBA1"
            var folder = gameIndex == 1 ? LBAAssembler.EditorSettings.Current.Lba1Directory : LBAAssembler.EditorSettings.Current.GameDirectory;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { Error("Set the LBA" + gameIndex + " game folder in Settings first."); return; }
            var donor = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(0), gameIndex);
            Body body = ((string)character.SelectedItem, gameIndex) switch
            {
                ("Mario", 2) => MarioCustom.Build(donor),
                ("Mario", 1) => MarioCustom.Build(donor, game: 1, red: 97, skin: 48, dark: 64, blue: 66),
                ("Luigi", 2) => LuigiCustom.Build(donor),
                ("Luigi", 1) => LuigiCustom.Build(donor, game: 1, green: 118, skin: 48, dark: 64, blue: 66),
                ("Peach", 2) => PeachCustom.Build(donor),
                ("Peach", 1) => PeachCustom.Build(donor, game: 1, pink: 224, skin: 48, gold: 144, blonde: 150),
                ("Toad", 2) => ToadCustom.Build(donor),
                ("Toad", 1) => ToadCustom.Build(donor, game: 1, red: 97, blue: 66, dark: 64, white: 214, cream: 48),
                ("Bowser", 2) => BowserCustom.Build(donor),
                ("Bowser", 1) => BowserCustom.Build(donor, game: 1, green: 118, cream: 48, shellColour: 97, dark: 64),
                ("Yoshi", 2) => YoshiCustom.Build(donor),
                ("Yoshi", 1) => YoshiCustom.Build(donor, game: 1, green: 118, cream: 48, saddle: 97, white: 214),
                _ => MarioCustom.Build(donor),
            };
            var nbBones = body.Bones.Count;
            Anim anim = ((string)animType.SelectedItem) switch
            {
                "Walk" => AnimGenerator.Gait(gameIndex, nbBones, frameCount.Value, frameTicks.Value, swingDeg.Value, kneeDeg.Value, armDeg.Value, 0),
                "Run" => AnimGenerator.Gait(gameIndex, nbBones, frameCount.Value, frameTicks.Value, swingDeg.Value, kneeDeg.Value, armDeg.Value, leanDeg.Value),
                "Idle" => AnimGenerator.Idle(gameIndex, nbBones),
                "Jump" => AnimGenerator.Jump(gameIndex, nbBones),
                _ => AnimGenerator.Walk(gameIndex, nbBones),
            };
            currentBody = body; currentAnim = anim; currentPalette = Generator.Palette(folder);
            currentFrame = 0; elapsedTicks = 0;
            preview.Model = new Generated(body, currentPalette, 0, "", "");
            export.IsEnabled = true;
            status.Text = $"{character.SelectedItem} ({(gameIndex == 1 ? "LBA1" : "LBA2")}) -- {animType.SelectedItem}, {anim.Frames.Count} keyframes, {body.Bones.Count} bones. Playing live.";
        }
        catch (Exception e) { Error(e.Message); }
    }

    // Advances playback and computes an interpolated pose between the current and next keyframe --
    // same linear-interpolation shape Lba1Animation.Player already uses for a parsed-from-bytes clip,
    // just working directly on the in-memory Anim being tuned here instead of round-tripping through
    // Write()/Parse() first (regenerating on every knob tweak already exercises the real writer, via
    // Export -- this preview path is deliberately the cheaper, no-serialization one).
    private void Advance()
    {
        if (currentAnim == null || currentBody == null || currentAnim.Frames.Count < 2) { RenderCurrentPose(); return; }
        var frames = currentAnim.Frames;
        var target = (currentFrame + 1 < frames.Count) ? currentFrame + 1 : currentAnim.LoopFrame;
        elapsedTicks += playTimer.Interval.TotalMilliseconds / 20.0; // ~50Hz engine tick, matches AnimGenerator's own frameTicks units
        var length = Math.Max(1, frames[target].FrameTime);
        if (elapsedTicks >= length) { elapsedTicks -= length; currentFrame = target; }
        RenderCurrentPose();
    }

    private void RenderCurrentPose()
    {
        if (currentAnim == null || currentBody == null) return;
        var frames = currentAnim.Frames;
        var from = frames[currentFrame];
        var target = (currentFrame + 1 < frames.Count) ? currentFrame + 1 : currentAnim.LoopFrame;
        var to = frames[target];
        var t = currentFrame == target ? 0 : Math.Clamp(elapsedTicks / Math.Max(1, to.FrameTime), 0, 1);
        var bones = new (int Type, double X, double Y, double Z)[from.Bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var a = from.Bones[i]; var b = to.Bones[i];
            // AnimBone stores turns (0..1 = 360 degrees); Lba1Pose.World's own Rotate() expects the
            // LBA1 1024-units-per-turn scale regardless of which game this clip will actually target
            // -- its rotation math is purely geometric, so this is just a fixed internal unit for the
            // preview, unrelated to Anim.Write()'s own game-aware angle scaling for the exported file.
            bones[i] = (b.Type, a.X * 1024 + (b.X - a.X) * 1024 * t, a.Y * 1024 + (b.Y - a.Y) * 1024 * t, a.Z * 1024 + (b.Z - a.Z) * 1024 * t);
        }
        preview.Pose = Lba1Pose.World(currentBody, bones);
        preview.Invalidate();
    }

    private void Export()
    {
        if (currentAnim == null) return;
        var d = new SaveFileDialog { Filter = "Animation archive|*.hqr;*.HQR", FileName = $"{character.SelectedItem}{animType.SelectedItem}.hqr" };
        if (d.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(d.FileName, Hqr.Build(new[] { currentAnim.Write() }));
            status.Text = $"Exported to {d.FileName} (entry 0, {currentAnim.Frames.Count} keyframes, {(currentAnim.Game == 1 ? "LBA1" : "LBA2")} angle scale).";
        }
        catch (Exception e) { Error(e.Message); }
    }

    private void Error(string message) { status.Text = message; MessageBox.Show(this, message, "Animation Studio", MessageBoxButton.OK, MessageBoxImage.Error); }
}
