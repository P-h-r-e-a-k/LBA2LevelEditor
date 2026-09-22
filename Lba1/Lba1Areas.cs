namespace LBAAssembler.Lba1;

// A scene placed in an area's shared coordinate space: the scene's world origin sits at
// (OffsetX, OffsetY, OffsetZ) there (world units, multiples of 512 horizontally and 256 vertically).
internal sealed record Lba1AreaTile(int Scene, int OffsetX, int OffsetY, int OffsetZ, CellWindow? Window = null, CellWindow? Without = null)
{
    // A scene's own number, or its negative for the piece of it a `Window` cuts out (a scene that has two areas the map puts in different places).
    public int Key => Window is null ? Scene : -Scene;

    // Whether the cell column (x, z) of the scene is drawn by this tile: all of it, only the window, or all but the window.
    public bool Holds(int x, int z) => (Window is null || Window.Value.Contains(x, z)) && (Without is null || !Without.Value.Contains(x, z));

    // ... and the point (world units: a cell is 512 wide, centred on cell * 512).
    public bool HoldsPoint(int worldX, int worldZ) => Holds((worldX + 256) >> 9, (worldZ + 256) >> 9);
}

// A rectangle of a scene's cell columns (both ends included).
internal readonly record struct CellWindow(int X0, int Z0, int X1, int Z1)
{
    public bool Contains(int x, int z) => x >= X0 && x <= X1 && z >= Z0 && z <= Z1;
}

// Scenes that join edge to edge into one continuous map (the first tile is at offset 0). Name says what the
// map is ("Outside" unless Lba1Areas.AreaNames knows better).
internal sealed record Lba1Area(int Island, IReadOnlyList<Lba1AreaTile> Tiles)
{
    public string Name { get; init; } = "Outside";

    // What the scene list shows for the map: its name (a map is not told apart by its scene numbers).
    public string Label => Name;
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
        // Outside the water tower: its ochre path leaves the scene along the east edge (cells z 55..63, on the ground layer) and
        // Peg Leg Street's path arrives along its west edge (cells z 7..15, three layers up), so the water tower sits
        // directly west of the street, its east edge against the street's west edge and the paths meeting (an earlier placement, by
        // the two motorbikes, buried six columns of the street under it and left the paths three cells apart).
        new(Anchor: 25, Scene: 37, DxCells: -64, DyCells: 3, DzCells: -48),
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
        // The transporter's inside is the building whose gate stands at the top of the outside scene's snowfield.
        new(Anchor: 65, Scene: 66),
        // The prison: outside it (71) the gate leads into the entrance hall (70), whose stairs lead up into the prison (64); the backdoor's
        // path (82) comes down to the prison's north end. Every offset comes from the cube-change zones (the two directions agree).
        new(Anchor: 71, Scene: 70),
        new(Anchor: 64, Scene: 70),
        new(Anchor: 82, Scene: 64),
        // Tippet Island: the village (74) is a cave town; the bar (76) opens off it and the Twinsun Cafe (80) off the bar. (The shop, 101, opens off the village
        // too but is left out: it sat in the way of the bar and the cafe.)
        // The three secret passage scenes lead on from one another (the second in the middle: the first below it, the third above it).
        // All from the zones (the two directions agree).
        new(Anchor: 74, Scene: 76),
        new(Anchor: 76, Scene: 80),
        new(Anchor: 75, Scene: 77),
        new(Anchor: 75, Scene: 79),
        // Citadel Island: the tavern (14) and the cellar below it (33), and the sewer of the first scene (34) and the secret sewer (55) it leads into,
        // each pair from the first scene's zones (the tavern's two directions agree, the sewers' to a cell).
        new(Anchor: 14, Scene: 33),
        new(Anchor: 34, Scene: 55),
        // Brundle Island: the inside of the teleportation building (95) with its secret room (98) and the telepods (99) off it, from its zones.
        new(Anchor: 95, Scene: 98),
        new(Anchor: 95, Scene: 99),
        // Fortress Island: the swimming pool (88) with the first secret passage scene (85) and the corridor near Zoe's cell (87) off it, from its zones.
        new(Anchor: 88, Scene: 85),
        new(Anchor: 88, Scene: 87),
        // The two end sequence scenes: the second ends by changing to the first (its zone), so the first sits where that zone puts it.
        new(Anchor: 117, Scene: 116),
        // The fortress's inside (83) and the cloning centre (89) off it, from the zones. The rune stone (90) has no zone: the cloning centre's script
        // changes to it (change_cube(90)) from its scenaric zone number 1 (cells x 35.5..36.5, z 56.5..60.5, from layer 3 up) once var_game(114) is set,
        // and the rune stone's scene starts Twinsen at x 62.4, z 56.2, layer 6 (its far east edge), so it is placed with that start point in the
        // zone, its floor (layer 3) level with the zone's bottom.
        new(Anchor: 83, Scene: 89),
        new(Anchor: 89, Scene: 90, DxCells: -26, DyCells: 0, DzCells: 2),
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
        (74, "Village"),              // Tippet Island: the village, the bar and the cafe
        (75, "Secret passage"),       // Tippet Island: the three secret passage scenes
        (14, "Tavern"),               // Citadel Island: the tavern and its cellar
        (34, "Sewer"),                // Citadel Island: the sewer of the first scene and the secret sewer
        (83, "Fortress"),             // Fortress Island: the inside of the fortress, the cloning centre and the rune stone
        (88, "Underground Fortress passage"),   // Fortress Island: the swimming pool, the secret passage scene 1 and the way to Zoe's cell
        (116, "End Credits"),         // the two end sequence scenes (filed under Citadel Island, see MapIsland)
        (95, "Teleportation"),        // Brundle Island: the inside of the teleportation building, its secret room and the telepods
    };

    // The scenes whose zones meet at a grid edge but that are not one place: the Principal Island harbour (11) and the ground outside the fortress (12).
    private static readonly (int A, int B)[] NotJoined = { (11, 12) };

    // Scenes the scene list files under another island than the one their data says (the two end sequence scenes are Citadel Island's ending).
    // Only the editor's lists use this; the game data (and so the simulation's text bank and palette) keeps the scene's own island.
    public static int MapIsland(int scene, int island) => scene is 116 or 117 ? 0 : island;

    // Scenes moved by a few cells once their map is placed, so that no two scenes of a map share a cell (`overlaps` and `areatests`): the doorways
    // then no longer meet exactly, but a joined map never has one scene inside another. (scene, cells in x, layers in y, cells in z)
    // (found by `separate <folder> 60 columns`: the smallest move of one scene of each pair that shares a plan column, until no two scenes of a map do)
    internal static readonly (int Scene, int Dx, int Dy, int Dz)[] Separations =
    {
        (66, 0, 0, -6),      // the transporter's inside, off the outside scene 65
        (70, 9, 0, -5),      // the prison's entrance hall, off the prison (64) and the outside of it (71)
        (82, 0, 0, -3),      // the prison's backdoor path, off the prison (64)
        (61, -33, 0, -22),    // the bedroom, off the Lupin Burg house it is docked to (13)
        (33, 8, 0, 0),       // the tavern's cellar, off the tavern (14)
        (91, 0, 0, -11),     // the Sacred Carrot, off the bunker's outside (81)
        (55, -3, 0, 0),      // the secret sewer, off the sewer of the first scene (34)
        (45, 0, 0, 7),       // Proxima's lower rune stone, off the city (42)
        (68, -11, 0, -5),     // the mutation centre's second floor, off the first (67)
        (76, -7, 0, -2),     // Tippet's bar, off the village (74)
        (80, -7, 0, -7),     // Tippet's cafe, off the bar (76)
        (77, 0, 0, 3),       // the second of the secret passage scenes, off the middle one (75)
        (79, -3, 0, 0),      // the third, off the middle one (75)
        (83, 5, 0, 0),       // the fortress's inside, off the cloning centre (89)
        (90, -3, 0, 0),      // the rune stone, off the cloning centre (89)
        (85, 0, 0, 5),       // the passage scene 1, off the swimming pool (88)
        (87, 0, 0, -2),      // the corridor near Zoe's cell, off the swimming pool (88)
        (98, 9, 0, 2),       // the teleportation building's secret room, off its hall (95)
        (99, -8, 0, 0),      // the telepods, off the hall (95)
        (117, 0, 0, -1),     // the second end sequence scene, off the first (116)
    };

    private readonly record struct Link(int From, int To, char Dir, int Perp, int PerpY);

    public static List<Lba1Area> Find(IReadOnlyDictionary<int, Lba1Scene> scenes)
    {
        var links = new List<Link>();
        foreach (var a in scenes.Values)
            foreach (var z in a.Zones.Where(z => z.Type == 0))
            {
                var b = z.Info[0];
                if (b == a.Index || !scenes.TryGetValue(b, out var target) || MapIsland(b, target.Island) != MapIsland(a.Index, a.Island)) continue;
                if (NotJoined.Any(p => (p.A == a.Index && p.B == b) || (p.A == b && p.B == a.Index))) continue;
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
            var tiles = offsets.OrderBy(o => o.Key).Select(o =>
            {
                var (sx, sy, sz) = Separations.Where(s => s.Scene == o.Key).Select(s => (s.Dx, s.Dy, s.Dz)).FirstOrDefault();
                return new Lba1AreaTile(o.Key, o.Value.X + sx * 512, o.Value.Y + sy * 256, o.Value.Z + sz * 512);
            }).ToList();
            var named = AreaNames.FirstOrDefault(n => tiles.Any(t => t.Scene == n.Scene)).Name;
            areas.Add(new Lba1Area(MapIsland(start, scenes[start].Island), tiles) { Name = named ?? "Outside" });
        }
        return areas;
    }

    // The area to show for an island when nothing more specific was asked for: its biggest outside map (the scene list has no indoor/outdoor
    // flag, but "Outside" is what an area is called unless AreaNames says otherwise).
    public static Lba1Area? MainArea(IEnumerable<Lba1Area> areas)
        => areas.OrderByDescending(a => a.Name == "Outside").ThenByDescending(a => a.Tiles.Count).FirstOrDefault();

    // The scene in the middle of a map: the tile whose middle is nearest to the middle of all the tiles (horizontally). What to open when the
    // area is shown one scene at a time.
    public static int CentralScene(Lba1Area area)
    {
        var cx = area.Tiles.Average(t => t.OffsetX + Span / 2.0);
        var cz = area.Tiles.Average(t => t.OffsetZ + Span / 2.0);
        return area.Tiles.OrderBy(t => Math.Pow(t.OffsetX + Span / 2.0 - cx, 2) + Math.Pow(t.OffsetZ + Span / 2.0 - cz, 2)).ThenBy(t => t.Scene).First().Scene;
    }

    // The scene a joined map is "looking at": the tile with the most of its ground inside the part of the screen around (centreX, centreY)
    // (a box of halfWidth x halfHeight either side, in the map picture's pixels). Zoomed right in on one scene that is the one; zoomed
    // out it is the one in the middle; two with equally much in the middle: the lower-numbered. `topOf` gives a scene's highest occupied
    // layer per grid column (-1 for an empty one), `project` the picture position of a world point.
    public static int FocusScene(IReadOnlyList<Lba1AreaTile> tiles, Func<int, int[]> topOf, Func<double, double, double, (double X, double Y)> project,
        double centreX, double centreY, double halfWidth, double halfHeight)
    {
        var best = tiles[0].Scene;
        var bestCount = -1;
        foreach (var tile in tiles.OrderBy(t => t.Scene))
        {
            var top = topOf(tile.Scene);
            var count = 0;
            for (var z = 0; z < 64; z++)
                for (var x = 0; x < 64; x++)
                {
                    var y = top[z * 64 + x];
                    if (y < 0) continue;
                    var p = project(tile.OffsetX + (x + .5) * 512, tile.OffsetY + (y + 1) * 256, tile.OffsetZ + (z + .5) * 512);
                    if (Math.Abs(p.X - centreX) <= halfWidth && Math.Abs(p.Y - centreY) <= halfHeight) count++;
                }
            if (count > bestCount) { best = tile.Scene; bestCount = count; }
        }
        if (bestCount > 0) return best;
        // nothing of the map is in the middle of the picture: the tile whose middle is nearest to it
        return tiles.OrderBy(t =>
        {
            var p = project(t.OffsetX + Span / 2.0, t.OffsetY, t.OffsetZ + Span / 2.0);
            return Math.Pow(p.X - centreX, 2) + Math.Pow(p.Y - centreY, 2);
        }).ThenBy(t => t.Scene).First().Scene;
    }

    private static int Round(double value, int step) => (int)Math.Round(value / step) * step;

    private static double Median(List<int> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
