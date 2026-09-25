using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using DColor = System.Drawing.Color;

namespace LbaBodyStudio;

// WPF port of Body Studio (formerly MainForm.cs, a WinForms Form) -- see the Interface Audit's "Two
// windows are genuinely WinForms in a WPF app" finding: different font rendering, DPI handling and focus
// visuals from the rest of the app, plus a launcher lifecycle (one static instance, see
// LBAAssembler.BodyStudioLauncher) unlike every other window's per-instance WindowLifecycle position
// memory. This keeps every field, button and behaviour (including the dirty/undo/close-warning safety net
// added earlier) exactly as it was; only the control types and the theme mechanism (Theme.xaml/UiBrushes
// instead of hand-copied System.Drawing colours) changed. Renderer.Render's own rasterizer is untouched --
// see Renderer.cs's ModelView for the only rendering-side change (GDI+ Bitmap -> WPF BitmapSource).
internal sealed class BodyStudioWindow : Window
{
    private static SolidColorBrush B(DColor c) { var b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)); b.Freeze(); return b; }
    private static readonly Brush PanelBg = B(Renderer.PanelBackground), FieldBg = B(Renderer.FieldBackground), ButtonBg = B(Renderer.ButtonBackground),
        ButtonBorder = B(Renderer.ButtonBorder), ButtonHover = B(Renderer.ButtonHover), Accent = B(Renderer.Accent), Border = B(Renderer.Border),
        TextBrush = B(Renderer.Text), TextMuted = B(Renderer.TextMuted);

    private static ComboBox Combo(params string[] items) { var c = new ComboBox(); foreach (var i in items) c.Items.Add(i); c.SelectedIndex = 0; return c; }
    private static NumberBox Number(int min, int max, int value) => new(min, max, value);

    private readonly TextBox imagePath = new(), game1 = new(), game2 = new(), output = new();
    private readonly TextBox bandanaText = new() { MaxLength = 8 };
    private readonly CheckBox headDetails = new() { Content = "Add bandana, teeth and rear ties" };
    private readonly ComboBox target = Combo("Both", "LBA1", "LBA2"), layout = Combo("Front + back", "Single front"), mask = Combo("Dark subject", "Light subject", "Transparent background", "Background colour");
    private readonly ComboBox method = Combo("New humanoid", "Fit template");
    private readonly NumberBox body1 = Number(0, 10000, 0), body2 = Number(0, 10000, 0), threshold = Number(1, 254, 45), fit = Number(0, 100, 80), height = Number(25, 300, 100), width = Number(25, 300, 100), depth = Number(25, 300, 100), head = Number(25, 150, 70), budget = Number(0, 540, 460);
    private readonly CheckBox autoCrop = new() { Content = "Auto crop subject", IsChecked = true }, flip = new() { Content = "Mirror image projection" }, negative = new() { Content = "Front faces negative Z" } /* actual default comes from Settings.NegativeZFront via Apply() below */, archive = new() { Content = "Include a separate BODY.HQR copy", IsChecked = true };
    private readonly NumberBox flatColours = Number(2, 40, 14);
    private readonly CheckBox pairImage = new() { Content = "The picture holds a front view (left) and a back view (right)" }, symmetric = new() { Content = "Make the figure symmetric (copy the left half)" }, lit = new() { Content = "Game lighting: shade the body like the game's own characters", IsChecked = true };
    private readonly Dictionary<int, BodyStyleStats> styleStats = new();
    private readonly ModelView preview = new();
    private readonly Image reference = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock status = new() { Padding = new Thickness(16, 8, 16, 8), Text = "Choose an image, generate, then inspect the preview before exporting.", TextWrapping = TextWrapping.Wrap };
    private readonly Button generate = new() { Content = "Generate preview" }, export = new() { Content = "Export selected games", IsEnabled = false };
    private readonly ComboBox previewGame = Combo("LBA1", "LBA2");
    private readonly List<Generated> generated = new();
    private Settings? generatedSettings;

    // No dirty flag, no undo, and no close-time warning existed here at all (unlike every WPF editor in
    // the app, which all guard Closing on a dirty flag) -- the one editor where a long tuning session could
    // vanish on a single misclick. Settings is already a plain POCO round-tripped whole via ReadSettings()/
    // Apply() for Save/Load, so that same pair doubles as the undo snapshot mechanism: no cloning method
    // needed, just capture what ReadSettings() returns before each change lands. `applying` guards Apply()
    // itself from re-triggering the same change-tracking loop it's driving (loading a project, or an undo/
    // redo restore, would otherwise push its own restore back onto the stack as if the user had typed it).
    private bool dirty, applying;
    private Settings lastKnownGood = new();
    // List, not Stack<T>: undo/redo both need to push/pop the newest end (List.Add / RemoveAt(Count-1) are
    // just as O(1) for that), but capping also needs to drop the OLDEST entry once full, which Stack<T> has
    // no way to do at all -- its Pop() only ever removes the newest, so capping through it would silently
    // discard the entry just pushed instead of the one furthest back.
    private readonly List<Settings> undoStack = new(), redoStack = new();
    private const int UndoCap = 50;
    private static void Push(List<Settings> stack, Settings s) { stack.Add(s); if (stack.Count > UndoCap) stack.RemoveAt(0); }
    private static Settings Pop(List<Settings> stack) { var s = stack[^1]; stack.RemoveAt(stack.Count - 1); return s; }

    public BodyStudioWindow()
    {
        Title = "LBA Assembler — Body Studio";
        Width = 1400; Height = 930; MinWidth = 1050; MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var main = new Grid();
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
        void Note(string text) => Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 325, Foreground = TextMuted });
        void Field(string text, UIElement c) { Add(new TextBlock { Text = text, Margin = new Thickness(0, 2, 0, 3), Foreground = TextBrush }); Add(c); }

        UIElement Browse(TextBox text, bool file)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            Grid.SetColumn(text, 0);
            var button = new Button { Content = "…", Margin = new Thickness(4, 0, 0, 0) };
            Grid.SetColumn(button, 1);
            row.Children.Add(text); row.Children.Add(button);
            button.Click += (_, _) =>
            {
                if (file)
                {
                    var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
                    if (d.ShowDialog(this) == true) { text.Text = d.FileName; LoadImage(); }
                }
                else
                {
                    var d = new OpenFolderDialog { InitialDirectory = Directory.Exists(text.Text) ? text.Text : null };
                    if (d.ShowDialog(this) == true) text.Text = d.FolderName;
                }
            };
            return row;
        }

        Label("LBA BODY STUDIO");
        Note("Fit a native character template to a reference image. Both games export independently with their own rig and palette.");
        Field("Reference image", Browse(imagePath, true)); Field("Image layout", layout); Field("Subject mask", mask); Field("Mask threshold", threshold);
        Add(autoCrop); Add(flip); Add(negative);
        Label("Flat picture for the game");
        Note("Turn any picture into the flat, few-colour, palette-exact picture the generator works best with (the game's own bodies use a dozen or so colours). Or export a game body as such a picture to see what one looks like: the template's game and index are chosen below.");
        Field("Flat colours", flatColours); Add(pairImage); Add(symmetric);
        var convertButton = new Button { Content = "Convert the reference image to game style" }; Add(convertButton); convertButton.Click += async (_, _) => await ConvertStyle();
        var sheetButton = new Button { Content = "Export a flat sheet of the selected template…" }; Add(sheetButton); sheetButton.Click += (_, _) => ExportSheet();
        Add(lit);
        Label("Head details — optional"); Add(headDetails); Field("Bandana lettering (up to 8 characters)", bandanaText);
        Note("New humanoid mode only. Builds actual head-bone geometry. Uses a compact block font; text is supplied here, not read automatically from the image.");
        headDetails.Checked += (_, _) => bandanaText.IsEnabled = true; headDetails.Unchecked += (_, _) => bandanaText.IsEnabled = false; bandanaText.IsEnabled = false;
        Label("Game assets and template");
        Field("LBA1 installation", Browse(game1, false)); Field("LBA1 body index (zero-based)", body1); Field("LBA2 installation", Browse(game2, false)); Field("LBA2 body index (zero-based)", body2);
        var originalButton = new Button { Content = "Preview original selected template" }; Add(originalButton); originalButton.Click += (_, _) => PreviewOriginal();
        Label("Shape and detail");
        Field("Mesh generation", method); Field("Silhouette fit (%) — template mode", fit); Field("Height (%)", height); Field("Width (%)", width); Field("Depth (%) — inferred", depth); Field("Head proportion (%) — 70 recommended", head); Field("Refinement primitive budget (0 = no extra detail)", budget);
        Label("Export");
        Field("Output format", target); Field("Output folder", Browse(output, false)); Add(archive);
        var projectButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "Save settings", Margin = new Thickness(0, 0, 8, 0) }; var load = new Button { Content = "Load settings" };
        projectButtons.Children.Add(save); projectButtons.Children.Add(load); Add(projectButtons);
        Note("Best input: upright full-body views on a plain background. Optional head details replace projected facial colours with stylised geometry and reserve the mesh budget for the face. Check Head close-up, then the whole body. In-game appearance still needs testing.");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 0, 8) };
        generate.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(generate); buttons.Children.Add(export);
        Grid.SetRow(buttons, 1);
        left.Children.Add(buttons);

        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Border };
        Grid.SetColumn(splitter, 1);
        main.Children.Add(splitter);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 2);
        main.Children.Add(right);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        previewGame.Width = 95; previewGame.Margin = new Thickness(0, 0, 8, 0);
        var wire = new CheckBox { Content = "Wireframe", Margin = new Thickness(0, 0, 8, 0) };
        var bones = new CheckBox { Content = "Bones", Margin = new Thickness(0, 0, 8, 0) };
        var front = new Button { Content = "Front", Margin = new Thickness(0, 0, 4, 0) };
        var back = new Button { Content = "Back", Margin = new Thickness(0, 0, 8, 0) };
        var closeUp = new CheckBox { Content = "Head close-up" };
        foreach (UIElement e in new UIElement[] { previewGame, wire, bones, front, back, closeUp }) toolbar.Children.Add(e);
        Grid.SetRow(toolbar, 0);
        right.Children.Add(toolbar);
        closeUp.Checked += (_, _) => { preview.HeadOnly = true; preview.Invalidate(); }; closeUp.Unchecked += (_, _) => { preview.HeadOnly = false; preview.Invalidate(); };

        var tabs = new TabControl();
        var modelTab = new TabItem { Header = "3D body", Content = preview };
        var imageTab = new TabItem { Header = "Reference image", Content = reference };
        tabs.Items.Add(modelTab); tabs.Items.Add(imageTab);
        Grid.SetRow(tabs, 1);
        right.Children.Add(tabs);

        wire.Checked += (_, _) => { preview.Wire = true; preview.Invalidate(); }; wire.Unchecked += (_, _) => { preview.Wire = false; preview.Invalidate(); };
        bones.Checked += (_, _) => { preview.Bones = true; preview.Invalidate(); }; bones.Unchecked += (_, _) => { preview.Bones = false; preview.Invalidate(); };
        front.Click += (_, _) => { preview.Yaw = negative.IsChecked == true ? 0 : MathF.PI; preview.Invalidate(); };
        back.Click += (_, _) => { preview.Yaw = negative.IsChecked == true ? MathF.PI : 0; preview.Invalidate(); };
        previewGame.SelectionChanged += (_, _) => ShowGenerated();
        generate.Click += async (_, _) => await Generate();
        export.Click += async (_, _) => await Export();
        save.Click += (_, _) => SaveProject();
        load.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "Body Studio project|*.json" };
            if (d.ShowDialog(this) == true) Try(() =>
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(d.FileName)) ?? throw new InvalidDataException("Empty project.");
                applying = true; try { Apply(loaded); } finally { applying = false; }
                LoadImage(); lastKnownGood = loaded; dirty = false;
            });
        };

        var statusBar = new Border { Background = PanelBg, Child = status };
        Grid.SetRow(statusBar, 1);
        root.Children.Add(statusBar);

        Apply(new Settings { OutputFolder = Path.Combine(AppContext.BaseDirectory, "Body Exports"), Lba1Folder = LBAAssembler.EditorSettings.Current.Lba1Directory, Lba2Folder = LBAAssembler.EditorSettings.Current.GameDirectory });
        dirty = false;

        // Any setting change invalidates the export snapshot until regenerated, marks the project dirty, and
        // (unless it's Apply() itself driving the controls, e.g. Load/Undo/Redo) pushes what things looked
        // like a moment ago onto the undo stack.
        void Changed()
        {
            InvalidateGeneration();
            if (applying) return;
            dirty = true;
            Push(undoStack, lastKnownGood); redoStack.Clear();
            lastKnownGood = ReadSettings();
        }
        foreach (var c in Descendants(fieldsPanel))
        {
            if (c is TextBox t) t.TextChanged += (_, _) => Changed();
            if (c is NumberBox n) n.ValueChanged += Changed;
            if (c is ComboBox cb) cb.SelectionChanged += (_, _) => Changed();
            if (c is CheckBox ch) { ch.Checked += (_, _) => Changed(); ch.Unchecked += (_, _) => Changed(); }
        }

        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z && undoStack.Count > 0) { Push(redoStack, ReadSettings()); ApplyRestoring(Pop(undoStack)); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y && redoStack.Count > 0) { Push(undoStack, ReadSettings()); ApplyRestoring(Pop(redoStack)); e.Handled = true; }
        };
        Closing += (_, e) => { if (dirty && !ConfirmDiscard()) e.Cancel = true; };
        ApplyTheme();
    }

    // Undo/redo restores through the same Apply() Save/Load already uses, but -- unlike a normal edit --
    // shouldn't itself push a fresh undo entry or leave the project marked dirty relative to this restored
    // point (Ctrl+Z then Ctrl+Z again should keep walking further back, not get stuck re-recording the same
    // step forward).
    private void ApplyRestoring(Settings s) { applying = true; try { Apply(s); } finally { applying = false; } lastKnownGood = s; dirty = true; }

    private void SaveProject()
    {
        var d = new SaveFileDialog { Filter = "Body Studio project|*.json", FileName = "body-project.json" };
        if (d.ShowDialog(this) != true) return;
        Try(() => { File.WriteAllText(d.FileName, JsonSerializer.Serialize(ReadSettings(), new JsonSerializerOptions { WriteIndented = true })); dirty = false; });
    }

    // Mirrors GridEditorWindow.cs / AssetEditorWindow.cs's own Closing-guard shape (Yes/No/Cancel, Yes saves first).
    private bool ConfirmDiscard()
    {
        var answer = MessageBox.Show(this, "This project has unsaved changes. Save before closing?", "Body Studio", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) SaveProject();
        return !dirty || answer == MessageBoxResult.No;
    }

    private void ApplyTheme()
    {
        foreach (var c in Descendants((DependencyObject)Content))
        {
            switch (c)
            {
                case TextBox tb: tb.Background = FieldBg; tb.Foreground = TextBrush; tb.BorderBrush = Border; break;
                case NumberBox nb: nb.Theme(FieldBg, TextBrush, Border); break;
                case ComboBox: break; // themed globally via Theme.xaml
                case Button b when b != generate && b != export: b.Background = ButtonBg; b.Foreground = TextBrush; b.BorderBrush = ButtonBorder; break;
                case CheckBox chk: chk.Foreground = TextBrush; break;
            }
        }
        foreach (var b in new[] { generate, export }) { b.Background = Accent; b.Foreground = Brushes.White; b.FontWeight = FontWeights.Bold; }
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

    private void InvalidateGeneration() { generatedSettings = null; export.IsEnabled = false; }

    private Settings ReadSettings() => new()
    {
        ImagePath = imagePath.Text, Lba1Folder = game1.Text, Lba2Folder = game2.Text, Lba1Body = body1.Value, Lba2Body = body2.Value,
        Target = (string)target.SelectedItem, Layout = (string)layout.SelectedItem, Method = (string)method.SelectedItem, Mask = (string)mask.SelectedItem,
        Threshold = threshold.Value, AutoCrop = autoCrop.IsChecked == true, FlipFront = flip.IsChecked == true, NegativeZFront = negative.IsChecked == true,
        Fit = fit.Value / 100f, Height = height.Value / 100f, Width = width.Value / 100f, Depth = depth.Value / 100f, HeadScale = head.Value / 100f,
        DetailBudget = budget.Value, HeadDetails = headDetails.IsChecked == true, BandanaText = bandanaText.Text, ArchiveCopy = archive.IsChecked == true,
        OutputFolder = output.Text, Lit = lit.IsChecked == true,
    };

    private void Apply(Settings s)
    {
        imagePath.Text = s.ImagePath; game1.Text = s.Lba1Folder; game2.Text = s.Lba2Folder; output.Text = s.OutputFolder;
        headDetails.IsChecked = s.HeadDetails; bandanaText.Text = s.BandanaText; bandanaText.IsEnabled = s.HeadDetails;
        body1.Value = Math.Clamp(s.Lba1Body, 0, 10000); body2.Value = Math.Clamp(s.Lba2Body, 0, 10000); threshold.Value = Math.Clamp(s.Threshold, 1, 254);
        fit.Value = (int)Math.Clamp(s.Fit * 100, 0, 100); height.Value = (int)Math.Clamp(s.Height * 100, 25, 300); width.Value = (int)Math.Clamp(s.Width * 100, 25, 300);
        depth.Value = (int)Math.Clamp(s.Depth * 100, 25, 300); head.Value = (int)Math.Clamp(s.HeadScale * 100, 25, 150); budget.Value = Math.Clamp(s.DetailBudget, 0, 540);
        lit.IsChecked = s.Lit; target.SelectedItem = s.Target; layout.SelectedItem = s.Layout; method.SelectedItem = s.Method; mask.SelectedItem = s.Mask;
        autoCrop.IsChecked = s.AutoCrop; flip.IsChecked = s.FlipFront; negative.IsChecked = s.NegativeZFront; archive.IsChecked = s.ArchiveCopy;
    }

    private void LoadImage() => Try(() =>
    {
        var bytes = File.ReadAllBytes(imagePath.Text);
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        using var stream = new MemoryStream(bytes);
        bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        reference.Source = bitmap;
    });

    private void ShowGenerated() { preview.Model = generated.FirstOrDefault(g => g.Body.Game == previewGame.SelectedIndex + 1); preview.Invalidate(); }

    private void PreviewOriginal() => Try(() =>
    {
        var game = previewGame.SelectedIndex + 1; var s = ReadSettings(); var folder = game == 1 ? s.Lba1Folder : s.Lba2Folder; var index = game == 1 ? s.Lba1Body : s.Lba2Body;
        var path = Generator.BodyArchive(folder);
        preview.Model = new Generated(Body.Read(new Hqr(path).Read(index), game), Generator.Palette(folder), index, path, "");
        preview.Invalidate();
        status.Text = "Template shown from " + Path.GetFileName(path) + ". Generate to return to your image-derived body.";
    });

    private async Task Generate()
    {
        var s = ReadSettings(); IsEnabled = false; status.Text = "Fitting geometry, projecting colours, and checking native bodies…";
        try
        {
            var models = await Task.Run(() =>
            {
                var list = new List<Generated>();
                if (s.Target is "Both" or "LBA1") list.Add(Generator.Generate(s, 1));
                if (s.Target is "Both" or "LBA2") list.Add(Generator.Generate(s, 2));
                return list;
            });
            generated.Clear(); generated.AddRange(models); generatedSettings = s; previewGame.SelectedIndex = models[0].Body.Game - 1; ShowGenerated(); export.IsEnabled = true; LoadImage();
            status.Text = "Preview ready. Drag to rotate; check front, back and bones. Export uses this exact generated snapshot.";
        }
        catch (Exception e) { generatedSettings = null; export.IsEnabled = false; Error(e); }
        finally { IsEnabled = true; }
    }

    private async Task Export()
    {
        if (generatedSettings == null) return;
        IsEnabled = false; status.Text = "Writing body files, archive copies and previews…";
        try
        {
            var folder = await Task.Run(() => Generator.Export(generatedSettings, generated));
            status.Text = "Exported to " + folder;
            MessageBox.Show(this, "Export complete. Original game files were not modified.\n\n" + folder + "\n\nRead INSTALL.txt before testing in a copy of the game.", "Export complete");
        }
        catch (Exception e) { Error(e); }
        finally { IsEnabled = true; }
    }

    // ---- flat pictures ----
    private static byte[] PaletteBytes(string folder) => new Hqr(Path.Combine(folder, "RESS.HQR")).Read(0);

    private BodyStyleStats StatsFor(int game, string folder)
    {
        if (styleStats.TryGetValue(game, out var known)) return known;
        var hqr = new Hqr(Generator.BodyArchive(folder));
        var bodies = new List<byte[]>();
        for (var i = 0; i < hqr.Count; i++) { try { bodies.Add(hqr.Read(i)); } catch (InvalidDataException) { } }
        return styleStats[game] = BodyStyleStats.Analyse(game, bodies);
    }

    // The template's game and body index are the preview game's own fields (LBA1 / LBA2 installation and body index).
    private void ExportSheet() => Try(() =>
    {
        var game = previewGame.SelectedIndex + 1; var s = ReadSettings(); var folder = game == 1 ? s.Lba1Folder : s.Lba2Folder; var index = game == 1 ? s.Lba1Body : s.Lba2Body;
        var body = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(index), game);
        var sheet = FlatSheet.Render(body, PaletteBytes(folder));
        var d = new SaveFileDialog { Filter = "PNG image|*.png", FileName = $"lba{game}-body{index}-flat-sheet.png" };
        if (d.ShowDialog(this) != true) return;
        FlatBitmap.Save(sheet, d.FileName);
        status.Text = $"Saved the game's own body {index} of LBA{game} as a flat front + back sheet ({FlatSheet.Colours(body).Length} colours, {body.Faces.Count} polygons): {d.FileName}. It is the kind of picture the generator reads.";
    });

    private async Task ConvertStyle()
    {
        var s = ReadSettings(); var game = previewGame.SelectedIndex + 1; var folder = game == 1 ? s.Lba1Folder : s.Lba2Folder;
        if (!File.Exists(s.ImagePath)) { Error(new InvalidDataException("Choose a reference image first.")); return; }
        var options = new StyleOptions { Colours = flatColours.Value, Symmetrise = symmetric.IsChecked == true, BackFromFront = true };
        var pair = pairImage.IsChecked == true; IsEnabled = false; status.Text = "Reading the game's bodies and flattening the picture…";
        try
        {
            var (result, path) = await Task.Run(() =>
            {
                options.Allowed = StatsFor(game, folder).RecommendedDisplay(3);
                var image = FlatBitmap.Load(s.ImagePath); var palette = PaletteBytes(folder);
                var r = pair ? GameStyle.ConvertPair(image, palette, options) : GameStyle.Convert(image, palette, options);
                var directory = Path.Combine(string.IsNullOrWhiteSpace(s.OutputFolder) ? AppContext.BaseDirectory : s.OutputFolder, "Flat sheets"); Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, Path.GetFileNameWithoutExtension(s.ImagePath) + $"-lba{game}-flat.png"); FlatBitmap.Save(r.Sheet, file);
                return (r, file);
            });
            imagePath.Text = path; layout.SelectedItem = "Front + back"; mask.SelectedItem = "Transparent background"; LoadImage();
            status.Text = $"Flat sheet saved to {path} and set as the reference image (front + back, transparent background): {result.Notes}. Generate to build the body.";
        }
        catch (Exception e) { Error(e); }
        finally { IsEnabled = true; }
    }

    private void Try(Action action) { try { action(); } catch (Exception e) { Error(e); } }
    private void Error(Exception e) { status.Text = e.Message; MessageBox.Show(this, e.Message, "Body Studio", MessageBoxButton.OK, MessageBoxImage.Error); }
}
