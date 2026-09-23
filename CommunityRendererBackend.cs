using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace LBAAssembler;

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
    private volatile bool reloadRequested;   // set by ReloadCubes, acted on inside the next render's lock
    private string? liveRoot;              // data root that stands in for the game folder while unsaved edits are previewed (LiveDataRoot)
    private string directFailure = "none";
    public string DirectFailure => directFailure;
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
        outputDirectory = Path.Combine(Path.GetTempPath(), "LBAAssembler", "native-renders");
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
        liveRoot = null;
        gameDirectory = path;
    }

    // Forces the next exterior render to load its island again (and so re-read SCENE.HQR), e.g. after a zone edit was saved.
    public void InvalidateLoadedIsland()
    {
        lock (directRenderLock) directIsland = null;
    }

    // Live preview of unsaved island edits: the renderer reads its data from `root` (a LiveDataRoot) instead of the game folder,
    // until EndLive. The island there is rewritten as the edits are made and ReloadCubes makes the next frame read it.
    public void BeginLive(string root)
    {
        lock (directRenderLock)
        {
            liveRoot = root;
            if (directSession && RendererLibrary is not null) RendererLibrary.SetDataRoot(root);
            directIsland = null;
        }
    }

    public void EndLive()
    {
        lock (directRenderLock)
        {
            if (liveRoot is null) return;
            liveRoot = null;
            if (directSession && RendererLibrary is not null) RendererLibrary.SetDataRoot(gameDirectory);
            directIsland = null;
        }
    }

    public bool LiveActive => liveRoot is not null;

    // BODY.HQR's own resource cache (HQR_Bodys) is opened once at native process start and never
    // follows BeginLive/SetDataRoot's own directory redirect (that only affects caches opened *after*
    // it runs) -- so a debug-body live preview (MainWindow.BodyDebugPreview.cs) needs this explicit
    // reload after writing its swapped-in BODY.HQR copy, or the preview keeps showing whatever was on
    // disk at process start. path is a full file path (the live copy's own BODY.HQR), not a directory.
    public bool ReloadBodies(string path)
    {
        lock (directRenderLock)
        {
            return directSession && RendererLibrary is not null && RendererLibrary.ReloadBodies(path);
        }
    }

    // Same as ReloadBodies, for HQR_Anims (ANIM.HQR's own cache) -- a debug-anim live preview needs
    // this the same way a debug-body one needs ReloadBodies.
    public bool ReloadAnims(string path)
    {
        lock (directRenderLock)
        {
            return directSession && RendererLibrary is not null && RendererLibrary.ReloadAnims(path);
        }
    }

    // The renderer keeps the cubes it has read in a cache that only loading the island again empties: the next frame does that.
    public void ReloadCubes()
    {
        reloadRequested = true;   // consumed by the next frame: this is called on every preview refresh and must not wait for one in flight
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
    // drawSky asserts the sky flag for this frame, and the sea flag is always turned back on: the
    // minimap render (RenderIslandTopDown) leaves sea off, and setting either flag from outside
    // this lock could land in the middle of a minimap render and put sea back over its land.
    public BitmapSource? RenderIslandDirect(string islandName, byte[] paletteBytes, int worldX, int worldY, int worldZ, int alpha = 240, int beta = -256, int gamma = 0, int distance = 30000, Action? afterRenderBeforeUnlock = null, int wideRadiusCubes = 0, bool drawSky = true)
    {
        if (RendererLibrary is null || !RendererLibrary.IsRendererReady) { directFailure = "renderer DLL unavailable"; return null; }
        lock (directRenderLock)
        {
            if (interiorLoaded) { directFailure = "an interior scene is loaded"; return null; }
            RendererLibrary.SetDrawSky(drawSky);
            RendererLibrary.SetDrawSea(true);
            var baseName = islandName.ToLowerInvariant();
            if (!directSession)
            {
                if (!RendererLibrary.SetDataRoot(liveRoot ?? gameDirectory)) { directFailure = "set data root failed"; return null; }
                if (!RendererLibrary.Initialize()) { directFailure = "native initialize failed"; return null; }
                directSession = true;
            }
            if (reloadRequested) { reloadRequested = false; directIsland = null; }
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

    // LBA2's indoor/interior scenes: a completely different, fixed-camera
    // isometric renderer from every RenderIsland* call above -- see
    // RendererLibraryApi.LoadInteriorScene's own doc comment. Shares
    // directRenderLock/directSession with RenderIslandDirect (both drive the
    // same single native renderer instance), but never touches directIsland:
    // an interior scene isn't addressed by island name, and switching back
    // to an exterior island afterwards still needs its own LoadIsland() call
    // regardless of what directIsland was left at, so this deliberately
    // leaves that field alone rather than invalidating or guessing at it.
    // cameraX/Y/Z come back as wherever CameraCenter(1) actually snapped the
    // camera to -- NOT the hero's own raw GetActor position (confirmed live
    // to be a completely different, unrelated coordinate: one real scene
    // had the hero's own stored position at world (229376, 0, 262144), an
    // address on the outdoor island's own much larger coordinate scheme,
    // while CameraCenter(1) actually snapped the camera to (0, 0, 2048),
    // local to this scene's own 64x512-unit brick grid -- seeding the pan
    // scrollbars from the former sent them to a six-figure coordinate
    // hundreds of bricks outside the scene's own grid, which is what was
    // actually behind interior panning corrupting into brick noise on the
    // very first scroll, not a rendering bug). Callers use these as the
    // starting point once the user starts
    // scrolling, since there's otherwise no way to know where this fixed
    // camera actually ended up.
    public BitmapSource? RenderInteriorSceneDirect(int numscene, byte[] paletteBytes, out int cameraX, out int cameraY, out int cameraZ)
    {
        cameraX = cameraY = cameraZ = 0;
        if (RendererLibrary is null || !RendererLibrary.IsRendererReady) { directFailure = "renderer DLL unavailable"; return null; }
        lock (directRenderLock)
        {
            if (!directSession)
            {
                if (!RendererLibrary.SetDataRoot(liveRoot ?? gameDirectory)) { directFailure = "set data root failed"; return null; }
                if (!RendererLibrary.Initialize()) { directFailure = "native initialize failed"; return null; }
                directSession = true;
            }
            // Loading the interior replaces the native actor/zone lists, so whichever island was loaded is
            // gone (even if the load fails part-way): the next exterior render must load its island again
            // even if it is the same name.
            directIsland = null;
            if (!RendererLibrary.LoadInteriorScene(numscene)) { directFailure = $"load interior scene failed: {numscene}"; return null; }
            RendererLibrary.GetInteriorCameraPosition(out cameraX, out cameraY, out cameraZ);
            if (!RendererLibrary.RenderInteriorFrame()) { directFailure = "native interior render returned failure"; return null; }
            var pointer = RendererLibrary.GetFramebuffer(out var width, out var height, out var pitch);
            if (pointer == IntPtr.Zero || width <= 0 || height <= 0) { directFailure = $"framebuffer invalid: {width}x{height}, {pitch}"; return null; }
            var pixels = new byte[width * height];
            for (var row = 0; row < height; row++) Marshal.Copy(pointer + row * pitch, pixels, row * width, width);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8, CreatePalette(paletteBytes), pixels, width);
            bitmap.Freeze();
            return bitmap;
        }
    }

    public const int InteriorCanvasWidth = 3712;
    public const int InteriorCanvasHeight = 2400;

    // While an interior scene is what the native side holds, exterior renders and minimap snapshots (which reload an island
    // and so replace the scene's actors and zones) must not run: one that was already queued behind the render lock when the
    // scene was chosen would otherwise slip in between the load and the stitched render, or after them, and the interior view
    // then reads (and renders from) another scene's state -- a sporadic native crash when picking a scene straight after an
    // island. Cleared by ReleaseInterior() when the interior view is left.
    private volatile bool interiorLoaded;
    public bool InteriorLoaded => interiorLoaded;
    public void ReleaseInterior() => interiorLoaded = false;

    // RenderInteriorSceneDirect + RenderInteriorFullDirect as one step under the render lock.
    public BitmapSource? RenderInteriorSceneFullDirect(int numscene, byte[] paletteBytes, out List<(int Index, int X, int Y, int HalfWidth, int HalfHeight, bool Marker)> actors, out InteriorOverlay overlay)
    {
        actors = new();
        overlay = InteriorOverlay.Empty;
        if (RendererLibrary is null || !RendererLibrary.IsRendererReady) { directFailure = "renderer DLL unavailable"; return null; }
        lock (directRenderLock)
        {
            interiorLoaded = false;
            var first = RenderInteriorSceneDirect(numscene, paletteBytes, out _, out _, out _);
            var canvas = first is null ? null : RenderInteriorFullDirect(paletteBytes, out actors, out overlay);
            interiorLoaded = canvas is not null;
            return canvas;
        }
    }

    // Renders the whole scene RenderInteriorSceneDirect last loaded into one
    // InteriorCanvasWidth x InteriorCanvasHeight indexed bitmap (the native
    // side stitches camera-stepped tiles -- see
    // lba2_renderer_render_interior_full), plus each scene actor's position
    // and hit size in that bitmap's pixel space, and the actors' patrol routes
    // and the scene's zones projected into the same space.
    public BitmapSource? RenderInteriorFullDirect(byte[] paletteBytes, out List<(int Index, int X, int Y, int HalfWidth, int HalfHeight, bool Marker)> actors, out InteriorOverlay overlay)
    {
        actors = new();
        overlay = InteriorOverlay.Empty;
        if (RendererLibrary is null || !RendererLibrary.IsRendererReady) { directFailure = "renderer DLL unavailable"; return null; }
        lock (directRenderLock)
        {
            var pixels = new byte[InteriorCanvasWidth * InteriorCanvasHeight];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                if (!RendererLibrary.RenderInteriorFull(handle.AddrOfPinnedObject(), InteriorCanvasWidth, InteriorCanvasHeight)) { directFailure = "native interior full render failed"; return null; }
            }
            finally { handle.Free(); }
            var count = RendererLibrary.GetActorCount();
            var routes = new List<(int, List<Point>)>();
            for (var i = 0; i < count; i++)
            {
                if (RendererLibrary.GetInteriorActorCanvas(i, out var x, out var y, out var hw, out var hh, out var marker)) actors.Add((i, x, y, hw, hh, marker));

                // Patrol route: the actor's own position, then each track waypoint.
                if (!RendererLibrary.GetActor(i, out var ax, out var ay, out var az, out var waypointCount) || waypointCount <= 0) continue;
                if (!RendererLibrary.ProjectInteriorPoint(ax, ay, az, out var px, out var py)) continue;
                var points = new List<Point> { new(px, py) };
                for (var w = 0; w < waypointCount; w++)
                    if (RendererLibrary.GetActorWaypoint(i, w, out var wx, out var wy, out var wz) && RendererLibrary.ProjectInteriorPoint(wx, wy, wz, out var qx, out var qy))
                        points.Add(new Point(qx, qy));
                if (points.Count > 1) routes.Add((i, points));
            }

            var zones = new List<ProjectedZone>();
            var zoneCount = RendererLibrary.GetZoneCount();
            var zoneOrdinals = new Dictionary<int, int>();
            for (var z = 0; z < zoneCount; z++)
            {
                // Position of this zone inside its scene's zone list (zones of one scene are listed together).
                var zoneScene = RendererLibrary.GetZoneScene(z);
                var zoneIndex = zoneOrdinals.GetValueOrDefault(zoneScene);
                zoneOrdinals[zoneScene] = zoneIndex + 1;
                if (!RendererLibrary.GetZone(z, out var x0, out var y0, out var z0, out var x1, out var y1, out var z1, out var type, out var num)) continue;
                var world = ZoneStyle.Corners(x0, y0, z0, x1, y1, z1);
                var corners = new Point[8];
                var ok = true;
                for (var c = 0; c < 8 && ok; c++)
                {
                    ok = RendererLibrary.ProjectInteriorPoint(world[c].X, world[c].Y, world[c].Z, out var cx, out var cy);
                    corners[c] = new Point(cx, cy);
                }
                if (ok) zones.Add(new ProjectedZone(type, num, corners, zoneScene >= 0 ? new ZoneRef(2, zoneScene, zoneIndex) : null));
            }
            overlay = new InteriorOverlay(routes, zones);

            var bitmap = BitmapSource.Create(InteriorCanvasWidth, InteriorCanvasHeight, 96, 96, PixelFormats.Indexed8, CreatePalette(paletteBytes), pixels, InteriorCanvasWidth);
            bitmap.Freeze();
            return bitmap;
        }
    }

    // A SPRITE_3D actor's sprite (a key, coin, chest...) on a transparent-looking
    // black frame, cropped to the sprite, for the attributes window's preview.
    public BitmapSource? RenderSpritePreview(int sprite, byte[] paletteBytes)
    {
        if (RendererLibrary is null || !RendererLibrary.IsRendererReady) return null;
        lock (directRenderLock)
        {
            if (!RendererLibrary.RenderSpritePreview(sprite, out var sx, out var sy, out var sw, out var sh)) return null;
            var pointer = RendererLibrary.GetFramebuffer(out var width, out var height, out var pitch);
            if (pointer == IntPtr.Zero || width <= 0 || height <= 0) return null;
            var pixels = new byte[width * height];
            for (var row = 0; row < height; row++) Marshal.Copy(pointer + row * pitch, pixels, row * width, width);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed8, CreatePalette(paletteBytes), pixels, width);
            var crop = new CroppedBitmap(bitmap, new Int32Rect(Math.Max(0, sx), Math.Max(0, sy), Math.Min(sw, width - Math.Max(0, sx)), Math.Min(sh, height - Math.Max(0, sy))));
            crop.Freeze();
            return crop;
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
        const int minDistance = 200, maxDistance = 40000;
        lock (directRenderLock)
        {
            // A silhouette that touches the frame edge has been clipped, so its
            // measured size is too small and a fit computed from it would still
            // clip. Back the probe off until the whole body is inside the frame.
            var probeDistance = 5000;
            FrameBounds probe = default;
            for (var attempt = 0; ; attempt++)
            {
                if (!RendererLibrary.RenderBodyPreview(genBody, genAnim, 0, probeDistance)) return null;
                if (MeasureNonBackgroundBounds() is not { } measured) return null;
                probe = measured;
                if (!TouchesFrameEdge(probe) || attempt >= 6 || probeDistance >= maxDistance) break;
                probeDistance = Math.Min(probeDistance * 2, maxDistance);
            }

            // How far the silhouette reaches from the frame centre (the camera
            // aims at the body's centre, but an animation can shift it).
            var reach = Math.Max(
                Math.Max(probe.Width / 2 - probe.MinX, probe.MaxX - probe.Width / 2),
                Math.Max(probe.Height / 2 - probe.MinY, probe.MaxY - probe.Height / 2));
            if (reach < 2) return null; // degenerate -- avoid dividing into an absurd distance
            var targetHalf = Math.Min(probe.Width, probe.Height) * targetFraction / 2;
            var distance = Math.Clamp((int)(probeDistance * reach / targetHalf), minDistance, maxDistance);

            // The live preview orbits the camera around the object (see
            // AffichageBodyPreview), so the rendered frame's silhouette size
            // still varies with orientation: a humanoid viewed diagonally
            // (limbs spread at an angle) can reach further from centre than
            // head-on. Sampling several angles across the full turn at the
            // real render distance and unioning their bounds keeps the crop
            // correct for the whole rotation instead of just whichever
            // single angle it was measured at. If any angle is still clipped
            // by the frame, pull the camera back and measure again.
            const int angleSamples = 8;
            int minX = 0, maxX = 0, minY = 0, maxY = 0;
            var width = probe.Width;
            var height = probe.Height;
            for (var pass = 0; ; pass++)
            {
                int? unionMinX = null, unionMaxX = null, unionMinY = null, unionMaxY = null;
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
                if (unionMinX is not int uMinX || unionMaxX is not int uMaxX || unionMinY is not int uMinY || unionMaxY is not int uMaxY)
                    return null; // every sampled angle failed to render
                minX = uMinX; maxX = uMaxX; minY = uMinY; maxY = uMaxY;
                var clipped = minX <= 0 || minY <= 0 || maxX >= width - 1 || maxY >= height - 1;
                if (!clipped || pass >= 5 || distance >= maxDistance) break;
                distance = Math.Min((int)(distance * 1.25), maxDistance);
            }

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

    private static bool TouchesFrameEdge(FrameBounds b)
        => b.MinX <= 0 || b.MinY <= 0 || b.MaxX >= b.Width - 1 || b.MaxY >= b.Height - 1;

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
            if (interiorLoaded) { directFailure = "an interior scene is loaded"; return null; }
            var baseName = islandName.ToLowerInvariant();
            if (!directSession)
            {
                if (!RendererLibrary.SetDataRoot(liveRoot ?? gameDirectory)) { directFailure = "set data root failed"; return null; }
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
            interiorLoaded = false;
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
