using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LBA2LevelEditor.Lba1;

// Renders LBA1 bodies (neutral pose, through the Body Studio renderer) for the map markers
// and the actor window's preview.
internal sealed class Lba1ActorImages
{
    // A body on a transparent background. The body fills 80% of the square frame's height
    // with its feet at 90% down, so its real height in world units is HeightUnits.
    public sealed record Marker(BitmapSource Image, double HeightUnits);

    internal const int MarkerSize = 128;
    // Renderer.Render clears to this colour; it is made transparent for markers.
    private static readonly (byte B, byte G, byte R) Background = (39, 30, 25);

    private readonly Func<int, byte[]?> readBody;
    private readonly int version;
    private readonly System.Drawing.Color[] palette;
    private readonly Dictionary<int, Marker?> markers = new();
    private readonly Dictionary<int, LbaBodyStudio.Body?> bodies = new();

    public Lba1ActorImages(Lba1Game game) : this(game.Palette, game.ReadBody, 1) { }

    // Bodies from any source: the 768-byte palette, a reader of BODY.HQR entries and the game (1 or 2) they are laid out for (LBA2's joined maps).
    public Lba1ActorImages(byte[] rawPalette, Func<int, byte[]?> readBody, int version)
    {
        this.readBody = readBody;
        this.version = version;
        palette = Enumerable.Range(0, 256).Select(i => System.Drawing.Color.FromArgb(rawPalette[i * 3], rawPalette[i * 3 + 1], rawPalette[i * 3 + 2])).ToArray();
    }

    public LbaBodyStudio.Body? Body(int bodyIndex)
    {
        if (bodies.TryGetValue(bodyIndex, out var cached)) return cached;
        LbaBodyStudio.Body? body = null;
        try
        {
            if (readBody(bodyIndex) is { } bytes) body = LbaBodyStudio.Body.Read(bytes, version);
        }
        catch (Exception error)
        {
            DebugLog.Log($"Lba1ActorImages: body {bodyIndex} unreadable: {error.Message}");
        }
        bodies[bodyIndex] = body;
        return body;
    }

    public Marker? GetMarker(int bodyIndex)
    {
        if (markers.TryGetValue(bodyIndex, out var cached)) return cached;
        Marker? marker = null;
        if (Body(bodyIndex) is { } body)
        {
            try
            {
                var world = body.World();
                var heightUnits = Math.Max(1, world.Max(v => v.Y) - world.Min(v => v.Y));
                using var bitmap = LbaBodyStudio.Renderer.Render(body, palette, MarkerSize, MarkerSize, 0.6f, wire: false);
                marker = new Marker(Transparent(bitmap), heightUnits);
            }
            catch (Exception error)
            {
                DebugLog.Log($"Lba1ActorImages: body {bodyIndex} didn't render: {error.Message}");
            }
        }
        markers[bodyIndex] = marker;
        return marker;
    }

    // A body in a given pose (bone frames as the animation player gives them) on a transparent background, framed exactly
    // like GetMarker so the two can be swapped while an actor animates.
    // With `light` the body is shaded as the game shades it (the actor's facing and the scene's light angles).
    public BitmapSource? RenderPosed(int bodyIndex, IReadOnlyList<(int Type, double X, double Y, double Z)> bones, float yaw, (int ActorBeta, int AlphaLight, int BetaLight)? light = null)
    {
        if (Body(bodyIndex) is not { } body) return null;
        try
        {
            var points = Lba1Pose.World(body, bones, out var matrices);
            var shading = light is { } l ? new LbaBodyStudio.Lba1Shading { BoneMatrices = matrices, ActorBeta = l.ActorBeta, AlphaLight = l.AlphaLight, BetaLight = l.BetaLight } : null;
            using var bitmap = LbaBodyStudio.Renderer.Render(body, palette, MarkerSize, MarkerSize, yaw, wire: false, pose: points, shading: shading);
            return Transparent(bitmap);
        }
        catch (Exception error)
        {
            DebugLog.Log($"Lba1ActorImages: body {bodyIndex} didn't pose: {error.Message}");
            return null;
        }
    }

    // A fully shaded render for the actor window, background opaque.
    public BitmapSource? RenderPreview(int bodyIndex, int width, int height, float yaw, System.Numerics.Vector3[]? pose = null)
    {
        if (Body(bodyIndex) is not { } body || width < 1 || height < 1) return null;
        using var bitmap = LbaBodyStudio.Renderer.Render(body, palette, width, height, yaw, wire: false, pose: pose, background: LbaBodyStudio.Renderer.ViewBackground, gridLine: LbaBodyStudio.Renderer.ViewGrid);
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    internal static BitmapSource Transparent(System.Drawing.Bitmap bitmap)
    {
        using var argb = new System.Drawing.Bitmap(bitmap.Width, bitmap.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(argb)) g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
        var data = argb.LockBits(new System.Drawing.Rectangle(0, 0, argb.Width, argb.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * argb.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        var stride = data.Stride;
        argb.UnlockBits(data);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var isBackground = Math.Abs(pixels[i] - Background.B) < 6 && Math.Abs(pixels[i + 1] - Background.G) < 6 && Math.Abs(pixels[i + 2] - Background.R) < 6;
            var isGrid = Math.Abs(pixels[i] - 64) < 6 && Math.Abs(pixels[i + 1] - 52) < 6 && Math.Abs(pixels[i + 2] - 44) < 6;
            if (isBackground || isGrid) pixels[i + 3] = 0;
        }
        var source = BitmapSource.Create(argb.Width, argb.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }
}
