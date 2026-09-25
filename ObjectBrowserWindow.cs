using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LbaBodyStudio;
using LBAAssembler.Lba1;

namespace LBAAssembler;

// Tools > LBA1 / LBA2: objects and bodies. Every body of BODY.HQR (both games) and LBA2's fixed objects (OBJFIX.HQR: the items, the holomap globes ...)
// drawn by Body Studio's renderer, textures and game lighting included; drag to turn. Export a body as the game's own payload or as a .obj,
// replace one from a .body file (checked before it is accepted; a .bak of the archive is kept), or send the game to Body Studio to generate a new one.
internal sealed class ObjectBrowserWindow : Window
{
    private sealed record Source(string Title, int Game, string Directory, string File, bool Static);

    private readonly ComboBox sourceBox = new() { Width = 200, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ListBox list = new() { Width = 130, FontFamily = new FontFamily("Consolas") };
    private readonly Image view = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock status = new() { Margin = new Thickness(8, 3, 8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly CheckBox wire = new() { Content = "Wireframe" };

    private Source? source;
    private HqrFile? archive;
    private Body? body;
    private int index = -1;
    private float yaw = 0.6f;
    private Point? drag;
    private System.Drawing.Color[] palette = Array.Empty<System.Drawing.Color>();
    private byte[]? texturePage;
    private readonly string? lba1Directory, lba2Directory;

    public ObjectBrowserWindow(string? lba1Directory, string? lba2Directory)
    {
        this.lba1Directory = lba1Directory; this.lba2Directory = lba2Directory;
        Title = "Objects and bodies";
        Width = 1100; Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(Control.BackgroundProperty, "ThemeWindowBrush");
        SetResourceReference(Control.ForegroundProperty, "ThemeTextBrush");
        BuildLayout();
        var sources = new List<Source>();
        if (Lba1Game.IsInstalled(lba1Directory ?? "") && File.Exists(System.IO.Path.Combine(lba1Directory!, "BODY.HQR"))) sources.Add(new("LBA1 bodies", 1, lba1Directory!, "BODY.HQR", false));
        if (lba2Directory is not null && File.Exists(System.IO.Path.Combine(lba2Directory, "BODY.HQR"))) sources.Add(new("LBA2 bodies", 2, lba2Directory, "BODY.HQR", false));
        if (lba2Directory is not null && File.Exists(System.IO.Path.Combine(lba2Directory, "OBJFIX.HQR"))) sources.Add(new("LBA2 fixed objects", 2, lba2Directory, "OBJFIX.HQR", true));
        foreach (var s in sources) sourceBox.Items.Add(new ComboBoxItem { Content = s.Title, Tag = s });
        sourceBox.SelectionChanged += (_, _) => { if (sourceBox.SelectedItem is ComboBoxItem { Tag: Source s }) Open(s); };
        Loaded += (_, _) => { if (sourceBox.Items.Count > 0) sourceBox.SelectedIndex = 0; else status.Text = "No LBA1 or LBA2 game folder is set (File > Settings)."; };
    }

    private void BuildLayout()
    {
        list.SetResourceReference(Control.BackgroundProperty, "ThemeFieldBrush");
        list.SetResourceReference(Control.ForegroundProperty, "ThemeTextBrush");
        info.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextBrush");
        status.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextBrush");
        var root = new DockPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        DockPanel.SetDock(top, Dock.Top);
        var libraryLabel = new TextBlock { Text = "Library", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        libraryLabel.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextMutedBrush");
        top.Children.Add(libraryLabel);
        top.Children.Add(sourceBox);
        foreach (var (text, tip, handler) in new (string, string, RoutedEventHandler)[]
        {
            ("Export .body", "The body exactly as the game stores it", (_, _) => ExportBody()),
            ("Export .obj", "The neutral pose as a Wavefront OBJ with palette materials", (_, _) => ExportObj()),
            ("Replace from .body…", "Replaces this entry with a body file (it is read back and checked first); a .bak of the archive is kept", (_, _) => Replace()),
            ("Body Studio…", "Opens Body Studio, to generate a body from a picture", (_, _) => BodyStudioLauncher.Show(this)),
        })
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += handler; top.Children.Add(b);
        }
        wire.SetResourceReference(Control.ForegroundProperty, "ThemeTextBrush"); wire.VerticalAlignment = VerticalAlignment.Center; wire.Checked += (_, _) => Draw(); wire.Unchecked += (_, _) => Draw();
        top.Children.Add(wire);
        root.Children.Add(top);
        var bottom = new Border { Child = status };
        bottom.SetResourceReference(Border.BackgroundProperty, "ThemeFieldBrush");
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        DockPanel.SetDock(list, Dock.Left);
        list.Margin = new Thickness(8);
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is Entry e) Show(e.Index); };
        root.Children.Add(list);

        var right = new StackPanel { Width = 250, Margin = new Thickness(8) };
        right.Children.Add(info);
        DockPanel.SetDock(right, Dock.Right);
        root.Children.Add(right);

        var host = new Border { Child = view };
        host.SetResourceReference(Border.BackgroundProperty, "ThemeWindowBrush");
        host.MouseLeftButtonDown += (_, e) => { drag = e.GetPosition(host); host.CaptureMouse(); };
        host.MouseMove += (_, e) => { if (drag is { } d && host.IsMouseCaptured) { var p = e.GetPosition(host); yaw += (float)(p.X - d.X) * 0.012f; drag = p; Draw(); } };
        host.MouseLeftButtonUp += (_, _) => { drag = null; host.ReleaseMouseCapture(); };
        host.SizeChanged += (_, _) => Draw();
        root.Children.Add(host);
        Content = root;
    }

    private sealed record Entry(int Index, string Text)
    {
        public override string ToString() => Text;
    }

    private void Open(Source s)
    {
        try
        {
            source = s;
            archive = HqrFile.Parse(File.ReadAllBytes(System.IO.Path.Combine(s.Directory, s.File)));
            palette = Generator.Palette(s.Directory);
            texturePage = s.Game == 2 ? HqrFile.Parse(File.ReadAllBytes(System.IO.Path.Combine(s.Directory, "RESS.HQR"))).Read(6) : null;
            list.Items.Clear();
            for (var i = 0; i < archive.Count; i++) if (!archive.IsEmpty(i)) list.Items.Add(new Entry(i, $"{i,5}"));
            body = null; index = -1;
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            status.Text = $"{s.Title}: {list.Items.Count} entries. Drag the picture to turn the body.";
            Title = $"Objects and bodies - {s.Title}  [{System.IO.Path.Combine(s.Directory, s.File)}]";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't open {s.Title}: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Show(int n)
    {
        if (archive is null || source is null) return;
        index = n;
        try
        {
            body = Body.Read(archive.Read(n), source.Game, true);
            body.TexturePage = texturePage;
            var textured = body.Faces.Count(f => f.Texture is not null);
            var lit = body.Faces.Count(f => LightModel.IsLit(f, body.Game, body.Lit));
            info.Text = $"Entry {n}\n{body.Vertices.Count} points, {body.Faces.Count} polygons ({textured} textured, {lit} lit)\n{body.Lines.Count} lines, {body.Spheres.Count} spheres\n{body.Bones.Count} bone{(body.Bones.Count == 1 ? "" : "s")}{(body.Static ? " (fixed object)" : "")}\n{body.Textures.Length} texture entries\n{palette.Length} colour palette";
        }
        catch (InvalidDataException e) { body = null; info.Text = $"Entry {n} isn't a body this editor reads:\n{e.Message}"; }
        Draw();
    }

    private void Draw()
    {
        if (body is null) { view.Source = null; return; }
        var w = (int)Math.Max(200, view.ActualWidth); var h = (int)Math.Max(200, view.ActualHeight);
        try
        {
            using var bitmap = Renderer.Render(body, palette, w, h, yaw, wire.IsChecked == true, background: Renderer.ViewBackground, gridLine: Renderer.ViewGrid);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            view.Source = image;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException) { status.Text = "Couldn't draw this body: " + e.Message; }
    }

    private void ExportBody()
    {
        if (archive is null || index < 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Game body|*.body", FileName = $"{System.IO.Path.GetFileNameWithoutExtension(source!.File).ToLowerInvariant()}-{index}.body" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllBytes(dialog.FileName, archive.Read(index));
        status.Text = $"Exported entry {index} to {dialog.FileName}.";
    }

    private void ExportObj()
    {
        if (body is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Wavefront OBJ|*.obj", FileName = $"body-{index}.obj" };
        if (dialog.ShowDialog(this) != true) return;
        var obj = new StringBuilder("mtllib " + System.IO.Path.GetFileNameWithoutExtension(dialog.FileName) + ".mtl\n");
        var mtl = new StringBuilder();
        foreach (var v in body.World()) obj.AppendLine(FormattableString.Invariant($"v {v.X} {v.Y} {v.Z}"));
        foreach (var c in body.Faces.Select(f => f.Colour).Distinct())
        {
            var p = palette[Math.Clamp(c, 0, palette.Length - 1)];
            mtl.AppendLine(FormattableString.Invariant($"newmtl palette{c}\nKd {p.R / 255f} {p.G / 255f} {p.B / 255f}"));
        }
        foreach (var f in body.Faces) { obj.AppendLine($"usemtl palette{f.Colour}"); obj.AppendLine("f " + string.Join(" ", f.Points.Select(p => p + 1))); }
        File.WriteAllText(dialog.FileName, obj.ToString());
        File.WriteAllText(System.IO.Path.ChangeExtension(dialog.FileName, ".mtl"), mtl.ToString());
        status.Text = $"Exported the neutral pose to {dialog.FileName} (textured polygons appear in their palette colour).";
    }

    private void Replace()
    {
        if (archive is null || source is null || index < 0) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Game body|*.body|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            var check = Body.Read(bytes, source.Game, true);   // throws unless it is a body the engine limits allow
            if (MessageBox.Show(this, $"Replace entry {index} of {source.File} with this body ({check.Vertices.Count} points, {check.Faces.Count} polygons)? A .bak of the archive is kept.", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            archive.SetStored(index, bytes);
            Scenes.HqrEntryStore.Save((Scenes.SceneGame)source.Game, source.Directory, $"Replace {source.File} entry {index}", new[] { new Scenes.HqrEntryStore.Edit(source.File, index, bytes) });
            Show(index);
            status.Text = $"Replaced entry {index} of {source.File} (the original archive is kept as .bak).";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't use that body: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
