using System.IO;

namespace LBAAssembler.Lba1;

internal sealed record Lba1SceneInfo(int Index, int Island, int ActorCount, int ZoneCount, IReadOnlyList<int> Exits);

// Read access to a Little Big Adventure 1 install: the scene list grouped by island,
// scene records, and the isometric map of each scene.
internal sealed class Lba1Game
{
    // The scene record's first byte is the island (the game's text bank), in this order. The
    // Hamalayi Mountains use two banks. Checked against LBAPackageManager's SCENE1.HQD descriptions.
    public static readonly string[] IslandNames =
    {
        "Citadel Island", "Principal Island", "White Leaf Desert", "Proxima Island", "Rebellion Island",
        "Hamalayi Mountains (1)", "Hamalayi Mountains (2)", "Tippet Island", "Brundle Island", "Fortress Island", "Polar Island",
    };

    private readonly string directory;
    private readonly Dictionary<int, Lba1Scene> sceneCache = new();
    private IReadOnlyList<string?> descriptions = Array.Empty<string?>();
    private readonly HqrArchive sceneArchive;
    private readonly HqrArchive gridArchive;
    private readonly HqrArchive blockArchive;
    private readonly HqrArchive brickArchive;
    private readonly Dictionary<int, byte[]?> brickCache = new();

    public byte[] Palette { get; }
    public IReadOnlyList<Lba1SceneInfo> Scenes { get; }
    public IReadOnlyList<Lba1Area> Areas { get; }

    // LBAPackageManager's description of a scene ("Citadel Island, Prison"), or null.
    // LBAPackageManager's SCENE1.HQD has the two Proxima rune stones the wrong way round (and a typo in 47).
    private static readonly Dictionary<int, string> DescriptionOverrides = new()
    {
        [45] = "Proxima Island, lower rune stone",
        [46] = "Proxima Island, upper rune stone",
        [47] = "Proxima Island, before the upper rune stone",
    };

    public string? Description(int scene)
        => DescriptionOverrides.TryGetValue(scene, out var fixedName) ? fixedName
         : scene >= 0 && scene < descriptions.Count ? descriptions[scene] : null;

    public static bool IsInstalled(string directory)
        => Directory.Exists(directory)
           && new[] { "SCENE.HQR", "LBA_GRI.HQR", "LBA_BLL.HQR", "LBA_BRK.HQR", "RESS.HQR" }.All(f => File.Exists(Path.Combine(directory, f)));

    public Lba1Game(string directory)
    {
        this.directory = directory;
        sceneArchive = HqrArchive.Open(Path.Combine(directory, "SCENE.HQR"));
        gridArchive = HqrArchive.Open(Path.Combine(directory, "LBA_GRI.HQR"));
        blockArchive = HqrArchive.Open(Path.Combine(directory, "LBA_BLL.HQR"));
        brickArchive = HqrArchive.Open(Path.Combine(directory, "LBA_BRK.HQR"));
        Palette = HqrArchive.Open(Path.Combine(directory, "RESS.HQR")).Read(0);

        var scenes = new List<Lba1SceneInfo>();
        foreach (var index in sceneArchive.ValidIndices.Where(i => i < sceneArchive.Count / 4))
        {
            try
            {
                var scene = Lba1Scene.Parse(index, sceneArchive.Read(index));
                sceneCache[index] = scene;
                var exits = scene.Zones.Where(z => z.Type == 0 && z.Info[0] != index).Select(z => z.Info[0]).Distinct().Order().ToList();
                scenes.Add(new Lba1SceneInfo(index, Lba1Areas.MapIsland(index, scene.Island), scene.Actors.Count - 1, scene.Zones.Count, exits));
            }
            catch (Exception error)
            {
                DebugLog.Log($"Lba1Game: scene {index} didn't parse: {error.Message}");
            }
        }
        Scenes = scenes;
        Areas = Lba1Areas.Find(sceneCache);
        descriptions = HqdDescriptions.Load("SCENE1.HQD", sceneArchive.Count / 4).Names;
    }

    public Lba1Scene LoadScene(int index) => sceneCache.TryGetValue(index, out var scene) ? scene : Lba1Scene.Parse(index, sceneArchive.Read(index));

    // Scene N is drawn from grid N with block library N.
    public Lba1SceneImage RenderScene(int index)
        => Lba1GridRenderer.Render(gridArchive.Read(index), blockArchive.Read(index), Brick, Palette);

    // Draws `grid` (a scene's grid entry, possibly edited and not saved yet) with the block library of scene `scene`.
    public Lba1SceneImage RenderGrid(int scene, byte[] grid)
        => Lba1GridRenderer.Render(grid, blockArchive.Read(scene), Brick, Palette);

    // Draws a map given as decoded cells (what the play mode has after grid fragments changed it) with scene `scene`'s blocks.
    public Lba1SceneImage RenderCells(int scene, byte[] cells)
    {
        var blocks = blockArchive.Read(scene);
        return Lba1GridRenderer.Render(new[] { new Lba1Tile(Lba1GridRenderer.PlacementsOfCells(cells, blocks), 0, 0, 0) }, Brick, Palette);
    }

    // For each column (index x + z * 64) of a scene's grid: the highest occupied layer (-1 if the
    // column is empty) and the brick drawn there. Used to check that two grids continue into each other.
    public (int[] TopY, int[] TopBrick) ColumnTops(int scene)
    {
        var top = new int[64 * 64];
        var brick = new int[64 * 64];
        Array.Fill(top, -1);
        foreach (var p in Lba1GridRenderer.Placements(gridArchive.Read(scene), blockArchive.Read(scene)))
        {
            var i = p.X + p.Z * 64;
            if (p.Y >= top[i]) { top[i] = p.Y; brick[i] = p.Brick; }
        }
        return (top, brick);
    }

    // Several scenes joined into one map (Areas): each drawn at its offset in the shared grid.
    public Lba1SceneImage RenderArea(Lba1Area area)
        => Lba1GridRenderer.Render(
            area.Tiles.Select(t => new Lba1Tile(Lba1GridRenderer.Placements(gridArchive.Read(t.Scene), blockArchive.Read(t.Scene)), t.OffsetX / 512, t.OffsetY / 256, t.OffsetZ / 512)).ToList(),
            Brick, Palette);

    // The raw grid / block library of a scene and a brick sprite, for the grid inspector and editor.
    public byte[] ReadGrid(int scene) => gridArchive.Read(scene);
    public byte[] ReadBlocks(int scene) => blockArchive.Read(scene);
    public byte[]? ReadBrick(int index) => Brick(index);

    private byte[]? Brick(int index)
    {
        if (brickCache.TryGetValue(index, out var cached)) return cached;
        var data = brickArchive.IsValid(index) ? brickArchive.Read(index) : null;
        brickCache[index] = data;
        return data;
    }

    // ---- entities (FILE3D.HQR) -------------------------------------------------
    // An entity lists its bodies and animations as records of (type, id, size, HQR index...):
    // type 1 is a body (BODY.HQR), type 3 an animation (ANIM.HQR); 0xFF ends the list.
    private sealed record Entity(Dictionary<int, int> Bodies, Dictionary<int, int> Anims);

    private HqrArchive? entityArchive;
    private HqrArchive? bodyArchive;
    private readonly Dictionary<int, Entity?> entities = new();

    private Entity? GetEntity(int index)
    {
        if (entities.TryGetValue(index, out var cached)) return cached;
        Entity? entity = null;
        try
        {
            entityArchive ??= HqrArchive.Open(Path.Combine(directory, "FILE3D.HQR"));
            if (entityArchive.IsValid(index))
            {
                var d = entityArchive.Read(index);
                var bodies = new Dictionary<int, int>();
                var anims = new Dictionary<int, int>();
                var p = 0;
                while (p + 5 <= d.Length && d[p] != 0xFF)
                {
                    int type = d[p], id = d[p + 1], size = d[p + 2];
                    var hqr = d[p + 3] | d[p + 4] << 8;
                    if (type == 1) bodies[id] = hqr; else if (type == 3) anims[id] = hqr;
                    p += 2 + Math.Max(size, 3);
                }
                entity = new Entity(bodies, anims);
            }
        }
        catch (Exception error)
        {
            DebugLog.Log($"Lba1Game: entity {index} didn't parse: {error.Message}");
        }
        entities[index] = entity;
        return entity;
    }

    // BODY.HQR / ANIM.HQR indices for an actor's (entity, body variant, animation), or null.
    public int? BodyIndex(int entity, int body) => GetEntity(entity) is { } e && e.Bodies.TryGetValue(body, out var i) ? i : null;
    public int? AnimIndex(int entity, int anim) => GetEntity(entity) is { } e && e.Anims.TryGetValue(anim, out var i) ? i : null;

    // Number of FILE3D.HQR entities.
    public int EntityCount => HqrArchive.CountEntries(Path.Combine(directory, "FILE3D.HQR"));

    // The bodies (id -> BODY.HQR index) and animations (id -> ANIM.HQR index) an entity offers.
    public IReadOnlyDictionary<int, int> EntityBodies(int entity) => GetEntity(entity)?.Bodies ?? new Dictionary<int, int>();
    public IReadOnlyDictionary<int, int> EntityAnims(int entity) => GetEntity(entity)?.Anims ?? new Dictionary<int, int>();

    // LBAPackageManager's descriptions: one line per FILE3D entity / BODY.HQR entry / ANIM.HQR entry.
    private IReadOnlyList<string?>? entityNames, bodyNames, animNames;
    public string? EntityName(int entity) => Name(ref entityNames, "FILE3D.HQD", entity);
    public string? BodyName(int bodyIndex) => Name(ref bodyNames, "BODY1.HQD", bodyIndex);
    public string? AnimName(int animIndex) => Name(ref animNames, "ANIM1.HQD", animIndex);

    private static string? Name(ref IReadOnlyList<string?>? cache, string file, int index)
    {
        cache ??= HqdDescriptions.Load(file, 0).Names;
        return index >= 0 && index < cache.Count ? cache[index] : null;
    }

    private HqrArchive? animArchive;
    private readonly Dictionary<int, Lba1Animation?> animations = new();

    // A parsed ANIM.HQR entry (null when it can't be read).
    public Lba1Animation? Animation(int animIndex)
    {
        if (animations.TryGetValue(animIndex, out var cached)) return cached;
        Lba1Animation? animation = null;
        try
        {
            animArchive ??= HqrArchive.Open(Path.Combine(directory, "ANIM.HQR"));
            if (animArchive.IsValid(animIndex)) animation = Lba1Animation.Parse(animArchive.Read(animIndex));
        }
        catch (Exception error)
        {
            DebugLog.Log($"Lba1Game: animation {animIndex} didn't read: {error.Message}");
        }
        animations[animIndex] = animation;
        return animation;
    }

    public byte[]? ReadBody(int bodyIndex)
    {
        try
        {
            bodyArchive ??= HqrArchive.Open(Path.Combine(directory, "BODY.HQR"));
            return bodyArchive.IsValid(bodyIndex) ? bodyArchive.Read(bodyIndex) : null;
        }
        catch (Exception error)
        {
            DebugLog.Log($"Lba1Game: body {bodyIndex} didn't read: {error.Message}");
            return null;
        }
    }
}
