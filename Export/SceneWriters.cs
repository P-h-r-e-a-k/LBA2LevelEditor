using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LBAAssembler.Export;

internal enum ExportFormat { Glb, Obj, Ply, Stl }

// Writes an ExportScene as glTF binary (.glb: whole scene, instanced objects, textures and baked light), Wavefront OBJ (+ .mtl and PNG textures),
// PLY (vertex colours, no textures needed) or binary STL (bare geometry, for printing). `scale` turns game units into the file's units.
internal static class SceneWriters
{
    public static string Extension(ExportFormat format) => format switch { ExportFormat.Glb => ".glb", ExportFormat.Obj => ".obj", ExportFormat.Ply => ".ply", _ => ".stl" };

    public static string Describe(ExportFormat format) => format switch
    {
        ExportFormat.Glb => "glTF binary (.glb): textures, colours, baked light and instanced objects in one file; opens in Blender, Windows 3D Viewer, browsers, Unity, Unreal, Godot",
        ExportFormat.Obj => "Wavefront OBJ (.obj + .mtl + PNG textures): the most widely read format",
        ExportFormat.Ply => "PLY (.ply): triangles with vertex colours, no separate texture files",
        _ => "STL (.stl): bare triangles with no colour, for 3D printing and CAD",
    };

    public static void Write(ExportScene scene, string path, ExportFormat format, float scale)
    {
        var converted = scene.ToRightHanded(scale);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        switch (format)
        {
            case ExportFormat.Glb: WriteGlb(converted, path); break;
            case ExportFormat.Obj: WriteObj(converted, path); break;
            case ExportFormat.Ply: WritePly(converted, path); break;
            default: WriteStl(converted, path); break;
        }
    }

    public static string Safe(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(c < 128 && char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        return sb.Length == 0 ? "unnamed" : sb.ToString();
    }

    private static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    // ---- PNG ---------------------------------------------------------------------------------------------------------------------------

    public static byte[] Png(TextureImage texture)
    {
        using var bitmap = new Bitmap(texture.Width, texture.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, texture.Width, texture.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[texture.Width * texture.Height * 4];
            for (var i = 0; i < texture.Width * texture.Height; i++)
            {
                bgra[i * 4] = texture.Rgba[i * 4 + 2]; bgra[i * 4 + 1] = texture.Rgba[i * 4 + 1]; bgra[i * 4 + 2] = texture.Rgba[i * 4]; bgra[i * 4 + 3] = texture.Rgba[i * 4 + 3];
            }
            Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally { bitmap.UnlockBits(data); }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    // ---- OBJ ---------------------------------------------------------------------------------------------------------------------------

    private static void WriteObj(ExportScene scene, string path)
    {
        var baseName = Path.GetFileNameWithoutExtension(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var textureFolder = baseName + "_textures";
        var names = UniqueNames(scene.Materials.Select(m => m.Name));

        var mtl = new StringBuilder();
        var written = new Dictionary<TextureImage, string>();
        for (var i = 0; i < scene.Materials.Count; i++)
        {
            var m = scene.Materials[i];
            mtl.AppendLine($"newmtl {names[i]}");
            if (m.Texture is { } texture)
            {
                if (!written.TryGetValue(texture, out var file))
                {
                    file = Safe(texture.Name) + (written.Values.Any(v => v.Equals(Safe(texture.Name) + ".png", StringComparison.OrdinalIgnoreCase)) ? $"_{written.Count}" : "") + ".png";
                    Directory.CreateDirectory(Path.Combine(directory, textureFolder));
                    File.WriteAllBytes(Path.Combine(directory, textureFolder, file), Png(texture));
                    written[texture] = file;
                }
                mtl.AppendLine("Kd 1 1 1");
                mtl.AppendLine($"map_Kd {textureFolder}/{file}");
            }
            else mtl.AppendLine($"Kd {F(m.R / 255f)} {F(m.G / 255f)} {F(m.B / 255f)}");
            mtl.AppendLine("Ks 0 0 0\nillum 1\n");
        }
        File.WriteAllText(Path.Combine(directory, baseName + ".mtl"), mtl.ToString());

        var obj = new StringBuilder($"# exported by LBA Assembler\nmtllib {baseName}.mtl\n");
        var offset = 0;
        var uv = scene.HasTextures;
        foreach (var (name, mesh) in scene.Baked())
        {
            obj.Append("o ").AppendLine(Safe(name));
            foreach (var p in mesh.Positions) obj.Append("v ").Append(F(p.X)).Append(' ').Append(F(p.Y)).Append(' ').Append(F(p.Z)).Append('\n');
            if (uv) foreach (var t in mesh.Uvs) obj.Append("vt ").Append(F(t.X)).Append(' ').Append(F(1 - t.Y)).Append('\n');
            foreach (var primitive in mesh.Primitives)
            {
                obj.Append("usemtl ").Append(names[primitive.Material]).Append('\n');
                for (var i = 0; i + 2 < primitive.Indices.Count; i += 3)
                {
                    int a = primitive.Indices[i] + 1 + offset, b = primitive.Indices[i + 1] + 1 + offset, c = primitive.Indices[i + 2] + 1 + offset;
                    if (uv) obj.Append($"f {a}/{a} {b}/{b} {c}/{c}\n"); else obj.Append($"f {a} {b} {c}\n");
                }
            }
            offset += mesh.Positions.Count;
        }
        File.WriteAllText(path, obj.ToString());
    }

    private static List<string> UniqueNames(IEnumerable<string> names)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var name = Safe(raw);
            if (seen.TryGetValue(name, out var n)) { seen[name] = n + 1; name = $"{name}_{n + 1}"; }
            else seen[name] = 1;
            result.Add(name);
        }
        return result;
    }

    // ---- glTF binary -------------------------------------------------------------------------------------------------------------------

    private sealed record View(int Offset, int Length, int? Target);
    private sealed record Accessor(int View, int ComponentType, int Count, string Type, float[]? Min = null, float[]? Max = null);

    private static void WriteGlb(ExportScene scene, string path)
    {
        var bin = new MemoryStream();
        var views = new List<View>();
        var accessors = new List<Accessor>();
        int AddView(ReadOnlySpan<byte> data, int? target)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            views.Add(new View((int)bin.Length, data.Length, target));
            bin.Write(data);
            return views.Count - 1;
        }
        int AddAccessor(Accessor accessor) { accessors.Add(accessor); return accessors.Count - 1; }

        // textures: one image per picture, two samplers (repeat for body tiles, clamp for the ground atlas), pixels stay sharp
        var images = new List<int>();                    // bufferView per image
        var imageIndex = new Dictionary<TextureImage, int>();
        var textureOf = new Dictionary<TextureImage, int>();
        var textures = new List<(int Sampler, int Source)>();
        foreach (var texture in scene.Materials.Select(m => m.Texture).Where(t => t is not null).Distinct().Cast<TextureImage>())
        {
            images.Add(AddView(Png(texture), null));
            imageIndex[texture] = images.Count - 1;
            textures.Add((texture.Repeat ? 0 : 1, images.Count - 1));
            textureOf[texture] = textures.Count - 1;
        }

        // meshes (one per distinct ExportMesh; nodes reuse them)
        var meshIds = new Dictionary<ExportMesh, int>();
        var meshJson = new List<(string Name, List<(Dictionary<string, int> Attributes, int Indices, int Material)> Primitives)>();
        foreach (var node in scene.Nodes)
        {
            if (meshIds.ContainsKey(node.Mesh)) continue;
            var mesh = node.Mesh;
            var attributes = new Dictionary<string, int>();
            var positions = new float[mesh.Positions.Count * 3];
            float[] min = { float.MaxValue, float.MaxValue, float.MaxValue }, max = { float.MinValue, float.MinValue, float.MinValue };
            for (var i = 0; i < mesh.Positions.Count; i++)
            {
                var p = mesh.Positions[i];
                positions[i * 3] = p.X; positions[i * 3 + 1] = p.Y; positions[i * 3 + 2] = p.Z;
                min[0] = Math.Min(min[0], p.X); min[1] = Math.Min(min[1], p.Y); min[2] = Math.Min(min[2], p.Z);
                max[0] = Math.Max(max[0], p.X); max[1] = Math.Max(max[1], p.Y); max[2] = Math.Max(max[2], p.Z);
            }
            if (mesh.Positions.Count == 0 || mesh.TriangleCount == 0) continue;
            attributes["POSITION"] = AddAccessor(new Accessor(AddView(MemoryMarshal.AsBytes<float>(positions), 34962), 5126, mesh.Positions.Count, "VEC3", min, max));
            if (mesh.Primitives.Any(p => scene.Materials[p.Material].Texture is not null))
            {
                var uvs = new float[mesh.Uvs.Count * 2];
                for (var i = 0; i < mesh.Uvs.Count; i++) { uvs[i * 2] = mesh.Uvs[i].X; uvs[i * 2 + 1] = mesh.Uvs[i].Y; }
                attributes["TEXCOORD_0"] = AddAccessor(new Accessor(AddView(MemoryMarshal.AsBytes<float>(uvs), 34962), 5126, mesh.Uvs.Count, "VEC2"));
            }
            if (mesh.Colors is { } colours)
            {
                var values = new float[colours.Count * 3];
                for (var i = 0; i < colours.Count; i++) { values[i * 3] = colours[i].X; values[i * 3 + 1] = colours[i].Y; values[i * 3 + 2] = colours[i].Z; }
                attributes["COLOR_0"] = AddAccessor(new Accessor(AddView(MemoryMarshal.AsBytes<float>(values), 34962), 5126, colours.Count, "VEC3"));
            }
            var primitives = new List<(Dictionary<string, int>, int, int)>();
            foreach (var primitive in mesh.Primitives)
            {
                if (primitive.Indices.Count == 0) continue;
                var indices = primitive.Indices.Select(i => (uint)i).ToArray();
                primitives.Add((attributes, AddAccessor(new Accessor(AddView(MemoryMarshal.AsBytes<uint>(indices), 34963), 5125, indices.Length, "SCALAR")), primitive.Material));
            }
            meshIds[mesh] = meshJson.Count;
            meshJson.Add((mesh.Name, primitives));
        }

        using var json = new MemoryStream();
        using (var w = new Utf8JsonWriter(json))
        {
            w.WriteStartObject();
            w.WriteStartObject("asset"); w.WriteString("version", "2.0"); w.WriteString("generator", "LBA Assembler"); w.WriteEndObject();
            var drawn = scene.Nodes.Where(n => meshIds.ContainsKey(n.Mesh)).ToList();
            w.WriteNumber("scene", 0);
            w.WriteStartArray("scenes"); w.WriteStartObject(); w.WriteStartArray("nodes");
            for (var i = 0; i < drawn.Count; i++) w.WriteNumberValue(i);
            w.WriteEndArray(); w.WriteEndObject(); w.WriteEndArray();

            w.WriteStartArray("nodes");
            foreach (var node in drawn)
            {
                w.WriteStartObject();
                w.WriteString("name", node.Name);
                w.WriteNumber("mesh", meshIds[node.Mesh]);
                if (node.Transform != Matrix4x4.Identity)
                {
                    var m = node.Transform;
                    w.WriteStartArray("matrix");
                    foreach (var v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) w.WriteNumberValue(v);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("meshes");
            foreach (var (name, primitives) in meshJson)
            {
                w.WriteStartObject(); w.WriteString("name", name); w.WriteStartArray("primitives");
                foreach (var (attributes, indices, material) in primitives)
                {
                    w.WriteStartObject(); w.WriteStartObject("attributes");
                    foreach (var (key, id) in attributes) w.WriteNumber(key, id);
                    w.WriteEndObject();
                    w.WriteNumber("indices", indices); w.WriteNumber("material", material); w.WriteNumber("mode", 4);
                    w.WriteEndObject();
                }
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray();

            var names = UniqueNames(scene.Materials.Select(m => m.Name));
            w.WriteStartArray("materials");
            for (var i = 0; i < scene.Materials.Count; i++)
            {
                var m = scene.Materials[i];
                w.WriteStartObject(); w.WriteString("name", names[i]); w.WriteBoolean("doubleSided", true);
                w.WriteStartObject("pbrMetallicRoughness");
                w.WriteStartArray("baseColorFactor");
                if (m.Texture is null) { w.WriteNumberValue(m.R / 255f); w.WriteNumberValue(m.G / 255f); w.WriteNumberValue(m.B / 255f); } else { w.WriteNumberValue(1f); w.WriteNumberValue(1f); w.WriteNumberValue(1f); }
                w.WriteNumberValue(1f); w.WriteEndArray();
                if (m.Texture is not null) { w.WriteStartObject("baseColorTexture"); w.WriteNumber("index", textureOf[m.Texture]); w.WriteEndObject(); }
                w.WriteNumber("metallicFactor", 0f); w.WriteNumber("roughnessFactor", 1f);
                w.WriteEndObject(); w.WriteEndObject();
            }
            w.WriteEndArray();

            if (textures.Count > 0)
            {
                w.WriteStartArray("textures");
                foreach (var (sampler, source) in textures) { w.WriteStartObject(); w.WriteNumber("sampler", sampler); w.WriteNumber("source", source); w.WriteEndObject(); }
                w.WriteEndArray();
                w.WriteStartArray("images");
                foreach (var view in images) { w.WriteStartObject(); w.WriteNumber("bufferView", view); w.WriteString("mimeType", "image/png"); w.WriteEndObject(); }
                w.WriteEndArray();
                w.WriteStartArray("samplers");
                foreach (var wrap in new[] { 10497, 33071 })
                { w.WriteStartObject(); w.WriteNumber("magFilter", 9728); w.WriteNumber("minFilter", 9728); w.WriteNumber("wrapS", wrap); w.WriteNumber("wrapT", wrap); w.WriteEndObject(); }
                w.WriteEndArray();
            }

            w.WriteStartArray("accessors");
            foreach (var a in accessors)
            {
                w.WriteStartObject(); w.WriteNumber("bufferView", a.View); w.WriteNumber("componentType", a.ComponentType); w.WriteNumber("count", a.Count); w.WriteString("type", a.Type);
                if (a.Min is not null) { w.WriteStartArray("min"); foreach (var v in a.Min) w.WriteNumberValue(v); w.WriteEndArray(); }
                if (a.Max is not null) { w.WriteStartArray("max"); foreach (var v in a.Max) w.WriteNumberValue(v); w.WriteEndArray(); }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("bufferViews");
            foreach (var v in views)
            {
                w.WriteStartObject(); w.WriteNumber("buffer", 0); w.WriteNumber("byteOffset", v.Offset); w.WriteNumber("byteLength", v.Length);
                if (v.Target is { } target) w.WriteNumber("target", target);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("buffers"); w.WriteStartObject(); w.WriteNumber("byteLength", (int)bin.Length); w.WriteEndObject(); w.WriteEndArray();
            w.WriteEndObject();
        }

        var jsonBytes = json.ToArray();
        var jsonPad = (4 - jsonBytes.Length % 4) % 4;
        var binPad = (4 - (int)bin.Length % 4) % 4;
        using var file = File.Create(path);
        using var o = new BinaryWriter(file);
        o.Write(0x46546C67u); o.Write(2u);
        o.Write((uint)(12 + 8 + jsonBytes.Length + jsonPad + 8 + bin.Length + binPad));
        o.Write((uint)(jsonBytes.Length + jsonPad)); o.Write(0x4E4F534Au); o.Write(jsonBytes); for (var i = 0; i < jsonPad; i++) o.Write((byte)0x20);
        o.Write((uint)(bin.Length + binPad)); o.Write(0x004E4942u); o.Write(bin.GetBuffer(), 0, (int)bin.Length); for (var i = 0; i < binPad; i++) o.Write((byte)0);
    }

    // ---- PLY / STL ---------------------------------------------------------------------------------------------------------------------

    // The colour a corner of a triangle takes in a format with only vertex colours: its material (a picture sampled at the corner) times its light.
    private static (byte R, byte G, byte B) CornerColour(ExportScene scene, ExportMesh mesh, int material, int vertex)
    {
        var m = scene.Materials[material];
        float r = m.R, g = m.G, b = m.B;
        if (m.Texture is { } t)
        {
            var uv = mesh.Uvs[vertex];
            int x, y;
            if (t.Repeat) { x = (int)Math.Floor(uv.X * t.Width) % t.Width; y = (int)Math.Floor(uv.Y * t.Height) % t.Height; if (x < 0) x += t.Width; if (y < 0) y += t.Height; }
            else { x = Math.Clamp((int)(uv.X * t.Width), 0, t.Width - 1); y = Math.Clamp((int)(uv.Y * t.Height), 0, t.Height - 1); }
            var o = (y * t.Width + x) * 4;
            r = t.Rgba[o]; g = t.Rgba[o + 1]; b = t.Rgba[o + 2];
        }
        if (mesh.Colors is { } colours) { r *= colours[vertex].X; g *= colours[vertex].Y; b *= colours[vertex].Z; }
        return ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
    }

    private static void WritePly(ExportScene scene, string path)
    {
        var vertices = new List<(Vector3 P, (byte R, byte G, byte B) C)>();
        foreach (var (_, mesh) in scene.Baked())
            foreach (var primitive in mesh.Primitives)
                foreach (var index in primitive.Indices) vertices.Add((mesh.Positions[index], CornerColour(scene, mesh, primitive.Material, index)));
        using var file = File.Create(path);
        using var o = new BinaryWriter(file);
        var header = $"ply\nformat binary_little_endian 1.0\ncomment exported by LBA Assembler\nelement vertex {vertices.Count}\nproperty float x\nproperty float y\nproperty float z\nproperty uchar red\nproperty uchar green\nproperty uchar blue\nelement face {vertices.Count / 3}\nproperty list uchar int vertex_indices\nend_header\n";
        o.Write(Encoding.ASCII.GetBytes(header));
        foreach (var (p, c) in vertices) { o.Write(p.X); o.Write(p.Y); o.Write(p.Z); o.Write(c.R); o.Write(c.G); o.Write(c.B); }
        for (var i = 0; i + 2 < vertices.Count; i += 3) { o.Write((byte)3); o.Write(i); o.Write(i + 1); o.Write(i + 2); }
    }

    private static void WriteStl(ExportScene scene, string path)
    {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        foreach (var (_, mesh) in scene.Baked())
            foreach (var primitive in mesh.Primitives)
                for (var i = 0; i + 2 < primitive.Indices.Count; i += 3)
                    triangles.Add((mesh.Positions[primitive.Indices[i]], mesh.Positions[primitive.Indices[i + 1]], mesh.Positions[primitive.Indices[i + 2]]));
        using var file = File.Create(path);
        using var o = new BinaryWriter(file);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("exported by LBA Assembler").CopyTo(header, 0);
        o.Write(header); o.Write((uint)triangles.Count);
        foreach (var (a, b, c) in triangles)
        {
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
            o.Write(n.X); o.Write(n.Y); o.Write(n.Z);
            foreach (var p in new[] { a, b, c }) { o.Write(p.X); o.Write(p.Y); o.Write(p.Z); }
            o.Write((ushort)0);
        }
    }
}
