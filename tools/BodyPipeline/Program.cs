using System.Drawing;
using System.Drawing.Imaging;
using LBAAssembler;
using LbaBodyStudio;

namespace BodyPipeline;

// dotnet run --project tools/BodyPipeline -- <command> ...
//   stats <game>                             colours the game's bodies use
//   sheet <game> <body> <out.png>            flat front+back sheet of a body
//   sheets <game> <outDir> [first] [count]   sheets of several bodies
//   style <game> <in.png> <out.png> [k]      convert artwork to a game-style sheet
//   roundtrip <game> <body>                  body -> sheet -> generated body -> sheet, with the overlap of the silhouettes
//   render3d <game> <body> <outDir>          any retail body's own silhouette, regenerated onto the Twinsen rig, 3D-rendered
//                                            at several angles (front/3-4/profile/back) -- a torso/limb join gap (see
//                                            widestance below) shows as a background-coloured sliver from some angle even
//                                            when a flat front sheet or a straight-on render hides it
//   dummybody <outDir>                       regenerates the in-game placeholder body (Assets/DummyBody.lm2): a clean,
//                                            pure-black, legs-together silhouette (no lossy game-style-conversion step,
//                                            which is what left the old asset a dark teal rather than black) run through
//                                            New humanoid with HeadDetails/BandanaText, so the legs are guaranteed
//                                            attached (HipAttach) and the bandana/teeth read clearly (NegativeZFront
//                                            must be true -- see the comment at its Settings -- or the lettering and
//                                            teeth silently render invisible while everything else still looks fine).
//                                            Writes dummybody.lm2 (LBA2 payload, Body.Write()'s own bytes) plus 3D
//                                            renders and a hip closeup to outDir for review before it is copied over
//                                            the real asset.
//   currentdummy <outDir>                    the same renders for the EXISTING Assets/DummyBody.lm2, unmodified -- a
//                                            before/after baseline.
//   reftest <image> <outDir>                New humanoid generation from an arbitrary real-world reference photo
//                                            (single front view, any background), rendered at several angles plus a
//                                            numbered bone-skeleton overlay, for judging how close Body Studio gets
//                                            on challenging (non-synthetic, non-silhouette) test data. REFTEST_BUDGET
//                                            (DetailBudget), REFTEST_TAG (output filename suffix) and REFTEST_DUMP=1
//                                            (per-bone world-vertex dump to stderr) are optional env var overrides.
//   hqrbody <image> <out.hqr>                same generation as reftest, written as a single-entry HQR archive
//                                            instead of renders (LbaBodyStudio.Hqr.Build) -- for building small
//                                            test/debug archives like BodyStudio/TestBodies/mario.hqr.
//   widestance <outDir> [bandana]            regression test for a real bug (2026-09-22): a hand-drawn wide-stance
//                                            silhouette with an asymmetric accessory along one leg, run through New
//                                            humanoid (add "bandana" for the HeadDetails branch too) and rendered
//                                            including a zoomed, magenta-background hip closeup where any gap between a
//                                            leg and the torso is unmissable. Humanoid.Build's Loft used to let each limb
//                                            independently re-derive its own top ring from the source image, so a leg
//                                            whose silhouette read differently from the torso's at the hip row could
//                                            come out visibly detached; HipAttach now anchors each leg's own top ring to
//                                            a sub-span of the torso's own hip ring instead, which cannot mismatch by
//                                            construction. This command should show a clean join at every camera angle.
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
            "render3d" => Render3D(game, args.Length > 2 ? int.Parse(args[2]) : 0, args.Length > 3 ? args[3] : Path.GetTempPath()),
            "dummybody" => DummyBody(args.Length > 1 ? args[1] : Path.GetTempPath()),
            "currentdummy" => CurrentDummy(args.Length > 1 ? args[1] : Path.GetTempPath()),
            "widestance" => WideStance(args.Length > 1 ? args[1] : Path.GetTempPath(), args.Length > 2 && args[2] == "bandana"),
            "reftest" => RefTest(args[1], args.Length > 2 ? args[2] : Path.GetTempPath()),
            "hqrbody" => HqrBody(args[1], args[2]),
            "mariocustom" => MarioCustomCmd(args[1]),
            "mariocustomlba1" => MarioCustomLba1Cmd(args[1]),
            "luigicustom" => LuigiCustomCmd(args[1]),
            "luigicustomlba1" => LuigiCustomLba1Cmd(args[1]),
            "bowsercustom" => BowserCustomCmd(args[1]),
            "peachcustom" => PeachCustomCmd(args[1]),
            "toadcustom" => ToadCustomCmd(args[1]),
            "yoshicustom" => YoshiCustomCmd(args[1]),
            "package" => PackageRoster(args.Length > 1 ? args[1] : "Assets"),
            "testanims" => TestAnimsCmd(args[1]),
            "dumpheader" => DumpHeader(args[1], int.Parse(args[2])),
            "hqrpreview" => HqrPreview(args[1], int.Parse(args[2]), args.Length > 3 ? args[3] : Path.GetTempPath(), args.Length > 4 ? int.Parse(args[4]) : 2),
            "hqrpreviewress" => HqrPreviewRess(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4], args.Length > 5 ? int.Parse(args[5]) : 2),
            "testappend" => TestAppend(game),
            "normals" => Normals(game, int.Parse(args[2])),
            "enginebody" => EngineBody(args[1], args[2], args.Skip(3).DefaultIfEmpty("humanoid unlit").ToArray()),
            "styletest" => StyleTest(game, args.Length > 2 ? int.Parse(args[2]) : 0),
            "colours" => Colours(game, args.Length > 2 ? int.Parse(args[2]) : 0),
            // NOT `game` (the top-level heuristic assumes args[1] is numeric, true for every other
            // command here but not this one, whose args[1] is a channel NAME like "green" -- using
            // it silently always scanned LBA2 regardless of a 3rd arg, found live 2026-09-23 when
            // `findcolour green 1` returned LBA2's own colour 133 again instead of scanning LBA1).
            "findcolour" => FindColour(args.Length > 2 ? int.Parse(args[2]) : 2, args[1]),
            "ramp" => Ramp(args.Length > 2 ? int.Parse(args[2]) : 2, int.Parse(args[1])),
            "facecolours" => FaceColours(args[1], int.Parse(args[2]), args.Length > 3 ? int.Parse(args[3]) : 2),
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

    // Scans every real body in both games' own BODY.HQR for face colours whose ramp-start RGB is
    // dominated by one channel (green/blue/etc) -- the same "reuse a real, already-correctly-lit
    // donor colour" approach MarioCustom.cs uses for its own colours (see its own comment: naive
    // nearest-RGB picking lands on the wrong position within a ramp and renders wrong once the
    // engine's own light-step arithmetic walks forward from it). Prints candidates actually used
    // by at least one real face, sorted by how much they're actually used (a colour a real body
    // relies on for a large visible area is safer to reuse than an obscure one-face colour).
    // Dumps a candidate colour's WHOLE 16-entry bank, regardless of how it's actually used --
    // findcolour only checks a candidate's OWN isolated RGB, which isn't enough (a real bug found
    // 2026-09-23: LBA1 colour 22 looked like a safe brown alone, but its own bank turned out to be a
    // fire/glow effect ramp, not a smooth shade progression -- see reference-anim-hqr-format memory).
    // Always eyeball this before committing to a findcolour result in a character file.
    private static int Ramp(int game, int colour)
    {
        var palette = PaletteBytes(game);
        var bank = colour & ~15;
        Console.WriteLine($"colour {colour} (bank {bank / 16}, position {colour & 15}):");
        Console.WriteLine("    " + string.Join(" ", Enumerable.Range(0, 16).Select(p => $"{palette[(bank + p) * 3]},{palette[(bank + p) * 3 + 1]},{palette[(bank + p) * 3 + 2]}")));
        return 0;
    }

    // Diagnostic (2026-09-23): histogram of a body's own face colour indices (ramp-start values, see
    // Body.cs's own Face.Colour comment), most-used first -- lets a specific body's dominant "robe"/"skin"/etc
    // colour index be identified so its own ramp can be checked (BodyPipeline ramp <colour>) for the "unsafe
    // near the edge of its 16-wide ramp block" issue this project has hit before (Mario's own shoe colour).
    private static int FaceColours(string hqrPath, int index, int game = 2)
    {
        var body = Body.Read(new Hqr(hqrPath).Read(index), game);
        var counts = new Dictionary<int, int>();
        foreach (var f in body.Faces) counts[f.Colour] = counts.GetValueOrDefault(f.Colour) + 1;
        foreach (var (colour, count) in counts.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  colour {colour} (bank {colour / 16}, position {colour % 16}): {count} faces");
        var textured = body.Faces.Count(f => f.Texture is not null);
        Console.WriteLine($"  {textured} of {body.Faces.Count} faces are textured; body.Textures.Length={body.Textures.Length}; body.TexturePage is {(body.TexturePage is null ? "null" : $"{body.TexturePage.Length} bytes")}");
        return 0;
    }

    private static int FindColour(int game, string channel)
    {
        // "pink" (axis 3) is not a single dominant channel like red/green/blue -- it's a magenta-leaning
        // signature (red AND blue both notably above green, red the stronger of the two) needed for Peach's
        // own dress (PeachCustom.cs), since plain red-channel filtering only turns up browns/bricks (red
        // dominant over both g and b, no requirement that b track with r) rather than anything pink.
        // "white" (axis 4) is likewise not a dominant-channel signature -- a neutral colour has no channel that
        // beats the others, so it needs its own test (all three channels bright AND close together) instead.
        // Needed for Toad's own pale skin/base cloth and his cap's white spots (ToadCustom.cs); the same "reuse a
        // real, already-lit donor colour instead of nearest-RGB matching" reasoning as the other axes.
        var axis = channel.ToLowerInvariant() switch { "red" or "r" => 0, "green" or "g" => 1, "blue" or "b" => 2, "pink" or "magenta" or "p" => 3, "white" or "cream" or "w" => 4, _ => -1 };
        if (axis < 0) { Console.WriteLine("channel must be red|green|blue|pink|white"); return 2; }
        var palette = PaletteBytes(game);
        var counts = new Dictionary<int, int>();
        foreach (var (index, data) in AllBodies(game))
        {
            Body model;
            try { model = Body.Read(data, game); } catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException) { continue; }
            foreach (var f in model.Faces)
            {
                var c = f.Colour;
                if (c < 0 || c > 255) continue;
                int r = palette[c * 3], g = palette[c * 3 + 1], b = palette[c * 3 + 2];
                var dominant = axis == 0 ? r > g + 15 && r > b + 15 : axis == 1 ? g > r + 15 && g > b + 15 : axis == 2 ? b > r + 15 && b > g + 15
                    : axis == 3 ? r > g + 20 && b > g + 10 && r >= b
                    : Math.Min(r, Math.Min(g, b)) >= 130 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 25;
                var bright = axis == 0 || axis == 3 ? r : axis == 1 ? g : axis == 2 ? b : Math.Min(r, Math.Min(g, b));
                if (dominant && bright >= 60) counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        }
        foreach (var (c, n) in counts.OrderByDescending(kv => kv.Value).Take(20))
            Console.WriteLine($"colour {c}: rgb=({palette[c*3]},{palette[c*3+1]},{palette[c*3+2]}) used by {n} faces across the archive");
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

    // currentdummy <outDir>: renders the EXISTING, not-yet-replaced Assets/DummyBody.lm2 the same way as dummybody's own
    // renders, for a direct before/after comparison (colour, leg gap, bandana/teeth legibility).
    private static int CurrentDummy(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Assets", "DummyBody.lm2"));
        var body = Body.Read(bytes, 2);
        var palette = Generator.Palette(Folder(2));
        foreach (var (name, yaw) in new (string, float)[] { ("front", 0f), ("threequarter", 0.7f), ("profile", MathF.PI / 2), ("back", MathF.PI) })
        {
            using var render = Renderer.Render(body, palette, 700, 900, yaw, false, background: Color.FromArgb(40, 60, 90));
            render.Save(Path.Combine(outDir, $"currentdummy_{name}.png"), ImageFormat.Png);
        }
        foreach (var (name, yaw) in new (string, float)[] { ("front", 0f), ("angle", 0.5f) })
        using (var headClose = Renderer.Render(body, palette, 900, 900, yaw, false, headOnly: true, background: Color.FromArgb(40, 60, 90)))
            headClose.Save(Path.Combine(outDir, $"currentdummy_head_closeup_{name}.png"), ImageFormat.Png);
        using (var close = Renderer.Render(body, palette, 1400, 1800, 0.5f, false, background: Color.FromArgb(230, 30, 200)))
        {
            var hipArea = new Rectangle(close.Width / 4, close.Height * 2 / 5, close.Width / 2, close.Height / 5);
            using var cropped = close.Clone(hipArea, close.PixelFormat);
            cropped.Save(Path.Combine(outDir, "currentdummy_hip_closeup.png"), ImageFormat.Png);
        }
        Console.WriteLine($"  current dummy: {body.Faces.Count} polygons, {body.Vertices.Count} points, Lit={body.Lit}, colours used: {string.Join(",", FlatSheet.Colours(body))}");
        Console.WriteLine($"  renders in {outDir}");
        return 0;
    }

    // The in-game placeholder body (Assets/DummyBody.lm2): a plain standing silhouette, legs together, arms at the
    // sides -- fed straight into New humanoid with no game-style-conversion pass (that lossy flattening step, not the
    // generator itself, is why the old asset came out dark teal instead of black: it re-quantises the picture through
    // the game's own allowed colours before the generator ever sees it). A pure black source pixel needs no such help;
    // the generator samples it directly.
    private static int DummyBody(string outDir)
    {
        Directory.CreateDirectory(outDir);
        const int w = 300, h = 700;
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var black = new SolidBrush(Color.Black);
            int cx = w / 2;
            g.FillEllipse(black, cx - 30, 20, 60, 70);                                                     // head
            g.FillPolygon(black, new PointF[] { new(cx - 45, 95), new(cx + 45, 95), new(cx + 55, 330), new(cx - 55, 330) }); // torso, tapering out slightly to the hips
            g.FillRectangle(black, cx - 90, 100, 30, 220);                                                  // left arm
            g.FillRectangle(black, cx + 60, 100, 30, 220);                                                  // right arm
            // legs together (a normal stance, not the wide-stance stress test): each leg starts right where the torso
            // ends and the inner edges stay close and roughly parallel, the kind of small, constant gap a standing
            // figure's own crotch and ankles actually have -- this is the easy case for HipAttach, not the hard one.
            g.FillPolygon(black, new PointF[] { new(cx - 58, 328), new(cx - 6, 328), new(cx - 8, 650), new(cx - 38, 650) });  // left leg
            g.FillPolygon(black, new PointF[] { new(cx + 6, 328), new(cx + 58, 328), new(cx + 38, 650), new(cx + 8, 650) });  // right leg
            g.FillRectangle(black, cx - 48, 650, 45, 30);                                                   // left foot
            g.FillRectangle(black, cx + 3, 650, 45, 30);                                                    // right foot
        }
        var path = Path.Combine(outDir, "dummybody_source.png");
        bmp.Save(path, ImageFormat.Png);

        var settings = new Settings
        {
            ImagePath = path, Lba1Folder = Folders[0], Lba2Folder = Folders[1], Lba1Body = 0, Lba2Body = 0,
            Mask = "Dark subject", Threshold = 128, Layout = "Single front", Method = "New humanoid", AutoCrop = true, DetailBudget = 300,
            HeadDetails = true, BandanaText = "Phreak", Lit = false,
            // HeadDecoration.Add mirrors X and Z when this is false (its own front-facing convention is the opposite of a
            // plain "Single front" silhouette's). Left at the Settings default (false) here, the bandana/teeth geometry
            // still builds -- same polygon and colour counts either way -- but its band-front polygons and the ordinary
            // body polygons disagree about which way is front, so the "off" (white) cells of the letter grid and the
            // ordinary head skin end up drawn over each other in screen space: the bandana cloth, knot and tails (which
            // don't depend on this) still look right, but the lettering and teeth are invisible. True matches how the
            // shipped asset was made.
            NegativeZFront = true,
        };
        var generated = Generator.Generate(settings, 2);
        var written = generated.Body.Write();
        File.WriteAllBytes(Path.Combine(outDir, "dummybody.lm2"), written);
        var colours = FlatSheet.Colours(generated.Body);
        Console.WriteLine($"  {generated.Body.Faces.Count} polygons, {generated.Body.Vertices.Count} points, {colours.Length} distinct colours, {written.Length} bytes -> {Path.Combine(outDir, "dummybody.lm2")}");
        foreach (var (name, yaw) in new (string, float)[] { ("front", 0f), ("threequarter", 0.7f), ("profile", MathF.PI / 2), ("back", MathF.PI) })
        {
            using var render = Renderer.Render(generated.Body, generated.Palette, 700, 900, yaw, false, background: Color.FromArgb(40, 60, 90));
            render.Save(Path.Combine(outDir, $"dummybody_{name}.png"), ImageFormat.Png);
        }
        using (var close = Renderer.Render(generated.Body, generated.Palette, 1400, 1800, 0.5f, false, background: Color.FromArgb(230, 30, 200)))
        {
            var hipArea = new Rectangle(close.Width / 4, close.Height * 2 / 5, close.Width / 2, close.Height / 5);
            using var cropped = close.Clone(hipArea, close.PixelFormat);
            cropped.Save(Path.Combine(outDir, "dummybody_hip_closeup.png"), ImageFormat.Png);
        }
        foreach (var (name, yaw) in new (string, float)[] { ("front", 0f), ("angle", 0.5f) })
        using (var headClose = Renderer.Render(generated.Body, generated.Palette, 900, 900, yaw, false, headOnly: true, background: Color.FromArgb(40, 60, 90)))
            headClose.Save(Path.Combine(outDir, $"dummybody_head_closeup_{name}.png"), ImageFormat.Png);
        Console.WriteLine($"  source + renders in {outDir}");
        return 0;
    }

    // Diagnostic for the reported torso/leg gap: a hand-drawn silhouette with a wide stance (the legs already visibly
    // separated well above where the torso's own taper ends -- exactly what a stride, or legs apart around an object,
    // looks like) and a thin diagonal "held object" along one leg only, the way a knife/weapon held at the side would
    // read in a silhouette mask. Feeds it through the real New-humanoid generator and renders the result.
    private static int WideStance(string outDir, bool headDetails = false)
    {
        Directory.CreateDirectory(outDir);
        const int w = 300, h = 700;
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var black = new SolidBrush(Color.Black);
            // head
            g.FillEllipse(black, w / 2 - 30, 20, 60, 70);
            // torso, tapering slightly to the hips
            g.FillPolygon(black, new PointF[] { new(w / 2 - 45, 95), new(w / 2 + 45, 95), new(w / 2 + 55, 330), new(w / 2 - 55, 330) });
            // arms, straight down at the sides
            g.FillRectangle(black, w / 2 - 90, 100, 30, 220);
            g.FillRectangle(black, w / 2 + 60, 100, 30, 220);
            // legs: a wide stance -- already two separate silhouettes well above the torso's own bottom (y=330),
            // splitting apart from as high as y=300 -- and asymmetric (the right leg planted further out).
            g.FillPolygon(black, new PointF[] { new(w / 2 - 55, 300), new(w / 2 - 15, 300), new(w / 2 - 30, 650), new(w / 2 - 85, 650) });   // left leg
            g.FillPolygon(black, new PointF[] { new(w / 2 + 15, 300), new(w / 2 + 65, 300), new(w / 2 + 110, 650), new(w / 2 + 40, 650) });  // right leg, wider stance
            // a thin "held object" (knife/weapon) along the right leg only, from hip to knee
            using var pen = new Pen(Color.Black, 6);
            g.DrawLine(pen, w / 2 + 70, 310, w / 2 + 95, 470);
            // feet
            g.FillRectangle(black, w / 2 - 95, 650, 65, 30);
            g.FillRectangle(black, w / 2 + 30, 650, 90, 30);
        }
        var path = Path.Combine(outDir, "widestance_source.png");
        bmp.Save(path, ImageFormat.Png);

        var settings = new Settings
        {
            ImagePath = path, Lba1Folder = Folders[0], Lba2Folder = Folders[1], Lba1Body = 0, Lba2Body = 0,
            Mask = "Dark subject", Threshold = 128, Layout = "Single front", Method = "New humanoid", AutoCrop = true, DetailBudget = 300,
            HeadDetails = headDetails, BandanaText = "Phreak",
        };
        var generated = Generator.Generate(settings, 2);
        var suffix = headDetails ? "_bandana" : "";
        foreach (var (name, yaw) in new (string, float)[] { ("front", 0f), ("threequarter", 0.7f), ("profile", MathF.PI / 2) })
        {
            using var render = Renderer.Render(generated.Body, generated.Palette, 700, 900, yaw, false, background: Color.FromArgb(40, 60, 90));
            render.Save(Path.Combine(outDir, $"widestance{suffix}_{name}.png"), ImageFormat.Png);
            using var wire = Renderer.Render(generated.Body, generated.Palette, 700, 900, yaw, true, background: Color.FromArgb(40, 60, 90));
            wire.Save(Path.Combine(outDir, $"widestance{suffix}_{name}_wire.png"), ImageFormat.Png);
        }
        // A tight crop right around the hip/leg join, zoomed, so a gap of even a few pixels is unambiguous.
        using (var close = Renderer.Render(generated.Body, generated.Palette, 1400, 1800, 0.5f, false, background: Color.FromArgb(230, 30, 200)))
        {
            var hipArea = new Rectangle(close.Width / 4, close.Height * 2 / 5, close.Width / 2, close.Height / 5);
            using var cropped = close.Clone(hipArea, close.PixelFormat);
            cropped.Save(Path.Combine(outDir, $"widestance{suffix}_hip_closeup.png"), ImageFormat.Png);
        }
        Console.WriteLine($"  source + renders in {outDir}");
        return 0;
    }

    // Real-world reference photo -> New humanoid, no synthetic silhouette involved: a genuine stress test of the
    // background/subject mask, AutoCrop, and per-pixel colour projection against something Body Studio was never
    // tuned against (multiple saturated colours, a soft drop shadow, non-silhouette shading on a toy figure).
    private static int RefTest(string imagePath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var settings = new Settings
        {
            ImagePath = imagePath, Lba1Folder = Folders[0], Lba2Folder = Folders[1], Lba1Body = 0, Lba2Body = 0,
            Mask = "Background colour", Threshold = 45, Layout = "Single front", Method = "New humanoid", AutoCrop = true,
            DetailBudget = int.TryParse(Environment.GetEnvironmentVariable("REFTEST_BUDGET"), out var b) ? b : 460,
            HeadDetails = false, Lit = false,
        };
        var generated = Generator.Generate(settings, 2);
        var tag = Environment.GetEnvironmentVariable("REFTEST_TAG") ?? "";
        foreach (var (name, yaw) in new (string, float)[] { ("front", 0f), ("threequarter", 0.7f), ("profile", MathF.PI / 2), ("back", MathF.PI) })
        {
            using var render = Renderer.Render(generated.Body, generated.Palette, 700, 900, yaw, false, background: Color.FromArgb(40, 60, 90));
            render.Save(Path.Combine(outDir, $"reftest{tag}_{name}.png"), ImageFormat.Png);
        }
        using (var boned = Renderer.Render(generated.Body, generated.Palette, 700, 900, 0f, false, bones: true, background: Color.FromArgb(40, 60, 90)))
            boned.Save(Path.Combine(outDir, $"reftest{tag}_bones.png"), ImageFormat.Png);
        if (Environment.GetEnvironmentVariable("REFTEST_DUMP") == "1")
        {
            var world = generated.Body.World();
            for (int bi = 0; bi < generated.Body.Bones.Count; bi++)
            {
                var bone = generated.Body.Bones[bi];
                Console.Error.WriteLine($"bone {bi}: start={bone.Start} count={bone.Count}");
                for (int p = bone.Start; p < bone.Start + bone.Count; p++)
                {
                    var v = world[p];
                    Console.Error.WriteLine($"  v{p}: ({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})");
                }
            }
        }
        Console.WriteLine($"  generated: {generated.Body.Faces.Count} polygons, {generated.Body.Vertices.Count} points, colours used: {string.Join(",", FlatSheet.Colours(generated.Body))}");
        Console.WriteLine($"  renders in {outDir}");
        return 0;
    }

    // Same generation settings as reftest, but writes a single-entry HQR archive (LbaBodyStudio.Hqr.Build) instead
    // of renders -- for building test/debug archives like BodyStudio/TestBodies/mario.hqr.
    private static int HqrBody(string imagePath, string outFile)
    {
        var settings = new Settings
        {
            ImagePath = imagePath, Lba1Folder = Folders[0], Lba2Folder = Folders[1], Lba1Body = 0, Lba2Body = 0,
            Mask = "Transparent background", Threshold = 45, Layout = "Single front", Method = "New humanoid", AutoCrop = true,
            DetailBudget = int.TryParse(Environment.GetEnvironmentVariable("REFTEST_BUDGET"), out var b) ? b : 500,
            HeadDetails = false, Lit = true,
        };
        var generated = Generator.Generate(settings, 2);
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outFile, LbaBodyStudio.Hqr.Build(new[] { generated.Body.Write() }));
        Console.WriteLine($"  {generated.Body.Faces.Count} polygons, {generated.Body.Vertices.Count} points -> {outFile}");
        return 0;
    }

    private static int DumpHeader(string hqrPath, int index)
    {
        var raw = new Hqr(hqrPath).Read(index);
        var info = BitConverter.ToInt32(raw, 0);
        var sizeHeader = BitConverter.ToInt16(raw, 4);
        var xmin = BitConverter.ToInt32(raw, 8); var xmax = BitConverter.ToInt32(raw, 12);
        var ymin = BitConverter.ToInt32(raw, 16); var ymax = BitConverter.ToInt32(raw, 20);
        var zmin = BitConverter.ToInt32(raw, 24); var zmax = BitConverter.ToInt32(raw, 28);
        var nbGroupes = BitConverter.ToInt32(raw, 32); var offGroupes = BitConverter.ToInt32(raw, 36);
        var nbPoints = BitConverter.ToInt32(raw, 40); var offPoints = BitConverter.ToInt32(raw, 44);
        Console.WriteLine($"{hqrPath} entry {index}: Info={info} SizeHeader={sizeHeader} XMin={xmin} XMax={xmax} YMin={ymin} YMax={ymax} ZMin={zmin} ZMax={zmax} NbGroupes={nbGroupes} OffGroupes={offGroupes} NbPoints={nbPoints} OffPoints={offPoints} totalBytes={raw.Length}");
        return 0;
    }

    private static int MarioCustomCmd(string outFile)
    {
        var folder = Folder(2);
        var donor = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(0), 2);
        var body = MarioCustom.Build(donor);
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outFile, LbaBodyStudio.Hqr.Build(new[] { body.Write() }));
        Console.WriteLine($"  {body.Faces.Count} polygons, {body.Vertices.Count} points -> {outFile}");
        return 0;
    }

    // LBA1's own RESS.HQR has a completely different palette layout than LBA2's, so the colour indices
    // must come from a fresh `findcolour ... 1` scan against LBA1's own BODY.HQR, not LBA2's -- reusing
    // Twinsen's own LBA1 donor body colours directly (`colours 1 0`), same "reuse a real, proven index"
    // technique MarioCustom.cs's own comment explains: skin=48, blue=66 (dark navy ramp-start, bank 4),
    // red=97 (bank 6 ramp-start), dark/brown=22 (a dark brown from the same donor's own red-channel range).
    private static int MarioCustomLba1Cmd(string outFile)
    {
        var folder = Folder(1);
        var donor = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(0), 1);
        // dark=64: colour 22 looked like a safe brown in isolation but its OWN ramp (bank 1) turned
        // out to be a fire/glow effect (dark brown -> bright orange -> yellow -> white), not a
        // smooth shade progression -- confirmed live (the shoes rendered streaked
        // orange/yellow/white instead of darkening tan). 64 is bank 4's own darkest position (same
        // bank as blue=66, already confirmed smooth/working) -- a near-black navy, safe reuse.
        var body = MarioCustom.Build(donor, game: 1, red: 97, skin: 48, dark: 64, blue: 66);
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outFile, LbaBodyStudio.Hqr.Build(new[] { body.Write() }));
        Console.WriteLine($"  {body.Faces.Count} polygons, {body.Vertices.Count} points -> {outFile}");
        return 0;
    }

    // Builds walk(0)/run(1)/idle(2)/jump(3) via AnimGenerator for the standard 19-bone rig (LBA2 --
    // matches mario.hqr's own game/bone-count) into one test archive, for ActorAttributesWindow's
    // "Load Debug Anim..." button (BodyStudio/TestBodies/testanims.hqr).
    private static int TestAnimsCmd(string outFile)
    {
        const int nbBodyBones = 19; // MarioCustom/LuigiCustom's own Bones.Count -- every entry below must match
        var anims = new[] { AnimGenerator.Walk(2, nbBodyBones), AnimGenerator.Run(2, nbBodyBones), AnimGenerator.Idle(2, nbBodyBones), AnimGenerator.Jump(2, nbBodyBones) };
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outFile, LbaBodyStudio.Hqr.Build(anims.Select(a => a.Write()).ToArray()));
        Console.WriteLine($"  walk/run/idle/jump ({string.Join(",", anims.Select(a => a.Frames.Count))} frames) -> {outFile}");
        return 0;
    }

    private static int LuigiCustomCmd(string outFile)
    {
        var folder = Folder(2);
        var donor = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(0), 2);
        var body = LuigiCustom.Build(donor);
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outFile, LbaBodyStudio.Hqr.Build(new[] { body.Write() }));
        Console.WriteLine($"  {body.Faces.Count} polygons, {body.Vertices.Count} points -> {outFile}");
        return 0;
    }

    // green=118 (whole ramp checked smooth via `ramp 118 1` first -- see reference-anim-hqr-format
    // memory's own write-up of why that check matters), skin/dark/blue reused from Mario's own
    // already-tested LBA1 picks (same donor, same tones apply).
    private static int LuigiCustomLba1Cmd(string outFile)
    {
        var folder = Folder(1);
        var donor = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(0), 1);
        var body = LuigiCustom.Build(donor, game: 1, green: 118, skin: 48, dark: 64, blue: 66);
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outFile, LbaBodyStudio.Hqr.Build(new[] { body.Write() }));
        Console.WriteLine($"  {body.Faces.Count} polygons, {body.Vertices.Count} points -> {outFile}");
        return 0;
    }

    private static int BowserCustomCmd(string outFile)
    {
        var folder = Folder(2);
        var donor = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(0), 2);
        var body = BowserCustom.Build(donor);
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outFile, LbaBodyStudio.Hqr.Build(new[] { body.Write() }));
        Console.WriteLine($"  {body.Faces.Count} polygons, {body.Vertices.Count} points -> {outFile}");
        return 0;
    }

    private static int PeachCustomCmd(string outFile)
    {
        var folder = Folder(2);
        var donor = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(0), 2);
        var body = PeachCustom.Build(donor);
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outFile, LbaBodyStudio.Hqr.Build(new[] { body.Write() }));
        Console.WriteLine($"  {body.Faces.Count} polygons, {body.Vertices.Count} points -> {outFile}");
        return 0;
    }

    private static int ToadCustomCmd(string outFile)
    {
        var folder = Folder(2);
        var donor = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(0), 2);
        var body = ToadCustom.Build(donor);
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outFile, LbaBodyStudio.Hqr.Build(new[] { body.Write() }));
        Console.WriteLine($"  {body.Faces.Count} polygons, {body.Vertices.Count} points -> {outFile}");
        return 0;
    }

    private static int YoshiCustomCmd(string outFile)
    {
        var folder = Folder(2);
        var donor = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(0), 2);
        var body = YoshiCustom.Build(donor);
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outFile, LbaBodyStudio.Hqr.Build(new[] { body.Write() }));
        Console.WriteLine($"  {body.Faces.Count} polygons, {body.Vertices.Count} points -> {outFile}");
        return 0;
    }

    // Packages the whole roster into the four archives the user asked for: LBA2Mario.HQR (all six
    // core-cast bodies, indices 0-5: Mario/Luigi/Peach/Toad/Bowser/Yoshi), LBA1Mario.HQR (only
    // Mario/Luigi so far -- the other four don't have LBA1 colour sets picked yet, see
    // project-mario-roster memory), and LBA2MarioAnims.HQR/LBA1MarioAnims.HQR (walk/run/idle/jump,
    // indices 0-3 -- ONE set works for the whole roster since every character shares the exact same
    // 19-bone rig; only the angle-unit scale differs per game, handled by Anim.Game, not a
    // per-character duplicate). outDir defaults to Assets/ (this project's own shipped-content
    // folder, alongside DummyBody.lm2).
    private static int PackageRoster(string outDir)
    {
        Directory.CreateDirectory(outDir);

        var lba2Donor = Body.Read(new Hqr(Generator.BodyArchive(Folder(2))).Read(0), 2);
        var lba2Bodies = new[]
        {
            MarioCustom.Build(lba2Donor).Write(),
            LuigiCustom.Build(lba2Donor).Write(),
            PeachCustom.Build(lba2Donor).Write(),
            ToadCustom.Build(lba2Donor).Write(),
            BowserCustom.Build(lba2Donor).Write(),
            YoshiCustom.Build(lba2Donor).Write(),
        };
        var lba2Path = Path.Combine(outDir, "LBA2Mario.HQR");
        File.WriteAllBytes(lba2Path, LbaBodyStudio.Hqr.Build(lba2Bodies));
        Console.WriteLine($"  {lba2Path}: {lba2Bodies.Length} bodies (Mario, Luigi, Peach, Toad, Bowser, Yoshi)");

        // LBA1 colour picks, same "reuse a real donor colour, check the whole ramp" recipe as
        // Mario/Luigi's own (see project-mario-roster memory): skin=48/dark=64/blue=66 are Twinsen's
        // own donor colours (already proven via Mario/Luigi); green=118 (Luigi's own LBA1 pick,
        // reused for Bowser/Yoshi too); pink=224 (bank 14 position 0, a genuinely pink-to-magenta
        // ramp -- LBA1's own palette actually has one, unlike LBA2's); gold=144 (bank 9 position 0,
        // 1609 faces, the single most-used LBA1 colour); white=214 (bank 13 position 6, smooth
        // gray-to-near-white).
        var lba1Donor = Body.Read(new Hqr(Generator.BodyArchive(Folder(1))).Read(0), 1);
        var lba1Bodies = new[]
        {
            MarioCustom.Build(lba1Donor, game: 1, red: 97, skin: 48, dark: 64, blue: 66).Write(),
            LuigiCustom.Build(lba1Donor, game: 1, green: 118, skin: 48, dark: 64, blue: 66).Write(),
            PeachCustom.Build(lba1Donor, game: 1, pink: 224, skin: 48, gold: 144, blonde: 150).Write(),
            ToadCustom.Build(lba1Donor, game: 1, red: 97, blue: 66, dark: 64, white: 214, cream: 48).Write(),
            BowserCustom.Build(lba1Donor, game: 1, green: 118, cream: 48, shellColour: 97, dark: 64).Write(),
            YoshiCustom.Build(lba1Donor, game: 1, green: 118, cream: 48, saddle: 97, white: 214).Write(),
        };
        var lba1Path = Path.Combine(outDir, "LBA1Mario.HQR");
        File.WriteAllBytes(lba1Path, LbaBodyStudio.Hqr.Build(lba1Bodies));
        Console.WriteLine($"  {lba1Path}: {lba1Bodies.Length} bodies (Mario, Luigi, Peach, Toad, Bowser, Yoshi)");

        const int nbBodyBones = 19;
        var lba2Anims = new[] { AnimGenerator.Walk(2, nbBodyBones), AnimGenerator.Run(2, nbBodyBones), AnimGenerator.Idle(2, nbBodyBones), AnimGenerator.Jump(2, nbBodyBones) };
        var lba2AnimPath = Path.Combine(outDir, "LBA2MarioAnims.HQR");
        File.WriteAllBytes(lba2AnimPath, LbaBodyStudio.Hqr.Build(lba2Anims.Select(a => a.Write()).ToArray()));
        Console.WriteLine($"  {lba2AnimPath}: walk(0)/run(1)/idle(2)/jump(3), works for the whole LBA2 roster");

        var lba1Anims = new[] { AnimGenerator.Walk(1, nbBodyBones), AnimGenerator.Run(1, nbBodyBones), AnimGenerator.Idle(1, nbBodyBones), AnimGenerator.Jump(1, nbBodyBones) };
        var lba1AnimPath = Path.Combine(outDir, "LBA1MarioAnims.HQR");
        File.WriteAllBytes(lba1AnimPath, LbaBodyStudio.Hqr.Build(lba1Anims.Select(a => a.Write()).ToArray()));
        Console.WriteLine($"  {lba1AnimPath}: walk(0)/run(1)/idle(2)/jump(3), works for the whole LBA1 roster");

        return 0;
    }

    // In-memory check for HqrWriter.AppendEntry (MainWindow.BodyDebugPreview.cs's own append-a-debug-body logic):
    // appends the mario.hqr body onto a real, retail-sized BODY.HQR (never written back to disk) and confirms every
    // original entry still reads back byte-identical, the new entry lands at the expected index, and it reads back
    // as the same bytes that went in.
    private static int TestAppend(int game)
    {
        var original = new Hqr(Generator.BodyArchive(Folder(game)));
        var originalCount = original.Count;
        var newEntry = new Hqr(FindTestArchive("mario.hqr")).Read(0);
        var real = File.ReadAllBytes(Generator.BodyArchive(Folder(game)));
        var newRecord = LBAAssembler.HqrWriter.CompressedEntry(newEntry);
        var appended = LBAAssembler.HqrWriter.AppendEntry(real, newRecord);
        var check = new Hqr(appended);
        if (check.Count != originalCount + 1) { Console.WriteLine($"FAIL: expected {originalCount + 1} entries, got {check.Count}"); return 1; }
        for (var i = 0; i < originalCount - 1; i++)
        {
            byte[] a, b;
            try { a = original.Read(i); } catch (InvalidDataException) { continue; }
            try { b = check.Read(i); } catch (InvalidDataException) { Console.WriteLine($"FAIL: entry {i} unreadable after append"); return 1; }
            if (!a.AsSpan().SequenceEqual(b)) { Console.WriteLine($"FAIL: entry {i} changed by append"); return 1; }
        }
        var readBack = check.Read(originalCount - 1);
        if (!readBack.AsSpan().SequenceEqual(newEntry)) { Console.WriteLine("FAIL: new entry doesn't read back identical"); return 1; }
        var usedMethod = BitConverter.ToUInt16(newRecord, 8);
        Console.WriteLine($"OK: {originalCount} -> {check.Count} entries, all {originalCount - 1} originals unchanged, new entry at index {originalCount - 1} reads back identical ({newEntry.Length} bytes, written as method {usedMethod})");
        return 0;
    }

    private static string FindTestArchive(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "BodyStudio", "TestBodies", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("mario.hqr not found (development checkout only).");
    }

    // Round-trip check for a debug/test archive like mario.hqr: reads one entry back with BodyStudio's own Hqr
    // reader (not the writer that just built it) and renders it, the same sanity check the game's own body archives
    // would need to pass.
    // Diagnostic (2026-09-23): same as HqrPreview, but reads a SPECIFIC RESS.HQR entry instead of always
    // entry 0 -- lets a body be rendered under a particular island's own palette (COMMON.H's RESS_XPL0..10)
    // to check whether a colour mismatch is a real per-island palette difference rather than a data bug.
    // Entry 0 is a plain 768-byte RGB table; a real island XPL entry (e.g. 29 for Desert, 42 for interiors)
    // is a structured record -- the real 768-byte table lives at the int32 offset stored at byte 4 of the
    // entry, not at the entry's own byte 0 (matches MainWindow.xaml.cs's own LoadPaletteEntry exactly).
    private static int HqrPreviewRess(string hqrPath, int index, int ressEntry, string outDir, int previewGame = 2)
    {
        Directory.CreateDirectory(outDir);
        var body = Body.Read(new Hqr(hqrPath).Read(index), previewGame);
        var xpl = new Hqr(Path.Combine(Folder(previewGame), "RESS.HQR")).Read(ressEntry);
        byte[] raw;
        if (ressEntry == 0) raw = xpl;
        else
        {
            var paletteOffset = BitConverter.ToInt32(xpl, 4);
            raw = xpl[paletteOffset..(paletteOffset + 768)];
        }
        var palette = new Color[256];
        for (var i = 0; i < 256; i++) palette[i] = Color.FromArgb(raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2]);
        foreach (var (name, yaw) in new (string, float)[] { ("front", 0f), ("threequarter", 0.7f), ("profile", MathF.PI / 2) })
        {
            using var render = Renderer.Render(body, palette, 700, 900, yaw, false, background: Color.FromArgb(40, 60, 90));
            render.Save(Path.Combine(outDir, $"hqrpreviewress_{index}_ress{ressEntry}_{name}.png"), ImageFormat.Png);
        }
        Console.WriteLine($"  entry {index} under RESS entry {ressEntry}: {body.Faces.Count} polygons -> {outDir}");
        return 0;
    }

    private static int HqrPreview(string hqrPath, int index, string outDir, int previewGame = 2)
    {
        Directory.CreateDirectory(outDir);
        var body = Body.Read(new Hqr(hqrPath).Read(index), previewGame);
        var palette = Generator.Palette(Folder(previewGame));
        foreach (var (name, yaw) in new (string, float)[] { ("front", 0f), ("threequarter", 0.7f), ("profile", MathF.PI / 2) })
        {
            using var render = Renderer.Render(body, palette, 700, 900, yaw, false, background: Color.FromArgb(40, 60, 90));
            render.Save(Path.Combine(outDir, $"hqrpreview_{index}_{name}.png"), ImageFormat.Png);
        }
        Console.WriteLine($"  entry {index}: {body.Faces.Count} polygons, {body.Vertices.Count} points, {body.Bones.Count} bones -> {outDir}");
        return 0;
    }

    // Diagnostic for the torso/leg attachment: body -> flat sheet -> New-humanoid generated body, rendered from several
    // yaw angles (front, 3/4, near-profile, from slightly below) so a gap at a limb joint shows up visually rather than
    // only in a flat orthographic silhouette (which a depth/side gap doesn't show at all).
    private static int Render3D(int game, int body, string outDir)
    {
        var folder = Folder(game);
        var palette = PaletteBytes(game);
        var model = Body.Read(new Hqr(Generator.BodyArchive(folder)).Read(body), game);
        var sheet = FlatSheet.Render(model, palette);
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(Path.GetTempPath(), "bodypipe_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        try
        {
            var path = Path.Combine(work, "sheet.png");
            using (var bmp = ToBitmap(sheet)) bmp.Save(path, ImageFormat.Png);
            var settings = new Settings
            {
                // New humanoid always needs the Twinsen-rig donor (body 0), regardless of which body's own silhouette
                // (sheet.png, from `body`) is being fed through it as the reference image.
                ImagePath = path, Lba1Folder = Folders[0], Lba2Folder = Folders[1], Lba1Body = 0, Lba2Body = 0,
                Mask = "Transparent background", Layout = "Front + back", Method = "New humanoid", AutoCrop = true, DetailBudget = 300,
            };
            var generated = Generator.Generate(settings, game);
            foreach (var (name, yaw) in new (string, float)[] { ("front", 0f), ("threequarter", 0.7f), ("profile", MathF.PI / 2), ("back", MathF.PI) })
            {
                using var bmp = Renderer.Render(generated.Body, generated.Palette, 700, 900, yaw, false, background: Color.FromArgb(40, 60, 90));
                bmp.Save(Path.Combine(outDir, $"render3d_lba{game}_{body}_{name}.png"), ImageFormat.Png);
            }
            Console.WriteLine($"  rendered to {outDir}");
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
