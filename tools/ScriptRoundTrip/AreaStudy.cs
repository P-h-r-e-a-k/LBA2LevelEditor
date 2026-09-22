using LBAAssembler.Lba1;

namespace ScriptRoundTrip;

// areas <folder>: the joined areas of the LBA1 install, then every scene with its description, island, and where its cube-change zones lead
// (the raw material for finding more scenes that can be joined).
internal static class AreaStudy
{
    public static int Run(string[] args)
    {
        var game = new Lba1Game(args.Length > 1 ? args[1] : @"E:\GOG Games\Little Big Adventure");
        Console.WriteLine($"{game.Areas.Count} areas:");
        for (var a = 0; a < game.Areas.Count; a++)
        {
            var area = game.Areas[a];
            Console.WriteLine($"  area {a}: island {area.Island} {area.Name}: " + string.Join(", ", area.Tiles.Select(t => $"{t.Scene}@({t.OffsetX / 512},{t.OffsetY / 256},{t.OffsetZ / 512})")));
        }
        var joined = game.Areas.SelectMany(a => a.Tiles.Select(t => t.Scene)).ToHashSet();
        foreach (var island in game.Scenes.Select(s => s.Island).Distinct().Order())
        {
            Console.WriteLine($"island {island} {Lba1Game.IslandNames.ElementAtOrDefault(island)}");
            foreach (var s in game.Scenes.Where(s => s.Island == island))
                Console.WriteLine($"  {s.Index,3}{(joined.Contains(s.Index) ? "*" : " ")} {game.Description(s.Index)}  exits: {string.Join(",", s.Exits)}");
        }
        return 0;
    }
}

// arearender <folder> <png> <area index|none> [scene=dx,dy,dz ...] [crop=x,y,w,h]: draws a joined area (or just the given tiles) with each tile's
// 64 x 64 cell footprint outlined and its scene number written on it; offsets are in cells (x, layers, z) and add to / replace the area's own,
// so a candidate placement can be looked at before it is put into Lba1Areas.ManualLinks.
internal static class AreaRender
{
    private static readonly string[] Digits =
    {
        "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001010010010", "111101111101111", "111101111001111",
    };

    public static int Run(string[] args)
    {
        var game = new Lba1Game(args[1]);
        var tiles = new Dictionary<int, Lba1AreaTile>();
        int? crop = null; int cx = 0, cy = 0, cw = 0, ch = 0;
        var marks = new List<(int X, int Y, int Z)>();
        if (args[3] != "none") foreach (var t in game.Areas[int.Parse(args[3])].Tiles) tiles[t.Scene] = t;
        foreach (var extra in args.Skip(4))
        {
            var parts = extra.Split('=');
            var v = parts[1].Split(',').Select(int.Parse).ToArray();
            if (parts[0] == "crop") { crop = 1; (cx, cy, cw, ch) = (v[0], v[1], v[2], v[3]); }
            else if (parts[0] == "mark") marks.Add((v[0], v[1], v[2]));
            else { var s = int.Parse(parts[0]); tiles[s] = new Lba1AreaTile(s, v[0] * 512, v[1] * 256, v[2] * 512); }
        }
        var list = tiles.Values.OrderBy(t => t.Scene).ToList();
        var image = game.RenderArea(new Lba1Area(game.LoadScene(list[0].Scene).Island, list));
        var px = (byte[])image.Bgra.Clone();
        void Plot(int x, int y, byte b, byte g, byte r)
        {
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return;
            var i = (y * image.Width + x) * 4;
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        void Line(System.Windows.Point a, System.Windows.Point c, byte b, byte g, byte r)
        {
            int x0 = (int)a.X, y0 = (int)a.Y, x1 = (int)c.X, y1 = (int)c.Y;
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
        var colors = new (byte B, byte G, byte R)[] { (0, 0, 255), (0, 200, 0), (255, 80, 0), (0, 220, 220), (255, 0, 255), (255, 255, 0), (0, 128, 255), (255, 255, 255) };
        var n = 0;
        foreach (var t in list)
        {
            var (b, g, r) = colors[n++ % colors.Length];
            var pts = new[] { (0, 0), (64, 0), (64, 64), (0, 64) }.Select(c => image.Project(t.OffsetX + c.Item1 * 512, t.OffsetY, t.OffsetZ + c.Item2 * 512)).ToArray();
            for (var i = 0; i < 4; i++) Line(pts[i], pts[(i + 1) % 4], b, g, r);
            var label = image.Project(t.OffsetX + 32 * 512, t.OffsetY, t.OffsetZ + 32 * 512);
            var text = t.Scene.ToString();
            for (var d = 0; d < text.Length; d++)
                for (var y = 0; y < 5; y++)
                    for (var x = 0; x < 3; x++)
                        if (Digits[text[d] - '0'][y * 3 + x] == '1')
                            for (var sy = 0; sy < 4; sy++) for (var sx = 0; sx < 4; sx++) Plot((int)label.X + (d * 4 + x) * 4 + sx, (int)label.Y + y * 4 + sy, b, g, r);
            Console.WriteLine($"  scene {t.Scene}: origin ({t.OffsetX / 512},{t.OffsetY / 256},{t.OffsetZ / 512}) cells, colour #{n}");
        }
        foreach (var (mx, my, mz) in marks)
        {
            var p = image.Project((mx + .5) * 512 + list[0].OffsetX, (my + 1) * 256 + list[0].OffsetY, (mz + .5) * 512 + list[0].OffsetZ);
            for (var d = -12; d <= 12; d++) { Plot((int)p.X + d, (int)p.Y, 0, 0, 255); Plot((int)p.X, (int)p.Y + d, 0, 0, 255); Plot((int)p.X + d, (int)p.Y + 1, 0, 0, 255); Plot((int)p.X + 1, (int)p.Y + d, 0, 0, 255); }
            Console.WriteLine($"  mark ({mx},{my},{mz}) at picture ({(int)p.X},{(int)p.Y})");
        }
        if (crop is not null) PngWriter.Write(args[2], Crop(px, image.Width, cx, cy, cw, ch), cw, ch);
        else PngWriter.Write(args[2], px, image.Width, image.Height);
        Console.WriteLine($"{image.Width} x {image.Height}");
        return 0;
    }

    private static byte[] Crop(byte[] px, int width, int x, int y, int w, int h)
    {
        var o = new byte[w * h * 4];
        for (var r = 0; r < h; r++) Buffer.BlockCopy(px, ((y + r) * width + x) * 4, o, r * w * 4, w * 4);
        return o;
    }
}

// edges <folder> <scene>...: for each scene, the top layer and brick of every cell along its four grid edges, as runs, so two scenes that
// continue into each other can be matched (a dirt path leaving one grid should arrive on the same layer and cells of the next).
internal static class EdgeStudy
{
    public static int Run(string[] args)
    {
        var game = new Lba1Game(args[1]);
        foreach (var scene in args.Skip(2).Select(int.Parse))
        {
            var (top, brick) = game.ColumnTops(scene);
            Console.WriteLine($"scene {scene}: {game.Description(scene)}");
            void Edge(string name, IEnumerable<(int Along, int Index)> cells)
            {
                var runs = new List<string>();
                (int Y, int B, int From, int To)? run = null;
                foreach (var (along, i) in cells)
                {
                    var key = (top[i], brick[i]);
                    if (run is { } r && r.Y == key.Item1 && r.B == key.Item2) run = (r.Y, r.B, r.From, along);
                    else { if (run is { } p) runs.Add($"{p.From}-{p.To}:y{p.Y}/b{p.B}"); run = (key.Item1, key.Item2, along, along); }
                }
                if (run is { } last) runs.Add($"{last.From}-{last.To}:y{last.Y}/b{last.B}");
                Console.WriteLine($"  {name}: {string.Join("  ", runs)}");
            }
            Edge("x=0 (west, along z)", Enumerable.Range(0, 64).Select(z => (z, z * 64)));
            Edge("x=63 (east, along z)", Enumerable.Range(0, 64).Select(z => (z, z * 64 + 63)));
            Edge("z=0 (north, along x)", Enumerable.Range(0, 64).Select(x => (x, x)));
            Edge("z=63 (south, along x)", Enumerable.Range(0, 64).Select(x => (x, 63 * 64 + x)));
        }
        return 0;
    }
}

// links <folder>: every pair of scenes on one island whose cube-change zones lead into each other, with where each says the other's origin sits
// (zone position minus arrival position, in cells) and whether the two agree; the pairs that agree are candidates for joining into one map.
internal static class LinkStudy
{
    public static int Run(string[] args)
    {
        var game = new Lba1Game(args[1]);
        var scenes = game.Scenes.Select(s => game.LoadScene(s.Index)).ToDictionary(s => s.Index);
        var areaOf = new Dictionary<int, int>();
        for (var a = 0; a < game.Areas.Count; a++) foreach (var t in game.Areas[a].Tiles) areaOf[t.Scene] = a;
        foreach (var a in scenes.Values.OrderBy(s => s.Index))
            foreach (var z in a.Zones.Where(z => z.Type == 0))
            {
                var bIndex = z.Info[0];
                if (bIndex <= a.Index || !scenes.TryGetValue(bIndex, out var b) || b.Island != a.Island) continue;
                var back = b.Zones.Where(y => y.Type == 0 && y.Info[0] == a.Index).ToList();
                var (dx, dy, dz) = (z.X0 - z.Info[1], z.Y0 - z.Info[2], z.Z0 - z.Info[3]);
                if (back.Count == 0) { Console.WriteLine($"{a.Index,3} -> {bIndex,3}  one way: b at ({dx / 512.0:0.#},{dy / 256.0:0.#},{dz / 512.0:0.#})"); continue; }
                foreach (var y in back)
                {
                    var (rx, ry, rz) = (y.Info[1] - y.X0, y.Info[2] - y.Y0, y.Info[3] - y.Z0);
                    var agree = Math.Abs(rx - dx) <= 1024 && Math.Abs(ry - dy) <= 512 && Math.Abs(rz - dz) <= 1024;
                    var joined = areaOf.TryGetValue(a.Index, out var aa) && areaOf.TryGetValue(bIndex, out var ba) && aa == ba;
                    Console.WriteLine($"{a.Index,3} <-> {bIndex,3}  from a: ({dx / 512.0:0.#},{dy / 256.0:0.#},{dz / 512.0:0.#})  from b: ({rx / 512.0:0.#},{ry / 256.0:0.#},{rz / 512.0:0.#})  {(agree ? "AGREE" : "differ")}{(joined ? "  [joined]" : "")}   {game.Description(a.Index)} / {game.Description(bIndex)}");
                }
            }
        return 0;
    }
}

// lba2names [folder]: LBA2's scene descriptions grouped by the words before the first comma (the island names the Scenes menu strips).
internal static class Lba2NameStudy
{
    public static int Run(string[] args)
    {
        var path = System.IO.Path.Combine(args.Length > 1 ? args[1] : @"E:\GOG Games\Little Big Adventure 2 - Level viewer", "SCENE.HQR");
        var names = LBAAssembler.HqdDescriptions.Load("SCENE2.HQD", LBAAssembler.HqrArchive.CountEntries(path)).Names;
        foreach (var g in names.Where(n => n is not null).GroupBy(n => n!.Contains(", ") ? n[..n.IndexOf(", ", StringComparison.Ordinal)] : "(no comma)").OrderByDescending(g => g.Count()))
            Console.WriteLine($"{g.Count(),4}  {g.Key}   e.g. {string.Join(" | ", g.Take(3))}");
        return 0;
    }
}

// overlaps <folder>: for every joined area, how many brick cells two of its scenes both fill (same world cell): scenes that really continue
// into each other share none (or a few at a seam), while a scene put in the wrong place buries part of its neighbour.
internal static class OverlapStudy
{
    public static int Run(string[] args) { Report(new Lba1Game(args[1]), true, args.Length > 2 && args[2] == "columns"); return 0; }

    // `columns`: the plan view instead of the cells: two scenes share a column when both have a brick in it at any height (stacked floors count).
    public static List<(int Area, int A, int B, int Cells)> Report(Lba1Game game, bool print, bool columns = false)
    {
        var result = new List<(int, int, int, int)>();
        for (var a = 0; a < game.Areas.Count; a++)
        {
            var area = game.Areas[a];
            var cells = area.Tiles.ToDictionary(t => t.Scene, t => Lba1GridRenderer.Placements(game.ReadGrid(t.Scene), game.ReadBlocks(t.Scene))
                .Select(p => (X: p.X + t.OffsetX / 512, Y: columns ? 0 : p.Y + t.OffsetY / 256, Z: p.Z + t.OffsetZ / 512)).ToHashSet());
            foreach (var t1 in area.Tiles)
                foreach (var t2 in area.Tiles.Where(t => t.Scene > t1.Scene))
                {
                    var shared = cells[t1.Scene].Count(c => cells[t2.Scene].Contains(c));
                    if (shared == 0) continue;
                    result.Add((a, t1.Scene, t2.Scene, shared));
                    if (print) Console.WriteLine($"  area {a} ({area.Name}): scenes {t1.Scene} and {t2.Scene} both fill {shared} cells");
                }
        }
        return result;
    }
}

// blockuse <folder> <grid> [x0 x1 z0 z1 ymin ymax]: every block a grid uses with its size and how many cells show it, and (with a range) the cells in it.
internal static class BlockUse
{
    public static int Run(string[] args)
    {
        var game = new Lba1Game(args[1]);
        var grid = int.Parse(args[2]);
        var cells = Lba1GridCodec.Decode(game.ReadGrid(grid));
        var blocks = game.ReadBlocks(grid);
        (int Dx, int Dy, int Dz) Size(int block)
        {
            var at = BitConverter.ToInt32(blocks, (block - 1) * 4);
            return (blocks[at], blocks[at + 1], blocks[at + 2]);
        }
        var use = new Dictionary<int, int>();
        for (var i = 0; i < 64 * 64 * 25; i++) { var b = cells[i * 2]; if (b != 0) use[b] = use.GetValueOrDefault(b) + 1; }
        foreach (var (b, n) in use.OrderBy(u => u.Key)) { var s = Size(b); Console.WriteLine($"block {b,4}: {s.Dx}x{s.Dy}x{s.Dz}  in {n} cells"); }
        if (args.Length > 8)
        {
            int x0 = int.Parse(args[3]), x1 = int.Parse(args[4]), z0 = int.Parse(args[5]), z1 = int.Parse(args[6]), y0 = int.Parse(args[7]), y1 = int.Parse(args[8]);
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                    Console.WriteLine($"x{x,2} z{z,2}: " + string.Join(" ", Enumerable.Range(y0, y1 - y0 + 1).Select(y => { var i = ((z * 64 + x) * 25 + y) * 2; return cells[i] == 0 ? (cells[i + 1] == 0 ? "." : "c" + cells[i + 1]) : $"{cells[i]}/{cells[i + 1]}"; })));
        }
        return 0;
    }
}

// blockcells <folder> <grid> <block>...: where those blocks' cells are (x, layer, z, position in the block).
internal static class BlockCells
{
    public static int Run(string[] args)
    {
        var game = new Lba1Game(args[1]);
        var cells = Lba1GridCodec.Decode(game.ReadGrid(int.Parse(args[2])));
        foreach (var block in args.Skip(3).Select(int.Parse))
        {
            var where = new List<string>();
            for (var z = 0; z < 64; z++) for (var x = 0; x < 64; x++) for (var y = 0; y < 25; y++) { var i = ((z * 64 + x) * 25 + y) * 2; if (cells[i] == block) where.Add($"({x},{y},{z})/{cells[i + 1]}"); }
            Console.WriteLine($"block {block}: {string.Join(" ", where.Take(20))}");
        }
        return 0;
    }
}

// toprare <folder> <grid> [max]: the bricks that top only a few columns of a grid (a lamp's globe, a sign, a plant...) with the columns.
internal static class TopRare
{
    public static int Run(string[] args)
    {
        var game = new Lba1Game(args[1]);
        var grid = int.Parse(args[2]);
        var max = args.Length > 3 ? int.Parse(args[3]) : 10;
        var (top, brick) = game.ColumnTops(grid);
        var cells = Lba1GridCodec.Decode(game.ReadGrid(grid));
        foreach (var g in Enumerable.Range(0, 64 * 64).Where(i => top[i] >= 0).GroupBy(i => brick[i]).Where(g => g.Count() <= max).OrderBy(g => g.Key))
            Console.WriteLine($"brick {g.Key,5}: " + string.Join(" ", g.Select(i => { var y = top[i]; var c = ((i * 25 + y) * 2); return $"({i % 64},{y},{i / 64} b{cells[c]}/{cells[c + 1]})"; })));
        return 0;
    }
}

// blockdump <folder> <grid> <block>: a block's cells (position -> x,y,z inside it, shape, sound, brick).
internal static class BlockDump
{
    public static int Run(string[] args)
    {
        var game = new Lba1Game(args[1]);
        var blocks = game.ReadBlocks(int.Parse(args[2]));
        var block = int.Parse(args[3]);
        var at = BitConverter.ToInt32(blocks, (block - 1) * 4);
        int dx = blocks[at], dy = blocks[at + 1], dz = blocks[at + 2];
        Console.WriteLine($"block {block}: {dx} x {dy} x {dz}");
        for (var pos = 0; pos < dx * dy * dz; pos++)
        {
            var e = at + 3 + pos * 4;
            var brick = BitConverter.ToUInt16(blocks, e + 2);
            var y = pos % dy; var x = pos / dy % dx; var z = pos / dy / dx;
            Console.WriteLine($"  pos {pos,2} (x{x} y{y} z{z}): shape {blocks[e]} sound {blocks[e + 1]} brick {(brick == 0 ? "-" : (brick - 1).ToString())}");
        }
        return 0;
    }
}

// lampdebug <folder>: stands Twinsen in the lamp's key zone in the simulation, presses action and prints what happens.
internal static class LampDebug
{
    public static int Run(string[] args)
    {
        var data = new LBAAssembler.Lba1.Runtime.Lba1RuntimeData(args[1]);
        var rt = new LBAAssembler.Lba1.Runtime.Lba1Runtime(data);
        rt.ChangeCube(13);
        rt.Run(30);
        rt.Place(768, 2048, 32000, 256);
        Console.WriteLine($"hero ({rt.Hero.PosX},{rt.Hero.PosY},{rt.Hero.PosZ}) keys {rt.NbLittleKeys} zone {rt.Hero.ZoneSce} comportement {rt.Comportement}");
        rt.Fire = LBAAssembler.Lba1.Runtime.Lba1Const.FSpace;
        for (var i = 0; i < 8; i++) { rt.Frame(); Console.WriteLine($"  frame {i}: hero ({rt.Hero.PosX},{rt.Hero.PosY},{rt.Hero.PosZ}) anim {rt.Hero.GenAnim} keys {rt.NbLittleKeys}"); }
        rt.Fire = 0;
        for (var i = 0; i < 12; i++) { rt.Run(20); foreach (var e in rt.Extras.Where(e => e.Sprite != -1)) Console.WriteLine($"  t+{i * 20}: extra sprite {e.Sprite} at ({e.PosX},{e.PosY},{e.PosZ}) flags {e.Flags:X} fly {(e.Flags & 2) != 0}"); }
        Console.WriteLine($"after: hero ({rt.Hero.PosX},{rt.Hero.PosY},{rt.Hero.PosZ}) keys {rt.NbLittleKeys}");
        foreach (var e in rt.Events.TakeLast(8)) Console.WriteLine("  " + e);
        return 0;
    }
}

// separate <folder> [radius]: for every joined area with scenes that share cells, the smallest move of one of them (in cells along x and z, keeping
// its layer; a layer move only when nothing near works) after which no two scenes of the area share a cell, as `Lba1Areas.Separations` lines.
internal static class SeparateStudy
{
    private static bool columnsMode;
    private static long Key(int x, int y, int z) => (((long)(x + 2048) * 8192) + (z + 2048)) * 512 + ((columnsMode ? 0 : y) + 256);

    public static int Run(string[] args)
    {
        var game = new Lba1Game(args[1]);
        var radius = args.Length > 2 && int.TryParse(args[2], out var rr) ? rr : 40;
        var columns = columnsMode = args.Contains("columns");      // the plan view: two scenes share a column when both have a brick in it at any height
        var lines = new List<string>();
        foreach (var area in game.Areas)
        {
            var own = area.Tiles.ToDictionary(t => t.Scene, t => Lba1GridRenderer.Placements(game.ReadGrid(t.Scene), game.ReadBlocks(t.Scene)).Select(p => (X: p.X, Y: columns ? 0 : p.Y, Z: p.Z)).Distinct().ToArray());
            var at = area.Tiles.ToDictionary(t => t.Scene, t => (X: t.OffsetX / 512, Y: t.OffsetY / 256, Z: t.OffsetZ / 512));
            var moved = area.Tiles.ToDictionary(t => t.Scene, _ => (X: 0, Y: 0, Z: 0));
            int Degree(int s) => Lba1Areas.ManualLinks.Count(l => l.Anchor == s || l.Scene == s);

            HashSet<long> Others(int scene) => area.Tiles.Where(t => t.Scene != scene).SelectMany(t => own[t.Scene].Select(c => Key(c.X + at[t.Scene].X, c.Y + at[t.Scene].Y, c.Z + at[t.Scene].Z))).ToHashSet();
            int Shared(int scene, HashSet<long> others, int dx, int dy, int dz) => own[scene].Count(c => others.Contains(Key(c.X + at[scene].X + dx, c.Y + at[scene].Y + dy, c.Z + at[scene].Z + dz)));

            for (var round = 0; round < 12; round++)
            {
                (int A, int B, int Cells)? worst = null;
                foreach (var a in area.Tiles)
                    foreach (var b in area.Tiles.Where(t => t.Scene > a.Scene))
                    {
                        var setB = own[b.Scene].Select(c => Key(c.X + at[b.Scene].X, c.Y + at[b.Scene].Y, c.Z + at[b.Scene].Z)).ToHashSet();
                        var n = own[a.Scene].Count(c => setB.Contains(Key(c.X + at[a.Scene].X, c.Y + at[a.Scene].Y, c.Z + at[a.Scene].Z)));
                        if (n > 0 && (worst is null || n > worst.Value.Cells)) worst = (a.Scene, b.Scene, n);
                    }
                if (worst is null) break;
                // move the scene with fewer links (ties: the higher number)
                var (pa, pb, _) = worst.Value;
                var mover = Degree(pa) < Degree(pb) ? pa : Degree(pb) < Degree(pa) ? pb : Math.Max(pa, pb);
                var others = Others(mover);
                (int X, int Y, int Z)? best = null;
                foreach (var dy in columns ? new[] { 0 } : new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6 })
                {
                    var candidates = new List<(int X, int Z)>();
                    for (var dx = -radius; dx <= radius; dx++) for (var dz = -radius; dz <= radius; dz++) candidates.Add((dx, dz));
                    foreach (var (dx, dz) in candidates.OrderBy(c => Math.Abs(c.X) + Math.Abs(c.Z)).ThenBy(c => c.X * c.X + c.Z * c.Z))
                        if (Shared(mover, others, dx, dy, dz) == 0) { best = (dx, dy, dz); break; }
                    if (best is not null) break;
                }
                if (best is null) { Console.WriteLine($"  area {area.Name}: no free place for scene {mover} within {radius} cells"); break; }
                at[mover] = (at[mover].X + best.Value.X, at[mover].Y + best.Value.Y, at[mover].Z + best.Value.Z);
                moved[mover] = (moved[mover].X + best.Value.X, moved[mover].Y + best.Value.Y, moved[mover].Z + best.Value.Z);
                Console.WriteLine($"  area {area.Name}: scenes {pa}/{pb} shared {worst.Value.Cells}: move {mover} by {best.Value}");
            }
            foreach (var (scene, m) in moved.Where(m => m.Value != (0, 0, 0))) lines.Add($"        ({scene}, {m.X}, {m.Y}, {m.Z}),   // {area.Name}");
        }
        Console.WriteLine("Separations:");
        foreach (var l in lines) Console.WriteLine(l);
        return 0;
    }
}

// lba2links <folder> <text>...: the LBA2 scenes whose description contains any of the texts, with every cube-change zone (destination, the zone,
// the arrival, and the offset in cells the two scenes sit at if the zone joins them: zone corner minus arrival) and whether the destination
// zone leads back.
internal static class Lba2LinkStudy
{
    public static int Run(string[] args)
    {
        var dir = args[1];
        var path = System.IO.Path.Combine(dir, "SCENE.HQR");
        var count = LBAAssembler.HqrArchive.CountEntries(path);
        var names = LBAAssembler.HqdDescriptions.Load("SCENE2.HQD", count).Names;
        var store = new LBAAssembler.Scenes.SceneStore(LBAAssembler.Scenes.SceneGame.Lba2, dir);
        string Name(int scene) => scene + 1 < names.Count ? names[scene + 1] ?? "?" : "?";
        var wanted = Enumerable.Range(0, count - 1).Where(s => args.Skip(2).Any(t => Name(s).Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var s in wanted)
        {
            var scene = store.Load(s);
            Console.WriteLine($"{s}: {Name(s)}  [{(scene.CubeMode == 0 ? "interior" : "exterior")}, island byte {scene.Island}, cube {scene.CubeX},{scene.CubeY}, {scene.Actors.Count} actors]");
            foreach (var z in scene.Zones.Where(z => z.Type == 0))
            {
                var dest = z.Num;
                var back = "";
                try
                {
                    var target = store.Load(dest);
                    var r = target.Zones.Where(t => t.Type == 0 && t.Num == s).ToList();
                    back = r.Count == 0 ? " (no zone back)" : " back: " + string.Join("; ", r.Select(t => $"offset {(t.X0 - t.Info[0]) / 512.0:0.#},{(t.Y0 - t.Info[1]) / 256.0:0.#},{(t.Z0 - t.Info[2]) / 512.0:0.#}"));
                }
                catch (Exception e) { back = " (" + e.GetType().Name + ")"; }
                Console.WriteLine($"    -> {dest}: {Name(dest)}  zone ({z.X0},{z.Y0},{z.Z0})-({z.X1},{z.Y1},{z.Z1}) arrival ({z.Info[0]},{z.Info[1]},{z.Info[2]}) offset {(z.X0 - z.Info[0]) / 512.0:0.#},{(z.Y0 - z.Info[1]) / 256.0:0.#},{(z.Z0 - z.Info[2]) / 512.0:0.#}{back}");
            }
        }
        return 0;
    }
}

// tippetfit <folder>: the smallest displacement of the bar (76) and the cafe (80) from where their zones put them (74 stays) that leaves no plan-view column shared
// by two scenes of the village map; cost = how far each door ends up from where its zone says (the bar from the village, the cafe from the bar).
internal static class TippetFit
{
    public static int Run(string[] args)
    {
        var game = new Lba1Game(args[1]);
        var area = game.Areas.First(a => a.Tiles.Any(t => t.Scene == 74));
        var scenes = area.Tiles.Select(t => t.Scene).ToList();
        Console.WriteLine("scenes: " + string.Join(", ", scenes));
        var own = area.Tiles.ToDictionary(t => t.Scene, t => Lba1GridRenderer.Placements(game.ReadGrid(t.Scene), game.ReadBlocks(t.Scene)).Select(p => (p.X + t.OffsetX / 512, p.Z + t.OffsetZ / 512)).Distinct().ToArray());
        int Shared(int a, (int X, int Z) da, int b, (int X, int Z) db)
        {
            var set = own[b].Select(c => (c.Item1 + db.X, c.Item2 + db.Z)).ToHashSet();
            return own[a].Count(c => set.Contains((c.Item1 + da.X, c.Item2 + da.Z)));
        }
        var best = new List<(int Cost, (int, int) D76, (int, int) D80)>();
        for (var x76 = -14; x76 <= 14; x76++)
            for (var z76 = -14; z76 <= 14; z76++)
            {
                if (Shared(74, (0, 0), 76, (x76, z76)) != 0) continue;
                for (var x80 = -14; x80 <= 14; x80++)
                    for (var z80 = -14; z80 <= 14; z80++)
                    {
                        if (Shared(74, (0, 0), 80, (x80, z80)) != 0 || Shared(76, (x76, z76), 80, (x80, z80)) != 0) continue;
                        var cost = Math.Abs(x76) + Math.Abs(z76) + Math.Abs(x80 - x76) + Math.Abs(z80 - z76);
                        best.Add((cost, (x76, z76), (x80, z80)));
                    }
            }
        foreach (var b in best.OrderBy(b => b.Cost).Take(12)) Console.WriteLine($"cost {b.Cost}: bar {b.D76}, cafe {b.D80}");
        return 0;
    }
}
