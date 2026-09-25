using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LbaBodyStudio;

// A small stand-in for WinForms' NumericUpDown (a bounded integer field with a spinner): nothing like it
// existed in this app before -- every other numeric input here (GridEditorWindow's own "layer" control) is
// a genuinely different shape, a live pick-a-value-along-a-range slider, not "type or nudge a precise
// number" -- so this is new, not a duplicate of something already available. Used by BodyStudioWindow and
// AnimationStudioWindow (the WPF port of Body Studio / Animation Studio), matching WinForms' own
// NumericUpDown behaviour closely enough that porting each field over was a direct swap.
internal sealed class NumberBox : Grid
{
    public readonly int Minimum, Maximum;
    private readonly TextBox box = new() { VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 4, 2), BorderThickness = new Thickness(1) };
    private int currentValue;

    public event Action? ValueChanged;

    public int Value
    {
        get => currentValue;
        set
        {
            var clamped = System.Math.Clamp(value, Minimum, Maximum);
            currentValue = clamped;
            var text = clamped.ToString();
            if (box.Text != text) box.Text = text;
            ValueChanged?.Invoke();
        }
    }

    public NumberBox(int min, int max, int initial)
    {
        Minimum = min; Maximum = max;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(box, 0); SetColumnSpan(box, 2);

        var up = new RepeatButton { Content = "▲", Width = 18, Height = 11, Padding = new Thickness(0), FontSize = 7, Focusable = false };
        var down = new RepeatButton { Content = "▼", Width = 18, Height = 11, Padding = new Thickness(0), FontSize = 7, Focusable = false };
        var spin = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
        spin.Children.Add(up); spin.Children.Add(down);
        SetColumn(spin, 1);

        Children.Add(box); Children.Add(spin);
        currentValue = System.Math.Clamp(initial, min, max);
        box.Text = currentValue.ToString();

        up.Click += (_, _) => Value = currentValue + 1;
        down.Click += (_, _) => Value = currentValue - 1;
        box.LostFocus += (_, _) => { if (int.TryParse(box.Text, out var v)) Value = v; else box.Text = currentValue.ToString(); };
        box.PreviewMouseWheel += (_, e) => { Value = currentValue + (e.Delta > 0 ? 1 : -1); e.Handled = true; };
    }

    public void Theme(Brush field, Brush text, Brush border)
    {
        box.Background = field; box.Foreground = text; box.BorderBrush = border;
    }
}
