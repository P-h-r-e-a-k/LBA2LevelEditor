using System.Numerics;

namespace LBAAssembler.Lba1;

// Poses a body from bone frames the way LBA1's renderer does: every bone has a rotation matrix built from
// its parent's (rotating about X, then Z, then Y) and its points are rotated about the pivot point, which
// is a point of the parent bone; a translation bone shifts its points instead of rotating them.
internal static class Lba1Pose
{
    public static Vector3[] World(LbaBodyStudio.Body body, IReadOnlyList<(int Type, double X, double Y, double Z)> bones)
        => World(body, bones, out _);

    // The points and, for lighting, every bone's rotation matrix (3 x 3, row-major).
    public static Vector3[] World(LbaBodyStudio.Body body, IReadOnlyList<(int Type, double X, double Y, double Z)> bones, out double[][] matrices)
    {
        var result = new Vector3[body.Vertices.Count];
        matrices = new double[body.Bones.Count][];
        for (var i = 0; i < body.Bones.Count; i++)
        {
            var bone = body.Bones[i];
            // Bone 0 takes no animation values: the renderer rotates it by the actor's own angles (its frame values are
            // the turn and step the animation makes; P_OB_ISO.ASM AnimNuage), so here it stays as modelled.
            var frame = i > 0 && i < bones.Count ? bones[i] : (0, 0d, 0d, 0d);
            var parent = bone.Parent < 0 ? Identity : matrices[bone.Parent];
            var origin = bone.Parent < 0 ? Vector3.Zero : result[bone.Pivot];
            double[] matrix;
            double tx = 0, ty = 0, tz = 0;
            if (frame.Item1 == 0) matrix = Rotate(parent, frame.Item2, frame.Item3, frame.Item4);
            else
            {
                matrix = parent;
                (tx, ty, tz) = (frame.Item2, frame.Item3, frame.Item4);
            }
            matrices[i] = matrix;
            for (var p = bone.Start; p < bone.Start + bone.Count; p++)
            {
                var v = body.Vertices[p];
                double x = v.X + tx, y = v.Y + ty, z = v.Z + tz;
                result[p] = new Vector3(
                    (float)(matrix[0] * x + matrix[1] * y + matrix[2] * z) + origin.X,
                    (float)(matrix[3] * x + matrix[4] * y + matrix[5] * z) + origin.Y,
                    (float)(matrix[6] * x + matrix[7] * y + matrix[8] * z) + origin.Z);
            }
        }
        return result;
    }

    private static readonly double[] Identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

    private static double[] Rotate(double[] m, double ax, double ay, double az)
    {
        static double Radians(double a) => a * Math.PI * 2 / 1024;
        var r = (double[])m.Clone();
        if (ax != 0)
        {
            double s = Math.Sin(Radians(ax)), c = Math.Cos(Radians(ax));
            var n = (double[])r.Clone();
            for (var row = 0; row < 3; row++)
            {
                n[row * 3 + 1] = r[row * 3 + 1] * c + r[row * 3 + 2] * s;
                n[row * 3 + 2] = r[row * 3 + 2] * c - r[row * 3 + 1] * s;
            }
            r = n;
        }
        if (az != 0)
        {
            double s = Math.Sin(Radians(az)), c = Math.Cos(Radians(az));
            var n = (double[])r.Clone();
            for (var row = 0; row < 3; row++)
            {
                n[row * 3] = r[row * 3] * c + r[row * 3 + 1] * s;
                n[row * 3 + 1] = r[row * 3 + 1] * c - r[row * 3] * s;
            }
            r = n;
        }
        if (ay != 0)
        {
            double s = Math.Sin(Radians(ay)), c = Math.Cos(Radians(ay));
            var n = (double[])r.Clone();
            for (var row = 0; row < 3; row++)
            {
                n[row * 3] = r[row * 3] * c - r[row * 3 + 2] * s;
                n[row * 3 + 2] = r[row * 3] * s + r[row * 3 + 2] * c;
            }
            r = n;
        }
        return r;
    }
}
