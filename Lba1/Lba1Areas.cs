namespace LBA2LevelEditor.Lba1;

// A scene placed in an area's shared coordinate space: the scene's world origin sits at
// (OffsetX, OffsetY, OffsetZ) there (world units, multiples of 512 horizontally and 256 vertically).
internal sealed record Lba1AreaTile(int Scene, int OffsetX, int OffsetY, int OffsetZ);

// Scenes that join edge to edge into one continuous map (the first tile is at offset 0). Name says what the
// map is ("Outside" unless Lba1Areas.AreaNames knows better).
internal sealed record Lba1Area(int Island, IReadOnlyList<Lba1AreaTile> Tiles)
{
    public string Name { get; init; } = "Outside";

    // "Outside (scenes 1, 2, 3)"
    public string Label => $"{Name} (scenes {string.Join(", ", Tiles.Select(t => t.Scene))})";
}

// LBA1 scene files carry no indoor/outdoor flag. What does identify a map that continues into
// its neighbour is the cube-change zone along a grid edge: walking off the +Z edge of one scene
// lands at the -Z edge of the next (arrival position ~0), and the neighbour has the matching
// zone leading back. Such reciprocal, same-island edge links join scenes into an area. Where the
// two grids sit relative to each other follows from the zone and the arrival position: they are
// the same spot of the world, so the difference (in each direction, and from both scenes) is the
// offset, including a height offset when the two floors are on different layers.
//
// Scenes that don't touch along an edge (a plateau reached by stairs, a harbour across a bay) can
// still be joined by hand: see ManualLinks.
internal static class Lba1Areas
{
    private const int Span = 64 * 512;

    // Places `Scene` relative to `Anchor`. Without an offset it is taken from the cube-change zone
    // in Anchor that leads to Scene (zone position minus arrival position, as for edge links);
    // with one, the offset of Scene's origin in Anchor's coordinates is given in cells.
    internal readonly record struct ManualLink(int Anchor, int Scene, int? DxCells = null, int? DyCells = null, int? DzCells = null);

    internal static readonly ManualLink[] ManualLinks =
    {
        // Outside the water tower: placed so its motorbike (actor 2) sits on Peg Leg Street's (actor 3).
        new(Anchor: 25, Scene: 37, DxCells: -57, DyCells: 3, DzCells: -53),
        // Port Belooga lies north of the military camp: the camp's north exit and Port Belooga's south gate share
        // the same columns (and both scenes start the hero there).
        new(Anchor: 19, Scene: 24, DxCells: -6, DyCells: 3, DzCells: -63),
        // The maze (its only exit leads south into the desert) sits north of the desert, placed from the maze's
        // side because the desert has no zone leading back. Its exit zone would put 36's origin at (+52, 0, +64)
        // cells from it; that overlaps the military camp, so it is slid 9 cells east (36 at +43).
        new(Anchor: 57, Scene: 36, DxCells: 43, DyCells: 0, DzCells: 64),
        // Proxima: the sea scene north of the city, placed so its pier (two layers below the city's quay) runs into
        // the gap in the platform's fence. The lower rune stone (45) is placed so its motorbike (actor 1) sits on the
        // city's (actor 14). The upper rune stone (46) follows on from the scene before it: its jetty continues the
        // pier's line, just past the cliff.
        new(Anchor: 42, Scene: 47, DxCells: 8, DyCells: 2, DzCells: -64),
        new(Anchor: 42, Scene: 45, DxCells: 29, DyCells: 3, DzCells: 57),
        new(Anchor: 47, Scene: 46, DxCells: 15, DyCells: 0, DzCells: -14),
        // Hamalayi Mountains (2): the Sacred Carrot leads up behind itself, the bunker to its lake; the mutation
        // centre's two floors form their own map.
        new(Anchor: 81, Scene: 91),
        // The lake's exit zone arrives at its east edge, so it sits west of the bunker's north-west corner, clear of
        // the bunker's buildings (its zone-derived position would bury it under them).
        new(Anchor: 73, Scene: 92, DxCells: -64, DyCells: 0, DzCells: -37),
        new(Anchor: 68, Scene: 67),
        // The bedroom (61) sits in the hollow behind the Lupin Burg house whose bricked-up east arch it is docked to
        // (see Lba1RoomDoorMod): its own doorway (east wall, x 63, z 54-56) lines up with the arch (x 51, z 12-14) one
        // cell further back, so the room hides behind the wall; its floor lines up with the street.
        new(Anchor: 13, Scene: 61, DxCells: -13, DyCells: -2, DzCells: -42),
    };

    // Areas that aren't just "Outside": any scene of the area picks the name.
    private static readonly (int Scene, string Name)[] AreaNames =
    {
        (8, "Temple of Bú"),        // White Leaf Desert, the temple's three scenes
        (67, "Mutation centre"),      // Hamalayi Mountains
    };

    private readonly record struct Link(int From, int To, char Dir, int Perp, int PerpY);

    public static List<Lba1Area> Find(IReadOnlyDictionary<int, Lba1Scene> scenes)
    {
        var links = new List<Link>();
        foreach (var a in scenes.Values)
            foreach (var z in a.Zones.Where(z => z.Type == 0))
            {
                var b = z.Info[0];
                if (b == a.Index || !scenes.TryGetValue(b, out var target) || target.Island != a.Island) continue;
                int ax = z.Info[1], ay = z.Info[2], az = z.Info[3];
                var dy = z.Y0 - ay;
                if (z.Z1 >= 31744 && z.Z0 >= 16000 && az <= 2048) links.Add(new Link(a.Index, b, 'S', z.X0 - ax, dy));
                else if (z.Z0 <= 1023 && z.Z1 <= 1535 && az >= 30000) links.Add(new Link(a.Index, b, 'N', z.X0 - ax, dy));
                else if (z.X1 >= 31744 && z.X0 >= 16000 && ax <= 2048) links.Add(new Link(a.Index, b, 'E', z.Z0 - az, dy));
                else if (z.X0 <= 1023 && z.X1 <= 1535 && ax >= 30000) links.Add(new Link(a.Index, b, 'W', z.Z0 - az, dy));
            }

        // One edge per (from, to, direction): where `to`'s origin sits relative to `from`'s.
        var edges = new Dictionary<int, List<(int To, int Dx, int Dy, int Dz)>>();
        void AddEdge(int from, int to, int dx, int dy, int dz)
        {
            if (!edges.TryGetValue(from, out var list)) edges[from] = list = new();
            list.Add((to, dx, dy, dz));
            if (!edges.TryGetValue(to, out var reverse)) edges[to] = reverse = new();
            reverse.Add((from, -dx, -dy, -dz));
        }

        foreach (var group in links.GroupBy(l => (l.From, l.To, l.Dir)))
        {
            var (from, to, dir) = group.Key;
            var opposite = dir switch { 'S' => 'N', 'N' => 'S', 'E' => 'W', _ => 'E' };
            var back = links.Where(l => l.From == to && l.To == from && l.Dir == opposite).ToList();
            if (back.Count == 0) continue;
            var forward = group.Select(l => l.Perp).ToList();
            var backPerp = back.Select(l => -l.Perp).ToList();
            if (Math.Abs(Median(forward) - Median(backPerp)) > 1536) continue;
            var perp = Round(Median(forward.Concat(backPerp).ToList()), 512);
            var dy = Round(Median(group.Select(l => l.PerpY).Concat(back.Select(l => -l.PerpY)).ToList()), 256);
            var (dx, dz) = dir switch { 'S' => (perp, Span), 'N' => (perp, -Span), 'E' => (Span, perp), _ => (-Span, perp) };
            AddEdge(from, to, dx, dy, dz);
        }

        foreach (var manual in ManualLinks)
        {
            if (!scenes.TryGetValue(manual.Anchor, out var anchor) || !scenes.ContainsKey(manual.Scene)) continue;
            if (manual.DxCells is int mx)
            {
                AddEdge(manual.Anchor, manual.Scene, mx * 512, (manual.DyCells ?? 0) * 256, (manual.DzCells ?? 0) * 512);
                continue;
            }
            var zone = anchor.Zones.FirstOrDefault(z => z.Type == 0 && z.Info[0] == manual.Scene);
            if (zone is null) continue;
            AddEdge(manual.Anchor, manual.Scene,
                Round(zone.X0 - zone.Info[1], 512), Round(zone.Y0 - zone.Info[2], 256), Round(zone.Z0 - zone.Info[3], 512));
        }

        var areas = new List<Lba1Area>();
        var seen = new HashSet<int>();
        foreach (var start in edges.Keys.Order())
        {
            if (!seen.Add(start)) continue;
            var offsets = new Dictionary<int, (int X, int Y, int Z)> { [start] = (0, 0, 0) };
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var (to, dx, dy, dz) in edges[current])
                {
                    if (offsets.ContainsKey(to)) continue;
                    offsets[to] = (offsets[current].X + dx, offsets[current].Y + dy, offsets[current].Z + dz);
                    seen.Add(to);
                    queue.Enqueue(to);
                }
            }
            var tiles = offsets.OrderBy(o => o.Key).Select(o => new Lba1AreaTile(o.Key, o.Value.X, o.Value.Y, o.Value.Z)).ToList();
            var named = AreaNames.FirstOrDefault(n => tiles.Any(t => t.Scene == n.Scene)).Name;
            areas.Add(new Lba1Area(scenes[start].Island, tiles) { Name = named ?? "Outside" });
        }
        return areas;
    }

    private static int Round(double value, int step) => (int)Math.Round(value / step) * step;

    private static double Median(List<int> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
