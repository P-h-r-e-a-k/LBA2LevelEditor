using System.Buffers.Binary;

namespace LBA2LevelEditor.LbaScript;

// One actor slot of a scene record: object 0 is the hero (its record has
// only start position + scripts), objects 1.. carry the full attribute block.
// Only the two script blobs matter to the translator; everything else in the
// record is kept as opaque bytes so a rewritten scene differs from the
// original in nothing but those blobs.
internal sealed class SceneActor
{
    public int Index;
    public uint Flags;          // object 0: 0
    public int X, Y, Z;         // cube-local position (object 0: hero start)
    public int TrackSizePos;    // position of the S16 length prefix in the raw record
    public int TrackPos;
    public int TrackLen;
    public int LifeSizePos;
    public int LifePos;
    public int LifeLen;
}

// Parser for one decompressed SCENE.HQR record, following DISKFUNC.CPP's
// LoadScene() field-for-field (only as far as the scripts: everything after
// the last object -- checksum, zones, track points, patches -- is passed
// through untouched).
internal sealed class SceneRecord
{
    private const uint Anim3ds = 1u << 18; // COMMON.H ANIM_3DS

    private readonly byte[] raw;
    public IReadOnlyList<SceneActor> Actors { get; }
    public int TailPos { get; }

    public byte Island => raw[0];
    public byte CubeMode => raw[5];
    public byte CubeX => raw[1];
    public byte CubeY => raw[2];

    private SceneRecord(byte[] raw, List<SceneActor> actors, int tailPos)
    {
        this.raw = raw;
        Actors = actors;
        TailPos = tailPos;
    }

    public ReadOnlySpan<byte> Track(SceneActor a) => raw.AsSpan(a.TrackPos, a.TrackLen);
    public ReadOnlySpan<byte> Life(SceneActor a) => raw.AsSpan(a.LifePos, a.LifeLen);

    public static SceneRecord Parse(byte[] raw)
    {
        var p = 0;
        int Need(int n)
        {
            if (p + n > raw.Length) throw new ScriptFormatException("Scene record is truncated", p);
            var at = p; p += n; return at;
        }
        short S16At(int at) => BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(at));

        // Island, CurrentCubeX/Y, ShadowLevel, ModeLabyrinthe, CubeMode, n
        Need(7);
        Need(4);        // AlphaLight, BetaLight
        Need(20 * 2);   // 4 x (SampleAmbiance, Repeat, Rnd, Freq, Vol)
        Need(4);        // SecondMin, SecondEcart
        Need(1);        // CubeJingle
        var heroPos = Need(6);        // hero start X/Y/Z

        var actors = new List<SceneActor>();

        SceneActor ReadScripts(int index, uint flags)
        {
            var a = new SceneActor { Index = index, Flags = flags };
            a.TrackSizePos = Need(2);
            a.TrackLen = S16At(a.TrackSizePos);
            if (a.TrackLen < 0) throw new ScriptFormatException("Negative track script size", a.TrackSizePos);
            a.TrackPos = Need(a.TrackLen);
            a.LifeSizePos = Need(2);
            a.LifeLen = S16At(a.LifeSizePos);
            if (a.LifeLen < 0) throw new ScriptFormatException("Negative life script size", a.LifeSizePos);
            a.LifePos = Need(a.LifeLen);
            return a;
        }

        var hero = ReadScripts(0, 0);
        hero.X = S16At(heroPos); hero.Y = S16At(heroPos + 2); hero.Z = S16At(heroPos + 4);
        actors.Add(hero);

        var nbObjects = S16At(Need(2));
        for (var n = 1; n < nbObjects; n++)
        {
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(Need(4)));
            Need(2);                // IndexFile3D
            Need(1);                // GenBody
            Need(2);                // GenAnim
            Need(2);                // Sprite
            var pos = Need(6);      // X, Y, Z
            Need(1);                // HitForce
            Need(2);                // OptionFlags
            Need(2);                // Beta
            Need(2);                // SRot
            Need(1);                // Move
            Need(8);                // Info..Info3
            Need(2);                // NbBonus
            Need(1);                // CoulObj
            if ((flags & Anim3ds) != 0) Need(4 + 2); // A3DS.Num, NbFps
            Need(1);                // Armure
            Need(1);                // LifePoint
            var obj = ReadScripts(n, flags);
            obj.X = S16At(pos); obj.Y = S16At(pos + 2); obj.Z = S16At(pos + 4);
            actors.Add(obj);
        }

        return new SceneRecord(raw, actors, p);
    }

    // Serializes the record with the given scripts substituted. Anything not
    // in the map keeps its original bytes; the S16 length prefixes are
    // recomputed. Scripts must fit an S16 length.
    public byte[] Rebuild(IReadOnlyDictionary<(int Actor, ScriptKind Kind), byte[]> replacements)
    {
        var output = new List<byte>(raw.Length + 256);
        var cursor = 0;

        void CopyUntil(int pos)
        {
            for (; cursor < pos; cursor++) output.Add(raw[cursor]);
        }

        void Splice(int sizePos, int blobPos, int blobLen, byte[]? replacement)
        {
            CopyUntil(sizePos);
            if (replacement is null) return; // leave prefix + blob to the next CopyUntil
            if (replacement.Length > short.MaxValue) throw new ScriptFormatException("Script exceeds the 32767-byte limit of the scene format");
            output.Add(unchecked((byte)replacement.Length));
            output.Add(unchecked((byte)(replacement.Length >> 8)));
            output.AddRange(replacement);
            cursor = blobPos + blobLen;
        }

        foreach (var a in Actors)
        {
            Splice(a.TrackSizePos, a.TrackPos, a.TrackLen, replacements.GetValueOrDefault((a.Index, ScriptKind.Track)));
            Splice(a.LifeSizePos, a.LifePos, a.LifeLen, replacements.GetValueOrDefault((a.Index, ScriptKind.Life)));
        }
        CopyUntil(raw.Length);
        return output.ToArray();
    }
}
