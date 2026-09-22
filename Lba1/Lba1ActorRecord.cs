using System.Buffers.Binary;
using System.IO;

namespace LBAAssembler.Lba1;

// The editable attributes of one LBA1 scene actor, as stored in the scene record's 35-byte header
// (layout in Lba1Scene.Parse). The hero (actor 0) has only a start position.
internal sealed class Lba1ActorData
{
    public int Index { get; init; }
    public bool IsHero => Index == 0;
    public int Flags { get; set; }
    public int Entity { get; set; }
    public int Body { get; set; }       // -1 = no body (stored as 255)
    public int Anim { get; set; }
    public int Sprite { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int HitForce { get; set; }
    public int Angle { get; set; }
    public int Speed { get; set; }
    public int Move { get; set; }
    public int Armor { get; set; }
    public int LifePoints { get; set; }

    public bool IsSprite => (Flags & 0x0400) != 0;
    public Lba1ActorData Clone() => (Lba1ActorData)MemberwiseClone();
}

internal static class Lba1ActorRecord
{
    public static Lba1ActorData Read(byte[] record, int actorIndex)
    {
        var scene = Lba1Scene.Parse(0, record);
        if (actorIndex == 0)
        {
            var p = scene.HeroPositionOffset;
            return new Lba1ActorData
            {
                Index = 0,
                X = BinaryPrimitives.ReadInt16LittleEndian(record.AsSpan(p)),
                Y = BinaryPrimitives.ReadInt16LittleEndian(record.AsSpan(p + 2)),
                Z = BinaryPrimitives.ReadInt16LittleEndian(record.AsSpan(p + 4)),
            };
        }
        if ((uint)actorIndex >= (uint)scene.ActorHeaderOffsets.Count) throw new InvalidDataException("That actor is no longer in the scene.");
        var h = scene.ActorHeaderOffsets[actorIndex];
        var d = record.AsSpan(h, 35);
        return new Lba1ActorData
        {
            Index = actorIndex,
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(d),
            Entity = BinaryPrimitives.ReadInt16LittleEndian(d[2..]),
            Body = d[4] == 255 ? -1 : d[4],
            Anim = d[5],
            Sprite = BinaryPrimitives.ReadInt16LittleEndian(d[6..]),
            X = BinaryPrimitives.ReadInt16LittleEndian(d[8..]),
            Y = BinaryPrimitives.ReadInt16LittleEndian(d[10..]),
            Z = BinaryPrimitives.ReadInt16LittleEndian(d[12..]),
            HitForce = d[14],
            Angle = BinaryPrimitives.ReadUInt16LittleEndian(d[17..]),
            Speed = BinaryPrimitives.ReadUInt16LittleEndian(d[19..]),
            Move = BinaryPrimitives.ReadUInt16LittleEndian(d[21..]),
            Armor = d[33],
            LifePoints = d[34],
        };
    }

    // A copy of `record` with the actor's fields overwritten (the record's size never changes).
    public static byte[] Patch(byte[] record, Lba1ActorData actor)
    {
        var scene = Lba1Scene.Parse(0, record);
        var result = (byte[])record.Clone();
        static void CheckS16(int v, string what) { if (v < short.MinValue || v > short.MaxValue) throw new InvalidDataException($"{what} must fit 16 bits (-32768..32767)."); }
        static void CheckU8(int v, string what) { if (v < 0 || v > 255) throw new InvalidDataException($"{what} must be 0..255."); }
        static void CheckU16(int v, string what) { if (v < 0 || v > 65535) throw new InvalidDataException($"{what} must be 0..65535."); }

        CheckS16(actor.X, "X"); CheckS16(actor.Y, "Y"); CheckS16(actor.Z, "Z");
        if (actor.IsHero)
        {
            var p = scene.HeroPositionOffset;
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(p), (short)actor.X);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(p + 2), (short)actor.Y);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(p + 4), (short)actor.Z);
            return result;
        }

        if ((uint)actor.Index >= (uint)scene.ActorHeaderOffsets.Count) throw new InvalidDataException("That actor is no longer in the scene.");
        CheckU16(actor.Flags, "Flags"); CheckS16(actor.Entity, "Entity"); CheckS16(actor.Sprite, "Sprite");
        if (actor.Body < -1 || actor.Body > 254) throw new InvalidDataException("Body must be -1 (none) or 0..254.");
        CheckU8(actor.Anim, "Animation"); CheckU8(actor.HitForce, "Hit force"); CheckU16(actor.Angle, "Facing");
        CheckU16(actor.Speed, "Speed"); CheckU16(actor.Move, "Move type"); CheckU8(actor.Armor, "Armour"); CheckU8(actor.LifePoints, "Life points");

        var d = result.AsSpan(scene.ActorHeaderOffsets[actor.Index], 35);
        BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)actor.Flags);
        BinaryPrimitives.WriteInt16LittleEndian(d[2..], (short)actor.Entity);
        d[4] = actor.Body < 0 ? (byte)255 : (byte)actor.Body;
        d[5] = (byte)actor.Anim;
        BinaryPrimitives.WriteInt16LittleEndian(d[6..], (short)actor.Sprite);
        BinaryPrimitives.WriteInt16LittleEndian(d[8..], (short)actor.X);
        BinaryPrimitives.WriteInt16LittleEndian(d[10..], (short)actor.Y);
        BinaryPrimitives.WriteInt16LittleEndian(d[12..], (short)actor.Z);
        d[14] = (byte)actor.HitForce;
        BinaryPrimitives.WriteUInt16LittleEndian(d[17..], (ushort)actor.Angle);
        BinaryPrimitives.WriteUInt16LittleEndian(d[19..], (ushort)actor.Speed);
        BinaryPrimitives.WriteUInt16LittleEndian(d[21..], (ushort)actor.Move);
        d[33] = (byte)actor.Armor;
        d[34] = (byte)actor.LifePoints;
        return result;
    }
}
