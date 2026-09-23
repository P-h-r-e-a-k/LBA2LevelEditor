using System.IO;

namespace LbaBodyStudio;

// One keyframe's per-bone-slot value: Type 0 = rotation (X/Y/Z in TURNS, 0..1 = a full circle --
// converted to the target game's own angle unit at Write() time, see Anim.Write's own comment for
// why that conversion has to happen per game); Type 1 = translation (X/Y/Z in raw model-space
// units, same scale as Body vertex coordinates, NOT angle-scaled). Slot 0 of every frame is the
// animation's own root/master record, not a real body bone -- see AnimFrame's own comment.
public sealed record AnimBone(int Type, float X, float Y, float Z)
{
    public static readonly AnimBone Neutral = new(0, 0, 0, 0);
}

// Bones[0] is the master/root slot (X/Y/Z = the whole object's own rotation delta this frame, only
// applied if Type's bit0 is set -- Type's bit1 suppresses gravity/ground-snap this frame, leave
// both clear for an ordinary looping cycle). Bones[1..] map positionally to the target body's own
// bones[1..] (body bone 0 is the root/pivot-only bone and never gets its own slot -- the body's
// own bone COUNT, not bone count minus one, is what Bones.Count must equal here: slot 0 covers the
// "no slot" body bone 0's absence AND carries the master rotation in the same breath).
public sealed record AnimFrame(int FrameTime, float StepX, float StepY, float StepZ, AnimBone[] Bones);

public sealed class Anim
{
    public int Game = 2;
    public int LoopFrame;
    public List<AnimFrame> Frames = [];

    public void Validate()
    {
        if (Frames.Count < 1) throw new InvalidDataException("An animation needs at least one keyframe.");
        var nbBones = Frames[0].Bones.Length;
        if (nbBones < 1 || nbBones > 30) throw new InvalidDataException("Animation exceeds the engine's 30-bone-slot limit.");
        foreach (var f in Frames) if (f.Bones.Length != nbBones) throw new InvalidDataException("Every keyframe must give the same number of bone slots.");
        if (LoopFrame < 0 || LoopFrame >= Frames.Count) throw new InvalidDataException("LoopFrame must be a real keyframe index.");
    }

    public byte[] Write()
    {
        Validate();
        using var s = new MemoryStream(); using var w = new BinaryWriter(s);
        var nbBones = Frames[0].Bones.Length;
        w.Write((ushort)Frames.Count);
        w.Write((ushort)nbBones);
        w.Write((ushort)LoopFrame);
        w.Write((ushort)0);
        // A full turn is 1024 units in LBA1, 4096 in LBA2 (verified against both games' own
        // source -- see reference-anim-hqr-format memory) -- the SAME real-world angle needs a
        // different raw value per game, so this can't be baked into AnimBone itself; it has to
        // happen here, once, at the point the target game is actually known.
        var turnUnits = Game == 1 ? 1024 : 4096;
        short Angle(float turns)
        {
            var raw = (int)MathF.Round(turns * turnUnits);
            var wrapped = ((raw % turnUnits) + turnUnits) % turnUnits; // normalize to [0, turnUnits)
            if (wrapped > turnUnits / 2) wrapped -= turnUnits;         // to (-turnUnits/2, turnUnits/2]
            return (short)wrapped;
        }
        short S16(float v) => (short)System.Math.Clamp(MathF.Round(v), short.MinValue, short.MaxValue);
        foreach (var f in Frames)
        {
            w.Write((ushort)f.FrameTime);
            w.Write(S16(f.StepX)); w.Write(S16(f.StepY)); w.Write(S16(f.StepZ));
            foreach (var b in f.Bones)
            {
                w.Write((short)b.Type);
                if ((b.Type & 1) == 0) { w.Write(Angle(b.X)); w.Write(Angle(b.Y)); w.Write(Angle(b.Z)); }
                else { w.Write(S16(b.X)); w.Write(S16(b.Y)); w.Write(S16(b.Z)); }
            }
        }
        return s.ToArray();
    }
}
