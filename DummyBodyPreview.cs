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
