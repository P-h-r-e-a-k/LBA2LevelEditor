namespace LBAAssembler.Lba1.Runtime;

// PERSO.C: the game's camera is a 640 x 480 screen whose centre shows one cell of the map (StartXCube / Y / Z). It stays
// put while the followed actor moves inside the screen's inner area, jumps to recentre when he leaves it, and camera zones
// pin it to a cell of their own.
internal sealed partial class Lba1Runtime
{
    public const int ScreenWidth = 640, ScreenHeight = 480, ProjectionCentreX = 320 - 8 - 1, ProjectionCentreY = 240;

    // The cell shown at the centre of the screen.
    public int CameraX { get; private set; }
    public int CameraY { get; private set; }
    public int CameraZ { get; private set; }
    public bool CameraPinned => cameraZone;

    // The screen position of a world point with the camera where it is (SetIsoProjection(311, 240, 512)).
    public (int X, int Y) ProjectToScreen(int x, int y, int z)
    {
        int rx = x - CameraX * SizeBrickXZ, ry = y - CameraY * SizeBrickY, rz = z - CameraZ * SizeBrickXZ;
        return (ProjectionCentreX + (rx - rz) * 24 / 512, ProjectionCentreY + (rx + rz) * 12 / 512 - ry * 15 / 256);
    }

    private const int SizeBrickXZ = 512, SizeBrickY = 256;

    private void CenterCameraOnHero()
    {
        var f = Objects[NumObjFollow];
        CameraX = (f.PosX + 256) / SizeBrickXZ;
        CameraY = (f.PosY + SizeBrickY) / SizeBrickY;
        CameraZ = (f.PosZ + 256) / SizeBrickXZ;
    }

    // Called every frame after the actors have moved.
    private void GereCamera()
    {
        if (cameraZone) return;
        var f = Objects[NumObjFollow];
        var (xp, yp) = ProjectToScreen(f.PosX, f.PosY, f.PosZ);
        if (xp < 80 || xp > 539 || yp < 80 || yp > 429)
        {
            int xm = (f.PosX + 256) / SizeBrickXZ, ym = f.PosY / SizeBrickY, zm = (f.PosZ + 256) / SizeBrickXZ;
            CameraX = Math.Min(63, xm + (xm - CameraX) / 2);
            CameraY = ym;
            CameraZ = Math.Min(63, zm + (zm - CameraZ) / 2);
        }
    }
}
