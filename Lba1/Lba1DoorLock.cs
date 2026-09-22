using System.IO;
using LBA2LevelEditor.LbaScript;
using LBA2LevelEditor.Scenes;

namespace LBA2LevelEditor.Lba1;

// The bedroom door in Lupin Burg (Lba1RoomDoorMod) needs a little key the first time it is opened. The key is spent and the fact is
// kept in a game flag ("Door unlocked"), so that every later visit just opens it. The game saves its flags with the save game
// (GAMEMENU.C writes all MAX_FLAGS_GAME = 255 of them), so the lock stays open in every save made afterwards.
//
// Which flag: scanning the decompiled life scripts of all 120 scenes and the engine's own sources, var_game(n) is used for n = 0..219
// except 27 (the four-leaf clover, engine) and 148, and never for 159..199 or 220..254. The flags below 28 are the inventory (the
// game hides them while the "consigne" flag 70 is set); 220, the first of the untouched tail, has no such behaviour, and nothing of
// the game's own ever writes it. The next two (221, 222) are left free.
internal static class Lba1DoorLock
{
    public const int Flag = 220;
    public const string FlagName = "Door unlocked";

    // The door's script before the lock (the standard sliding door, Scenes/ActorPrefabs) and with it. Only the collision test changes.
    private static string OpensText(int actor) =>
        $"    if ({actor} == col_obj(0))\n    {{\n        set_track(label_0);\n        set_comportement(comportement_2);\n    }}\n";

    private static string LockedText(int actor) =>
        $"    if ({actor} == col_obj(0))\n    {{\n" +
        $"        if (0 == var_game({Flag}))\n        {{\n            if (0 < nb_little_keys())\n            {{\n                use_one_little_key();\n                set_var_game({Flag}, 1);\n            }}\n        }}\n" +
        $"        if (1 == var_game({Flag}))\n        {{\n            set_track(label_0);\n            set_comportement(comportement_2);\n        }}\n    }}\n";

    // Puts the lock into the door's script (actor `door` of the scene) unless it is there. Returns the scene to save (a new one when the script
    // changed) and whether it changed. A door script that is neither the standard one nor this one is refused.
    public static (SceneModel Scene, bool Changed) Apply(SceneModel scene, int sceneNumber, int door)
    {
        var scripts = SceneScripts.Load(SceneSerializer.Write(scene), sceneNumber, null, lba1: true);
        var text = scripts.GetText(door, ScriptKind.Life);
        var locked = LockedText(door);
        if (Occurrences(text, locked) == 1) return (scene, false);
        var opens = OpensText(door);
        if (Occurrences(text, opens) != 1)
            throw new InvalidDataException($"Scene {sceneNumber}, actor {door}'s life script (the bedroom door) isn't what the door lock expects; not touching it. If an earlier edit changed it, restore SCENE.HQR from the .bak copy first.");
        scripts.SetText(door, ScriptKind.Life, text.Replace(opens, locked));
        var built = scripts.Build();
        if (!built.Ok) throw new InvalidDataException($"Scene {sceneNumber}'s door script with the lock doesn't compile: {string.Join("; ", built.Errors.Select(e => e.ToString()))}");
        return (SceneSerializer.Parse(SceneGame.Lba1, built.Record!), true);
    }

    // True if the door's script has the lock.
    public static bool IsLocked(SceneModel scene, int sceneNumber, int door)
    {
        var scripts = SceneScripts.Load(SceneSerializer.Write(scene), sceneNumber, null, lba1: true);
        return Occurrences(scripts.GetText(door, ScriptKind.Life), LockedText(door)) == 1;
    }

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
