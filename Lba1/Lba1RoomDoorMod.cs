using System.IO;
using LBA2LevelEditor.Scenes;

namespace LBA2LevelEditor.Lba1;

// Connects LBA1's unused bedroom (scene 61, "Some room (cut-out ?)") to the bricked-up arch on the east side of a
// Lupin Burg house (scene 13), the way the game connects its other houses (the Rabbibunny house, scene 28, is the
// model; everything here is that house's entrance moved to the new arch):
//   * the arch: the bricked panel is replaced by an open arch with its recess floor, copied cell for cell from
//     the Rabbibunny doorway of the same grid (cells x 33..37, z 53..55);
//   * the door: the standard east-facing sliding door (ActorPrefabs.DoorEast: sprite 11, with the blue upper part) in
//     the arch cell. It slides open when Twinsen bumps into it and closes when he is far away;
//   * the zones: a cube-change zone in the recess leads into room 61 at its doorway, and a zone in the room's
//     doorway leads back out into the arch. The engine fires a cube-change zone while the hero stands in it, and
//     the hero arrives at the destination corner plus his offset inside the zone.
// Everything is one SceneStore transaction (grid 13, scenes 13 and 61) and one step on SceneHistory's undo log.
internal static class Lba1RoomDoorMod
{
    private const int OutsideScene = 13, RoomScene = 61;

    // The bricked arch: x = 51, z = 12..14 (pillars at 11 and 15); the reference doorway: x = 33..37, z = 53..55.
    private const int ArchX = 51, ArchZ = 12, ReferenceX = 33, ReferenceZ = 53, RecessDepth = 5, ArchWidth = 3;
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
        var edits = new List<Lba1GridCell>();
        for (var dx = 0; dx < RecessDepth; dx++)
            for (var dz = 0; dz < ArchWidth; dz++)
                for (var y = 0; y < 10; y++)
                {
                    var i = (((ReferenceZ + dz) * 64 + ReferenceX + dx) * 25 + y) * 2;
                    edits.Add(new Lba1GridCell(ArchX - (RecessDepth - 1) + dx, y, ArchZ + dz, cells[i], cells[i + 1]));
                }
        return edits;
    }

    // `roomEdit` may change the bedroom's scene (61) further before it is saved (returns true when it did) and
    // `extraFiles` are written in the same transaction: Lba1SurpriseChanges adds its elf this way, so that everything
    // is one all-or-nothing save and one undo step. `historyName` names that step. Without them this is the door alone.
    public static Result Apply(string directory, Func<SceneModel, bool>? roomEdit = null, IReadOnlyList<ExtraFile>? extraFiles = null, string? historyName = null)
    {
        var store = new SceneStore(SceneGame.Lba1, directory);
        var extras = extraFiles ?? Array.Empty<ExtraFile>();

        // ---- grid 13 ----
        var grid = store.LoadGrid(OutsideScene);
        if (!Lba1GridEdit.UsedBlocksListed(grid))
            throw new InvalidDataException("Grid 13 has an earlier edit that left its used-blocks table wrong (the game shows a broken, slow scene). Restore LBA_GRI.HQR and SCENE.HQR from the .bak copies and run this again.");
        if (Lba1GridEdit.Get(grid, ArchX, 3, ArchZ + 1).Block == DoorPlateBlock)
            throw new InvalidDataException("Grid 13 has an earlier version of this edit (a flat wooden door plate instead of the sliding door). Restore LBA_GRI.HQR and SCENE.HQR from the .bak copies and run this again.");
        byte[]? newGrid = null;
        if (ArchIsBricked(grid)) newGrid = Lba1GridEdit.SetCells(grid, ArchEdits(grid));
        else if (!ArchIsOpen(grid)) throw new InvalidDataException("The house's east wall isn't what this edit expects (neither the bricked arch nor the opened one); not touching it.");

        // ---- scenes 13 and 61 ----
        var outside = store.Load(OutsideScene);
        var room = store.Load(RoomScene);
        var outsideChanged = false;
        if (!outside.Zones.Any(z => z.Type == 0 && z.Info[0] == RoomScene)) { outside.Zones.Add(ZoneIn()); outsideChanged = true; }
        if (!outside.Actors.Any(a => a.IsSprite && a.Sprite == 11 && a.X == DoorX && a.Z == DoorZ))
        {
            ActorPrefabs.Place(outside, ActorPrefabs.DoorEast, DoorX, DoorY, DoorZ);
            outsideChanged = true;
        }
        var roomChanged = false;
        if (!room.Zones.Any(z => z.Type == 0 && z.Info[0] == OutsideScene)) { room.Zones.Add(ZoneOut()); roomChanged = true; }

        var doorChanged = newGrid is not null || outsideChanged || roomChanged;
        var doorScenesChanged = outsideChanged || roomChanged;
        if (roomEdit?.Invoke(room) == true) roomChanged = true;

        if (newGrid is null && !outsideChanged && !roomChanged && extras.Count == 0) return new Result(false, "Scene 61 is already connected; nothing to change.");

        var changes = new List<SceneChange>();
        if (outsideChanged || newGrid is not null) changes.Add(new SceneChange(OutsideScene, outside, newGrid));
        if (roomChanged) changes.Add(new SceneChange(RoomScene, room));
        if (changes.Count > 0) store.SaveMany(changes, description: historyName ?? HistoryName, extraFiles: extras);
        else
        {
            var transaction = new FileTransaction();
            foreach (var extra in extras) transaction.Write(extra.Path, extra.Content, extra.Verify);
            transaction.Commit();
        }

        var messages = new List<string>();
        if (newGrid is not null) messages.Add("grid 13: bricked arch replaced by an open arch and recess (LBA_GRI.HQR)");
        if (doorScenesChanged) messages.Add("scene 13: sliding door actor and the zone into scene 61; scene 61: the zone back out (SCENE.HQR)");
        var standalone = roomEdit is null && extras.Count == 0;
        return new Result(true, doorChanged ? string.Join("; ", messages) + (standalone ? ". Originals kept as .bak." : ".") : "");
    }
}
