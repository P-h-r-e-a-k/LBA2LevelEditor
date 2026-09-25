using System.Windows;
using System.Windows.Media;

namespace LBAAssembler;

// A scene trigger box ("zone") with its 8 corners already projected into the
// coordinate space of whichever view produced it (framebuffer pixels outdoors,
// canvas pixels indoors). Corner i has bit 0 = X1 side, bit 1 = Y1 side, bit 2 = Z1 side.
internal sealed record ProjectedZone(int Type, int Num, Point[] Corners, ZoneRef? Ref = null);

// Everything the interior view draws on top of the stitched canvas besides
// actors: actor patrol routes and zones, in canvas pixels.
internal sealed record InteriorOverlay(List<(int ActorIndex, List<Point> Points)> Routes, List<ProjectedZone> Zones)
{
    public static InteriorOverlay Empty { get; } = new(new(), new());
}

// How zones look: one colour per type (docs/ZONES.md), and the box geometry.
internal static class ZoneStyle
{
    public static readonly string[] TypeNames =
        { "cube change", "camera", "scenaric", "grid", "giver", "message", "ladder", "escalator", "hit", "rail" };

    // The Light theme's own original palette -- ApplyTheme replaces this array wholesale for every other
    // theme (ThemeManager.cs calls it on every switch, including once at startup); not a WPF resource since
    // this is drawn onto a canvas from plain C# (MainWindow's zone overlay, the Zone types list swatches),
    // not styled through a Style/ControlTemplate the way DynamicResource-bound chrome is.
    private static Color[] TypeColors =
    {
        Color.FromRgb(0xF5, 0xA6, 0x23), // cube change
        Color.FromRgb(0x5B, 0xC0, 0xEB), // camera
        Color.FromRgb(0xE0, 0x40, 0xFB), // scenaric
        Color.FromRgb(0x9E, 0x9E, 0x9E), // grid
        Color.FromRgb(0xFF, 0xD5, 0x4F), // giver
        Color.FromRgb(0x66, 0xBB, 0x6A), // message
        Color.FromRgb(0xA1, 0x88, 0x7F), // ladder
        Color.FromRgb(0x26, 0xA6, 0x9A), // escalator
        Color.FromRgb(0xEF, 0x53, 0x50), // hit
        Color.FromRgb(0x7E, 0x57, 0xC2), // rail
    };

    // The colour-blind-safe theme's own palette (Okabe-Ito hues plus grey/brown/charcoal neutrals to
    // stretch 8 safe hues to 10 categories -- see Themes/ColorBlindSafe.xaml's own comment). High-contrast
    // variants get a brightened/darkened version of the ORIGINAL palette (still hue-coded, since those
    // themes are solving a contrast problem, not a colour-confusion one) rather than their own set.
    private static readonly Color[] ColorBlindSafeColors =
    {
        Color.FromRgb(0xE6, 0x9F, 0x00), // cube change -- orange
        Color.FromRgb(0x56, 0xB4, 0xE9), // camera -- sky blue
        Color.FromRgb(0xCC, 0x79, 0xA7), // scenaric -- reddish purple
        Color.FromRgb(0x99, 0x99, 0x99), // grid -- grey
        Color.FromRgb(0xF0, 0xE4, 0x42), // giver -- yellow
        Color.FromRgb(0x00, 0x9E, 0x73), // message -- bluish green
        Color.FromRgb(0x8A, 0x6D, 0x3B), // ladder -- brown
        Color.FromRgb(0x00, 0x72, 0xB2), // escalator -- blue
        Color.FromRgb(0xD5, 0x5E, 0x00), // hit -- vermillion
        Color.FromRgb(0x3D, 0x3D, 0x3D), // rail -- charcoal
    };

    private static readonly Color[] HighContrastDarkColors =
    {
        Color.FromRgb(0xFF, 0xB4, 0x00), Color.FromRgb(0x33, 0xCF, 0xFF), Color.FromRgb(0xFF, 0x33, 0xFF), Color.FromRgb(0xCC, 0xCC, 0xCC),
        Color.FromRgb(0xFF, 0xEE, 0x55), Color.FromRgb(0x33, 0xFF, 0x66), Color.FromRgb(0xD9, 0xB8, 0xAC), Color.FromRgb(0x00, 0xE6, 0xCC),
        Color.FromRgb(0xFF, 0x44, 0x44), Color.FromRgb(0xB3, 0x88, 0xFF),
    };

    private static readonly Color[] HighContrastLightColors =
    {
        Color.FromRgb(0xC9, 0x7A, 0x00), Color.FromRgb(0x00, 0x80, 0xB3), Color.FromRgb(0xB3, 0x00, 0xB3), Color.FromRgb(0x59, 0x59, 0x59),
        Color.FromRgb(0xB3, 0x8F, 0x00), Color.FromRgb(0x22, 0x7A, 0x33), Color.FromRgb(0x6B, 0x4F, 0x42), Color.FromRgb(0x00, 0x7A, 0x6E),
        Color.FromRgb(0xCC, 0x1F, 0x1B), Color.FromRgb(0x5A, 0x32, 0xA3),
    };

    private static readonly Color[] LightColors = (Color[])TypeColors.Clone();

    public static void ApplyTheme(AppTheme theme) => TypeColors = theme switch
    {
        AppTheme.ColorBlindSafe => ColorBlindSafeColors,
        AppTheme.HighContrastDark => HighContrastDarkColors,
        AppTheme.HighContrastLight => HighContrastLightColors,
        _ => LightColors,   // Light and Dark share the original, already-vivid palette
    };

    public const int TypeCount = 10;

    public static Color ColorOf(int type) => TypeColors[(uint)type < TypeColors.Length ? type : 3];

    public static string NameOf(int type) => (uint)type < TypeNames.Length ? TypeNames[type] : $"type {type}";

    // The 12 edges of the box, as pairs of corner indices (corners differ in exactly one bit).
    public static readonly (int A, int B)[] Edges =
    {
        (0, 1), (2, 3), (4, 5), (6, 7),   // along X
        (0, 2), (1, 3), (4, 6), (5, 7),   // along Y
        (0, 4), (1, 5), (2, 6), (3, 7),   // along Z
    };

    // World-space corners of the box, in the order ProjectedZone.Corners uses.
    public static (int X, int Y, int Z)[] Corners(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        int minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
        int minY = Math.Min(y0, y1), maxY = Math.Max(y0, y1);
        int minZ = Math.Min(z0, z1), maxZ = Math.Max(z0, z1);
        var corners = new (int, int, int)[8];
        for (var i = 0; i < 8; i++)
            corners[i] = ((i & 1) != 0 ? maxX : minX, (i & 2) != 0 ? maxY : minY, (i & 4) != 0 ? maxZ : minZ);
        return corners;
    }

    // Draws the wireframe of `zone` (points already in the target space, scaled by
    // `map`) into a geometry: one figure per edge. Returns null if nothing is drawable.
    public static Geometry? Wireframe(ProjectedZone zone, Func<Point, Point> map)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var (a, b) in Edges)
            {
                ctx.BeginFigure(map(zone.Corners[a]), false, false);
                ctx.LineTo(map(zone.Corners[b]), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }
}
