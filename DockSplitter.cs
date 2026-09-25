using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LBAAssembler;

// A drag handle that resizes one neighbouring DockGroup by adjusting its explicit Height/Width, for use
// inside a DockPanel (GridSplitter needs a Grid parent, which the DockPanel-based layout in MainWindow.xaml
// doesn't have -- see DockGroup's own comment for why). The DockPanel's remaining ("fill") child absorbs
// whatever size change this makes automatically; there is nothing on the other side of the splitter to
// adjust in turn.
internal sealed class DockSplitter : Thumb
{
    private readonly FrameworkElement target;
    private readonly bool resizesWidth;   // true: a vertical bar the user drags left/right, resizing Width. false: a horizontal bar, resizing Height.
    private readonly int sizeSign;        // +1 if dragging down/right grows the target, -1 if it shrinks it (depends which side of the target the splitter sits on)
    private const double Min = 40, Max = 900;

    public DockSplitter(FrameworkElement target, bool resizesWidth, int sizeSign)
    {
        this.target = target; this.resizesWidth = resizesWidth; this.sizeSign = sizeSign;
        SetResourceReference(BackgroundProperty, "ThemeBorderBrush");
        if (resizesWidth) { Width = 4; Cursor = System.Windows.Input.Cursors.SizeWE; HorizontalAlignment = HorizontalAlignment.Stretch; }
        else { Height = 4; Cursor = System.Windows.Input.Cursors.SizeNS; VerticalAlignment = VerticalAlignment.Stretch; }
        DragDelta += OnDragDelta;
        // Follows the target's own collapse (DockGroup.RebuildHeader, when its last item floats or closes):
        // nothing left to resize against once the target takes no space. IsVisibleChanged's NewValue is the
        // computed IsVisible bool, not the Visibility enum target.Visibility itself is set to.
        Visibility = target.Visibility;
        target.IsVisibleChanged += (_, e) => Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        var delta = (resizesWidth ? e.HorizontalChange : e.VerticalChange) * sizeSign;
        if (resizesWidth) target.Width = Math.Clamp((double.IsNaN(target.Width) ? target.ActualWidth : target.Width) + delta, Min, Max);
        else target.Height = Math.Clamp((double.IsNaN(target.Height) ? target.ActualHeight : target.Height) + delta, Min, Max);
    }
}
