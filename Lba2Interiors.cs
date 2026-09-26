using System.IO;
using LBAAssembler.Grids;
using LBAAssembler.Lba1;
using LBAAssembler.Scenes;

namespace LBAAssembler;

// LBA2's interior scenes drawn by the managed grid renderer (the same one that draws LBA1's: LBA2's grids, blocks and bricks have the same
// layout): what a joined map (Lba2Areas) is made of. One scene shown alone still goes through the native engine.
internal sealed class Lba2Interiors
{
    private readonly string directory;
    private Lba2GridBackend? gridBackend;      // (LBA_BKG.HQR is big: read when a map is first drawn, not when the scenes are)
    private readonly SceneStore store;
    private readonly Dictionary<int, byte[]?> bricks = new();
    private readonly Dictionary<int, SceneModel?> scenes = new();

    public Lba2Interiors(string directory)
    {
        this.directory = directory;
        store = new SceneStore(SceneGame.Lba2, directory);
    }

    private Lba2GridBackend backend => gridBackend ??= new Lba2GridBackend(directory);

    public byte[] Palette => backend.Palette;

    // The scene (null when it isn't in SCENE.HQR).
    public SceneModel? LoadScene(int scene)
    {
        if (scenes.TryGetValue(scene, out var known)) return known;
        SceneModel? loaded = null;
        try { loaded = store.SceneExists(scene) ? store.Load(scene) : null; }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or SceneFormatException) { DebugLog.Log($"Lba2Interiors: scene {scene}: {error.Message}"); }
        scenes[scene] = loaded;
        return loaded;
    }

    public bool HasGrid(int scene) => backend.GridOfScene(scene) is not null;

    // Every filled cell of the scene's grid (block cells with their bricks).
    public List<Lba1Placement> Placements(int scene)
    {
        var grid = backend.GridOfScene(scene) ?? throw new InvalidDataException($"Scene {scene} has no interior grid.");
        return Lba1GridRenderer.Placements(backend.LoadGrid(grid), backend.LoadLibrary(grid));
    }

    // The cells a tile draws: the scene's, or the part of them its window cuts out (or leaves).
    public List<Lba1Placement> Placements(Lba1AreaTile tile)
    {
        var all = Placements(tile.Scene);
        return tile.Window is null && tile.Without is null ? all : all.Where(p => tile.Holds(p.X, p.Z)).ToList();
    }

    // A brick picture of LBA_BKG.HQR (null when there is none).
    public byte[]? ReadBrick(int index) => Brick(index);

    private byte[]? Brick(int index)
    {
        if (bricks.TryGetValue(index, out var known)) return known;
        var data = backend.Brick(index);
        bricks[index] = data;
        return data;
    }

    // ---- actors' bodies ----------------------------------------------------------------------------------------------------------------

    // An actor's body: FILE3D.HQR entry `entity` lists the bodies (F_BODY = 1: generic number, size, BODY.HQR index) and animations the entity has, and
    // the actor's own body number picks one, as SearchBody (FICHE.CPP) does; the record's length is 2 + its size byte (an animation's generic number
    // is a word, so its record is 3 + size).
    private byte[]? entityTable;
    private HqrArchive? bodyArchive;
    private readonly Dictionary<int, Dictionary<int, int>?> entities = new();

    public int? BodyIndex(int entity, int body)
    {
        if (entity < 0) return null;
        if (!entities.TryGetValue(entity, out var bodies))
        {
            bodies = null;
            try
            {
                entityTable ??= HqrArchive.Open(Path.Combine(directory, "RESS.HQR")).Read(44);
                var count = BitConverter.ToInt32(entityTable, 0) / 4 - 1;      // (the first offset is where the records start)
                if (entity < count)
                {
                    var start = BitConverter.ToInt32(entityTable, entity * 4);
                    var end = entity + 1 < count ? BitConverter.ToInt32(entityTable, (entity + 1) * 4) : entityTable.Length;
                    var d = entityTable.AsSpan(start, end - start).ToArray();
                    bodies = new Dictionary<int, int>();
                    for (var p = 0; p + 2 < d.Length && d[p] != 255;)
                    {
                        var command = d[p];
                        var size = d[p + 2];
                        if (command == 1 && p + 5 <= d.Length) bodies.TryAdd(d[p + 1], d[p + 3] | d[p + 4] << 8);
                        p += command == 3 ? 3 + d[p + 3] : 2 + size;
                    }
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
            {
                DebugLog.Log($"Lba2Interiors: entity {entity}: {error.Message}");
            }
            entities[entity] = bodies;
        }
        return bodies is not null && bodies.TryGetValue(body, out var index) ? index : null;
    }

    // A BODY.HQR entry (null when it isn't one).
    public byte[]? ReadBody(int index)
    {
        try
        {
            bodyArchive ??= HqrArchive.Open(Path.Combine(directory, "BODY.HQR"));
            return bodyArchive.IsValid(index) ? bodyArchive.Read(index) : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            DebugLog.Log($"Lba2Interiors: body {index}: {error.Message}");
            return null;
        }
    }


    // The tiles drawn at their offsets in one picture.
    public Lba1SceneImage RenderArea(IReadOnlyList<Lba1AreaTile> tiles)
        => Lba1GridRenderer.Render(tiles.Select(t => new Lba1Tile(Placements(t), t.OffsetX / 512, t.OffsetY / 256, t.OffsetZ / 512)).ToList(), Brick, Palette);
}
