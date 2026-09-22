using System.Buffers.Binary;
using System.IO;

namespace LBAAssembler.Terrain;

// One decor object of a cube (DOB record, T_DECORS in the engine): a body of the island's OBL file placed in the
// world. The ZV (XMin..ZMax) is absolute, so it moves with the object. Kept as the raw record so nothing is lost.
internal sealed class IslandDecor
{
    public byte[] Raw;

    public IslandDecor(byte[] raw) => Raw = raw;

    private int Get(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Raw.AsSpan(offset));
    private void Set(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(Raw.AsSpan(offset), value);

    // Body & 0xFFFF is the body's index in the OBL file, the high bits are the engine's DEC_ flags.
    public int Body { get => Get(0); set => Set(0, value); }
    public int X { get => Get(4); set => Set(4, value); }
    public int Y { get => Get(8); set => Set(8, value); }
    public int Z { get => Get(12); set => Set(12, value); }
    public int CodeJeu { get => Get(16); set => Set(16, value); }
    // Beta: the low 16 bits are the facing, the high 16 bits a game variable that hides the object (negative: when it is off).
    public int Beta { get => Get(20); set => Set(20, value); }
    public int XMin { get => Get(24); set => Set(24, value); }
    public int YMin { get => Get(28); set => Set(28, value); }
    public int ZMin { get => Get(32); set => Set(32, value); }
    public int XMax { get => Get(36); set => Set(36, value); }
    public int YMax { get => Get(40); set => Set(40, value); }
    public int ZMax { get => Get(44); set => Set(44, value); }

    public IslandDecor Clone() => new((byte[])Raw.Clone());

    // Moves the object and its ZV together.
    public void MoveTo(int x, int y, int z)
    {
        var (dx, dy, dz) = (x - X, y - Y, z - Z);
        X = x; Y = y; Z = z;
        XMin += dx; XMax += dx; YMin += dy; YMax += dy; ZMin += dz; ZMax += dz;
    }
}

// One 64 x 64 cell cube of an island: 65 x 65 vertices for height and light, two triangles per cell (the polygon map),
// the texture definitions those triangles point at, the decor list and the cube's light / fog settings (INF).
internal sealed class IslandCube
{
    public const int Cells = 64, Vertices = 65;
    public const int InfoAlphaLight = 0, InfoBetaLight = 1, InfoNbDecors = 2, InfoSkyY = 3, InfoStartZFog = 4, InfoClipZFar = 5;
    public const int InfoSize = 10;

    public int Id { get; init; }
    // INF as the engine sees it (S32 list); the record may be longer, the rest is kept in InfoTail.
    public int[] Info { get; set; } = new int[InfoSize];
    public byte[] InfoTail { get; set; } = Array.Empty<byte>();
    public List<IslandDecor> Decors { get; set; } = new();
    public int DecorStride { get; set; } = 48;
    public int DecorTailBytes { get; set; }
    public uint[] Polygons { get; set; } = new uint[Cells * Cells * 2];
    public byte[] PolygonTail { get; set; } = Array.Empty<byte>();
    public ushort[] TextureDefs { get; set; } = Array.Empty<ushort>();
    public byte[] TextureTail { get; set; } = Array.Empty<byte>();
    public short[] Heights { get; set; } = new short[Vertices * Vertices];
    public byte[] HeightTail { get; set; } = Array.Empty<byte>();
    // 65 x 65: the low nibble is the vertex brightness (0..15), the high nibble is kept as it is.
    public byte[] Intensity { get; set; } = new byte[Vertices * Vertices];
    public byte[] IntensityTail { get; set; } = Array.Empty<byte>();
    public bool HasIntensity { get; set; } = true;
    public bool HasPolygons { get; set; } = true;

    // The light direction the engine shades the cube with: two angles in 1/4096 turns.
    public int AlphaLight { get => Info[InfoAlphaLight] & 0xFFFF; set => Info[InfoAlphaLight] = (Info[InfoAlphaLight] & ~0xFFFF) | (value & 0xFFFF); }
    public int BetaLight { get => Info[InfoBetaLight]; set => Info[InfoBetaLight] = value; }
    // The high half of the first INF word (CubeBitField).
    public int BitField { get => (Info[InfoAlphaLight] >> 16) & 0xFFFF; set => Info[InfoAlphaLight] = (Info[InfoAlphaLight] & 0xFFFF) | ((value & 0xFFFF) << 16); }

    public short Height(int x, int z) => Heights[z * Vertices + x];
    public byte Light(int x, int z) => (byte)(Intensity[z * Vertices + x] & 15);
    public void SetLight(int x, int z, int light)
    {
        var index = z * Vertices + x;
        Intensity[index] = (byte)((Intensity[index] & 0xF0) | Math.Clamp(light, 0, 15));
    }

    // The two triangles of cell (x, z) are interleaved, as the engine reads them: [z * 128 + x * 2 + half].
    public uint Polygon(int x, int z, int half) => Polygons[z * Cells * 2 + x * 2 + half];
    public void SetPolygon(int x, int z, int half, uint value) => Polygons[z * Cells * 2 + x * 2 + half] = value;
}

// A whole island (.ILE): the 16 x 16 cube map, the two texture atlases and up to 127 cubes, over an HqrFile so a save
// writes back only the records that changed and leaves the rest byte for byte.
//
// Records: 0 = cube map (one byte per cell: cube id in the low 7 bits, 0x80 a flag), 1 = ground texture 256 x 256,
// 2 = object texture 256 x 256, then per cube id c (1..127) six records from 3 + 6 * (c - 1): INF, DOB, GRD, TXD, Y, LUM.
internal sealed class IslandFile
{
    public const int MapSize = 16, CellSize = 512, CubeSize = 64 * 512;
    private const int FirstCubeRecord = 3, RecordsPerCube = 6;
    private const int RecInfo = 0, RecDecors = 1, RecPolygons = 2, RecTextures = 3, RecHeights = 4, RecIntensity = 5;

    private HqrFile archive;
    private readonly byte[]?[] originalPayloads;

    public string Path { get; private set; }
    public byte[] Map { get; }
    public byte[] MapTail { get; private set; } = Array.Empty<byte>();
    public byte[] GroundTexture { get; }
    public byte[] GroundTail { get; private set; } = Array.Empty<byte>();
    public byte[] ObjectTexture { get; }
    public byte[] ObjectTail { get; private set; } = Array.Empty<byte>();
    public SortedDictionary<int, IslandCube> Cubes { get; } = new();

    private IslandFile(string path, HqrFile archive, byte[] map, byte[] ground, byte[] objects)
    {
        Path = path; this.archive = archive; Map = map; GroundTexture = ground; ObjectTexture = objects;
        originalPayloads = new byte[]?[archive.Count];
    }

    public static IslandFile Load(string path) => Parse(File.ReadAllBytes(path), path);

    public static IslandFile Parse(byte[] bytes, string path = "")
    {
        var hqr = HqrFile.Parse(bytes);
        if (hqr.Count < 3) throw new InvalidDataException("The ILE archive does not contain the island texture records.");
        var mapRecord = hqr.Read(0);
        var ground = hqr.Read(1);
        var objects = hqr.Read(2);
        if (mapRecord.Length < MapSize * MapSize) throw new InvalidDataException("The ILE island map is incomplete.");
        if (ground.Length < 256 * 256 || objects.Length < 256 * 256) throw new InvalidDataException("The ILE texture atlas is incomplete.");
        var file = new IslandFile(path, hqr, mapRecord[..(MapSize * MapSize)], ground[..(256 * 256)], objects[..(256 * 256)])
        {
            MapTail = mapRecord[(MapSize * MapSize)..], GroundTail = ground[(256 * 256)..], ObjectTail = objects[(256 * 256)..],
        };
        file.originalPayloads[0] = mapRecord; file.originalPayloads[1] = ground; file.originalPayloads[2] = objects;

        for (var id = 1; id < 128; id++)
        {
            var start = FirstCubeRecord + RecordsPerCube * (id - 1);
            if (start + RecIntensity >= hqr.Count) break;
            if (hqr.IsEmpty(start + RecHeights)) continue;
            var heights = hqr.Read(start + RecHeights);
            if (heights.Length < IslandCube.Vertices * IslandCube.Vertices * 2) continue;
            var cube = new IslandCube { Id = id };
            file.originalPayloads[start + RecHeights] = heights;

            if (!hqr.IsEmpty(start + RecInfo))
            {
                var info = hqr.Read(start + RecInfo);
                file.originalPayloads[start + RecInfo] = info;
                var words = Math.Min(IslandCube.InfoSize, info.Length / 4);
                for (var i = 0; i < words; i++) cube.Info[i] = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(i * 4));
                cube.InfoTail = info[(words * 4)..];
            }

            if (!hqr.IsEmpty(start + RecDecors))
            {
                var decors = hqr.Read(start + RecDecors);
                file.originalPayloads[start + RecDecors] = decors;
                var count = cube.Info[IslandCube.InfoNbDecors];
                // the engine reads 12 S32 per object; the files have been seen with a wider record, so the stride is the record size / count
                var stride = count > 0 && decors.Length % count == 0 ? decors.Length / count : 48;
                if (stride < 48) stride = 48;
                cube.DecorStride = stride;
                for (var i = 0; i + stride <= decors.Length; i += stride) cube.Decors.Add(new IslandDecor(decors[i..(i + stride)]));
                cube.DecorTailBytes = decors.Length - cube.Decors.Count * stride;
            }

            if (!hqr.IsEmpty(start + RecPolygons))
            {
                var polygons = hqr.Read(start + RecPolygons);
                file.originalPayloads[start + RecPolygons] = polygons;
                if (polygons.Length >= IslandCube.Cells * IslandCube.Cells * 2 * 4)
                {
                    for (var i = 0; i < cube.Polygons.Length; i++) cube.Polygons[i] = BinaryPrimitives.ReadUInt32LittleEndian(polygons.AsSpan(i * 4));
                    cube.PolygonTail = polygons[(cube.Polygons.Length * 4)..];
                }
                else cube.HasPolygons = false;
            }
            else cube.HasPolygons = false;

            if (!hqr.IsEmpty(start + RecTextures))
            {
                var textures = hqr.Read(start + RecTextures);
                file.originalPayloads[start + RecTextures] = textures;
                cube.TextureDefs = new ushort[textures.Length / 2];
                for (var i = 0; i < cube.TextureDefs.Length; i++) cube.TextureDefs[i] = BinaryPrimitives.ReadUInt16LittleEndian(textures.AsSpan(i * 2));
                cube.TextureTail = textures.Length % 2 == 1 ? textures[^1..] : Array.Empty<byte>();
            }

            for (var i = 0; i < cube.Heights.Length; i++) cube.Heights[i] = BinaryPrimitives.ReadInt16LittleEndian(heights.AsSpan(i * 2));
            cube.HeightTail = heights[(cube.Heights.Length * 2)..];

            if (!hqr.IsEmpty(start + RecIntensity))
            {
                var light = hqr.Read(start + RecIntensity);
                file.originalPayloads[start + RecIntensity] = light;
                if (light.Length >= cube.Intensity.Length)
                {
                    Array.Copy(light, cube.Intensity, cube.Intensity.Length);
                    cube.IntensityTail = light[cube.Intensity.Length..];
                }
                else cube.HasIntensity = false;
            }
            else cube.HasIntensity = false;
            file.Cubes[id] = cube;
        }
        return file;
    }

    public int CubeIdAt(int cubeX, int cubeZ) => Map[cubeZ * MapSize + cubeX] & 0x7F;
    public IslandCube? CubeAt(int cubeX, int cubeZ)
    {
        if ((uint)cubeX >= MapSize || (uint)cubeZ >= MapSize) return null;
        return Cubes.TryGetValue(CubeIdAt(cubeX, cubeZ), out var cube) ? cube : null;
    }

    public (int MinX, int MinZ, int MaxX, int MaxZ) PresentBounds()
    {
        int minX = MapSize, minZ = MapSize, maxX = -1, maxZ = -1;
        for (var z = 0; z < MapSize; z++)
        for (var x = 0; x < MapSize; x++)
        {
            if (CubeAt(x, z) is null) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
        return maxX < 0 ? (0, 0, MapSize - 1, MapSize - 1) : (minX, minZ, maxX, maxZ);
    }

    // The map cells that show cube id `id` (a cube can be repeated; editing one edits all of them).
    public List<(int X, int Z)> CellsOf(int id)
    {
        var list = new List<(int, int)>();
        for (var i = 0; i < Map.Length; i++)
            if ((Map[i] & 0x7F) == id) list.Add((i % MapSize, i / MapSize));
        return list;
    }

    // ---- island-wide vertex grid: (gx, gz) in 0..1024, cell size 512 world units -----------------------------------
    // A vertex on the border of a cube is stored in every neighbouring cube; these accessors read and write all copies.

    public const int GridSize = MapSize * IslandCube.Cells;

    public IEnumerable<(IslandCube Cube, int X, int Z)> Owners(int gx, int gz)
    {
        if (gx < 0 || gz < 0 || gx > GridSize || gz > GridSize) yield break;
        var cx = gx / IslandCube.Cells; var lx = gx % IslandCube.Cells;
        var cz = gz / IslandCube.Cells; var lz = gz % IslandCube.Cells;
        for (var dz = 0; dz < (lz == 0 && cz > 0 ? 2 : 1); dz++)
        for (var dx = 0; dx < (lx == 0 && cx > 0 ? 2 : 1); dx++)
        {
            var ox = cx - dx; var oz = cz - dz;
            if (ox >= MapSize || oz >= MapSize) continue;
            if (CubeAt(ox, oz) is not { } cube) continue;
            yield return (cube, dx == 1 ? IslandCube.Cells : lx, dz == 1 ? IslandCube.Cells : lz);
        }
    }

    public bool HasVertex(int gx, int gz) => Owners(gx, gz).Any();

    public short? HeightAt(int gx, int gz)
    {
        foreach (var (cube, x, z) in Owners(gx, gz)) return cube.Height(x, z);
        return null;
    }

    public void SetHeight(int gx, int gz, int height)
    {
        var value = (short)Math.Clamp(height, short.MinValue, short.MaxValue);
        foreach (var (cube, x, z) in Owners(gx, gz)) cube.Heights[z * IslandCube.Vertices + x] = value;
    }

    public int? LightAt(int gx, int gz)
    {
        foreach (var (cube, x, z) in Owners(gx, gz)) return cube.HasIntensity ? cube.Light(x, z) : null;
        return null;
    }

    public void SetLight(int gx, int gz, int light)
    {
        foreach (var (cube, x, z) in Owners(gx, gz)) if (cube.HasIntensity) cube.SetLight(x, z, light);
    }

    // The map cell (cube coordinates) and cell in it of a world position.
    public static (int Gx, int Gz) VertexOf(double worldX, double worldZ) => ((int)Math.Round(worldX / CellSize), (int)Math.Round(worldZ / CellSize));

    // ---- saving --------------------------------------------------------------------------------------------------------

    // The island as a file: unchanged records keep their stored bytes (compression included), changed records are
    // written as stored entries (the engine loads both).
    public byte[] ToBytes()
    {
        var file = HqrFile.Parse(archive.ToBytes());
        void Put(int slot, byte[] payload)
        {
            var original = originalPayloads[slot];
            if (original is not null && original.AsSpan().SequenceEqual(payload)) return;
            file.SetEntry(slot, HqrWriter.StoredEntry(payload));
        }

        Put(0, Concat(Map, MapTail));
        Put(1, Concat(GroundTexture, GroundTail));
        Put(2, Concat(ObjectTexture, ObjectTail));
        foreach (var (id, cube) in Cubes)
        {
            var start = FirstCubeRecord + RecordsPerCube * (id - 1);
            cube.Info[IslandCube.InfoNbDecors] = cube.Decors.Count;
            var info = new byte[cube.Info.Length * 4];
            for (var i = 0; i < cube.Info.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(i * 4), cube.Info[i]);
            if (originalPayloads[start + RecInfo] is not null) Put(start + RecInfo, Concat(info, cube.InfoTail));

            if (originalPayloads[start + RecDecors] is not null || cube.Decors.Count > 0)
            {
                var decors = new byte[cube.Decors.Count * cube.DecorStride + cube.DecorTailBytes];
                for (var i = 0; i < cube.Decors.Count; i++) cube.Decors[i].Raw.CopyTo(decors, i * cube.DecorStride);
                if (originalPayloads[start + RecDecors] is { } old && cube.DecorTailBytes > 0) old.AsSpan(old.Length - cube.DecorTailBytes).CopyTo(decors.AsSpan(cube.Decors.Count * cube.DecorStride));
                Put(start + RecDecors, decors);
            }

            if (cube.HasPolygons)
            {
                var polygons = new byte[cube.Polygons.Length * 4];
                for (var i = 0; i < cube.Polygons.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(polygons.AsSpan(i * 4), cube.Polygons[i]);
                Put(start + RecPolygons, Concat(polygons, cube.PolygonTail));
            }

            if (originalPayloads[start + RecTextures] is not null)
            {
                var textures = new byte[cube.TextureDefs.Length * 2];
                for (var i = 0; i < cube.TextureDefs.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(textures.AsSpan(i * 2), cube.TextureDefs[i]);
                Put(start + RecTextures, Concat(textures, cube.TextureTail));
            }

            var heights = new byte[cube.Heights.Length * 2];
            for (var i = 0; i < cube.Heights.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(heights.AsSpan(i * 2), cube.Heights[i]);
            Put(start + RecHeights, Concat(heights, cube.HeightTail));

            if (cube.HasIntensity) Put(start + RecIntensity, Concat(cube.Intensity, cube.IntensityTail));
        }
        return file.ToBytes();
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        if (b.Length == 0) return a;
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0); b.CopyTo(result, a.Length);
        return result;
    }

    // Writes the island next to its .bak (FileTransaction: verify the new file parses back the same, then swap).
    public void Save(string? path = null)
    {
        var target = path ?? Path;
        var bytes = ToBytes();
        new FileTransaction().Write(target, bytes, VerifySaved).Commit();
        Path = target;
        // what is on disk is now the baseline
        var reloaded = Parse(bytes, target);
        archive = HqrFile.Parse(bytes);
        for (var i = 0; i < originalPayloads.Length && i < reloaded.originalPayloads.Length; i++) originalPayloads[i] = reloaded.originalPayloads[i];
    }

    private string? VerifySaved(byte[] bytes)
    {
        try
        {
            var back = Parse(bytes);
            if (back.Cubes.Count != Cubes.Count) return "the saved island has a different number of cubes";
            foreach (var (id, cube) in Cubes)
            {
                if (!back.Cubes.TryGetValue(id, out var other)) return $"cube {id} is missing after the save";
                if (!other.Heights.AsSpan().SequenceEqual(cube.Heights)) return $"cube {id}: heights differ after the save";
                if (cube.HasIntensity && !other.Intensity.AsSpan().SequenceEqual(cube.Intensity)) return $"cube {id}: light differs after the save";
                if (other.Decors.Count != cube.Decors.Count) return $"cube {id}: decor count differs after the save";
            }
            return null;
        }
        catch (Exception e) { return "the saved island does not parse: " + e.Message; }
    }
}
