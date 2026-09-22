namespace LBAAssembler.Terrain;

internal sealed record IslandProblem(bool IsError, string Message);

// What the game would trip over in an island (errors) and what merely looks wrong (notes).
internal static class IslandValidator
{
    public const int MaxCubes = 20;   // MAX_CUBES_PER_ISLE in the engine

    public static List<IslandProblem> Validate(IslandFile island, int? oblBodyCount = null)
    {
        var problems = new List<IslandProblem>();
        for (var i = 0; i < island.Map.Length; i++)
        {
            var id = island.Map[i] & 0x7F;
            if (id != 0 && !island.Cubes.ContainsKey(id)) problems.Add(new IslandProblem(true, $"map cell ({i % 16}, {i / 16}) shows cube {id}, which has no data"));
        }
        if (island.Cubes.Count > MaxCubes) problems.Add(new IslandProblem(true, $"{island.Cubes.Count} cubes: the engine keeps at most {MaxCubes} per island"));
        foreach (var (id, cube) in island.Cubes)
        {
            if (cube.Decors.Count > IslandDecors.MaxPerCube) problems.Add(new IslandProblem(true, $"cube {id} holds {cube.Decors.Count} objects (the engine's limit is {IslandDecors.MaxPerCube})"));
            var defs = cube.TextureDefs.Length / 6;
            if (cube.HasPolygons)
            {
                var bad = 0;
                foreach (var raw in cube.Polygons)
                {
                    var p = new IslandPolygon(raw);
                    if (p.TexFlag != 0 && p.TextureIndex >= defs) bad++;
                }
                if (bad > 0) problems.Add(new IslandProblem(true, $"cube {id}: {bad} triangles point past the end of the texture list ({defs} definitions)"));
            }
            foreach (var d in cube.Decors)
            {
                if (oblBodyCount is { } count && (d.Body & 0xFFFF) >= count) problems.Add(new IslandProblem(true, $"cube {id}: an object uses body {d.Body & 0xFFFF}, but the island's OBL has {count} bodies"));
                if (d.X < 0 || d.X > 32767 || d.Z < 0 || d.Z > 32767) problems.Add(new IslandProblem(false, $"cube {id}: an object at ({d.X}, {d.Z}) lies outside its cube"));
                if (d.XMax < d.XMin || d.YMax < d.YMin || d.ZMax < d.ZMin) problems.Add(new IslandProblem(false, $"cube {id}: an object at ({d.X}, {d.Y}, {d.Z}) has an inverted bounding box"));
            }
        }
        int seams = 0;
        for (var gz = 0; gz <= IslandFile.GridSize; gz += 1)
        for (var gx = 0; gx <= IslandFile.GridSize; gx += 1)
        {
            if (gx % IslandCube.Cells != 0 && gz % IslandCube.Cells != 0) continue;
            var owners = island.Owners(gx, gz).ToList();
            if (owners.Count > 1 && owners.Select(o => o.Cube.Height(o.X, o.Z)).Distinct().Count() > 1) seams++;
        }
        if (seams > 0) problems.Add(new IslandProblem(false, $"{seams} vertices on cube borders differ between the neighbouring cubes (visible cracks; \"Weld cube borders\" makes them agree)"));
        return problems;
    }
}
