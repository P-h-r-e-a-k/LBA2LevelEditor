using System.Buffers.Binary;
using System.IO;
using LBAAssembler.Lba1;
using LBAAssembler.LbaScript;

namespace LBAAssembler;

// Identifies one zone: game (1 or 2), scene (LBA1 scene number / LBA2 numscene) and its position
// in that scene's zone list.
internal sealed record ZoneRef(int Game, int Scene, int Index);

// A scene zone (trigger box) as stored in SCENE.HQR, in the scene's own coordinates.
//   LBA1: six S16 coordinates, U16 type, four S16 info words, S16 snap (24 bytes).
//   LBA2: six S32 coordinates, eight S32 info words, S16 type, S16 num (60 bytes); see docs/ZONES.md.
// Cube changes (type 0) name the scene they lead to: LBA1 in Info[0], LBA2 in Num.
internal sealed class ZoneData
{
    public required int Game { get; init; }
    public required int Scene { get; init; }
    public required int Index { get; init; }
    public int X0 { get; set; }
    public int Y0 { get; set; }
    public int Z0 { get; set; }
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int Z1 { get; set; }
    public int Type { get; set; }
    public int Num { get; set; }
    public int[] Info { get; set; } = Array.Empty<int>();
    public int Snap { get; set; }

    public ZoneRef Ref => new(Game, Scene, Index);
    public int SizeX => Math.Abs(X1 - X0);
    public int SizeY => Math.Abs(Y1 - Y0);
    public int SizeZ => Math.Abs(Z1 - Z0);

    // The scene a cube change leads to.
    public int Destination
    {
        get => Game == 1 ? Info[0] : Num;
        set { if (Game == 1) Info[0] = value; else Num = value; }
    }

    public ZoneData Clone()
        => new() { Game = Game, Scene = Scene, Index = Index, X0 = X0, Y0 = Y0, Z0 = Z0, X1 = X1, Y1 = Y1, Z1 = Z1, Type = Type, Num = Num, Info = (int[])Info.Clone(), Snap = Snap };
}

internal static class SceneZones
{
    private const int Lba2ZoneSize = 60;

    public static string HqrPath(int game)
        => Path.Combine(game == 1 ? EditorSettings.Current.Lba1Directory : EditorSettings.Current.GameDirectory, "SCENE.HQR");

    // SCENE.HQR entry of a scene: LBA1 stores scene N in entry N, LBA2 in entry N + 1 (entry 0 is a size record).
    private static int Entry(int game, int scene) => game == 1 ? scene : scene + 1;

    public static byte[] ReadRecord(int game, int scene) => HqrArchive.Open(HqrPath(game)).Read(Entry(game, scene));

    public static List<ZoneData> Parse(int game, int scene, byte[] record)
    {
        var zones = new List<ZoneData>();
        if (game == 1)
        {
            var parsed = Lba1Scene.Parse(scene, record);
            for (var i = 0; i < parsed.Zones.Count; i++)
            {
                var z = parsed.Zones[i];
                zones.Add(new ZoneData
                {
                    Game = 1, Scene = scene, Index = i, X0 = z.X0, Y0 = z.Y0, Z0 = z.Z0, X1 = z.X1, Y1 = z.Y1, Z1 = z.Z1,
                    Type = z.Type, Info = (int[])z.Info.Clone(), Snap = z.Snap,
                });
            }
            return zones;
        }

        var raw = SceneRecord.Parse(record);
        var p = raw.TailPos + 4;                        // checksum
        var count = BinaryPrimitives.ReadInt16LittleEndian(record.AsSpan(p));
        p += 2;
        for (var i = 0; i < count; i++)
        {
            var q = p + i * Lba2ZoneSize;
            int S32(int at) => BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(q + at));
            var info = new int[8];
            for (var k = 0; k < 8; k++) info[k] = S32(24 + k * 4);
            zones.Add(new ZoneData
            {
                Game = 2, Scene = scene, Index = i, X0 = S32(0), Y0 = S32(4), Z0 = S32(8), X1 = S32(12), Y1 = S32(16), Z1 = S32(20),
                Info = info,
                Type = BinaryPrimitives.ReadInt16LittleEndian(record.AsSpan(q + 56)),
                Num = BinaryPrimitives.ReadInt16LittleEndian(record.AsSpan(q + 58)),
            });
        }
        return zones;
    }

    // A copy of `record` with the zone's fields overwritten (the record's size never changes).
    public static byte[] Patch(ZoneData zone, byte[] record)
    {
        var result = (byte[])record.Clone();
        if (zone.Game == 1)
        {
            var parsed = Lba1Scene.Parse(zone.Scene, record);
            if (zone.Index >= parsed.ZoneOffsets.Count) throw new InvalidDataException("That zone is no longer in the scene.");
            var p = parsed.ZoneOffsets[zone.Index];
            void S16(int value, string what)
            {
                if (value < short.MinValue || value > short.MaxValue) throw new InvalidDataException($"{what} must fit 16 bits (-32768..32767).");
                BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(p), (short)value);
                p += 2;
            }
            S16(zone.X0, "X min"); S16(zone.Y0, "Y min"); S16(zone.Z0, "Z min");
            S16(zone.X1, "X max"); S16(zone.Y1, "Y max"); S16(zone.Z1, "Z max");
            S16(zone.Type, "Type");
            foreach (var info in zone.Info) S16(info, "Info value");
            S16(zone.Snap, "Snap");
            return result;
        }

        var raw = SceneRecord.Parse(record);
        var start = raw.TailPos + 4;
        var count = BinaryPrimitives.ReadInt16LittleEndian(record.AsSpan(start));
        if (zone.Index >= count) throw new InvalidDataException("That zone is no longer in the scene.");
        var q = start + 2 + zone.Index * Lba2ZoneSize;
        void S32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(q + at), value);
        S32(0, zone.X0); S32(4, zone.Y0); S32(8, zone.Z0); S32(12, zone.X1); S32(16, zone.Y1); S32(20, zone.Z1);
        for (var k = 0; k < 8; k++) S32(24 + k * 4, zone.Info[k]);
        if (zone.Type < short.MinValue || zone.Type > short.MaxValue || zone.Num < short.MinValue || zone.Num > short.MaxValue)
            throw new InvalidDataException("Type and scene/number must fit 16 bits.");
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(q + 56), (short)zone.Type);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(q + 58), (short)zone.Num);
        return result;
    }

    // Writes the edited zone into the scene's record in SCENE.HQR. The first save keeps the untouched
    // file as SCENE.HQR.bak; the new file is written beside the original, checked, then swapped in.
    public static void Save(ZoneData zone) => SaveRecord(zone.Game, zone.Scene, record => Patch(zone, record), $"Edit zone {zone.Index} of scene {zone.Scene}");

    // Rewrites one scene's record in SCENE.HQR with `patch` applied to the current record. The result goes through
    // SceneStore: validated against the engine's limits, LBA2's patch table and largest-scene record kept true,
    // written as one verified transaction (first-time .bak, written beside, swapped in), and put on the undo log.
    public static void SaveRecord(int game, int scene, Func<byte[], byte[]> patch, string? description = null)
    {
        var sceneGame = game == 1 ? Scenes.SceneGame.Lba1 : Scenes.SceneGame.Lba2;
        var store = new Scenes.SceneStore(sceneGame, Path.GetDirectoryName(HqrPath(game))!);
        var updatedRecord = patch(store.LoadRecord(scene));
        store.Save(scene, Scenes.SceneSerializer.Parse(sceneGame, updatedRecord), description: description ?? $"Edit scene {scene}");
    }
}
