using System.IO;
using LbaBodyStudio;
using LBAAssembler.Lba1;

namespace LBAAssembler.Export;

internal sealed class ExportOptions
{
    public bool IslandTerrain { get; init; } = true;
    public bool IslandObjects { get; init; } = true;
    public Action<string>? Log { get; init; }
}

// One thing that can be exported: it becomes one file (Folder / FileName + the format's extension).
internal sealed record ExportItem(string Label, string Folder, string FileName, Func<ExportOptions, ExportScene?> Build);

internal sealed record ExportCategory(string Title, string Description, Func<List<ExportItem>> Load);

// What the game folders hold that can be exported as 3D models, grouped the way a person looks for it.
internal sealed class ExportCatalog
{
    private readonly string? lba1, lba2;

    public IReadOnlyList<ExportCategory> Categories { get; }

    public ExportCatalog(string? lba1Directory, string? lba2Directory)
    {
        lba1 = lba1Directory is not null && File.Exists(Path.Combine(lba1Directory, "BODY.HQR")) ? lba1Directory : null;
        lba2 = lba2Directory is not null && File.Exists(Path.Combine(lba2Directory, "BODY.HQR")) ? lba2Directory : null;
        var list = new List<ExportCategory>();
        if (lba2 is not null)
        {
            list.Add(new("LBA2 islands (ground and every object)", "Whole islands: the textured, lit terrain of every cube plus every building, tree and prop placed on it. The Desert island includes its race track.", Lba2Islands));
            list.Add(new("LBA2 island objects (buildings, trees, props)", "The bodies of each island's .OBL file, one by one: houses, rocks, plants, furniture and the like, with the island's own textures.", Lba2IslandObjects));
            list.Add(new("LBA2 interiors (blocky maps)", "The block map of an interior scene as a model of boxes in each brick's colour (the bricks are pictures, so this is an outline of the room and its furniture).", Lba2Interiors));
            list.Add(new("LBA2 joined interiors (several rooms as one model)", "Rooms that connect (a factory's floors, the control tower and the palace ...) placed edge to edge in one model, as the joined maps show them.", Lba2JoinedInteriors));
            list.Add(new("LBA2 actors (by entity)", "Every body of every actor kind (Twinsen, Zoe, creatures, guards ...) in the neutral pose, named by entity.", Lba2Actors));
            list.Add(new("LBA2 buggy and cars", "The desert buggy (empty, with the racer, with Twinsen in it, open or closed) and the other cars: bodies of BODY.HQR found by name.", Lba2Cars));
            list.Add(new("LBA2 bodies (all of BODY.HQR)", "Every body of the archive by number.", Lba2Bodies));
            list.Add(new("LBA2 fixed objects (items, furniture, globes)", "OBJFIX.HQR: inventory items, the holomap globes and other loose objects.", Lba2Fixed));
        }
        if (lba1 is not null)
        {
            list.Add(new("LBA1 scenes (blocky maps)", "The block map of an LBA1 scene as a model of boxes in each brick's colour.", Lba1Scenes));
            list.Add(new("LBA1 joined maps (several scenes as one model)", "Scenes that continue into each other (the Citadel Island outdoors, the harbour ...) placed edge to edge in one model, as the joined maps show them.", Lba1JoinedMaps));
            list.Add(new("LBA1 actors (by entity)", "Every body of every actor kind in the neutral pose, named by entity.", Lba1Actors));
            list.Add(new("LBA1 bodies (all of BODY.HQR)", "Every body of the archive by number.", Lba1Bodies));
        }
        Categories = list;
    }

    // ---- bodies --------------------------------------------------------------------------------------------------------------------------

    // The real entries of an archive: HqrArchive.ValidIndices also walks past the end of the offset table into the entry data, where words that
    // merely look like offsets give "entries" that are not there.
    private static IEnumerable<int> Entries(HqrArchive archive, string path)
    {
        var count = HqrArchive.CountEntries(path);
        return archive.ValidIndices.Where(i => i < count);
    }

    private static byte[] Palette(string directory) => HqrArchive.Open(Path.Combine(directory, "RESS.HQR")).Read(0);

    private ExportScene? BodyScene(int game, string directory, HqrArchive archive, int index, byte[] palette, byte[]? page, bool allowStatic, string name)
    {
        var body = Body.Read(archive.Read(index), game, allowStatic || game == 2);      // (LBA2 has plain non-animated bodies in BODY.HQR too)
        body.TexturePage = page;
        var scene = new ExportScene { Name = name, ExpectedTriangles = BodyMesher.ExpectedTriangles(body) };
        scene.Add(BodyMesher.Build(scene, body, palette, name));
        return scene;
    }

    private List<ExportItem> BodiesOf(int game, string directory, string file, bool allowStatic, string folder, string prefix, string? names, Func<string?, bool>? nameFilter = null)
    {
        var archivePath = Path.Combine(directory, file);
        var archive = HqrArchive.Open(archivePath);
        var palette = Palette(directory);
        var page = game == 2 ? HqrArchive.Open(Path.Combine(directory, "RESS.HQR")).Read(6) : null;
        var described = names is null ? Array.Empty<string?>() : HqdDescriptions.Load(names, archive.Count).Names;
        var items = new List<ExportItem>();
        foreach (var i in Entries(archive, archivePath))
        {
            var index = i;
            var description = index < described.Count ? described[index] : null;
            if (nameFilter is not null && !nameFilter(description)) continue;
            items.Add(new($"{index,4}  {description}".TrimEnd(), folder, $"{prefix}_{index:D4}", _ => BodyScene(game, directory, archive, index, palette, page, allowStatic, $"{prefix}_{index}")));
        }
        return items;
    }

    private List<ExportItem> Lba1Bodies() => BodiesOf(1, lba1!, "BODY.HQR", false, "LBA1/bodies", "body", "BODY1.HQD");
    private List<ExportItem> Lba2Bodies() => BodiesOf(2, lba2!, "BODY.HQR", false, "LBA2/bodies", "body", "BODY2.HQD");
    // The buggy is an ordinary body (BODY.HQR "Empty car", "Car with racer", Twinsen in the car ...; BUGGY.CPP draws the actor with GEN_BODY_NORMAL); the wheels turning
    // is an animation, and animations are not exported.
    private List<ExportItem> Lba2Cars() => BodiesOf(2, lba2!, "BODY.HQR", false, "LBA2/buggy_and_cars", "car", "BODY2.HQD",
        name => name is not null && System.Text.RegularExpressions.Regex.IsMatch(name, @"(car|buggy)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    private List<ExportItem> Lba2Fixed() => BodiesOf(2, lba2!, "OBJFIX.HQR", true, "LBA2/fixed_objects", "objfix", null);

    private List<ExportItem> Lba1Actors()
    {
        var directory = lba1!;
        var bodies = HqrArchive.Open(Path.Combine(directory, "BODY.HQR"));
        var entitiesPath = Path.Combine(directory, "FILE3D.HQR");
        var entities = HqrArchive.Open(entitiesPath);
        var palette = Palette(directory);
        var names = HqdDescriptions.Load("FILE3D.HQD", entities.Count).Names;
        var items = new List<ExportItem>();
        foreach (var entity in Entries(entities, entitiesPath))
        {
            var e = entity;
            var d = entities.Read(e);
            var found = new List<(int Id, int Body)>();
            for (var p = 0; p + 5 <= d.Length && d[p] != 0xFF; p += 2 + Math.Max(d[p + 2], (byte)3))
                if (d[p] == 1) found.Add((d[p + 1], d[p + 3] | d[p + 4] << 8));
            var entityName = e < names.Count ? names[e] : null;
            foreach (var (id, body) in found)
            {
                var (bodyId, bodyIndex) = (id, body);
                if (!bodies.IsValid(bodyIndex)) continue;
                items.Add(new($"Entity {e}{(entityName is null ? "" : " " + entityName)}, body {bodyId}", $"LBA1/actors/entity_{e:D3}", $"entity{e}_body{bodyId}",
                    _ => BodyScene(1, directory, bodies, bodyIndex, palette, null, false, $"entity{e}_body{bodyId}")));
            }
        }
        return items;
    }

    private List<ExportItem> Lba2Actors()
    {
        var directory = lba2!;
        var table = Lba2EntityTable.Load(directory);
        if (table is null) return new();
        var bodies = HqrArchive.Open(Path.Combine(directory, "BODY.HQR"));
        var palette = Palette(directory);
        var page = HqrArchive.Open(Path.Combine(directory, "RESS.HQR")).Read(6);
        var items = new List<ExportItem>();
        foreach (var entity in table.Entities)
            foreach (var (generic, body) in entity.Bodies)
            {
                var (id, g, b) = (entity.Id, generic, body);
                if (!bodies.IsValid(b)) continue;
                items.Add(new($"Entity {id}, body {g} (BODY {b})", $"LBA2/actors/entity_{id:D3}", $"entity{id}_body{g}", _ => BodyScene(2, directory, bodies, b, palette, page, false, $"entity{id}_body{g}")));
            }
        return items;
    }

    // ---- islands --------------------------------------------------------------------------------------------------------------------------

    private List<ExportItem> Lba2Islands()
    {
        var directory = lba2!;
        return Directory.GetFiles(directory, "*.ILE").Order(StringComparer.OrdinalIgnoreCase).Select(file =>
        {
            var name = Path.GetFileNameWithoutExtension(file);
            return new ExportItem(name + (name.Equals("DESERT", StringComparison.OrdinalIgnoreCase) ? "  (Desert island: the race track)" : ""), "LBA2/islands", name.ToLowerInvariant(),
                options => IslandMesher.Build(file, directory, options.IslandTerrain, options.IslandObjects, options.Log));
        }).ToList();
    }

    private List<ExportItem> Lba2IslandObjects()
    {
        var directory = lba2!;
        var items = new List<ExportItem>();
        foreach (var obl in Directory.GetFiles(directory, "*.OBL").Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(obl);
            var ile = Path.ChangeExtension(obl, ".ILE");
            if (!File.Exists(ile)) continue;
            var archive = HqrArchive.Open(obl);
            Terrain.IslandFile? island = null;
            byte[]? palette = null;
            foreach (var i in Entries(archive, obl))
            {
                var index = i;
                items.Add(new($"{name}  #{index}", $"LBA2/island_objects/{name.ToLowerInvariant()}", $"{name.ToLowerInvariant()}_object_{index:D3}", _ =>
                {
                    island ??= Terrain.IslandFile.Load(ile);
                    palette ??= Terrain.IslandMapRenderer.LoadPalette(directory, name);
                    var body = Body.Read(archive.Read(index), 2, allowStatic: true);
                    body.TexturePage = island.ObjectTexture;
                    var scene = new ExportScene { Name = $"{name}_object_{index}", ExpectedTriangles = BodyMesher.ExpectedTriangles(body) };
                    scene.Add(BodyMesher.Build(scene, body, palette, $"{name}_object_{index}"));
                    return scene;
                }));
            }
        }
        return items;
    }

    // ---- block maps -----------------------------------------------------------------------------------------------------------------------

    private List<ExportItem> Lba1Scenes()
    {
        var directory = lba1!;
        var gridsPath = Path.Combine(directory, "LBA_GRI.HQR");
        var grids = HqrArchive.Open(gridsPath);
        var blocks = HqrArchive.Open(Path.Combine(directory, "LBA_BLL.HQR"));
        var bricks = HqrArchive.Open(Path.Combine(directory, "LBA_BRK.HQR"));
        var palette = Palette(directory);
        var names = HqdDescriptions.Load("SCENE1.HQD", 0).Names;
        var cache = new Dictionary<int, byte[]?>();
        byte[]? Brick(int index) => cache.TryGetValue(index, out var known) ? known : cache[index] = bricks.IsValid(index) ? bricks.Read(index) : null;
        var items = new List<ExportItem>();
        foreach (var i in Entries(grids, gridsPath).Where(i => i < 120 && blocks.IsValid(i)))
        {
            var scene = i;
            var description = scene < names.Count ? names[scene] : null;
            items.Add(new($"Scene {scene}{(description is null ? "" : ": " + description)}", "LBA1/scenes", $"scene_{scene:D3}", _ =>
                GridMesher.Build($"scene_{scene}", new[] { new GridMesher.Tile(Lba1GridRenderer.Placements(grids.Read(scene), blocks.Read(scene))) }, Brick, palette)));
        }
        return items;
    }

    private List<ExportItem> Lba1JoinedMaps()
    {
        var directory = lba1!;
        var game = new Lba1Game(directory);
        var grids = HqrArchive.Open(Path.Combine(directory, "LBA_GRI.HQR"));
        var blocks = HqrArchive.Open(Path.Combine(directory, "LBA_BLL.HQR"));
        var bricks = HqrArchive.Open(Path.Combine(directory, "LBA_BRK.HQR"));
        var palette = Palette(directory);
        var cache = new Dictionary<int, byte[]?>();
        byte[]? Brick(int index) => cache.TryGetValue(index, out var known) ? known : cache[index] = bricks.IsValid(index) ? bricks.Read(index) : null;
        var items = new List<ExportItem>();
        for (var a = 0; a < game.Areas.Count; a++)
        {
            var area = game.Areas[a];
            if (area.Tiles.Count < 2) continue;
            var number = a;
            var island = area.Island >= 0 && area.Island < Lba1Game.IslandNames.Length ? Lba1Game.IslandNames[area.Island] : $"island {area.Island}";
            items.Add(new($"{island}: {area.Name} (scenes {string.Join(", ", area.Tiles.Select(t => t.Scene).Distinct())})", "LBA1/joined_maps", $"area_{number:D2}_{SceneWriters.Safe(area.Name.ToLowerInvariant())}", _ =>
                GridMesher.Build($"area_{number}", area.Tiles.Select(t => new GridMesher.Tile(Lba1GridRenderer.Placements(grids.Read(t.Scene), blocks.Read(t.Scene)).Where(p => t.Holds(p.X, p.Z)).ToList(), t.OffsetX, t.OffsetY, t.OffsetZ)), Brick, palette)));
        }
        return items;
    }

    private List<ExportItem> Lba2JoinedInteriors()
    {
        var directory = lba2!;
        var interiors = new LBAAssembler.Lba2Interiors(directory);
        var areas = Lba2Areas.Find(interiors.LoadScene);
        var items = new List<ExportItem>();
        for (var a = 0; a < areas.Count; a++)
        {
            var area = areas[a];
            var number = a;
            items.Add(new($"{area.Name} (scenes {string.Join(", ", area.Tiles.Select(t => t.Scene).Distinct())})", "LBA2/joined_interiors", $"area_{number:D2}_{SceneWriters.Safe(area.Name.ToLowerInvariant())}", _ =>
                GridMesher.Build($"area_{number}", area.Tiles.Select(t => new GridMesher.Tile(interiors.Placements(t), t.OffsetX, t.OffsetY, t.OffsetZ)), interiors.ReadBrick, interiors.Palette)));
        }
        return items;
    }

    private List<ExportItem> Lba2Interiors()
    {
        var directory = lba2!;
        var interiors = new LBAAssembler.Lba2Interiors(directory);
        var items = new List<ExportItem>();
        foreach (var (id, label) in Lba2SceneList.Load(directory))
        {
            var scene = id;
            bool has;
            try { has = interiors.HasGrid(scene); } catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { has = false; }
            if (!has) continue;
            items.Add(new(label, "LBA2/interiors", $"interior_{scene:D3}", _ =>
                GridMesher.Build($"interior_{scene}", new[] { new GridMesher.Tile(interiors.Placements(scene)) }, interiors.ReadBrick, interiors.Palette)));
        }
        return items;
    }
}

// Runs a batch of exports, one file each; a failure of one item is logged and the rest go on.
internal static class ExportRunner
{
    public static (int Done, int Failed, int Skipped) Run(IReadOnlyList<ExportItem> items, ExportFormat format, float scale, string outputDirectory, ExportOptions options,
        IProgress<(int Index, string Message)> progress, CancellationToken cancel)
    {
        int done = 0, failed = 0, skipped = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (cancel.IsCancellationRequested) break;
            var item = items[i];
            try
            {
                var scene = item.Build(options);
                if (scene is not null && scene.TriangleCount == 0 && scene.ExpectedTriangles == 0)
                { progress.Report((i, $"{item.Label}: skipped, the game's own body is empty (points but no polygons).")); skipped++; continue; }
                if (scene is null || scene.TriangleCount == 0) { progress.Report((i, $"{item.Label}: nothing to export (no geometry).")); failed++; continue; }
                if (scene.Problem() is { } problem) { progress.Report((i, $"{item.Label}: NOT EXPORTED, the geometry is wrong ({problem}).")); failed++; continue; }
                var path = Path.Combine(outputDirectory, item.Folder, SceneWriters.Safe(item.FileName) + SceneWriters.Extension(format));
                SceneWriters.Write(scene, path, format, scale);
                done++;
                progress.Report((i, $"{item.Label}: {scene.TriangleCount:N0} triangles -> {Path.GetRelativePath(outputDirectory, path)}"));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or OverflowException)
            {
                failed++;
                DebugLog.Log($"Export: {item.Label}: {error}");
                progress.Report((i, $"{item.Label}: failed ({error.Message})"));
            }
        }
        return (done, failed, skipped);
    }
}
