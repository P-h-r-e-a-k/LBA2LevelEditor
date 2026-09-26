using System.IO;
using System.Numerics;
using LbaBodyStudio;
using LBAAssembler.Lba1;
using LBAAssembler.Terrain;

namespace LBAAssembler.Export;

// Turns the game's data into ExportScene geometry: bodies (actors, furniture, items, buildings ...), whole islands, and the block maps of
// interiors. The output is for looking at and using elsewhere, not for the game: bodies are in their neutral pose, lighting is dropped.
internal static class BodyMesher
{
    // The triangles a body must give: every polygon fanned, a sphere (10 x 6 segments) and a line (a 4-sided tube) each a fixed number.
    public static int ExpectedTriangles(Body body)
    {
        var world = body.World();
        return body.Faces.Where(f => f.Points.Length >= 3).Sum(f => f.Points.Length - 2) + body.Spheres.Count * 120
             + body.Lines.Count(l => (world[l.A] - world[l.B]).LengthSquared() >= 1e-6f) * 8;
    }

    public static ExportMesh Build(ExportScene scene, Body body, byte[] palette, string name, string keyPrefix = "")
    {
        var mesh = new ExportMesh(name);
        var world = body.World();
        foreach (var v in world) mesh.AddVertex(v);

        ExportMaterial Colour(int index)
        {
            var (r, g, b) = Palettes.Colour(palette, index);
            return new ExportMaterial { Name = $"palette{index}", R = r, G = g, B = b };
        }

        foreach (var face in body.Faces)
        {
            if (face.Points.Length < 3) continue;
            if (face.Texture is { } tex && body.TexturePage is { } page && tex.Handle < body.Textures.Length
                && TileMaterial(scene, keyPrefix, body, palette, page, tex.Handle) is { } material)
            {
                var (w, h) = material.Size;
                var first = mesh.Positions.Count;
                for (var j = 0; j < face.Points.Length; j++)
                    mesh.AddVertex(world[face.Points[j]], new Vector2(tex.UV[j * 2] / 256f / w, tex.UV[j * 2 + 1] / 256f / h));
                for (var t = 1; t < face.Points.Length - 1; t++) mesh.AddTriangle(material.Index, first, first + t, first + t + 1);
                continue;
            }
            var colour = scene.Material($"c{face.Colour}", () => Colour(face.Colour));
            for (var t = 1; t < face.Points.Length - 1; t++) mesh.AddTriangle(colour, face.Points[0], face.Points[t], face.Points[t + 1]);
        }

        foreach (var sphere in body.Spheres)
            AddSphere(mesh, scene.Material($"c{sphere.Colour}", () => Colour(sphere.Colour)), world[sphere.Point], Math.Max(1, sphere.Radius));
        foreach (var line in body.Lines)
            AddTube(mesh, scene.Material($"c{line.Colour}", () => Colour(line.Colour)), world[line.A], world[line.B], 6);
        return mesh;
    }

    // One tile of the body's texture page as a repeating picture of its own: the game wraps (u, v) by the tile's mask, so a glTF / OBJ
    // texture with repeat wrapping and the unwrapped (u, v) of the polygon reproduces it.
    private static (int Index, (int W, int H) Size)? TileMaterial(ExportScene scene, string keyPrefix, Body body, byte[] palette, byte[] page, int handle)
    {
        var info = body.Textures[handle];
        int offset = (int)(info & 0xFFFF), mask = (int)(info >> 16), mx = mask & 0xFF, my = (mask >> 8) & 0xFF;
        int w = mx + 1, h = my + 1;
        if (w > 256 || h > 256 || page.Length < 65536) return null;
        var key = $"{keyPrefix}tile{handle}";
        var index = scene.Material(key, () =>
        {
            var rgba = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var (r, g, b) = Palettes.Colour(palette, page[(offset + y * 256 + x) & 0xFFFF]);
                    var o = (y * w + x) * 4;
                    rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255;
                }
            return new ExportMaterial { Name = $"{keyPrefix}texture{handle}", Texture = new TextureImage { Name = $"{keyPrefix}texture{handle}", Width = w, Height = h, Rgba = rgba } };
        });
        return (index, (w, h));
    }

    private static void AddSphere(ExportMesh mesh, int material, Vector3 centre, float radius)
    {
        const int segments = 10, rings = 6;
        var first = mesh.Positions.Count;
        for (var r = 0; r <= rings; r++)
        {
            var phi = Math.PI * r / rings;
            for (var s = 0; s < segments; s++)
            {
                var theta = 2 * Math.PI * s / segments;
                mesh.AddVertex(centre + radius * new Vector3((float)(Math.Sin(phi) * Math.Cos(theta)), (float)Math.Cos(phi), (float)(Math.Sin(phi) * Math.Sin(theta))));
            }
        }
        for (var r = 0; r < rings; r++)
            for (var s = 0; s < segments; s++)
            {
                int a = first + r * segments + s, b = first + r * segments + (s + 1) % segments, c = first + (r + 1) * segments + s, d = first + (r + 1) * segments + (s + 1) % segments;
                Meshing.AddOutward(mesh, material, centre, a, b, c);
                Meshing.AddOutward(mesh, material, centre, b, d, c);
            }
    }

    // A line of a body (hair, whiskers): a thin square tube.
    private static void AddTube(ExportMesh mesh, int material, Vector3 a, Vector3 b, float radius)
    {
        var d = b - a;
        if (d.LengthSquared() < 1e-6f) return;
        d = Vector3.Normalize(d);
        var helper = Math.Abs(d.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(d, helper)) * radius;
        var v = Vector3.Normalize(Vector3.Cross(d, u)) * radius;
        Vector3[] ring = { u + v, u - v, -u - v, -u + v };
        var first = mesh.Positions.Count;
        foreach (var p in ring) mesh.AddVertex(a + p);
        foreach (var p in ring) mesh.AddVertex(b + p);
        var middle = (a + b) / 2;
        for (var k = 0; k < 4; k++)
        {
            int k2 = (k + 1) % 4;
            Meshing.AddOutward(mesh, material, middle, first + k, first + k2, first + 4 + k);
            Meshing.AddOutward(mesh, material, middle, first + k2, first + 4 + k2, first + 4 + k);
        }
    }
}

internal static class Meshing
{
    // A triangle wound so that it faces away from `inside` (a point behind the surface).
    public static void AddOutward(ExportMesh mesh, int material, Vector3 inside, int a, int b, int c)
    {
        var pa = mesh.Positions[a];
        var normal = Vector3.Cross(mesh.Positions[b] - pa, mesh.Positions[c] - pa);
        if (Vector3.Dot(normal, pa - inside) >= 0) mesh.AddTriangle(material, a, b, c);
        else mesh.AddTriangle(material, a, c, b);
    }
}

// A whole LBA2 island: the ground (every cube, textured with the ground atlas and lit with the baked light) and every object placed on it.
internal static class IslandMesher
{
    // corner k of a cell: (x, z) offsets, and the three corners of each of the cell's four possible triangles
    private static readonly (int X, int Z)[] Offsets = { (0, 0), (0, 1), (1, 1), (1, 0) };
    private static readonly int[][] Corners = { new[] { 0, 1, 2 }, new[] { 2, 3, 0 }, new[] { 3, 0, 1 }, new[] { 1, 2, 3 } };

    public static ExportScene Build(string ilePath, string gameDirectory, bool terrain, bool objects, Action<string>? log = null)
    {
        var island = IslandFile.Load(ilePath);
        var name = Path.GetFileNameWithoutExtension(ilePath);
        var palette = IslandMapRenderer.LoadPalette(gameDirectory, name);
        var scene = new ExportScene { Name = name };
        if (terrain) AddTerrain(scene, island, palette);
        if (objects) AddObjects(scene, island, palette, Path.ChangeExtension(ilePath, ".OBL"), log);
        return scene;
    }

    private static void AddTerrain(ExportScene scene, IslandFile island, byte[] palette)
    {
        var ground = scene.Material("ground", () =>
        {
            var rgba = new byte[256 * 256 * 4];
            for (var i = 0; i < 256 * 256; i++)
            {
                var (r, g, b) = Palettes.Colour(palette, island.GroundTexture[i]);
                rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255;
            }
            return new ExportMaterial { Name = "ground", Texture = new TextureImage { Name = "ground", Width = 256, Height = 256, Rgba = rgba, Repeat = false } };
        });

        foreach (var (cx, cz, cube) in IslandOps.CubeCells(island))
        {
            if (!cube.HasPolygons) continue;
            var mesh = new ExportMesh($"terrain_{cx}_{cz}", vertexColours: true);
            for (var z = 0; z < IslandCube.Cells; z++)
                for (var x = 0; x < IslandCube.Cells; x++)
                {
                    var polygons = new[] { new IslandPolygon(cube.Polygon(x, z, 0)), new IslandPolygon(cube.Polygon(x, z, 1)) };
                    var diagonal = polygons[0].Diagonal;
                    for (var half = 0; half < 2; half++)
                    {
                        var poly = polygons[half];
                        if (poly.TexFlag == 0 && poly.PolyFlag == 0) continue;      // nothing drawn: the sea shows through
                        var corners = Corners[(diagonal ? 2 : 0) + half];
                        var textured = poly.TexFlag != 0 && poly.TextureIndex * 6 + 6 <= cube.TextureDefs.Length;
                        int material;
                        if (textured) material = ground;
                        else
                        {
                            var colour = (poly.Bank << 4) + 11;
                            material = scene.Material($"c{colour}", () => { var (r, g, b) = Palettes.Colour(palette, colour); return new ExportMaterial { Name = $"palette{colour}", R = r, G = g, B = b }; });
                        }
                        var p = new Vector3[3]; var uv = new Vector2[3]; var shade = new Vector3[3];
                        for (var k = 0; k < 3; k++)
                        {
                            var (ox, oz) = Offsets[corners[k]];
                            int vx = x + ox, vz = z + oz;
                            p[k] = new Vector3((cx * IslandCube.Cells + vx) * IslandFile.CellSize, cube.Height(vx, vz), (cz * IslandCube.Cells + vz) * IslandFile.CellSize);
                            if (textured)
                            {
                                var def = cube.TextureDefs.AsSpan(poly.TextureIndex * 6, 6);
                                uv[k] = new Vector2(def[k * 2] / 65536f, def[k * 2 + 1] / 65536f);
                            }
                            var light = cube.HasIntensity ? cube.Light(vx, vz) : 15;
                            shade[k] = new Vector3(Math.Min(1f, 0.48f + light / 15f * 0.72f));
                        }
                        var order = Vector3.Cross(p[1] - p[0], p[2] - p[0]).Y >= 0 ? new[] { 0, 1, 2 } : new[] { 0, 2, 1 };
                        var a = mesh.AddVertex(p[order[0]], uv[order[0]], shade[order[0]]);
                        var b2 = mesh.AddVertex(p[order[1]], uv[order[1]], shade[order[1]]);
                        var c2 = mesh.AddVertex(p[order[2]], uv[order[2]], shade[order[2]]);
                        mesh.AddTriangle(material, a, b2, c2);
                    }
                }
            if (mesh.TriangleCount > 0) scene.Add(mesh);
        }
    }

    private static void AddObjects(ExportScene scene, IslandFile island, byte[] palette, string oblPath, Action<string>? log)
    {
        if (!File.Exists(oblPath)) { log?.Invoke($"{Path.GetFileName(oblPath)} not found: no objects exported."); return; }
        var archive = HqrFile.Parse(File.ReadAllBytes(oblPath));
        var meshes = new Dictionary<int, ExportMesh?>();
        var placed = 0;
        foreach (var (cx, cz, cube) in IslandOps.CubeCells(island))
            for (var i = 0; i < cube.Decors.Count; i++)
            {
                var decor = cube.Decors[i];
                var bodyIndex = decor.Body & 0xFFFF;
                if (!meshes.TryGetValue(bodyIndex, out var mesh))
                {
                    mesh = null;
                    try
                    {
                        var body = Body.Read(archive.Read(bodyIndex), 2, allowStatic: true);
                        body.TexturePage = island.ObjectTexture;
                        mesh = BodyMesher.Build(scene, body, palette, $"object_{bodyIndex}", $"obj{bodyIndex}_");
                    }
                    catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException)
                    {
                        log?.Invoke($"Object body {bodyIndex} can't be read ({error.Message}): left out.");
                    }
                    meshes[bodyIndex] = mesh;
                }
                if (mesh is null) continue;
                var angle = (float)((decor.Beta & 0xFFFF) * 2 * Math.PI / 4096);
                var transform = Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(cx * IslandFile.CubeSize + decor.X, decor.Y, cz * IslandFile.CubeSize + decor.Z);
                scene.Add(mesh, transform, $"object_{bodyIndex}_cube{cube.Id}_{i}");
                placed++;
            }
        log?.Invoke($"{placed} objects placed ({meshes.Count(m => m.Value is not null)} different bodies).");
    }
}

// The block map of a scene (LBA1's grids, LBA2's interiors) as a blocky model: every occupied cell of the 64 x 25 x 64 map a box in the average
// colour of its brick picture, only the faces that show. The bricks are pictures, not 3D shapes, so ramps and curves are boxes here.
internal static class GridMesher
{
    public sealed record Tile(IReadOnlyList<Lba1Placement> Cells, int OffsetX = 0, int OffsetY = 0, int OffsetZ = 0);

    public static ExportScene Build(string name, IEnumerable<Tile> tiles, Func<int, byte[]?> brick, byte[] palette)
    {
        var scene = new ExportScene { Name = name };
        var mesh = new ExportMesh(name);
        var occupied = new HashSet<(int X, int Y, int Z)>();
        var all = new List<(int X, int Y, int Z, int Brick)>();
        foreach (var tile in tiles)
            foreach (var c in tile.Cells)
            {
                var cell = (c.X + tile.OffsetX / 512, c.Y + tile.OffsetY / 256, c.Z + tile.OffsetZ / 512);
                if (occupied.Add(cell)) all.Add((cell.Item1, cell.Item2, cell.Item3, c.Brick));
            }
        var colours = new Dictionary<int, (float R, float G, float B)>();
        (float R, float G, float B) Average(int index)
        {
            if (colours.TryGetValue(index, out var known)) return known;
            return colours[index] = BrickAverage(brick(index), palette);
        }

        // face: the neighbour to test, the shade of that kind of face, the four corners (in an order that winds outward is fixed up afterwards)
        var faces = new (int Dx, int Dy, int Dz, float Shade, string Kind)[]
        {
            (0, 1, 0, 1.00f, "top"), (0, -1, 0, 0.50f, "bottom"), (1, 0, 0, 0.84f, "east"), (-1, 0, 0, 0.84f, "west"), (0, 0, 1, 0.70f, "south"), (0, 0, -1, 0.70f, "north"),
        };
        foreach (var (x, y, z, index) in all)
        {
            float x0 = x * 512 - 256, x1 = x * 512 + 256, y0 = y * 256, y1 = y * 256 + 256, z0 = z * 512 - 256, z1 = z * 512 + 256;
            var centre = new Vector3(x * 512, y * 256 + 128, z * 512);
            var (r, g, b) = Average(index);
            foreach (var (dx, dy, dz, shade, kind) in faces)
            {
                if (occupied.Contains((x + dx, y + dy, z + dz))) continue;
                var material = scene.Material($"b{index}_{kind}", () => new ExportMaterial { Name = $"brick{index}_{kind}", R = (byte)Math.Clamp(r * shade, 0, 255), G = (byte)Math.Clamp(g * shade, 0, 255), B = (byte)Math.Clamp(b * shade, 0, 255) });
                Vector3[] quad = kind switch
                {
                    "top" => new Vector3[] { new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1) },
                    "bottom" => new Vector3[] { new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1) },
                    "east" => new Vector3[] { new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1), new(x1, y0, z1) },
                    "west" => new Vector3[] { new(x0, y0, z0), new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1) },
                    "south" => new Vector3[] { new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1) },
                    _ => new Vector3[] { new(x0, y0, z0), new(x1, y0, z0), new(x1, y1, z0), new(x0, y1, z0) },
                };
                var ids = quad.Select(p => mesh.AddVertex(p)).ToArray();
                Meshing.AddOutward(mesh, material, centre, ids[0], ids[1], ids[2]);
                Meshing.AddOutward(mesh, material, centre, ids[0], ids[2], ids[3]);
            }
        }
        scene.Add(mesh);
        return scene;
    }

    // The mean colour of a brick's drawn pixels (the run-length format of LBA_BRK: width, lines, hot spot, then per line a list of runs).
    private static (float R, float G, float B) BrickAverage(byte[]? data, byte[] palette)
    {
        if (data is null || data.Length < 4) return (150, 150, 150);
        double r = 0, g = 0, b = 0; var n = 0;
        var src = 4;
        int lines = data[1];
        for (var line = 0; line < lines && src < data.Length; line++)
        {
            int runs = data[src++];
            for (var run = 0; run < runs && src < data.Length; run++)
            {
                var control = data[src++];
                var count = (control & 0x3F) + 1;
                switch (control >> 6)
                {
                    case 0: break;
                    case 1:
                        for (var k = 0; k < count && src < data.Length; k++) { var c = Palettes.Colour(palette, data[src++]); r += c.R; g += c.G; b += c.B; n++; }
                        break;
                    default:
                        if (src >= data.Length) break;
                        var colour = Palettes.Colour(palette, data[src++]);
                        r += colour.R * count; g += colour.G * count; b += colour.B * count; n += count;
                        break;
                }
            }
        }
        return n == 0 ? (150, 150, 150) : ((float)(r / n), (float)(g / n), (float)(b / n));
    }
}
