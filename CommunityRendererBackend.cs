using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace LBA2LevelEditor;

internal sealed class CommunityRendererBackend
{
    private readonly string enginePath;
    private readonly string gameDirectory;
    private readonly string saveDirectory;
    private readonly string outputDirectory;
    private readonly string referenceDirectory;
    private readonly string rendererLibraryPath;
    private readonly object directRenderLock = new();
    private string? directIsland;
    private bool directSession;
    private string directFailure = "none";
    public RendererLibraryApi? RendererLibrary { get; }

    public CommunityRendererBackend()
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
        gameDirectory = @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
        saveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Twinsen", "LBA2", "save");
        outputDirectory = Path.Combine(Path.GetTempPath(), "LBA2LevelEditor", "native-renders");
        try { RendererLibrary = new RendererLibraryApi(rendererLibraryPath); } catch { RendererLibrary = null; }
    }

    public bool IsAvailable => File.Exists(enginePath) && Directory.Exists(gameDirectory);
    public bool RendererLibraryAvailable => File.Exists(rendererLibraryPath);
    public string RendererLibraryPath => rendererLibraryPath;
    public bool DirectRendererLoaded => RendererLibrary?.IsLoaded == true;
    public bool DirectRendererReady => RendererLibrary?.IsRendererReady == true;

    public BitmapSource? RenderIslandDirect(string islandName, byte[] paletteBytes, int alpha = 240, int beta = -256, int gamma = 0, int distance = 30000)
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
                if (RendererLibrary.LoadCube(8, 9) == 0) { directFailure = $"load cube failed: {baseName} 8,9"; return null; }
                directIsland = baseName;
            }
            RendererLibrary.SetCamera(alpha, beta, gamma, distance);
            if (RendererLibrary.RenderFrame() == 0) { directFailure = "native render returned failure"; return null; }
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
