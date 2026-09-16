using System.IO;
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetCameraFn(int alpha, int beta, int gamma, int distance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetDrawSkyFn(int enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FramebufferFn(out int width, out int height, out int pitch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorCountFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorFn(int index, out int x, out int y, out int z, out int waypointCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetActorWaypointFn(int actorIndex, int waypointIndex, out int x, out int y, out int z);
    private IntPtr handle;
    private VersionFn? version;
    private InitializeFn? initialize;
    private SetDataRootFn? setDataRoot;
    private ShutdownFn? shutdown;
    private LoadIslandFn? loadIsland;
    private LoadCubeFn? loadCube;
    private SetViewTargetFn? setViewTarget;
    private RenderFrameFn? renderFrame;
    private SetCameraFn? setCamera;
    private SetDrawSkyFn? setDrawSky;
    private FramebufferFn? framebuffer;
    private GetActorCountFn? getActorCount;
    private GetActorFn? getActor;
    private GetActorWaypointFn? getActorWaypoint;

    public RendererLibraryApi(string path)
    {
        if (!File.Exists(path)) return;
        SetDllDirectory("C:\\msys64\\ucrt64\\bin");
        handle = NativeLibrary.Load(path);
        version = Marshal.GetDelegateForFunctionPointer<VersionFn>(NativeLibrary.GetExport(handle, "lba2_renderer_version"));
        initialize = Get<InitializeFn>("lba2_renderer_initialize"); setDataRoot = Get<SetDataRootFn>("lba2_renderer_set_data_root"); shutdown = Get<ShutdownFn>("lba2_renderer_shutdown");
        loadIsland = Get<LoadIslandFn>("lba2_renderer_load_island"); loadCube = Get<LoadCubeFn>("lba2_renderer_load_cube");
        setViewTarget = Get<SetViewTargetFn>("lba2_renderer_set_view_target");
        renderFrame = Get<RenderFrameFn>("lba2_renderer_render_frame"); setCamera = Get<SetCameraFn>("lba2_renderer_set_camera"); framebuffer = Get<FramebufferFn>("lba2_renderer_framebuffer");
        setDrawSky = Get<SetDrawSkyFn>("lba2_renderer_set_draw_sky");
        getActorCount = Get<GetActorCountFn>("lba2_renderer_get_actor_count");
        getActor = Get<GetActorFn>("lba2_renderer_get_actor");
        getActorWaypoint = Get<GetActorWaypointFn>("lba2_renderer_get_actor_waypoint");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);

    public bool IsLoaded => handle != IntPtr.Zero && version is not null;
    public int Version => version?.Invoke() ?? 0;
    public bool Initialize() => initialize?.Invoke() == 1;
    public void Shutdown() => shutdown?.Invoke();
    public bool SetDataRoot(string path) => setDataRoot?.Invoke(path) == 1;
    public int LoadIsland(string name) => loadIsland?.Invoke(name) ?? 0;
    public int LoadCube(int x, int y) => loadCube?.Invoke(x, y) ?? 0;
    public int SetViewTarget(int worldX, int worldY, int worldZ) => setViewTarget?.Invoke(worldX, worldY, worldZ) ?? 0;
    public int RenderFrame() => renderFrame?.Invoke() ?? 0;
    public void SetCamera(int alpha, int beta, int gamma, int distance) => setCamera?.Invoke(alpha, beta, gamma, distance);
    public void SetDrawSky(bool enabled) => setDrawSky?.Invoke(enabled ? 1 : 0);
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

    public bool IsRendererReady => IsLoaded && initialize is not null && setDataRoot is not null && loadIsland is not null && loadCube is not null && setViewTarget is not null && renderFrame is not null && framebuffer is not null;

    private T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        NativeLibrary.Free(handle);
        handle = IntPtr.Zero;
        version = null;
        initialize = null; setDataRoot = null; shutdown = null; loadIsland = null; loadCube = null; setViewTarget = null; renderFrame = null; setCamera = null; setDrawSky = null; framebuffer = null;
        getActorCount = null; getActor = null; getActorWaypoint = null;
    }
}
