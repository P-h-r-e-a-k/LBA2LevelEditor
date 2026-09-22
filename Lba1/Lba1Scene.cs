using System.Buffers.Binary;

namespace LBAAssembler.Lba1;

// One scene actor. Positions are world units (512 per brick horizontally, 256 vertically).
// Entity is the FILE3D.HQR entity (-1 for none); Body picks one of that entity's bodies and
// Anim one of its animations. Sprite actors (keys, coins...) have entity -1 and a sprite id.
internal sealed record Lba1Actor(
    int Index, int StaticFlags, int Entity, int Body, int Anim, int Sprite,
    int X, int Y, int Z, int Angle, int Speed, int ControlMode,
    int Armor, int LifePoints, int TalkColor, byte[] TrackScript, byte[] LifeScript)
{
    public bool IsSprite => (StaticFlags & 0x0400) != 0;
    public bool IsInvisible => (StaticFlags & 0x0200) != 0;
    public bool HasEntity => !IsSprite && Entity >= 0;
}

// A trigger box (coordinates in world units). Type: 0 cube change (Info[0] = destination scene,
// Info[1..3] = arrival position), 1 camera, 2 scenaric, 3 grid fragment, 4 giver, 5 message, 6 ladder.
internal sealed record Lba1Zone(int X0, int Y0, int Z0, int X1, int Y1, int Z1, int Type, int[] Info, int Snap, int Num);

internal sealed record Lba1TrackPoint(int X, int Y, int Z);

// A parsed LBA1 SCENE.HQR record (all little endian):
//   island (text bank), game-over scene, 4 unused bytes, light angles (2 words),
//   4 ambient samples (id, repeat, round), 2 delays, music, hero start (3 words),
//   hero track script, hero life script, actor count (hero included), then per actor a
//   35-byte header + track script + life script, zone count + 24-byte zones,
//   track point count + 6-byte points.
internal sealed class Lba1Scene
{
    public int Index { get; init; }
    public int Island { get; init; }
    public int GameOverScene { get; init; }
    public int AlphaLight { get; init; }
    public int BetaLight { get; init; }
    public int Music { get; init; }
    public List<Lba1Actor> Actors { get; init; } = new();
    public List<Lba1Zone> Zones { get; init; } = new();
    public List<Lba1TrackPoint> Tracks { get; init; } = new();
    // Position of each zone record inside the scene record (24 bytes each), for patching zones in place.
    public List<int> ZoneOffsets { get; init; } = new();
    // Where the hero start position (three S16) and each further actor's 35-byte attribute header
    // (index 0 = the hero, which has none: -1) sit in the record, for patching actors in place.
    public int HeroPositionOffset { get; init; }
    // Where the U16 actor count (hero included) sits, and the U16 zone count (the zone records follow it; the
    // actors end right before it).
    public int ActorCountOffset { get; init; }
    public int ZoneCountOffset { get; init; }
    public List<int> ActorHeaderOffsets { get; init; } = new();
    public int BytesParsed { get; init; }
    public int BytesTotal { get; init; }

    public static Lba1Scene Parse(int index, byte[] d)
    {
        var p = 0;
        int U8() => d[p++];
        int U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p)); p += 2; return v; }
        int S16() { var v = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(p)); p += 2; return v; }
        byte[] Bytes(int n) { var b = d.AsSpan(p, n).ToArray(); p += n; return b; }

        var island = U8();
        var gameOver = U8();
        p += 4;
        var alpha = U16();
        var beta = U16();
        p += 4 * 6;
        p += 4;
        var music = U8();

        var heroPositionOffset = p;
        var heroX = S16(); var heroY = S16(); var heroZ = S16();
        var heroTrack = Bytes(U16());
        var heroLife = Bytes(U16());

        var actors = new List<Lba1Actor>
        {
            new(0, 0, 0, 0, 0, 0, heroX, heroY, heroZ, 0, 0, 0, 0, 0, 0, heroTrack, heroLife),
        };

        var headerOffsets = new List<int> { -1 };
        var actorCountOffset = p;
        var count = U16();
        for (var a = 1; a < count; a++)
        {
            headerOffsets.Add(p);
            var flags = U16();
            var entity = S16();
            var body = U8();
            var anim = U8();
            var sprite = S16();
            var x = S16(); var y = S16(); var z = S16();
            p += 1;              // strength of hit
            p += 2;              // bonus parameter
            var angle = U16();
            var speed = U16();
            var control = U16();
            p += 8;              // crop rectangle
            p += 1;              // bonus amount
            var talk = U8();
            var armor = U8();
            var life = U8();
            var track = Bytes(U16());
            var lifeScript = Bytes(U16());
            actors.Add(new Lba1Actor(a, flags, entity, body, anim, sprite, x, y, z, angle, speed, control, armor, life, talk, track, lifeScript));
        }

        var zones = new List<Lba1Zone>();
        var zoneOffsets = new List<int>();
        var zoneCountOffset = p;
        var zoneCount = U16();
        for (var i = 0; i < zoneCount; i++)
        {
            zoneOffsets.Add(p);
            var x0 = S16(); var y0 = S16(); var z0 = S16();
            var x1 = S16(); var y1 = S16(); var z1 = S16();
            var type = U16();
            var info = new[] { S16(), S16(), S16(), S16() };
            var snap = S16();
            zones.Add(new Lba1Zone(x0, y0, z0, x1, y1, z1, type, info, snap, i));
        }

        var tracks = new List<Lba1TrackPoint>();
        var trackCount = U16();
        for (var i = 0; i < trackCount; i++) tracks.Add(new Lba1TrackPoint(S16(), S16(), S16()));

        return new Lba1Scene
        {
            Index = index, Island = island, GameOverScene = gameOver, AlphaLight = alpha, BetaLight = beta,
            Music = music, Actors = actors, Zones = zones, ZoneOffsets = zoneOffsets, Tracks = tracks, BytesParsed = p, BytesTotal = d.Length,
            HeroPositionOffset = heroPositionOffset, ActorHeaderOffsets = headerOffsets, ActorCountOffset = actorCountOffset, ZoneCountOffset = zoneCountOffset,
        };
    }
}
