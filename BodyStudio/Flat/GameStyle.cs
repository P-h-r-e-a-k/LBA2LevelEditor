using System.IO;
using System.Numerics;

namespace LbaBodyStudio;

// What the game's own bodies look like as colours: which palette entries they use and how many they get by with. Built by
// reading every body of a BODY.HQR; it gives the "allowed colours" the game-style converter snaps artwork to.
public sealed class BodyStyleStats
{
    public int Game { get; init; }
    public int Bodies { get; private set; }
    public int Skipped { get; private set; }
    // per palette index: faces / lines / spheres that use it, and how many different bodies use it
    public int[] Uses { get; } = new int[256];
    public int[] BodiesUsing { get; } = new int[256];
    // polygon type (LBA1: 0 flat unlit ... 7-10 lit; LBA2: 0-23, above 7 textured) -> faces
    public SortedDictionary<int, int> MaterialFaces { get; } = new();
    public List<int> ColoursPerBody { get; } = new();
    public List<int> FacesPerBody { get; } = new();

    public static BodyStyleStats Analyse(int game, IEnumerable<byte[]> bodies)
    {
        var stats = new BodyStyleStats { Game = game };
        foreach (var data in bodies)
        {
            Body body;
            try { body = Body.Read(data, game); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException) { stats.Skipped++; continue; }
            stats.Bodies++;
            var used = new HashSet<int>();
            foreach (var f in body.Faces) { stats.Uses[Math.Clamp(f.Colour, 0, 255)]++; used.Add(Math.Clamp(f.Colour, 0, 255)); stats.MaterialFaces[f.Material] = stats.MaterialFaces.GetValueOrDefault(f.Material) + 1; }
            foreach (var l in body.Lines) { stats.Uses[Math.Clamp(l.Colour, 0, 255)]++; used.Add(Math.Clamp(l.Colour, 0, 255)); }
            foreach (var s in body.Spheres) { stats.Uses[Math.Clamp(s.Colour, 0, 255)]++; used.Add(Math.Clamp(s.Colour, 0, 255)); }
            foreach (var c in used) stats.BodiesUsing[c]++;
            stats.ColoursPerBody.Add(used.Count);
            stats.FacesPerBody.Add(body.Faces.Count);
        }
        return stats;
    }

    // The palette indices at least `minBodies` different bodies use: the colours the game's artists actually reach for.
    public int[] Recommended(int minBodies = 3) => Enumerable.Range(1, 255).Where(i => BodiesUsing[i] >= minBodies).ToArray();

    // The colours those bases show on screen once lit (see LightModel): what a flat picture for the generator should be made of.
    public int[] RecommendedDisplay(int minBodies = 3) => Recommended(minBodies).Select(i => LightModel.DisplayOf(i, Game)).Distinct().ToArray();

    // Position in the colour's 16-step ramp (index & 15) -> uses; the game lights a polygon by adding up to 15 to its index.
    public int[] RampPositions()
    {
        var result = new int[16];
        for (var i = 0; i < 256; i++) result[i & 15] += Uses[i];
        return result;
    }

    public int MedianColoursPerBody() => ColoursPerBody.Count == 0 ? 0 : ColoursPerBody.OrderBy(c => c).ElementAt(ColoursPerBody.Count / 2);
}

public sealed class StyleOptions
{
    // "Auto" reads transparency, else the colour of the picture's border; the others match Settings.Mask of the generator.
    public string Mask { get; set; } = "Auto";
    public int Threshold { get; set; } = 45;
    // how many flat colours the result gets (the game's own bodies use a dozen or two)
    public int Colours { get; set; } = 14;
    // analysis resolution (height of the subject in pixels)
    public int WorkHeight { get; set; } = 320;
    public int SheetHeight { get; set; } = 768;
    public int SmoothPasses { get; set; } = 2;
    // regions smaller than this share of the subject are merged into their neighbours
    public double MinRegionPercent { get; set; } = 0.06;
    // add a mirrored back view (the generator then reads a front + back sheet), else a single front picture
    public bool BackFromFront { get; set; } = true;
    // copy the left half of the subject onto the right: most game characters are symmetric front-on
    public bool Symmetrise { get; set; }
    // 0 keeps the picture's proportions; otherwise the subject is squeezed / stretched to width / height
    public double AspectRatio { get; set; }
    // palette indices the result may use; null = every colour except black at 0
    public int[]? Allowed { get; set; }
}

public sealed record StyleResult(FlatImage Sheet, FlatImage Subject, int[] PaletteIndices, int Regions, int SubjectPixels, string Notes);

// Turns any picture of a character into the flat picture the generator (and the game's polygon bodies) work best with:
// the subject cut out, its colours reduced to a handful of flat regions that use real palette entries, specks removed.
public static class GameStyle
{
    public static StyleResult Convert(FlatImage source, byte[] palette768, StyleOptions? options = null)
    {
        options ??= new StyleOptions();
        var notes = new List<string>();

        // work at a bounded resolution
        var work = source.Height > options.WorkHeight * 2 ? source.Resize(Math.Max(1, source.Width * options.WorkHeight * 2 / source.Height), options.WorkHeight * 2) : source;
        var mask = ForegroundMask(work, options, notes);
        var bounds = Bounds(mask, work.Width, work.Height) ?? throw new InvalidDataException("No subject found. Try another mask mode or threshold, or use a picture with a plain or transparent background.");
        var crop = work.Crop(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var cropMask = new bool[bounds.Width * bounds.Height];
        for (var y = 0; y < bounds.Height; y++) for (var x = 0; x < bounds.Width; x++) cropMask[y * bounds.Width + x] = mask[(bounds.Y + y) * work.Width + bounds.X + x];

        // resample to the analysis size (keeping the subject's proportions, or the ones asked for)
        var targetHeight = options.WorkHeight;
        var aspect = options.AspectRatio > 0 ? options.AspectRatio : bounds.Width / (double)bounds.Height;
        var targetWidth = Math.Max(4, (int)Math.Round(targetHeight * aspect));
        var subject = Resample(crop, cropMask, targetWidth, targetHeight, out var m);
        if (options.Symmetrise) Symmetrise(subject, m);

        // flat colours: smooth, cluster in Lab, snap to the palette
        var lab = ToLab(subject);
        Smooth(lab, m, subject.Width, subject.Height, options.SmoothPasses);
        var allowed = (options.Allowed is { Length: >= 8 } list ? list : Enumerable.Range(1, 255).ToArray()).Distinct().ToArray();
        var paletteLab = allowed.ToDictionary(i => i, i => LabColour.FromRgb(palette768[i * 3], palette768[i * 3 + 1], palette768[i * 3 + 2]));
        var centres = Cluster(lab, m, Math.Clamp(options.Colours, 2, 40));
        var chosen = new List<int>();
        foreach (var c in centres)
        {
            var best = allowed.OrderBy(i => LabColour.Distance(paletteLab[i], c)).First();
            if (!chosen.Contains(best)) chosen.Add(best);
        }
        var labels = new int[m.Length];
        for (var i = 0; i < m.Length; i++)
        {
            if (!m[i]) { labels[i] = -1; continue; }
            var bestK = 0; var bestD = float.MaxValue;
            for (var k = 0; k < chosen.Count; k++) { var d = LabColour.Distance(paletteLab[chosen[k]], lab[i]); if (d < bestD) { bestD = d; bestK = k; } }
            labels[i] = bestK;
        }
        var subjectPixels = m.Count(v => v);
        Majority(labels, subject.Width, subject.Height, 2);
        var minArea = Math.Max(4, (int)(subjectPixels * options.MinRegionPercent / 100));
        var merged = MergeSmallRegions(labels, subject.Width, subject.Height, minArea);
        var regions = CountRegions(labels, subject.Width, subject.Height);
        notes.Add($"{chosen.Count} colours, {regions} regions ({merged} specks merged)");

        // the flat subject picture
        var flat = new FlatImage(subject.Width, subject.Height);
        var used = new SortedSet<int>();
        for (var y = 0; y < subject.Height; y++)
            for (var x = 0; x < subject.Width; x++)
            {
                var l = labels[y * subject.Width + x];
                if (l < 0) continue;
                var idx = chosen[l]; used.Add(idx);
                flat.Set(x, y, palette768[idx * 3], palette768[idx * 3 + 1], palette768[idx * 3 + 2]);
            }

        // the sheet: scaled up with nearest-neighbour (the regions stay flat), transparent background
        var margin = 12;
        var scale = (options.SheetHeight - 2 * margin) / (double)flat.Height;
        var viewWidth = (int)Math.Ceiling(flat.Width * scale) + 2 * margin;
        var view = new FlatImage(viewWidth, options.SheetHeight);
        for (var y = 0; y < options.SheetHeight; y++)
            for (var x = 0; x < viewWidth; x++)
            {
                int sx = (int)Math.Floor((x - margin) / scale), sy = (int)Math.Floor((y - margin) / scale);
                if (sx < 0 || sy < 0 || sx >= flat.Width || sy >= flat.Height) continue;
                Array.Copy(flat.Bgra, flat.Index(sx, sy), view.Bgra, view.Index(x, y), 4);
            }
        FlatImage sheet;
        if (options.BackFromFront)
        {
            sheet = new FlatImage(viewWidth * 2, options.SheetHeight);
            sheet.Paste(view, 0, 0);
            sheet.Paste(view.FlipHorizontal(), viewWidth, 0);
        }
        else sheet = view;
        return new StyleResult(sheet, flat, used.ToArray(), regions, subjectPixels, string.Join("; ", notes));
    }

    // A picture that holds a front view (left half) and a back view (right half): each half is converted on its own, then the two are padded to
    // one width and joined, which is the layout the generator reads as "Front + back".
    public static StyleResult ConvertPair(FlatImage source, byte[] palette768, StyleOptions? options = null)
    {
        options ??= new StyleOptions();
        var single = new StyleOptions
        {
            Mask = options.Mask, Threshold = options.Threshold, Colours = options.Colours, WorkHeight = options.WorkHeight, SheetHeight = options.SheetHeight, SmoothPasses = options.SmoothPasses,
            MinRegionPercent = options.MinRegionPercent, BackFromFront = false, Symmetrise = options.Symmetrise, AspectRatio = options.AspectRatio, Allowed = options.Allowed,
        };
        var front = Convert(source.Crop(0, 0, source.Width / 2, source.Height), palette768, single);
        var back = Convert(source.Crop(source.Width / 2, 0, source.Width - source.Width / 2, source.Height), palette768, single);
        var width = Math.Max(front.Sheet.Width, back.Sheet.Width);
        var sheet = new FlatImage(width * 2, front.Sheet.Height);
        sheet.Paste(front.Sheet, (width - front.Sheet.Width) / 2, 0);
        sheet.Paste(back.Sheet, width + (width - back.Sheet.Width) / 2, 0);
        return new StyleResult(sheet, front.Subject, front.PaletteIndices.Union(back.PaletteIndices).OrderBy(i => i).ToArray(), front.Regions + back.Regions, front.SubjectPixels + back.SubjectPixels, "front: " + front.Notes + " | back: " + back.Notes);
    }

    // ---- foreground -----------------------------------------------------------------------------------------------------

    private static bool[] ForegroundMask(FlatImage image, StyleOptions options, List<string> notes)
    {
        int w = image.Width, h = image.Height;
        var mask = new bool[w * h];
        var mode = options.Mask;
        var transparent = 0;
        for (var i = 3; i < image.Bgra.Length; i += 4) if (image.Bgra[i] < 128) transparent++;
        if (mode == "Auto") mode = transparent > w * h / 100 ? "Transparent background" : "Background colour";

        if (mode == "Background colour")
        {
            var bg = BorderColour(image);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var (r, g, b, a) = image.Pixel(x, y);
                    mask[y * w + x] = a > 127 && Math.Max(Math.Abs(r - bg.R), Math.Max(Math.Abs(g - bg.G), Math.Abs(b - bg.B))) > options.Threshold;
                }
            notes.Add($"background colour ({bg.R},{bg.G},{bg.B})");
        }
        else
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var (r, g, b, a) = image.Pixel(x, y);
                    var lum = (r * 299 + g * 587 + b * 114) / 1000;
                    mask[y * w + x] = mode switch
                    {
                        "Transparent background" => a > 127,
                        "Light subject" => a > 127 && lum > 255 - options.Threshold,
                        _ => a > 127 && lum < options.Threshold,
                    };
                }
        }
        Clean(mask, w, h);
        return mask;
    }

    private static (byte R, byte G, byte B) BorderColour(FlatImage image)
    {
        var rs = new List<byte>(); var gs = new List<byte>(); var bs = new List<byte>();
        void Take(int x, int y) { var (r, g, b, a) = image.Pixel(x, y); if (a < 128) return; rs.Add(r); gs.Add(g); bs.Add(b); }
        for (var x = 0; x < image.Width; x++) { Take(x, 0); Take(x, image.Height - 1); }
        for (var y = 0; y < image.Height; y++) { Take(0, y); Take(image.Width - 1, y); }
        if (rs.Count == 0) return (255, 255, 255);
        rs.Sort(); gs.Sort(); bs.Sort();
        return (rs[rs.Count / 2], gs[gs.Count / 2], bs[bs.Count / 2]);
    }

    // Fills the holes in the subject and drops specks: keeps components at least 0.25% the size of the biggest (shoes, hands).
    private static void Clean(bool[] mask, int w, int h)
    {
        // holes: background not connected to the border
        var outside = new bool[w * h];
        var queue = new Queue<int>();
        void Seed(int x, int y) { var i = y * w + x; if (!mask[i] && !outside[i]) { outside[i] = true; queue.Enqueue(i); } }
        for (var x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (var y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue(); int x = i % w, y = i / w;
            if (x > 0) Seed(x - 1, y); if (x < w - 1) Seed(x + 1, y); if (y > 0) Seed(x, y - 1); if (y < h - 1) Seed(x, y + 1);
        }
        for (var i = 0; i < mask.Length; i++) if (!outside[i]) mask[i] = true;

        var label = new int[w * h]; var sizes = new List<int> { 0 };
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || label[start] != 0) continue;
            var id = sizes.Count; sizes.Add(0);
            var q = new Queue<int>(); q.Enqueue(start); label[start] = id;
            while (q.Count > 0)
            {
                var i = q.Dequeue(); sizes[id]++; int x = i % w, y = i / w;
                void Visit(int nx, int ny) { var n = ny * w + nx; if (mask[n] && label[n] == 0) { label[n] = id; q.Enqueue(n); } }
                if (x > 0) Visit(x - 1, y); if (x < w - 1) Visit(x + 1, y); if (y > 0) Visit(x, y - 1); if (y < h - 1) Visit(x, y + 1);
            }
        }
        var biggest = sizes.Skip(1).DefaultIfEmpty(0).Max();
        for (var i = 0; i < mask.Length; i++) if (mask[i] && sizes[label[i]] < biggest / 400) mask[i] = false;
    }

    private static (int X, int Y, int Width, int Height)? Bounds(bool[] mask, int w, int h)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) if (mask[y * w + x]) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // Area-averaged resample of the colours and a resampled mask (a target pixel is subject when most of its source is).
    private static FlatImage Resample(FlatImage crop, bool[] mask, int width, int height, out bool[] resampled)
    {
        var opaque = crop.Clone();
        for (var i = 0; i < mask.Length; i++) opaque.Bgra[i * 4 + 3] = mask[i] ? (byte)255 : (byte)0;
        var scaled = opaque.Resize(width, height);
        resampled = new bool[width * height];
        for (var i = 0; i < resampled.Length; i++) resampled[i] = scaled.Bgra[i * 4 + 3] >= 128;
        return scaled;
    }

    private static void Symmetrise(FlatImage image, bool[] mask)
    {
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width / 2; x++)
            {
                int mx = image.Width - 1 - x;
                var left = y * image.Width + x; var right = y * image.Width + mx;
                // the left half wins where it has the subject; the right half fills in where the left has none
                var source = mask[left] ? left : mask[right] ? right : -1;
                if (source < 0) continue;
                Array.Copy(image.Bgra, source * 4, image.Bgra, left * 4, 4);
                Array.Copy(image.Bgra, source * 4, image.Bgra, right * 4, 4);
                mask[left] = mask[right] = true;
            }
    }

    // ---- colour ---------------------------------------------------------------------------------------------------------

    private static Vector3[] ToLab(FlatImage image)
    {
        var lab = new Vector3[image.Width * image.Height];
        for (var i = 0; i < lab.Length; i++) lab[i] = LabColour.FromRgb(image.Bgra[i * 4 + 2], image.Bgra[i * 4 + 1], image.Bgra[i * 4]);
        return lab;
    }

    // Bilateral smoothing inside the subject: flattens texture and noise, keeps the edges between colour regions.
    private static void Smooth(Vector3[] lab, bool[] mask, int w, int h, int passes)
    {
        const int radius = 3; const float spatial = 2f, range = 14f;
        for (var pass = 0; pass < passes; pass++)
        {
            var next = (Vector3[])lab.Clone();
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (!mask[i]) continue;
                    Vector3 sum = Vector3.Zero; float total = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                        for (var dx = -radius; dx <= radius; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h || !mask[ny * w + nx]) continue;
                            var c = lab[ny * w + nx];
                            var d = LabColour.Distance(c, lab[i]);
                            var weight = MathF.Exp(-(dx * dx + dy * dy) / (2 * spatial * spatial) - d * d / (2 * range * range));
                            sum += c * weight; total += weight;
                        }
                    if (total > 0) next[i] = sum / total;
                }
            Array.Copy(next, lab, lab.Length);
        }
    }

    // k-means (k-means++ seeding, fixed seed) over the subject's Lab colours.
    private static List<Vector3> Cluster(Vector3[] lab, bool[] mask, int k)
    {
        var samples = new List<Vector3>();
        for (var i = 0; i < lab.Length; i++) if (mask[i]) samples.Add(lab[i]);
        var step = Math.Max(1, samples.Count / 20000);
        samples = samples.Where((_, i) => i % step == 0).ToList();
        if (samples.Count == 0) return new List<Vector3>();
        var rng = new Random(12345);
        var centres = new List<Vector3> { samples[rng.Next(samples.Count)] };
        var nearest = samples.Select(s => LabColour.Distance(s, centres[0])).ToArray();
        while (centres.Count < Math.Min(k, samples.Count))
        {
            var total = nearest.Sum(d => (double)d * d);
            if (total <= 1e-6) break;
            var pick = rng.NextDouble() * total; var index = 0;
            for (; index < nearest.Length - 1; index++) { pick -= (double)nearest[index] * nearest[index]; if (pick <= 0) break; }
            centres.Add(samples[index]);
            for (var i = 0; i < samples.Count; i++) nearest[i] = Math.Min(nearest[i], LabColour.Distance(samples[i], centres[^1]));
        }
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var sums = new Vector3[centres.Count]; var counts = new int[centres.Count];
            foreach (var s in samples)
            {
                var best = 0; var bestD = float.MaxValue;
                for (var c = 0; c < centres.Count; c++) { var d = LabColour.Distance(s, centres[c]); if (d < bestD) { bestD = d; best = c; } }
                sums[best] += s; counts[best]++;
            }
            for (var c = 0; c < centres.Count; c++) if (counts[c] > 0) centres[c] = sums[c] / counts[c];
        }
        return centres;
    }

    // ---- flat regions ----------------------------------------------------------------------------------------------------

    private static void Majority(int[] labels, int w, int h, int passes)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            var next = (int[])labels.Clone();
            var counts = new Dictionary<int, int>();
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (labels[i] < 0) continue;
                    counts.Clear();
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            var l = labels[ny * w + nx];
                            if (l >= 0) counts[l] = counts.GetValueOrDefault(l) + 1;
                        }
                    var best = labels[i]; var bestCount = counts.GetValueOrDefault(best);
                    foreach (var (l, c) in counts) if (c > bestCount) { best = l; bestCount = c; }
                    next[i] = best;
                }
            Array.Copy(next, labels, labels.Length);
        }
    }

    // Regions (4-connected, same label) under `minArea` pixels take the label most common around them. Returns how many merged.
    private static int MergeSmallRegions(int[] labels, int w, int h, int minArea)
    {
        var merged = 0;
        for (var round = 0; round < 3; round++)
        {
            var seen = new bool[labels.Length]; var changed = false;
            for (var start = 0; start < labels.Length; start++)
            {
                if (seen[start] || labels[start] < 0) continue;
                var label = labels[start]; var pixels = new List<int>(); var queue = new Queue<int>();
                queue.Enqueue(start); seen[start] = true;
                var around = new Dictionary<int, int>();
                while (queue.Count > 0)
                {
                    var i = queue.Dequeue(); pixels.Add(i); int x = i % w, y = i / w;
                    for (var d = 0; d < 4; d++)
                    {
                        int nx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), ny = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        var n = ny * w + nx;
                        if (labels[n] == label) { if (!seen[n]) { seen[n] = true; queue.Enqueue(n); } }
                        else if (labels[n] >= 0) around[labels[n]] = around.GetValueOrDefault(labels[n]) + 1;
                    }
                }
                if (pixels.Count >= minArea || around.Count == 0) continue;
                var target = around.OrderByDescending(kv => kv.Value).First().Key;
                foreach (var p in pixels) labels[p] = target;
                merged++; changed = true;
            }
            if (!changed) break;
        }
        return merged;
    }

    private static int CountRegions(int[] labels, int w, int h)
    {
        var seen = new bool[labels.Length]; var count = 0;
        for (var start = 0; start < labels.Length; start++)
        {
            if (seen[start] || labels[start] < 0) continue;
            count++;
            var queue = new Queue<int>(); queue.Enqueue(start); seen[start] = true;
            while (queue.Count > 0)
            {
                var i = queue.Dequeue(); int x = i % w, y = i / w;
                for (var d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), ny = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var n = ny * w + nx;
                    if (!seen[n] && labels[n] == labels[start]) { seen[n] = true; queue.Enqueue(n); }
                }
            }
        }
        return count;
    }
}
