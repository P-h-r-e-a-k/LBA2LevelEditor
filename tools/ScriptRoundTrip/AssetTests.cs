using LBA2LevelEditor;
using LBA2LevelEditor.Assets;

namespace ScriptRoundTrip;

// The run-length pictures (bricks of both games, sprites of LBA1): every picture decodes, encodes and decodes to the same pixels, and a library
// saves with one picture replaced. `assets png <lba1|lba2> <number> <out.png>` writes one picture to look at.
//   assets [all]
internal static class AssetTests
{
    private static readonly string Lba1Dir = Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure";
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";

    public static int Run(string[] args)
    {
        if (args.Length > 4 && args[1] == "png") return Png(args);
        if (args.Length > 2 && args[1] == "probe") return SpriteProbe.Run(args[2]);
        var failures = 0;
        failures += Library(GphLibrary.Lba1Bricks(Lba1Dir));
        failures += Library(GphLibrary.Lba1Sprites(Lba1Dir));
        failures += Library(GphLibrary.Lba2Bricks(Lba2Dir));
        failures += Library(GphLibrary.Lba2Sprites(Lba2Dir));
        failures += Library(GphLibrary.Lba2RawSprites(Lba2Dir));
        Console.WriteLine(failures == 0 ? "asset tests: all passed" : $"asset tests: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static int Library(GphLibrary library)
    {
        var failures = 0; var decoded = 0; var unreadable = 0; long original = 0, encoded = 0;
        foreach (var number in library.Numbers())
        {
            GphImage image;
            try { image = library.Read(number); } catch (InvalidDataException) { unreadable++; continue; }
            decoded++;
            var bytes = library.Raw ? image.EncodeRaw() : image.Encode();
            var again = library.Raw ? GphImage.DecodeRaw(bytes) : GphImage.Decode(bytes);
            original += image.Length; encoded += bytes.Length;
            if (again.Width != image.Width || again.Height != image.Height || again.OffsetX != image.OffsetX || again.OffsetY != image.OffsetY
                || !again.Pixels.AsSpan().SequenceEqual(image.Pixels) || !again.Opaque.AsSpan().SequenceEqual(image.Opaque))
            {
                if (failures++ < 5) Console.WriteLine($"  {library.Title} picture {number}: re-encoded picture differs");
            }
        }
        Console.WriteLine($"  {library.Title}: {decoded} pictures decode ({unreadable} not pictures), re-encode identical for all but {failures}; encoded size {encoded * 100.0 / Math.Max(1, original):F0}% of the original");

        // replace one picture in a copy and check the library still reads and the others are untouched
        var target = library.Numbers().First(n => { try { library.Read(n); return true; } catch (InvalidDataException) { return false; } });
        var before = library.Numbers().Where(n => n != target).Take(20).ToDictionary(n => n, n => { try { return library.Read(n).Pixels.ToArray(); } catch (InvalidDataException) { return null; } });
        var picture = library.Read(target);
        for (var i = 0; i < picture.Pixels.Length; i++) if (picture.Opaque[i]) picture.Pixels[i] = (byte)(picture.Pixels[i] ^ 0x11);
        library.Replace(target, picture);
        var afterBytes = library.ToBytes();
        var check = HqrFile.Parse(afterBytes);
        var ok = library.Read(target).Pixels.AsSpan().SequenceEqual(picture.Pixels);
        foreach (var (n, pixels) in before) if (pixels is not null && !library.Read(n).Pixels.AsSpan().SequenceEqual(pixels)) ok = false;
        Console.WriteLine($"  {library.Title}: replacing picture {target} keeps the others: {(ok ? "ok" : "FAILED")}");

        // a new picture: it gets the next number, reads back, and nothing else moves (for LBA2 bricks the scene -> grid table behind them moves one slot up with the count)
        var countBefore = library.Numbers().Count();
        var sample = new GphImage(20, 12) { OffsetX = 0, OffsetY = 0 };
        for (var y = 0; y < 12; y++) for (var x = 0; x < 20; x++) if (x + y > 3 && x + y < 28) sample.Set(x, y, (byte)(1 + (x + y) % 5));
        if (library.Raw) for (var i = 0; i < sample.Pixels.Length; i++) if (sample.Opaque[i] && sample.Pixels[i] == 0) sample.Pixels[i] = 1;
        var extra = 0;
        var tableBefore = library.Title == "LBA2 bricks" ? TableAfterBricks(library) : null;
        var added = library.Add(sample);
        var back = library.Read(added);
        var addOk = library.Numbers().Count() == countBefore + 1 && added == countBefore + (library.Title == "LBA2 bricks" ? 0 : 0) + (library.Numbers().Min() == 0 ? 0 : 0)
            && back.Width == sample.Width && back.Height == sample.Height && back.Pixels.Where((_, i) => back.Opaque[i]).SequenceEqual(sample.Pixels.Where((_, i) => sample.Opaque[i]));
        foreach (var (n, pixels) in before) if (pixels is not null && !library.Read(n).Pixels.AsSpan().SequenceEqual(pixels)) addOk = false;
        if (tableBefore is not null) addOk &= TableAfterBricks(library)!.AsSpan().SequenceEqual(tableBefore);
        _ = extra;
        Console.WriteLine($"  {library.Title}: a new picture is number {added}, reads back, the rest is untouched{(tableBefore is not null ? ", the scene -> grid table follows the bricks" : "")}: {(addOk ? "ok" : "FAILED")}");
        return failures + (ok ? 0 : 1) + (addOk ? 0 : 1);
    }

    // The scene -> grid table behind the bricks of LBA_BKG.HQR (entry Brk_Start + Max_Brk after the header says how many bricks there are).
    private static byte[]? TableAfterBricks(GphLibrary library)
    {
        var file = HqrFile.Parse(library.ToBytes());
        var header = file.Read(0);
        int start = BitConverter.ToUInt16(header, 6), max = BitConverter.ToUInt16(header, 8);
        return file.Read(start + max);
    }

    private static int Png(string[] args)
    {
        var lba2 = args[2] == "lba2";
        var library = lba2 ? GphLibrary.Lba2Bricks(Lba2Dir) : args.Length > 5 && args[5] == "sprites" ? GphLibrary.Lba1Sprites(Lba1Dir) : GphLibrary.Lba1Bricks(Lba1Dir);
        var image = library.Read(int.Parse(args[3]));
        var palette = library.LoadPalette();
        var scale = 8;
        var bgra = new byte[image.Width * scale * image.Height * scale * 4];
        var pixels = image.ToBgra(palette);
        for (var y = 0; y < image.Height * scale; y++)
            for (var x = 0; x < image.Width * scale; x++)
            {
                var s = ((y / scale) * image.Width + x / scale) * 4; var d = (y * image.Width * scale + x) * 4;
                var solid = pixels[s + 3] != 0;
                var checker = ((x / 8 + y / 8) & 1) == 0 ? (byte)70 : (byte)100;
                for (var c = 0; c < 3; c++) bgra[d + c] = solid ? pixels[s + c] : checker;
                bgra[d + 3] = 255;
            }
        PngWriter.Write(args[4], bgra, image.Width * scale, image.Height * scale);
        Console.WriteLine($"{args[4]}: {image.Width}x{image.Height}, hot spot {image.OffsetX},{image.OffsetY}");
        return 0;
    }
}
