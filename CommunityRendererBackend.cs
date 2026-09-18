using System.Diagnostics;
using System.IO;
using System.Windows;
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

        // This dev-tree path is only a fallback: RendererLibraryApi tries
        // loading "liblba2_renderer.dll" by its bare name first, which finds
        // a co-located copy next to the exe (bin/Debug, a folder publish) via
        // the OS's normal search order, and -- for a single-file publish
        // with native-library self-extraction -- the copy .NET's own
        // single-file host extracts to its private cache directory (which is
        // *not* AppContext.BaseDirectory; that still points at the original
        // exe's own folder for a single-file app, confirmed by a co-located
        // liblba2_renderer.dll simply not being there at runtime). This
        // absolute path only matters when neither of those exists, e.g.
        // running straight from source before the static DLL has been built
        // at all -- it points at the dynamically-linked dev build instead.
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
    public BitmapSource? RenderBodyPreview(int genBody, int genAnim, int cameraBeta, int cameraDistance, byte[] paletteBytes)
    {
        if (RendererLibrary is null || !RendererLibrary.IsRendererReady) return null;
        lock (directRenderLock)
        {
            if (!RendererLibrary.RenderBodyPreview(genBody, genAnim, cameraBeta, cameraDistance)) return null;
            var pointer = RendererLibrary.GetFramebuffer(out var width, out var height, out var pitch);
            if (pointer == IntPtr.Zero || width <= 0 || height <= 0) return null;
            var pixels = new byte[width * height];
            for (var row = 0; row < height; row++) Marshal.Copy(pointer + row * pitch, pixels, row * width, width);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8, CreatePalette(paletteBytes), pixels, width);
            bitmap.Freeze();
            return bitmap;
        }
    }

    // Distance to render the preview at, and a crop rectangle (in
    // framebuffer pixels) the caller should crop every subsequent frame to
    // before displaying it -- both computed once per body/anim selection
    // and reused across every rotation angle/animation frame until that
    // selection changes again (recomputing every tick would make the crop
    // visibly resize as the silhouette's own bounding box changes shape
    // while animating/rotating).
    public readonly record struct BodyPreviewCalibration(int Distance, Int32Rect CropRect);

    // Solves for a camera distance that fills roughly targetFraction of the
    // frame with the body's own silhouette, since there's no reliable
    // per-body bounding box available here (see AffichageBodyPreview's own
    // comment) -- a rat and a building-sized boss both render fine, just at
    // very different natural distances. Renders once at a known probe
    // distance, measures the actual non-background pixel footprint (the
    // frame corner is always pure background for this isolated, otherwise-
    // empty preview -- unlike a real terrain shot, nothing else is ever
    // drawn into it), and scales the probe distance by how far off that
    // footprint is from the target size (apparent size under perspective is
    // roughly proportional to 1/distance, so distance scales linearly with
    // the size ratio).
    //
    // AffichageBodyPreview (EXTFUNC.CPP) clamps the distance it's actually
    // given to stay outside the engine's near-clip plane, which for a small
    // body can be well past the ideal distance computed above -- the
    // resulting silhouette then comes out smaller than targetFraction asked
    // for. Rather than duplicate that clamp's exact threshold here, this
    // re-measures fresh at whatever distance the native side actually used
    // and derives the crop rectangle from that real measurement, padded for
    // room to animate/rotate -- so the displayed (cropped-then-stretched)
    // image still fills the frame regardless of whether the clamp kicked in.
    //
    // Returns null if nothing drew (e.g. NO_BODY) or the renderer isn't
    // ready; callers should fall back to a fixed distance and an
    // uncropped/full-frame rectangle.
    // targetAspect is the display panel's own width/height ratio (e.g. 0.55
    // for a panel noticeably taller than it is wide) -- the crop rectangle
    // below is grown, never shrunk, to match it, so Stretch="Uniform" fills
    // the whole panel instead of letterboxing top-and-bottom against a
    // squarer crop. 1.0 (square) if the caller doesn't know its own layout
    // yet.
    public BodyPreviewCalibration? CalibrateBodyPreviewDistance(int genBody, int genAnim, byte[] paletteBytes, double targetFraction = 0.8, double targetAspect = 1.0)
    {
        if (RendererLibrary is null || !RendererLibrary.IsRendererReady) return null;
        const int probeDistance = 5000;
        lock (directRenderLock)
        {
            if (!RendererLibrary.RenderBodyPreview(genBody, genAnim, 0, probeDistance)) return null;
            var probeBounds = MeasureNonBackgroundBounds();
            if (probeBounds is not { } probe) return null;

            var silhouetteSize = Math.Max(probe.MaxX - probe.MinX, probe.MaxY - probe.MinY);
            if (silhouetteSize < 4) return null; // degenerate -- avoid dividing into an absurd distance
            var targetSize = Math.Min(probe.Width, probe.Height) * targetFraction;
            var idealDistance = (int)(probeDistance * silhouetteSize / targetSize);
            var distance = Math.Clamp(idealDistance, 200, 40000);

            // The live preview orbits the camera around the object (see
            // AffichageBodyPreview), so the rendered frame's silhouette size
            // still varies with orientation: a humanoid viewed diagonally
            // (limbs spread at an angle) can reach further from centre than
            // head-on. Sampling several angles across the full turn at the
            // real render distance and unioning their bounds keeps the crop
            // correct for the whole rotation instead of just whichever
            // single angle it was measured at.
            const int angleSamples = 8;
            int? unionMinX = null, unionMaxX = null, unionMinY = null, unionMaxY = null;
            var width = probe.Width;
            var height = probe.Height;
            for (var i = 0; i < angleSamples; i++)
            {
                var angle = i * 4096 / angleSamples;
                if (!RendererLibrary.RenderBodyPreview(genBody, genAnim, angle, distance)) continue;
                if (MeasureNonBackgroundBounds() is not { } sample) continue;
                unionMinX = unionMinX is { } a ? Math.Min(a, sample.MinX) : sample.MinX;
                unionMaxX = unionMaxX is { } b ? Math.Max(b, sample.MaxX) : sample.MaxX;
                unionMinY = unionMinY is { } c ? Math.Min(c, sample.MinY) : sample.MinY;
                unionMaxY = unionMaxY is { } d ? Math.Max(d, sample.MaxY) : sample.MaxY;
            }
            if (unionMinX is not int minX || unionMaxX is not int maxX || unionMinY is not int minY || unionMaxY is not int maxY)
                return null; // every sampled angle failed to render

            var centerX = (minX + maxX) / 2;
            var centerY = (minY + maxY) / 2;
            var paddedWidth = (maxX - minX) * 1.3 + 8;
            var paddedHeight = (maxY - minY) * 1.3 + 8;
            // Grow (never shrink) whichever axis is proportionally short of
            // targetAspect, so the body's own measured footprint is always
            // still fully contained -- e.g. a humanoid's natural silhouette
            // (narrow, tall) is already narrower than a ~0.55 panel aspect,
            // so this adds side padding rather than cropping any tighter.
            if (paddedWidth / paddedHeight < targetAspect) paddedWidth = paddedHeight * targetAspect;
            else paddedHeight = paddedWidth / targetAspect;
            var halfW = (int)(paddedWidth / 2.0);
            var halfH = (int)(paddedHeight / 2.0);
            var left = Math.Clamp(centerX - halfW, 0, width - 1);
            var top = Math.Clamp(centerY - halfH, 0, height - 1);
            var right = Math.Clamp(centerX + halfW, left + 1, width);
            var bottom = Math.Clamp(centerY + halfH, top + 1, height);
            return new BodyPreviewCalibration(distance, new Int32Rect(left, top, right - left, bottom - top));
        }
    }

    private readonly record struct FrameBounds(int Width, int Height, int MinX, int MaxX, int MinY, int MaxY);

    // Scans the current framebuffer for the bounding box of every pixel that
    // isn't the background colour -- valid for this isolated, otherwise-
    // empty preview where the top-left corner pixel is always background
    // (unlike a real terrain shot, nothing else is ever drawn into it).
    // Must be called with directRenderLock already held (it reads native
    // framebuffer state a concurrent render could otherwise be changing).
    private FrameBounds? MeasureNonBackgroundBounds()
    {
        var pointer = RendererLibrary!.GetFramebuffer(out var width, out var height, out var pitch);
        if (pointer == IntPtr.Zero || width <= 0 || height <= 0) return null;

        var backgroundIndex = Marshal.ReadByte(pointer);
        var row = new byte[width];
        int minX = width, maxX = -1, minY = height, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(pointer + y * pitch, row, 0, width);
            for (var x = 0; x < width; x++)
            {
                if (row[x] == backgroundIndex) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < minX || maxY < minY ? null : new FrameBounds(width, height, minX, maxX, minY, maxY);
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
