namespace LBAAssembler.Scenes;

internal enum SceneGame { Lba1 = 1, Lba2 = 2 }

// One of the four ambient sample slots of a scene. LBA1 has sample / repeat / round; LBA2 adds frequency and volume.
internal sealed class SceneAmbientSample
{
    public int Sample, Repeat, Round, Frequency, Volume;
    public SceneAmbientSample Clone() => (SceneAmbientSample)MemberwiseClone();
}

// One actor ("object") of a scene, in the engine's numbering: actors[0] is the hero, which has only a start position
// and scripts (the position is stored in X / Y / Z); the others carry a full attribute block.
//
// Field widths follow each game's loader (DISKFUNC.C / DISKFUNC.CPP); values are stored as the engine reads them, with
// Body normalised to -1 for "no body" in both games.
internal sealed class SceneActorModel
{
    public uint Flags;          // LBA1: 16 bits, LBA2: 32 bits
    public int Entity;          // FILE3D entity (S16), -1 for sprites
    public int Body;            // -1 = none
    public int Anim;            // LBA1: 8 bits, LBA2: S16
    public int Sprite;
    public int X, Y, Z;
    public int HitForce;
    public int OptionFlags;     // LBA1 "bonus parameter"
    public int Beta;            // facing
    public int SRot;            // speed
    public int Move;            // control mode
    public int[] Info = new int[4];   // doors: the screen clip rectangle; also follow / track parameters
    public int NbBonus;
    public int CoulObj;         // dialogue colour
    public uint Anim3dsNum;     // LBA2, when Flags has ANIM_3DS
    public int Anim3dsFps;      // LBA2, when Flags has ANIM_3DS
    public int Armor;
    public int LifePoints;
    public byte[] Track = Array.Empty<byte>();
    public byte[] Life = Array.Empty<byte>();

    public bool IsSprite => (Flags & 0x0400) != 0;

    public SceneActorModel Clone()
    {
        var copy = (SceneActorModel)MemberwiseClone();
        copy.Info = (int[])Info.Clone();
        copy.Track = (byte[])Track.Clone();
        copy.Life = (byte[])Life.Clone();
        return copy;
    }
}

// A trigger box. Coordinates are scene units (LBA1: 16 bits, LBA2: 32 bits).
//   LBA1: type, four info words and a snap word;  LBA2: type, eight info words and Num (the destination scene of a
//   cube change, the zone number of a scenaric zone ...).
internal sealed class SceneZoneModel
{
    public int X0, Y0, Z0, X1, Y1, Z1;
    public int Type;
    public int[] Info = new int[4];
    public int Snap;    // LBA1
    public int Num;     // LBA2

    public SceneZoneModel Clone()
    {
        var copy = (SceneZoneModel)MemberwiseClone();
        copy.Info = (int[])Info.Clone();
        return copy;
    }
}

internal readonly record struct SceneTrackPoint(int X, int Y, int Z);

// A whole scene record (one SCENE.HQR entry): everything the engine's LoadScene reads, so a parsed record written back
// is byte-for-byte the original. Header fields that only one game has are ignored (kept at 0) for the other.
internal sealed class SceneModel
{
    public SceneGame Game { get; init; }

    // ---- header, both games ----
    public int Island;
    public int AlphaLight, BetaLight;                       // light direction
    public SceneAmbientSample[] Ambient = Enumerable.Range(0, 4).Select(_ => new SceneAmbientSample()).ToArray();
    public int SecondMin, SecondEcart;                      // delay range between ambient samples
    public int Music;                                       // "CubeJingle"

    // ---- LBA1 header ----
    public int GameOverScene;
    public int Reserved1, Reserved2;                        // two words the loader reads and discards

    // ---- LBA2 header ----
    public int CubeX, CubeY;                                // island cube (outdoor scenes)
    public int ShadowLevel, LabyrinthMode, CubeMode;       // CubeMode 0 = interior, 1 = exterior
    public int HeaderSpare;                                 // the byte the loader reads into a scratch variable

    // ---- objects: [0] is the hero ----
    public List<SceneActorModel> Actors { get; init; } = new();
    public List<SceneZoneModel> Zones { get; init; } = new();
    public List<SceneTrackPoint> TrackPoints { get; init; } = new();

    // ---- LBA2 ----
    public uint Checksum;                                   // compared with the one in save games; left as found
    // Whatever follows the last piece the loader parses: LBA2's patch count and table, LBA1 normally nothing.
    public byte[] Tail = Array.Empty<byte>();
    // LBA2: write the patch table (see Lba2Patches) fresh from the scripts instead of copying Tail. On by default:
    // offsets in a copied table go stale as soon as anything before a script changes size.
    public bool RebuildPatches = true;

    public SceneActorModel Hero => Actors[0];

    public SceneModel Clone()
    {
        var copy = new SceneModel
        {
            Game = Game, Island = Island, AlphaLight = AlphaLight, BetaLight = BetaLight,
            SecondMin = SecondMin, SecondEcart = SecondEcart, Music = Music,
            GameOverScene = GameOverScene, Reserved1 = Reserved1, Reserved2 = Reserved2,
            CubeX = CubeX, CubeY = CubeY, ShadowLevel = ShadowLevel, LabyrinthMode = LabyrinthMode, CubeMode = CubeMode, HeaderSpare = HeaderSpare,
            Checksum = Checksum, Tail = (byte[])Tail.Clone(), RebuildPatches = RebuildPatches,
        };
        copy.Ambient = Ambient.Select(a => a.Clone()).ToArray();
        copy.Actors.AddRange(Actors.Select(a => a.Clone()));
        copy.Zones.AddRange(Zones.Select(z => z.Clone()));
        copy.TrackPoints.AddRange(TrackPoints);
        return copy;
    }
}

// Scene problems reported as an IOException so the editor's existing "Not saved: ..." handling shows them.
internal class SceneEditException : System.IO.IOException
{
    public SceneEditException(string message) : base(message) { }
}

internal sealed class SceneFormatException : SceneEditException
{
    public SceneFormatException(string message) : base(message) { }
}
