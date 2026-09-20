using System.Numerics;

namespace LbaBodyStudio;

// A game body drawn as the flat reference pictures Body Studio's generator reads: orthographic front and back views in plain
// palette colours (no lighting), on a transparent background, side by side. It is the game's own idea of what a body looks
// like "flattened", so it doubles as the template for artwork made for the generator, and generator(sheet(body)) ~ body is
// the test that the two halves of the pipeline agree.
public static class FlatSheet
{
    public sealed class Options
    {
        // pixel height of each view
        public int Height { get; set; } = 768;
        public int Margin { get; set; } = 12;
        // draw at this many times the size and average down, for smooth silhouette edges
        public int Supersample { get; set; } = 2;
        // the character's front faces negative Z (the game's own default is positive Z, see Settings.NegativeZFront)
        public bool NegativeZFront { get; set; }
        public bool IncludeBack { get; set; } = true;
        // draw lit polygons in the colour they show on screen (base colour plus the average light) instead of the stored base colour
        public bool ShowLighting { get; set; } = true;
    }

    // The 256-colour palette (RESS.HQR entry 0: 768 bytes of RGB).
    public static byte[] Rgb(byte[] palette768, int index) => new[] { palette768[index * 3], palette768[index * 3 + 1], palette768[index * 3 + 2] };

    public static (float Min, float Max) Extent(Vector3[] world, int axis)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in world) { var c = axis == 0 ? v.X : axis == 1 ? v.Y : v.Z; lo = Math.Min(lo, c); hi = Math.Max(hi, c); }
        return (lo, hi);
    }

    // Front then back, each `halfWidth` pixels wide, `Height` tall. The scale is shared, so the two views line up.
    public static FlatImage Render(Body body, byte[] palette768, Options? options = null)
    {
        options ??= new Options();
        var world = body.World();
        // spheres (hair buns, hands) reach beyond their centre point: the extents include their radius
        var reach = world.Concat(body.Spheres.SelectMany(s => new[] { world[s.Point] + new Vector3(s.Radius, s.Radius, s.Radius), world[s.Point] - new Vector3(s.Radius, s.Radius, s.Radius) })).ToArray();
        var (minY, maxY) = Extent(reach, 1);
        var (minX, maxX) = Extent(reach, 0);
        var centreX = (minX + maxX) / 2;
        var halfExtent = Math.Max(reach.Max(v => Math.Abs(v.X - centreX)), 1);
        var scale = (options.Height - 2 * options.Margin) / Math.Max(1, maxY - minY);
        var halfWidth = (int)Math.Ceiling(halfExtent * 2 * scale) + 2 * options.Margin;

        var front = RenderView(body, world, palette768, true, options, scale, halfWidth, centreX, minY);
        if (!options.IncludeBack) return front;
        var back = RenderView(body, world, palette768, false, options, scale, halfWidth, centreX, minY);
        var sheet = new FlatImage(halfWidth * 2, options.Height);
        sheet.Paste(front, 0, 0);
        sheet.Paste(back, halfWidth, 0);
        return sheet;
    }

    private static FlatImage RenderView(Body body, Vector3[] world, byte[] palette, bool front, Options options, float scale, int halfWidth, float centreX, float minY)
    {
        var s = Math.Max(1, options.Supersample);
        int w = halfWidth * s, h = options.Height * s;
        var pixels = new byte[w * h * 4];
        var depth = new float[w * h];
        Array.Fill(depth, float.PositiveInfinity);
        float k = scale * s;

        // In the front view +X runs right (the generator samples u = 0.5 + x / width); the back view is seen from behind, so
        // +X runs left. The nearer surface is the one on the viewer's side of the character.
        var facing = options.NegativeZFront ? -1f : 1f;
        Vector3 Project(Vector3 v)
        {
            var x = (v.X - centreX) * (front ? 1 : -1);
            var nearness = v.Z * facing * (front ? 1 : -1);          // larger = nearer to the viewer
            return new Vector3(w / 2f + x * k, h - options.Margin * s - (v.Y - minY) * k, -nearness);
        }
        var projected = world.Select(Project).ToArray();

        void Put(int x, int y, float z, int colour)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            var i = y * w + x;
            if (z > depth[i]) return;
            depth[i] = z;
            var p = i * 4;
            pixels[p] = palette[colour * 3 + 2]; pixels[p + 1] = palette[colour * 3 + 1]; pixels[p + 2] = palette[colour * 3]; pixels[p + 3] = 255;
        }

        foreach (var face in body.Faces)
        {
            var colour = Math.Clamp(face.Colour, 0, 255);
            if (options.ShowLighting && LightModel.IsLit(face, body.Game, body.Lit)) colour = LightModel.DisplayOf(colour, body.Game);
            for (var t = 1; t < face.Points.Length - 1; t++)
                Triangle(projected[face.Points[0]], projected[face.Points[t]], projected[face.Points[t + 1]], colour, w, h, Put);
        }
        foreach (var l in body.Lines)
        {
            var a = projected[l.A]; var b = projected[l.B];
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))));
            for (var i = 0; i <= steps; i++)
            {
                var t = i / (float)steps;
                int x = (int)Math.Round(a.X + (b.X - a.X) * t), y = (int)Math.Round(a.Y + (b.Y - a.Y) * t);
                var z = a.Z + (b.Z - a.Z) * t - 2f;
                for (var dy = 0; dy < s; dy++) for (var dx = 0; dx < s; dx++) Put(x + dx, y + dy, z, Math.Clamp(l.Colour, 0, 255));
            }
        }
        foreach (var sp in body.Spheres)
        {
            var c = projected[sp.Point]; var r = Math.Max(1f, sp.Radius * k);
            for (var y = (int)Math.Floor(c.Y - r); y <= (int)Math.Ceiling(c.Y + r); y++)
                for (var x = (int)Math.Floor(c.X - r); x <= (int)Math.Ceiling(c.X + r); x++)
                {
                    float dx = x + .5f - c.X, dy = y + .5f - c.Y, d2 = dx * dx + dy * dy;
                    if (d2 <= r * r) Put(x, y, c.Z - MathF.Sqrt(r * r - d2) / k, Math.Clamp(sp.Colour, 0, 255));
                }
        }
        var big = new FlatImage(w, h, pixels);
        return s == 1 ? big : big.Resize(halfWidth, options.Height);
    }

    private static void Triangle(Vector3 a, Vector3 b, Vector3 c, int colour, int w, int h, Action<int, int, float, int> put)
    {
        var area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (Math.Abs(area) < 1e-4f) return;
        int x0 = Math.Max(0, (int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))), x1 = Math.Min(w - 1, (int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))), y1 = Math.Min(h - 1, (int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float px = x + .5f, py = y + .5f;
                var wa = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                var wb = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                var wc = 1 - wa - wb;
                if (wa < -.0001f || wb < -.0001f || wc < -.0001f) continue;
                put(x, y, wa * a.Z + wb * b.Z + wc * c.Z, colour);
            }
    }

    // The distinct palette colours a body's faces use.
    public static int[] Colours(Body body) => body.Faces.Select(f => f.Colour).Concat(body.Lines.Select(l => l.Colour)).Concat(body.Spheres.Select(s => s.Colour)).Distinct().OrderBy(c => c).ToArray();
}
