using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Buffers.Binary;

namespace LBA2LevelEditor;

internal sealed class IslandDocument
{
    private const int MapSize = 16;
    private readonly byte[] map;
    private readonly Dictionary<int, short[]> heights;
    private readonly Dictionary<int, uint[]> groundPolygons;
    private readonly Dictionary<int, ushort[]> textureDefinitions;
    private readonly Dictionary<int, byte[]> intensities;
    private readonly Dictionary<int, Decor[]> decors;

    internal readonly record struct Decor(int Body, int X, int Y, int Z, int XMin, int YMin, int ZMin, int XMax, int YMax, int ZMax);

    private IslandDocument(string path, byte[] map, byte[] groundTexture, byte[] objectTexture, Dictionary<int, short[]> heights, Dictionary<int, uint[]> groundPolygons, Dictionary<int, ushort[]> textureDefinitions, Dictionary<int, byte[]> intensities, Dictionary<int, Decor[]> decors, byte[] palette, byte[]? shadeTable, int shadeLevel)
    {
        Path = path;
        this.map = map;
        GroundTexture = groundTexture;
        ObjectTexture = objectTexture;
        this.heights = heights;
        this.groundPolygons = groundPolygons;
        this.textureDefinitions = textureDefinitions;
        this.intensities = intensities;
        this.decors = decors;
        Palette = palette;
        this.shadeTable = shadeTable;
        this.shadeLevel = Math.Clamp(shadeLevel, 0, 15);
    }

    public string Path { get; }
    public byte[] GroundTexture { get; }
    public byte[] ObjectTexture { get; }
    public byte[] Palette { get; }
    private readonly byte[]? shadeTable;
    private readonly int shadeLevel;
    public int CubeCount => map.Count(value => (value & 0x7F) != 0);
    public byte CubeAt(int x, int y) => map[y * MapSize + x];
    public short HeightAt(int cubeId, int x, int y) => heights.TryGetValue(cubeId, out var heightMap) ? heightMap[y * 65 + x] : (short)0;
    public uint PolygonAt(int cubeId, int x, int y) => groundPolygons.TryGetValue(cubeId, out var polygons) ? polygons[y * 128 + x] : 0;
    public ushort[]? TextureAt(int cubeId, int index) => textureDefinitions.TryGetValue(cubeId, out var textures) && index * 6 + 5 < textures.Length ? textures[(index * 6)..(index * 6 + 6)] : null;
    public byte IntensityAt(int cubeId, int x, int y) => intensities.TryGetValue(cubeId, out var values) ? (byte)(values[y * 65 + x] & 15) : (byte)15;
    public Color ColorAt(double u, double v, int lightLevel)
    {
        var x = Math.Clamp((int)Math.Round(u), 0, 255);
        var y = Math.Clamp((int)Math.Round(v), 0, 255);
        var sourceIndex = GroundTexture[y * 256 + x];
        var paletteIndex = sourceIndex * 3;
        var sixBitPalette = Palette.Take(768).Max() <= 63;
        var factor = 0.48 + Math.Clamp(lightLevel, 0, 15) / 15.0 * 0.72;
        return Color.FromRgb(
            ShadeColor(Palette[paletteIndex], sixBitPalette, factor),
            ShadeColor(Palette[paletteIndex + 1], sixBitPalette, factor),
            ShadeColor(Palette[paletteIndex + 2], sixBitPalette, factor));
    }
    public IEnumerable<Decor> DecorsAt(int cubeId) => decors.TryGetValue(cubeId, out var values) ? values : Array.Empty<Decor>();

    public static IslandDocument Open(string path, byte[]? palette = null, byte[]? shadeTable = null, int shadeLevel = 0)
    {
        var archive = HqrArchive.Open(path);
        if (archive.Count < 3) throw new InvalidDataException("The ILE archive does not contain the island texture records.");
        var map = archive.Read(0);
        var ground = archive.Read(1);
        var objects = archive.Read(2);
        if (map.Length < MapSize * MapSize) throw new InvalidDataException("The ILE island map is incomplete.");
        if (ground.Length < 256 * 256 || objects.Length < 256 * 256) throw new InvalidDataException("The ILE texture atlas is incomplete.");
        var heights = new Dictionary<int, short[]>();
        var groundPolygons = new Dictionary<int, uint[]>();
        var textureDefinitions = new Dictionary<int, ushort[]>();
        var intensities = new Dictionary<int, byte[]>();
        var decors = new Dictionary<int, Decor[]>();
        for (var cubeId = 1; cubeId < 128; cubeId++)
        {
            var recordIndex = 7 + (cubeId - 1) * 6;
            if (!archive.IsValid(recordIndex)) continue;
            var polygonRecord = archive.Read(recordIndex - 2);
            var decorIndex = recordIndex - 3;
            if (archive.IsValid(decorIndex))
            {
                var decorRecord = archive.Read(decorIndex);
                var count = decorRecord.Length / 52;
                var values = new Decor[count];
                for (var index = 0; index < count; index++)
                {
                    var span = decorRecord.AsSpan(index * 52);
                    values[index] = new Decor(
                        BinaryPrimitives.ReadInt32LittleEndian(span), BinaryPrimitives.ReadInt32LittleEndian(span[4..]), BinaryPrimitives.ReadInt32LittleEndian(span[8..]), BinaryPrimitives.ReadInt32LittleEndian(span[12..]),
                        BinaryPrimitives.ReadInt32LittleEndian(span[24..]), BinaryPrimitives.ReadInt32LittleEndian(span[28..]), BinaryPrimitives.ReadInt32LittleEndian(span[32..]), BinaryPrimitives.ReadInt32LittleEndian(span[36..]), BinaryPrimitives.ReadInt32LittleEndian(span[40..]), BinaryPrimitives.ReadInt32LittleEndian(span[44..]));
                }
                decors[cubeId] = values;
            }
            if (polygonRecord.Length >= 64 * 64 * 2 * 4)
            {
                var polygons = new uint[64 * 64 * 2];
                for (var index = 0; index < polygons.Length; index++)
                    polygons[index] = BinaryPrimitives.ReadUInt32LittleEndian(polygonRecord.AsSpan(index * 4));
                groundPolygons[cubeId] = polygons;
            }
            var textureRecord = archive.Read(recordIndex - 1);
            if (textureRecord.Length >= 16)
            {
                var textures = new ushort[textureRecord.Length / 2];
                for (var index = 0; index < textures.Length; index++)
                    textures[index] = BinaryPrimitives.ReadUInt16LittleEndian(textureRecord.AsSpan(index * 2));
                textureDefinitions[cubeId] = textures;
            }
            var record = archive.Read(recordIndex);
            if (record.Length < 65 * 65 * 2) continue;
            var heightMap = new short[65 * 65];
            for (var index = 0; index < heightMap.Length; index++)
                heightMap[index] = BinaryPrimitives.ReadInt16LittleEndian(record.AsSpan(index * 2));
            heights[cubeId] = heightMap;
            var intensityRecordIndex = recordIndex + 5;
            if (archive.IsValid(intensityRecordIndex))
            {
                var intensityRecord = archive.Read(intensityRecordIndex);
                if (intensityRecord.Length >= 65 * 65) intensities[cubeId] = intensityRecord[..(65 * 65)];
            }
        }
        return new IslandDocument(path, map, ground, objects, heights, groundPolygons, textureDefinitions, intensities, decors, palette is { Length: >= 768 } ? palette : DefaultPalette(), shadeTable, shadeLevel);
    }

    private static byte[] DefaultPalette() => Enumerable.Repeat((byte)0, 768).ToArray();

    public BitmapSource CreatePreview()
    {
        var pixels = new byte[MapSize * MapSize * 4];
        for (var y = 0; y < MapSize; y++)
        for (var x = 0; x < MapSize; x++)
        {
            var cube = CubeAt(x, y) & 0x7F;
            var pixel = (y * MapSize + x) * 4;
            pixels[pixel] = cube == 0 ? (byte)22 : (byte)(70 + (cube * 37) % 130);
            pixels[pixel + 1] = cube == 0 ? (byte)70 : (byte)Math.Min(255, pixels[pixel] + 28);
            pixels[pixel + 2] = cube == 0 ? (byte)150 : (byte)Math.Min(255, pixels[pixel] + 52);
            pixels[pixel + 3] = 255;
        }
        return BitmapSource.Create(MapSize, MapSize, 96, 96, PixelFormats.Bgra32, null, pixels, MapSize * 4);
    }

    public BitmapSource CreateGroundBitmap(int lightLevel = -1)
    {
        var pixels = new byte[256 * 256 * 4];
        var sixBitPalette = Palette.Take(768).Max() <= 63;
        for (var index = 0; index < 256 * 256; index++)
        {
            var sourceIndex = GroundTexture[index];
            var mappedIndex = shadeTable is { Length: >= 4096 } ? shadeTable[(lightLevel >= 0 ? Math.Clamp(lightLevel, 0, 15) : shadeLevel) * 256 + sourceIndex] : sourceIndex;
            var paletteIndex = mappedIndex * 3;
            var pixel = index * 4;
            var red = ScalePalette(Palette[paletteIndex], sixBitPalette);
            var green = ScalePalette(Palette[paletteIndex + 1], sixBitPalette);
            var blue = ScalePalette(Palette[paletteIndex + 2], sixBitPalette);
            pixels[pixel] = blue;
            pixels[pixel + 1] = green;
            pixels[pixel + 2] = red;
            pixels[pixel + 3] = 255;
        }
        return BitmapSource.Create(256, 256, 96, 96, PixelFormats.Bgra32, null, pixels, 256 * 4);
    }

    private static byte ScalePalette(byte value, bool sixBitPalette) => sixBitPalette ? (byte)Math.Min(255, value * 4) : value;
    private static byte ShadeColor(byte value, bool sixBitPalette, double factor) => (byte)Math.Clamp(ScalePalette(value, sixBitPalette) * factor, 0, 255);
}
