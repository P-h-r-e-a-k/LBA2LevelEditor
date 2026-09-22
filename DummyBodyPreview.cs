using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LBAAssembler;

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

    // Forget any cached result (the game folders changed, so the palette source may have too).
    public static void Reset()
    {
        cachedBody = null;
        cachedPalette = null;
        cachedMarker = null;
        loadFailed = false;
    }

    private static bool TryLoad()
    {
        if (cachedBody is not null && cachedPalette is not null) return true;
        if (loadFailed) return false;
        try
        {
            // The body ships inside the exe. Its colours come from a game palette (RESS.HQR entry 0
            // is the same layout in both games), from whichever game folder is set.
            var folder = new[] { EditorSettings.Current.GameDirectory, EditorSettings.Current.Lba1Directory }
                .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) && File.Exists(Path.Combine(f, "RESS.HQR")));
            if (folder is null) return false; // no game folder yet: try again once one is set
            using var stream = typeof(DummyBodyPreview).Assembly.GetManifestResourceStream("DummyBody.lm2")
                ?? throw new FileNotFoundException("DummyBody.lm2 is not embedded.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            cachedBody = LbaBodyStudio.Body.Read(bytes.ToArray(), 2);
            cachedPalette = LbaBodyStudio.Generator.Palette(folder);
            return true;
        }
        catch
        {
            // The palette/body couldn't be read -- ActorAttributesWindow
            // falls back to its usual "no body to preview" message rather
            // than showing a broken image.
            loadFailed = true;
            return false;
        }
    }


    private static BitmapSource? cachedMarker;
    private static double markerHeightUnits;

    // The dummy body on a transparent background, for marking where an invisible / body-less actor is in the main views (see MainWindow.AddDummyMarker). It is
    // rendered exactly as every other actor's body is (Lba1ActorImages: the same square frame, 80% high with its feet 90% down, the key background and the
    // renderer's ground grid line made transparent), so it can be drawn at the body's real size. Rendered once and cached.
    public static BitmapSource? RenderMarker()
    {
        if (cachedMarker is not null) return cachedMarker;
        if (!TryLoad()) return null;
        var world = cachedBody!.World();
        markerHeightUnits = Math.Max(1, world.Max(v => v.Y) - world.Min(v => v.Y));
        using var bitmap = LbaBodyStudio.Renderer.Render(cachedBody!, cachedPalette!, Lba1.Lba1ActorImages.MarkerSize, Lba1.Lba1ActorImages.MarkerSize, 0.6f, wire: false);
        cachedMarker = Lba1.Lba1ActorImages.Transparent(bitmap);
        return cachedMarker;
    }

    // Half the height, in view pixels at zoom 1, the dummy body has at its real size (the same scale as every other body marker: 15 pixels per 256 units, at
    // least 14 and at most 260 pixels tall); 16 while the body can't be loaded.
    public static int MarkerHalfHeight
    {
        get
        {
            RenderMarker();
            return markerHeightUnits <= 0 ? 16 : (int)(Math.Clamp(markerHeightUnits * 15 / 256, 14, 260) / 2);
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
