using LBAAssembler.Lba1;
using LBAAssembler.Scenes;

namespace LBAAssembler;

// LBA2 interior scenes that lead into one another through cube-change zones (a factory's rooms, a control tower and the palace behind it) drawn as
// one map, the way Lba1Areas joins LBA1's. Which scenes belong together is written down in `Links` (LBA2's scenes carry no indoor / outdoor
// grouping that says a room continues into the next; joining every pair of scenes whose zones meet would join a whole island's interiors): the
// second scene of a link is placed where the first one's cube-change zone to it puts it (the zone's corner minus the arrival point is the
// same spot of the world seen from both scenes), and then, so that no two scenes of a map share a cell, `Separations` moves some by a few cells.
// The maps are drawn with the managed grid renderer (Lba2Interiors), the same as the LBA1 ones.
internal static class Lba2Areas
{
    // `Scene` sits where the `Nth` (0 = first) cube-change zone of `Anchor` leading to it puts it.
    internal readonly record struct Link(int Anchor, int Scene, int Nth = 0);

    internal static readonly Link[] Links =
    {
        // Francos Island's gazogem factory: the four rooms in a chain, each one's zone leads into the next (the two directions agree to a cell or two),
        // and the two secret rooms open off the second room and the fourth.
        new(140, 141), new(141, 142), new(142, 143), new(141, 145), new(143, 146),
        // Island CX's control tower is two maps: the upper level (the stairs, the tower itself and the only outside scene, each opening onto the next)
        // and the lower one (the room in the emperor's palace and the secret passage between its two levels, which is why it has two zones into
        // that room: the first is used). The zone from the outside scene down to the palace room is left out: put in one picture the two levels lay
        // over one another. The Esmer shuttle only leads back to Francos Island, and the stairs' three zones that lead back into their own scene are
        // one flight going up.
        new(177, 178), new(178, 179), new(181, 180),
        // Otringal's palace: sixteen rooms of 13 x 13 cells in a square, four by four (each room's zones lead to the next along the row and down the
        // column, and the two directions agree exactly), and the last room of the palace (80) at the far end of the fourth row.
        new(151, 152), new(152, 153), new(153, 154), new(155, 156), new(156, 157), new(157, 158), new(159, 160), new(160, 161), new(161, 162), new(163, 164), new(164, 165), new(165, 166),
        new(151, 155), new(155, 159), new(159, 163), new(152, 156), new(156, 160), new(160, 164), new(153, 157), new(157, 161), new(161, 165), new(154, 158), new(158, 162), new(162, 166),
        new(163, 80),
        // Every other group of interiors whose cube-change zones lead into one another both ways (`lba2groups` lists them): one map each, the first
        // scene of a link placed where its zone puts the second. Links whose two zones disagree are left out (the temple's first two scenes, the
        // Esmer shuttle and the departure room's space port).
        // Citadel Island: Twinsen's house, the Tralu building, the tavern and its cellar, the baggage claim building and the sewer with the rooms off it.
        new(0, 1),
        new(2, 19), new(2, 20),
        new(3, 4),
        new(5, 6), new(6, 16),
        new(17, 18), new(17, 21), new(17, 34),
        // White Leaf Desert: the Esmer base with the temple's second scene, the hacienda with the two Turkish baths and the passage between them, the
        // School of Magic and its training room, and the protection spell cave.
        new(11, 12),
        new(29, 24), new(29, 25), new(29, 30), new(30, 32),
        new(27, 28), new(27, 33),
        new(183, 184),
        // Emerald Moon: the two scenes outside Baldino's cell and the four buildings and his cell that open off them, one map called Inside Base.
        new(13, 23), new(13, 52), new(13, 54), new(13, 167), new(23, 53), new(23, 168),
        // Otringal: the Imperial Hotel and its rooms, the elevators, the prison, the casino, the bar and the souvenir shop with the dissidents' hide out.
        new(81, 79), new(81, 169), new(81, 170),
        new(82, 139),
        new(83, 84),
        new(135, 133),
        new(136, 134), new(136, 137),
        new(147, 148),
        // Wannies Island: the mine (its first three rooms: the mine's temple, 117, and the box transport building, 124, are in theory connected too but do not fit\n        // with them, so they stay scenes of their own) and the city (the houses, the way to its temple and the temple).
        new(100, 112), new(100, 122), new(112, 111),
        new(118, 101), new(118, 116), new(118, 119), new(119, 121),
        // Mosquibees Island: the queen's throne with the maze and the bee test room off it.
        new(104, 106), new(104, 128),
        // The Dark Monk Statue (Celebration Island): five scenes stacked one above another (the top, 186, above the wizards' scene, 185; the third, 187,
        // below it: the wizards' scene has a one-way zone down to it, and the last scene and the secret scene open off the third).
        new(185, 186), new(185, 187), new(187, 188), new(187, 192),
    };

    // A scene with two separate areas that the map puts in different places: `Window` (cell columns of the scene) is cut out of the scene's tile and
    // drawn moved by (dx, dy, dz) cells. Island CX's stairs (177) are a tower at one corner of the grid and, at the other, the small room the top of
    // the stairs leads into: the stairs' top zone arrives in the room at cell (56, 8, 54) from (9, 25, 0), so the room is drawn (-47, +17, -54) cells
    // from where it is in the scene, on top of the stairs.
    internal readonly record struct Part(int Scene, CellWindow Window, int Dx, int Dy, int Dz);

    internal static readonly Part[] Parts =
    {
        new(177, new CellWindow(44, 44, 63, 63), -47, 17, -54),
    };

    // Areas are called by any one of their scenes here.
    private static readonly (int Scene, string Name)[] AreaNames =
    {
        (140, "Gazogem factory"),
        (177, "Control tower, upper level"),
        (181, "Control tower, lower level"),
        (151, "Emperor's palace"),
        (0, "Twinsen's house"),
        (2, "Tralu"),
        (3, "Tavern"),
        (5, "Baggage claim building"),
        (17, "Sewer"),
        (11, "Esmer base"),
        (29, "Turkish baths"),
        (27, "School of Magic"),
        (183, "Protection spell cave"),
        (13, "Inside Base"),
        (81, "Imperial Hotel"),
        (82, "Elevators"),
        (83, "Prison"),
        (135, "Casino"),
        (136, "Bar"),
        (147, "Souvenir shop"),
        (100, "The mine"),
        (118, "City"),
        (104, "Queen's throne"),
        (185, "Dark Monk Statue"),
    };

    // Scenes moved by a few cells once their map is placed, so that no two scenes of a map share a cell (`lba2overlaps`, `lba2separate`):
    // (scene, cells in x, layers in y, cells in z).
    // (found by `lba2separate <folder>`: the smallest move of one scene of each overlapping pair that leaves no shared cell in its map)
    internal static readonly (int Scene, int Dx, int Dy, int Dz)[] Separations =
    {
        // Otringal's palace: the sixteen rooms overlap their neighbours' walls by up to four cells where the zones put them (13 cells apart), so the square
        // is drawn with a pitch of 17 cells (four more for every room along a row and for every row down the column; `lba2pitch` finds the least pitch
        // that shares no cell) and each room stands clear of the next.
        (152, 4, 0, 0), (153, 8, 0, 0), (154, 12, 0, 0),
        (155, 0, 0, -4), (156, 4, 0, -4), (157, 8, 0, -4), (158, 12, 0, -4),
        (159, 0, 0, -8), (160, 4, 0, -8), (161, 8, 0, -8), (162, 12, 0, -8),
        (163, 0, 0, -12), (164, 4, 0, -12), (165, 8, 0, -12), (166, 12, 0, -12),
        (80, 0, 0, -16),      // the palace's last room, off the maze's fourth row
        // found by `lba2separate`: the smallest move of one tile of each pair that shares a plan column, until no two tiles of a map do
        // Twinsen's house
        (1, -2, 0, 4),
        // Tralu
        (19, 0, 0, -5),
        (20, -2, 0, -2),
        // Tavern
        (4, 6, 0, 0),
        // Baggage claim building
        (5, -2, 0, 0),
        (16, -4, 0, -1),
        // Esmer base
        (12, -52, 0, 0),
        // Inside Base (Emerald Moon)
        (23, 0, 0, -2),
        (52, 3, 0, 0),
        (53, -3, 0, -2),
        (54, -3, 0, 0),
        (167, 3, 0, 0),
        (168, 3, 0, 0),
        // Sewer
        (18, -3, 0, -3),
        (21, 0, 0, 13),
        (34, -27, 0, 0),
        // Turkish baths
        (24, 0, 0, 2),
        (25, 13, 0, 0),
        (30, -14, 0, 0),
        (32, 0, 0, -18),
        // School of Magic
        (28, 0, 0, 2),
        (33, -4, 0, 0),
        // Imperial Hotel
        (79, -9, 0, -2),
        (169, 5, 0, -3),
        (170, 0, 0, -12),
        // Elevators
        (139, 0, 0, -4),
        // Prison
        (84, 0, 0, 13),
        // City
        (101, 3, 0, 0),
        (116, 3, 0, -2),
        (119, -14, 0, 1),
        (121, 0, 0, -5),
        // Queen's throne
        (106, -4, 0, 0),
        (128, 10, 0, 0),
        // Casino
        (135, 3, 0, 0),
        // Bar
        (134, -14, 0, -3),
        (137, 0, 0, -10),
        // Gazogem factory
        (140, -2, 0, 0),
        (142, -3, 0, -9),
        (145, 1, 0, -12),
        (146, 0, 0, -6),
        // Souvenir shop
        (148, 0, 0, -2),
        // Control tower, upper level
        (178, 4, 0, -9),
        (179, 3, 0, 3),
        // Control tower, lower level
        (181, 51, 0, 0),
        // Protection spell cave
        (184, 4, 0, -7),
        // The mine (the temple and the box transport building are scenes of their own)
        (111, 0, 0, -27), (112, -2, 0, 0), (122, -7, 0, -5),
        // Dark Monk Statue: the five scenes stack one above another where their zones put them in the plan (no move in x or z), each lifted so far above the one below it that
        // its picture stands clear of that one's: no screen column of the upper scene reaches down to the lower scene's picture (`lba2screenlift <folder>`; bottom to top: 188,
        // 187, 192, 185, 186; 192 is the small secret scene that stands over the third one and the last). The picture is tall and has black space between the levels.
        (187, 0, 87, 0), (192, 0, 107, 0), (185, 0, 162, 0), (186, 0, 194, 0),
    };

    // Maps whose scenes really stack one above another (the Dark Monk statue): their overlap is counted in cells, not in the plan view.
    public static bool IsStacked(Lba1Area area) => area.Tiles.Any(t => t.Scene == 185);

    private static int Round(int value, int unit) => (int)Math.Round(value / (double)unit) * unit;

    // The offset (world units) of `Link.Scene`'s origin in `Link.Anchor`'s coordinates, or null when the anchor has no such zone.
    public static (int X, int Y, int Z)? OffsetOf(Link link, Func<int, SceneModel?> load)
    {
        if (load(link.Anchor) is not { } anchor) return null;
        var zone = anchor.Zones.Where(z => z.Type == 0 && z.Num == link.Scene).Skip(link.Nth).FirstOrDefault();
        return zone is null ? null : (Round(zone.X0 - zone.Info[0], 512), Round(zone.Y0 - zone.Info[1], 256), Round(zone.Z0 - zone.Info[2], 512));
    }

    public static List<Lba1Area> Find(Func<int, SceneModel?> load)
    {
        var edges = new Dictionary<int, List<(int To, int Dx, int Dy, int Dz)>>();
        void AddEdge(int from, int to, int dx, int dy, int dz)
        {
            if (!edges.TryGetValue(from, out var list)) edges[from] = list = new();
            list.Add((to, dx, dy, dz));
            if (!edges.TryGetValue(to, out var reverse)) edges[to] = reverse = new();
            reverse.Add((from, -dx, -dy, -dz));
        }
        foreach (var link in Links)
        {
            if (load(link.Scene) is null) continue;
            if (OffsetOf(link, load) is { } o) AddEdge(link.Anchor, link.Scene, o.X, o.Y, o.Z);
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
            var tiles = new List<Lba1AreaTile>();
            (int X, int Y, int Z) Moved(int key) => Separations.Where(s => s.Scene == key).Select(s => (s.Dx, s.Dy, s.Dz)).FirstOrDefault();
            foreach (var o in offsets.OrderBy(o => o.Key))
            {
                var part = Parts.Where(p => p.Scene == o.Key).Cast<Part?>().FirstOrDefault();
                var (sx, sy, sz) = Moved(o.Key);
                tiles.Add(new Lba1AreaTile(o.Key, o.Value.X + sx * 512, o.Value.Y + sy * 256, o.Value.Z + sz * 512, Without: part?.Window));
                if (part is { } p)
                {
                    var (px, py, pz) = Moved(-o.Key);
                    tiles.Add(new Lba1AreaTile(o.Key, o.Value.X + (p.Dx + px) * 512, o.Value.Y + (p.Dy + py) * 256, o.Value.Z + (p.Dz + pz) * 512, Window: p.Window));
                }
            }
            var name = AreaNames.FirstOrDefault(n => tiles.Any(t => t.Scene == n.Scene)).Name ?? "Connected interiors";
            areas.Add(new Lba1Area(load(start)!.Island, tiles) { Name = name });
        }
        return areas;
    }
}
