using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LBA2LevelEditor;

// Renders Assets/DummyBody.lm2 -- a real LBA2 body payload, in the same raw
// format a BODY.HQR entry decodes to -- directly through the absorbed Body
// Studio code (LbaBodyStudio.Body/Renderer), entirely independent of the
// native game engine and BODY.HQR. Used only for ActorAttributesWindow's own
// preview panel while an actor is still a placeholder (RendererLibraryApi.
// IsActorPlaceholder) with no real body chosen yet: showing something here
// never requires installing anything into the user's actual game files, the
// way giving the actor this body for real (in the 3D world view) would.
internal static class DummyBodyPreview
{
    private static LbaBodyStudio.Body? cachedBody;
    private static System.Drawing.Color[]? cachedPalette;
    private static bool loadFailed;

    private static bool TryLoad()
    {
        if (cachedBody is not null && cachedPalette is not null) return true;
        if (loadFailed) return false;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "DummyBody.lm2");
            cachedBody = LbaBodyStudio.Body.Read(File.ReadAllBytes(path), 2);
            cachedPalette = LbaBodyStudio.Generator.Palette(EditorSettings.Current.GameDirectory);
            return true;
        }
        catch
        {
            // No installed game folder yet, or the palette/body couldn't be
            // read -- ActorAttributesWindow falls back to its usual "no body
            // to preview" message rather than showing a broken image.
            loadFailed = true;
            return false;
        }
    }


    private static BitmapSource? cachedMarker;

    // The dummy body on a transparent background, for marking where an
    // invisible / body-less actor is in the main views (see
    // MainWindow.AddDummyMarker). Rendered once and cached.
    public static BitmapSource? RenderMarker()
    {
        if (cachedMarker is not null) return cachedMarker;
        if (!TryLoad()) return null;
        const int size = 160;
        using var bitmap = LbaBodyStudio.Renderer.Render(cachedBody!, cachedPalette!, size, size, 0.6f, wire: false);
        using var argb = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(argb)) g.DrawImage(bitmap, 0, 0, size, size);
        var data = argb.LockBits(new System.Drawing.Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * size];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        argb.UnlockBits(data);
        // Renderer.Render clears to this colour; drop it (and near-matches
        // from antialiasing against it) so only the body remains.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (Math.Abs(pixels[i] - 39) < 6 && Math.Abs(pixels[i + 1] - 30) < 6 && Math.Abs(pixels[i + 2] - 25) < 6) pixels[i + 3] = 0;
        }
        var source = BitmapSource.Create(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, data.Stride);
        source.Freeze();
        cachedMarker = source;
        return source;
    }
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    public static BitmapSource? Render(int width, int height, float yawRadians)
    {
        if (!TryLoad() || width <= 0 || height <= 0) return null;
        using var bitmap = LbaBodyStudio.Renderer.Render(cachedBody!, cachedPalette!, width, height, yawRadians, wire: false);
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
}
