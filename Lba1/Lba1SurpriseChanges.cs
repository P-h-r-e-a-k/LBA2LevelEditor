using LBAAssembler.Scenes;

namespace LBAAssembler.Lba1;

// Tools > LBA1: Make surprise changes. Everything the editor adds to the LBA1 game files on its own, as one save:
//   * the bedroom (scene 61) connected to Lupin Burg (Lba1RoomDoorMod: the arch, the sliding door, the two zones);
//   * a pink elf in that bedroom (Lba1PinkElf: a recoloured Raymond in BODY.HQR / FILE3D.HQR, and its actor, who greets Twinsen);
//   * the three fishermen's boat trips opened up for chapter 6 (Lba1Fishermen: scenes 24, 39 and 42).
//   * a lamp post at the west corner of Lupin Burg with a key bonus zone (Lba1LampPost);
//   * a lock on the bedroom door (Lba1DoorLock) and, in the bedroom, a meca penguin and twelve mushrooms that give rewards (Lba1SecretRoomExtras);
// LBA_GRI.HQR, SCENE.HQR, TEXT.HQR, BODY.HQR and FILE3D.HQR are written in one FileTransaction (each keeps a one-time .bak),
// nothing is written when any part is refused, and running it again changes nothing. Edit > Undo puts the scenes, the
// grid, and the new BODY.HQR/FILE3D.HQR/TEXT.HQR entries back to what they held before, all in one step (see
// HqrEntryStore): only the entries this save actually touched are kept for that, not a copy of those archives.
internal static class Lba1SurpriseChanges
{
    public const string Title = "Make surprise changes";

    public const string HistoryName = "Make surprise changes";

    // The edits/text edits of both plans, one per (path, entry) or per path (the later plan's own copy of a shared one wins --
    // both plans only ever touch FILE3D.HQR's own entity records at different entity indices and BODY.HQD's own sidecar, so
    // this is mostly for BODY.HQR staying additive and BODY.HQD keeping both plans' lines, not just the later one's).
    private static IReadOnlyList<HqrEntryStore.Edit> MergeFiles(IReadOnlyList<HqrEntryStore.Edit> first, IReadOnlyList<HqrEntryStore.Edit> second)
        => first.Where(f => !second.Any(s => string.Equals(s.RelativePath, f.RelativePath, StringComparison.OrdinalIgnoreCase) && s.Entry == f.Entry)).Concat(second).ToList();

    private static IReadOnlyList<HqrEntryStore.TextEdit> MergeTexts(IReadOnlyList<HqrEntryStore.TextEdit> first, IReadOnlyList<HqrEntryStore.TextEdit> second)
        => first.Where(f => !second.Any(s => string.Equals(s.RelativePath, f.RelativePath, StringComparison.OrdinalIgnoreCase))).Concat(second).ToList();

    public static Lba1RoomDoorMod.Result Apply(string directory)
    {
        // plan the files first: nothing is written until the door, the scenes and these all check out
        var files = Lba1PinkElf.PlanFiles(directory);
        var fishermen = Lba1Fishermen.PlanFor(new SceneStore(SceneGame.Lba1, directory));
        var text = Lba1DialogueText.Plan(directory, new[] { Lba1Fishermen.Question, Lba1PinkElf.Greeting });
        var mushroom = Lba1SecretRoomExtras.PlanFiles(directory, files.Files, files.Texts);      // (builds on the pink elf's BODY.HQR, FILE3D.HQR and BODY.HQD)
        var extras = Lba1SecretRoomExtras.LoadTemplates(new SceneStore(SceneGame.Lba1, directory));
        string? elfMessage = null, extrasMessage = null;

        var door = Lba1RoomDoorMod.Apply(directory,
            roomEdit: room => ((elfMessage = Lba1PinkElf.EditRoom(room)) is not null) | ((extrasMessage = Lba1SecretRoomExtras.EditRoom(room, extras, mushroom.BodyId)) is not null),
            extraEdits: MergeFiles(files.Files, mushroom.Files).Concat(text).ToList(),
            extraTexts: MergeTexts(files.Texts, mushroom.Texts),
            historyName: HistoryName,
            extraScenes: fishermen.Changes);

        var lines = new List<string>();
        if (door.Changed && door.Message.Length > 0) lines.Add(door.Message);
        if (files.Changed) lines.Add($"BODY.HQR: the pink elf's body added as entry {files.BodyIndex}; FILE3D.HQR: the Elf entity (49) got it as body {Lba1PinkElf.BodyId}.");
        if (elfMessage is not null) lines.Add(elfMessage + ".");
        if (extrasMessage is not null) lines.Add(extrasMessage + ".");
        if (mushroom.Changed) lines.Add("BODY.HQR / FILE3D.HQR: the mushroom body put in place (entity " + Lba1SecretRoomExtras.MushroomEntity + ", body " + mushroom.BodyId + ").");
        foreach (var note in fishermen.Notes) lines.Add("fishermen, " + note + " (SCENE.HQR).");
        if (text.Count > 0) lines.Add($"TEXT.HQR: new texts ({Lba1Fishermen.QuestionId}, {Lba1PinkElf.GreetingId}) added to island 1's dialogue in all five languages.");
        if (lines.Count == 0) return new Lba1RoomDoorMod.Result(false, "The bedroom is already connected, the pink elf is there and the fishermen already take the trips; nothing to change.");
        lines.Add("Originals are kept as .bak copies.");
        return new Lba1RoomDoorMod.Result(true, string.Join("\n", lines));
    }
}
