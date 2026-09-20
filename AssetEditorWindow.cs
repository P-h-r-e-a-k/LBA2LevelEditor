using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LBA2LevelEditor.Assets;
using LBA2LevelEditor.Lba1;

namespace LBA2LevelEditor;

// Tools > Bricks and sprites: the game's run-length pictures (LBA1 and LBA2 bricks, LBA1 sprites) with a small pixel editor, PNG export and
// import (colours snap to the game's palette) and save back into the game file (a .bak of the original, the other pictures untouched).
internal sealed class AssetEditorWindow : Window
{
    private readonly string? lba1Directory, lba2Directory;
    private readonly ComboBox libraryBox = new() { Width = 170, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox filterBox = new() { Width = 90, Padding = new Thickness(3), ToolTip = "Jump to a picture number" };
    private readonly ListBox list = new() { Width = 140, FontFamily = new FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x13, 0x11)), Foreground = new SolidColorBrush(Color.FromRgb(0xC5, 0xC6, 0xB9)) };
    private readonly Image picture = new();
    private readonly Canvas surface = new() { Background = Brushes.Transparent };
    private readonly Canvas paletteCanvas = new() { Width = 256, Height = 256 };
    private readonly TextBlock info = new() { Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock status = new() { Foreground = Brushes.Gainsboro, Margin = new Thickness(8, 3, 8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Slider zoomSlider = new() { Minimum = 2, Maximum = 24, Value = 10, Width = 140, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly RadioButton paintTool = new() { Content = "Paint", IsChecked = true, GroupName = "t" }, eraseTool = new() { Content = "Erase", GroupName = "t" };
    private readonly Button saveButton = new() { Content = "Save" };

    private GphLibrary? library;
    private byte[] palette = new byte[768];
    private GphImage? image;
    private int number = -1;
    private byte colour = 1;
    private readonly Stack<(byte[] Pixels, bool[] Opaque)> undo = new();
    private readonly HashSet<int> changed = new();
    private bool dirty;
    private WriteableBitmap? bitmap;
    private System.Windows.Shapes.Rectangle? swatch;

    private sealed record Choice(string Title, Func<GphLibrary> Open);

    public AssetEditorWindow(string? lba1Directory, string? lba2Directory)
    {
        this.lba1Directory = lba1Directory; this.lba2Directory = lba2Directory;
        Title = "Bricks and sprites";
        Width = 1180; Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x19));
        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xDA));
        BuildLayout();
        var choices = new List<Choice>();
        if (Lba1Game.IsInstalled(lba1Directory ?? "")) { choices.Add(new("LBA1 bricks", () => GphLibrary.Lba1Bricks(lba1Directory!))); choices.Add(new("LBA1 sprites", () => GphLibrary.Lba1Sprites(lba1Directory!))); }
        if (lba2Directory is not null && File.Exists(System.IO.Path.Combine(lba2Directory, "LBA_BKG.HQR"))) choices.Add(new("LBA2 bricks", () => GphLibrary.Lba2Bricks(lba2Directory)));
        if (lba2Directory is not null && File.Exists(System.IO.Path.Combine(lba2Directory, "SPRITES.HQR"))) choices.Add(new("LBA2 sprites", () => GphLibrary.Lba2Sprites(lba2Directory)));
        if (lba2Directory is not null && File.Exists(System.IO.Path.Combine(lba2Directory, "SPRIRAW.HQR"))) choices.Add(new("LBA2 raw sprites", () => GphLibrary.Lba2RawSprites(lba2Directory)));
        foreach (var c in choices) libraryBox.Items.Add(new ComboBoxItem { Content = c.Title, Tag = c });
        libraryBox.SelectionChanged += (_, _) => { if (libraryBox.SelectedItem is ComboBoxItem { Tag: Choice c }) OpenLibrary(c); };
        Closing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };
        Loaded += (_, _) => { if (libraryBox.Items.Count > 0) libraryBox.SelectedIndex = 0; else SetStatus("No LBA1 or LBA2 game folder is set (File > Settings)."); };
    }

    private void BuildLayout()
    {
        var root = new DockPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = "Library", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = Brushes.Gray });
        top.Children.Add(libraryBox);
        foreach (var (text, tip, handler) in new (string, string, RoutedEventHandler)[]
        {
            ("Undo", "Undo the last stroke", (_, _) => Undo()),
            ("Export PNG…", "Saves the picture as a PNG with transparency", (_, _) => Export()),
            ("Import PNG…", "Replaces the picture with a PNG (each pixel becomes the nearest palette colour; transparent pixels stay transparent)", (_, _) => Import()),
            ("New picture", "Adds a new picture (a flat tile shape in the chosen colour) at the end of the library: a new brick or sprite. Save writes it; the grid editor's New block can use it as a brick", (_, _) => NewPicture()),
            ("Clear", "Makes every pixel transparent", (_, _) => Clear()),
            ("Revert picture", "Reloads this picture from the file", (_, _) => Revert()),
        })
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += handler; top.Children.Add(b);
        }
        saveButton.Padding = new Thickness(14, 3, 14, 3); saveButton.Click += (_, _) => Save();
        top.Children.Add(saveButton);
        root.Children.Add(top);
        var bottom = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x13, 0x11)), Child = status };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var left = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(filterBox, Dock.Top);
        filterBox.Margin = new Thickness(0, 0, 0, 6);
        filterBox.TextChanged += (_, _) => { if (int.TryParse(filterBox.Text, out var n) && list.Items.OfType<Entry>().FirstOrDefault(e => e.Number >= n) is { } entry) { list.SelectedItem = entry; list.ScrollIntoView(entry); } };
        left.Children.Add(filterBox);
        left.Children.Add(list);
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is Entry e && e.Number != number) { if (ConfirmKeep()) ShowPicture(e.Number); else { /* stay */ } } };

        var right = new StackPanel { Width = 290, Margin = new Thickness(8) };
        right.Children.Add(new TextBlock { Text = "COLOUR (click to choose, right-click the picture to pick)", FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 4) });
        paletteCanvas.MouseLeftButtonDown += (_, e) => { var p = e.GetPosition(paletteCanvas); colour = (byte)(Math.Clamp((int)(p.Y / 16), 0, 15) * 16 + Math.Clamp((int)(p.X / 16), 0, 15)); UpdateSwatch(); };
        right.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = paletteCanvas, HorizontalAlignment = HorizontalAlignment.Left });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        paintTool.Foreground = eraseTool.Foreground = Foreground; paintTool.Margin = new Thickness(0, 0, 12, 0);
        tools.Children.Add(paintTool); tools.Children.Add(eraseTool);
        right.Children.Add(tools);
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        zoomRow.Children.Add(new TextBlock { Text = "Zoom", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        zoomRow.Children.Add(zoomSlider);
        zoomSlider.ValueChanged += (_, _) => Layout();
        right.Children.Add(zoomRow);
        right.Children.Add(info);
        DockPanel.SetDock(right, Dock.Right);
        root.Children.Add(right);

        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        surface.Children.Add(picture);
        surface.MouseLeftButtonDown += (_, e) => { BeginStroke(); Paint(e.GetPosition(picture), false); surface.CaptureMouse(); };
        surface.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && surface.IsMouseCaptured) Paint(e.GetPosition(picture), false); };
        surface.MouseLeftButtonUp += (_, _) => surface.ReleaseMouseCapture();
        surface.MouseRightButtonDown += (_, e) => PickColour(e.GetPosition(picture));
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = surface, Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x27, 0x25)) };
        root.Children.Add(scroll);
        Content = root;
        PreviewKeyDown += (_, e) => { if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo(); e.Handled = true; } else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { Save(); e.Handled = true; } };
    }

    private sealed record Entry(int Number, string Text)
    {
        public override string ToString() => Text;
    }

    private void SetStatus(string text) => status.Text = text;

    // ---- library ---------------------------------------------------------------------------------------------------------------------

    private bool ConfirmDiscard()
    {
        if (!dirty) return true;
        var answer = MessageBox.Show(this, "Save the changed pictures first?", Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) Save();
        return !dirty;
    }

    private bool ConfirmKeep() => true;

    private void OpenLibrary(Choice choice)
    {
        if (!ConfirmDiscard()) return;
        try
        {
            library = choice.Open();
            palette = library.LoadPalette();
            list.Items.Clear();
            foreach (var n in library.Numbers()) list.Items.Add(new Entry(n, $"{n,6}"));
            changed.Clear(); dirty = false; image = null; number = -1; undo.Clear();
            DrawPalette();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            UpdateButtons();
            SetStatus($"{choice.Title}: {list.Items.Count} pictures. Paint with the left button, right-click picks a colour, Ctrl+Z undoes.");
            Title = $"Bricks and sprites - {choice.Title}  [{library.Path}]";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't open {choice.Title}: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateButtons() => saveButton.Content = dirty ? $"Save * ({changed.Count})" : "Save";

    private void Save()
    {
        if (library is null) return;
        try
        {
            library.Save();
            dirty = false; changed.Clear();
            UpdateButtons();
            SetStatus($"Saved {System.IO.Path.GetFileName(library.Path)} (the original is kept as .bak).");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't save: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- the picture -------------------------------------------------------------------------------------------------------------------

    private void ShowPicture(int n)
    {
        if (library is null) return;
        try { image = library.Read(n); number = n; undo.Clear(); }
        catch (InvalidDataException e) { SetStatus($"Picture {n} isn't a picture: {e.Message}"); image = null; return; }
        Redraw(); Layout();
    }

    private void Layout()
    {
        if (image is null) return;
        var zoom = (int)zoomSlider.Value;
        picture.Width = image.Width * zoom; picture.Height = image.Height * zoom;
        surface.Width = picture.Width + 40; surface.Height = picture.Height + 40;
        Canvas.SetLeft(picture, 20); Canvas.SetTop(picture, 20);
    }

    private void Redraw()
    {
        if (image is null) return;
        // the picture over a checkerboard (transparent pixels show it)
        var bgra = image.ToBgra(palette);
        var flat = new byte[bgra.Length];
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                var solid = bgra[i + 3] != 0;
                var check = ((x + y) & 1) == 0 ? (byte)62 : (byte)86;
                flat[i] = solid ? bgra[i] : check; flat[i + 1] = solid ? bgra[i + 1] : check; flat[i + 2] = solid ? bgra[i + 2] : check; flat[i + 3] = 255;
            }
        bitmap = new WriteableBitmap(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, image.Width, image.Height), flat, image.Width * 4, 0);
        picture.Source = bitmap;
        info.Text = $"Picture {number}\n{image.Width} x {image.Height} pixels\nhot spot {image.OffsetX}, {image.OffsetY}\n{image.Opaque.Count(o => o)} drawn pixels\nused colours: {image.Pixels.Where((_, i) => image.Opaque[i]).Distinct().Count()}"
            + (changed.Contains(number) ? "\n(changed - not saved)" : "");
    }

    private void DrawPalette()
    {
        paletteCanvas.Children.Clear();
        var sixBit = palette.Take(768).Max() <= 63;
        for (var i = 0; i < 256; i++)
        {
            byte S(byte v) => sixBit ? (byte)Math.Min(255, v * 4) : v;
            var r = new System.Windows.Shapes.Rectangle { Width = 16, Height = 16, Fill = new SolidColorBrush(Color.FromRgb(S(palette[i * 3]), S(palette[i * 3 + 1]), S(palette[i * 3 + 2]))), IsHitTestVisible = false };
            Canvas.SetLeft(r, (i % 16) * 16); Canvas.SetTop(r, (i / 16) * 16);
            paletteCanvas.Children.Add(r);
        }
        swatch = new System.Windows.Shapes.Rectangle { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        paletteCanvas.Children.Add(swatch);
        UpdateSwatch();
    }

    private void UpdateSwatch()
    {
        if (swatch is null) return;
        Canvas.SetLeft(swatch, (colour % 16) * 16); Canvas.SetTop(swatch, (colour / 16) * 16);
    }

    private (int X, int Y)? PixelAt(Point p)
    {
        if (image is null) return null;
        var zoom = zoomSlider.Value;
        int x = (int)Math.Floor(p.X / zoom), y = (int)Math.Floor(p.Y / zoom);
        return x >= 0 && y >= 0 && x < image.Width && y < image.Height ? (x, y) : null;
    }

    private void BeginStroke()
    {
        if (image is null) return;
        undo.Push(((byte[])image.Pixels.Clone(), (bool[])image.Opaque.Clone()));
    }

    private void Paint(Point p, bool _)
    {
        if (image is null || PixelAt(p) is not { } px) return;
        var erase = eraseTool.IsChecked == true || Keyboard.Modifiers == ModifierKeys.Shift;
        image.Set(px.X, px.Y, colour, !erase);
        changed.Add(number); dirty = true;
        Commit();
    }

    private void PickColour(Point p)
    {
        if (image is null || PixelAt(p) is not { } px || !image.Opaque[px.Y * image.Width + px.X]) return;
        colour = image.Pixels[px.Y * image.Width + px.X]; UpdateSwatch();
        SetStatus($"Colour {colour} picked.");
    }

    private void Commit()
    {
        if (image is null || library is null) return;
        library.Replace(number, image);
        Redraw(); UpdateButtons();
    }

    private void Undo()
    {
        if (image is null || undo.Count == 0) return;
        var (pixels, opaque) = undo.Pop();
        pixels.CopyTo(image.Pixels, 0); opaque.CopyTo(image.Opaque, 0);
        Commit();
    }

    // A new picture at the end of the library: for bricks the top face of a block (the 48-wide isometric diamond), for sprites a square, in the chosen colour.
    private void NewPicture()
    {
        if (library is null) return;
        try
        {
            var brick = library.Title.Contains("brick");
            var fresh = brick ? new GphImage(48, 38) : new GphImage(24, 24);
            if (brick)
            {
                for (var y = 0; y < 25; y++)
                {
                    var half = 24 - Math.Abs(12 - y) * 2;
                    for (var x = 24 - half; x < 24 + half; x++) fresh.Set(x, y + 6, colour);
                }
                fresh.OffsetX = 0; fresh.OffsetY = 0;
            }
            else for (var y = 4; y < 20; y++) for (var x = 4; x < 20; x++) fresh.Set(x, y, colour);
            var n = library.Add(fresh);
            list.Items.Add(new Entry(n, $"{n,6}"));
            dirty = true; changed.Add(n);
            list.SelectedIndex = list.Items.Count - 1; list.ScrollIntoView(list.SelectedItem);
            UpdateButtons();
            SetStatus($"Picture {n} added ({fresh.Width} x {fresh.Height}). Paint it, Save, then in the grid editor make a New block with brick {n}.");
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { SetStatus("Couldn't add a picture: " + e.Message); }
    }

    private void Clear()
    {
        if (image is null) return;
        BeginStroke();
        Array.Clear(image.Opaque);
        changed.Add(number); dirty = true;
        Commit();
    }

    private void Revert()
    {
        if (library is null || number < 0) return;
        // the library holds the edited picture in memory; reloading the file reads the saved one
        try
        {
            var fresh = library.GetType() == typeof(GphLibrary) ? Reload() : null;
            if (fresh is null) return;
            library = fresh; changed.Remove(number); dirty = changed.Count > 0;
            ShowPicture(number); UpdateButtons();
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { SetStatus(e.Message); }
    }

    // Reads the same library again from disk, keeping the pictures changed so far except the current one.
    private GphLibrary? Reload()
    {
        if (libraryBox.SelectedItem is not ComboBoxItem { Tag: Choice c }) return null;
        var edited = changed.Where(n => n != number).ToDictionary(n => n, n => library!.Read(n));
        var fresh = c.Open();
        foreach (var (n, img) in edited) fresh.Replace(n, img);
        return fresh;
    }

    // ---- PNG ----------------------------------------------------------------------------------------------------------------------------

    private void Export()
    {
        if (image is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG image|*.png", FileName = $"picture{number}.png" };
        if (dialog.ShowDialog(this) != true) return;
        var bgra = image.ToBgra(palette);
        var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, bgra, image.Width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var stream = File.Create(dialog.FileName)) encoder.Save(stream);
        SetStatus($"Exported picture {number} to {dialog.FileName}.");
    }

    private void Import()
    {
        if (image is null || library is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.bmp;*.gif;*.jpg" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(dialog.FileName), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            if (source.PixelWidth > 255 || source.PixelHeight > 255) { MessageBox.Show(this, "A game picture can be at most 255 x 255 pixels.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var bgra = new byte[source.PixelWidth * source.PixelHeight * 4];
            source.CopyPixels(bgra, source.PixelWidth * 4, 0);
            var sizeChanged = source.PixelWidth != image.Width || source.PixelHeight != image.Height;
            BeginStroke();
            var fresh = GphImage.FromBgra(bgra, source.PixelWidth, source.PixelHeight, palette, image.OffsetX, image.OffsetY, library.Raw);
            image = fresh; changed.Add(number); dirty = true;
            Commit();
            Layout();
            SetStatus(sizeChanged ? $"Imported {System.IO.Path.GetFileName(dialog.FileName)}. The picture's size changed: a brick that is not the size of the others can look wrong in the game's map." : $"Imported {System.IO.Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't import that picture: {e.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
