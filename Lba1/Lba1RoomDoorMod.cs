using System.IO;
using LBA2LevelEditor.Scenes;

namespace LBA2LevelEditor.Lba1;

// Connects LBA1's unused bedroom (scene 61, "Some room (cut-out ?)") to the bricked-up arch on the east side of a
// Lupin Burg house (scene 13), the way the game connects its other houses (the Rabbibunny house, scene 28, is the
// model; everything here is that house's entrance moved to the new arch):
//   * the arch: the bricked panel is replaced by an open arch with its recess, copied cell for cell from the
//     Rabbibunny doorway of the same grid (cells x 33..37, z 52..56: the pillars, the floor and the invisible
//     side walls too), and the recess is lined with grey stone (block 2, the cobbles of the house walls) behind
//     and on its north side, so that nothing black shows when the door slides open;
//   * the door: the standard east-facing sliding door (ActorPrefabs.DoorEast: sprite 11, with the blue upper part) in
//     the arch cell. It slides open when Twinsen bumps into it and closes when he is far away. The engine draws a
//     door cut to a clip rectangle, and the retail rectangle stops 8 pixels below the door's base point: on the
//     Rabbibunny house the raised ground hides that, but with a level floor the door's lower left end (the door
//     runs down and to the left, along z) was cut off and floated above the floor. This door's rectangle is taller;
//   * the arch's legs: the doorway copy would bring the Rabbibunny house's pillars, whose lower parts are stone or missing (that house
//     stands on raised ground); this wall's legs stay the smooth arch stones from the floor up (blocks 150 and 149) it had, and
//     the street in front is left as the game's own ochre dirt;
//   * the zones: a cube-change zone in the recess leads into room 61 at its doorway, and a zone in the room's
//     doorway leads back out into the arch. The engine fires a cube-change zone while the hero stands in it, and
//     the hero arrives at the destination corner plus his offset inside the zone;
//   * the lamp post at the map's west corner and the key zone around it (Lba1LampPost): its cells are part of the grid edit and its zone
//     is added with the others.
// Everything is one SceneStore transaction (grid 13, scenes 13 and 61) and one step on SceneHistory's undo log.
internal static class Lba1RoomDoorMod
{
    private const int OutsideScene = 13, RoomScene = 61;

    // The bricked arch: x = 51, z = 12..14 (pillars at 11 and 15); the reference doorway: x = 33..37, z = 53..55.
    private const int ArchX = 51, ArchZ = 12, ReferenceX = 33, ReferenceZ = 53, RecessDepth = 5, ArchWidth = 3;

    // Grey stone lining the recess: block 2 (1 x 2 x 1 cobbles), layers 1..6, behind it (x = 46) and along its north side (z = 11).
    private const int LiningBlock = 2, LiningTop = 6;

    // The arch's legs (north z = 11, south z = 15) and how far the street's dirt reaches in front of it (x = 52..55, up to its curb at 56).
    private const int NorthLegBlock = 150, SouthLegBlock = 149, StreetDepth = 4;

    // The door sprite is 140 pixels tall and reaches 36 below its base point; the retail clip rectangle stops at 8.
    private const int ClipBottom = 36;
    private const int WalledBlock = 155, ArchTopBlock = 151, DoorPlateBlock = 236;

    // The door stands on the arch's cell, at its first z cell (world coordinates: a cell is 512 wide, centred on
    // cell * 512, so its first z edge is cell * 512 - 256).
    private const int DoorX = ArchX * 512, DoorY = 256, DoorZ = ArchZ * 512 - 256;

    // The Rabbibunny house's pair of zones (scene 13 -> 28 in the recess, scene 28 -> 13 in its doorway), moved: the
    // step into the recess leads to the room's doorway, and the zone in the room's doorway leads back out into the arch
    // (destination = where the zone's min corner lands; the room's doorway is 4 cells nearer to the origin than 28's).
    private static SceneZoneModel ZoneIn() => new() { X0 = 25344, Y0 = 256, Z0 = 5376, X1 = 25855, Y1 = 1535, Z1 = 7935, Type = 0, Info = new[] { RoomScene, 31488, 768, 26880 } };
    private static SceneZoneModel ZoneOut() => new() { X0 = 32000, Y0 = 768, Z0 = 27392, X1 = 32511, Y1 = 2047, Z1 = 28927, Type = 0, Info = new[] { OutsideScene, 25856, 256, 5888 } };

    public sealed record Result(bool Changed, string Message);

    public const string HistoryName = "Connect the bedroom (scene 61) to Lupin Burg";

    public static string Describe =>
        "Replaces the bricked-up arch in Lupin Burg (scene 13, east face of the house at x 51, z 11-15) with an open\n" +
        "arch and recess like the Rabbibunny house's, adds the standard sliding door there (a sprite actor with its\n" +
        "open/close scripts, the same as that house's door), adds a cube-change zone in the recess into scene 61 (the\n" +
        "bedroom), and a zone in the bedroom's doorway that leads back out. Writes LBA_GRI.HQR (grid 13) and\n" +
        "SCENE.HQR (scenes 13 and 61); the first change to each keeps a .bak copy, and Edit > Undo takes it back.";

    // True if the grid still has the bricked arch (block 155) at the door.
    public static bool ArchIsBricked(byte[] grid) => Lba1GridEdit.Get(grid, ArchX, 4, ArchZ + 1).Block == WalledBlock;

    // True if the arch has been opened the way this edit does it (arch top over an empty doorway).
    public static bool ArchIsOpen(byte[] grid) =>
        Lba1GridEdit.Get(grid, ArchX, 8, ArchZ + 1).Block == ArchTopBlock && Lba1GridEdit.Get(grid, ArchX, 3, ArchZ + 1).Block == 0;

    public static List<Lba1GridCell> ArchEdits(byte[] grid)
    {
        var cells = Lba1GridCodec.Decode(grid);
        var edits = new Dictionary<(int X, int Y, int Z), Lba1GridCell>();   // (the lining replaces what the doorway copy put in the same cell)
        void Set(Lba1GridCell cell) => edits[(cell.X, cell.Y, cell.Z)] = cell;
        // the doorway with the pillar rows either side of it (z one before the arch to one after it)
        for (var dx = 0; dx < RecessDepth; dx++)
            for (var dz = -1; dz <= ArchWidth; dz++)
                for (var y = 0; y < 10; y++)
                {
                    var i = (((ReferenceZ + dz) * 64 + ReferenceX + dx) * 25 + y) * 2;
                    Set(new Lba1GridCell(ArchX - (RecessDepth - 1) + dx, y, ArchZ + dz, cells[i], cells[i + 1]));
                }
        // the floor row under the far pillar: bare in the reference (its raised ground hides that), a black hole beside this level street
        for (var dx = 0; dx < RecessDepth; dx++)
        {
            var i = (((ReferenceZ + ArchWidth - 1) * 64 + ReferenceX + dx) * 25) * 2;
            Set(new Lba1GridCell(ArchX - (RecessDepth - 1) + dx, 0, ArchZ + ArchWidth, cells[i], cells[i + 1]));
        }
        // the grey lining: the block's two layers alternate (position 0, 1)
        var back = ArchX - RecessDepth;
        for (var y = 1; y <= LiningTop; y++)
        {
            for (var z = ArchZ - 1; z <= ArchZ + ArchWidth; z++) Set(new Lba1GridCell(back, y, z, LiningBlock, (y - 1) % 2));
            for (var x = back + 1; x < ArchX; x++) Set(new Lba1GridCell(x, y, ArchZ - 1, LiningBlock, (y - 1) % 2));
        }
        // the arch's own legs: the copy brought the Rabbibunny doorway's pillars, whose lower parts are stone (north) or missing (south, they
        // stand on that house's raised ground). This wall's legs are the smooth arch stones from the floor up (blocks 150 and 149, as at the
        // game's other entrances, e.g. house 58), and are what the bricked-up arch had; keep them
        for (var y = 1; y <= 9; y++)
        {
            Set(new Lba1GridCell(ArchX, y, ArchZ - 1, NorthLegBlock, y - 1));
            Set(new Lba1GridCell(ArchX, y, ArchZ + ArchWidth, SouthLegBlock, y - 1));
        }
        // the street in front of the door is the game's own ochre dirt (blocks 18 and 19); an earlier version of this edit paved it
        for (var x = ArchX + 1; x <= ArchX + StreetDepth; x++)
            for (var z = ArchZ - 1; z <= ArchZ + ArchWidth; z++)
            {
                var (block, pos) = StreetCell(x, z);
                Set(new Lba1GridCell(x, 0, z, block, pos));
            }
        // the lamp post at the map's west corner (Lba1LampPost)
        foreach (var cell in Lba1LampPost.Cells()) Set(cell);
        return edits.Values.ToList();
    }

    // The street's dirt at x = 52..55, z = 11..15 as the game has it (LBA_GRI entry 13 before any edit).
    private static (int Block, int Pos) StreetCell(int x, int z) => (x, z) switch
    {
        (52, 11) => (19, 0), (53, 11) => (19, 0), (54, 11) => (19, 1), (55, 11) => (19, 2),
        (52, 12) => (19, 3), (53, 12) => (19, 3), (54, 12) => (19, 4), (55, 12) => (19, 5),
        (52, 13) => (19, 0), (53, 13) => (18, 6), (54, 13) => (18, 7), (55, 13) => (18, 8),
        (52, 14) => (19, 3), (53, 14) => (19, 0), (54, 14) => (19, 1), (55, 14) => (19, 2),
        (52, 15) => (18, 6), (53, 15) => (19, 3), (54, 15) => (19, 4), (55, 15) => (19, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(x), $"no street cell at {x}, {z}"),
    };

    // True if every cell the arch edit sets already holds what it would set.
    public static bool ArchIsComplete(byte[] grid)
    {
        var cells = Lba1GridCodec.Decode(grid);
        return ArchEdits(grid).All(e =>
        {
            var i = ((e.Z * 64 + e.X) * 25 + e.Y) * 2;
            return cells[i] == e.Block && cells[i + 1] == e.Pos;
        });
    }

    // The door's clip rectangle: the retail one for that spot, made taller so the whole door is drawn.
    private static int[] DoorClip(int x, int y, int z)
    {
        var retail = ActorPrefabs.DoorEast.Build(0, x, y, z).Info;
        return new[] { retail[0], retail[1], retail[2], retail[3] - 8 + ClipBottom };
    }

    // `roomEdit` may change the bedroom's scene (61) further before it is saved (returns true when it did) and
    // `extraEdits`/`extraTexts` are written in the same transaction: Lba1SurpriseChanges adds its elf's body (and
    // its BODY.HQD description) this way, so that everything is one all-or-nothing save and one undo step.
    // `historyName` names that step. Without them this is the door alone.
    public static Result Apply(string directory, Func<SceneModel, bool>? roomEdit = null, IReadOnlyList<HqrEntryStore.Edit>? extraEdits = null,
        IReadOnlyList<HqrEntryStore.TextEdit>? extraTexts = null, string? historyName = null, IReadOnlyList<SceneChange>? extraScenes = null)
    {
        var store = new SceneStore(SceneGame.Lba1, directory);
        var extras = extraEdits ?? Array.Empty<HqrEntryStore.Edit>();
        var extraTextEdits = extraTexts ?? Array.Empty<HqrEntryStore.TextEdit>();
        var moreScenes = extraScenes ?? Array.Empty<SceneChange>();

        // ---- grid 13 ----
        var grid = store.LoadGrid(OutsideScene);
        if (!Lba1GridEdit.UsedBlocksListed(grid))
            throw new InvalidDataException("Grid 13 has an earlier edit that left its used-blocks table wrong (the game shows a broken, slow scene). Restore LBA_GRI.HQR and SCENE.HQR from the .bak copies and run this again.");
        if (Lba1GridEdit.Get(grid, ArchX, 3, ArchZ + 1).Block == DoorPlateBlock)
            throw new InvalidDataException("Grid 13 has an earlier version of this edit (a flat wooden door plate instead of the sliding door). Restore LBA_GRI.HQR and SCENE.HQR from the .bak copies and run this again.");
        byte[]? newGrid = null;
        var wasBricked = ArchIsBricked(grid);
        if (!ArchIsBricked(grid) && !ArchIsOpen(grid)) throw new InvalidDataException("The house's east wall isn't what this edit expects (neither the bricked arch nor the opened one); not touching it.");
        if (!ArchIsComplete(grid)) newGrid = Lba1GridEdit.SetCells(grid, ArchEdits(grid));   // (also completes an arch an earlier version of this edit opened)

        // ---- scenes 13 and 61 ----
        var outside = store.Load(OutsideScene);
        var room = store.Load(RoomScene);
        // the bedroom's floor has a hole (Lba1SecretRoomExtras)
        var roomGrid = store.LoadGrid(RoomScene);
        byte[]? newRoomGrid = Lba1SecretRoomExtras.FloorHasHole(roomGrid) ? Lba1SecretRoomExtras.FillFloorHole(roomGrid) : null;
        var outsideChanged = false;
        bool zoneAdded = false, doorAdded = false, clipFixed = false, lockAdded = false;
        int doorIndex;
        if (!outside.Zones.Any(z => z.Type == 0 && z.Info[0] == RoomScene)) { outside.Zones.Add(ZoneIn()); outsideChanged = zoneAdded = true; }
        var clip = DoorClip(DoorX, DoorY, DoorZ);
        if (outside.Actors.FirstOrDefault(a => a.IsSprite && a.Sprite == 11 && a.X == DoorX && a.Z == DoorZ) is { } door)
        {
            if (!door.Info.SequenceEqual(clip)) { door.Info = clip; outsideChanged = clipFixed = true; }
            doorIndex = outside.Actors.IndexOf(door);
        }
        else
        {
            var index = ActorPrefabs.Place(outside, ActorPrefabs.DoorEast, DoorX, DoorY, DoorZ);
            outside.Actors[index].Info = clip;
            doorIndex = index;
            outsideChanged = doorAdded = true;
        }
        var lampZoneAdded = false;
        if (!Lba1LampPost.ZoneIsThere(outside))
        {
            // (an earlier version put the key zone somewhere else: the game itself has no zone that gives only a key, so any is that one)
            outside.Zones.RemoveAll(z => z.Type == 4 && (z.Info[1] == Lba1LampPost.LittleKeyBonus || z.Info[0] == Lba1LampPost.LittleKeyBonus));   // (the first versions wrote the bonus bits into the zone's number word, where the game never looks)
            outside.Zones.Add(Lba1LampPost.Zone());
            outsideChanged = lampZoneAdded = true;
        }
        // the door needs a little key the first time (Lba1DoorLock); this swaps the scene for its reparsed copy, so it comes after the other scene 13 edits
        (outside, lockAdded) = Lba1DoorLock.Apply(outside, OutsideScene, doorIndex);
        if (lockAdded) outsideChanged = true;
        var roomChanged = false;
        var roomZoneAdded = false;
        if (!room.Zones.Any(z => z.Type == 0 && z.Info[0] == OutsideScene)) { room.Zones.Add(ZoneOut()); roomChanged = roomZoneAdded = true; }

        if (roomEdit?.Invoke(room) == true) roomChanged = true;

        if (newGrid is null && newRoomGrid is null && !outsideChanged && !roomChanged && extras.Count == 0 && extraTextEdits.Count == 0 && moreScenes.Count == 0) return new Result(false, "Scene 61 is already connected; nothing to change.");

        var changes = new List<SceneChange>();
        if (outsideChanged || newGrid is not null) changes.Add(new SceneChange(OutsideScene, outside, newGrid));
        if (roomChanged || newRoomGrid is not null) changes.Add(new SceneChange(RoomScene, room, newRoomGrid));
        changes.AddRange(moreScenes);
        if (changes.Count > 0) store.SaveMany(changes, description: historyName ?? HistoryName, extraEdits: extras, extraTexts: extraTextEdits);
        else HqrEntryStore.Save(SceneGame.Lba1, directory, historyName ?? HistoryName, extras, extraTextEdits);

        var messages = new List<string>();
        if (newGrid is not null) messages.Add(wasBricked ? "grid 13: bricked arch replaced by an open arch (smooth legs to the floor) and a stone-lined recess (LBA_GRI.HQR)" : "grid 13: the arch completed (smooth legs to the floor, doorway floor, grey stone lining, dirt street) (LBA_GRI.HQR)");
        if (doorAdded || zoneAdded || roomZoneAdded) messages.Add("scene 13: sliding door actor and the zone into scene 61; scene 61: the zone back out (SCENE.HQR)");
        if (lampZoneAdded) messages.Add("scene 13: the lamp post's key zone (SCENE.HQR)");
        if (newRoomGrid is not null) messages.Add("grid 61: the hole in the bedroom's floor filled (LBA_GRI.HQR)");
        if (lockAdded) messages.Add($"scene 13: the door needs a little key the first time (game flag {Lba1DoorLock.Flag}, \"{Lba1DoorLock.FlagName}\") (SCENE.HQR)");
        if (clipFixed) messages.Add("scene 13: the door's clip rectangle made taller so the door reaches the floor (SCENE.HQR)");
        var standalone = roomEdit is null && extras.Count == 0 && extraTextEdits.Count == 0 && moreScenes.Count == 0;
        return new Result(true, messages.Count > 0 ? string.Join("; ", messages) + (standalone ? ". Originals kept as .bak." : ".") : "");
    }
}
