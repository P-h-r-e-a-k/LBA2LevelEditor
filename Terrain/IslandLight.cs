namespace LBA2LevelEditor.Terrain;

// The lighting maths shared by the shadow tools and the tests.
internal static class IslandLight
{
    // Per-vertex surface normals of a cube's heightmap (central differences; cell size 512 world units).
    public static (double X, double Y, double Z)[] Normals(IslandCube cube)
    {
        var normals = new (double, double, double)[IslandCube.Vertices * IslandCube.Vertices];
        for (var z = 0; z < IslandCube.Vertices; z++)
        for (var x = 0; x < IslandCube.Vertices; x++)
        {
            double H(int px, int pz) => cube.Height(Math.Clamp(px, 0, 64), Math.Clamp(pz, 0, 64));
            var dhx = (H(x + 1, z) - H(x - 1, z)) / (2.0 * 512);
            var dhz = (H(x, z + 1) - H(x, z - 1)) / (2.0 * 512);
            var len = Math.Sqrt(dhx * dhx + dhz * dhz + 1);
            normals[z * IslandCube.Vertices + x] = (-dhx / len, 1 / len, -dhz / len);
        }
        return normals;
    }

    // Unit vector towards the light for an azimuth and an elevation in degrees (azimuth 0 = +Z, 90 = +X).
    public static (double X, double Y, double Z) Direction(double azimuth, double elevation)
    {
        var a = azimuth * Math.PI / 180; var e = elevation * Math.PI / 180;
        return (Math.Cos(e) * Math.Sin(a), Math.Sin(e), Math.Cos(e) * Math.Cos(a));
    }

    public static double Lambert((double X, double Y, double Z) normal, double azimuth, double elevation)
    {
        var l = Direction(azimuth, elevation);
        return Math.Max(0, normal.X * l.X + normal.Y * l.Y + normal.Z * l.Z);
    }
}
