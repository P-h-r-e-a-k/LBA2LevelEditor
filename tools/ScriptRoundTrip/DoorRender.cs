using System.Buffers.Binary;
using System.IO.Compression;
using LBAAssembler;
using LBAAssembler.Lba1;
using LBAAssembler.Scenes;

namespace ScriptRoundTrip;

// doorrender <folder> <scene> <actor> <out.png> [lift px] [scale]: scene <scene>'s isometric map around one sprite door, composited the way the
// LBA1 engine does it (OBJECT.C: the bricks in painter order, then the sprite cut to its Info clip rectangle, then DrawOverBrick3 puts every brick
// "in front" of the door's cell (layer >= its layer, and z or x further) back over it). `lift` raises the sprite by that many screen pixels, which
// is how far an opening door has moved (the rectangle stays where it is).
internal static class DoorRender
{
    public static int Run(string[] args)
    {
        var folder = args[1];
        var scene = int.Parse(args[2]);
        var actorIndex = int.Parse(args[3]);
        var output = args[4];
        var lift = args.Length > 5 ? int.Parse(args[5]) : 0;
        var scale = args.Length > 6 ? int.Parse(args[6]) : 3;
        Render(folder, scene, actorIndex, output, lift, scale, Console.WriteLine);
        return 0;
    }

    public static void Render(string folder, int scene, int actorIndex, string output, int lift, int scale, Action<string>? log, Func<byte[], byte[]>? editGrid = null, int[]? infoDelta = null)
    {
        var grids = HqrArchive.Open(Path.Combine(folder, "LBA_GRI.HQR"));
        var blocks = HqrArchive.Open(Path.Combine(folder, "LBA_BLL.HQR"));
        var bricks = HqrArchive.Open(Path.Combine(folder, "LBA_BRK.HQR"));
        var ress = HqrArchive.Open(Path.Combine(folder, "RESS.HQR"));
        var sprites = HqrArchive.Open(Path.Combine(folder, "SPRITES.HQR"));
        var palette = ress.Read(0);
        var sceneModel = new SceneStore(SceneGame.Lba1, folder).Load(scene);
        var door = sceneModel.Actors[actorIndex];

        var grid = grids.Read(scene);
        var library = blocks.Read(scene);
        var brickCache = new Dictionary<int, byte[]?>();
        byte[]? Brick(int i) => brickCache.TryGetValue(i, out var b) ? b : brickCache[i] = bricks.IsValid(i) ? bricks.Read(i) : null;

        if (editGrid is not null) grid = editGrid(grid);
        var image = Lba1GridRenderer.Render(grid, library, Brick, palette);
        var placements = Lba1GridRenderer.Placements(grid, library);
        var background = image.Bgra;
        var width = image.Width;
        var height = image.Height;
        var work = (byte[])background.Clone();

        // the sprite: its record is (picture offset, size, ...) and its offset from the projected position is RESS entry 3
        var spriteEntry = sprites.Read(door.Sprite);
        var spriteData = spriteEntry[(int)BitConverter.ToUInt32(spriteEntry, 0)..];
        var ressSprites = ress.Read(3);
        int offsetX = (short)(ressSprites[door.Sprite * 16] | ressSprites[door.Sprite * 16 + 1] << 8);
        int offsetY = (short)(ressSprites[door.Sprite * 16 + 2] | ressSprites[door.Sprite * 16 + 3] << 8);

        int feetX = (door.X - door.Z) * 24 / 512 + image.OriginX;
        int feetY = ((door.X + door.Z) * 12 - door.Y * 30) / 512 + image.OriginY;
        int left = feetX + offsetX, top = feetY + offsetY - lift;
        var delta = infoDelta ?? (Environment.GetEnvironmentVariable("DOORCLIP") is { } d ? d.Split(',').Select(int.Parse).ToArray() : new[] { 0, 0, 0, 0 });
        int clipX0 = door.Info[0] + delta[0] + image.OriginX, clipY0 = door.Info[1] + delta[1] + image.OriginY, clipX1 = door.Info[2] + delta[2] + image.OriginX, clipY1 = door.Info[3] + delta[3] + image.OriginY;
        log?.Invoke($"door at feet ({feetX},{feetY}) sprite {spriteData[0]}x{spriteData[1]} at ({left},{top}) clip ({clipX0},{clipY0})-({clipX1},{clipY1})");

        // the sprite, cut to the clip
        var layer = new byte[width * height * 4];
        Lba1GridRenderer.Blit(layer, width, height, spriteData, left, top, palette);
        for (var y = Math.Max(clipY0, 0); y <= Math.Min(clipY1, height - 1); y++)
            for (var x = Math.Max(clipX0, 0); x <= Math.Min(clipX1, width - 1); x++)
            {
                var o = (y * width + x) * 4;
                if (layer[o + 3] != 0) { work[o] = layer[o]; work[o + 1] = layer[o + 1]; work[o + 2] = layer[o + 2]; work[o + 3] = 255; }
            }

        // DrawOverBrick3: bricks in front of the door's cell, restored from the background inside the clip
        int xm = (door.X + 256) / 512, ym = door.Y / 256, zm = (door.Z + 256) / 512;
        var covering = new List<Lba1Placement>();
        foreach (var p in placements)
        {
            if (p.Y < ym) continue;
            if (!((p.Z == zm && p.X == xm) || p.Z > zm || p.X > xm)) continue;
            var data = Brick(p.Brick);
            if (data is null || data.Length < 4) continue;
            int bx = 24 * (p.X - p.Z) + image.OriginX + (sbyte)data[2], by = 12 * (p.X + p.Z) - 15 * p.Y + image.OriginY + (sbyte)data[3];
            if (bx + data[0] < clipX0 || bx > clipX1 || by + data[1] < clipY0 || by > clipY1) continue;
            var mask = new byte[width * height * 4];
            Lba1GridRenderer.Blit(mask, width, height, data, bx, by, palette);
            var touched = false;
            for (var y = Math.Max(Math.Max(clipY0, by), 0); y <= Math.Min(Math.Min(clipY1, by + data[1] - 1), height - 1); y++)
                for (var x = Math.Max(Math.Max(clipX0, bx), 0); x <= Math.Min(Math.Min(clipX1, bx + data[0] - 1), width - 1); x++)
                {
                    var o = (y * width + x) * 4;
                    if (mask[o + 3] == 0) continue;
                    if (!(work[o] == background[o] && work[o + 1] == background[o + 1] && work[o + 2] == background[o + 2])) touched = true;
                    work[o] = background[o]; work[o + 1] = background[o + 1]; work[o + 2] = background[o + 2]; work[o + 3] = 255;
                }
            if (touched) covering.Add(p);
        }
        foreach (var p in covering) log?.Invoke($"  brick over the door: cell ({p.X},{p.Y},{p.Z}) brick {p.Brick}");

        // crop around the door and enlarge
        var half = Environment.GetEnvironmentVariable("DOORCROP") is { } c ? c.Split(',').Select(int.Parse).ToArray() : new[] { 150, 170, 150, 90 };
        int cropX = Math.Max(0, feetX - half[0]), cropY = Math.Max(0, feetY - half[1]), cropW = Math.Min(width - cropX, half[0] + half[2]), cropH = Math.Min(height - cropY, half[1] + half[3]);
        WritePng(output, work, width, cropX, cropY, cropW, cropH, scale);
    }

    internal static void WritePng(string path, byte[] bgra, int stride, int x0, int y0, int w, int h, int scale)
    {
        int ow = w * scale, oh = h * scale;
        var raw = new byte[(ow * 4 + 1) * oh];
        for (var y = 0; y < oh; y++)
        {
            var row = y * (ow * 4 + 1);
            raw[row] = 0;
            for (var x = 0; x < ow; x++)
            {
                var s = ((y0 + y / scale) * stride + x0 + x / scale) * 4;
                var o = row + 1 + x * 4;
                raw[o] = bgra[s + 2]; raw[o + 1] = bgra[s + 1]; raw[o + 2] = bgra[s]; raw[o + 3] = bgra[s + 3] == 0 ? (byte)255 : bgra[s + 3];
            }
        }
        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)ow); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)oh);
        header[8] = 8; header[9] = 6;
        Chunk(file, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true)) z.Write(raw);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        var crc = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(body));
        s.Write(crc);
    }

    private static uint[]? table;
    private static uint Crc(byte[] data)
    {
        table ??= Enumerable.Range(0, 256).Select(n => { var c = (uint)n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; return c; }).ToArray();
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = table[(crc ^ b) & 255] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}

// blocksheet <folder> <scene> <out.png> [first last]: every block of the scene's library drawn as a small isometric picture with its number,
// to choose blocks for an edit by eye.
internal static class BlockSheet
{
    internal static string Glyph(char c) => Digits[c - '0'];
    private static readonly string[] Digits = { "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001", "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111" };

    public static int Run(string[] args)
    {
        var folder = args[1];
        var scene = int.Parse(args[2]);
        var output = args[3];
        var first = args.Length > 4 ? int.Parse(args[4]) : 1;
        var lastArg = args.Length > 5 ? int.Parse(args[5]) : int.MaxValue;
        var blocks = HqrArchive.Open(Path.Combine(folder, "LBA_BLL.HQR")).Read(scene);
        var bricks = HqrArchive.Open(Path.Combine(folder, "LBA_BRK.HQR"));
        var palette = HqrArchive.Open(Path.Combine(folder, "RESS.HQR")).Read(0);
        var count = (int)(BinaryPrimitives.ReadUInt32LittleEndian(blocks) / 4);
        var last = Math.Min(count, lastArg);
        const int cellW = 150, cellH = 150, perRow = 10;
        var rows = (last - first) / perRow + 1;
        int width = perRow * cellW, height = rows * cellH;
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4) { bgra[i] = 40; bgra[i + 1] = 40; bgra[i + 2] = 40; bgra[i + 3] = 255; }
        for (var b = first; b <= last; b++)
        {
            var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(blocks.AsSpan((b - 1) * 4));
            if (at + 3 > blocks.Length) continue;
            int dx = blocks[at], dy = blocks[at + 1], dz = blocks[at + 2];
            var index = b - first;
            int ox = (index % perRow) * cellW + cellW / 2, oy = (index / perRow) * cellH + cellH - 30;
            // painter order z, x, y
            for (var z = 0; z < dz; z++)
                for (var x = 0; x < dx; x++)
                    for (var y = 0; y < dy; y++)
                    {
                        var pos = ((y * dx) + x) * dz + z;   // block templates are stored x-major then z (see Lba1GridRenderer); tried both orders below
                        var entry = at + 3 + PositionOf(x, y, z, dx, dy, dz) * 4;
                        if (entry + 4 > blocks.Length) continue;
                        var number = BinaryPrimitives.ReadUInt16LittleEndian(blocks.AsSpan(entry + 2));
                        if (number == 0 || !bricks.IsValid(number - 1)) continue;
                        var data = bricks.Read(number - 1);
                        Lba1GridRenderer.Blit(bgra, width, height, data, ox + 24 * (x - z) + (sbyte)data[2], oy + 12 * (x + z) - 15 * y + (sbyte)data[3] - 0, palette);
                    }
            Label(bgra, width, height, b.ToString(), (index % perRow) * cellW + 4, (index / perRow) * cellH + 4);
        }
        DoorRender.WritePng(output, bgra, width, 0, 0, width, height, int.TryParse(Environment.GetEnvironmentVariable("SHEETSCALE"), out var s) ? s : 1);
        return 0;
    }

    // the engine's template order: (y * dx + x) * dz + z? Lba1GridRenderer indexes a block's cells by the position byte of the grid cell, in the same order.
    private static int PositionOf(int x, int y, int z, int dx, int dy, int dz) => (z * dx + x) * dy + y;

    private static void Label(byte[] bgra, int width, int height, string text, int x0, int y0)
    {
        foreach (var ch in text)
        {
            var glyph = Digits[ch - '0'];
            for (var gy = 0; gy < 5; gy++)
                for (var gx = 0; gx < 3; gx++)
                    if (glyph[gy * 3 + gx] == '1')
                        for (var sy = 0; sy < 2; sy++)
                            for (var sx = 0; sx < 2; sx++)
                            {
                                var o = ((y0 + gy * 2 + sy) * width + x0 + gx * 2 + sx) * 4;
                                if (o + 3 < bgra.Length) { bgra[o] = bgra[o + 1] = bgra[o + 2] = 255; }
                            }
            x0 += 8;
        }
    }
}

// scenemap <folder> <scene> <centreX> <centreZ> <halfWidth> <halfHeight> <out.png> [scale]: the scene's isometric map around a world position with the track
// points (yellow, numbered) and actors (red, numbered) drawn on it; a point's marker sits at its own height, so its screen position shows whether it is on the floor.
internal static class SceneMap
{
    public static int Run(string[] args)
    {
        var folder = args[1];
        var scene = int.Parse(args[2]);
        int cx = int.Parse(args[3]), cz = int.Parse(args[4]), halfW = int.Parse(args[5]), halfH = int.Parse(args[6]);
        var scale = args.Length > 8 ? int.Parse(args[8]) : 3;
        var grids = HqrArchive.Open(Path.Combine(folder, "LBA_GRI.HQR"));
        var blocks = HqrArchive.Open(Path.Combine(folder, "LBA_BLL.HQR"));
        var bricks = HqrArchive.Open(Path.Combine(folder, "LBA_BRK.HQR"));
        var palette = HqrArchive.Open(Path.Combine(folder, "RESS.HQR")).Read(0);
        var cache = new Dictionary<int, byte[]?>();
        byte[]? Brick(int i) => cache.TryGetValue(i, out var b) ? b : cache[i] = bricks.IsValid(i) ? bricks.Read(i) : null;
        var image = Lba1GridRenderer.Render(grids.Read(scene), blocks.Read(scene), Brick, palette);
        var model = new SceneStore(SceneGame.Lba1, folder).Load(scene);
        var bgra = (byte[])image.Bgra.Clone();
        void Mark(int x, int y, int z, int number, byte r, byte g, byte b)
        {
            var p = image.Project(x, y, z);
            int sx = (int)p.X, sy = (int)p.Y;
            for (var dy = -2; dy <= 2; dy++)
                for (var dx = -2; dx <= 2; dx++)
                {
                    var o = ((sy + dy) * image.Width + sx + dx) * 4;
                    if (o >= 0 && o + 3 < bgra.Length) { bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = 255; }
                }
            var text = number.ToString();
            for (var i = 0; i < text.Length; i++)
            {
                var glyph = BlockSheet.Glyph(text[i]);
                for (var gy = 0; gy < 5; gy++)
                    for (var gx = 0; gx < 3; gx++)
                        if (glyph[gy * 3 + gx] == '1')
                        {
                            var o = ((sy - 9 + gy) * image.Width + sx + 4 + i * 4 + gx) * 4;
                            if (o >= 0 && o + 3 < bgra.Length) { bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = 255; }
                        }
            }
        }
        for (var i = 0; i < model.TrackPoints.Count; i++) Mark(model.TrackPoints[i].X, model.TrackPoints[i].Y, model.TrackPoints[i].Z, i, 255, 230, 0);
        for (var i = 0; i < model.Actors.Count; i++) if (model.Actors[i].X != 0 || model.Actors[i].Z != 0) Mark(model.Actors[i].X, model.Actors[i].Y, model.Actors[i].Z, i, 255, 40, 40);
        var centre = image.Project(cx, 256, cz);
        int x0 = Math.Max(0, (int)centre.X - halfW), y0 = Math.Max(0, (int)centre.Y - halfH);
        DoorRender.WritePng(args[7], bgra, image.Width, x0, y0, Math.Min(image.Width - x0, halfW * 2), Math.Min(image.Height - y0, halfH * 2), scale);
        return 0;
    }
}
