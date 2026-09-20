using System.Buffers.Binary;
using LBA2LevelEditor.LbaScript;

namespace LBA2LevelEditor.Scenes;

// SCENE.HQR records <-> SceneModel, following DISKFUNC.C (LBA1) and DISKFUNC.CPP (LBA2) field for field.
//
//   LBA1:  island U8, game over scene U8, 2 x U16 (discarded), light alpha / beta U16, 4 x (sample, repeat, round) U16,
//          delay min / spread U16, jingle U8, hero start 3 x S16, hero track / life (U16 length + bytes),
//          object count U16 (hero included), per object a 35-byte header + track + life,
//          zone count U16 + 24-byte zones, track point count U16 + 6-byte points.
//   LBA2:  7 header bytes (island, cube x, cube y, shadow level, labyrinth mode, cube mode, spare), light alpha / beta S16,
//          4 x (sample, repeat, round, frequency, volume) S16, delay min / spread S16, jingle S8, hero start 3 x S16,
//          hero track / life (S16 length + bytes), object count S16, per object its attributes + track + life,
//          checksum U32, zone count S16 + 60-byte zones, track point count S16 + 12-byte points,
//          then the patch count U32 and table: rebuilt from the scripts on every write (see Lba2Patches), or copied from Tail
//          when SceneModel.RebuildPatches is off.
internal static class SceneSerializer
{
    private const uint Anim3ds = 1u << 18; // COMMON.H ANIM_3DS

    public static SceneModel Parse(SceneGame game, byte[] record) => game == SceneGame.Lba1 ? ParseLba1(record) : ParseLba2(record);

    public static byte[] Write(SceneModel scene) => scene.Game == SceneGame.Lba1 ? WriteLba1(scene) : WriteLba2(scene);

    // ---------------------------------------------------------------------------------------------------- reading

    private sealed class Reader
    {
        private readonly byte[] d;
        public int Position;
        public Reader(byte[] data) { d = data; }
        public int Left => d.Length - Position;

        private void Need(int n)
        {
            if (n < 0 || Position + n > d.Length) throw new SceneFormatException($"The scene record is truncated at byte {Position} (needs {n} more).");
        }
        public int U8() { Need(1); return d[Position++]; }
        public int S8() { Need(1); return (sbyte)d[Position++]; }
        public int U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(Position)); Position += 2; return v; }
        public int S16() { Need(2); var v = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(Position)); Position += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(Position)); Position += 4; return v; }
        public int S32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(Position)); Position += 4; return v; }
        public byte[] Bytes(int n) { Need(n); var b = d.AsSpan(Position, n).ToArray(); Position += n; return b; }
    }

    private static SceneModel ParseLba1(byte[] record)
    {
        var r = new Reader(record);
        var s = new SceneModel { Game = SceneGame.Lba1 };
        s.Island = r.U8();
        s.GameOverScene = r.U8();
        s.Reserved1 = r.U16();
        s.Reserved2 = r.U16();
        s.AlphaLight = r.U16();
        s.BetaLight = r.U16();
        foreach (var a in s.Ambient) { a.Sample = r.U16(); a.Repeat = r.U16(); a.Round = r.U16(); }
        s.SecondMin = r.U16();
        s.SecondEcart = r.U16();
        s.Music = r.U8();

        var hero = new SceneActorModel { X = r.S16(), Y = r.S16(), Z = r.S16() };
        hero.Track = r.Bytes(r.U16());
        hero.Life = r.Bytes(r.U16());
        s.Actors.Add(hero);

        var objects = r.U16();
        for (var n = 1; n < objects; n++)
        {
            var a = new SceneActorModel();
            a.Flags = (uint)r.U16();
            a.Entity = r.S16();
            var body = r.U8();
            a.Body = body == 255 ? -1 : body;
            a.Anim = r.U8();
            a.Sprite = r.S16();
            a.X = r.S16(); a.Y = r.S16(); a.Z = r.S16();
            a.HitForce = r.U8();
            a.OptionFlags = r.U16();
            a.Beta = r.U16();
            a.SRot = r.U16();
            a.Move = r.U16();
            for (var k = 0; k < 4; k++) a.Info[k] = r.S16();
            a.NbBonus = r.U8();
            a.CoulObj = r.U8();
            a.Armor = r.U8();
            a.LifePoints = r.U8();
            a.Track = r.Bytes(r.U16());
            a.Life = r.Bytes(r.U16());
            s.Actors.Add(a);
        }

        var zones = r.U16();
        for (var i = 0; i < zones; i++)
        {
            var z = new SceneZoneModel { X0 = r.S16(), Y0 = r.S16(), Z0 = r.S16(), X1 = r.S16(), Y1 = r.S16(), Z1 = r.S16(), Type = r.U16() };
            for (var k = 0; k < 4; k++) z.Info[k] = r.S16();
            z.Snap = r.S16();
            s.Zones.Add(z);
        }

        var points = r.U16();
        for (var i = 0; i < points; i++) s.TrackPoints.Add(new SceneTrackPoint(r.S16(), r.S16(), r.S16()));
        s.Tail = r.Bytes(r.Left);
        return s;
    }

    private static SceneModel ParseLba2(byte[] record)
    {
        var r = new Reader(record);
        var s = new SceneModel { Game = SceneGame.Lba2 };
        s.Island = r.U8();
        s.CubeX = r.U8();
        s.CubeY = r.U8();
        s.ShadowLevel = r.U8();
        s.LabyrinthMode = r.U8();
        s.CubeMode = r.U8();
        s.HeaderSpare = r.U8();
        s.AlphaLight = r.S16();
        s.BetaLight = r.S16();
        foreach (var a in s.Ambient) { a.Sample = r.S16(); a.Repeat = r.S16(); a.Round = r.S16(); a.Frequency = r.S16(); a.Volume = r.S16(); }
        s.SecondMin = r.S16();
        s.SecondEcart = r.S16();
        s.Music = r.S8();

        var hero = new SceneActorModel { X = r.S16(), Y = r.S16(), Z = r.S16() };
        hero.Track = r.Bytes(NonNegative(r.S16(), "hero track script length"));
        hero.Life = r.Bytes(NonNegative(r.S16(), "hero life script length"));
        s.Actors.Add(hero);

        var objects = r.S16();
        for (var n = 1; n < objects; n++)
        {
            var a = new SceneActorModel();
            a.Flags = r.U32();
            a.Entity = r.S16();
            a.Body = r.S8();
            a.Anim = r.S16();
            a.Sprite = r.S16();
            a.X = r.S16(); a.Y = r.S16(); a.Z = r.S16();
            a.HitForce = r.S8();
            a.OptionFlags = r.S16();
            a.Beta = r.S16();
            a.SRot = r.S16();
            a.Move = r.S8();
            for (var k = 0; k < 4; k++) a.Info[k] = r.S16();
            a.NbBonus = r.S16();
            a.CoulObj = r.S8();
            if ((a.Flags & Anim3ds) != 0) { a.Anim3dsNum = r.U32(); a.Anim3dsFps = r.S16(); }
            a.Armor = r.S8();
            a.LifePoints = r.S8();
            a.Track = r.Bytes(NonNegative(r.S16(), $"object {n} track script length"));
            a.Life = r.Bytes(NonNegative(r.S16(), $"object {n} life script length"));
            s.Actors.Add(a);
        }

        s.Checksum = r.U32();

        var zones = r.S16();
        for (var i = 0; i < zones; i++)
        {
            var z = new SceneZoneModel { X0 = r.S32(), Y0 = r.S32(), Z0 = r.S32(), X1 = r.S32(), Y1 = r.S32(), Z1 = r.S32() };
            z.Info = new int[8];
            for (var k = 0; k < 8; k++) z.Info[k] = r.S32();
            z.Type = r.S16();
            z.Num = r.S16();
            s.Zones.Add(z);
        }

        var points = r.S16();
        for (var i = 0; i < points; i++) s.TrackPoints.Add(new SceneTrackPoint(r.S32(), r.S32(), r.S32()));
        s.Tail = r.Bytes(r.Left);
        return s;
    }

    private static int NonNegative(int value, string what) => value >= 0 ? value : throw new SceneFormatException($"Negative {what}.");

    // ---------------------------------------------------------------------------------------------------- writing

    private sealed class Writer
    {
        public readonly List<byte> Bytes = new(16384);

        private static void Range(long v, long min, long max, string what)
        {
            if (v < min || v > max) throw new SceneFormatException($"{what} = {v} doesn't fit the record ({min}..{max}).");
        }
        public void U8(int v, string what) { Range(v, 0, 255, what); Bytes.Add((byte)v); }
        public void S8(int v, string what) { Range(v, -128, 127, what); Bytes.Add((byte)(sbyte)v); }
        public void U16(int v, string what) { Range(v, 0, 65535, what); Bytes.Add((byte)v); Bytes.Add((byte)(v >> 8)); }
        public void S16(int v, string what) { Range(v, -32768, 32767, what); Bytes.Add((byte)v); Bytes.Add((byte)(v >> 8)); }
        public void S32(int v) { Bytes.Add((byte)v); Bytes.Add((byte)(v >> 8)); Bytes.Add((byte)(v >> 16)); Bytes.Add((byte)(v >> 24)); }
        public void U32(uint v) { S32(unchecked((int)v)); }
        public void Raw(byte[] b) => Bytes.AddRange(b);
    }

    private static byte[] WriteLba1(SceneModel s)
    {
        if (s.Actors.Count < 1) throw new SceneFormatException("A scene needs at least the hero.");
        var w = new Writer();
        w.U8(s.Island, "island");
        w.U8(s.GameOverScene, "game over scene");
        w.U16(s.Reserved1, "reserved word 1");
        w.U16(s.Reserved2, "reserved word 2");
        w.U16(s.AlphaLight, "light alpha");
        w.U16(s.BetaLight, "light beta");
        for (var i = 0; i < 4; i++)
        {
            w.U16(s.Ambient[i].Sample, $"ambient sample {i}");
            w.U16(s.Ambient[i].Repeat, $"ambient repeat {i}");
            w.U16(s.Ambient[i].Round, $"ambient round {i}");
        }
        w.U16(s.SecondMin, "delay minimum");
        w.U16(s.SecondEcart, "delay spread");
        w.U8(s.Music, "music");

        var hero = s.Actors[0];
        w.S16(hero.X, "hero X"); w.S16(hero.Y, "hero Y"); w.S16(hero.Z, "hero Z");
        w.U16(hero.Track.Length, "hero track script length"); w.Raw(hero.Track);
        w.U16(hero.Life.Length, "hero life script length"); w.Raw(hero.Life);

        w.U16(s.Actors.Count, "object count");
        for (var n = 1; n < s.Actors.Count; n++)
        {
            var a = s.Actors[n];
            var what = $"object {n}";
            w.U16(checked((int)a.Flags), $"{what} flags");
            w.S16(a.Entity, $"{what} entity");
            w.U8(a.Body < 0 ? 255 : a.Body, $"{what} body");
            w.U8(a.Anim, $"{what} animation");
            w.S16(a.Sprite, $"{what} sprite");
            w.S16(a.X, $"{what} X"); w.S16(a.Y, $"{what} Y"); w.S16(a.Z, $"{what} Z");
            w.U8(a.HitForce, $"{what} hit force");
            w.U16(a.OptionFlags, $"{what} option flags");
            w.U16(a.Beta, $"{what} facing");
            w.U16(a.SRot, $"{what} speed");
            w.U16(a.Move, $"{what} move");
            for (var k = 0; k < 4; k++) w.S16(a.Info[k], $"{what} info {k}");
            w.U8(a.NbBonus, $"{what} bonus");
            w.U8(a.CoulObj, $"{what} colour");
            w.U8(a.Armor, $"{what} armour");
            w.U8(a.LifePoints, $"{what} life points");
            w.U16(a.Track.Length, $"{what} track script length"); w.Raw(a.Track);
            w.U16(a.Life.Length, $"{what} life script length"); w.Raw(a.Life);
        }

        w.U16(s.Zones.Count, "zone count");
        for (var i = 0; i < s.Zones.Count; i++)
        {
            var z = s.Zones[i];
            var what = $"zone {i}";
            w.S16(z.X0, $"{what} X0"); w.S16(z.Y0, $"{what} Y0"); w.S16(z.Z0, $"{what} Z0");
            w.S16(z.X1, $"{what} X1"); w.S16(z.Y1, $"{what} Y1"); w.S16(z.Z1, $"{what} Z1");
            w.U16(z.Type, $"{what} type");
            if (z.Info.Length != 4) throw new SceneFormatException($"{what}: an LBA1 zone has four info words.");
            for (var k = 0; k < 4; k++) w.S16(z.Info[k], $"{what} info {k}");
            w.S16(z.Snap, $"{what} snap");
        }

        w.U16(s.TrackPoints.Count, "track point count");
        foreach (var p in s.TrackPoints) { w.S16(p.X, "track point X"); w.S16(p.Y, "track point Y"); w.S16(p.Z, "track point Z"); }
        w.Raw(s.Tail);
        return w.Bytes.ToArray();
    }

    private static byte[] WriteLba2(SceneModel s)
    {
        if (s.Actors.Count < 1) throw new SceneFormatException("A scene needs at least the hero.");
        var w = new Writer();
        w.U8(s.Island, "island");
        w.U8(s.CubeX, "cube X"); w.U8(s.CubeY, "cube Y");
        w.U8(s.ShadowLevel, "shadow level"); w.U8(s.LabyrinthMode, "labyrinth mode"); w.U8(s.CubeMode, "cube mode");
        w.U8(s.HeaderSpare, "header spare byte");
        w.S16(s.AlphaLight, "light alpha");
        w.S16(s.BetaLight, "light beta");
        for (var i = 0; i < 4; i++)
        {
            var a = s.Ambient[i];
            w.S16(a.Sample, $"ambient sample {i}"); w.S16(a.Repeat, $"ambient repeat {i}"); w.S16(a.Round, $"ambient round {i}");
            w.S16(a.Frequency, $"ambient frequency {i}"); w.S16(a.Volume, $"ambient volume {i}");
        }
        w.S16(s.SecondMin, "delay minimum");
        w.S16(s.SecondEcart, "delay spread");
        w.S8(s.Music, "music");

        var hero = s.Actors[0];
        w.S16(hero.X, "hero X"); w.S16(hero.Y, "hero Y"); w.S16(hero.Z, "hero Z");
        var sites = new List<Lba2Patches.ScriptSite>();
        w.S16(hero.Track.Length, "hero track script length"); var heroTrackAt = w.Bytes.Count; w.Raw(hero.Track);
        w.S16(hero.Life.Length, "hero life script length"); sites.Add(new(heroTrackAt, hero.Track, w.Bytes.Count, hero.Life)); w.Raw(hero.Life);

        w.S16(s.Actors.Count, "object count");
        for (var n = 1; n < s.Actors.Count; n++)
        {
            var a = s.Actors[n];
            var what = $"object {n}";
            w.U32(a.Flags);
            w.S16(a.Entity, $"{what} entity");
            w.S8(a.Body, $"{what} body");
            w.S16(a.Anim, $"{what} animation");
            w.S16(a.Sprite, $"{what} sprite");
            w.S16(a.X, $"{what} X"); w.S16(a.Y, $"{what} Y"); w.S16(a.Z, $"{what} Z");
            w.S8(a.HitForce, $"{what} hit force");
            w.S16(a.OptionFlags, $"{what} option flags");
            w.S16(a.Beta, $"{what} facing");
            w.S16(a.SRot, $"{what} speed");
            w.S8(a.Move, $"{what} move");
            for (var k = 0; k < 4; k++) w.S16(a.Info[k], $"{what} info {k}");
            w.S16(a.NbBonus, $"{what} bonus");
            w.S8(a.CoulObj, $"{what} colour");
            if ((a.Flags & Anim3ds) != 0) { w.U32(a.Anim3dsNum); w.S16(a.Anim3dsFps, $"{what} animation fps"); }
            w.S8(a.Armor, $"{what} armour");
            w.S8(a.LifePoints, $"{what} life points");
            w.S16(a.Track.Length, $"{what} track script length"); var trackAt = w.Bytes.Count; w.Raw(a.Track);
            w.S16(a.Life.Length, $"{what} life script length"); sites.Add(new(trackAt, a.Track, w.Bytes.Count, a.Life)); w.Raw(a.Life);
        }

        w.U32(s.Checksum);

        w.S16(s.Zones.Count, "zone count");
        for (var i = 0; i < s.Zones.Count; i++)
        {
            var z = s.Zones[i];
            w.S32(z.X0); w.S32(z.Y0); w.S32(z.Z0); w.S32(z.X1); w.S32(z.Y1); w.S32(z.Z1);
            if (z.Info.Length != 8) throw new SceneFormatException($"zone {i}: an LBA2 zone has eight info words.");
            foreach (var v in z.Info) w.S32(v);
            w.S16(z.Type, $"zone {i} type");
            w.S16(z.Num, $"zone {i} number");
        }

        w.S16(s.TrackPoints.Count, "track point count");
        foreach (var p in s.TrackPoints) { w.S32(p.X); w.S32(p.Y); w.S32(p.Z); }
        if (s.RebuildPatches)
        {
            using var scope = Opcodes.Use(Opcodes.Lba2);
            w.Raw(Lba2Patches.Build(sites));
        }
        else w.Raw(s.Tail);
        return w.Bytes.ToArray();
    }
}
