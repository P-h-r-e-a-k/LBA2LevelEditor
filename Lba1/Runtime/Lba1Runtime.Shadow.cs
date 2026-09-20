using static LBA2LevelEditor.Lba1.Runtime.Lba1Const;

namespace LBA2LevelEditor.Lba1.Runtime;

internal sealed partial class Lba1Runtime
{
    // OBJECT.C GetShadow: where an actor's shadow lies, on the top of the first occupied cell below him (following a
    // ramp's slope); an actor carried by another has it just under his feet. Null when he casts none.
    public (int X, int Y, int Z)? ShadowOf(Lba1Object o)
    {
        if (o.Body == -1 || o.IsSprite || (o.Flags & (NoShadow | Invisible)) != 0 || o.IsDead) return null;
        if (o.CarryBy != -1) return (o.PosX, o.PosY - 1, o.PosZ);

        int xm = (o.PosX + DemiBrickXZ) / SizeBrickXZ, ym = o.PosY / SizeBrickY, zm = (o.PosZ + DemiBrickXZ) / SizeBrickXZ;
        if (xm < 0 || xm > 63 || zm < 0 || zm > 63) return null;
        ym = Math.Clamp(ym, 0, 24);
        var y = ym;
        for (; y > 0; y--)
        {
            var (block, second) = Cube.Cell(xm, y, zm);
            if (block != 0 || second != 0) break;
        }
        xMap = xm; yMap = y; zMap = zm;
        nxw = o.PosX; nyw = (y + 1) * SizeBrickY; nzw = o.PosZ;
        var (b, pos) = Cube.Cell(xm, y, zm);
        var shape = b != 0 ? Cube.ShapeOf(b, pos) : 0;
        ReajustPos(shape);
        return (nxw, nyw, nzw);
    }
}
