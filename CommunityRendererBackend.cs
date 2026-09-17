using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace LBA2LevelEditor;

internal sealed class CommunityRendererBackend
{
    private readonly string enginePath;
    private string gameDirectory;
    private readonly string saveDirectory;
    private readonly string outputDirectory;
    private readonly string referenceDirectory;
    private readonly string rendererLibraryPath;
    private readonly object directRenderLock = new();
    private string? directIsland;
    private bool directSession;
    private string directFailure = "none";
    public RendererLibraryApi? RendererLibrary { get; }

    public CommunityRendererBackend(string gameDirectory)
    {
        var editorRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var vendoredRoot = Path.Combine(editorRoot, "native", "lba2-classic-community");
        var siblingRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "lba2-classic-community"));
        var vendoredLibrary = Path.Combine(vendoredRoot, "out", "build", "windows_ucrt64", "SOURCES", "3DEXT", "liblba2_renderer.dll");
        var siblingLibrary = Path.Combine(siblingRoot, "out", "build", "windows_ucrt64", "SOURCES", "3DEXT", "liblba2_renderer.dll");
        var repoRoot = File.Exists(vendoredLibrary) ? vendoredRoot : File.Exists(siblingLibrary) ? siblingRoot : vendoredRoot;
        enginePath = Path.Combine(repoRoot, "out", "build", "windows_ucrt64", "SOURCES", "lba2cc.exe");
        referenceDirectory = Path.Combine(repoRoot, "out", "named-probes");
        rendererLibraryPath = Path.Combine(repoRoot, "out", "build", "windows_ucrt64", "SOURCES", "3DEXT", "liblba2_renderer.dll");
        this.gameDirectory = gameDirectory;
        saveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Twinsen", "LBA2", "save");
        outputDirectory = Path.Combine(Path.GetTempPath(), "LBA2LevelEditor", "native-renders");
        try { RendererLibrary = new RendererLibraryApi(rendererLibraryPath); } catch { RendererLibrary = null; }
    }

    // Called when the user changes the game folder in Settings. Tears down
    // the active native session (if any) so the next render re-initializes
    // and re-chdirs against the new path instead of continuing to read from
    // the old one.
    public void SetGameDirectory(string path)
    {
        if (string.Equals(gameDirectory, path, StringComparison.OrdinalIgnoreCase)) return;
        ShutdownDirectRenderer();
        gameDirectory = path;
    }

    public bool IsAvailable => File.Exists(enginePath) && Directory.Exists(gameDirectory);
    public bool RendererLibraryAvailable => File.Exists(rendererLibraryPath);
    public string RendererLibraryPath => rendererLibraryPath;
    public bool DirectRendererLoaded => RendererLibrary?.IsLoaded == true;
    public bool DirectRendererReady => RendererLibrary?.IsRendererReady == true;

    // afterRenderBeforeUnlock runs (if given) immediately after a successful
    // RenderFrame(), still inside directRenderLock -- i.e. before any other
    // thread can call SetViewTarget/SetCamera/RenderFrame again and move the
    // native camera state (LongWorldRotatePoint's MatriceWorld, X0/Y0/Z0,
    // etc.) out from under it. Used by the actor-marker overlay to compute
    // lba2_renderer_project_point() results for the exact camera that
    // produced the frame it's about to be drawn on top of, instead of
    // reprojecting later from the UI thread where a newer in-flight render
    // (from rapid dragging) could have already changed that shared state --
    // the visible symptom of that race was actor markers "swimming" a few
    // pixels out of sync with the terrain while panning.
    public BitmapSource? RenderIslandDirect(string islandName, byte[] paletteBytes, int worldX, int worldY, int worldZ, int alpha = 240, int beta = -256, int gamma = 0, int distance = 30000, Action? afterRenderBeforeUnlock = null, int wideRadiusCubes = 0)
    {
        if (RendererLibrary is null || !RendererLibrary.IsRendererReady) { directFailure = "renderer DLL unavailable"; return null; }
        lock (directRenderLock)
        {
            var baseName = islandName.ToLowerInvariant();
            if (!directSession)
            {
                if (!RendererLibrary.SetDataRoot(gameDirectory)) { directFailure = "set data root failed"; return null; }
                if (!RendererLibrary.Initialize()) { directFailure = "native initialize failed"; return null; }
                directSession = true;
            }
            if (!string.Equals(directIsland, baseName, StringComparison.OrdinalIgnoreCase))
            {
                if (RendererLibrary.LoadIsland(baseName) == 0) { directFailure = $"load island failed: {baseName}"; return null; }
                directIsland = baseName;
            }
            if (RendererLibrary.SetViewTarget(worldX, worldY, worldZ) == 0) { directFailure = $"set view target failed: {baseName} {worldX},{worldY},{worldZ}"; return null; }
            RendererLibrary.SetCamera(alpha, beta, gamma, distance);
            // wideRadiusCubes>0 loads and draws neighboring cubes into the
            // same frame (AffGrilleExtWide) instead of just the one under
            // the camera target -- see its own comment for why that doesn't
            // need the terrain/decor/actor arrays resized. Reserved for
            // zoomed-out views where a single cube's terrain would otherwise
            // visibly run out before the horizon does; the extra cube loads
            // cost real time (roughly (2*radius+1)^2 vs. 1 per frame), so
            // callers should only ask for it at distances that need it.
            var renderOk = wideRadiusCubes > 0 ? RendererLibrary.RenderFrameWide(wideRadiusCubes) : RendererLibrary.RenderFrame();
            if (renderOk == 0) { directFailure = "native render returned failure"; return null; }
            afterRenderBeforeUnlock?.Invoke();
            var pointer = RendererLibrary.GetFramebuffer(out var width, out var height, out var pitch);
            if (pointer == IntPtr.Zero || width <= 0 || height <= 0) { directFailure = $"framebuffer invalid: {width}x{height}, {pitch}"; return null; }
            var pixels = new byte[width * height];
            for (var row = 0; row < height; row++) Marshal.Copy(pointer + row * pitch, pixels, row * width, width);
            if (!pixels.Any(value => value != 0)) { directFailure = "native framebuffer contains only zero indices"; return null; }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8, CreatePalette(paletteBytes), pixels, width);
            bitmap.Freeze();
            return bitmap;
        }
    }

    // For the actor-attributes editor's rotating body preview. Shares
    // directRenderLock with RenderIslandDirect/RenderIslandTopDown above --
    // without it, a preview tick landing mid-frame of an in-flight main-view
    // render would touch the same native camera/framebuffer state from two
    // threads at once. Returns null if the body doesn't resolve to anything
    // drawable (e.g. NO_BODY) or the renderer isn't ready; the caller should
    // show a fallback message rather than a stale frame in that case.
    public BitmapSource? RenderBodyPreview(int genBody, int genAnim, int cameraBeta, byte[] paletteBytes)
    {
        if (RendererLibrary is null || !RendererLibrary.IsRendererReady) return null;
        lock (directRenderLock)
        {
            if (!RendererLibrary.RenderBodyPreview(genBody, genAnim, cameraBeta)) return null;
            var pointer = RendererLibrary.GetFramebuffer(out var width, out var height, out var pitch);
            if (pointer == IntPtr.Zero || width <= 0 || height <= 0) return null;
            var pixels = new byte[width * height];
            for (var row = 0; row < height; row++) Marshal.Copy(pointer + row * pitch, pixels, row * width, width);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8, CreatePalette(paletteBytes), pixels, width);
            bitmap.Freeze();
            return bitmap;
        }
    }

    // Alpha=+1023 (of the engine's 4096-per-turn angle unit -- COMMON.H's
    // MAX_ANGLE) orbits the follow-camera to within one unit of directly
    // overhead; beta/gamma control yaw/roll and stay at 0 so north stays
    // "up" and there's no roll to correct for. The sign matters a lot more
    // than it looks: alpha is a polar elevation for the camera's *position*
    // around the target (SetFollowCamera/CAMERA.CPP), not a look-direction
    // angle, so -1023 doesn't mirror +1023 the way it would for a pure tilt
    // -- it orbits the camera to the opposite pole, i.e. *underground*
    // looking up through the terrain from below. That's not a subtle
    // artifact: real rendered coverage collapses to under 1% of the frame
    // (confirmed by sweeping alpha and counting non-background pixels,
    // 49% at +1023 vs. <1% at -1023 with everything else identical), so a
    // sign flip here silently reintroduces an almost-empty minimap, not a
    // visibly-wrong one.
    //
    // At Distance=50000 the cube exactly fills the square screen region
    // [124,44]-[516,436] of the fixed 640x480 framebuffer -- centered on the
    // frame's own center (320,240) with no perspective skew, confirmed by
    // projecting a cube's four corners individually
    // (lba2_renderer_project_point) and finding world X maps straight to
    // screen X and world Z inversely and linearly to screen Y, with no cross
    // term. That match to a plain axis-aligned crop (rather than a
    // quadrilateral needing a perspective warp) is what makes stitching
    // per-cube snapshots into one image tractable at all; it isn't
    // guaranteed by the math in general, only verified empirically for this
    // specific angle/distance pair, so changing either constant needs
    // re-verifying against a fresh corner projection.
    private const int TopDownAlpha = 1023;
    private const int TopDownDistance = 50000;

    // The exact calibrated cube bounds are [124,44]-[516,436] (392x392, see
    // the corner-projection note above), but cropping to precisely that
    // leaves a visible seam of background color between adjacent cube
    // tiles: the terrain rasterizer only reliably draws within roughly a
    // 185px radius of frame center at this distance (measured directly --
    // walking outward from center on all four sides across 8 different
    // cubes and finding where each row/column turns solidly into
    // background color; the true per-cube edge is at radius 196, so
    // anywhere from a handful up to ~14 px past that measured radius is
    // simply never drawn, apparently a clip-cone rounding effect rather
    // than the corner projection being wrong). A first attempt at fixing
    // this by overscanning (cropping *wider* than one cube, on the theory
    // that a neighboring cube's own overscanned tile would paint over the
    // gap) made it worse: every cube's render is independent and centered
    // on itself, so there's no actual neighboring content anywhere in a
    // single cube's own framebuffer to spill over -- overscanning just
    // captured proportionally *more* of the same background. Cropping
    // *tighter* than one cube instead, to a radius safely inside what's
    // reliably drawn everywhere, and stretching that up to fill the same
    // output tile, keeps every sampled pixel real; the corresponding
    // ~7% per-tile zoom-in is not worth correcting for a minimap.
    private const int TopDownSafeRadius = 180;
    private const int TopDownCropX0 = 320 - TopDownSafeRadius, TopDownCropY0 = 240 - TopDownSafeRadius, TopDownCropSize = TopDownSafeRadius * 2;
    private const int TopDownTileSize = 256;

    // Renders a full island top-down, cube by cube, through the community
    // engine's real terrain/texture/lighting pipeline (the same one the main
    // 3D view uses) instead of TopDownMapRenderer's from-scratch CPU
    // rasterizer -- so the minimap actually reflects what the "main
    // rendering engine" would show from above, texture quirks and all,
    // rather than a separate reimplementation that can drift out of sync
    // with it. The renderer only ever has one cube's terrain loaded at a
    // time (this session's own "single area cube" limitation, noted
    // elsewhere), so there's no single wide-shot alternative to stitching --
    // presentCubes lists which of the island's cubes actually have terrain,
    // one lba2_renderer_render_frame() call happens per entry.
    //
    // The palette-indexed framebuffer can't be smoothly downscaled (there's
    // no such thing as "halfway between palette index 12 and 40" -- blending
    // them like RGB would produce a wrong color, not an intermediate one),
    // so each cube's 392x392 crop is nearest-neighbor sampled down to a
    // TopDownTileSize tile; the result reads a little more blocky than the
    // old bilinear-filtered renderer but is honest about being the same
    // point-sampled palette data the 3D view itself draws with.
    public BitmapSource? RenderIslandTopDown(string islandName, byte[] paletteBytes, IReadOnlyList<(int CubeX, int CubeY)> presentCubes, int minCubeX, int minCubeY, int cubeSpanX, int cubeSpanY)
    {
        if (RendererLibrary is null || !RendererLibrary.IsRendererReady || presentCubes.Count == 0) { directFailure = "renderer DLL unavailable"; return null; }
        lock (directRenderLock)
        {
            var baseName = islandName.ToLowerInvariant();
            if (!directSession)
            {
                if (!RendererLibrary.SetDataRoot(gameDirectory)) { directFailure = "set data root failed"; return null; }
                if (!RendererLibrary.Initialize()) { directFailure = "native initialize failed"; return null; }
                directSession = true;
            }
            if (!string.Equals(directIsland, baseName, StringComparison.OrdinalIgnoreCase))
            {
                if (RendererLibrary.LoadIsland(baseName) == 0) { directFailure = $"load island failed: {baseName}"; return null; }
                directIsland = baseName;
            }
            RendererLibrary.SetDrawSky(false);

            var masterWidth = cubeSpanX * TopDownTileSize;
            var masterHeight = cubeSpanY * TopDownTileSize;
            var master = new byte[masterWidth * masterHeight];
            var cropLand = new byte[TopDownCropSize * TopDownCropSize];
            var cropSea = new byte[TopDownCropSize * TopDownCropSize];

            foreach (var (cubeX, cubeY) in presentCubes)
            {
                var worldX = cubeX * 32768 + 16384;
                var worldZ = cubeY * 32768 + 16384;
                if (RendererLibrary.SetViewTarget(worldX, 3000, worldZ) == 0) continue;
                RendererLibrary.SetCamera(TopDownAlpha, 0, 0, TopDownDistance);

                // Rendered twice and merged rather than once with sea simply
                // off: see lba2_renderer_set_draw_sea's own comment -- from
                // this straight-down camera, DrawOneSea's flat sea plane can
                // sort as nearer than elevated terrain it should sit behind,
                // painting sea color over real ground (the cause of tiles
                // that looked "lost"/out of order once the crop-seam fix
                // made misrendered tiles obvious instead of blending into
                // the seam noise). But a cube that's genuinely all/mostly
                // sea has nothing else to draw once sea is off, so a single
                // sea-off render turns *those* tiles into holes instead --
                // an earlier fix that just turned sea off unconditionally
                // traded one bug for the other. Taking the sea-off pixels
                // wherever they drew anything, and falling back to the
                // sea-on pixels only where sea-off left true background,
                // gets correct land *and* correct open water.
                RendererLibrary.SetDrawSea(false);
                if (RendererLibrary.RenderFrame() == 0) continue;
                var pointer = RendererLibrary.GetFramebuffer(out var fbWidth, out var fbHeight, out var pitch);
                if (pointer == IntPtr.Zero || fbWidth < TopDownCropX0 + TopDownCropSize || fbHeight < TopDownCropY0 + TopDownCropSize) continue;
                for (var row = 0; row < TopDownCropSize; row++)
                    Marshal.Copy(pointer + (TopDownCropY0 + row) * pitch + TopDownCropX0, cropLand, row * TopDownCropSize, TopDownCropSize);
                // The screen-clear color (ClsTerrainZBuf's SetClearColor(FogCoul))
                // is each island's own ambience fog index, not a fixed palette
                // slot -- sampled fresh from a frame corner, safely outside the
                // centered crop region, instead of hardcoding whatever index one
                // island happened to use.
                var backgroundIndex = Marshal.ReadByte(pointer);

                RendererLibrary.SetDrawSea(true);
                if (RendererLibrary.RenderFrame() == 0) continue;
                pointer = RendererLibrary.GetFramebuffer(out fbWidth, out fbHeight, out pitch);
                if (pointer == IntPtr.Zero || fbWidth < TopDownCropX0 + TopDownCropSize || fbHeight < TopDownCropY0 + TopDownCropSize) continue;
                for (var row = 0; row < TopDownCropSize; row++)
                    Marshal.Copy(pointer + (TopDownCropY0 + row) * pitch + TopDownCropX0, cropSea, row * TopDownCropSize, TopDownCropSize);

                var tileOffsetX = (cubeX - minCubeX) * TopDownTileSize;
                var tileOffsetY = (cubeY - minCubeY) * TopDownTileSize;
                // Defensive: a presentCubes entry outside [minCubeX,minCubeX+cubeSpanX)
                // x [minCubeY,minCubeY+cubeSpanY) would otherwise write past the
                // master array and throw, faulting the whole minimap render for
                // an island that's otherwise fine.
                if (tileOffsetX < 0 || tileOffsetY < 0 || tileOffsetX + TopDownTileSize > masterWidth || tileOffsetY + TopDownTileSize > masterHeight) continue;
                for (var ty = 0; ty < TopDownTileSize; ty++)
                {
                    // Larger world Z maps directly to larger screen/crop row
                    // at TopDownAlpha=+1023 -- no flip needed here, matching
                    // the "larger Z -> larger pixel row" convention the rest
                    // of the minimap code (actor markers, click-to-jump,
                    // TopDownMapRenderer before it) already assumes. This
                    // used to flip, from calibration done at alpha=-1023
                    // before that sign turned out to orbit the camera to the
                    // wrong pole (see RenderIslandDirect's own comment on
                    // why +1023 is correct) -- the coverage fix changed
                    // which screen direction Z maps to as a side effect, but
                    // this flip was never re-verified against the new sign,
                    // so every tile was quietly composited upside down. Each
                    // tile still looked individually plausible (a mirrored
                    // coastline still reads as "a coastline"), which is why
                    // it passed a "does this look reasonable" visual check;
                    // only comparing against the real cube layout (or,
                    // cheaper, re-deriving the mapping from a fresh corner
                    // projection any time the camera sign changes) exposes
                    // it as wrong.
                    var srcRow = ty * TopDownCropSize / TopDownTileSize;
                    var destRow = tileOffsetY + ty;
                    var destRowStart = destRow * masterWidth + tileOffsetX;
                    var srcRowStart = srcRow * TopDownCropSize;
                    for (var tx = 0; tx < TopDownTileSize; tx++)
                    {
                        var srcIndex = srcRowStart + tx * TopDownCropSize / TopDownTileSize;
                        var land = cropLand[srcIndex];
                        master[destRowStart + tx] = land != backgroundIndex ? land : cropSea[srcIndex];
                    }
                }
            }

            var bitmap = BitmapSource.Create(masterWidth, masterHeight, 96, 96, PixelFormats.Indexed8, CreatePalette(paletteBytes), master, masterWidth);
            bitmap.Freeze();
            return bitmap;
        }
    }

    private static BitmapPalette CreatePalette(byte[] paletteBytes)
    {
        var colors = new List<Color>(256);
        var sixBit = paletteBytes.Length >= 768 && paletteBytes.Take(768).Max() <= 63;
        for (var index = 0; index < 256; index++)
        {
            var offset = index * 3;
            var red = offset + 2 < paletteBytes.Length ? paletteBytes[offset] : (byte)0;
            var green = offset + 2 < paletteBytes.Length ? paletteBytes[offset + 1] : (byte)0;
            var blue = offset + 2 < paletteBytes.Length ? paletteBytes[offset + 2] : (byte)0;
            if (sixBit) { red = (byte)Math.Min(255, red * 4); green = (byte)Math.Min(255, green * 4); blue = (byte)Math.Min(255, blue * 4); }
            colors.Add(Color.FromRgb(red, green, blue));
        }
        return new BitmapPalette(colors);
    }

    public void ShutdownDirectRenderer()
    {
        lock (directRenderLock)
        {
            if (!directSession || RendererLibrary is null) return;
            RendererLibrary.Shutdown();
            directSession = false;
            directIsland = null;
        }
    }
    public bool IsRendererLibraryLoaded => RendererLibrary?.IsLoaded == true;

    public string Diagnostics => $"Engine={enginePath}\nExists={File.Exists(enginePath)}\nGame={gameDirectory}\nExists={Directory.Exists(gameDirectory)}\nSaves={saveDirectory}\nDirect={directFailure}";

    public BitmapImage? RenderIsland(string islandName)
        => RenderIsland(islandName, null, CancellationToken.None);

    public BitmapImage? RenderIsland(string islandName, string? cameraCommand)
        => RenderIsland(islandName, cameraCommand, CancellationToken.None);

    public BitmapImage? RenderIsland(string islandName, string? cameraCommand, CancellationToken cancellationToken)
    {
        var saveName = islandName.ToUpperInvariant() switch
        {
            "CITADEL" => "citadel",
            "DESERT" => "desert",
            _ => null
        };
        if (saveName is null) return null;
        var reference = Path.Combine(referenceDirectory, saveName + ".png");
        if (!IsAvailable || !File.Exists(Path.Combine(saveDirectory, saveName + ".LBA")))
            return LoadImage(reference);

        Directory.CreateDirectory(outputDirectory);
        var screenshotPath = Path.Combine(outputDirectory, $"{saveName}-{Guid.NewGuid():N}.png");
        var startInfo = new ProcessStartInfo
        {
            FileName = enginePath,
            WorkingDirectory = Path.GetDirectoryName(enginePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--game-dir"); startInfo.ArgumentList.Add(gameDirectory);
        startInfo.ArgumentList.Add("--headless"); startInfo.ArgumentList.Add("--no-audio"); startInfo.ArgumentList.Add("--no-autosave");
        startInfo.ArgumentList.Add("--load"); startInfo.ArgumentList.Add(saveName);
        startInfo.ArgumentList.Add("--exec"); startInfo.ArgumentList.Add("cam_follow 1");
        if (cameraCommand is not null) { startInfo.ArgumentList.Add("--exec-at"); startInfo.ArgumentList.Add("10"); startInfo.ArgumentList.Add(cameraCommand); }
        startInfo.ArgumentList.Add("--tick"); startInfo.ArgumentList.Add("40"); startInfo.ArgumentList.Add("--screenshot"); startInfo.ArgumentList.Add(screenshotPath); startInfo.ArgumentList.Add("--exit");
        startInfo.Environment["PATH"] = $"C:\\msys64\\ucrt64\\bin;C:\\msys64\\usr\\bin;{Environment.GetEnvironmentVariable("PATH")}";
        using var process = Process.Start(startInfo);
        if (process is null) return LoadImage(reference);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!process.WaitForExit(100))
        {
            if (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline) continue;
            try { process.Kill(true); } catch { }
            return null;
        }
        if (!process.HasExited || process.ExitCode != 0 || !File.Exists(screenshotPath)) return LoadImage(reference);
        var image = LoadImage(screenshotPath);
        try { File.Delete(screenshotPath); } catch { }
        return image;
    }

    private static BitmapImage? LoadImage(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
