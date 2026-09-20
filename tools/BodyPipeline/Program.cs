using System.Drawing;
using System.Drawing.Imaging;
using LBA2LevelEditor;
using LbaBodyStudio;

namespace BodyPipeline;

// dotnet run --project tools/BodyPipeline -- <command> ...
//   stats <game>                             colours the game's bodies use
//   sheet <game> <body> <out.png>            flat front+back sheet of a body
//   sheets <game> <outDir> [first] [count]   sheets of several bodies
//   style <game> <in.png> <out.png> [k]      convert artwork to a game-style sheet
//   roundtrip <game> <body>                  body -> sheet -> generated body -> sheet, with the overlap of the silhouettes
internal static class Program
{
    private static readonly string[] Folders =
    {
        Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure",
        Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer",
    };

    private static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("usage: BodyPipeline stats|sheet|sheets|style|roundtrip ..."); return 2; }
        var game = args.Length > 1 && int.TryParse(args[1], out var parsedGame) ? parsedGame : 2;
        return args[0] switch
        {
            "stats" => Stats(game),
            "sheet" => Sheet(game, int.Parse(args[2]), args[3]),
            "sheets" => Sheets(game, args[2], args.Length > 3 ? int.Parse(args[3]) : 0, args.Length > 4 ? int.Parse(args[4]) : 12),
            "style" => Style(game, args[2], args[3], args.Length > 4 ? int.Parse(args[4]) : 14),
            "roundtrip" => RoundTrip(game, args.Length > 2 ? int.Parse(args[2]) : 0),
            "normals" => Normals(game, int.Parse(args[2])),
            "enginebody" => EngineBody(args[1], args[2], args.Skip(3).DefaultIfEmpty("humanoid unlit").ToArray()),
            "styletest" => StyleTest(game, args.Length > 2 ? int.Parse(args[2]) : 0),
            "colours" => Colours(game, args.Length > 2 ? int.Parse(args[2]) : 0),
            "formsmoke" => FormSmoke(),
            "bodyroundtrip" => BodyRoundTrip(),
            "object" => ObjectPicture(int.Parse(args[1]), args[2], int.Parse(args[3]), double.Parse(args[4]), args[5]),
            "ress" => Ress(game, int.Parse(args[2])),
            "header" => Header(args[1], int.Parse(args[2])),
            "lba1lit" => Lba1Lit(args.Length > 1 ? int.Parse(args[1]) : 0),
            "winding" => Winding(game, args.Length > 2 ? int.Parse(args[2]) : 0, args.Length > 3 ? int.Parse(args[3]) : 20),
            _ => 2,
        };
    }

    private static string Folder(int game) => Folders[game - 1];
    private static byte[] PaletteBytes(int game) => new Hqr(Path.Combine(Folder(game), "RESS.HQR")).Read(0);

    private static IEnumerable<(int Index, byte[] Data)> AllBodies(int game)
    {
        var hqr = new Hqr(Generator.BodyArchive(Folder(game)));
        for (var i = 0; i < hqr.Count; i++)
        {
            byte[]? data = null;
            try { data = hqr.Read(i); } catch (InvalidDataException) { }
            if (data is not null) yield return (i, data);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr reserved);

    // enginebody <in.png|-> <outDir>: LBA2 body 0 (Twinsen) replaced by a body generated from the sheet (or, with -, from the game's own
    // body 0 flat sheet), then the real engine renders scene 55 headless: the frames go to outDir (base.png, generated_<mode>.png).
    private static int EngineBody(string input, string outDirectory, string[] modes)
    {
        var engine = Lba2Engine.Find();
        if (engine is null) { Console.WriteLine("no lba2cc.exe found"); return 1; }
        var sandbox = @"E:\dump\_lba2body";
        var user = Path.Combine(Path.GetTempPath(), "bodyengine_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(sandbox); Directory.CreateDirectory(user); Directory.CreateDirectory(outDirectory);
        var source = Folders[1];
        try
        {
            foreach (var file in Directory.GetFiles(source))
            {
                var target = Path.Combine(sandbox, Path.GetFileName(file));
                if (File.Exists(target)) File.Delete(target);
                if (Path.GetFileName(file).Equals("BODY.HQR", StringComparison.OrdinalIgnoreCase)) File.Copy(file, target, true);
                else CreateHardLinkW(target, file, IntPtr.Zero);
            }
            var palette = PaletteBytes(2);
            var original = Body.Read(new Hqr(Generator.BodyArchive(source)).Read(0), 2);
            var sheetPath = input;
            if (input == "-")
            {
                sheetPath = Path.Combine(user, "sheet.png");
                using var bmp = ToBitmap(FlatSheet.Render(original, palette));
                bmp.Save(sheetPath, ImageFormat.Png);
            }
            Shoot(engine, sandbox, user, Path.Combine(outDirectory, "base.png"));
            foreach (var mode in modes)
            {
                if (mode.StartsWith("rewrite"))
                {
                    // the game's own body 0 written back with our writer (lit: every polygon becomes a Gouraud one with vertex normals)
                    var copy = Body.Read(new Hqr(Generator.BodyArchive(source)).Read(0), 2);
                    copy.Lit = !mode.Contains("unlit");
                    File.WriteAllBytes(Path.Combine(sandbox, "BODY.HQR"), new Hqr(Path.Combine(source, "BODY.HQR")).Replace(0, copy.Write()));
                    var rewritten = Path.Combine(outDirectory, $"generated_{mode.Replace(' ', '_')}.png");
                    Shoot(engine, sandbox, user, rewritten);
                    Console.WriteLine($"{mode}: rewritten retail body -> {rewritten}");
                    continue;
                }
                var settings = new Settings
                {
                    ImagePath = sheetPath, Lba1Folder = Folders[0], Lba2Folder = Folders[1], Lba2Body = 0, Mask = "Transparent background", Layout = "Front + back",
                    Method = mode.Contains("template") ? "Fit template" : "New humanoid", AutoCrop = true, DetailBudget = 300, Lit = !mode.Contains("unlit"),
                };
                var generated = Generator.Generate(settings, 2);
                var written = generated.Body.Write();
                Console.WriteLine($"  written body: lit={generated.Body.Lit}, {written.Length} bytes, normals {BitConverter.ToInt32(written, 48)}, non-zero {Enumerable.Range(0, BitConverter.ToInt32(written, 48)).Count(i => BitConverter.ToInt16(written, BitConverter.ToInt32(written, 52) + i * 8) != 0 || BitConverter.ToInt16(written, BitConverter.ToInt32(written, 52) + i * 8 + 2) != 0 || BitConverter.ToInt16(written, BitConverter.ToInt32(written, 52) + i * 8 + 4) != 0)}, first polygon block type {BitConverter.ToUInt16(written, BitConverter.ToInt32(written, 68))}");
                var archive = new Hqr(Path.Combine(source, "BODY.HQR")).Replace(0, generated.Body.Write());
                File.WriteAllBytes(Path.Combine(sandbox, "BODY.HQR"), archive);
                var frame = Path.Combine(outDirectory, $"generated_{mode.Replace(' ', '_')}.png");
                Shoot(engine, sandbox, user, frame);
                Console.WriteLine($"{mode}: {generated.Body.Faces.Count} polygons, {generated.Body.Vertices.Count} points -> {frame} ({(File.Exists(frame) ? "rendered" : "NO FRAME")})");
            }
        }
        finally
        {
            try { Directory.Delete(user, true); } catch (IOException) { }
            try { Directory.Delete(sandbox, true); } catch (IOException) { }
        }
        return 0;
    }

    private static void Shoot(string engine, string gameDir, string user, string png)
    {
        ShootRaw(engine, gameDir, user, png);
        if (!File.Exists(png)) return;
        // a close-up of the hero (the frame is 1280x960; he stands a little below the middle)
        using var frame = new Bitmap(png);
        var box = new Rectangle(frame.Width / 2 - 130, frame.Height * 11 / 20 - 120, 260, 300);
        using var zoom = new Bitmap(box.Width * 3, box.Height * 3);
        using (var g = Graphics.FromImage(zoom)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half; g.DrawImage(frame, new Rectangle(0, 0, zoom.Width, zoom.Height), box, GraphicsUnit.Pixel); }
        zoom.Save(Path.ChangeExtension(png, null) + "_zoom.png", ImageFormat.Png);
    }

    private static void ShootRaw(string engine, string gameDir, string user, string png)
    {
        var start = new System.Diagnostics.ProcessStartInfo(engine) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new Lba2PlayOptions { Scene = 55, Sound = false, Width = 1280, Height = 960 }.Arguments(gameDir, user)) start.ArgumentList.Add(a);
        foreach (var a in new[] { "--headless", "--fixed-dt", "20", "--tick", "700", "--screenshot", png, "--exit" }) start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        process.WaitForExit(90000);
    }

    // styletest <game> <body>: a game body's front view is turned into "artwork" (shaded, noisy, on a coloured background), the game-style
    // converter flattens it again, and the result is compared with the original flat picture.
    private static int StyleTest(int game, int body)
    {
        var palette = PaletteBytes(game);
        var model = Body.Read(new Hqr(Generator.BodyArchive(Folder(game))).Read(body), game);
        var flat = FlatSheet.Render(model, palette, new FlatSheet.Options { IncludeBack = false });
        var art = new FlatImage(flat.Width, flat.Height);
        var rng = new Random(7);
        for (var y = 0; y < flat.Height; y++)
            for (var x = 0; x < flat.Width; x++)
            {
                var (r, g, b, a) = flat.Pixel(x, y);
                if (a < 128) { art.Set(x, y, (byte)(200 - y / 12), (byte)(210 - y / 14), (byte)(225 - y / 16)); continue; }
                var shade = 0.82 + 0.36 * x / flat.Width + (rng.NextDouble() - 0.5) * 0.10;
                art.Set(x, y, (byte)Math.Clamp(r * shade, 0, 255), (byte)Math.Clamp(g * shade, 0, 255), (byte)Math.Clamp(b * shade, 0, 255));
            }
        var stats = BodyStyleStats.Analyse(game, AllBodies(game).Select(b2 => b2.Data));
        var result = GameStyle.Convert(art, palette, new StyleOptions { Colours = 12, Allowed = stats.RecommendedDisplay(3), BackFromFront = false });
        // compare: crop both to their subject and resample to a grid
        FlatImage Norm(FlatImage s) { var box = s.OpaqueBounds() ?? (0, 0, s.Width, s.Height); return s.Crop(box.X, box.Y, box.Width, box.Height).Resize(200, 400); }
        var a0 = Norm(flat); var b0 = Norm(result.Sheet);
        var overlap = FlatImage.SilhouetteOverlap(a0, b0);
        double error = 0; var n = 0;
        for (var y = 0; y < 400; y++) for (var x = 0; x < 200; x++)
        {
            if (a0.Alpha(x, y) < 128 || b0.Alpha(x, y) < 128) continue;
            var p = a0.Pixel(x, y); var q = b0.Pixel(x, y);
            error += LabColour.Distance(LabColour.FromRgb(p.R, p.G, p.B), LabColour.FromRgb(q.R, q.G, q.B)); n++;
        }
        Console.WriteLine($"LBA{game} body {body}: silhouette overlap {overlap:P0}, mean colour error {error / Math.Max(1, n):F1} (dE), {result.PaletteIndices.Length} colours, {result.Regions} regions; {result.Notes}");
        using (var bmp = ToBitmap(art)) bmp.Save(Path.Combine(Path.GetTempPath(), $"styletest_lba{game}_{body}_art.png"), ImageFormat.Png);
        using (var bmp = ToBitmap(result.Sheet)) bmp.Save(Path.Combine(Path.GetTempPath(), $"styletest_lba{game}_{body}_result.png"), ImageFormat.Png);
        var both = GameStyle.Convert(art, palette, new StyleOptions { Colours = 12, Allowed = stats.RecommendedDisplay(3), BackFromFront = true });
        using (var bmp = ToBitmap(both.Sheet)) bmp.Save(Path.Combine(Path.GetTempPath(), $"styletest_lba{game}_{body}_sheet.png"), ImageFormat.Png);
        return 0;
    }

    // colours <game> <body>: the body's palette indices with their whole 16-step ramps as RGB
    private static int Colours(int game, int body)
    {
        var palette = PaletteBytes(game);
        var model = Body.Read(new Hqr(Generator.BodyArchive(Folder(game))).Read(body), game);
        foreach (var c in FlatSheet.Colours(model))
        {
            var bank = c & ~15;
            Console.WriteLine($"colour {c} (bank {bank / 16}, position {c & 15}, used by {model.Faces.Count(f => f.Colour == c)} faces of types {string.Join(",", model.Faces.Where(f => f.Colour == c).Select(f => f.Material).Distinct())}):");
            Console.WriteLine("    ramp " + string.Join(" ", Enumerable.Range(0, 16).Select(p => $"{palette[(bank + p) * 3]},{palette[(bank + p) * 3 + 1]},{palette[(bank + p) * 3 + 2]}")));
        }
        return 0;
    }

    private static (int Outward, int Inward) WindingOf(Body body)
    {
        var world = body.World();
        var boneOf = new int[world.Length];
        for (var b = 0; b < body.Bones.Count; b++) for (var i = body.Bones[b].Start; i < body.Bones[b].Start + body.Bones[b].Count; i++) boneOf[i] = b;
        var centres = body.Bones.Select(b => b.Count == 0 ? System.Numerics.Vector3.Zero : world.Skip(b.Start).Take(b.Count).Aggregate(System.Numerics.Vector3.Zero, (a, v) => a + v) / b.Count).ToArray();
        int outward = 0, inward = 0;
        foreach (var f in body.Faces)
        {
            var n = System.Numerics.Vector3.Zero;
            for (var i = 0; i < f.Points.Length; i++)
            {
                var a = world[f.Points[i]]; var b = world[f.Points[(i + 1) % f.Points.Length]];
                n += new System.Numerics.Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
            }
            var centre = f.Points.Select(p => world[p]).Aggregate(System.Numerics.Vector3.Zero, (a, v) => a + v) / f.Points.Length;
            var dot = System.Numerics.Vector3.Dot(n, centre - centres[boneOf[f.Points[0]]]);
            if (dot > 0) outward++; else if (dot < 0) inward++;
        }
        return (outward, inward);
    }

    private static int Header(string file, int index)
    {
        var b = new Hqr(Path.Combine(Folders[1], file)).Read(index);
        Console.WriteLine($"{file}[{index}] {b.Length} bytes: " + string.Join(" ", Enumerable.Range(0, 24).Select(i => BitConverter.ToInt32(b, i * 4))));
        return 0;
    }

    private static int Ress(int game, int index)
    {
        var e = new Hqr(Path.Combine(Folder(game), "RESS.HQR")).Read(index);
        Console.WriteLine($"RESS[{index}] of LBA{game}: {e.Length} bytes, head {Convert.ToHexString(e.AsSpan(0, Math.Min(32, e.Length)))}");
        return 0;
    }

    // object <game> <file.hqr> <index> <yaw degrees> <out.png>: a body (animated or not) drawn with the Body Studio renderer, textures included.
    private static int ObjectPicture(int game, string file, int index, double yaw, string output)
    {
        var folder = Folder(game);
        var body = Body.Read(new Hqr(Path.Combine(folder, file)).Read(index), game, true);
        if (game == 2) body.TexturePage = new Hqr(Path.Combine(folder, "RESS.HQR")).Read(6);
        var palette = Generator.Palette(folder);
        using var bmp = Renderer.Render(body, palette, 700, 700, (float)(yaw * Math.PI / 180), false);
        bmp.Save(output, ImageFormat.Png);
        Console.WriteLine($"{output}: {body.Vertices.Count} points, {body.Faces.Count} polygons ({body.Faces.Count(f => f.Texture is not null)} textured), {body.Textures.Length} textures");
        return 0;
    }

    // bodyroundtrip: every body of both games (and LBA2's fixed objects) reads, writes and reads back with the same points, bones and polygons (textured ones with their
    // texture handle and coordinates).
    private static int BodyRoundTrip()
    {
        var failures = 0;
        foreach (var (game, file, allowStatic) in new[] { (1, "BODY.HQR", false), (2, "BODY.HQR", false), (2, "OBJFIX.HQR", true) })
        {
            var hqr = new Hqr(Path.Combine(Folder(game), file));
            int ok = 0, skipped = 0, textured = 0;
            for (var i = 0; i < hqr.Count; i++)
            {
                Body a;
                try { a = Body.Read(hqr.Read(i), game, allowStatic); } catch (Exception) { skipped++; continue; }
                Body b;
                try { b = Body.Read(a.Write(), game, allowStatic); }
                catch (Exception e) { Console.WriteLine($"  LBA{game} {file}[{i}]: written body does not read back: {e.Message}"); failures++; continue; }
                static string Key(Face f) => string.Join(",", f.Points) + (f.Texture is null ? "" : "|" + f.Texture.Handle + "|" + string.Join(",", f.Texture.UV));
                var same = a.Vertices.SequenceEqual(b.Vertices) && a.Bones.Count == b.Bones.Count && a.Bones.Zip(b.Bones).All(p => p.First.Start == p.Second.Start && p.First.Count == p.Second.Count && p.First.Pivot == p.Second.Pivot)
                    && a.Faces.Select(Key).OrderBy(k => k).SequenceEqual(b.Faces.Select(Key).OrderBy(k => k)) && a.Textures.SequenceEqual(b.Textures)
                    && a.Lines.Count == b.Lines.Count && a.Spheres.Count == b.Spheres.Count;
                if (a.Faces.Any(f => f.Texture is not null)) textured++;
                if (same) ok++; else { failures++; if (failures < 8) Console.WriteLine($"  LBA{game} {file}[{i}]: differs after a write and read"); }
            }
            Console.WriteLine($"LBA{game} {file}: {ok} bodies survive a write and read ({textured} with textured polygons), {skipped} not readable as bodies");
        }
        return failures == 0 ? 0 : 1;
    }

    // formsmoke: Body Studio's window builds and shows with its new controls (the flat-picture buttons, the game lighting box).
    [STAThread]
    private static int FormSmoke()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        using var form = new MainForm();
        form.Show();
        System.Windows.Forms.Application.DoEvents();
        static IEnumerable<System.Windows.Forms.Control> All(System.Windows.Forms.Control c) { foreach (System.Windows.Forms.Control child in c.Controls) { yield return child; foreach (var d in All(child)) yield return d; } }
        var texts = All(form).Select(c => c.Text).Where(t => t.Length > 0).ToList();
        var wanted = new[] { "Convert the reference image to game style", "Export a flat sheet of the selected template…", "Game lighting: shade the body like the game's own characters", "Flat colours" };
        var missing = wanted.Where(w => !texts.Any(t => t == w)).ToList();
        Console.WriteLine(missing.Count == 0 ? $"body studio window: ok ({texts.Count} labelled controls)" : "MISSING: " + string.Join(" | ", missing));
        form.Close();
        return missing.Count == 0 ? 0 : 1;
    }

    // lba1lit <body>: an LBA1 body written back lit reads back with normals for every point, and the game's own shading maths gives it about the
    // same light as the original (the original's normals are shared and hand-tuned, ours are per point).
    private static int Lba1Lit(int index)
    {
        var retail = Body.Read(new Hqr(Generator.BodyArchive(Folders[0])).Read(index), 1);
        var copy = Body.Read(new Hqr(Generator.BodyArchive(Folders[0])).Read(index), 1);
        copy.Lit = true;
        var written = copy.Write();
        var back = Body.Read(written, 1);
        Console.WriteLine($"retail: {retail.Normals.Count} normals, faces by type {string.Join(" ", retail.Faces.GroupBy(f => f.Material).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}");
        Console.WriteLine($"lit rewrite: {back.Normals.Count} normals for {back.Vertices.Count} points, faces by type {string.Join(" ", back.Faces.GroupBy(f => f.Material).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}");
        var ok = back.Normals.Count == back.Vertices.Count && back.Faces.All(f => f.Material == 9 && f.PointNormals!.All(n => n >= 0 && n < back.Normals.Count)) && back.NormalBone.Length == back.Normals.Count;
        Console.WriteLine($"normals per point and valid references: {(ok ? "ok" : "FAILED")}");
        // the game's lighting (Lba1Shading) with a typical light: mean intensity over the faces' corners
        double Mean(Body b)
        {
            var identity = b.Bones.Select(_ => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }).ToArray();
            var shading = new Lba1Shading { BoneMatrices = identity, ActorBeta = 0, AlphaLight = 100, BetaLight = 200 };
            var lights = shading.Intensities(b);
            double sum = 0; int n = 0;
            foreach (var f in b.Faces) foreach (var p in f.PointNormals ?? Array.Empty<int>()) if (p < lights.Length) { sum += lights[p]; n++; }
            return n == 0 ? 0 : sum / n;
        }
        Console.WriteLine($"mean light on face corners: retail {Mean(retail):F2}, rewritten {Mean(back):F2}");
        return ok ? 0 : 1;
    }

    // winding <game> <body>: does a face's vertex order (Newell normal) point away from its bone's centre in the game's own bodies?
    private static int Winding(int game, int first, int count)
    {
        var hqr = new Hqr(Generator.BodyArchive(Folder(game)));
        int outward = 0, inward = 0;
        for (var index = first; index < first + count; index++)
        {
            Body body;
            try { body = Body.Read(hqr.Read(index), game); } catch (Exception) { continue; }
            var world = body.World();
            var boneOf = new int[world.Length];
            for (var b = 0; b < body.Bones.Count; b++) for (var i = body.Bones[b].Start; i < body.Bones[b].Start + body.Bones[b].Count; i++) boneOf[i] = b;
            var centres = body.Bones.Select(b => b.Count == 0 ? System.Numerics.Vector3.Zero : world.Skip(b.Start).Take(b.Count).Aggregate(System.Numerics.Vector3.Zero, (a, v) => a + v) / b.Count).ToArray();
            foreach (var f in body.Faces)
            {
                var n = System.Numerics.Vector3.Zero;
                for (var i = 0; i < f.Points.Length; i++)
                {
                    var a = world[f.Points[i]]; var b = world[f.Points[(i + 1) % f.Points.Length]];
                    n += new System.Numerics.Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
                }
                var centre = f.Points.Select(p => world[p]).Aggregate(System.Numerics.Vector3.Zero, (a, v) => a + v) / f.Points.Length;
                var away = centre - centres[boneOf[f.Points[0]]];
                var dot = System.Numerics.Vector3.Dot(n, away);
                if (dot > 0) outward++; else if (dot < 0) inward++;
            }
        }
        Console.WriteLine($"LBA{game} bodies {first}..{first + count - 1}: newell normal points away from the bone centre for {outward} faces, towards it for {inward}");
        return 0;
    }

    // normals <game> <body>: the lighting data of a retail body, to learn its conventions
    private static int Normals(int game, int index)
    {
        var b = new Hqr(Generator.BodyArchive(Folder(game))).Read(index);
        int I(int p) => BitConverter.ToInt32(b, p);
        int S(int p) => BitConverter.ToInt16(b, p);
        int U(int p) => BitConverter.ToUInt16(b, p);
        if (game == 2)
        {
            Console.WriteLine($"header: groups {I(32)}@{I(36)} points {I(40)}@{I(44)} normals {I(48)}@{I(52)} faceNormals {I(56)}@{I(60)} polys {I(64)}@{I(68)} lines {I(72)}@{I(76)} spheres {I(80)}@{I(84)} textures {I(88)} size {I(92)} flags {I(0):X}");
            var n = I(48); var o = I(52);
            for (var i = 0; i < Math.Min(n, 12); i++)
            {
                int x = S(o + i * 8), y = S(o + i * 8 + 2), z = S(o + i * 8 + 4), w = U(o + i * 8 + 6);
                Console.WriteLine($"  vertex normal {i}: ({x},{y},{z}) length {Math.Sqrt(x * x + y * y + z * z):F0} extra {w}");
            }
            var fn = I(56); var fo = I(60);
            for (var i = 0; i < Math.Min(fn, 6); i++)
            {
                int x = S(fo + i * 8), y = S(fo + i * 8 + 2), z = S(fo + i * 8 + 4), w = U(fo + i * 8 + 6);
                Console.WriteLine($"  face normal {i}: ({x},{y},{z}) length {Math.Sqrt(x * x + y * y + z * z):F0} extra {w}");
            }
            // the first few polygon records
            var p = I(68); var end = I(76);
            var shown = 0;
            while (p < end && shown < 4)
            {
                int type = U(p), count = U(p + 2), size = I(p + 4); p += 8;
                var quad = (type & 32768) != 0; var t = type & 255;
                var stride = (t > 7) ? (quad ? 32 : 24) : 12;
                Console.WriteLine($"  polygon block type {t}{(quad ? " quad" : " tri")} x{count}, {size} bytes; first: {string.Join(" ", Enumerable.Range(0, stride / 2).Select(k => U(p + k * 2)))}");
                p += count * stride; shown++;
            }
        }
        else
        {
            var body = Body.Read(b, 1);
            Console.WriteLine($"{body.Normals.Count} normals for {body.Bones.Count} bones, {body.Vertices.Count} points; per bone normals: {string.Join(",", body.Bones.Select(x => BitConverter.ToUInt16(x.Record, 18)))}");
            foreach (var (n, i) in body.Normals.Take(12).Select((n, i) => (n, i)))
                Console.WriteLine($"  normal {i}: ({n.X},{n.Y},{n.Z}) length {Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z):F0} range {n.Range}");
            foreach (var f in body.Faces.Take(6))
                Console.WriteLine($"  face type {f.Material}: {f.Points.Length} points, colour {f.Colour}, face normal {f.FaceNormal}, point normals [{(f.PointNormals is null ? "" : string.Join(",", f.PointNormals))}]");
        }
        return 0;
    }

    private static int Stats(int game)
    {
        var stats = BodyStyleStats.Analyse(game, AllBodies(game).Select(b => b.Data));
        Console.WriteLine($"LBA{game}: {stats.Bodies} bodies read ({stats.Skipped} skipped: static or unsupported)");
        Console.WriteLine($"  colours per body: median {stats.MedianColoursPerBody()}, min {stats.ColoursPerBody.DefaultIfEmpty().Min()}, max {stats.ColoursPerBody.DefaultIfEmpty().Max()}");
        Console.WriteLine($"  polygons per body: median {stats.FacesPerBody.OrderBy(x => x).ElementAtOrDefault(stats.FacesPerBody.Count / 2)}, max {stats.FacesPerBody.DefaultIfEmpty().Max()}");
        Console.WriteLine($"  ramp positions (index & 15) by use: {string.Join(" ", stats.RampPositions())}");
        var banks = Enumerable.Range(0, 16).Select(b => Enumerable.Range(0, 16).Sum(o => stats.Uses[b * 16 + o])).ToArray();
        Console.WriteLine($"  banks (index >> 4) by use:        {string.Join(" ", banks)}");
        Console.WriteLine($"  colours used by 3+ bodies: {stats.Recommended(3).Length}");
        Console.WriteLine($"  polygons by type: {string.Join("  ", stats.MaterialFaces.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        return 0;
    }

    private static Bitmap ToBitmap(FlatImage image)
    {
        var bmp = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(image.Bgra, 0, data.Scan0, image.Bgra.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    private static FlatImage FromBitmap(Bitmap bmp)
    {
        using var copy = bmp.Clone(new Rectangle(0, 0, bmp.Width, bmp.Height), PixelFormat.Format32bppArgb);
        var data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[copy.Width * copy.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        copy.UnlockBits(data);
        return new FlatImage(copy.Width, copy.Height, bytes);
    }

    private static int Sheet(int game, int body, string output)
    {
        var palette = PaletteBytes(game);
        var model = Body.Read(new Hqr(Generator.BodyArchive(Folder(game))).Read(body), game);
        var sheet = FlatSheet.Render(model, palette);
        using var bmp = ToBitmap(sheet);
        bmp.Save(output, ImageFormat.Png);
        Console.WriteLine($"{output}: {sheet.Width}x{sheet.Height}, {FlatSheet.Colours(model).Length} colours, {model.Faces.Count} polygons");
        return 0;
    }

    private static int Sheets(int game, string directory, int first, int count)
    {
        Directory.CreateDirectory(directory);
        var palette = PaletteBytes(game);
        var done = 0;
        foreach (var (index, data) in AllBodies(game).Where(b => b.Index >= first))
        {
            Body model;
            try { model = Body.Read(data, game); } catch (InvalidDataException) { continue; }
            var sheet = FlatSheet.Render(model, palette, new FlatSheet.Options { Height = 384 });
            using var bmp = ToBitmap(sheet);
            bmp.Save(Path.Combine(directory, $"lba{game}_body{index}.png"), ImageFormat.Png);
            Console.WriteLine($"body {index}: {model.Faces.Count} polygons, {FlatSheet.Colours(model).Length} colours");
            if (++done >= count) break;
        }
        return 0;
    }

    private static int Style(int game, string input, string output, int colours)
    {
        var palette = PaletteBytes(game);
        var stats = BodyStyleStats.Analyse(game, AllBodies(game).Select(b => b.Data));
        using var source = new Bitmap(input);
        var result = GameStyle.Convert(FromBitmap(source), palette, new StyleOptions { Colours = colours, Allowed = stats.RecommendedDisplay(3) });
        using var bmp = ToBitmap(result.Sheet);
        bmp.Save(output, ImageFormat.Png);
        Console.WriteLine($"{output}: {result.Sheet.Width}x{result.Sheet.Height}; {result.Notes}; palette indices {string.Join(",", result.PaletteIndices)}");
        return 0;
    }

    private static int RoundTrip(int game, int body)
    {
        var folder = Folder(game);
        var palette = PaletteBytes(game);
        var model = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(body), game);
        var sheet = FlatSheet.Render(model, palette);
        Console.WriteLine($"  sheet {sheet.Width}x{sheet.Height}, opaque pixels {sheet.OpaqueCount()} of {sheet.Width * sheet.Height}, bounds {sheet.OpaqueBounds()}");
        var work = Path.Combine(Path.GetTempPath(), "bodypipe_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        try
        {
            var path = Path.Combine(work, "sheet.png");
            using (var bmp = ToBitmap(sheet)) bmp.Save(path, ImageFormat.Png);
            foreach (var method in new[] { "New humanoid", "Fit template" })
            {
                var settings = new Settings
                {
                    ImagePath = path, Lba1Folder = Folders[0], Lba2Folder = Folders[1], Lba1Body = game == 1 ? body : 0, Lba2Body = game == 2 ? body : 0,
                    Mask = "Transparent background", Layout = "Front + back", Method = method, AutoCrop = true, DetailBudget = 300,
                };
                Generated generated;
                try { generated = Generator.Generate(settings, game); }
                catch (Exception e) { Console.WriteLine($"  {method}: {e.Message}"); continue; }
                var again = FlatSheet.Render(generated.Body, palette, new FlatSheet.Options { Height = sheet.Height });
                var (wOut, wIn) = WindingOf(generated.Body);
                Console.WriteLine($"    generated winding: {wOut} faces outward, {wIn} inward");
                var overlap = Overlap(sheet, again);
                var colours = FlatSheet.Colours(generated.Body);
                Console.WriteLine($"  {method}: {generated.Body.Faces.Count} polygons (original {model.Faces.Count}), {colours.Length} colours (original {FlatSheet.Colours(model).Length}), front/back silhouette overlap {overlap.Front:P0} / {overlap.Back:P0}");
                using var bmp = ToBitmap(again);
                bmp.Save(Path.Combine(Path.GetTempPath(), $"roundtrip_lba{game}_{body}_{method.Split(' ')[0]}.png"), ImageFormat.Png);
            }
            using var original = ToBitmap(sheet);
            original.Save(Path.Combine(Path.GetTempPath(), $"roundtrip_lba{game}_{body}_original.png"), ImageFormat.Png);
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
        return 0;
    }

    // Silhouette overlap of the front and of the back views: each view is cropped to its bounding box and resized to a common
    // grid, so a generated body of another width still compares by shape.
    private static (double Front, double Back) Overlap(FlatImage a, FlatImage b)
    {
        static FlatImage Half(FlatImage s, bool right) => s.Crop(right ? s.Width / 2 : 0, 0, s.Width / 2, s.Height);
        static FlatImage Norm(FlatImage s)
        {
            var box = s.OpaqueBounds() ?? (0, 0, s.Width, s.Height);
            return s.Crop(box.X, box.Y, box.Width, box.Height).Resize(200, 400);
        }
        double Score(bool right) => FlatImage.SilhouetteOverlap(Norm(Half(a, right)), Norm(Half(b, right)));
        return (Score(false), Score(true));
    }
}
