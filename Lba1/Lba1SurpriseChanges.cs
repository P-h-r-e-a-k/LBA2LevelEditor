namespace LBA2LevelEditor.Lba1;

// Tools > LBA1: Make surprise changes. Everything the editor adds to the LBA1 game files on its own, as one save:
//   * the bedroom (scene 61) connected to Lupin Burg (Lba1RoomDoorMod: the arch, the sliding door, the two zones);
//   * a pink elf in that bedroom (Lba1PinkElf: a recoloured Raymond in BODY.HQR / FILE3D.HQR, and its actor).
// LBA_GRI.HQR, SCENE.HQR, BODY.HQR and FILE3D.HQR are written in one FileTransaction (each keeps a one-time .bak),
// nothing is written when any part is refused, and running it again changes nothing. Edit > Undo puts the scenes and
// the grid back in one step; the pink body stays in BODY.HQR then (unused, harmless).
internal static class Lba1SurpriseChanges
{
    public const string Title = "Make surprise changes";

    public const string HistoryName = "Make surprise changes (bedroom door and pink elf)";

    public static string Describe =>
        "Makes LBA1's unused bedroom (scene 61, \"Some room (cut-out ?)\") reachable, and gives it a visitor.\n\n" +
        "The door: replaces the bricked-up arch in Lupin Burg (scene 13, east face of the house at x 51, z 11-15) with an open " +
        "arch and recess like the Rabbibunny house's, adds the standard sliding door there, a cube-change zone in the recess " +
        "into scene 61, and a zone in the bedroom's doorway that leads back out.\n\n" +
        "The elf: a pink copy of Raymond the Elf (his blue hat, tunic and cuffs recoloured; face, hands and feet unchanged) " +
        "is added to BODY.HQR and to the Elf entity in FILE3D.HQR as body 42, so it shares the elves' animations. Scene 61 " +
        "gets it as an actor between the beds; it turns to face Twinsen when he comes near, and its text would be pink.\n\n" +
        "Writes LBA_GRI.HQR (grid 13), SCENE.HQR (scenes 13 and 61), BODY.HQR and FILE3D.HQR in one go; the first change to " +
        "each keeps a .bak copy. Edit > Undo takes back the scenes and the grid (the new body stays in BODY.HQR, unused).";

    public static Lba1RoomDoorMod.Result Apply(string directory)
    {
        // plan the files first: nothing is written until the door, the scenes and these all check out
        var files = Lba1PinkElf.PlanFiles(directory);
        string? elfMessage = null;

        var door = Lba1RoomDoorMod.Apply(directory,
            roomEdit: room => (elfMessage = Lba1PinkElf.EditRoom(room)) is not null,
            extraFiles: files.Files,
            historyName: HistoryName);

        var lines = new List<string>();
        if (door.Changed && door.Message.Length > 0) lines.Add(door.Message);
        if (files.Changed) lines.Add($"BODY.HQR: the pink elf's body added as entry {files.BodyIndex}; FILE3D.HQR: the Elf entity (49) got it as body {Lba1PinkElf.BodyId}.");
        if (elfMessage is not null) lines.Add(elfMessage + ".");
        if (lines.Count == 0) return new Lba1RoomDoorMod.Result(false, "The bedroom is already connected and the pink elf is already there; nothing to change.");
        lines.Add("Originals are kept as .bak copies.");
        return new Lba1RoomDoorMod.Result(true, string.Join("\n", lines));
    }
}
