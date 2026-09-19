using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

// usage: UiSmoke <path-to-xaml> [name-of-element-to-require ...]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var xaml = File.ReadAllText(args[0]);

        // The compiled class and its event handlers don't exist here: strip them so the
        // rest (resources, styles, control templates, bindings) is parsed for real.
        xaml = Regex.Replace(xaml, @"\sx:Class=""[^""]*""", "");
        xaml = Regex.Replace(xaml, @"\s(Click|Checked|Unchecked|TextChanged|PreviewKeyDown|KeyDown|LostKeyboardFocus|PreviewMouseLeftButtonUp|MouseDoubleClick|MouseLeftButtonDown|MouseLeftButtonUp|Loaded|Closing|Closed|SelectionChanged|ValueChanged)=""[^""]*""", "");

        try
        {
            var app = new Application();
            var root = XamlReader.Parse(xaml);
            if (root is not Window window) { Console.WriteLine($"root is {root.GetType().Name}, expected Window"); return 1; }

            var missing = args.Skip(1).Where(n => window.FindName(n) is null).ToList();
            if (missing.Count > 0) { Console.WriteLine("missing named elements: " + string.Join(", ", missing)); return 1; }

            // Force templates/styles to actually apply: measure and arrange the tree.
            window.Width = 1180; window.Height = 760;
            window.Show();
            window.UpdateLayout();
            var tabs = new[] { "ViewLifeButton", "ViewTrackButton", "DisassemblyTextBox", "SaveButton", "RevertButton" }
                .Select(n => window.FindName(n) as FrameworkElement).Where(e => e is not null).ToList();
            foreach (var t in tabs) Console.WriteLine($"  {t!.Name}: {t.ActualWidth:0}x{t.ActualHeight:0}");
            var png = Environment.GetEnvironmentVariable("UISMOKE_PNG");
            if (png is not null)
            {
                if (window.FindName("ScriptTextBox") is TextBox tb) tb.Text = File.ReadAllText(Environment.GetEnvironmentVariable("UISMOKE_TEXT") ?? "");
                if (window.FindName("StatusText") is TextBlock st) { st.Text = "✓ compiles · 118 bytes · edited · unsaved edits in scene 2"; st.Foreground = System.Windows.Media.Brushes.LightGreen; }
                if (window.FindName("HintLabel") is TextBlock hl) hl.Text = "C source — edits stay in memory until you Save";
                if (window.FindName("TitleLabel") is TextBlock tl) tl.Text = "Actor 12   ·   scene 2, object 5";
                if (window.FindName("ViewLifeButton") is RadioButton rb) rb.IsChecked = true;
                window.UpdateLayout();
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(window);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(png); enc.Save(fs);
                Console.WriteLine("wrote " + png);
            }
            window.Close();
            Console.WriteLine("XAML parsed and laid out OK");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("XAML FAILED: " + e.GetType().Name + ": " + e.Message);
            if (e.InnerException is not null) Console.WriteLine("  inner: " + e.InnerException.Message);
            return 1;
        }
    }
}
