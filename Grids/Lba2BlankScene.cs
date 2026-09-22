using LBAAssembler.Lba1;
using LBAAssembler.Lba1.Runtime;
using LBAAssembler.Scenes;

namespace LBAAssembler.Grids;

// A blank LBA2 interior for an existing slot: an empty world with a flat floor to stand on and Twinsen standing on it. Scenes can't be added to the game, so "new" means
// "replace this slot"; the slot keeps its header (light, music, ambience, the interior's block library and style, the hero's own scripts) and loses its actors, zones and
// track points; its grid becomes 32 x 32 cells of the library's most used plain floor block.
internal static class Lba2BlankScene
{
    public const int FloorFirst = 16, FloorLast = 47;
    private const int TableSize = 64 * 64 * 2, UsedBlocksSize = 32;

    // A grid in the LBA1 shape with no cell in use.
    public static byte[] EmptyGrid()
    {
        var bytes = new List<byte>(TableSize + 1 + UsedBlocksSize);
        for (var i = 0; i < 64 * 64; i++) { bytes.Add(unchecked((byte)TableSize)); bytes.Add((byte)(TableSize >> 8)); }
        bytes.Add(1); bytes.Add(0x18);
        bytes.AddRange(new byte[UsedBlocksSize]);
        return bytes.ToArray();
    }

    // The floor block and the layer it is used on: the (block, position) of one solid cell that the slot's own grid uses most as ground.
    public static (int Block, int Pos, int Layer)? PickFloor(byte[] grid, byte[] library)
    {
        var cube = new Lba1Cube(grid, library);
        var counts = new Dictionary<(int, int, int), int>();
        for (var y = 0; y < 4; y++)
            for (var z = 0; z < 64; z++)
                for (var x = 0; x < 64; x++)
                {
                    var (block, pos) = cube.Cell(x, y, z);
                    if (block == 0 || cube.ExtentOf(block) != 1 || cube.ShapeOf(block, pos) != 1) continue;
                    // a floor: solid with nothing solid in the cell above it
                    var above = cube.Cell(x, y + 1, z);
                    if (above.Block != 0) continue;
                    counts[(block, pos, y)] = counts.GetValueOrDefault((block, pos, y)) + 1;
                }
        if (counts.Count > 0) { var best = counts.OrderByDescending(c => c.Value).First().Key; return (best.Item1, best.Item2, best.Item3); }
        for (var block = 1; block <= cube.BlockCount; block++)
            if (cube.ExtentOf(block) == 1 && cube.ShapeOf(block, 0) == 1) return (block, 0, 0);
        return null;
    }

    public static (SceneModel Scene, byte[] Grid) Create(SceneStore store, Lba2GridBackend backend, int slot)
    {
        if (store.Game != SceneGame.Lba2) throw new SceneEditException("This makes blank LBA2 interiors.");
        var old = store.Load(slot);
        if (old.CubeMode != 0) throw new SceneEditException($"Scene {slot} is an island scene; only interiors have a grid to make blank.");
        var gridId = backend.GridOfScene(slot) ?? throw new SceneEditException($"Scene {slot} has no interior grid.");
        var floor = PickFloor(backend.LoadGrid(gridId), backend.LoadLibrary(gridId))
            ?? throw new SceneEditException($"The block library of scene {slot}'s interior has no solid floor block to build with.");

        var cells = new List<Lba1GridCell>();
        for (var z = FloorFirst; z <= FloorLast; z++)
            for (var x = FloorFirst; x <= FloorLast; x++)
                cells.Add(new Lba1GridCell(x, floor.Layer, z, floor.Block, floor.Pos));
        var grid = Lba1GridEdit.SetCells(EmptyGrid(), cells);

        var scene = old.Clone();
        // everything but the hero goes at once (deleting one by one would trip over the scripts that point at each other)
        scene.Actors.RemoveRange(1, scene.Actors.Count - 1);
        scene.Zones.Clear();
        scene.TrackPoints.Clear();
        var middle = (FloorFirst + FloorLast + 1) / 2 * 512;
        scene.Hero.X = middle; scene.Hero.Z = middle; scene.Hero.Y = (floor.Layer + 1) * 256;
        return (scene, grid);
    }
}
