using LBAAssembler.Lba1.Runtime;
using LBAAssembler.Scenes;

namespace LBAAssembler.Lba1;

// A blank scene for an existing slot: an empty world with a flat floor to stand on and Twinsen standing on it, nothing
// else. Scenes can't be added to the game, so "new" means "replace this slot"; the slot keeps its island, text bank,
// music, light and block library (LBA_BLL entry N belongs to scene N), which is where the floor comes from: the slot's own
// most common single-cell solid floor block on the ground layer.
internal static class Lba1BlankScene
{
    public const int FloorFirst = 16, FloorLast = 47;      // the floor covers cells 16..47 in x and z (32 x 32)
    private const int TableSize = 64 * 64 * 2, UsedBlocksSize = 32;

    // A grid entry with no cell in use: every column is the same 25 empty cells.
    public static byte[] EmptyGrid()
    {
        var bytes = new List<byte>(TableSize + 1 + UsedBlocksSize);
        for (var i = 0; i < 64 * 64; i++) { bytes.Add(unchecked((byte)TableSize)); bytes.Add((byte)(TableSize >> 8)); }
        bytes.Add(1);                                       // one run ...
        bytes.Add(0x18);                                    // ... that skips 25 cells
        bytes.AddRange(new byte[UsedBlocksSize]);           // no block is used
        return bytes.ToArray();
    }

    // The (block, position) pair the slot's own map uses most for its ground: a block of one solid cell.
    public static (int Block, int Pos)? PickFloor(byte[] grid, byte[] library)
    {
        var cube = new Lba1Cube(grid, library);
        var counts = new Dictionary<(int, int), int>();
        for (var z = 0; z < 64; z++)
            for (var x = 0; x < 64; x++)
            {
                var (block, pos) = cube.Cell(x, 0, z);
                if (block == 0 || cube.ExtentOf(block) != 1 || cube.ShapeOf(block, pos) != 1 || !Plain(cube, block)) continue;
                counts[(block, pos)] = counts.GetValueOrDefault((block, pos)) + 1;
            }
        if (counts.Count > 0) return counts.OrderByDescending(c => c.Value).First().Key;
        for (var block = 1; block <= cube.BlockCount; block++)
            if (cube.ExtentOf(block) == 1 && cube.ShapeOf(block, 0) == 1 && Plain(cube, block)) return (block, 0);
        return null;
    }

    // Ground that is just ground: game codes 0xF1..0xFF are water and other special surfaces (WorldCodeBrick).
    private static bool Plain(Lba1Cube cube, int block)
    {
        var code = cube.GameCodeOf(block);
        return code == 0xF0 || (code & 0xF0) != 0xF0;
    }

    public static (SceneModel Scene, byte[] Grid) Create(SceneStore store, int slot)
    {
        if (store.Game != SceneGame.Lba1) throw new SceneEditException("Blank scenes are only made for LBA1 so far.");
        var old = store.Load(slot);
        var floor = PickFloor(store.LoadGrid(slot), store.LoadLibrary(slot))
            ?? throw new SceneEditException($"Scene {slot}'s block library has no plain solid floor block to build with.");

        var cells = new List<Lba1GridCell>();
        for (var z = FloorFirst; z <= FloorLast; z++)
            for (var x = FloorFirst; x <= FloorLast; x++)
                cells.Add(new Lba1GridCell(x, 0, z, floor.Block, floor.Pos));
        var grid = Lba1GridEdit.SetCells(EmptyGrid(), cells);

        var middle = (FloorFirst + FloorLast + 1) / 2 * 512;
        var scene = new SceneModel
        {
            Game = SceneGame.Lba1,
            Island = old.Island, GameOverScene = old.GameOverScene, AlphaLight = old.AlphaLight, BetaLight = old.BetaLight,
            Music = old.Music, SecondMin = old.SecondMin, SecondEcart = old.SecondEcart,
            Ambient = old.Ambient.Select(a => a.Clone()).ToArray(),
            Reserved1 = old.Reserved1, Reserved2 = old.Reserved2,
        };
        // the hero: the plain record 9 retail scenes use (an END in each script)
        scene.Actors.Add(new SceneActorModel { Flags = 0, Entity = 0, Body = 0, Anim = 0, X = middle, Y = 256, Z = middle, Life = new byte[] { 0 }, Track = new byte[] { 0 } });
        return (scene, grid);
    }
}
