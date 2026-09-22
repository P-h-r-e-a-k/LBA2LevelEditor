using System.IO;
using LBA2LevelEditor;
using LBA2LevelEditor.Lba1;

namespace ScriptRoundTrip;

// The joined LBA2 interior maps (Lba2Areas):
//   lba2areas <folder>                       every map with where each tile sits
//   lba2overlaps <folder> [cells]            the pairs of tiles of a map that share a plan column (with `cells`: only a cell, which lets one floor stand over another)
//   lba2separate <folder> [radius] [cells]   the smallest move of one tile per overlapping pair after which none do, as Lba2Areas.Separations lines
//   lba2render <folder> <png> <map> [outline]   the map drawn, with each grid outlined and numbered
// Two tiles overlap when they have a brick in the same (x, z) column at any height: a scene stood over another one (a floor of one above the rooms of the other)
// is an overlap on the picture as much as one in the same cells, and the map has to have none.
internal static class Lba2AreaStudy
{
    private static long Key(int x, int y, int z) => (((long)(x + 4096) * 16384) + (z + 4096)) * 512 + (y + 256);

    public static List<Lba1Area> Areas(Lba2Interiors interiors) => Lba2Areas.Find(interiors.LoadScene);

    // The cells (columns when `columns`) a tile fills, as keys, at the tile's place in its map.
    public static HashSet<long> TileKeys(Lba2Interiors interiors, Lba1AreaTile tile, bool columns, int dx = 0, int dy = 0, int dz = 0)
        => interiors.Placements(tile).Select(p => Key(p.X + tile.OffsetX / 512 + dx, columns ? 0 : p.Y + tile.OffsetY / 256 + dy, p.Z + tile.OffsetZ / 512 + dz)).ToHashSet();

    public static int Areas(string[] args)
    {
        var interiors = new Lba2Interiors(args[1]);
        var areas = Areas(interiors);
        for (var i = 0; i < areas.Count; i++)
            Console.WriteLine($"  area {i}: {areas[i].Name} (island {areas[i].Island}): {string.Join(", ", areas[i].Tiles.Select(t => $"{(t.Window is null ? t.Scene.ToString() : t.Scene + "(part)")}@({t.OffsetX / 512},{t.OffsetY / 256},{t.OffsetZ / 512})"))}");
        return 0;
    }

    public static List<(int Area, int A, int B, int Cells)> Overlaps(Lba2Interiors interiors, List<Lba1Area> areas, bool print, bool columns = true)
    {
        var result = new List<(int, int, int, int)>();
        for (var a = 0; a < areas.Count; a++)
        {
            var asColumns = columns && !Lba2Areas.IsStacked(areas[a]);      // (a stacked map, the Dark Monk statue, may share columns: it only may not share cells)
            var keys = areas[a].Tiles.ToDictionary(t => t.Key, t => TileKeys(interiors, t, asColumns));
            foreach (var t1 in areas[a].Tiles)
                foreach (var t2 in areas[a].Tiles.Where(t => t.Key > t1.Key && t.Scene != t1.Scene))      // (a part of a scene is meant to stand over the rest of it)
                {
                    var shared = keys[t1.Key].Count(keys[t2.Key].Contains);
                    if (shared == 0) continue;
                    result.Add((a, t1.Key, t2.Key, shared));
                    if (print) Console.WriteLine($"  area {a} ({areas[a].Name}): tiles {t1.Key} and {t2.Key} share {shared} {(asColumns ? "columns" : "cells")}");
                }
        }
        return result;
    }

    public static int OverlapsCommand(string[] args)
    {
        var interiors = new Lba2Interiors(args[1]);
        var found = Overlaps(interiors, Areas(interiors), true, !(args.Length > 2 && args[2] == "cells"));
        Console.WriteLine($"{found.Count} overlapping pairs");
        return 0;
    }

    public static int Separate(string[] args)
    {
        var interiors = new Lba2Interiors(args[1]);
        var radius = args.Length > 2 && int.TryParse(args[2], out var r) ? r : 90;
        var lines = new List<string>();
        foreach (var area in Areas(interiors))
        {
            var columns = !args.Contains("cells") && !Lba2Areas.IsStacked(area);
            var tiles = area.Tiles.ToList();
            var moved = tiles.ToDictionary(t => t.Key, _ => (X: 0, Y: 0, Z: 0));
            var shift = tiles.ToDictionary(t => t.Key, _ => (X: 0, Y: 0, Z: 0));
            var own = tiles.ToDictionary(t => t.Key, t => TileKeys(interiors, t, columns));
            int Degree(int key) => Lba2Areas.Links.Count(l => l.Anchor == Math.Abs(key) || l.Scene == Math.Abs(key)) + (key < 0 ? 100 : 0);
            HashSet<long> At(int key) => own[key].Select(k => k).ToHashSet();
            HashSet<long> Shifted(int key, int dx, int dy, int dz)
            {
                var t = tiles.First(t => t.Key == key);
                return TileKeys(interiors, t, columns, shift[key].X + dx, shift[key].Y + dy, shift[key].Z + dz);
            }
            HashSet<long> Others(int key) => tiles.Where(t => t.Scene != Math.Abs(key)).SelectMany(t => Shifted(t.Key, 0, 0, 0)).ToHashSet();

            for (var round = 0; round < 40; round++)
            {
                var now = tiles.ToDictionary(t => t.Key, t => Shifted(t.Key, 0, 0, 0));
                (int A, int B, int Cells)? worst = null;
                foreach (var a in tiles)
                    foreach (var b in tiles.Where(t => t.Key > a.Key && t.Scene != a.Scene))
                    {
                        var n = now[a.Key].Count(now[b.Key].Contains);
                        if (n > 0 && (worst is null || n > worst.Value.Cells)) worst = (a.Key, b.Key, n);
                    }
                if (worst is null) break;
                var (pa, pb, _) = worst.Value;
                var mover = Degree(pa) < Degree(pb) ? pa : Degree(pb) < Degree(pa) ? pb : Math.Max(pa, pb);
                var others = Others(mover);
                (int X, int Y, int Z)? best = null;
                foreach (var dy in columns ? new[] { 0 } : new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4 })
                {
                    var candidates = new List<(int X, int Z)>();
                    for (var dx = -radius; dx <= radius; dx++) for (var dz = -radius; dz <= radius; dz++) candidates.Add((dx, dz));
                    foreach (var (dx, dz) in candidates.OrderBy(c => Math.Abs(c.X) + Math.Abs(c.Z)).ThenBy(c => c.X * c.X + c.Z * c.Z))
                        if (!Shifted(mover, dx, dy, dz).Any(others.Contains)) { best = (dx, dy, dz); break; }
                    if (best is not null) break;
                }
                if (best is null) { Console.WriteLine($"  {area.Name}: no free place for tile {mover} within {radius} cells"); break; }
                shift[mover] = (shift[mover].X + best.Value.X, shift[mover].Y + best.Value.Y, shift[mover].Z + best.Value.Z);
                moved[mover] = shift[mover];
                Console.WriteLine($"  {area.Name}: tiles {pa}/{pb} share {worst.Value.Cells}: move {mover} by {best.Value}");
            }
            foreach (var (key, m) in moved.Where(m => m.Value != (0, 0, 0))) lines.Add($"        ({key}, {m.X}, {m.Y}, {m.Z}),   // {area.Name}");
        }
        Console.WriteLine("Separations (added to the ones in Lba2Areas now):");
        foreach (var l in lines) Console.WriteLine(l);
        return 0;
    }

    // The map drawn as a picture (the same picture the editor shows).
    public static int Render(string[] args)
    {
        var interiors = new Lba2Interiors(args[1]);
        var areas = Areas(interiors);
        var area = areas[int.Parse(args[3])];
        var image = interiors.RenderArea(area.Tiles);
        var px = (byte[])image.Bgra.Clone();
        if (args.Length > 4 && args[4] == "outline")
        {
            // each tile's 64 x 64 plan outlined (at the height of its origin) with its number, in its own colour
            void Plot(int x, int y, byte b, byte g, byte r)
            {
                if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return;
                var i = (y * image.Width + x) * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
            }
            var colors = new (byte B, byte G, byte R)[] { (0, 0, 255), (0, 200, 0), (255, 80, 0), (0, 220, 220), (255, 0, 255), (255, 255, 0), (0, 128, 255), (255, 255, 255) };
            var n = 0;
            foreach (var t in area.Tiles)
            {
                var (b, g, r) = colors[n++ % colors.Length];
                var (w0, z0, w1, z1) = t.Window is { } w ? (w.X0, w.Z0, w.X1 + 1, w.Z1 + 1) : (0, 0, 64, 64);
                var pts = new[] { (w0, z0), (w1, z0), (w1, z1), (w0, z1) }.Select(c => image.Project(t.OffsetX + c.Item1 * 512, t.OffsetY, t.OffsetZ + c.Item2 * 512)).ToArray();
                for (var i = 0; i < 4; i++)
                {
                    int x0 = (int)pts[i].X, y0 = (int)pts[i].Y, x1 = (int)pts[(i + 1) % 4].X, y1 = (int)pts[(i + 1) % 4].Y;
                    int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
                    while (true)
                    {
                        Plot(x0, y0, b, g, r); Plot(x0 + 1, y0, b, g, r);
                        if (x0 == x1 && y0 == y1) break;
                        var e2 = 2 * err;
                        if (e2 >= dy) { err += dy; x0 += sx; }
                        if (e2 <= dx) { err += dx; y0 += sy; }
                    }
                }
                var label = image.Project(t.OffsetX + (w0 + w1) / 2 * 512, t.OffsetY, t.OffsetZ + (z0 + z1) / 2 * 512);
                var text = t.Scene.ToString();
                string[] digits = { "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001", "111100111001111", "111100111101111", "111001010010010", "111101111101111", "111101111001111" };
                for (var d = 0; d < text.Length; d++)
                    for (var y = 0; y < 5; y++)
                        for (var x = 0; x < 3; x++)
                            if (digits[text[d] - '0'][y * 3 + x] == '1')
                                for (var sy = 0; sy < 5; sy++) for (var sx = 0; sx < 5; sx++) Plot((int)label.X + (d * 4 + x) * 5 + sx, (int)label.Y + y * 5 + sy, b, g, r);
                Console.WriteLine($"  tile {t.Key}: origin ({t.OffsetX / 512},{t.OffsetY / 256},{t.OffsetZ / 512}) cells, colour #{n}");
            }
        }
        PngWriter.Write(args[2], px, image.Width, image.Height);
        Console.WriteLine($"{image.Width} x {image.Height}");
        return 0;
    }
}
// lba2pitch <folder>: Otringal's palace as a 4 x 4 square of rooms (scenes 151..166): the total number of cells shared by any two rooms for row and
// column pitches of 13..20 cells (the zones give 13).
internal static class Lba2PitchStudy
{
    public static int Run(string[] args)
    {
        var interiors = new Lba2Interiors(args[1]);
        var own = Enumerable.Range(151, 16).ToDictionary(s => s, s => interiors.Placements(s).Select(p => (p.X, p.Y, p.Z)).Distinct().ToArray());
        for (var px = 13; px <= 20; px++)
            for (var pz = 13; pz <= 20; pz++)
            {
                var world = own.ToDictionary(o => o.Key, o =>
                {
                    var c = (o.Key - 151) % 4; var r = (o.Key - 151) / 4;
                    return o.Value.Select(v => ((long)(v.X + c * px + 2048) * 8192 + (v.Z + 84 - r * pz + 2048)) * 512 + v.Y + 256).ToHashSet();
                });
                var shared = 0;
                foreach (var a in own.Keys) foreach (var b in own.Keys.Where(k => k > a)) shared += world[a].Count(world[b].Contains);
                Console.Write($"{shared,6}");
            }
            Console.WriteLine();
        return 0;
    }
}

// lba2bodies <folder> <scene>...: each actor of the scenes with its entity, body number and the BODY.HQR entry Lba2Interiors resolves them to.
internal static class Lba2BodyStudy
{
    public static int Run(string[] args)
    {
        var interiors = new Lba2Interiors(args[1]);
        foreach (var scene in args.Skip(2).Select(int.Parse))
        {
            var model = interiors.LoadScene(scene)!;
            var found = 0;
            for (var i = 1; i < model.Actors.Count; i++)
            {
                var a = model.Actors[i];
                var index = a.IsSprite ? null : interiors.BodyIndex(a.Entity, a.Body);
                if (index is not null) found++;
                Console.WriteLine($"  scene {scene} actor {i}: {(a.IsSprite ? $"sprite {a.Sprite}" : $"entity {a.Entity} body {a.Body}")} -> {(index is { } b ? $"BODY.HQR {b} ({interiors.ReadBody(b)?.Length} bytes)" : "none")}");
            }
            Console.WriteLine($"scene {scene}: {found} of {model.Actors.Count - 1} actors have a body");
        }
        return 0;
    }
}

// lba2actors <folder> <scene>: every actor's flags, entity, body, animation, sprite, movement and info words.
internal static class Lba2ActorStudy
{
    public static int Run(string[] args)
    {
        var interiors = new Lba2Interiors(args[1]);
        var model = interiors.LoadScene(int.Parse(args[2]))!;
        Console.WriteLine($"scene {args[2]}: {model.Actors.Count} actors, {model.TrackPoints.Count} track points, cube mode {model.CubeMode}");
        for (var i = 0; i < model.Actors.Count; i++)
        {
            var a = model.Actors[i];
            Console.WriteLine($"  {i}: flags {a.Flags:X} entity {a.Entity} body {a.Body} anim {a.Anim} sprite {a.Sprite} move {a.Move} info [{string.Join(",", a.Info)}] anim3ds {a.Anim3dsNum} at ({a.X},{a.Y},{a.Z})");
        }
        return 0;
    }
}

// lba2footprint <folder> <scene>...: how much of each scene's 64 x 64 plan is built on (columns with any brick), its bounding box in cells and its layers.
internal static class Lba2FootprintStudy
{
    public static int Run(string[] args)
    {
        var interiors = new Lba2Interiors(args[1]);
        foreach (var scene in args.Skip(2).Select(int.Parse))
        {
            var cells = interiors.Placements(scene);
            var columns = cells.Select(c => (c.X, c.Z)).Distinct().ToList();
            Console.WriteLine($"scene {scene}: {cells.Count} cells, {columns.Count} columns, x {columns.Min(c => c.X)}..{columns.Max(c => c.X)}, z {columns.Min(c => c.Z)}..{columns.Max(c => c.Z)}, layers {cells.Min(c => c.Y)}..{cells.Max(c => c.Y)}, columns with only layer 0 or 1: {cells.GroupBy(c => (c.X, c.Z)).Count(g => g.Max(c => c.Y) <= 1)}");
        }
        return 0;
    }
}

// lba2plan <folder> <scene>: the scene's plan, one character per column: the highest layer built there in base 36 ('.' for an empty column); z runs down, x across.
internal static class Lba2PlanStudy
{
    public static int Run(string[] args)
    {
        var interiors = new Lba2Interiors(args[1]);
        var scene = int.Parse(args[2]);
        var top = new int[64, 64];
        for (var z = 0; z < 64; z++) for (var x = 0; x < 64; x++) top[x, z] = -1;
        foreach (var c in interiors.Placements(scene)) if (c.Y > top[c.X, c.Z]) top[c.X, c.Z] = c.Y;
        Console.WriteLine("    " + string.Concat(Enumerable.Range(0, 64).Select(x => (x % 10).ToString())));
        for (var z = 0; z < 64; z++)
            Console.WriteLine($"{z,3} " + string.Concat(Enumerable.Range(0, 64).Select(x => top[x, z] < 0 ? "." : "0123456789abcdefghijklmnopqrstuvwxyz"[top[x, z]].ToString())));
        return 0;
    }
}

// lba2groups <folder>: LBA2's interior scenes grouped by the cube-change zones that lead from one into another (both ways), per island, with each
// link's offset in cells and whether the zone forward and the zone back agree (to within 2 cells horizontally, 3 layers vertically).
internal static class Lba2GroupStudy
{
    public static int Run(string[] args)
    {
        var dir = args[1];
        var path = System.IO.Path.Combine(dir, "SCENE.HQR");
        var count = LBA2LevelEditor.HqrArchive.CountEntries(path);
        var names = LBA2LevelEditor.HqdDescriptions.Load("SCENE2.HQD", count).Names;
        var store = new LBA2LevelEditor.Scenes.SceneStore(LBA2LevelEditor.Scenes.SceneGame.Lba2, dir);
        string Name(int s) => s + 1 < names.Count ? names[s + 1] ?? "?" : "?";
        var scenes = new Dictionary<int, LBA2LevelEditor.Scenes.SceneModel>();
        for (var s = 0; s < count - 1; s++)
        {
            try { var m = store.Load(s); if (m.CubeMode == 0 && !Name(s).StartsWith("Demo")) scenes[s] = m; } catch (Exception) { }
        }
        (int X, int Y, int Z) Off(LBA2LevelEditor.Scenes.SceneZoneModel z) => ((int)Math.Round((z.X0 - z.Info[0]) / 512.0), (int)Math.Round((z.Y0 - z.Info[1]) / 256.0), (int)Math.Round((z.Z0 - z.Info[2]) / 512.0));
        var edges = new Dictionary<int, HashSet<int>>();
        foreach (var (s, m) in scenes)
            foreach (var z in m.Zones.Where(z => z.Type == 0 && z.Num != s && scenes.ContainsKey(z.Num)))
            {
                var back = scenes[z.Num].Zones.Where(t => t.Type == 0 && t.Num == s).ToList();
                if (back.Count == 0) continue;
                if (!edges.ContainsKey(s)) edges[s] = new(); if (!edges.ContainsKey(z.Num)) edges[z.Num] = new();
                edges[s].Add(z.Num); edges[z.Num].Add(s);
            }
        var seen = new HashSet<int>();
        foreach (var start in edges.Keys.Order())
        {
            if (!seen.Add(start)) continue;
            var group = new List<int> { start };
            for (var i = 0; i < group.Count; i++) foreach (var n in edges[group[i]]) if (seen.Add(n)) group.Add(n);
            group.Sort();
            Console.WriteLine($"island {scenes[group[0]].Island}: {group.Count} scenes: {string.Join(", ", group)}");
            foreach (var s in group) Console.WriteLine($"    {s}: {Name(s)}  -> {string.Join(", ", edges[s].Order().Select(n => { var f = scenes[s].Zones.First(z => z.Type == 0 && z.Num == n); var b = scenes[n].Zones.First(z => z.Type == 0 && z.Num == s); var (o, p) = (Off(f), Off(b)); var ok = Math.Abs(o.X + p.X) <= 2 && Math.Abs(o.Z + p.Z) <= 2 && Math.Abs(o.Y + p.Y) <= 3; return $"{n}{(ok ? "" : "?")}"; }))}");
        }
        return 0;
    }
}

// lba2cubes <folder> <scene>...: every line of the scenes' life scripts that changes cube (a scene change made by a script, not a zone), with the actor.
internal static class Lba2CubeStudy
{
    public static int Run(string[] args)
    {
        var store = new LBA2LevelEditor.Scenes.SceneStore(LBA2LevelEditor.Scenes.SceneGame.Lba2, args[1]);
        foreach (var scene in args.Skip(2).Select(int.Parse))
        {
            var scripts = LBA2LevelEditor.LbaScript.SceneScripts.Load(store.LoadRecord(scene), scene, null, lba1: false);
            var model = store.Load(scene);
            for (var actor = 0; actor < model.Actors.Count; actor++)
            {
                string text;
                try { text = scripts.GetText(actor, LBA2LevelEditor.LbaScript.ScriptKind.Life); } catch (Exception) { continue; }
                foreach (var line in text.Split('\n').Where(l => l.Contains("change_cube", StringComparison.OrdinalIgnoreCase) || l.Contains("change_scene", StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"  scene {scene} actor {actor}: {line.Trim()}");
            }
        }
        return 0;
    }
}

// lba1plan <folder> <scene>: an LBA1 scene's plan, one character per column: the highest layer built there in base 36 ('.' for an empty column).
internal static class Lba1PlanStudy
{
    public static int Run(string[] args)
    {
        var game = new LBA2LevelEditor.Lba1.Lba1Game(args[1]);
        var scene = int.Parse(args[2]);
        var top = new int[64, 64];
        for (var z = 0; z < 64; z++) for (var x = 0; x < 64; x++) top[x, z] = -1;
        foreach (var c in LBA2LevelEditor.Lba1.Lba1GridRenderer.Placements(game.ReadGrid(scene), game.ReadBlocks(scene))) if (c.Y > top[c.X, c.Z]) top[c.X, c.Z] = c.Y;
        Console.WriteLine("    " + string.Concat(Enumerable.Range(0, 64).Select(x => (x % 10).ToString())));
        for (var z = 0; z < 64; z++)
            Console.WriteLine($"{z,3} " + string.Concat(Enumerable.Range(0, 64).Select(x => top[x, z] < 0 ? "." : "0123456789abcdefghijklmnopqrstuvwxyz"[top[x, z]].ToString())));
        return 0;
    }
}

// lba1sprites <folder> <png>: a contact sheet of SPRITES.HQR (each sprite drawn as it is, at most 96 pixels, with its number).
internal static class Lba1SpriteSheet
{
    public static int Run(string[] args)
    {
        var dir = args[1];
        var palette = LBA2LevelEditor.HqrArchive.Open(System.IO.Path.Combine(dir, "RESS.HQR")).Read(0);
        var sprites = LBA2LevelEditor.HqrArchive.Open(System.IO.Path.Combine(dir, "SPRITES.HQR"));
        const int Cell = 110, Columns = 12;
        var total = Math.Min(sprites.Count, 156);
        var rows = (total + Columns - 1) / Columns;
        var w = Columns * Cell; var h = rows * Cell;
        var sheet = new byte[w * h * 4];
        for (var i = 0; i < sheet.Length; i += 4) { sheet[i] = 40; sheet[i + 1] = 40; sheet[i + 2] = 40; sheet[i + 3] = 255; }
        string[] digits = { "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001", "111100111001111", "111100111101111", "111001010010010", "111101111101111", "111101111001111" };
        for (var n = 0; n < total; n++)
        {
            var (cx, cy) = (n % Columns * Cell, n / Columns * Cell);
            if (sprites.IsValid(n))
            {
                byte[] entry; try { entry = sprites.Read(n); } catch (InvalidDataException) { continue; }
                var start = entry.Length >= 8 ? (int)BitConverter.ToUInt32(entry, 0) : entry.Length;
                var data = start + 4 <= entry.Length ? entry[start..] : Array.Empty<byte>();
                if (data.Length > 4 && data[0] > 0 && data[1] > 0 && data[0] <= 100 && data[1] <= 100)
                {
                    var bgra = new byte[data[0] * data[1] * 4];
                    LBA2LevelEditor.Lba1.Lba1GridRenderer.Blit(bgra, data[0], data[1], data, 0, 0, palette);
                    for (var y = 0; y < data[1]; y++) for (var x = 0; x < data[0]; x++)
                    {
                        var s = (y * data[0] + x) * 4;
                        if (bgra[s + 3] == 0) continue;
                        var d = ((cy + 4 + y) * w + cx + 4 + x) * 4;
                        sheet[d] = bgra[s]; sheet[d + 1] = bgra[s + 1]; sheet[d + 2] = bgra[s + 2];
                    }
                }
            }
            var text = n.ToString();
            for (var t = 0; t < text.Length; t++) for (var y = 0; y < 5; y++) for (var x = 0; x < 3; x++)
                if (digits[text[t] - '0'][y * 3 + x] == '1') for (var sy = 0; sy < 2; sy++) for (var sx = 0; sx < 2; sx++)
                { var d = ((cy + Cell - 14 + y * 2 + sy) * w + cx + 4 + (t * 4 + x) * 2 + sx) * 4; sheet[d] = 0; sheet[d + 1] = 255; sheet[d + 2] = 255; }
        }
        PngWriter.Write(args[2], sheet, w, h);
        Console.WriteLine($"{sprites.Count} sprites, {w} x {h}");
        return 0;
    }
}

// lba1bodywinding <folder> <body>...: for each BODY.HQR entry, how many of its polygons face away from the body's centre by the Newell normal of the point order
// (positive) and how many towards it: the winding the game's bodies use.
internal static class Lba1BodyWinding
{
    public static int Run(string[] args)
    {
        var bodies = LBA2LevelEditor.HqrArchive.Open(System.IO.Path.Combine(args[1], "BODY.HQR"));
        foreach (var index in args.Skip(2).Select(int.Parse))
        {
            var body = LbaBodyStudio.Body.Read(bodies.Read(index), 1);
            var world = body.World();
            var centre = new System.Numerics.Vector3(world.Average(v => v.X), world.Average(v => v.Y), world.Average(v => v.Z));
            int outward = 0, inward = 0;
            foreach (var f in body.Faces)
            {
                var n = System.Numerics.Vector3.Zero;
                for (var i = 0; i < f.Points.Length; i++)
                {
                    var a = world[f.Points[i]]; var b = world[f.Points[(i + 1) % f.Points.Length]];
                    n += new System.Numerics.Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
                }
                var c = f.Points.Aggregate(System.Numerics.Vector3.Zero, (s, p) => s + world[p]) / f.Points.Length;
                if (System.Numerics.Vector3.Dot(n, c - centre) > 0) outward++; else inward++;
            }
            Console.WriteLine($"body {index}: {body.Vertices.Count} points, {body.Bones.Count} bones, {body.Faces.Count} faces; Newell normal outward {outward}, inward {inward}; header {BitConverter.ToString(body.Header)}");
        }
        return 0;
    }
}

// mushroom <folder>: builds the secret room's mushroom body from the game's donor body, reads it back and checks its winding and limits.
internal static class MushroomStudy
{
    public static int Run(string[] args)
    {
        var bodies = LBA2LevelEditor.HqrArchive.Open(System.IO.Path.Combine(args[1], "BODY.HQR"));
        var bytes = LBA2LevelEditor.Lba1.Lba1SecretRoomExtras.BuildBody(bodies.Read(LBA2LevelEditor.Lba1.Lba1SecretRoomExtras.DonorBody));
        var body = LbaBodyStudio.Body.Read(bytes, 1);
        var world = body.World();
        Console.WriteLine($"{bytes.Length} bytes, {body.Vertices.Count} points, {body.Bones.Count} bone(s), {body.Faces.Count} faces, x {world.Min(v => v.X)}..{world.Max(v => v.X)}, y {world.Min(v => v.Y)}..{world.Max(v => v.Y)}");
        var centre = new System.Numerics.Vector3(0, world.Average(v => v.Y), 0);
        int outward = 0, inward = 0;
        foreach (var f in body.Faces)
        {
            var n = System.Numerics.Vector3.Zero;
            for (var i = 0; i < f.Points.Length; i++)
            {
                var a = world[f.Points[i]]; var b = world[f.Points[(i + 1) % f.Points.Length]];
                n += new System.Numerics.Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
            }
            var c = f.Points.Aggregate(System.Numerics.Vector3.Zero, (s, p) => s + world[p]) / f.Points.Length;
            var up = f.Points.All(p => world[p].Y == world[f.Points[0]].Y);
            if (System.Numerics.Vector3.Dot(n, up ? new System.Numerics.Vector3(0, -1, 0) : c - centre) > 0) outward++; else inward++;
        }
        Console.WriteLine($"faces facing out {outward}, in {inward} (the flat underside counts as out when it faces down)");
        return inward == 0 ? 0 : 1;
    }
}

// lba1entity <folder> <entity>...: the raw FILE3D.HQR records of entities.
internal static class Lba1EntityDump
{
    public static int Run(string[] args)
    {
        var file = LBA2LevelEditor.HqrFile.Parse(System.IO.File.ReadAllBytes(System.IO.Path.Combine(args[1], "FILE3D.HQR")));
        foreach (var e in args.Skip(2).Select(int.Parse))
            Console.WriteLine($"entity {e}: {BitConverter.ToString(file.Read(e))}");
        return 0;
    }
}

// lba1entities <folder>: every FILE3D.HQR entity with its body and animation counts and the BODY.HQR entries of its bodies (entities without animations first).
internal static class Lba1EntityScan
{
    public static int Run(string[] args)
    {
        var file = LBA2LevelEditor.HqrFile.Parse(System.IO.File.ReadAllBytes(System.IO.Path.Combine(args[1], "FILE3D.HQR")));
        var names = LBA2LevelEditor.HqdDescriptions.Load("FILE3D.HQD", 0).Names;
        for (var e = 0; e < file.Count; e++)
        {
            if (file.IsEmpty(e)) continue;
            var d = file.Read(e); int bodies = 0, anims = 0; var hq = new List<int>();
            for (var p = 0; p < d.Length && d[p] != 255;) { if (d[p] == 1) { bodies++; hq.Add(d[p + 3] | d[p + 4] << 8); } if (d[p] == 3) anims++; p += 2 + d[p + 2]; }
            if (anims == 0 || args.Contains("all")) Console.WriteLine($"entity {e,3}: {bodies} bodies {anims} animations, BODY.HQR {string.Join(",", hq)}  {(e < names.Count ? names[e] : "")}");
        }
        return 0;
    }
}

// lba1onebone <folder>: FILE3D entities whose bodies all have one bone (what a one-bone mushroom can be added to), with the bone count of each animation.
internal static class Lba1OneBone
{
    public static int Run(string[] args)
    {
        var file = LBA2LevelEditor.HqrFile.Parse(System.IO.File.ReadAllBytes(System.IO.Path.Combine(args[1], "FILE3D.HQR")));
        var bodies = LBA2LevelEditor.HqrArchive.Open(System.IO.Path.Combine(args[1], "BODY.HQR"));
        var anims = LBA2LevelEditor.HqrArchive.Open(System.IO.Path.Combine(args[1], "ANIM.HQR"));
        var names = LBA2LevelEditor.HqdDescriptions.Load("FILE3D.HQD", 0).Names;
        for (var e = 0; e < file.Count; e++)
        {
            if (file.IsEmpty(e)) continue;
            var d = file.Read(e); var counts = new List<int>(); var animBones = new List<string>();
            for (var p = 0; p < d.Length && d[p] != 255; p += 2 + d[p + 2])
            {
                var hq = d[p + 3] | d[p + 4] << 8;
                if (d[p] == 1) { try { counts.Add(LbaBodyStudio.Body.Read(bodies.Read(hq), 1).Bones.Count); } catch (Exception) { counts.Add(-1); } }
                if (d[p] == 3) { try { var a = anims.Read(hq); animBones.Add($"{BitConverter.ToUInt16(a, 0)}f/{BitConverter.ToUInt16(a, 2)}b"); } catch (Exception) { animBones.Add("?"); } }
            }
            if (counts.Count > 0 && counts.All(c => c == 1)) Console.WriteLine($"entity {e,3}: bodies with bones {string.Join(",", counts)}; anims {string.Join(" ", animBones)}  {(e < names.Count ? names[e] : "")}");
        }
        return 0;
    }
}

// lba1textchars <folder>: the bytes above 127 that each language's Principal Island dialogue uses, with an example (which code page the game's text is in).
internal static class Lba1TextChars
{
    public static int Run(string[] args)
    {
        var texts = LBA2LevelEditor.HqrFile.Parse(File.ReadAllBytes(Path.Combine(args[1], "TEXT.HQR")));
        for (var language = 0; language < 5; language++)
        {
            var at = (language * 14 + 4) * 2;
            var order = texts.Read(at); var data = texts.Read(at + 1);
            var seen = new SortedDictionary<int, (int Count, string Sample)>();
            for (var i = 0; i < order.Length / 2; i++)
            {
                int start = BitConverter.ToUInt16(data, i * 2), end = BitConverter.ToUInt16(data, i * 2 + 2);
                var s = data[start..end];
                foreach (var b in s.Where(b => b >= 128).Distinct())
                {
                    var (c, sample) = seen.TryGetValue(b, out var v) ? v : (0, "");
                    var text = System.Text.Encoding.Latin1.GetString(s, 0, Math.Max(0, s.Length - 1));
                    var pos = Array.IndexOf(s, (byte)b);
                    seen[b] = (c + 1, sample.Length > 0 ? sample : text.Substring(Math.Max(0, pos - 12), Math.Min(text.Length - Math.Max(0, pos - 12), 26)));
                }
            }
            Console.WriteLine($"language {language}: " + string.Join("  ", seen.Select(e => $"0x{e.Key:X2}x{e.Value.Count} '{e.Value.Sample}'")));
        }
        return 0;
    }
}

// lba2stack <folder>: the Dark Monk statue's scenes (185, 186, 187, 188, 192): where their zones put them (no separation), the cells they fill (x, y layers, z),
// and which pairs of them fill the same plan columns and how far apart their layers are there.
internal static class Lba2StackStudy
{
    public static int Run(string[] args)
    {
        var interiors = new Lba2Interiors(args[1]);
        var area = Lba2AreaStudy.Areas(interiors).First(a => a.Tiles.Any(t => t.Scene == 185));
        var raw = new Dictionary<int, (int X, int Y, int Z)>();
        var cells = new Dictionary<int, List<(int X, int Y, int Z)>>();
        foreach (var t in area.Tiles)
        {
            var sep = Lba2Areas.Separations.Where(s => s.Scene == t.Scene).Select(s => (s.Dx, s.Dy, s.Dz)).FirstOrDefault();
            raw[t.Scene] = (t.OffsetX / 512 - sep.Dx, t.OffsetY / 256 - sep.Dy, t.OffsetZ / 512 - sep.Dz);
            cells[t.Scene] = interiors.Placements(t).Select(p => (p.X + raw[t.Scene].X + sep.Dx, p.Y + raw[t.Scene].Y + sep.Dy, p.Z + raw[t.Scene].Z + sep.Dz)).ToList();
            var c = cells[t.Scene];
            var own = interiors.Placements(t).ToList();
            Console.WriteLine($"scene {t.Scene}: zone offset {raw[t.Scene]}, separation {sep}, own cells x {own.Min(p => p.X)}..{own.Max(p => p.X)} y {own.Min(p => p.Y)}..{own.Max(p => p.Y)} z {own.Min(p => p.Z)}..{own.Max(p => p.Z)}");
        }
        // pairs: with the zone offsets alone (undo the separations), which plan columns are shared and the layers used there
        foreach (var a in area.Tiles)
            foreach (var b in area.Tiles.Where(t => t.Scene > a.Scene))
            {
                var sa = Lba2Areas.Separations.Where(s => s.Scene == a.Scene).Select(s => (s.Dx, s.Dy, s.Dz)).FirstOrDefault();
                var sb = Lba2Areas.Separations.Where(s => s.Scene == b.Scene).Select(s => (s.Dx, s.Dy, s.Dz)).FirstOrDefault();
                var colsA = interiors.Placements(a).Select(p => (X: p.X + raw[a.Scene].X, Y: p.Y + raw[a.Scene].Y, Z: p.Z + raw[a.Scene].Z)).GroupBy(p => (p.X, p.Z)).ToDictionary(g => g.Key, g => (Min: g.Min(p => p.Y), Max: g.Max(p => p.Y)));
                var colsB = interiors.Placements(b).Select(p => (X: p.X + raw[b.Scene].X, Y: p.Y + raw[b.Scene].Y, Z: p.Z + raw[b.Scene].Z)).GroupBy(p => (p.X, p.Z)).ToDictionary(g => g.Key, g => (Min: g.Min(p => p.Y), Max: g.Max(p => p.Y)));
                var shared = colsA.Keys.Where(colsB.ContainsKey).ToList();
                if (shared.Count == 0) continue;
                int cellsShared = 0, aboveB = 0, belowB = 0;
                foreach (var k in shared)
                {
                    var (x, y) = (colsA[k], colsB[k]);
                    if (x.Min > y.Max) aboveB++; else if (x.Max < y.Min) belowB++; else cellsShared++;
                }
                Console.WriteLine($"  {a.Scene} / {b.Scene}: {shared.Count} shared columns: {a.Scene} entirely above {b.Scene} in {aboveB}, below in {belowB}, layers interleaved in {cellsShared}");
            }
        return 0;
    }
}

// lba2lift <folder> [gap]: how far each scene of the Dark Monk statue has to be lifted above the one below it (the zones' x, z and their heights kept, plus this lift) so that
// in every plan column the upper scene's lowest brick is `gap` layers above the lower one's highest.
internal static class Lba2LiftStudy
{
    public static int Run(string[] args)
    {
        var gap = args.Length > 2 ? int.Parse(args[2]) : 2;
        var interiors = new Lba2Interiors(args[1]);
        var area = Lba2AreaStudy.Areas(interiors).First(a => a.Tiles.Any(t => t.Scene == 185));
        var raw = area.Tiles.ToDictionary(t => t.Scene, t =>
        {
            var sep = Lba2Areas.Separations.Where(s => s.Scene == t.Scene).Select(s => (s.Dx, s.Dy, s.Dz)).FirstOrDefault();
            return (X: t.OffsetX / 512 - sep.Dx, Y: t.OffsetY / 256 - sep.Dy, Z: t.OffsetZ / 512 - sep.Dz);
        });
        var cols = area.Tiles.ToDictionary(t => t.Scene, t => interiors.Placements(t).Select(p => (X: p.X + raw[t.Scene].X, Y: p.Y + raw[t.Scene].Y, Z: p.Z + raw[t.Scene].Z)).GroupBy(p => (p.X, p.Z)).ToDictionary(g => g.Key, g => (Min: g.Min(p => p.Y), Max: g.Max(p => p.Y))));
        // bottom to top: the order the zones put them in
        var order = new[] { 188, 187, 192, 185, 186 };
        var lift = order.ToDictionary(s => s, _ => 0);
        for (var i = 1; i < order.Length; i++)
            for (var j = 0; j < i; j++)
            {
                var (upper, lower) = (order[i], order[j]);
                var need = int.MinValue;
                foreach (var (k, a) in cols[upper]) if (cols[lower].TryGetValue(k, out var b)) need = Math.Max(need, gap + b.Max - a.Min);
                if (need == int.MinValue) continue;
                lift[upper] = Math.Max(lift[upper], lift[lower] + need);
                Console.WriteLine($"  {upper} over {lower}: needs {need} layers between them (shared columns), so {upper} is at least {lift[lower] + need} up");
            }
        foreach (var s in order) Console.WriteLine($"scene {s}: zone height {raw[s].Y}, lifted by {lift[s]} -> {raw[s].Y + lift[s]}");
        return 0;
    }
}

// The Dark Monk statue's screen silhouettes: each scene drawn alone, and per screen column the rows its picture covers (topmost, bottommost), in the map's own screen
// coordinates once the scene is at its zone offset plus `lift` layers (one layer is 15 pixels).
internal static class Lba2Silhouettes
{
    public sealed record Silhouette(Dictionary<int, (int Top, int Bottom)> Columns);

    // The scene's silhouette at its zone-derived place (no lift), keyed by global screen x.
    public static Dictionary<int, Silhouette> Of(Lba2Interiors interiors, Lba1Area area, Func<int, (int X, int Y, int Z)> zonePlace)
    {
        var result = new Dictionary<int, Silhouette>();
        foreach (var tile in area.Tiles)
        {
            var (ox, oy, oz) = zonePlace(tile.Scene);
            var image = interiors.RenderArea(new[] { new Lba1AreaTile(tile.Scene, 0, 0, 0) });
            int dx = 24 * (ox - oz), dy = 12 * (ox + oz) - 15 * oy;
            var columns = new Dictionary<int, (int Top, int Bottom)>();
            for (var x = 0; x < image.Width; x++)
            {
                int top = -1, bottom = -1;
                for (var y = 0; y < image.Height; y++)
                    if (image.Bgra[(y * image.Width + x) * 4 + 3] != 0) { if (top < 0) top = y; bottom = y; }
                if (top >= 0) columns[x - image.OriginX + dx] = (top - image.OriginY + dy, bottom - image.OriginY + dy);
            }
            result[tile.Scene] = new Silhouette(columns);
        }
        return result;
    }

    // How many layers the upper scene has to be lifted (on top of the lower one's lift) so that its picture is `margin` pixels clear above the lower one's, column by column.
    public static int Need(Silhouette upper, Silhouette lower, int margin)
    {
        var need = int.MinValue;
        foreach (var (x, u) in upper.Columns)
            if (lower.Columns.TryGetValue(x, out var l)) need = Math.Max(need, (int)Math.Ceiling((u.Bottom - l.Top + margin) / 15.0));
        return need;
    }
}

// lba2screenlift <folder> [margin px]: the lifts (layers, on top of the zones' heights) that leave every scene of the Dark Monk statue's picture clear above the picture of the
// one below it, whatever the plan columns, from bottom to top: 188, 187, 192, 185, 186.
internal static class Lba2ScreenLiftStudy
{
    public static int Run(string[] args)
    {
        var margin = args.Length > 2 ? int.Parse(args[2]) : 24;
        var interiors = new Lba2Interiors(args[1]);
        var area = Lba2AreaStudy.Areas(interiors).First(a => a.Tiles.Any(t => t.Scene == 185));
        var raw = area.Tiles.ToDictionary(t => t.Scene, t =>
        {
            var sep = Lba2Areas.Separations.Where(s => s.Scene == t.Scene).Select(s => (s.Dx, s.Dy, s.Dz)).FirstOrDefault();
            return (X: t.OffsetX / 512 - sep.Dx, Y: t.OffsetY / 256 - sep.Dy, Z: t.OffsetZ / 512 - sep.Dz);
        });
        var silhouettes = Lba2Silhouettes.Of(interiors, area, s => raw[s]);
        var order = new[] { 188, 187, 192, 185, 186 };
        var lift = order.ToDictionary(s => s, _ => 0);
        for (var i = 1; i < order.Length; i++)
            for (var j = 0; j < i; j++)
            {
                var need = Lba2Silhouettes.Need(silhouettes[order[i]], silhouettes[order[j]], margin);
                if (need == int.MinValue) { Console.WriteLine($"  {order[i]} over {order[j]}: no screen column in common"); continue; }
                lift[order[i]] = Math.Max(lift[order[i]], lift[order[j]] + need);
                Console.WriteLine($"  {order[i]} over {order[j]}: needs {need} layers between them");
            }
        foreach (var s in order) Console.WriteLine($"scene {s}: zone height {raw[s].Y}, lift {lift[s]} -> {raw[s].Y + lift[s]}");
        Console.WriteLine("Separations: " + string.Join(" ", order.Where(s => lift[s] != 0).Select(s => $"({s}, 0, {lift[s]}, 0),")));
        return 0;
    }
}

// lba1bodyanim <folder>: for every LBA1 entity, its bodies' bone counts and its animations' bone counts, and the (body, animation) pairs the actor window's preview can't play
// (an animation with fewer bones than the body: the window draws the body still).
internal static class Lba1BodyAnimStudy
{
    public static int Run(string[] args)
    {
        var game = new LBA2LevelEditor.Lba1.Lba1Game(args[1]);
        int entities = 0, mixed = 0, pairs = 0, unplayable = 0;
        for (var e = 0; e < game.EntityCount; e++)
        {
            var bodies = game.EntityBodies(e);
            var anims = game.EntityAnims(e);
            if (bodies.Count == 0 && anims.Count == 0) continue;
            entities++;
            var bodyBones = bodies.ToDictionary(b => b.Key, b =>
            {
                try { return game.ReadBody(b.Value) is { } bytes ? LbaBodyStudio.Body.Read(bytes, 1).Bones.Count : -1; } catch { return -1; }
            });
            var animBones = anims.ToDictionary(a => a.Key, a => game.Animation(a.Value)?.BoneCount ?? -1);
            var distinctBodies = bodyBones.Values.Distinct().Count();
            var distinctAnims = animBones.Values.Distinct().Count();
            if (distinctBodies > 1 || distinctAnims > 1) mixed++;
            var bad = new List<string>();
            foreach (var b in bodyBones) foreach (var a in animBones) { pairs++; if (a.Value < b.Value) { unplayable++; if (bad.Count < 6) bad.Add($"body {b.Key} ({b.Value} bones) x anim {a.Key} ({a.Value})"); } }
            if (distinctBodies > 1 || distinctAnims > 1 || bad.Count > 0)
                Console.WriteLine($"entity {e}: bodies {string.Join(", ", bodyBones.Select(b => $"{b.Key}:{b.Value}"))}; anim bones {string.Join(",", animBones.Values.Distinct().Order())}; unplayable pairs {(bad.Count == 0 ? "none" : string.Join("; ", bad))}");
        }
        Console.WriteLine($"{entities} entities, {mixed} with mixed bone counts, {unplayable} of {pairs} body x animation pairs unplayable");
        return 0;
    }
}

// lba2bodyanim <folder>: for every LBA2 entity, its bodies' bone counts and its animations' group counts (ANIM.HQR entry header: frames, groups, ...), and how the actor
// window's animation list for a body would look (the entity's own animations first).
internal static class Lba2BodyAnimStudy
{
    public static int Run(string[] args)
    {
        var table = LBA2LevelEditor.Lba2EntityTable.Load(args[1]) ?? throw new InvalidDataException("no entity table");
        var bodies = LBA2LevelEditor.HqrArchive.Open(Path.Combine(args[1], "BODY.HQR"));
        var anims = LBA2LevelEditor.HqrArchive.Open(Path.Combine(args[1], "ANIM.HQR"));
        int Groups(int index) { try { return anims.IsValid(index) ? BitConverter.ToUInt16(anims.Read(index), 2) : -1; } catch { return -1; } }
        int Bones(int index) { try { return bodies.IsValid(index) ? LbaBodyStudio.Body.Read(bodies.Read(index), 2).Bones.Count : -1; } catch { return -1; } }
        int entities = 0, mixedBodies = 0, exact = 0, more = 0, fewer = 0;
        foreach (var e in table.Entities.Where(e => e.Bodies.Count > 0 || e.Anims.Count > 0))
        {
            entities++;
            var bodyBones = e.Bodies.Select(b => Bones(b.Body)).Where(n => n >= 0).ToList();
            var animGroups = e.Anims.Select(a => Groups(a.Anim)).Where(n => n >= 0).ToList();
            if (bodyBones.Distinct().Count() > 1) mixedBodies++;
            foreach (var b in bodyBones) foreach (var g in animGroups) { if (g == b) exact++; else if (g > b) more++; else fewer++; }
            if (bodyBones.Distinct().Count() > 1 || animGroups.Distinct().Count() > 1)
                Console.WriteLine($"entity {e.Id}: body bones {string.Join(",", bodyBones.Distinct().Order())}; anim groups {string.Join(",", animGroups.Distinct().Order())}");
        }
        Console.WriteLine($"{entities} entities; body x own-animation pairs: {exact} equal, {more} animation has more groups, {fewer} animation has fewer; {mixedBodies} entities with bodies of different bone counts");
        var owners = table.EntitiesWithBody(25).Select(e => e.Id).ToList();
        Console.WriteLine($"body 25 belongs to entities {string.Join(",", owners)}; animations {string.Join(",", table.AnimationsOfBody(25, out var standing))} (standing {standing})");
        return 0;
    }
}

// lba2entity2 <folder> <entity>...: an LBA2 entity's bodies (generic -> BODY.HQR, bones) and animations (generic -> ANIM.HQR, groups).
internal static class Lba2EntityDump2
{
    public static int Run(string[] args)
    {
        var table = LBA2LevelEditor.Lba2EntityTable.Load(args[1])!;
        var bodies = LBA2LevelEditor.HqrArchive.Open(Path.Combine(args[1], "BODY.HQR"));
        var anims = LBA2LevelEditor.HqrArchive.Open(Path.Combine(args[1], "ANIM.HQR"));
        foreach (var id in args.Skip(2).Select(int.Parse))
        {
            var e = table.Entities[id];
            Console.WriteLine($"entity {id}: {e.Bodies.Count} bodies, {e.Anims.Count} animations");
            foreach (var b in e.Bodies) Console.WriteLine($"  body {b.Generic} -> BODY.HQR {b.Body}, {(bodies.IsValid(b.Body) ? LbaBodyStudio.Body.Read(bodies.Read(b.Body), 2).Bones.Count : -1)} bones");
            Console.WriteLine("  animations: " + string.Join(", ", e.Anims.Select(a => $"{a.Generic}->{a.Anim}({(anims.IsValid(a.Anim) ? BitConverter.ToUInt16(anims.Read(a.Anim), 2) : -1)})")));
        }
        return 0;
    }
}
