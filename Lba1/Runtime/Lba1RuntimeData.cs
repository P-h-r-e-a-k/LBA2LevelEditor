using System.IO;
using LBAAssembler.Scenes;

namespace LBAAssembler.Lba1.Runtime;

// A FILE3D.HQR entity as the engine's SearchBody / SearchAnim read it (FICHE.C): a list of records
// [type][generic number][size][U16 HQR index][has action]...; type 1 is a body, type 3 an animation, 255 ends it.
// A body's action block may carry a fixed collision volume (type 14: xmin, ymin, zmin, xmax, ymax, zmax).
internal sealed class Lba1Entity
{
    public sealed record Body(int Hqr, (int XMin, int YMin, int ZMin, int XMax, int YMax, int ZMax)? Volume);
    public sealed record Anim(int Hqr, byte[]? Actions);

    public Dictionary<int, Body> Bodies { get; } = new();
    public Dictionary<int, Anim> Anims { get; } = new();

    public static Lba1Entity Parse(byte[] d)
    {
        var entity = new Lba1Entity();
        var p = 0;
        while (p + 3 <= d.Length && d[p] != 255)
        {
            int type = d[p], generic = d[p + 1], size = d[p + 2];
            var hqr = p + 5 <= d.Length ? d[p + 3] | d[p + 4] << 8 : -1;
            if (type == 1)
            {
                (int, int, int, int, int, int)? volume = null;
                // has action (1 byte); its first byte is the action type, 14 = collision volume
                if (p + 6 <= d.Length && d[p + 5] != 0 && p + 7 + 12 <= d.Length && d[p + 6] == 14)
                {
                    short S(int at) => (short)(d[at] | d[at + 1] << 8);
                    volume = (S(p + 7), S(p + 9), S(p + 11), S(p + 13), S(p + 15), S(p + 17));
                }
                entity.Bodies[generic] = new Body(hqr & 0x7FFF, volume);
            }
            else if (type == 3)
            {
                // the "has action" byte is the count of the frame actions that follow (FICHE.C GereAnimAction)
                byte[]? actions = null;
                if (p + 5 < d.Length && d[p + 5] != 0)
                {
                    var end = Math.Min(d.Length, p + 2 + Math.Max(size, 1));
                    actions = d[(p + 5)..end];
                }
                entity.Anims[generic] = new Anim(hqr, actions);
            }
            p += 2 + Math.Max(size, 1);
        }
        return entity;
    }
}

// One scene's map for the simulation: the 64 x 25 x 64 cells (block, position) as BufCube holds them, and for every
// block of the scene's library the collision shape of each of its cells.
internal sealed class Lba1Cube
{
    public const int SizeX = 64, SizeY = 25, SizeZ = 64;

    public byte[] Cells { get; private set; }
    private byte[] original;
    private readonly byte[][] shapes;
    private readonly byte[] codes;

    private Lba1Cube(Lba1Cube other)
    {
        Cells = (byte[])other.Cells.Clone();
        original = other.original;
        shapes = other.shapes;
        codes = other.codes;
    }

    // A private copy of the map, which a run may change (grid fragments) without touching the cached one.
    public Lba1Cube Copy() => new(this);

    // MixteMapToCube: the fragment's cells that hold a block replace the map's.
    public void Mix(byte[] fragmentGrid)
    {
        var fragment = Lba1GridCodec.Decode(fragmentGrid);
        for (var i = 0; i + 1 < Cells.Length && i + 1 < fragment.Length; i += 2)
            if (fragment[i] != 0) { Cells[i] = fragment[i]; Cells[i + 1] = fragment[i + 1]; }
    }

    // CopyMapToCube: the map as the scene has it.
    public void Restore() => Array.Copy(original, Cells, Cells.Length);

    public Lba1Cube(byte[] grid, byte[] library)
    {
        Cells = Lba1GridCodec.Decode(grid);
        original = (byte[])Cells.Clone();
        var count = library.Length >= 4 ? (int)(BitConverter.ToUInt32(library, 0) / 4) : 0;
        shapes = new byte[count][];
        codes = new byte[count];
        for (var b = 0; b < count; b++)
        {
            var at = (int)BitConverter.ToUInt32(library, b * 4);
            int extent = library[at] * library[at + 1] * library[at + 2];
            var s = new byte[extent];
            for (var k = 0; k < extent; k++) s[k] = library[at + 3 + k * 4];
            shapes[b] = s;
            codes[b] = extent > 0 ? library[at + 3 + 1] : (byte)0xF0;
        }
    }

    public int BlockCount => shapes.Length;
    public int ExtentOf(int block) => block >= 1 && block <= shapes.Length ? shapes[block - 1].Length : 0;

    // The cell at map coordinates (block, second byte).
    public (int Block, int Second) Cell(int x, int y, int z)
    {
        var i = ((z * SizeX + x) * SizeY + y) * 2;
        return (Cells[i], Cells[i + 1]);
    }

    // The collision shape of a block's cell (0 = free, 1 = solid, 2..13 ramps ...), or the "second byte" of an empty cell,
    // which is used as a collision code as it is.
    public int ShapeOf(int block, int position) => block == 0 ? position : (block <= shapes.Length && position < shapes[block - 1].Length ? shapes[block - 1][position] : 0);

    // WorldCodeBrick: the "sound" byte of the first cell of the block (0xF0 = nothing, 0xF1 = water ...).
    public int GameCodeOf(int block) => block == 0 || block > codes.Length ? 0xF0 : codes[block - 1];
}

// What the simulation needs from a game folder: scenes, grids and block libraries, FILE3D entities, bodies' collision
// boxes, animations and sprites' collision volumes. Read-only; everything is cached.
internal sealed class Lba1RuntimeData
{
    public string Directory { get; }

    private readonly HqrArchive scenes, grids, libraries, entities, bodies, animations, resources;
    private readonly Dictionary<int, SceneModel> sceneCache = new();
    private readonly Dictionary<int, Lba1Cube> cubes = new();
    private readonly Dictionary<int, Lba1Entity?> entityCache = new();
    private readonly Dictionary<int, (int, int, int, int, int, int)?> bodyBoxes = new();
    private readonly Dictionary<int, Lba1Animation?> animationCache = new();
    private short[]? spriteVolumes;

    public Lba1RuntimeData(string directory)
    {
        Directory = directory;
        scenes = HqrArchive.Open(Path.Combine(directory, "SCENE.HQR"));
        grids = HqrArchive.Open(Path.Combine(directory, "LBA_GRI.HQR"));
        libraries = HqrArchive.Open(Path.Combine(directory, "LBA_BLL.HQR"));
        entities = HqrArchive.Open(Path.Combine(directory, "FILE3D.HQR"));
        bodies = HqrArchive.Open(Path.Combine(directory, "BODY.HQR"));
        animations = HqrArchive.Open(Path.Combine(directory, "ANIM.HQR"));
        resources = HqrArchive.Open(Path.Combine(directory, "RESS.HQR"));
        SceneCount = HqrArchive.CountEntries(Path.Combine(directory, "SCENE.HQR"));
    }

    public int SceneCount { get; }

    // Dialogue text bank `file` (0 = general, 2 = game texts, 3 + island = an island's) in the game's language.
    private readonly Dictionary<int, Lba1TextBank?> textBanks = new();
    private HqrArchive? textArchive;
    public int Language { get; set; }

    // A sound effect of SAMPLES.HQR as a WAV file (null when the entry is missing or in a format not handled).
    private HqrArchive? sampleArchive;
    private readonly Dictionary<int, byte[]?> samples = new();

    public byte[]? SampleWav(int sample)
    {
        if (samples.TryGetValue(sample, out var cached)) return cached;
        var path = Path.Combine(Directory, "SAMPLES.HQR");
        if (sampleArchive is null && File.Exists(path)) sampleArchive = HqrArchive.Open(path);
        byte[]? wav = null;
        if (sampleArchive is not null && sampleArchive.IsValid(sample))
        {
            try { wav = Lba1Voc.ToWav(sampleArchive.Read(sample)); }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { }
        }
        return samples[sample] = wav;
    }

    // ---- the game CD image (films and the CD's music) ----

    private Lba1Iso? disc;
    private bool discChecked;

    public Lba1Iso? Disc
    {
        get
        {
            if (!discChecked)
            {
                discChecked = true;
                try { if (Lba1Iso.Find(Directory) is { } path) disc = new Lba1Iso(path); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { disc = null; }
            }
            return disc;
        }
    }

    // A film by name ("INTROD"): from a FLA folder or the game folder when the files are there, else off the CD image.
    public byte[]? Film(string name)
    {
        name = Path.GetFileNameWithoutExtension(name.Trim());
        foreach (var folder in new[] { Path.Combine(Directory, "FLA"), Directory })
        {
            var path = Path.Combine(folder, name + ".FLA");
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }
        return Disc is { } d && d.Files.FirstOrDefault(f => f.Path.EndsWith("/FLA/" + name + ".FLA", StringComparison.OrdinalIgnoreCase)) is { } entry ? d.Read(entry) : null;
    }

    // The sound effects films use are their own archive, FLASAMP.HQR, next to the films.
    private HqrFile? filmSamples;
    private readonly Dictionary<int, byte[]?> filmWavs = new();

    public byte[]? FilmSampleWav(int sample)
    {
        if (filmWavs.TryGetValue(sample, out var cached)) return cached;
        byte[]? wav = null;
        try
        {
            if (filmSamples is null)
            {
                foreach (var folder in new[] { Path.Combine(Directory, "FLA"), Directory })
                {
                    var path = Path.Combine(folder, "FLASAMP.HQR");
                    if (File.Exists(path)) { filmSamples = HqrFile.Parse(File.ReadAllBytes(path)); break; }
                }
                if (filmSamples is null && Disc is { } d && d.Files.FirstOrDefault(f => f.Path.EndsWith("/FLA/FLASAMP.HQR", StringComparison.OrdinalIgnoreCase)) is { } entry)
                    filmSamples = HqrFile.Parse(d.Read(entry));
            }
            if (filmSamples is not null && sample >= 0 && sample < filmSamples.Count && !filmSamples.IsEmpty(sample)) wav = Lba1Voc.ToWav(filmSamples.Read(sample));
        }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException) { }
        return filmWavs[sample] = wav;
    }

    // The CD's audio tracks (music 1..9 are CD tracks 2..10 when the game has the disc), as WAV files: 44.1 kHz, 16-bit,
    // stereo. LBA.DAT is the cue sheet: "TRACK nn AUDIO" then "INDEX 01 mm:ss:ff" for where each starts.
    private List<long>? trackStarts;
    private readonly Dictionary<int, byte[]?> cdTracks = new();

    public bool HasCdMusic => Disc is not null && File.Exists(Path.Combine(Directory, "LBA.DAT"));

    public byte[]? CdTrackWav(int track)
    {
        if (cdTracks.TryGetValue(track, out var cached)) return cached;
        byte[]? wav = null;
        try
        {
            if (trackStarts is null)
            {
                trackStarts = new List<long> { 0 };      // index = track number - 1
                foreach (var line in File.ReadAllLines(Path.Combine(Directory, "LBA.DAT")))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("INDEX 01")) continue;
                    var msf = t[8..].Trim().Split(':');
                    trackStarts.Add((long.Parse(msf[0]) * 60 + long.Parse(msf[1])) * 75 + long.Parse(msf[2]));
                }
                // the first INDEX 01 belongs to track 1 (00:00:00): keep list aligned by track number
                trackStarts.RemoveAt(0);
            }
            if (Disc is { } d && track >= 2 && track <= trackStarts.Count)
            {
                var first = trackStarts[track - 1];
                var end = track < trackStarts.Count ? trackStarts[track] : d.SectorCount;
                var pcm = d.ReadAudio(first, end - first);
                wav = new byte[44 + pcm.Length];
                using var w = new BinaryWriter(new MemoryStream(wav));
                w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVEfmt "u8); w.Write(16);
                w.Write((short)1); w.Write((short)2); w.Write(44100); w.Write(44100 * 4); w.Write((short)4); w.Write((short)16);
                w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
            }
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException or OverflowException) { wav = null; }
        return cdTracks[track] = wav;
    }

    // Spoken dialogue: VOX\EN_000.VOX ... one file per text file, an offset table, then one voice sample (a compressed
    // voice file whose first byte is a "more follows" flag) for each text in the order of the text file.
    private static readonly string[] TextFileNames = { "sys", "cre", "gam", "000", "001", "002", "003", "004", "005", "006", "007", "008", "009", "010", "011" };
    private static readonly string[] LanguagePrefixes = { "EN_", "FR_", "DE_", "SP_", "IT_" };
    private readonly Dictionary<(int, int), byte[]?> speech = new();

    public byte[]? Speech(int textFile, int textId)
    {
        if (speech.TryGetValue((textFile, textId), out var cached)) return cached;
        byte[]? wav = null;
        try
        {
            var index = Text(textFile)?.IndexOf(textId) ?? -1;
            var path = Path.Combine(Directory, "VOX", LanguagePrefixes[Math.Clamp(Language, 0, 4)] + TextFileNames[Math.Clamp(textFile, 0, TextFileNames.Length - 1)].ToUpperInvariant() + ".VOX");
            if (index >= 0 && File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var reader = new BinaryReader(stream);
                var tableSize = (int)reader.ReadUInt32();
                stream.Position = 0;
                var table = new uint[tableSize / 4];
                for (var i = 0; i < table.Length; i++) table[i] = reader.ReadUInt32();
                if (index < table.Length - 1 && table[index] != 0)
                {
                    var parts = new List<byte[]>();
                    var at = (long)table[index];
                    for (var guard = 0; guard < 16; guard++)
                    {
                        stream.Position = at;
                        var head = reader.ReadBytes(10);
                        if (head.Length < 10) break;
                        var stored = (int)BitConverter.ToUInt32(head, 4);
                        var body = new byte[10 + stored];
                        Array.Copy(head, body, 10);
                        reader.Read(body, 10, stored);
                        var voc = HqrArchive.DecodeEntry(body);
                        var more = voc.Length > 0 && voc[0] != 0;    // the first byte replaces the 'C' of "Creative": non-zero = another part follows
                        if (voc.Length > 0) voc[0] = (byte)'C';
                        if (Lba1Voc.ToWav(voc) is { } part) parts.Add(part);
                        at += body.Length;
                        if (!more) break;
                    }
                    wav = parts.Count switch { 0 => null, 1 => parts[0], _ => JoinWavs(parts) };
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or OverflowException)
        {
            wav = null;
        }
        return speech[(textFile, textId)] = wav;
    }

    // Several 8-bit WAVs at one rate, end to end.
    private static byte[] JoinWavs(List<byte[]> parts)
    {
        var pcm = parts.SelectMany(p => p.Skip(44)).ToArray();
        var wav = new byte[44 + pcm.Length];
        Array.Copy(parts[0], wav, 44);
        Array.Copy(BitConverter.GetBytes(36 + pcm.Length), 0, wav, 4, 4);
        Array.Copy(BitConverter.GetBytes(pcm.Length), 0, wav, 40, 4);
        Array.Copy(pcm, 0, wav, 44, pcm.Length);
        return wav;
    }

    // A piece of music (MIDI_MI.HQR entry) as a Standard MIDI File.
    private HqrArchive? midiArchive;
    private readonly Dictionary<int, byte[]?> midis = new();

    public byte[]? MidiFile(int num)
    {
        if (midis.TryGetValue(num, out var cached)) return cached;
        var path = Path.Combine(Directory, "MIDI_MI.HQR");
        if (midiArchive is null && File.Exists(path)) midiArchive = HqrArchive.Open(path);
        byte[]? midi = null;
        if (midiArchive is not null && midiArchive.IsValid(num))
        {
            try { midi = Lba1Xmi.ToMidi(midiArchive.Read(num)); }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException) { }
        }
        return midis[num] = midi;
    }

    public Lba1TextBank? Text(int file)
    {
        if (textBanks.TryGetValue(file, out var cached)) return cached;
        var path = Path.Combine(Directory, "TEXT.HQR");
        if (textArchive is null && File.Exists(path)) textArchive = HqrArchive.Open(path);
        return textBanks[file] = textArchive is null ? null : Lba1TextBank.Load(textArchive, Language, file);
    }

    public SceneModel Scene(int number)
    {
        if (!sceneCache.TryGetValue(number, out var scene)) sceneCache[number] = scene = SceneSerializer.Parse(SceneGame.Lba1, scenes.Read(number));
        return scene;
    }

    public Lba1Cube Cube(int number)
    {
        if (!cubes.TryGetValue(number, out var cube)) cubes[number] = cube = new Lba1Cube(grids.Read(number), libraries.Read(number));
        return cube;
    }

    // A grid fragment: LBA_GRI entries from 120 on, in the same format as a scene's grid.
    public byte[]? GridFragment(int number) => grids.IsValid(120 + number) ? grids.Read(120 + number) : null;

    public Lba1Entity? Entity(int index)
    {
        if (entityCache.TryGetValue(index, out var e)) return e;
        return entityCache[index] = entities.IsValid(index) ? Lba1Entity.Parse(entities.Read(index)) : null;
    }

    // BODY.HQR entry's bounding box (the six words after its first two bytes: x0, x1, ymin, ymax, z0, z1).
    public (int X0, int X1, int YMin, int YMax, int Z0, int Z1)? BodyBox(int hqrBody)
    {
        if (bodyBoxes.TryGetValue(hqrBody, out var box)) return box;
        (int, int, int, int, int, int)? result = null;
        if (bodies.IsValid(hqrBody))
        {
            var d = bodies.Read(hqrBody);
            if (d.Length >= 14)
            {
                short S(int at) => (short)(d[at] | d[at + 1] << 8);
                result = (S(2), S(4), S(6), S(8), S(10), S(12));
            }
        }
        return bodyBoxes[hqrBody] = result;
    }

    public Lba1Animation? Animation(int hqrAnim)
    {
        if (animationCache.TryGetValue(hqrAnim, out var a)) return a;
        return animationCache[hqrAnim] = animations.IsValid(hqrAnim) ? Lba1Animation.Parse(animations.Read(hqrAnim)) : null;
    }

    // Where a sprite's picture starts relative to the actor's projected position (words 0 and 1 of its RESS.HQR entry 3 record).
    public (int X, int Y) SpriteOffset(int sprite)
    {
        SpriteVolume(sprite);
        var at = sprite * 8;
        return sprite >= 0 && at + 2 <= spriteVolumes!.Length ? (spriteVolumes[at], spriteVolumes[at + 1]) : (0, 0);
    }

    // RESS.HQR entry 3: 8 words per sprite; words 2..7 are the collision volume xmin, xmax, ymin, ymax, zmin, zmax.
    public (int XMin, int XMax, int YMin, int YMax, int ZMin, int ZMax)? SpriteVolume(int sprite)
    {
        if (spriteVolumes is null)
        {
            var d = resources.IsValid(3) ? resources.Read(3) : Array.Empty<byte>();
            spriteVolumes = new short[d.Length / 2];
            for (var i = 0; i < spriteVolumes.Length; i++) spriteVolumes[i] = (short)(d[i * 2] | d[i * 2 + 1] << 8);
        }
        var at = sprite * 8 + 2;
        if (sprite < 0 || at + 6 > spriteVolumes.Length) return null;
        return (spriteVolumes[at], spriteVolumes[at + 1], spriteVolumes[at + 2], spriteVolumes[at + 3], spriteVolumes[at + 4], spriteVolumes[at + 5]);
    }
}
