namespace LBAAssembler;

// The native renderer's camera as a pinhole model fitted from its own projection (lba2_renderer_project_point), so the main
// window can turn a mouse position into a point on the ground (for editing the terrain in the 3D view) and project points
// (the brush ring) without calling into the native library, whose camera state moves with every frame.
//
// The fit is a direct linear transform: 3 x 4 matrix P (P34 = 1) with  u = (P1 . X) / (P3 . X),  v = (P2 . X) / (P3 . X)  over
// world points X = (x, y, z, 1) taken relative to the camera target, least squares over a grid of sample points around it.
// Screen coordinates are the native framebuffer's pixels.
internal sealed class NativeCameraModel
{
    private readonly double[] p;                    // P11..P33, 11 values (row major, P34 = 1)
    private readonly double cx, cy, cz;             // the target the world points were taken relative to
    private readonly double worldScale;             // world units per unit of the normalised coordinates

    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public double RmsError { get; }                 // pixels, over the samples that were fitted

    private NativeCameraModel(double[] p, double cx, double cy, double cz, double worldScale, int width, int height, double rms)
    {
        this.p = p; this.cx = cx; this.cy = cy; this.cz = cz; this.worldScale = worldScale;
        FrameWidth = width; FrameHeight = height; RmsError = rms;
    }

    // project(x, y, z) -> the framebuffer pixel, or null when the point isn't visible.
    public static NativeCameraModel? Fit(Func<int, int, int, (int X, int Y)?> project, double targetX, double targetY, double targetZ, double distance, int width, int height)
    {
        var span = Math.Clamp(distance * 0.55, 2500, 26000);
        var samples = new List<(double X, double Y, double Z, double U, double V)>();
        foreach (var dy in new[] { -0.6, 0.0, 0.6, 1.4 })
            for (var i = -3; i <= 3; i++)
                for (var j = -3; j <= 3; j++)
                {
                    double x = targetX + i * span / 3, y = targetY + dy * span, z = targetZ + j * span / 3;
                    if (project((int)x, (int)y, (int)z) is not { } s) continue;
                    if (Math.Abs(s.X) > 20000 || Math.Abs(s.Y) > 20000) continue;
                    samples.Add((x, y, z, s.X, s.Y));
                }
        if (samples.Count < 12) return null;

        var scale = span;
        // normal equations of the 2n x 11 system
        var a = new double[11, 12];
        void Row(double[] r, double rhs)
        {
            for (var i = 0; i < 11; i++)
            {
                for (var j = 0; j < 11; j++) a[i, j] += r[i] * r[j];
                a[i, 11] += r[i] * rhs;
            }
        }
        foreach (var s in samples)
        {
            double x = (s.X - targetX) / scale, y = (s.Y - targetY) / scale, z = (s.Z - targetZ) / scale;
            double u = (s.U - width / 2.0) / width, v = (s.V - height / 2.0) / width;
            Row(new[] { x, y, z, 1, 0, 0, 0, 0, -u * x, -u * y, -u * z }, u);
            Row(new[] { 0, 0, 0, 0, x, y, z, 1, -v * x, -v * y, -v * z }, v);
        }
        var solution = Solve(a, 11);
        if (solution is null) return null;
        var model = new NativeCameraModel(solution, targetX, targetY, targetZ, scale, width, height, 0);
        double sum = 0;
        foreach (var s in samples)
        {
            if (!model.Project(s.X, s.Y, s.Z, out var pu, out var pv)) return null;
            sum += (pu - s.U) * (pu - s.U) + (pv - s.V) * (pv - s.V);
        }
        return new NativeCameraModel(solution, targetX, targetY, targetZ, scale, width, height, Math.Sqrt(sum / samples.Count));
    }

    private static double[]? Solve(double[,] a, int n)
    {
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-12) return null;
            if (pivot != col) for (var c = 0; c <= n; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var f = a[r, col] / a[col, col];
                if (f == 0) continue;
                for (var c = col; c <= n; c++) a[r, c] -= f * a[col, c];
            }
        }
        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = a[i, n] / a[i, i];
        return x;
    }

    // World point -> framebuffer pixel (false when it is behind the camera).
    public bool Project(double wx, double wy, double wz, out double u, out double v)
    {
        double x = (wx - cx) / worldScale, y = (wy - cy) / worldScale, z = (wz - cz) / worldScale;
        var d = p[8] * x + p[9] * y + p[10] * z + 1;
        u = v = 0;
        if (d < 1e-9) return false;
        u = (p[0] * x + p[1] * y + p[2] * z + p[3]) / d * FrameWidth + FrameWidth / 2.0;
        v = (p[4] * x + p[5] * y + p[6] * z + p[7]) / d * FrameWidth + FrameHeight / 2.0;
        return true;
    }

    // The world point on the horizontal plane y = height that a framebuffer pixel sees.
    public bool RayToPlane(double u, double v, double height, out double wx, out double wz)
    {
        wx = wz = 0;
        var un = (u - FrameWidth / 2.0) / FrameWidth;
        var vn = (v - FrameHeight / 2.0) / FrameWidth;
        var y = (height - cy) / worldScale;
        // (P1 - u P3) . (x, y, z, 1) = 0 and (P2 - v P3) . (x, y, z, 1) = 0, unknowns x and z
        double a1 = p[0] - un * p[8], b1 = p[2] - un * p[10], c1 = un * (p[9] * y + 1) - p[1] * y - p[3];
        double a2 = p[4] - vn * p[8], b2 = p[6] - vn * p[10], c2 = vn * (p[9] * y + 1) - p[5] * y - p[7];
        var det = a1 * b2 - a2 * b1;
        if (Math.Abs(det) < 1e-12) return false;
        var x = (c1 * b2 - c2 * b1) / det;
        var z = (a1 * c2 - a2 * c1) / det;
        wx = cx + x * worldScale;
        wz = cz + z * worldScale;
        return true;
    }
}
