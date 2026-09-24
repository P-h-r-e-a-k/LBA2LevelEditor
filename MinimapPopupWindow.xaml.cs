using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LBAAssembler;

// The minimap "popped out" into its own resizable window -- opened by double-clicking the docked minimap
// (MainWindow.xaml's own MinimapContent), closed like any other window (its own X, Alt+F4, or the owner
// closing). MainWindow keeps a single instance alive while open and pushes a fresh snapshot to it every time
// the docked minimap's own image or markers change (see MainWindow's own RefreshMinimapPopup), rather than
// this window rendering anything on its own -- it has no renderer/game-data access of its own, only what it's
// handed.
public partial class MinimapPopupWindow : Window
{
    // Raised with a click's position in the SAME pixel space as the bitmap last passed to SetImage (i.e. the
    // minimap's own full, unscrolled image) -- MainWindow reuses its own existing Minimap_MouseDown math on
    // this exactly as if the docked minimap itself had been clicked there.
    public event Action<Point>? ImageClicked;

    public MinimapPopupWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
    }

    public void SetImage(ImageSource? source) => PopupImage.Source = source;

    private void PopupContent_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PopupImage.Source is not BitmapSource bitmap) return;
        // PopupImage (Stretch="Uniform") only fills part of PopupContent's own box when their aspect ratios
        // differ -- letterboxed on whichever axis has slack. Map the click back through that same fit (same
        // formula Image itself uses to decide where to actually draw the bitmap) to the source's own pixel
        // coordinates, not just scale blindly by the container size.
        var clickInContent = e.GetPosition(PopupContent);
        var containerWidth = PopupContent.ActualWidth;
        var containerHeight = PopupContent.ActualHeight;
        if (containerWidth <= 0 || containerHeight <= 0) return;
        var scale = Math.Min(containerWidth / bitmap.PixelWidth, containerHeight / bitmap.PixelHeight);
        if (scale <= 0) return;
        var drawnWidth = bitmap.PixelWidth * scale;
        var drawnHeight = bitmap.PixelHeight * scale;
        var originX = (containerWidth - drawnWidth) / 2;
        var originY = (containerHeight - drawnHeight) / 2;
        var px = (clickInContent.X - originX) / scale;
        var py = (clickInContent.Y - originY) / scale;
        if (px < 0 || py < 0 || px >= bitmap.PixelWidth || py >= bitmap.PixelHeight) return;
        ImageClicked?.Invoke(new Point(px, py));
    }
}
