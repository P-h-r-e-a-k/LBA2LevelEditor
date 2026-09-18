using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LBA2LevelEditor;

internal sealed class RendererLibraryApi : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VersionFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetDataRootFn([MarshalAs(UnmanagedType.LPStr)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ShutdownFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadIslandFn([MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadCubeFn(int x, int y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetViewTargetFn(int worldX, int worldY, int worldZ);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RenderFrameFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RenderFrameWideFn(int radiusCubes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetCameraFn(int alpha, int beta, int gamma, int distance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetDrawSkyFn(int enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetDrawSeaFn(int enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FramebufferFn(out int width, out int height, out int pitch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorCountFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorFn(int index, out int x, out int y, out int z, out int waypointCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorWaypointFn(int actorIndex, int waypointIndex, out int x, out int y, out int z);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorAttributesFn(int index, out int beta, out int body, out int anim, out int lifePoint, out int armor, out int hitForce, out int move);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorScriptFn(int index, [Out] byte[]? buffer, int bufferSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetActorAttributesFn(int index, int beta, int body, int anim, int lifePoint, int armor, int hitForce, int move);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetActorPositionFn(int index, int worldX, int worldY, int worldZ);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AddActorFn(int worldX, int worldY, int worldZ, int beta, int body, int anim, int lifePoint, int armor, int hitForce, int move);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorFlagsFn(int index, out uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetActorFlagsFn(int index, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorBoundsFn(int index, out int xMin, out int xMax, out int yMin, out int yMax, out int zMin, out int zMax);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorNativeAnimsFn(int index, [Out] int[]? outAnims, int maxCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RenderBodyPreviewFn(int genBody, int genAnim, int cameraBeta, int cameraDistance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetBodyPreviewAnimationPausedFn(int paused);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ProjectPointFn(int worldX, int worldY, int worldZ, out int screenX, out int screenY);
    private IntPtr handle;
    private VersionFn? version;
    private InitializeFn? initialize;
    private SetDataRootFn? setDataRoot;
    private ShutdownFn? shutdown;
    private LoadIslandFn? loadIsland;
    private LoadCubeFn? loadCube;
    private SetViewTargetFn? setViewTarget;
    private RenderFrameFn? renderFrame;
    private RenderFrameWideFn? renderFrameWide;
    private SetCameraFn? setCamera;
    private SetDrawSkyFn? setDrawSky;
    private SetDrawSeaFn? setDrawSea;
    private FramebufferFn? framebuffer;
    private GetActorCountFn? getActorCount;
    private GetActorFn? getActor;
    private GetActorWaypointFn? getActorWaypoint;
    private GetActorAttributesFn? getActorAttributes;
    private GetActorScriptFn? getActorScript;
    private SetActorAttributesFn? setActorAttributes;
    private SetActorPositionFn? setActorPosition;
    private AddActorFn? addActor;
    private GetActorFlagsFn? getActorFlags;
    private SetActorFlagsFn? setActorFlags;
    private GetActorBoundsFn? getActorBounds;
    private GetActorNativeAnimsFn? getActorNativeAnims;
    private RenderBodyPreviewFn? renderBodyPreview;
    private SetBodyPreviewAnimationPausedFn? setBodyPreviewAnimationPaused;
    private ProjectPointFn? projectPoint;

    // devTreeFallbackPath is an absolute path into the native dev build
    // tree (dynamically linked against MSYS2's GCC runtime/SDL3 -- fine
    // locally, since msys64 is on a dev machine), used only when neither of
    // the two distributable options below is present: running straight
    // from source before the statically-linked DLL has ever been built.
    //
    // The two distributable options, tried first:
    //  1. A copy of the DLL next to the exe (LBA2LevelEditor.csproj copies
    //     it there via CopyToOutputDirectory) -- covers bin/Debug and a
    //     plain folder-based dotnet publish.
    //  2. Failing that, an embedded copy (LBA2LevelEditor.csproj also
    //     embeds it as a resource) extracted to a stable cache directory --
    //     covers a single-file publish. dotnet publish's own
    //     IncludeNativeLibrariesForSelfExtract looked like the built-in
    //     answer for this, but it isn't: it does extract the DLL at
    //     runtime, just into its own private %TEMP%\.net\<app>\<hash>\
    //     cache directory rather than next to the exe (AppContext.
    //     BaseDirectory stays the exe's own folder for a single-file app),
    //     and NativeLibrary.Load resolving a bare "liblba2_renderer.dll"
    //     does not search that directory either -- confirmed empirically by
    //     a DllNotFoundException there. Embedding it ourselves and
    //     extracting it to a location we choose sidesteps the single-file
    //     host's own native-library resolver entirely instead of fighting it.
    public RendererLibraryApi(string devTreeFallbackPath)
    {
        var path = ResolveLibraryPath(devTreeFallbackPath);
        if (path is null) return;
        // Only the dev-tree DLL is dynamically linked; both distributable
        // paths are statically linked and have no non-system dependencies,
        // but pointing this at a real MSYS2 install is harmless either way.
        SetDllDirectory("C:\\msys64\\ucrt64\\bin");
        try { handle = NativeLibrary.Load(path); } catch { handle = IntPtr.Zero; }
        if (handle == IntPtr.Zero) return;
        version = Marshal.GetDelegateForFunctionPointer<VersionFn>(NativeLibrary.GetExport(handle, "lba2_renderer_version"));
        initialize = Get<InitializeFn>("lba2_renderer_initialize"); setDataRoot = Get<SetDataRootFn>("lba2_renderer_set_data_root"); shutdown = Get<ShutdownFn>("lba2_renderer_shutdown");
        loadIsland = Get<LoadIslandFn>("lba2_renderer_load_island"); loadCube = Get<LoadCubeFn>("lba2_renderer_load_cube");
        setViewTarget = Get<SetViewTargetFn>("lba2_renderer_set_view_target");
        renderFrame = Get<RenderFrameFn>("lba2_renderer_render_frame"); setCamera = Get<SetCameraFn>("lba2_renderer_set_camera"); framebuffer = Get<FramebufferFn>("lba2_renderer_framebuffer");
        renderFrameWide = Get<RenderFrameWideFn>("lba2_renderer_render_frame_wide");
        setDrawSky = Get<SetDrawSkyFn>("lba2_renderer_set_draw_sky");
        setDrawSea = Get<SetDrawSeaFn>("lba2_renderer_set_draw_sea");
        getActorCount = Get<GetActorCountFn>("lba2_renderer_get_actor_count");
        getActor = Get<GetActorFn>("lba2_renderer_get_actor");
        getActorWaypoint = Get<GetActorWaypointFn>("lba2_renderer_get_actor_waypoint");
        getActorAttributes = Get<GetActorAttributesFn>("lba2_renderer_get_actor_attributes");
        getActorScript = Get<GetActorScriptFn>("lba2_renderer_get_actor_script");
        setActorAttributes = Get<SetActorAttributesFn>("lba2_renderer_set_actor_attributes");
        setActorPosition = Get<SetActorPositionFn>("lba2_renderer_set_actor_position");
        addActor = Get<AddActorFn>("lba2_renderer_add_actor");
        getActorFlags = Get<GetActorFlagsFn>("lba2_renderer_get_actor_flags");
        setActorFlags = Get<SetActorFlagsFn>("lba2_renderer_set_actor_flags");
        getActorBounds = Get<GetActorBoundsFn>("lba2_renderer_get_actor_bounds");
        getActorNativeAnims = Get<GetActorNativeAnimsFn>("lba2_renderer_get_actor_native_anims");
        renderBodyPreview = Get<RenderBodyPreviewFn>("lba2_renderer_render_body_preview");
        setBodyPreviewAnimationPaused = Get<SetBodyPreviewAnimationPausedFn>("lba2_renderer_set_body_preview_animation_paused");
        projectPoint = Get<ProjectPointFn>("lba2_renderer_project_point");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);

    // See the constructor's own comment for why there are three candidates
    // and why the embedded-resource one exists at all.
    private static string? ResolveLibraryPath(string devTreeFallbackPath)
    {
        var coLocated = Path.Combine(AppContext.BaseDirectory, "liblba2_renderer.dll");
        if (File.Exists(coLocated)) return coLocated;

        var assembly = Assembly.GetExecutingAssembly();
        using (var resource = assembly.GetManifestResourceStream("liblba2_renderer.dll"))
        {
            if (resource is not null)
            {
                // A per-version cache directory (not just a fixed name)
                // means a later app update with a changed DLL extracts
                // fresh instead of reusing a stale one left by a previous
                // install -- keyed on the running exe's own file size/write
                // time, which changes whenever the embedded resource does,
                // without needing to compute or store an explicit
                // version/hash anywhere. Environment.ProcessPath rather than
                // Assembly.Location: the latter always returns "" for an
                // assembly embedded in a single-file app.
                var assemblyInfo = new FileInfo(Environment.ProcessPath ?? "");
                var cacheKey = assemblyInfo.Exists
                    ? $"{assemblyInfo.Length}-{assemblyInfo.LastWriteTimeUtc.Ticks}"
                    : resource.Length.ToString();
                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LBA2LevelEditor", "native", cacheKey);
                var extractedPath = Path.Combine(cacheDir, "liblba2_renderer.dll");
                if (!File.Exists(extractedPath))
                {
                    Directory.CreateDirectory(cacheDir);
                    var tempPath = extractedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var file = File.Create(tempPath)) resource.CopyTo(file);
                    // Atomic-ish: a crash/concurrent launch mid-extract never
                    // leaves a half-written file at the path other launches
                    // check for.
                    File.Move(tempPath, extractedPath, overwrite: true);
                }
                return extractedPath;
            }
        }

        return File.Exists(devTreeFallbackPath) ? devTreeFallbackPath : null;
    }

    public bool IsLoaded => handle != IntPtr.Zero && version is not null;
    public int Version => version?.Invoke() ?? 0;
    public bool Initialize() => initialize?.Invoke() == 1;
    public void Shutdown() => shutdown?.Invoke();
    public bool SetDataRoot(string path) => setDataRoot?.Invoke(path) == 1;
    public int LoadIsland(string name) => loadIsland?.Invoke(name) ?? 0;
    public int LoadCube(int x, int y) => loadCube?.Invoke(x, y) ?? 0;
    public int SetViewTarget(int worldX, int worldY, int worldZ) => setViewTarget?.Invoke(worldX, worldY, worldZ) ?? 0;
    public int RenderFrame() => renderFrame?.Invoke() ?? 0;
    // radiusCubes=0 behaves exactly like RenderFrame(); >0 also loads and
    // draws that many rings of neighboring cubes into the same frame (see
    // AffGrilleExtWide/lba2_renderer_render_frame_wide for how). Costs
    // roughly (2*radiusCubes+1)^2 cube loads, so callers should reserve
    // larger radii for camera distances where a single cube's terrain
    // visibly runs out before the horizon does.
    public int RenderFrameWide(int radiusCubes) => renderFrameWide?.Invoke(radiusCubes) ?? RenderFrame();
    public void SetCamera(int alpha, int beta, int gamma, int distance) => setCamera?.Invoke(alpha, beta, gamma, distance);
    public void SetDrawSky(bool enabled) => setDrawSky?.Invoke(enabled ? 1 : 0);
    public void SetDrawSea(bool enabled) => setDrawSea?.Invoke(enabled ? 1 : 0);
    public IntPtr GetFramebuffer(out int width, out int height, out int pitch)
    {
        if (framebuffer is null) { width = height = pitch = 0; return IntPtr.Zero; }
        return framebuffer(out width, out height, out pitch);
    }
    public int GetActorCount() => getActorCount?.Invoke() ?? 0;
    public bool GetActor(int index, out int x, out int y, out int z, out int waypointCount)
    {
        if (getActor is null) { x = y = z = waypointCount = 0; return false; }
        return getActor(index, out x, out y, out z, out waypointCount) == 1;
    }
    public bool GetActorWaypoint(int actorIndex, int waypointIndex, out int x, out int y, out int z)
    {
        if (getActorWaypoint is null) { x = y = z = 0; return false; }
        return getActorWaypoint(actorIndex, waypointIndex, out x, out y, out z) == 1;
    }
    public bool GetActorAttributes(int index, out int beta, out int body, out int anim, out int lifePoint, out int armor, out int hitForce, out int move)
    {
        if (getActorAttributes is null) { beta = body = anim = lifePoint = armor = hitForce = move = 0; return false; }
        return getActorAttributes(index, out beta, out body, out anim, out lifePoint, out armor, out hitForce, out move) == 1;
    }
    public string GetActorScript(int index)
    {
        if (getActorScript is null) return string.Empty;
        var buffer = new byte[16384];
        var needed = getActorScript(index, buffer, buffer.Length);
        if (needed <= 0) return string.Empty;
        if (needed > buffer.Length) { buffer = new byte[needed]; getActorScript(index, buffer, buffer.Length); }
        var nul = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, nul < 0 ? buffer.Length : nul);
    }
    public bool ProjectPoint(int worldX, int worldY, int worldZ, out int screenX, out int screenY)
    {
        if (projectPoint is null) { screenX = screenY = 0; return false; }
        return projectPoint(worldX, worldY, worldZ, out screenX, out screenY) == 1;
    }
    // Session-only: not written to disk by these calls alone. See
    // lba2_renderer_set_actor_attributes's own doc comment (RENDERER_API.H)
    // for exactly what "session-only" means here (live, survives panning,
    // but gone on next app launch until a separate save operation exists).
    public bool SetActorAttributes(int index, int beta, int body, int anim, int lifePoint, int armor, int hitForce, int move)
        => setActorAttributes?.Invoke(index, beta, body, anim, lifePoint, armor, hitForce, move) == 1;
    public bool SetActorPosition(int index, int worldX, int worldY, int worldZ)
        => setActorPosition?.Invoke(index, worldX, worldY, worldZ) == 1;
    public int AddActor(int worldX, int worldY, int worldZ, int beta, int body, int anim, int lifePoint, int armor, int hitForce, int move)
        => addActor?.Invoke(worldX, worldY, worldZ, beta, body, anim, lifePoint, armor, hitForce, move) ?? -1;
    public bool GetActorFlags(int index, out uint flags)
    {
        if (getActorFlags is null) { flags = 0; return false; }
        return getActorFlags(index, out flags) == 1;
    }
    public bool SetActorFlags(int index, uint flags) => setActorFlags?.Invoke(index, flags) == 1;
    // Only succeeds for an actor whose scene is currently loaded/rendered
    // and that has a body assigned -- see lba2_renderer_get_actor_bounds's
    // own doc comment. Callers should fall back to a fixed-size hit target
    // when this returns false.
    public bool GetActorBounds(int index, out int xMin, out int xMax, out int yMin, out int yMax, out int zMin, out int zMax)
    {
        if (getActorBounds is null) { xMin = xMax = yMin = yMax = zMin = zMax = 0; return false; }
        return getActorBounds(index, out xMin, out xMax, out yMin, out yMax, out zMin, out zMax) == 1;
    }
    // Empty (not null-vs-empty distinguished) whenever this actor's scene
    // isn't the one currently loaded/rendered, or it genuinely has no F_ANIM
    // entries in its own character-fiche table -- callers should fall back
    // to showing the full, unordered animation list in that case. See
    // lba2_renderer_get_actor_native_anims's own doc comment for what this
    // list actually means (the character's real moveset, not a guess).
    // Uses a fixed pre-allocated buffer, never a null-then-resize two-call
    // round trip -- passing null for the sizing call was a genuine
    // departure from GetActorScript's own established [Out] byte[]
    // pattern above (which always passes a real, pre-sized buffer), and
    // was suspected for a time during a hard-to-pin-down access-violation
    // crash (coreclr.dll, confirmed via Windows Event Log) reachable by
    // opening the Body/Animation dropdown -- but that crash turned out to
    // trace to EXTFUNC.CPP's own animation-restart logic instead (see its
    // own comment), reproducing identically with this code bypassed
    // entirely. Kept anyway on its own merits: it's the same proven-safe
    // shape as every other [Out] array call in this file, and a
    // character's own fiche realistically never lists anywhere near 128
    // animations regardless.
    public IReadOnlyList<int> GetActorNativeAnims(int index)
    {
        if (getActorNativeAnims is null) return Array.Empty<int>();
        var buffer = new int[128];
        var count = getActorNativeAnims(index, buffer, buffer.Length);
        if (count <= 0) return Array.Empty<int>();
        if (count > buffer.Length) count = buffer.Length;
        var result = new int[count];
        Array.Copy(buffer, result, count);
        return result;
    }
    // Renders into the same shared framebuffer GetFramebuffer() reads --
    // safe to call between ordinary main-view renders, but the caller must
    // read the framebuffer immediately after and not assume the main view
    // is unaffected by anything except its own next render. See
    // lba2_renderer_render_body_preview's own doc comment.
    public bool RenderBodyPreview(int genBody, int genAnim, int cameraBeta, int cameraDistance) => renderBodyPreview?.Invoke(genBody, genAnim, cameraBeta, cameraDistance) == 1;
    // Freezes/resumes the body preview's own animation playback -- the
    // turntable's own rotation angle is entirely client-side (see
    // ActorAttributesWindow's previewAngle) and keeps advancing regardless.
    public void SetBodyPreviewAnimationPaused(bool paused) => setBodyPreviewAnimationPaused?.Invoke(paused ? 1 : 0);

    public bool IsRendererReady => IsLoaded && initialize is not null && setDataRoot is not null && loadIsland is not null && loadCube is not null && setViewTarget is not null && renderFrame is not null && framebuffer is not null;

    private T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        NativeLibrary.Free(handle);
        handle = IntPtr.Zero;
        version = null;
        initialize = null; setDataRoot = null; shutdown = null; loadIsland = null; loadCube = null; setViewTarget = null; renderFrame = null; renderFrameWide = null; setCamera = null; setDrawSky = null; setDrawSea = null; framebuffer = null;
        getActorCount = null; getActor = null; getActorWaypoint = null; getActorAttributes = null; getActorScript = null; projectPoint = null;
    }
}
