using System.IO;

namespace LBAAssembler.Export;

// LBAAssembler.exe --export <folder> [--format glb|obj|ply|stl] [--category text] [--only text] [--limit n] [--scale n] [--no-terrain] [--no-objects]
// Exports without opening a window (the same catalog as Tools > Export 3D models): every category whose title contains --category (default all),
// items whose label contains --only. A summary goes to <folder>\export.log; the exit code is 0 when nothing failed.
internal static class ExportCli
{
    public static bool Wants(string[] args) => args.Any(a => a.Equals("--export", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        string? Value(string name) { var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var folder = Value("--export");
        if (string.IsNullOrWhiteSpace(folder)) return 2;
        Directory.CreateDirectory(folder);
        var log = new StreamWriter(Path.Combine(folder, "export.log")) { AutoFlush = true };
        try
        {
            var format = (Value("--format") ?? "glb").ToLowerInvariant() switch { "obj" => ExportFormat.Obj, "ply" => ExportFormat.Ply, "stl" => ExportFormat.Stl, _ => ExportFormat.Glb };
            var scale = float.TryParse(Value("--scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0.001f;
            var limit = int.TryParse(Value("--limit"), out var l) ? l : int.MaxValue;
            var category = Value("--category"); var only = Value("--only");
            var settings = EditorSettings.Current;
            var catalog = new ExportCatalog(settings.Lba1Directory, settings.GameDirectory);
            var options = new ExportOptions { IslandTerrain = !args.Contains("--no-terrain"), IslandObjects = !args.Contains("--no-objects"), Log = message => log.WriteLine("  " + message) };
            int done = 0, failed = 0;
            foreach (var c in catalog.Categories.Where(c => category is null || c.Title.Contains(category, StringComparison.OrdinalIgnoreCase)))
            {
                log.WriteLine($"== {c.Title}");
                var items = c.Load().Where(i => only is null || i.Label.Contains(only, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList();
                var progress = new Progress<(int Index, string Message)>();
                var reports = new List<string>();
                var (d, f) = ExportRunner.Run(items, format, scale, folder, options, new SyncProgress(m => log.WriteLine(m)), CancellationToken.None);
                done += d; failed += f;
            }
            log.WriteLine($"done: {done} exported, {failed} failed");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            log.WriteLine("export crashed: " + error);
            return 3;
        }
        finally { log.Dispose(); }
    }

    private sealed class SyncProgress : IProgress<(int Index, string Message)>
    {
        private readonly Action<string> write;
        public SyncProgress(Action<string> write) => this.write = write;
        public void Report((int Index, string Message) value) => write(value.Message);
    }
}
