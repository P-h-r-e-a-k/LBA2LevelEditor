using System.Text.Json;
using LBAAssembler;
using LBAAssembler.Lba1;

namespace ScriptRoundTrip;

// The joined LBA1 maps (Lba1Areas) on the real game files: where the hand-placed scenes sit, that they don't bury their neighbours, which scene
// a view is "looking at", how the Scenes menu writes names, and the "Join connected areas" setting.
internal static class AreaTests
{
    private static readonly string Lba1Dir = Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure";
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
    private static int failures, checks;

    private static void Check(bool ok, string what)
    {
        checks++;
        if (ok) return;
        failures++;
        Console.WriteLine($"  FAIL {what}");
    }

    public static int Run(string[] args)
    {
        Names();
        Settings();
        var game = new Lba1Game(Lba1Dir);
        Placement(game);
        Focus(game);
        LbaNames(game);
        Lba2Maps();
        Console.WriteLine(failures == 0 ? $"area tests: all {checks} checks passed" : $"area tests: {failures} of {checks} checks FAILED");
        return failures == 0 ? 0 : 1;
    }

    // The LBA2 interior maps (Lba2Areas): which scenes, where the zones put them, that no two share a cell, that they draw.
    private static void Lba2Maps()
    {
        if (!File.Exists(Path.Combine(Lba2Dir, "SCENE.HQR")) || !File.Exists(Path.Combine(Lba2Dir, "LBA_BKG.HQR"))) { Console.WriteLine("  (no LBA2 folder; the LBA2 maps aren't checked)"); return; }
        var interiors = new LBAAssembler.Lba2Interiors(Lba2Dir);
        var areas = Lba2Areas.Find(interiors.LoadScene);
        var factory = areas.SingleOrDefault(a => a.Tiles.Any(t => t.Scene == 140));
        var tower = areas.SingleOrDefault(a => a.Tiles.Any(t => t.Scene == 177));
        var lower = areas.SingleOrDefault(a => a.Tiles.Any(t => t.Scene == 181));
        var palace = areas.SingleOrDefault(a => a.Tiles.Any(t => t.Scene == 151));
        Check(factory is not null && factory.Name == "Gazogem factory" && factory.Island == 8 && factory.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 140, 141, 142, 143, 145, 146 }), "LBA2: the gazogem factory's six scenes (four rooms and two secret rooms) are one map, called Gazogem factory, on Francos Island");
        Check(tower is not null && tower.Name == "Control tower, upper level" && tower.Island == 9 && tower.Tiles.Select(t => t.Scene).Distinct().Order().SequenceEqual(new[] { 177, 178, 179 }), "LBA2: Island CX's stairs, control tower and outside scene are one map, Control tower, upper level");
        Check(lower is not null && lower.Name == "Control tower, lower level" && lower.Island == 9 && lower.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 180, 181 }), "LBA2: Island CX's palace room and secret passage are another, Control tower, lower level (the two levels are not put in one picture)");
        Check(palace is not null && palace.Name == "Emperor's palace" && palace.Island == 4 && palace.Tiles.Select(t => t.Scene).Order().SequenceEqual(Enumerable.Range(151, 16).Append(80).Order()), "LBA2: Otringal's palace (the sixteen rooms and the last room) is one map, called Emperor's palace");
        Check(areas.Count == 24 && areas.Select(a => a.Name).Distinct().Count() == 24 && areas.All(a => a.Tiles.Select(t => t.Scene).Distinct().Count() >= 2), "LBA2: 24 joined maps, each with its own name and two scenes or more (Twinsen's house, Tralu, the tavern, the sewer, the baths, the mine, the city, the bar ...)");
        var mine = areas.SingleOrDefault(a => a.Tiles.Any(t => t.Scene == 100));
        var statue = areas.SingleOrDefault(a => a.Tiles.Any(t => t.Scene == 185));
        Check(mine is not null && mine.Name == "The mine" && mine.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 100, 111, 112, 122 }) && !areas.Any(a => a.Tiles.Any(t => t.Scene is 117 or 124)), "LBA2: Wannies Island's mine is its first three rooms and the entrance; the temple (117) and the box transport building (124) are not part of it");
        Check(statue is not null && statue.Name == "Dark Monk Statue" && statue.Island == 5 && statue.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 185, 186, 187, 188, 192 }) && Lba2Areas.IsStacked(statue), "LBA2: the Dark Monk Statue (Celebration Island) is one map of five scenes, stacked one above another");
        if (statue is not null) { var (s0, s1, s2) = (statue.Tiles.Single(t => t.Scene == 185), statue.Tiles.Single(t => t.Scene == 186), statue.Tiles.Single(t => t.Scene == 187)); Check(s1.OffsetY > s0.OffsetY && s2.OffsetY < s0.OffsetY, "LBA2: on the statue the top scene (186) is above the wizards' scene (185) and the third (187) below it"); }
        if (statue is not null)
        {
            // stacked one above another: where the zones put them in the plan (no sideways move), and in every plan column two scenes' bricks are at least three layers apart
            Check(statue.Tiles.All(t => Lba2Areas.Separations.Where(s => s.Scene == t.Scene).All(s => s.Dx == 0 && s.Dz == 0)), "LBA2: the Dark Monk Statue's scenes are not moved sideways: they stack where their zones put them");
            var columns = statue.Tiles.ToDictionary(t => t.Scene, t => interiors.Placements(t).Select(p => (X: p.X + t.OffsetX / 512, Y: p.Y + t.OffsetY / 256, Z: p.Z + t.OffsetZ / 512)).GroupBy(p => (p.X, p.Z)).ToDictionary(g => g.Key, g => (Min: g.Min(p => p.Y), Max: g.Max(p => p.Y))));
            var tightest = int.MaxValue;
            var sharedColumns = 0;
            foreach (var a in statue.Tiles)
                foreach (var b in statue.Tiles.Where(t => t.Scene > a.Scene))
                    foreach (var (k, ra) in columns[a.Scene])
                        if (columns[b.Scene].TryGetValue(k, out var rb)) { sharedColumns++; tightest = Math.Min(tightest, Math.Max(ra.Min - rb.Max, rb.Min - ra.Max)); }
            Check(sharedColumns > 1000 && tightest >= 3, $"LBA2: the Dark Monk Statue's scenes are stacked with clear space between them: in {sharedColumns} shared plan columns the least gap between two scenes' layers is {tightest} (at least 3)");
            // and each level's picture is clear of the one below it (no screen column where an upper scene's lowest pixel is lower than a lower scene's highest, less a layer's margin)
            var pictures = ScriptRoundTrip.Lba2Silhouettes.Of(interiors, statue, s => { var t = statue.Tiles.Single(t => t.Scene == s); return (t.OffsetX / 512, t.OffsetY / 256, t.OffsetZ / 512); });
            var bottomToTop = new[] { 188, 187, 192, 185, 186 };
            var worst = int.MinValue;
            for (var i = 1; i < bottomToTop.Length; i++)
                for (var j = 0; j < i; j++) worst = Math.Max(worst, ScriptRoundTrip.Lba2Silhouettes.Need(pictures[bottomToTop[i]], pictures[bottomToTop[j]], 15));
            Check(worst <= 0, $"LBA2: the Dark Monk Statue's levels stand clear of each other on the picture: an upper level never reaches the one below it, with a layer to spare (the worst needs {worst} more layers)");
        }
        if (factory is null || tower is null || lower is null || palace is null) return;

        // where the zones put the scenes (before the small moves that keep them apart)
        (int X, int Y, int Z) At(Lba1Area area, int scene)
        {
            var t = area.Tiles.Single(t => t.Scene == scene && t.Window is null);
            var (sx, sy, sz) = Lba2Areas.Separations.Where(s => s.Scene == scene).Select(s => (s.Dx, s.Dy, s.Dz)).FirstOrDefault();
            return (t.OffsetX / 512 - sx, t.OffsetY / 256 - sy, t.OffsetZ / 512 - sz);
        }
        (int, int, int) Between(Lba1Area area, int from, int to) { var (a, b) = (At(area, from), At(area, to)); return (b.X - a.X, b.Y - a.Y, b.Z - a.Z); }
        Check(Between(factory, 140, 141) == (29, -7, 4) && Between(factory, 141, 142) == (32, 14, -14) && Between(factory, 142, 143) == (12, 2, -36) && Between(factory, 141, 145) == (31, -5, 40) && Between(factory, 143, 146) == (30, 8, 45),
            "LBA2: the factory's rooms sit where each first room's cube-change zone puts the next (zone corner minus arrival point)");
        Check(Between(tower, 177, 178) == (-28, -5, -37) && Between(tower, 178, 179) == (34, -5, 27) && Between(lower, 181, 180) == (-13, -14, -1),
            "LBA2: the control tower's scenes sit where the zones put them");
        Check(Enumerable.Range(0, 3).All(r => Enumerable.Range(0, 3).All(c => Between(palace, 151 + 4 * r + c, 152 + 4 * r + c) == (13, 0, 0))) && Enumerable.Range(0, 3).All(r => Enumerable.Range(0, 4).All(c => Between(palace, 151 + 4 * r + c, 155 + 4 * r + c) == (0, 0, -13))) && Between(palace, 163, 80) == (0, 0, -45),
            "LBA2: the palace's rooms are 13 cells apart along the rows and down the columns, as every room's zones say, and the last room is 45 cells past the fourth row");
        // and that the zone back agrees with the zone forward, within a few cells (the arrival point in a doorway is not the spot the other zone stands on)
        foreach (var link in Lba2Areas.Links)
        {
            var forward = Lba2Areas.OffsetOf(link, interiors.LoadScene);
            var back = Lba2Areas.OffsetOf(new Lba2Areas.Link(link.Scene, link.Anchor), interiors.LoadScene);
            if (link == new Lba2Areas.Link(185, 187)) { Check(forward is not null && back is null, "LBA2: the wizards' scene (185) has a one-way zone down to the third scene (187), which has none back"); continue; }
            Check(forward is { } f && back is { } b && Math.Abs(f.X + b.X) <= 2 * 512 && Math.Abs(f.Y + b.Y) <= 3 * 256 && Math.Abs(f.Z + b.Z) <= 3 * 512,
                $"LBA2: scene {link.Scene}'s zone back to {link.Anchor} agrees with the zone forward to within a few cells");
        }

        // the actors' bodies: an actor's entity lists its bodies (FILE3D, entry 44 of RESS.HQR), and each one is a BODY.HQR entry that reads
        Check(interiors.BodyIndex(14, 0) == 25 && interiors.BodyIndex(200, 0) == 289 && interiors.BodyIndex(32, 50) == 53 && interiors.BodyIndex(14, 99) is null && interiors.BodyIndex(-1, 0) is null && interiors.BodyIndex(100000, 0) is null,
            "LBA2: an actor's entity and body number give its BODY.HQR entry (Twinsen 25, the guard 289 ...), and unknown ones give none");
        // the actor window's animation list follows the body: an entity's bodies and animations (RESS.HQR entry 44), and the animations that have as many groups as the body has bones
        var entityTable = LBAAssembler.Lba2EntityTable.Load(Lba2Dir);
        Check(entityTable is not null && entityTable.Entities.Count(e => e.Bodies.Count > 0) > 300 && entityTable.Entities.All(e => e.Bodies.All(b => interiors.BodyIndex(e.Id, b.Generic) == b.Body || e.Bodies.Count(x => x.Generic == b.Generic) > 1)),
            "LBA2: the entity table reads the same bodies as the joined maps' lookup (every entity's every body)");
        if (entityTable is not null)
        {
            var bodyArchive = HqrArchive.Open(Path.Combine(Lba2Dir, "BODY.HQR"));
            var animArchive = HqrArchive.Open(Path.Combine(Lba2Dir, "ANIM.HQR"));
            int? Bones(int body) { try { return bodyArchive.IsValid(body) ? LbaBodyStudio.Body.Read(bodyArchive.Read(body), 2).Bones.Count : null; } catch (InvalidDataException) { return null; } }
            int? Groups(int anim) => animArchive.IsValid(anim) ? BitConverter.ToUInt16(animArchive.Read(anim), 2) : null;
            // entity 217 has five bodies of 22, 28, 30, 17 and 17 bones and animations for each: 311 has 22 (its standing animation is 1440), 312 has 28
            var big = entityTable.ChooseAnimations(312, Bones(312), Groups, 1440);
            var small = entityTable.ChooseAnimations(311, Bones(311), Groups, 1454);
            var same = entityTable.ChooseAnimations(311, Bones(311), Groups, 1441);
            Check(big is not null && big.Natural.All(a => Groups(a) == 28) && big.Natural.Count > 0 && big.Chosen != 1440 && big.Natural.Contains(big.Chosen),
                $"LBA2: picking the 28-bone body of entity 217 gives it its 28-group animations, and an animation for another skeleton (1440) gives way to one of them ({big?.Chosen})");
            Check(small is not null && small.Chosen == 1440 && small.Natural.All(a => Groups(a) == 22) && same is not null && same.Chosen == 1441,
                "LBA2: picking the 22-bone body takes its standing animation (1440) when the animation was for another skeleton, and keeps one of its own (1441)");
            Check(entityTable.ChooseAnimations(-1, null, Groups, 0) is null && entityTable.ChooseAnimations(999999, null, Groups, 0) is null, "LBA2: a body that no entity has leaves the animation list as it was");
            // every body of every entity finds animations whose groups equal its bones, when its entity has any
            var stray = 0; var checkedBodies = 0;
            foreach (var e in entityTable.Entities.Where(e => e.Anims.Count > 0))
                foreach (var b in e.Bodies)
                {
                    if (Bones(b.Body) is not { } bones) continue;
                    var choice = entityTable.ChooseAnimations(b.Body, bones, Groups, -5);
                    if (choice is null) { stray++; continue; }
                    checkedBodies++;
                    if (e.Anims.Any(a => Groups(a.Anim) == bones) && choice.Natural.Any(a => Groups(a) != bones)) stray++;
                    if (!choice.Natural.Contains(choice.Chosen)) stray++;
                }
            Check(checkedBodies > 300 && stray == 0, $"LBA2: for every body of every entity the list is its skeleton's animations and the chosen one is in it ({checkedBodies} bodies, {stray} odd)");
        }
        var bodiless = 0; var bodies = 0;
        foreach (var area in areas)
            foreach (var tile in area.Tiles)
                foreach (var a in interiors.LoadScene(tile.Scene)!.Actors.Skip(1).Where(a => !a.IsSprite && a.Entity >= 0 && a.Body >= 0))
                {
                    if (interiors.BodyIndex(a.Entity, a.Body) is { } index && interiors.ReadBody(index) is { Length: > 100 }) bodies++; else bodiless++;
                }
        Check(bodies > 100 && bodiless * 10 <= bodies, $"LBA2: nearly every actor of the joined maps that has a body number resolves to a readable body ({bodies} do, {bodiless} don't)");

        // never overlap
        // (in the plan view: a brick in the same x, z column at any height; a part of a scene may stand over the rest of that scene: it is the top of its stairs)
        var overlapping = ScriptRoundTrip.Lba2AreaStudy.Overlaps(interiors, areas, false);
        Check(overlapping.Count == 0, $"LBA2: no two scenes of any joined map share a plan column ({overlapping.Count} pairs do" + (overlapping.Count > 0 ? $", e.g. {overlapping[0].A} and {overlapping[0].B}" : "") + ")");
        var tileKeys = areas.SelectMany(a => a.Tiles.Select(t => t.Key)).ToHashSet();
        Check(Lba2Areas.Separations.Select(s => s.Scene).Distinct().Count() == Lba2Areas.Separations.Length && Lba2Areas.Separations.All(s => tileKeys.Contains(s.Scene) && Math.Abs(s.Dx) <= 60 && Math.Abs(s.Dz) <= 60 && (s.Dy == 0 || s.Scene is 185 or 186 or 187 or 192)),
            "LBA2: every separation is for one tile of a map and moves it sideways by at most 60 cells (the Dark Monk Statue's scenes are lifted instead)");
        var stairs = tower.Tiles.Where(t => t.Scene == 177).ToList();
        Check(stairs.Count == 2 && stairs.Single(t => t.Window is not null).Window == new CellWindow(44, 44, 63, 63) && stairs.Single(t => t.Window is null).Without == new CellWindow(44, 44, 63, 63)
            && stairs.Single(t => t.Window is not null).OffsetY - stairs.Single(t => t.Window is null).OffsetY == 17 * 256,
            "LBA2: the small room of the stairs scene (177) is drawn 17 layers up, on top of the stairs it leads up from, and the rest of the scene leaves it out");

        // and they draw, on top of the palette's black: something is there
        foreach (var area in areas)
        {
            var image = interiors.RenderArea(area.Tiles);
            var filled = 0;
            for (var i = 3; i < image.Bgra.Length; i += 4 * 97) if (image.Bgra[i] != 0) filled++;
            Check(image.Width > 500 && image.Height > 300 && filled > 60, $"LBA2: the {area.Name} map draws ({image.Width} x {image.Height})");
        }
    }

    // "Join connected areas" starts on, and what an earlier build saved under the old name says nothing about it.
    private static void Settings()
    {
        Check(new EditorSettings().Lba1JoinConnectedAreas, "settings: joining connected areas is on by default");
        Check(JsonSerializer.Deserialize<EditorSettings>("{}")!.Lba1JoinConnectedAreas, "settings: a settings file without the option leaves it on");
        Check(JsonSerializer.Deserialize<EditorSettings>("{\"Lba1JoinAreas\": false}")!.Lba1JoinConnectedAreas, "settings: the old option (which started off) is ignored, so the first run of a new build has it on");
        Check(!JsonSerializer.Deserialize<EditorSettings>("{\"Lba1JoinConnectedAreas\": false}")!.Lba1JoinConnectedAreas, "settings: turning it off is kept");
        var saved = JsonSerializer.Serialize(new EditorSettings { Lba1JoinConnectedAreas = false });
        Check(saved.Contains("\"Lba1JoinConnectedAreas\":false") && !JsonSerializer.Deserialize<EditorSettings>(saved)!.Lba1JoinConnectedAreas, "settings: the option round-trips through the file");
    }

    private static void Names()
    {
        string C(string s) => SceneMenuNames.Clean(s);
        Check(C("12: Temple of Bú, 1st scene (room #12)") == "Temple of Bú, 1st scene", "names: the number and the room number go, the temple's own comma stays");
        Check(C("Citadel Island, near the tavern") == "Near the tavern", "names: the island goes and the first letter is a capital");
        Check(C("Rebelion Island, Harbor") == "Harbor" && C("Hamalayi Mountains, Entrance to the prison") == "Entrance to the prison", "names: the game's own spellings of the islands go too");
        Check(C("Citadel Island, end sequence (1)") == "End sequence (1)", "names: another island's name goes as well");
        Check(C("Emerald Moon, Outside Baldino's cell(room #15)") == "Outside Baldino's cell", "names: a room number written against the word goes");
        Check(C("Otringal Prison (room #54)") == "Otringal Prison" && C("Some room (cut-out ?)") == "Some room (cut-out ?)", "names: a name that only starts with an island's word (no comma) keeps it");
        Check(C("Wannies Island, the city outside scene") == "The city outside scene", "names: (LBA2) wannies");
        Check(C("Citadel Island") == "Citadel Island", "names: a description that is only an island's name is left as it is");
    }

    // Every description of both games, cleaned: no number, no island name in front, a capital first.
    private static void LbaNames(Lba1Game game)
    {
        var descriptions = game.Scenes.Select(s => game.Description(s.Index)).Where(d => d is not null).Select(d => d!).ToList();
        var lba2Path = Path.Combine(Lba2Dir, "SCENE.HQR");
        if (File.Exists(lba2Path)) descriptions.AddRange(HqdDescriptions.Load("SCENE2.HQD", HqrArchive.CountEntries(lba2Path)).Names.Where(n => n is not null).Select(n => n!));
        var bad = descriptions.Where(d =>
        {
            var c = SceneMenuNames.Clean(d);
            return c.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(c, @"^\d+\s*:") || (char.IsLetter(c[0]) && !char.IsUpper(c[0])) || c.Contains("(room #")
                || SceneMenuNames.IslandNames.Any(n => c.StartsWith(n + ",", StringComparison.OrdinalIgnoreCase) && c.Length > n.Length + 1 && d.Length > c.Length + 1);
        }).ToList();
        Check(bad.Count == 0, $"names: all {descriptions.Count} descriptions of the two games come out without number, island name or lower-case start" + (bad.Count > 0 ? $" (e.g. {bad[0]})" : ""));
    }

    // Where a scene sits in its map as the joins place it: its offset without the small move Lba1Areas.Separations gives it afterwards (which
    // only exists so that no two scenes of a map share a cell). The checks below are about where the zones put scenes.
    private static (int X, int Y, int Z) At(Lba1Area area, int scene)
    {
        var t = area.Tiles.Single(t => t.Scene == scene);
        var (sx, sy, sz) = Lba1Areas.Separations.Where(s => s.Scene == scene).Select(s => (s.Dx, s.Dy, s.Dz)).FirstOrDefault();
        return (t.OffsetX / 512 - sx, t.OffsetY / 256 - sy, t.OffsetZ / 512 - sz);
    }

    private static void Placement(Lba1Game game)
    {
        var principal = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 13));
        var (x37, y37, z37) = At(principal, 37);
        var (x25, y25, z25) = At(principal, 25);
        Check((x37 - x25, y37 - y25, z37 - z25) == (-64, 3, -48), "water tower: it sits directly west of Peg Leg Street (its east edge against the street's west edge), three layers up, path to path");

        // its ochre path (east edge, rows 55..63 on the ground layer) arrives on the street's ochre path (west edge): same world row, same height, dirt bricks
        var (top37, brick37) = game.ColumnTops(37);
        var (top25, brick25) = game.ColumnTops(25);
        var meeting = 0;
        for (var z = 55; z <= 63; z++)
        {
            var i37 = z * 64 + 63;
            var row25 = z + z37 - z25;
            if (row25 < 0 || row25 > 63) continue;
            var i25 = row25 * 64;
            var dirt = brick37[i37] is >= 549 and <= 555 && brick25[i25] is >= 549 and <= 555;
            if (dirt && top37[i37] + y37 == top25[i25] + y25) meeting++;
        }
        Check(meeting >= 8, $"water tower: {meeting} of its 9 path rows meet the street's path at the same height");

        var overlaps = OverlapStudy.Report(game, false, columns: true);
        Check(overlaps.Count == 0, "areas: no two scenes of a joined map share a plan column (a brick in the same x, z at any height)" + (overlaps.Count > 0 ? $" (e.g. {overlaps[0].A} and {overlaps[0].B}: {overlaps[0].Cells} cells)" : ""));
        Check(Lba1Areas.Separations.All(s => Math.Abs(s.Dx) <= 35 && Math.Abs(s.Dy) <= 2 && Math.Abs(s.Dz) <= 25), "areas: a scene is moved at most 35 cells sideways to keep its map free of overlap");
        Check(Lba1Areas.Separations.Select(s => s.Scene).Distinct().Count() == Lba1Areas.Separations.Length && Lba1Areas.Separations.All(s => game.Areas.Any(a => a.Tiles.Any(t => t.Scene == s.Scene))), "areas: every separation is for one scene of a joined map");
        // Tippet Island: the cave village with its bar and cafe (its shop is left out, see ManualLinks), and the three secret passage scenes, each one map
        // Tippet Island: the cave village with its shop, bar and cafe, and the three secret passage scenes, each one map
        var village = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 74));
        Check(village.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 74, 76, 80 }) && village.Name == "Village", "Tippet Island: the village, the bar and the cafe are one map, called Village (the shop, 101, is left out)");
        var (x74, y74, z74) = At(village, 74);
        var (x76, y76, z76) = At(village, 76);
        var (x80, y80, z80) = At(village, 80);
        Check((x76 - x74, y76 - y74, z76 - z74) == (-44, 2, -38) && (x80 - x76, y80 - y76, z80 - z76) == (-15, 4, -15), "Tippet Island: the bar sits where the village's zone puts it, and the cafe where the bar's zone does");
        Check(!game.Areas.Any(a => a.Tiles.Any(t => t.Scene == 101)), "Tippet Island: the shop (101) is not part of any joined map");
        var passage = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 75));
        Check(passage.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 75, 77, 79 }) && passage.Name == "Secret passage", "Tippet Island: the three secret passage scenes are one map, called Secret passage");
        var (x75, y75, z75) = At(passage, 75);
        var (x77, y77, z77) = At(passage, 77);
        var (x79, y79, z79) = At(passage, 79);
        Check((x77 - x75, y77 - y75, z77 - z75) == (-7, -11, 57) && (x79 - x75, y79 - y75, z79 - z75) == (-18, 13, -30), "Tippet Island: the passage's first scene is below the second and the third above it");

        // Citadel Island: the tavern and its cellar, the sewer of the first scene and the secret sewer; each pair one map, placed by its zones
        (int X, int Y, int Z)? ZoneOffset(int from, int to)
        {
            var zone = game.LoadScene(from).Zones.FirstOrDefault(z => z.Type == 0 && z.Info[0] == to);
            return zone is null ? null : ((int)Math.Round((zone.X0 - zone.Info[1]) / 512.0), (int)Math.Round((zone.Y0 - zone.Info[2]) / 256.0), (int)Math.Round((zone.Z0 - zone.Info[3]) / 512.0));
        }
        var tavernMap = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 14));
        var (x14, y14, z14) = At(tavernMap, 14);
        var (x33, y33, z33) = At(tavernMap, 33);
        Check(tavernMap.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 14, 33 }) && tavernMap.Name == "Tavern", "Citadel Island: the tavern and its cellar are one map, called Tavern");
        Check(ZoneOffset(14, 33) is { } down && ZoneOffset(33, 14) is { } up && down == (x33 - x14, y33 - y14, z33 - z14) && (up.X, up.Y, up.Z) == (x14 - x33, y14 - y33, z14 - z33), "Citadel Island: the cellar sits where the tavern's zone puts it, and the cellar's zone back agrees");
        var sewerMap = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 34));
        var (x34, y34, z34) = At(sewerMap, 34);
        var (x55, y55, z55) = At(sewerMap, 55);
        Check(sewerMap.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 34, 55 }) && sewerMap.Name == "Sewer", "Citadel Island: the sewer of the first scene and the secret sewer are one map, called Sewer");
        // (the sewers' two zones differ by one cell in x: the arrival point in a tunnel need not be the spot the other zone stands on)
        Check(ZoneOffset(34, 55) is { } into && ZoneOffset(55, 34) is { } back && into == (x55 - x34, y55 - y34, z55 - z34)
            && Math.Abs(back.X - (x34 - x55)) <= 1 && back.Y == y34 - y55 && Math.Abs(back.Z - (z34 - z55)) <= 1, "Citadel Island: the secret sewer sits where the first sewer's zone puts it, and its zone back agrees to a cell");

        // Brundle Island: the inside of the teleportation building with its secret room and the telepods, one map placed by the zones
        var teleport = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 95));
        Check(teleport.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 95, 98, 99 }) && teleport.Name == "Teleportation", "Brundle Island: the inside of the teleportation, the secret room and the telepods are one map, called Teleportation");
        var (x95, y95, z95) = At(teleport, 95);
        foreach (var other in new[] { 98, 99 })
        {
            var (ox, oy, oz) = At(teleport, other);
            Check(ZoneOffset(95, other) is { } out95 && out95 == (ox - x95, oy - y95, oz - z95) && ZoneOffset(other, 95) is { } in95 && Math.Abs(in95.X - (x95 - ox)) <= 1 && in95.Y == y95 - oy && Math.Abs(in95.Z - (z95 - oz)) <= 1,
                $"Brundle Island: scene {other} sits where the zone in scene 95 puts it, and its zone back agrees to a cell");
        }

        // Fortress Island: the swimming pool with the first secret passage scene and the corridor near Zoe's cell off it
        var pool = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 88));
        Check(pool.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 85, 87, 88 }) && pool.Name == "Underground Fortress passage", "Fortress Island: the secret passage scene 1, the swimming pool and the corridor near Zoe's cell are one map, called Underground Fortress passage");
        var (x88, y88, z88) = At(pool, 88);
        foreach (var other in new[] { 85, 87 })
        {
            var (ox, oy, oz) = At(pool, other);
            Check(ZoneOffset(88, other) is { } fromPool && fromPool == (ox - x88, oy - y88, oz - z88) && ZoneOffset(other, 88) is { } toPool && Math.Abs(toPool.X - (x88 - ox)) <= 1 && toPool.Y == y88 - oy && Math.Abs(toPool.Z - (z88 - oz)) <= 1,
                $"Fortress Island: scene {other} sits where the pool's zone puts it, and its zone back agrees to a cell");
        }

        // Fortress Island: the inside of the fortress with the cloning centre (zones) and the rune stone (placed by the cloning centre's change_cube(90))
        var fortress = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 83));
        Check(fortress.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 83, 89, 90 }) && fortress.Name == "Fortress", "Fortress Island: the inside of the fortress, the cloning centre and the rune stone are one map, called Fortress");
        var (x83, y83, z83) = At(fortress, 83);
        var (x89, y89, z89) = At(fortress, 89);
        var (x90, y90, z90) = At(fortress, 90);
        Check(ZoneOffset(83, 89) is { } toClone && toClone == (x89 - x83, y89 - y83, z89 - z83) && ZoneOffset(89, 83) is { } fromClone && fromClone == (x83 - x89, y83 - y89, z83 - z89), "Fortress Island: the cloning centre sits where the fortress's zone puts it, and its zone back agrees");
        // the rune stone's start point (Twinsen arrives at x 31950, z 28779) lands inside the cloning centre's scenaric zone 1 that changes to it
        var cloning = game.LoadScene(89);
        var trigger = cloning.Zones.Single(z => z.Type == 2 && z.Info[0] == 1);
        var (startX, startY, startZ) = ((x90 - x89) * 512 + 31950, (y90 - y89) * 256 + 1536, (z90 - z89) * 512 + 28779);
        Check(startX >= trigger.X0 && startX <= trigger.X1 && startY >= trigger.Y0 && startY <= trigger.Y1 && startZ >= trigger.Z0 && startZ <= trigger.Z1,"Fortress Island: the rune stone is placed with its start point inside the zone of the cloning centre that leads to it");

        // Principal Island: the harbour (11) meets the fortress's outside (12) at a grid edge, but is not part of that map (nor of any)
        Check(!game.Areas.Any(a => a.Tiles.Any(t => t.Scene == 11)) && game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 12)).Tiles.All(t => t.Scene != 11), "Principal Island: the harbour (scene 11) is not joined to the outside scenes");

        // the two end sequence scenes are Citadel Island's (their data says Polar Island), one map called End Credits
        var credits = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 116));
        Check(credits.Name == "End Credits" && credits.Island == 0 && credits.Tiles.Select(t => t.Scene).Order().SequenceEqual(new[] { 116, 117 }), "end sequence: scenes 116 and 117 are one map, End Credits, under Citadel Island");
        Check(game.Scenes.Where(s => s.Index is 116 or 117).All(s => s.Island == 0) && game.Scenes.Where(s => s.Island == 10).All(s => s.Index is not (116 or 117)) && game.LoadScene(116).Island == 10, "end sequence: the scene list files them under Citadel Island, Polar Island has neither (the game data still says Polar)");
        Check(game.Areas.All(a => a.Label == a.Name && !a.Label.Contains('(')), "areas: a joined map is listed by its name, with no scene numbers in brackets");

        // Hamalayi Mountains (1): the prison chain hangs off the outside scene, and the transporter off its outside
        var hamalayi = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 9));
        Check(new[] { 64, 66, 70, 71, 82 }.All(s => hamalayi.Tiles.Any(t => t.Scene == s)), "prison: the outside of the prison, its entrance hall, the prison, its backdoor and the transporter's inside are all in the Hamalayi outside map");
        var (x71, y71, z71) = At(hamalayi, 71);
        var (x70, y70, z70) = At(hamalayi, 70);
        var (x64, y64, z64) = At(hamalayi, 64);
        var (x82, y82, z82) = At(hamalayi, 82);
        var (x65, y65, z65) = At(hamalayi, 65);
        var (x66, y66, z66) = At(hamalayi, 66);
        Check((x70 - x71, y70 - y71, z70 - z71) == (5, 0, -21), "prison: the entrance hall is where the gate's zone puts it, north of the outside scene");
        Check((x64 - x70, y64 - y70, z64 - z70) == (-24, 8, -16), "prison: the prison is up the stairs from the entrance hall, as the zones say");
        Check((x82 - x64, y82 - y64, z82 - z64) == (-35, 14, -61), "prison: the backdoor's path comes down to the prison's north end");
        Check((x66 - x65, y66 - y65, z66 - z65) == (-19, -3, -58), "transporter: its inside is the building at the top of the outside scene's snowfield");

        // the areas' names and each island's main map
        Check(Lba1Areas.MainArea(game.Areas.Where(a => a.Island == 2))!.Tiles.Any(t => t.Scene == 39), "main map: the desert's is its outside (military camp), not the temple");
        Check(Lba1Areas.MainArea(game.Areas.Where(a => a.Island == 1))!.Tiles.Count == principal.Tiles.Count, "main map: Principal Island's is Lupin Burg's");
        Check(game.Areas.Where(a => a.Island == 10).All(a => Lba1Areas.MainArea(new[] { a }) == a), "main map: an island with one map has that one");
        foreach (var area in game.Areas)
        {
            var central = Lba1Areas.CentralScene(area);
            Check(area.Tiles.Any(t => t.Scene == central), $"central scene: {central} is a scene of {area.Name} (island {area.Island})");
        }
        Check(Lba1Areas.CentralScene(game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 2))) is 2 or 3, "central scene: Citadel Island's middle scene is at the tavern or the pharmacy, not the harbour or the prison");
    }

    // The scene a view is looking at.
    private static void Focus(Lba1Game game)
    {
        var area = game.Areas.Single(a => a.Tiles.Any(t => t.Scene == 13));
        var image = game.RenderArea(area);
        (double X, double Y) Project(double x, double y, double z) { var p = image.Project(x, y, z); return (p.X, p.Y); }
        int[] TopOf(int scene) => game.ColumnTops(scene).TopY;
        var tilesById = area.Tiles.ToDictionary(t => t.Scene);

        // zoomed right in on the middle of a scene's ground: that scene
        foreach (var scene in new[] { 12, 13, 19, 25, 37, 24, 17 })
        {
            var tile = tilesById[scene];
            var (top, _) = game.ColumnTops(scene);
            var columns = Enumerable.Range(0, 64 * 64).Where(i => top[i] >= 0).ToList();
            var mx = (int)columns.Average(i => i % 64);
            var mz = (int)columns.Average(i => i / 64);
            var i0 = columns.OrderBy(i => Math.Abs(i % 64 - mx) + Math.Abs(i / 64 - mz)).First();
            var (cx, cy) = Project(tile.OffsetX + (i0 % 64 + .5) * 512, tile.OffsetY + (top[i0] + 1) * 256, tile.OffsetZ + (i0 / 64 + .5) * 512);
            Check(Lba1Areas.FocusScene(area.Tiles, TopOf, Project, cx, cy, 120, 80) == scene, $"focus: zoomed in on the middle of scene {scene}, that scene is the one");
        }

        // zoomed right out (the whole map in view, centred): some scene of the map, and the same one every time (equally central: a fixed pick)
        var half = (image.Width / 4.0, image.Height / 4.0);
        var wide = Lba1Areas.FocusScene(area.Tiles, TopOf, Project, image.Width / 2.0, image.Height / 2.0, half.Item1, half.Item2);
        Check(area.Tiles.Any(t => t.Scene == wide) && wide == Lba1Areas.FocusScene(area.Tiles, TopOf, Project, image.Width / 2.0, image.Height / 2.0, half.Item1, half.Item2), $"focus: zoomed out, the scene in the middle of the picture ({wide}) is a scene of the map, picked the same way each time");

        // a view that shows none of the map: the scene whose middle is nearest
        var far = Lba1Areas.FocusScene(area.Tiles, TopOf, Project, -5000, image.Height / 2.0, 100, 100);
        Check(area.Tiles.Any(t => t.Scene == far), $"focus: with nothing of the map in view the nearest scene ({far}) is chosen");

        // two scenes the same distance out: the lower-numbered one
        var twin = new[] { new Lba1AreaTile(25, 0, 0, 0), new Lba1AreaTile(12, 0, 0, 0) };
        Check(Lba1Areas.FocusScene(twin, s => Enumerable.Repeat(s == 12 || s == 25 ? 0 : -1, 64 * 64).ToArray(), (x, y, z) => (x / 24, y / 24 + z / 48), 0, 0, 100000, 100000) == 12, "focus: two scenes with equally much in the middle: one of them is picked (the lower-numbered)");
    }
}
