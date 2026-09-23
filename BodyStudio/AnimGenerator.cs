namespace LbaBodyStudio;

// Procedural (sine-driven) walk/run/idle/jump cycles for the standard 19-bone Twinsen-topology rig
// every custom body in this project (MarioCustom.cs and its own siblings) shares -- no motion-
// capture or reference data exists for any of these characters, so hand-tuned joint-angle curves
// are the only practical source for new clips. Bone-slot indices below are body-bone indices
// directly (Anim's own slot 0 is the separate master/root record, not a body bone -- see Anim.cs's
// own comment -- so AnimFrame.Bones[i] for i>=1 corresponds to body bone i exactly, matching the
// rig's own Parents array: 2=hips, 3=upper torso, 4/6=upper arms (R/L), 5/7=forearms, 8/11=thighs
// (R/L), 9/12=shins, 10/13=feet, 14=head, 15=cap, 1/16/17/18=empty accessory bones).
public static class AnimGenerator
{
    const int Hips = 2, Torso = 3, ArmR = 4, ForeR = 5, ArmL = 6, ForeL = 7;
    const int ThighR = 8, ShinR = 9, FootR = 10, ThighL = 11, ShinL = 12, FootL = 13;
    const int Head = 14;

    static float Turns(float degrees) => degrees / 360f;

    // A neutral (all-zero rotation) full bone-slot array for the given body bone count (bones[0]
    // is the body's own root/pivot-only bone and never gets a real slot -- see AnimFrame.Bones's
    // own comment -- so nbBodyBones slots are produced, index 0 standing in for the master record).
    static AnimBone[] Neutral(int nbBodyBones)
    {
        var bones = new AnimBone[nbBodyBones];
        for (var i = 0; i < nbBodyBones; i++) bones[i] = AnimBone.Neutral;
        return bones;
    }

    // Alpha=X-axis pitch (forward/back swing -- confirmed via Lba1Animation.cs's own comment that
    // Beta, not Alpha, "carries the turn"/yaw), so a limb's forward-back gait swing is on X.
    static AnimBone Pitch(float degrees) => new(0, Turns(degrees), 0, 0);

    // frames: how many evenly-spaced keyframes make up one full gait cycle (a higher count gives a
    // smoother swing at the cost of a bigger file -- 8 is a reasonable default). frameTicks: engine
    // timer ticks (~50Hz) between consecutive keyframes -- smaller means a faster cycle (run).
    // swingDeg/kneeDeg/armDeg: peak joint-angle amplitude in degrees. leanDeg: a constant forward
    // torso tilt (0 for walk, a few degrees for run). nbBodyBones: the target body's own Bones.Count
    // (must match exactly -- see reference-anim-hqr-format memory's own NbBones==NbGroupes note).
    public static Anim Gait(int game, int nbBodyBones, int frames, int frameTicks, float swingDeg, float kneeDeg, float armDeg, float leanDeg)
    {
        var anim = new Anim { Game = game, LoopFrame = 0 };
        for (var i = 0; i < frames; i++)
        {
            var phase = i * MathF.PI * 2 / frames;
            var swing = MathF.Sin(phase);           // +1 = right leg fully forward, -1 = fully back
            // Knee bend: only during the forward-swing half of this leg's own cycle (a knee never
            // bends backward), peaking a quarter-cycle after the leg passes under the hip (roughly
            // where a real gait's knee-lift peaks) -- a half-rectified sine offset by a quarter turn.
            var kneeR = MathF.Max(0, MathF.Sin(phase - MathF.PI / 2 * 0.5f));
            var kneeL = MathF.Max(0, MathF.Sin(phase + MathF.PI - MathF.PI / 2 * 0.5f));

            var bones = Neutral(nbBodyBones);
            bones[ThighR] = Pitch(swing * swingDeg);
            bones[ThighL] = Pitch(-swing * swingDeg);
            bones[ShinR] = Pitch(kneeR * kneeDeg);
            bones[ShinL] = Pitch(kneeL * kneeDeg);
            // Arms counter-swing relative to the same-side leg (contralateral coordination).
            bones[ArmR] = Pitch(-swing * armDeg);
            bones[ArmL] = Pitch(swing * armDeg);
            bones[ForeR] = Pitch(MathF.Max(0, -swing) * armDeg * 0.4f);
            bones[ForeL] = Pitch(MathF.Max(0, swing) * armDeg * 0.4f);
            if (leanDeg != 0) bones[Torso] = Pitch(leanDeg);

            var master = leanDeg != 0 ? new AnimBone(0, 0, 0, 0) : AnimBone.Neutral; // no root turn baked in -- see Anim.cs's own comment on slot 0
            var full = new AnimBone[nbBodyBones];
            full[0] = master;
            for (var b = 1; b < nbBodyBones; b++) full[b] = bones[b];
            anim.Frames.Add(new AnimFrame(frameTicks, 0, 0, 0, full));
        }
        return anim;
    }

    public static Anim Walk(int game, int nbBodyBones) => Gait(game, nbBodyBones, frames: 8, frameTicks: 9, swingDeg: 22, kneeDeg: 35, armDeg: 18, leanDeg: 0);
    public static Anim Run(int game, int nbBodyBones) => Gait(game, nbBodyBones, frames: 8, frameTicks: 5, swingDeg: 38, kneeDeg: 55, armDeg: 32, leanDeg: 6);

    // A slow, tiny breathing sway -- torso and arms drift a couple of degrees and back over a long,
    // gentle cycle. Loops seamlessly (frame count chosen so the sine completes exactly one period).
    public static Anim Idle(int game, int nbBodyBones)
    {
        const int frames = 12;
        var anim = new Anim { Game = game, LoopFrame = 0 };
        for (var i = 0; i < frames; i++)
        {
            var phase = i * MathF.PI * 2 / frames;
            var sway = MathF.Sin(phase);
            var bones = Neutral(nbBodyBones);
            bones[Torso] = Pitch(sway * 1.5f);
            bones[ArmR] = Pitch(sway * 2.5f);
            bones[ArmL] = Pitch(sway * 2.5f);
            bones[Head] = Pitch(sway * 1f);
            anim.Frames.Add(new AnimFrame(22, 0, 0, 0, bones));
        }
        return anim;
    }

    // A short, non-looping-feeling clip (LoopFrame freezes on the landing pose, per the "no
    // explicit one-shot bit" finding in reference-anim-hqr-format -- the calling game logic is
    // expected to swap to a different animation once this one's done, not let it keep repeating):
    // crouch -> launch -> airborne tuck -> land -> settle.
    public static Anim Jump(int game, int nbBodyBones)
    {
        var anim = new Anim { Game = game, LoopFrame = 4 };
        AnimBone[] Pose(float thighDeg, float kneeDeg, float armDeg)
        {
            var bones = Neutral(nbBodyBones);
            bones[ThighR] = Pitch(thighDeg); bones[ThighL] = Pitch(thighDeg);
            bones[ShinR] = Pitch(kneeDeg); bones[ShinL] = Pitch(kneeDeg);
            bones[ArmR] = Pitch(armDeg); bones[ArmL] = Pitch(armDeg);
            return bones;
        }
        anim.Frames.Add(new AnimFrame(10, 0, 0, 0, Pose(10, 30, -20)));   // crouch, ready
        anim.Frames.Add(new AnimFrame(6, 0, 0, 0, Pose(-15, 5, -60)));    // launch, legs kick back, arms up
        anim.Frames.Add(new AnimFrame(14, 0, 0, 0, Pose(20, 45, -40)));   // airborne tuck
        anim.Frames.Add(new AnimFrame(8, 0, 0, 0, Pose(15, 35, -10)));    // landing, absorbing
        anim.Frames.Add(new AnimFrame(10, 0, 0, 0, Pose(0, 0, 0)));       // settled, neutral
        return anim;
    }
}
