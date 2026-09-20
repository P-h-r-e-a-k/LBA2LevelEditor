namespace System.Windows
{
    // Just enough of WPF's Point for Lba1GridRenderer.Project to compile in the console harness.
    public readonly record struct Point(double X, double Y);
}
