namespace LBAAssembler.Lba1;

// One LBA1 ANIM.HQR entry: a list of key frames, each giving every bone of the body either a rotation
// (type 0: three angles in 1024ths of a turn) or a translation (type 1). Layout: U16 frame count,
// U16 bone count, U16 loop frame, U16 unused, then per frame U16 duration (ms), S16 x y z (root movement)
// and per bone S16 type, S16 x y z.
internal sealed class Lba1Animation
{
    public sealed record BoneFrame(int Type, int X, int Y, int Z);
    // Length is in ticks of the game's 50 Hz timer (the engine compares it with TimerRef differences); StepX/Y/Z is how
    // far the actor moves during the frame, in its own frame of reference (rotated by its facing). Bone 0's angles
    // carry the turn the frame makes: Bones[0].Type is the "master rotation" flag, Bones[0].Y the turn (beta).
    public sealed record KeyFrame(int Length, IReadOnlyList<BoneFrame> Bones, int StepX = 0, int StepY = 0, int StepZ = 0);

    public IReadOnlyList<KeyFrame> Frames { get; }
    public int BoneCount { get; }
    public int LoopFrame { get; }

    private Lba1Animation(List<KeyFrame> frames, int boneCount, int loopFrame)
    {
        Frames = frames;
        BoneCount = boneCount;
        LoopFrame = Math.Clamp(loopFrame, 0, Math.Max(0, frames.Count - 1));
    }

    public static Lba1Animation? Parse(byte[] d)
    {
        if (d.Length < 8) return null;
        int U16(int p) => BitConverter.ToUInt16(d, p);
        int S16(int p) => BitConverter.ToInt16(d, p);
        int frameCount = U16(0), boneCount = U16(2), loop = U16(4);
        if (frameCount < 1 || boneCount < 1 || d.Length < 8 + frameCount * (8 + boneCount * 8)) return null;
        var frames = new List<KeyFrame>();
        var p = 8;
        for (var f = 0; f < frameCount; f++)
        {
            var length = U16(p);
            int stepX = S16(p + 2), stepY = S16(p + 4), stepZ = S16(p + 6);
            p += 8;
            var bones = new List<BoneFrame>(boneCount);
            for (var b = 0; b < boneCount; b++, p += 8) bones.Add(new BoneFrame(S16(p), S16(p + 2), S16(p + 4), S16(p + 6)));
            frames.Add(new KeyFrame(length, bones, stepX, stepY, stepZ));
        }
        return new Lba1Animation(frames, boneCount, loop);
    }

    // Plays the animation: key frame 0, then towards frame 1 over frame 1's duration, and so on; after the
    // last frame it goes back to the loop frame and repeats from there.
    public sealed class Player
    {
        private readonly Lba1Animation animation;
        private int from;
        private int target;
        private double elapsed;

        public Player(Lba1Animation animation)
        {
            this.animation = animation;
            target = animation.Frames.Count > 1 ? 1 : 0;
        }

        public void Advance(double milliseconds)
        {
            if (animation.Frames.Count < 2) return;
            elapsed += milliseconds;
            for (var guard = 0; guard < 10000; guard++)
            {
                var length = Math.Max(1, animation.Frames[target].Length);
                if (elapsed < length) break;
                elapsed -= length;
                from = target;
                target = target + 1 < animation.Frames.Count ? target + 1 : animation.LoopFrame;
            }
        }

        public (int Type, double X, double Y, double Z)[] Current()
        {
            var a = animation.Frames[from];
            var b = animation.Frames[target];
            var t = from == target ? 0 : Math.Clamp(elapsed / Math.Max(1, b.Length), 0, 1);
            var result = new (int, double, double, double)[animation.BoneCount];
            for (var i = 0; i < result.Length; i++)
            {
                var x = a.Bones[i];
                var y = b.Bones[i];
                if (x.Type != 0 || y.Type != 0)
                {
                    result[i] = (y.Type, x.X + (y.X - x.X) * t, x.Y + (y.Y - x.Y) * t, x.Z + (y.Z - x.Z) * t);
                    continue;
                }
                double Angle(int p, int q)
                {
                    var delta = ((q - p) & 1023);
                    if (delta > 512) delta -= 1024;
                    return p + delta * t;
                }
                result[i] = (0, Angle(x.X, y.X), Angle(x.Y, y.Y), Angle(x.Z, y.Z));
            }
            return result;
        }
    }
}

