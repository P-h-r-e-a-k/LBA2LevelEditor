using System.Diagnostics;
using System.IO.Compression;
using LBA2LevelEditor;
using LBA2LevelEditor.Terrain;

namespace ScriptRoundTrip;

// The engine plays what the island editor saves: an exterior scene on DESERT is rendered headless before and after edits to a
// sandbox copy of DESERT.ILE (a raised hill, then darkened ground), and the frames must differ the way the edit says.
//   islandengine        (env ISLAND_E2E_KEEP=<folder> keeps the frames as PNGs)
internal static class IslandEngineTest
{
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr reserved);

    public static int Run(string[] args)
    {
        var engine = Lba2Engine.Find();
        if (engine is null) { Console.WriteLine("no lba2cc.exe found"); return 1; }
        var sandbox = @"E:\dump\_lba2isl_e2e";
        var user = Path.Combine(Path.GetTempPath(), "island_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);
        try
        {
            foreach (var file in Directory.GetFiles(Lba2Dir))
            {
                var target = Path.Combine(sandbox, Path.GetFileName(file));
                if (File.Exists(target)) File.Delete(target);
                if (Path.GetFileName(file).Equals("DESERT.ILE", StringComparison.OrdinalIgnoreCase)) File.Copy(file, target, true);
                else CreateHardLinkW(target, file, IntPtr.Zero);
            }
            // an exterior scene of DESERT (island id 2, cube mode 1)
            var scenes = HqrArchive.Open(Path.Combine(Lba2Dir, "SCENE.HQR"));
            var count = HqrArchive.CountEntries(Path.Combine(Lba2Dir, "SCENE.HQR"));
            var scene = -1; byte[]? record = null;
            for (var i = 1; i < count && scene < 0; i++)
            {
                if (!scenes.IsValid(i)) continue;
                var bytes = scenes.Read(i);
                if (bytes.Length > 5 && bytes[0] == 2 && bytes[5] == 1) { scene = i - 1; record = bytes; }
            }
            if (record is null) { Console.WriteLine("no exterior desert scene"); return 1; }
            int cubeX = record[1], cubeZ = record[2];
            var model = new LBA2LevelEditor.Scenes.SceneStore(LBA2LevelEditor.Scenes.SceneGame.Lba2, Lba2Dir).Load(scene);
            Console.WriteLine($"scene {scene}: cube ({cubeX},{cubeZ}), hero at ({model.Hero.X},{model.Hero.Y},{model.Hero.Z})");
            var gx = cubeX * 64 + model.Hero.X / 512; var gz = cubeZ * 64 + model.Hero.Z / 512;

            Directory.CreateDirectory(user);
            var baseline = Shoot(engine, sandbox, user, scene, Path.Combine(user, "base.png"));
            var again = Shoot(engine, sandbox, user, scene, Path.Combine(user, "base2.png"));
            var failures = 0;
            failures += Check("the engine renders a frame", baseline is not null);
            if (baseline is null) return 1;
            failures += Check("the same run twice gives the same frame", again is not null && again.SameAs(baseline));

            var island = IslandFile.Load(Path.Combine(sandbox, "DESERT.ILE"));
            IslandOps.Raise(island, new BrushRegion(gx + 3, gz - 6, 14, 0.5), 1800);
            island.Save();
            var hill = Shoot(engine, sandbox, user, scene, Path.Combine(user, "hill.png"));
            Console.WriteLine($"  hill frame: {(hill is null ? "none" : $"{Mean(hill):F1} mean luminance, {Diff(hill, baseline):F2} mean difference from baseline")}");
            failures += Check("a raised hill changes the frame", hill is not null && Diff(hill, baseline) > 1.0);

            island = IslandFile.Load(Path.Combine(sandbox, "DESERT.ILE"));
            IslandLightOps.Paint(island, new BrushRegion(gx, gz, 30, 0.8), IslandLightOps.Mode.Set, 0);
            island.Save();
            var dark = Shoot(engine, sandbox, user, scene, Path.Combine(user, "dark.png"));
            Console.WriteLine($"  dark frame: {(dark is null ? "none" : $"{Mean(dark):F1} mean luminance (baseline {Mean(baseline):F1})")}");
            failures += Check("darkened ground makes the frame darker", dark is not null && Mean(dark) < Mean(baseline) - 2);

            var keep = Environment.GetEnvironmentVariable("ISLAND_E2E_KEEP");
            if (keep is not null) foreach (var f in Directory.GetFiles(user, "*.png")) File.Copy(f, Path.Combine(keep, Path.GetFileName(f)), true);
            Console.WriteLine(failures == 0 ? "island engine test: passed" : $"island engine test: {failures} FAILED");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(user, true); } catch (IOException) { }
            try { Directory.Delete(sandbox, true); } catch (IOException) { }
        }
    }

    private static int Check(string what, bool ok) { Console.WriteLine($"  {(ok ? "ok    " : "FAILED")} {what}"); return ok ? 0 : 1; }

    private static Frame? Shoot(string engine, string gameDir, string user, int scene, string png)
    {
        var start = new ProcessStartInfo(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new Lba2PlayOptions { Scene = scene, Sound = false, Width = 640, Height = 480 }.Arguments(gameDir, user)) start.ArgumentList.Add(a);
        foreach (var a in new[] { "--headless", "--fixed-dt", "20", "--tick", "700", "--screenshot", png, "--exit" }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        process.WaitForExit(90000);
        return File.Exists(png) ? Frame.Decode(File.ReadAllBytes(png)) : null;
    }

    private static double Mean(Frame i) { double s = 0; for (var p = 0; p < i.Width * i.Height; p++) s += (i.Rgb[p * 3] + i.Rgb[p * 3 + 1] + i.Rgb[p * 3 + 2]) / 3.0; return s / (i.Width * i.Height); }
    private static double Diff(Frame a, Frame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 999;
        double s = 0; for (var i = 0; i < a.Rgb.Length; i++) s += Math.Abs(a.Rgb[i] - b.Rgb[i]);
        return s / a.Rgb.Length;
    }

    // Enough PNG to read the engine's screenshots: 8-bit RGB / RGBA, non-interlaced.
    private sealed class Frame
    {
        public int Width, Height; public byte[] Rgb = Array.Empty<byte>();
        public bool SameAs(Frame o) => Width == o.Width && Height == o.Height && Rgb.AsSpan().SequenceEqual(o.Rgb);

        public static Frame? Decode(byte[] png)
        {
            int pos = 8, w = 0, h = 0, depth = 0, colour = 0;
            using var idat = new MemoryStream();
            while (pos + 8 <= png.Length)
            {
                var len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
                var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                if (type == "IHDR") { w = (png[pos + 8] << 24) | (png[pos + 9] << 16) | (png[pos + 10] << 8) | png[pos + 11]; h = (png[pos + 12] << 24) | (png[pos + 13] << 16) | (png[pos + 14] << 8) | png[pos + 15]; depth = png[pos + 16]; colour = png[pos + 17]; }
                else if (type == "IDAT") idat.Write(png, pos + 8, len);
                pos += 12 + len;
            }
            if (depth != 8 || colour is not (2 or 6)) return null;
            var bpp = colour == 6 ? 4 : 3;
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            var raw = new MemoryStream(); z.CopyTo(raw);
            var data = raw.ToArray();
            var stride = w * bpp;
            var rows = new byte[h * stride];
            for (var y = 0; y < h; y++)
            {
                var filter = data[y * (stride + 1)];
                for (var x = 0; x < stride; x++)
                {
                    int cur = data[y * (stride + 1) + 1 + x];
                    int a = x >= bpp ? rows[y * stride + x - bpp] : 0, b = y > 0 ? rows[(y - 1) * stride + x] : 0, c = x >= bpp && y > 0 ? rows[(y - 1) * stride + x - bpp] : 0;
                    int v = filter switch
                    {
                        0 => cur, 1 => cur + a, 2 => cur + b, 3 => cur + ((a + b) >> 1),
                        _ => cur + Paeth(a, b, c),
                    };
                    rows[y * stride + x] = (byte)v;
                }
            }
            var rgb = new byte[w * h * 3];
            for (var i = 0; i < w * h; i++) { rgb[i * 3] = rows[i * bpp]; rgb[i * 3 + 1] = rows[i * bpp + 1]; rgb[i * 3 + 2] = rows[i * bpp + 2]; }
            return new Frame { Width = w, Height = h, Rgb = rgb };
        }

        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
