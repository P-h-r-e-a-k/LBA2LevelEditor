using System.IO;
using System.Numerics;
using LbaBodyStudio;
using LBAAssembler.LbaScript;
using LBAAssembler.Scenes;

namespace LBAAssembler.Lba1;

// What the bedroom (scene 61, the secret room off Lupin Burg) gets besides the pink elf:
//   * a meca penguin that walks up and down the room and can be collected, done the way the rebels' village (scene 60) does it: the actor is
//     scene 60's own (entity 9, its walk and its "explodes when hit" script), the hero's life script takes it when Twinsen touches it
//     (game flag 14, the penguin item: set_var_game(14, 1); found_object(14)) while he does not have one;
//   * twelve mushrooms, a red cap on a pale stem that LBA1 has no model for: BODY.HQR gets one (built here from nothing but points and polygons,
//     the way a body is put together, on the ID card body's header and bone) and the ID card's entity, whose one animation is one frame of one bone,
//     gets a record for it. They stand as two smiley faces either side of the door lane, each two eyes, a nose and a mouth of five: the left face
//     (larger z on the picture's left) gives clovers with a heart worth 50 for its nose, the right one clover boxes with a magic bottle worth 80. The eyes
//     are kash coins worth 50 each (the game's own sprite 3 as sprite actors: a popped-out coin is taken away after 20 seconds, these stay until touched);
//   * a mushroom is asked when Twinsen presses action within reach of it, the nearest one within 700 units, once per press (the hero's script sets a scene flag, var_cube, for its slot). A clover,
//     heart or bottle mushroom then pops its reward out of its top (give_bonus: the actor's OptionFlags say which bonus and NbBonus how much, the
//     engine's own way for a creature to drop one) and is used up (suicide); it is back on the next visit. Clover boxes cannot be given by give_bonus (it
//     offers money, a heart, magic, a key or a clover leaf), so a clover-box mushroom works like the game's own clover boxes: a sprite actor
//     (scene 25's) stays hidden until its mushroom is asked, then appears where the mushroom stood; touching it does inc_clover_box and sets one game flag (221..225,
//     one per box) so that it is given once, ever, and its mushroom is then gone for good.
internal static class Lba1SecretRoomExtras
{
    public const int RoomScene = 61;
    public const int PenguinScene = 60, PenguinActor = 7, BoxScene = 25, BoxActor = 2;
    // FILE3D entity 42 (the ID card): one body (BODY.HQR 78, one bone) and one animation (one frame of one bone): what a one-bone mushroom needs, since a
    // body has to have as many bones as the animations of its entity. The card's body is the template for the mushroom's header and bone record.
    public const int MushroomEntity = 42, DonorBody = 78;
    public const int PenguinFlag = 14;
    public const int FirstBoxFlag = 221, Boxes = 5;
    public const int HeartWorth = 50, MagicWorth = 80;
    // How close Twinsen has to be to ask a mushroom. The mushrooms are two cells apart and each has a box of its own (Twinsen's box and the mushroom's leave him
    // about 460 from its middle), so he can be within reach of two of them: the hero's script asks the nearest one, by trying the mushrooms in turn at each of
    // these distances, closest first (the script can only compare a distance with a constant).
    private static readonly int[] Reaches = { 500, 550, 600, 650, 700 };

    private const int Floor = 768;
    private const int Bonus = 4;                            // zone type 4: giver (an earlier version used them for the rewards)
    private const int HeartBit = 1 << 5, MagicBit = 1 << 6, CloverBit = 1 << 8;

    // The penguin walks along x 56 (between the beds and the elf, at 57, 55) from z 50 to z 60.
    private const int PenguinFromZ = 50, PenguinToZ = 60, PenguinLaneX = 56;
    // ---- the mushroom's body ---------------------------------------------------------------------------------------

    private const byte StemColour = 48, CapColour = 80, SpotColour = 240, UnderColour = 128;   // palette ramps 3 (skin), 5 (crimson), 15 (white), 8 (brown)

    // The body's bytes: a stem (8 sides, 280 tall) under a cap (a lip, two rings and an apex, 610 tall in all), one bone, lit like the retail bodies (the cap's box, 420 wide, leaves room to walk between two mushrooms two cells apart).
    // `donor` supplies the header and a bone record of an animated one-bone body (the game's, entry 78).
    public static byte[] BuildBody(byte[] donor)
    {
        var template = Body.Read(donor, 1);
        var body = new Body { Game = 1, Lit = true, Header = template.Header };
        const int Sides = 8;
        int Ring(float radius, float y)
        {
            var first = body.Vertices.Count;
            for (var i = 0; i < Sides; i++)
            {
                var angle = i * MathF.PI * 2 / Sides;
                body.Vertices.Add(new Vector3(MathF.Round(radius * MathF.Cos(angle)), y, MathF.Round(radius * MathF.Sin(angle))));
            }
            return first;
        }
        var foot = Ring(70, 0);
        var neck = Ring(55, 280);
        var lip = Ring(210, 280);
        var shoulder = Ring(200, 350);
        var dome = Ring(155, 480);
        var crown = Ring(85, 570);
        var apex = body.Vertices.Count;
        body.Vertices.Add(new Vector3(0, 610, 0));

        // Rings joined lower to upper going round: the order that makes the polygon's normal point outwards (the game's bodies are all this way).
        void Band(int lower, int upper, int colour, Func<int, int>? spot = null)
        {
            for (var i = 0; i < Sides; i++)
            {
                var next = (i + 1) % Sides;
                body.Faces.Add(new Face(new[] { lower + i, upper + i, upper + next, lower + next }, spot is null ? colour : spot(i)));
            }
        }
        Band(foot, neck, StemColour);
        // the underside of the cap, facing down: neck to lip
        for (var i = 0; i < Sides; i++)
        {
            var next = (i + 1) % Sides;
            body.Faces.Add(new Face(new[] { neck + i, lip + i, lip + next, neck + next }, UnderColour));
        }
        Band(lip, shoulder, CapColour);
        Band(shoulder, dome, CapColour, i => i % 4 == 1 ? SpotColour : CapColour);
        Band(dome, crown, CapColour, i => i % 4 == 3 ? SpotColour : CapColour);
        for (var i = 0; i < Sides; i++) body.Faces.Add(new Face(new[] { crown + i, apex, crown + (i + 1) % Sides }, CapColour));

        // one bone owning every point: the donor's first bone record with its point range and parent replaced
        var record = (byte[])template.Bones[0].Record.Clone();
        BitConverter.GetBytes((ushort)0).CopyTo(record, 0);
        BitConverter.GetBytes((ushort)body.Vertices.Count).CopyTo(record, 2);
        BitConverter.GetBytes((ushort)0).CopyTo(record, 4);
        BitConverter.GetBytes((short)-1).CopyTo(record, 6);
        body.Bones.Add(new Bone(0, body.Vertices.Count, 0, -1, record));
        return body.Write();
    }

    // ---- BODY.HQR and FILE3D.HQR -----------------------------------------------------------------------------------

    private sealed record Record(int Type, int Id, int Start, int End, int Hqr);

    private static List<Record> Records(byte[] entity)
    {
        var records = new List<Record>();
        var p = 0;
        while (p < entity.Length && entity[p] != 0xFF)
        {
            if (p + 3 > entity.Length || entity[p + 2] < 1 || p + 2 + entity[p + 2] > entity.Length) throw new InvalidDataException($"FILE3D entity {MushroomEntity} has a broken record.");
            var end = p + 2 + entity[p + 2];
            var hqr = entity[p] is 1 or 3 && end - p >= 5 ? entity[p + 3] | entity[p + 4] << 8 : -1;
            records.Add(new Record(entity[p], entity[p + 1], p, end, hqr));
            p = end;
        }
        if (p >= entity.Length) throw new InvalidDataException($"FILE3D entity {MushroomEntity} has no end marker.");
        return records;
    }

    private static bool IsMushroom(byte[] bytes)
    {
        try
        {
            var body = Body.Read(bytes, 1);
            return body.Vertices.Count == 49 && body.Bones.Count == 1 && body.Faces.Count == 48;
        }
        catch (Exception) { return false; }
    }

    public sealed record Plan(int BodyId, int BodyIndex, IReadOnlyList<HqrEntryStore.Edit> Files, IReadOnlyList<HqrEntryStore.TextEdit> Texts)
    {
        public bool Changed => Files.Count > 0;
    }

    // The body registered in the game's files: what to write (nothing when the mushroom body is there already) and its id in the entity.
    // earlier/earlierTexts: edits (and BODY.HQD lines) another part of the same save has already planned (the pink elf's BODY.HQR,
    // FILE3D.HQR and BODY.HQD): this one builds on those (LoadWithPending / Describe's own pendingContent), so both end up in the one
    // file each rather than the second plan's write undoing the first's.
    public static Plan PlanFiles(string directory, IReadOnlyList<HqrEntryStore.Edit>? earlier = null, IReadOnlyList<HqrEntryStore.TextEdit>? earlierTexts = null)
    {
        var bodyPath = Path.Combine(directory, "BODY.HQR");
        var entityPath = Path.Combine(directory, "FILE3D.HQR");
        if (!File.Exists(bodyPath) || !File.Exists(entityPath)) throw new InvalidDataException("BODY.HQR and FILE3D.HQR must be in the game folder.");
        var bodies = HqrEntryStore.LoadWithPending(directory, "BODY.HQR", earlier);
        var entities = HqrEntryStore.LoadWithPending(directory, "FILE3D.HQR", earlier);
        if (MushroomEntity >= entities.Count || entities.IsEmpty(MushroomEntity)) throw new InvalidDataException($"FILE3D.HQR has no entity {MushroomEntity}.");
        if (DonorBody >= bodies.Count || bodies.IsEmpty(DonorBody)) throw new InvalidDataException($"BODY.HQR has no entry {DonorBody}.");
        var mushroom = BuildBody(bodies.Read(DonorBody));

        var entity = entities.Read(MushroomEntity);
        var records = Records(entity);
        var bodyRecords = records.Where(r => r.Type == 1).ToList();
        if (bodyRecords.Count == 0 || bodyRecords[0].Hqr != DonorBody || records.Count(r => r.Type == 3) != 1) throw new InvalidDataException($"FILE3D.HQR entity {MushroomEntity} isn't the ID card (body {DonorBody}, one animation) the mushroom is added to.");
        var existing = bodyRecords.FirstOrDefault(r => r.Hqr >= 0 && r.Hqr < bodies.Count && !bodies.IsEmpty(r.Hqr) && bodies.Read(r.Hqr).AsSpan().SequenceEqual(mushroom));
        if (existing is not null) return new Plan(existing.Id, existing.Hqr, Array.Empty<HqrEntryStore.Edit>(), Array.Empty<HqrEntryStore.TextEdit>());

        const string description = "Mushroom, added by the level editor for the bedroom's secret room, scene 61";
        var pendingHqd = earlierTexts?.LastOrDefault(t => string.Equals(t.RelativePath, HqdWriter.SidecarName("BODY.HQR"), StringComparison.OrdinalIgnoreCase)).Content;

        // an earlier shape of the mushroom (this tool's own body: 49 points, one bone, 48 faces) is replaced in place, not added to
        var older = bodyRecords.FirstOrDefault(r => r.Hqr >= 0 && r.Hqr != DonorBody && r.Hqr < bodies.Count && !bodies.IsEmpty(r.Hqr) && IsMushroom(bodies.Read(r.Hqr)));
        if (older is not null)
            return new Plan(older.Id, older.Hqr, new[] { new HqrEntryStore.Edit("BODY.HQR", older.Hqr, mushroom) },
                new[] { new HqrEntryStore.TextEdit(HqdWriter.SidecarName("BODY.HQR"), HqdWriter.Describe(directory, "BODY.HQR", SceneGame.Lba1, older.Hqr, description, pendingHqd)) });

        var id = bodyRecords.Max(r => r.Id) + 1;
        if (id > 255) throw new InvalidDataException($"FILE3D entity {MushroomEntity} has no free body number.");
        var index = bodies.Count;
        if (index >= 0x8000) throw new InvalidDataException("BODY.HQR has no room for another body (the engine keeps bit 15 of a body reference for its own use).");
        var record = new byte[] { 1, (byte)id, 4, (byte)(index & 255), (byte)(index >> 8), 0 };
        var last = bodyRecords[^1];
        var newEntity = entity[..last.End].Concat(record).Concat(entity[last.End..]).ToArray();
        var files = new[]
        {
            new HqrEntryStore.Edit("BODY.HQR", index, mushroom),
            new HqrEntryStore.Edit("FILE3D.HQR", MushroomEntity, newEntity),
        };
        var texts = new[] { new HqrEntryStore.TextEdit(HqdWriter.SidecarName("BODY.HQR"), HqdWriter.Describe(directory, "BODY.HQR", SceneGame.Lba1, index, description, pendingHqd)) };
        return new Plan(id, index, files, texts);
    }

    // ---- the room's floor ------------------------------------------------------------------------------------------

    // The room's grid (61) has a hole in its floor: the one column of the floor at x 59, z 62 (the row along the south wall) has no brick at all, where the floor
    // is a single layer (2) of block 1's tiles. It is filled with a tile of that block.
    private const int HoleX = 59, HoleY = 2, HoleZ = 62, HoleBlock = 1, HolePosition = 6;

    public static bool FloorHasHole(byte[] grid) => Lba1GridEdit.Get(grid, HoleX, HoleY, HoleZ).Block == 0;

    public static byte[] FillFloorHole(byte[] grid)
        => FloorHasHole(grid) ? Lba1GridEdit.SetCells(grid, new[] { new Lba1GridCell(HoleX, HoleY, HoleZ, HoleBlock, HolePosition) }) : grid;
    // ---- scene 61 --------------------------------------------------------------------------------------------------

    // The actors the game already has that these are made from: the penguin of the rebels' village and a clover box.
    public sealed record Templates(SceneActorModel Penguin, SceneActorModel Box);

    public static Templates LoadTemplates(SceneStore store)
    {
        var penguin = store.Load(PenguinScene).Actors[PenguinActor];
        var box = store.Load(BoxScene).Actors[BoxActor];
        if (penguin.Entity != 9 || penguin.IsSprite || !box.IsSprite || box.Sprite != 41)
            throw new InvalidDataException("Scenes 60 and 25 aren't what the secret room's extras are made from (the meca penguin actor and the clover box sprite); not touching them.");
        return new Templates(penguin.Clone(), box.Clone());
    }

    private static int CellX(int cell) => cell * 512;

    // What each thing in a face is for: a bonus a mushroom pops out (its actor's OptionFlags and NbBonus, given by give_bonus) or a clover box (a hidden sprite that takes
    // the mushroom's place).
    private enum Reward { Clover, Heart, Magic, CloverBox }

    // The two faces, seen on the picture (the isometric view: up on the screen is decreasing x + z, right is increasing x - z): two eyes, a nose and a mouth of five that
    // curves up at both ends. Each is 5 x 5 cells; the left one (larger z) is clovers with a heart for a nose, the right one clover boxes with a bottle. The door lane
    // (z 54..56) stays free between them, and the mushrooms are two cells apart so that Twinsen can walk between them. The eyes are coins, sprite actors (Coins below).
    // (cell x, cell z, reward), face by face, the mouth first and then the nose, in the order the actors are added.
    private static readonly (int X, int Z, Reward What)[] Mushrooms =
    {
        (58, 61, Reward.Clover), (60, 61, Reward.Clover), (62, 61, Reward.Clover), (62, 59, Reward.Clover), (62, 57, Reward.Clover),          // the left face: mouth,
        (60, 59, Reward.Heart),                                                                                                             //   nose
        (58, 53, Reward.CloverBox), (60, 53, Reward.CloverBox), (62, 53, Reward.CloverBox), (62, 51, Reward.CloverBox), (62, 49, Reward.CloverBox), // the right face: mouth,
        (60, 51, Reward.Magic),                                                                                                             //   nose
    };

    // The eyes: a kash coin (the game's own sprite 3, the one a monster drops) worth 50 lying where each eye is, left face first. A popped-out coin is taken away again
    // after 20 seconds (EXTRA.C), so these are sprite actors that stay: touching one pops the game's own coin out of it (give_bonus) at once, towards Twinsen,
    // and the sprite is gone for that visit. Like the mushrooms' clovers they are back on the next visit.
    private static readonly (int X, int Z)[] Coins = { (58, 59), (60, 57), (58, 51), (60, 49) };
    private const int CoinSprite = 3, CoinWorth = 50, KashBit = 1 << 4;

    private static int BoxMushroomSlot(int box) => Enumerable.Range(0, Mushrooms.Length).Where(i => Mushrooms[i].What == Reward.CloverBox).ElementAt(box);

    private static SceneActorModel Mushroom(int slot, int index, int bodyId)
    {
        var (x, z, what) = Mushrooms[slot];
        var box = what == Reward.CloverBox;
        var flag = box ? FirstBoxFlag + Enumerable.Range(0, slot).Count(i => Mushrooms[i].What == Reward.CloverBox) : -1;
        var life = box
            // a box mushroom: gone at once when its clover box has been given, and used up when it has shown it
            ? $"void comportement_0()\n{{\n    if (1 == var_game({flag}))\n    {{\n        suicide();\n    }}\n    else\n    {{\n        set_comportement(comportement_1);\n    }}\n}}\n\n" +
              $"void comportement_1()\n{{\n    if (1 == var_cube({slot}))\n    {{\n        suicide();\n    }}\n}}\n"
            // a bonus mushroom: when it is asked it pops its reward out of its top and is used up
            : $"void comportement_0()\n{{\n    set_comportement(comportement_1);\n}}\n\n" +
              $"void comportement_1()\n{{\n    if (1 == var_cube({slot}))\n    {{\n        give_bonus(1);\n        suicide();\n    }}\n}}\n";
        var compiled = Compile(life, "", index);
        return new SceneActorModel
        {
            Flags = 0x0803, Entity = MushroomEntity, Body = bodyId, Anim = 0, Sprite = 0,
            X = CellX(x), Y = Floor, Z = CellX(z), Beta = 0, SRot = 35, Move = 0,
            HitForce = 0,
            OptionFlags = what switch { Reward.Clover => CloverBit, Reward.Heart => HeartBit, Reward.Magic => MagicBit, _ => 0 },
            Info = new[] { -1, -1, -1, -1 },
            NbBonus = what switch { Reward.Heart => HeartWorth, Reward.Magic => MagicWorth, _ => 1 }, CoulObj = 1, Armor = 51, LifePoints = 1,
            Track = Array.Empty<byte>(), Life = compiled.Life,
        };
    }

    // The hero's script: takes the penguin when he touches it, and when action is pressed within 600 units of a mushroom asks the first one in this list
    // that is near enough (one press, one mushroom: the ones already used up are 32000 units away, being dead, and a box mushroom whose box has been given
    // is left out).
    public static string HeroScript(int penguin, int firstMushroom)
    {
        var text = new System.Text.StringBuilder();
        text.Append("void comportement_0()\n{\n    set_comportement(comportement_1);\n}\n\n");
        text.Append("void comportement_1()\n{\n");
        text.Append($"    if (0 == var_game({PenguinFlag}))\n    {{\n        if ({penguin} == col())\n        {{\n            set_var_game({PenguinFlag}, 1);\n            kill_obj({penguin});\n            found_object({PenguinFlag});\n        }}\n    }}\n");
        // one mushroom per press: the press is held in a cube variable of its own until action is let go, so a held button doesn't go on to the next mushroom
        text.Append($"    if (1 == action())\n    {{\n        if (0 == var_cube({Mushrooms.Length}))\n        {{\n            set_var_cube({Mushrooms.Length}, 1);\n");
        // (a mushroom that has suicided is 32000 away, so the ones already gone are never asked)
        var first = true;
        foreach (var reach in Reaches)
            for (var slot = 0; slot < Mushrooms.Length; slot++)
            {
                text.Append($"            {(first ? "if" : "else if")} ({reach} > distance({firstMushroom + slot}))\n            {{\n                set_var_cube({slot}, 1);\n            }}\n");
                first = false;
            }
        text.Append($"        }}\n    }}\n    else\n    {{\n        set_var_cube({Mushrooms.Length}, 0);\n    }}\n}}\n");
        return text.ToString();
    }

    public static string PenguinLife(int index) =>
        "void comportement_0()\n{\n    set_track(label_0);\n    set_comportement(comportement_1);\n}\n\n" +
        $"void comportement_1()\n{{\n    if (50 > life_point_obj({index}))\n    {{\n        set_life_point_obj({index}, 50);\n    }}\n    swif (6 == hit_by())\n    {{\n        stop_l_track();\n        set_track(label_1);\n        explode_obj({index});\n        explode_obj({index});\n        explode_obj({index});\n        set_comportement(comportement_2);\n    }}\n}}\n\n" +
        "void comportement_2()\n{\n    if (100 == l_track())\n    {\n        anim(1);\n        restore_l_track();\n        set_comportement(comportement_1);\n    }\n}\n";

    public static string PenguinTrack(int first, int second) =>
        $"label(0);\nanim(1);\ngoto_point({first});\ngoto_point({second});\ngoto(label_0);\n\nlabel(1);\nsample(37);\n\nlabel(100);\nstop();\n";

    // A clover box: hidden until its mushroom has been asked (var_cube of the mushroom's slot), then given once by touch.
    public static string BoxLife(int index, int flag, int slot) =>
        $"void comportement_0()\n{{\n    if (1 == var_game({flag}))\n    {{\n        suicide();\n    }}\n    else\n    {{\n        invisible(1);\n        set_comportement(comportement_1);\n    }}\n}}\n\n" +
        $"void comportement_1()\n{{\n    if (1 == var_cube({slot}))\n    {{\n        invisible(0);\n        set_comportement(comportement_2);\n    }}\n}}\n\n" +
        $"void comportement_2()\n{{\n    oneif ({index} == col_obj(0))\n    {{\n        inc_clover_box();\n        set_var_game({flag}, 1);\n        set_track(label_0);\n    }}\n    if (1 == l_track())\n    {{\n        suicide();\n    }}\n}}\n";

    public const string BoxTrack = "label(0);\nsample(41);\n\nlabel(1);\nstop();\n";

    // Compiles a script against the actor's own track script (set_track(label_n) is a label's byte offset in it).
    private static (byte[] Life, byte[] Track) Compile(string life, string track, int index)
    {
        using var scope = Opcodes.Use(Opcodes.Lba1);
        var t = TrackText.Compile(track);
        var l = LifeText.Compile(life, index, NoSymbols.Instance);
        l.ResolveExternals(r => t.Symbols.TryGetValue(r.Symbol, out var offset) ? offset : null);
        return (l.Bytes, t.Bytes);
    }

    // Where a box appears: where its mushroom stood (the mushroom is used up as the box shows, so it takes the mushroom's place; an earlier version put it one cell towards
    // the door lane, where a box stood in the way of Twinsen coming through the door).
    private static (int X, int Z) BoxCell(int box) { var (x, z, _) = Mushrooms[BoxMushroomSlot(box)]; return (x, z); }

    // A coin: a sprite actor made from the clover box's, showing the kash sprite; touched by Twinsen it pops the coin out (worth 50, flying towards him) and is used up.
    private static string CoinLife(int index) =>
        "void comportement_0()\n{\n    set_comportement(comportement_1);\n}\n\n" +
        $"void comportement_1()\n{{\n    if ({index} == col_obj(0))\n    {{\n        give_bonus(1);\n        suicide();\n    }}\n}}\n";

    private static SceneActorModel Coin(Templates templates, int coin, int index)
    {
        var actor = templates.Box.Clone();
        actor.Sprite = CoinSprite;
        actor.X = CellX(Coins[coin].X); actor.Y = Floor; actor.Z = CellX(Coins[coin].Z);
        actor.OptionFlags = KashBit; actor.NbBonus = CoinWorth;
        actor.Life = Compile(CoinLife(index), "", index).Life;
        actor.Track = Array.Empty<byte>();
        return actor;
    }

    // Brings the bedroom up to date: the penguin, the mushrooms (in place when an earlier version put them elsewhere or with other scripts), the clover boxes and
    // the hero's script, and no bonus zones (an earlier version used zones for the rewards). Returns what it changed, or null when everything was right.
    public static string? EditRoom(SceneModel room, Templates templates, int bodyId)
    {
        var existing = room.Actors.Where(a => !a.IsSprite && a.Entity == MushroomEntity && a.Body == bodyId).ToList();
        if (existing.Count != 0 && existing.Count != Mushrooms.Length) throw new InvalidDataException($"Scene {RoomScene} has {existing.Count} mushrooms where this tool puts {Mushrooms.Length}; not touching it.");
        var changes = new List<string>();
        var fresh = existing.Count == 0;
        var penguin = fresh ? room.Actors.Count : room.Actors.IndexOf(existing[0]) - 1;
        var firstMushroom = penguin + 1;
        var firstBox = firstMushroom + Mushrooms.Length;
        var firstCoin = firstBox + Boxes;
        if (!fresh && (penguin < 1 || room.Actors[penguin].IsSprite || room.Actors[penguin].Entity != 9 || room.Actors.Count < firstBox + Boxes
                       || room.Actors.Skip(firstMushroom).Take(Mushrooms.Length).Any(a => a.IsSprite || a.Entity != MushroomEntity)
                       || room.Actors.Skip(firstBox).Take(Boxes).Any(a => !a.IsSprite || a.Sprite != 41)
                       || (room.Actors.Count != firstCoin && (room.Actors.Count != firstCoin + Coins.Length || room.Actors.Skip(firstCoin).Any(a => !a.IsSprite || a.Sprite != CoinSprite)))))
            throw new InvalidDataException($"Scene {RoomScene}'s mushrooms aren't laid out as this tool lays them out (penguin, twelve mushrooms, five boxes, four coins); not touching it.");

        // set the fields an update may change, and say whether any did
        static bool Set(SceneActorModel actor, SceneActorModel wanted)
        {
            var same = actor.X == wanted.X && actor.Y == wanted.Y && actor.Z == wanted.Z && actor.OptionFlags == wanted.OptionFlags && actor.NbBonus == wanted.NbBonus
                       && actor.Life.AsSpan().SequenceEqual(wanted.Life) && actor.Track.AsSpan().SequenceEqual(wanted.Track);
            if (same) return false;
            (actor.X, actor.Y, actor.Z, actor.OptionFlags, actor.NbBonus, actor.Life, actor.Track) = (wanted.X, wanted.Y, wanted.Z, wanted.OptionFlags, wanted.NbBonus, wanted.Life, wanted.Track);
            return true;
        }
        int Point(int cellX, int cellZ)
        {
            var point = new SceneTrackPoint(CellX(cellX), Floor, CellX(cellZ));
            var at = room.TrackPoints.IndexOf(point);
            return at >= 0 ? at : SceneOps.AddTrackPoint(room, point);
        }

        // the penguin: the village's own, walking between two track points in the room
        var wantedPenguin = templates.Penguin.Clone();
        wantedPenguin.X = CellX(PenguinLaneX); wantedPenguin.Y = Floor; wantedPenguin.Z = CellX(PenguinFromZ);
        (wantedPenguin.Life, wantedPenguin.Track) = Compile(PenguinLife(penguin), PenguinTrack(Point(PenguinLaneX, PenguinFromZ), Point(PenguinLaneX, PenguinToZ)), penguin);
        if (fresh) { if (SceneOps.AddActor(room, wantedPenguin) != penguin) throw new InvalidDataException("The room's actors moved while the extras were being added."); changes.Add($"a meca penguin (actor {penguin})"); }
        else if (Set(room.Actors[penguin], wantedPenguin)) changes.Add("the penguin's walk");

        var moved = 0;
        for (var slot = 0; slot < Mushrooms.Length; slot++)
        {
            var wanted = Mushroom(slot, firstMushroom + slot, bodyId);
            if (fresh) { if (SceneOps.AddActor(room, wanted) != firstMushroom + slot) throw new InvalidDataException("The room's actors moved while the extras were being added."); }
            else if (Set(room.Actors[firstMushroom + slot], wanted)) moved++;
        }
        if (fresh) changes.Add($"twelve mushrooms (actors {firstMushroom}..{firstMushroom + Mushrooms.Length - 1}) as two smiley faces");
        else if (moved > 0) changes.Add($"{moved} mushrooms made into two smiley faces that suicide after giving their bonus");

        var boxesChanged = 0;
        for (var i = 0; i < Boxes; i++)
        {
            var box = templates.Box.Clone();
            var (bx, bz) = BoxCell(i);
            box.X = CellX(bx); box.Y = Floor; box.Z = CellX(bz);
            (box.Life, box.Track) = Compile(BoxLife(firstBox + i, FirstBoxFlag + i, BoxMushroomSlot(i)), BoxTrack, firstBox + i);
            if (fresh) { if (SceneOps.AddActor(room, box) != firstBox + i) throw new InvalidDataException("The room's actors moved while the extras were being added."); }
            else if (Set(room.Actors[firstBox + i], box)) boxesChanged++;
        }
        if (fresh) changes.Add($"five clover boxes (actors {firstBox}..{firstBox + Boxes - 1})");
        else if (boxesChanged > 0) changes.Add($"{boxesChanged} clover boxes moved onto their mushrooms' places");

        var coinsChanged = 0;
        for (var i = 0; i < Coins.Length; i++)
        {
            var coin = Coin(templates, i, firstCoin + i);
            if (room.Actors.Count <= firstCoin + i) { SceneOps.AddActor(room, coin); coinsChanged++; }
            else if (Set(room.Actors[firstCoin + i], coin)) coinsChanged++;
        }
        if (coinsChanged > 0) changes.Add($"{coinsChanged} kash coins (worth {CoinWorth}) for the faces' eyes (actors {firstCoin}..{firstCoin + Coins.Length - 1})");

        // an earlier version gave the rewards through bonus zones of ours (a zone 640 units tall on the floor at 768): they go
        var oldZones = room.Zones.Where(z => z.Type == Bonus && z.Y0 == Floor && z.Y1 == Floor + 640 && z.Info[0] == 0 && z.Info[3] == 0).ToList();
        if (oldZones.Count > 0) { room.Zones.RemoveAll(oldZones.Contains); changes.Add($"{oldZones.Count} bonus zones removed"); }

        // the hero: no script yet (a one-byte end marker is none), or the one this tool wrote
        var hero = room.Actors[0];
        var heroLife = Compile(HeroScript(penguin, firstMushroom), "", 0).Life;
        if (!hero.Life.AsSpan().SequenceEqual(heroLife))
        {
            if (hero.Life.Any(b => b != 0) && !SceneScripts.Load(SceneSerializer.Write(room), RoomScene, null, lba1: true).GetText(0, ScriptKind.Life).Contains($"found_object({PenguinFlag})"))
                throw new InvalidDataException("The bedroom's hero already has a life script that isn't this tool's; not touching it.");
            hero.Life = heroLife;
            changes.Add("the hero's script");
        }
        return changes.Count == 0 ? null : $"scene {RoomScene}: " + string.Join(", ", changes);
    }
}