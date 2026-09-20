using LBA2LevelEditor.LbaScript;

namespace LBA2LevelEditor.Scenes;

// A ready-made actor: the header fields and scripts of a kind of actor the games use over and over, built for a given
// actor number and position. Prefabs are modelled on real retail actors (each one is tested to reproduce the actor it
// was taken from), so an actor made from one behaves as the game's own.
internal sealed record ActorPrefab(string Id, string Name, string Description, SceneGame Game, Func<int, int, int, int, SceneActorModel> Build);

internal static class ActorPrefabs
{
    // LBA1 door sprites: 11 fits an arch facing +x (east), 12 an arch facing +z (south). The door slides "up" (towards -z)
    // or "left" (towards -x) into the wall beside it; Info holds the door's clip rectangle on screen, which for a door
    // at a given spot is the same rectangle (measured from the retail doors) relative to the spot's projection.
    private const uint LbaDoorFlags = 0xD409;   // MINI_ZV | ... | NO_SHADOW | SPRITE_3D | SPRITE_CLIP | CHECK_OBJ_COL

    public static readonly ActorPrefab DoorEast = new(
        "lba1-door-east", "Door (arch facing east)",
        "The standard sliding door of an arch on a wall facing +x (sprite 11). Opens when Twinsen bumps into it, closes when he is 3000 units away. Place it on the arch's cell, at the arch's first z cell (z = cell * 512 - 256).",
        SceneGame.Lba1,
        (index, x, y, z) => LbaDoor(index, x, y, z, sprite: 11, up: true, clip: (-60, -94, 4, 8)));

    public static readonly ActorPrefab DoorSouth = new(
        "lba1-door-south", "Door (arch facing south)",
        "The standard sliding door of an arch on a wall facing +z (sprite 12). Opens when Twinsen bumps into it, closes when he is 3000 units away.",
        SceneGame.Lba1,
        (index, x, y, z) => LbaDoor(index, x, y, z, sprite: 12, up: false, clip: (-2, -98, 61, 9)));

    public static IReadOnlyList<ActorPrefab> For(SceneGame game) => All.Where(p => p.Game == game).ToList();

    public static IReadOnlyList<ActorPrefab> All { get; } = new[] { DoorEast, DoorSouth };

    public static ActorPrefab? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    // Appends an actor made from `prefab` and returns its number. Actor numbers are what other scripts refer to, so
    // new actors always go last.
    public static int Place(SceneModel scene, ActorPrefab prefab, int x, int y, int z)
    {
        if (prefab.Game != scene.Game) throw new InvalidOperationException($"'{prefab.Name}' is for the other game.");
        if (scene.Actors.Count >= SceneValidator.MaxObjects) throw new InvalidOperationException($"The scene already has {SceneValidator.MaxObjects} actors, the most the engine allows.");
        var index = scene.Actors.Count;
        scene.Actors.Add(prefab.Build(index, x, y, z));
        return index;
    }

    // The isometric projection of the game (24 px per cell along x - z, 12 along x + z, 15 per layer), origin at the world's.
    private static (int X, int Y) Screen(int x, int y, int z) => ((x - z) * 24 / 512, (x + z) * 12 / 512 - y * 15 / 256);

    private static SceneActorModel LbaDoor(int index, int x, int y, int z, int sprite, bool up, (int X0, int Y0, int X1, int Y1) clip)
    {
        var (sx, sy) = Screen(x, y, z);
        var door = new SceneActorModel
        {
            Flags = LbaDoorFlags, Entity = -1, Body = 0, Anim = 0, Sprite = sprite,
            X = x, Y = y, Z = z, HitForce = 0, OptionFlags = 0, Beta = 0, SRot = 0, Move = 0,
            Info = new[] { sx + clip.X0, sy + clip.Y0, sx + clip.X1, sy + clip.Y1 },
            NbBonus = 1, CoulObj = 7, Armor = 51, LifePoints = 1,
        };

        var setDoor = up ? "set_door_up" : "set_door_left";
        var open = up ? "open_up" : "open_left";
        using var scope = Opcodes.Use(Opcodes.Lba1);
        var track = TrackText.Compile(
            $"label(0);\nbackground(0);\nsample(35);\n{open}(1550);\nwait_door();\nlabel(100);\nstop();\n\n" +
            "label(1);\nsample(35);\nclose();\nwait_door();\nbackground(1);\nlabel(110);\nstop();\n");
        var life = LifeText.Compile(
            $"void comportement_0()\n{{\n    {setDoor}(1550);\n    set_comportement(comportement_2);\n}}\n\n" +
            $"void comportement_1()\n{{\n    if ({index} == col_obj(0))\n    {{\n        set_track(label_0);\n        set_comportement(comportement_2);\n    }}\n}}\n\n" +
            "void comportement_2()\n{\n    if (3000 < distance(0))\n    {\n        set_track(label_1);\n        set_comportement(comportement_1);\n    }\n}\n",
            index, NoSymbols.Instance);
        // set_track(label_n) is the label's byte offset in this actor's own track script
        life.ResolveExternals(r => track.Symbols.TryGetValue(r.Symbol, out var offset) ? offset : null);
        door.Track = track.Bytes;
        door.Life = life.Bytes;
        return door;
    }
}
