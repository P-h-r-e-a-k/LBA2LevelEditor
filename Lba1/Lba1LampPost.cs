using LBAAssembler.Scenes;

namespace LBAAssembler.Lba1;

// A street lamp at the west corner of Lupin Burg (scene 13), that gives Twinsen a key.
//
// The lamp is what Lupin Burg's other six lamps are: block 117, a single column of ten cells (1 x 10 x 1, position = layer), standing on
// the curb cap (block 135) of the platform. It goes on the corner cell of the map, x 0 z 63, where the curb runs along the platform's west
// and south edges (cap on layer 8): the block's ten cells on layers 9..18. The block is already in grid 13's used-blocks table (and its bricks
// are loaded), so the edit costs no brick memory.
//
// The key is a bonus zone (zone type 4, "giver": ZoneGiveExtraBonus in EXTRA.C): while Twinsen stands in it and presses the action key, a
// bonus pops out of the top of the zone. The zone's words are its number (unused here, 0), then Info0, which says which bonuses it may be (one bit each from bit 4: money, life, magic points, little key,
// clover leaf), Info1 how many, Info2 whether it has been taken since the scene was entered (the engine resets it on every entry: Twinsen can take
// another key on each visit). Here: only the little key (bit 7 = 128), one of it. The zone covers the platform around the lamp, on the layer
// of its cobbles.
internal static class Lba1LampPost
{
    public const int Scene = 13, Block = 117, X = 0, Z = 63, FirstLayer = 9, Layers = 10;
    public const int LittleKeyBonus = 1 << 7;

    // The lamp's ten cells.
    public static IEnumerable<Lba1GridCell> Cells()
    {
        for (var pos = 0; pos < Layers; pos++)
            yield return new Lba1GridCell(X, FirstLayer + pos, Z, Block, pos);
    }

    // The zone is centred on the lamp's foot (x 0, z 63: 32256): the key pops out of the middle of the zone at its top, and flies off towards
    // Twinsen (about a thousand units, then lands). Twinsen can only stand east or north of the lamp, so with the lamp at its centre the key
    // always flies out over the cobbles, never off the platform's corner. The zone stretches from the west edge of the map (-1023) to 1023 and
    // along the cell south of the lamp's row (31745..32767, the end of the 16-bit range); the cobbles' floor (layer 7: 8 * 256) up 2303 units
    // is the height of the lamp's post, where the key appears.
    public static SceneZoneModel Zone() => new()
    {
        X0 = -1023, Y0 = 2048, Z0 = 31745, X1 = 1023, Y1 = 2048 + 2303, Z1 = 32767,
        Type = 4, Info = new[] { 0, LittleKeyBonus, 1, 0 },   // number, which bonuses, how many, taken (the game's own giver zones read like { 0, 48, 1, 0 })
    };
    public static bool ZoneIsThere(SceneModel scene)
    {
        var z = Zone();
        return scene.Zones.Any(o => o.Type == 4 && o.X0 == z.X0 && o.Y0 == z.Y0 && o.Z0 == z.Z0 && o.X1 == z.X1 && o.Y1 == z.Y1 && o.Z1 == z.Z1 && o.Info[0] == 0 && o.Info[1] == LittleKeyBonus && o.Info[2] == 1 && o.Info[3] == 0);
    }
}
