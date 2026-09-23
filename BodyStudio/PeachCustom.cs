using System.IO;
using System.Linq;
using System.Numerics;

namespace LbaBodyStudio;

// Princess Peach, hand-authored the same way MarioCustom.cs/LuigiCustom.cs are (see MarioCustom.cs's own comment
// for why: placing rings directly rather than fitting a photo silhouette) -- same 19-bone Twinsen-topology rig,
// same ring/Chain/pivot construction pattern, deliberately NOT sharing code with either of them (this codebase's
// own precedent -- see LuigiCustom.cs's own comment: a parallel implementation per character, not a forced
// abstraction, since each character's own proportions/silhouette are genuinely different data).
//
// Differs from Mario/Luigi in the ways her own design actually differs: noticeably narrower shoulders and no
// visible waist-to-hip step (bone 2, the lower-torso bone, is stretched into a long pink skirt instead of a short
// belt band -- its own bottom ring sits well below hip height, down at upper-thigh height, rather than stopping
// at the hip like Mario's/Luigi's overalls waist do), a gold trim collar, and a crown (bone 15, the same
// head-accessory slot Mario/Luigi use for a cap) instead of a cap. The legs still carry real geometry (the rig
// requires every bone to have some -- see the fallback-vertex loop near the end of this file) but stay thin and
// Skin-coloured rather than a garment colour, since most of their length is meant to read as hidden under the
// dress; only the sliver between the skirt hem (~.33H) and the knee is actually left uncovered, which is a
// deliberate compromise -- a genuinely floor-length rigid skirt on bone 2 would not follow the leg bones' own
// swing during a walk/run animation (bone 2 itself doesn't get that rotation, only bones 8-13 do) and would clip
// visibly through the animated legs, whereas a knee-length hem stays clear of that arc while still reading as
// "a long dress" next to Mario's/Luigi's belt-line overalls.
public static class PeachCustom
{
    static readonly int[] Parents = [-1, 0, 1, 2, 3, 4, 3, 6, 2, 8, 9, 2, 11, 12, 3, 14, 15, 3, 17];

    // game/colour overrides: see MarioCustom.Build's own comment -- each game has its own RESS.HQR
    // palette, so a colour index from one means nothing in the other. Defaults are LBA2's own
    // proven values (unchanged from before this became a parameter).
    public static Body Build(Body donor, int game = 2, int pink = 70, int skin = 35, int gold = 102, int blonde = 38)
    {
        const float H = 1230; // slightly shorter than Mario's H=1240 -- a normal in-game NPC scale, per the brief
        var vertices = Enumerable.Range(0, 19).Select(_ => new List<Vector3>()).ToArray();
        var faces = new List<(int Bone, int[] Points, int Colour)>();

        // Real, already-correctly-lit ramp-START colours actually used by real faces across LBA2's own BODY.HQR,
        // reused directly for the same reason MarioCustom.cs/LuigiCustom.cs reuse theirs: naive nearest-RGB
        // matching lands on the wrong position within a ramp and renders wrong once the engine's own "add a
        // light step" arithmetic runs (see reference-body-lighting-and-flat-sheets memory). That memory's own
        // warning turned out to be worth re-learning the hard way here: this file's FIRST attempt picked the
        // brightest, already-pale END of a ramp for "pink" (thinking a lighter raw RGB would look more pink),
        // rendered it with Renderer.Render (LightModel.Max(2)=9, i.e. up to +9 is added to a face's stored index
        // at render time), and got a dark, wrong-hued patch -- because +9 from a colour already at its own ramp's
        // last slot overflows into the START of the NEXT, visually unrelated ramp. Every colour below is instead
        // chosen so index+9 still lands inside the SAME ramp block:
        //
        // Pink=70: rgb=(139,7,7) raw, ramps up to rgb=(255,179,175) (a real pale pink, still used by 384 real
        // faces at that exact end index) at maximum light -- this palette has no dedicated magenta/hot-pink ramp
        // at all (confirmed by dumping all 256 entries), so this red ramp's own light end is the closest real,
        // already-used pink available. `findcolour red` alone (dominant-red, no constraint on blue) mostly turns
        // up browns/bricks instead, since it doesn't require blue to track with red the way a pink/magenta hue
        // does -- tools/BodyPipeline/Program.cs's FindColour gained a "pink" channel case for this (r notably
        // above g, b also above g, r>=b) alongside this file.
        // Gold=102: rgb=(131,103,55) raw, ramps up to rgb=(247,231,191) pale gold-cream at max light -- the
        // single most-used real "gold-adjacent" colour in the whole archive (1467 faces), found via a
        // green-adjacent scan (r and g both high and close together, b low -- the actual "gold" hue).
        // Blonde=38: rgb=(195,147,115) raw (already a warm, believable dark-blonde tone even unlit), ramps up to
        // rgb=(255,251,207) pale cream-blonde at max light -- within the same long, smooth 16-47 skin/tan ramp
        // Skin=35 (below) sits in, just further along it; used sparingly as a hair accent behind the crown.
        // Skin=35: the same warm tan MarioCustom.cs/LuigiCustom.cs both already use -- a proven-safe reuse
        // (35+9=44 also safely inside that same 16-47 ramp).
        int Pink = pink, Skin = skin, Gold = gold, Blonde = blonde;

        // A ring of `segments` points around a vertical axis at (cx,cy,cz), independent ellipse radii in X/Z.
        List<Vector3> Ring(int bone, float cx, float cy, float cz, float rx, float rz, int segments)
        {
            var ring = new List<Vector3>();
            for (var j = 0; j < segments; j++)
            {
                var a = j * 2 * MathF.PI / segments;
                ring.Add(new Vector3(cx + rx * MathF.Cos(a), cy, cz + rz * MathF.Sin(a)));
            }
            vertices[bone].AddRange(ring);
            return ring;
        }

        // Stitches consecutive same-size rings (quads) and caps the very first/last ring with a triangle fan --
        // exactly Loft's own topology (Humanoid.cs), just driven by explicit rings instead of a photo-derived span.
        void Chain(int bone, List<List<Vector3>> rings, int colour, bool capStart, bool capEnd)
        {
            // See MarioCustom.cs's own comment on this exact check -- catches the "rings weren't
            // actually trailing" bug at build time instead of a silent bad render.
            var begin = vertices[bone].Count - rings.Sum(r => r.Count);
            if (begin < 0 || vertices[bone][begin] != rings[0][0])
                throw new InvalidOperationException($"Chain(bone {bone}): rings aren't the trailing span of vertices[{bone}] -- build them immediately before this call, not earlier.");
            var offsets = new int[rings.Count]; var pos = begin;
            for (var i = 0; i < rings.Count; i++) { offsets[i] = pos; pos += rings[i].Count; }
            for (var r = 0; r < rings.Count - 1; r++)
            {
                var n = rings[r].Count;
                for (var j = 0; j < n; j++)
                    faces.Add((bone, [offsets[r] + j, offsets[r] + (j + 1) % n, offsets[r + 1] + (j + 1) % n, offsets[r + 1] + j], colour));
            }
            if (capStart) { var n = rings[0].Count; for (var j = 1; j < n - 2; j += 2) faces.Add((bone, [offsets[0], offsets[0] + j + 2, offsets[0] + j + 1, offsets[0] + j], colour)); }
            if (capEnd) { var n = rings[^1].Count; var b = offsets[^1]; for (var j = 1; j < n - 2; j += 2) faces.Add((bone, [b, b + j, b + j + 1, b + j + 2], colour)); }
        }

        List<Vector3> R(int bone, float cx, float cy, float cz, float rx, float rz, int segments = 8) => Ring(bone, cx, cy, cz, rx, rz, segments);

        // --- Legs: thigh(8/11) -> shin(9/12) -> foot(10/13) -- thin and Skin-coloured (bare legs under the dress
        // hem, only visible below it); pink flats at the feet echo the dress colour instead of a boot colour ---
        void Leg(int thigh, int shin, int foot, int side)
        {
            float x = side * 70;
            var hip = R(thigh, x, H * .455f, 0, 48, 44, 8);
            var knee = R(thigh, x, H * .29f, 0, 40, 38, 8);
            Chain(thigh, [hip, knee], Skin, true, false);
            var shinTop = R(shin, x, H * .29f, 0, 40, 38, 8);
            var ankle = R(shin, x, H * .095f, 0, 34, 32, 8);
            Chain(shin, [shinTop, ankle], Skin, false, false);
            var footTop = R(foot, x, H * .095f, 0, 36, 34, 8);
            var toe = R(foot, x, H * .01f, H * .10f, 40, 38, 8);
            var sole = R(foot, x, H * .0f, H * .06f, 32, 34, 8);
            Chain(foot, [footTop, toe, sole], Pink, false, true);
        }
        Leg(8, 9, 10, 1); Leg(11, 12, 13, -1);

        // --- Hip / lower torso (2): the dress skirt. Bottom ring (hem) sits at upper-thigh height, well below hip
        // height, rather than stopping at the hip the way Mario's/Luigi's overalls waist band does -- see this
        // file's own top comment for why it doesn't go any lower than that. Flares slightly wider at the hem than
        // at the hip (a gentle bell shape) and tapers gradually up to the chest band, rather than Mario's/Luigi's
        // sharper waist-to-chest step, per the brief's own "gently tapering" guidance. ---
        {
            var hem = R(2, 0, H * .33f, 0, 175, 130, 10);
            var hip = R(2, 0, H * .455f, 0, 165, 125, 10);
            var waist = R(2, 0, H * .53f, 0, 130, 95, 10);
            var chestband = R(2, 0, H * .58f, 0, 120, 90, 10);
            Chain(2, [hem, hip, waist, chestband], Pink, false, false);
        }
        // --- Upper torso / shoulders (3): the bodice, same pink as the skirt (one continuous dress), narrower
        // shoulders than Mario's/Luigi's, with a short gold trim collar just below the shoulder line. Each ring
        // is created immediately before the Chain call that consumes it (rather than creating every ring for
        // this bone up front, then making several Chain calls against them): Chain's own `begin` maths assumes
        // the rings passed to any one call are the CURRENT trailing span of vertices[bone] at that moment, and a
        // later call whose rings are a strict prefix of an already-fully-built list breaks that assumption --
        // found by actually rendering this bone with all 5 rings pre-built and 3 chain calls after (produced a
        // real hole plus a mislabelled colour region), and confirmed to be a real, pre-existing pitfall of this
        // same pattern by rendering MarioCustom.Build directly: its own bone 3 (4 rings built up front, THEN
        // `Chain(3,[bib,chest],...)` followed by `Chain(3,[chest,shoulders,neck],...)`) shows the identical kind
        // of hole/mislabelled-colour band between its bib and chest rings. Left alone here (MarioCustom.cs is an
        // existing file this task doesn't touch), but avoided in this file by only ever calling Chain with the
        // rings most recently added for that bone. ---
        {
            var bib = R(3, 0, H * .58f, 0, 120, 90, 10);
            var underbust = R(3, 0, H * .66f, 0, 124, 92, 10);
            var shoulders = R(3, 0, H * .775f, 0, 92, 80, 10);
            Chain(3, [bib, underbust, shoulders], Pink, false, false);
            var collar = R(3, 0, H * .80f, 0, 58, 52, 10);
            Chain(3, [shoulders, collar], Gold, false, false);
            var neck = R(3, 0, H * .815f, 0, 42, 38, 10);
            Chain(3, [collar, neck], Pink, false, false);
        }

        // --- Arms: upper(4/6) -> forearm+hand(5/7) -- slender, narrower than Mario's/Luigi's; short pink puff
        // sleeve at the shoulder tapering to a bare Skin-coloured forearm and hand (no glove) ---
        void Arm(int upper, int fore, int side)
        {
            float x = side * 145;
            var shoulder = R(upper, x, H * .775f, 0, 42, 40, 8);
            var elbow = R(upper, x, H * .62f, 0, 36, 34, 8);
            Chain(upper, [shoulder, elbow], Pink, true, false);
            var foreTop = R(fore, x, H * .62f, 0, 36, 34, 8);
            var wrist = R(fore, x, H * .48f, 0, 30, 28, 8);
            Chain(fore, [foreTop, wrist], Skin, false, false);
            var palm = R(fore, x, H * .40f, 0, 34, 32, 8);
            var fist = R(fore, x, H * .345f, 0, 28, 26, 8);
            Chain(fore, [wrist, palm, fist], Skin, false, true);
        }
        Arm(4, 5, 1); Arm(6, 7, -1);

        // --- Head (14): tan skin, slender/oval, with a pale blonde hair band (poofing out past the brow, then
        // narrowing back in before the crown sits on top) instead of continuing Skin all the way up like Mario's/
        // Luigi's bare head does ---
        {
            var neck = R(14, 0, H * .815f, 0, 42, 38, 10);
            var jaw = R(14, 0, H * .85f, 0, 58, 52, 10);
            var cheeks = R(14, 0, H * .885f, 0, 64, 58, 10);
            var brow = R(14, 0, H * .925f, 0, 58, 52, 10);
            Chain(14, [neck, jaw, cheeks, brow], Skin, false, false);
            var hairPoof = R(14, 0, H * .955f, 0, 72, 64, 10);
            var headTop = R(14, 0, H * .975f, 0, 42, 42, 10);
            Chain(14, [brow, hairPoof, headTop], Blonde, false, true);

            // A small nose bump, same technique/winding as MarioCustom.cs's own (a standalone 4-point base plus a
            // tip poking further forward, +Z -- not routed through Chain, wound by hand to match Chain's own ring
            // handedness), just daintier and scaled to this narrower head.
            var noseBase = vertices[14].Count;
            vertices[14].Add(new Vector3(-16, H * .895f, 64)); vertices[14].Add(new Vector3(16, H * .895f, 64));
            vertices[14].Add(new Vector3(14, H * .875f, 60)); vertices[14].Add(new Vector3(-14, H * .875f, 60));
            var noseTip = vertices[14].Count;
            vertices[14].Add(new Vector3(0, H * .884f, 84));
            faces.Add((14, [noseBase + 1, noseBase, noseTip], Skin));
            faces.Add((14, [noseBase + 2, noseBase + 1, noseTip], Skin));
            faces.Add((14, [noseBase + 3, noseBase + 2, noseTip], Skin));
            faces.Add((14, [noseBase, noseBase + 3, noseTip], Skin));
            // A small pink lips patch, same flat-quad technique as MarioCustom.cs's own moustache patch, just
            // smaller/lower and pink instead of dark.
            var lipsBase = vertices[14].Count;
            vertices[14].Add(new Vector3(-16, H * .868f, 54)); vertices[14].Add(new Vector3(16, H * .868f, 54));
            vertices[14].Add(new Vector3(18, H * .852f, 50)); vertices[14].Add(new Vector3(-18, H * .852f, 50));
            faces.Add((14, [lipsBase + 3, lipsBase + 2, lipsBase + 1, lipsBase], Pink));
        }

        // --- Crown (15, a child of the head bone -- the same empty accessory slot Mario/Luigi use for a cap):
        // a short gold band narrowing to a small flat-ish top, no brim (crowns don't have one, unlike a cap) --
        // plus a single small pink jewel on the front, same nose-bump pyramid technique as above, tying the
        // crown back to the dress colour instead of a front badge like Mario's/Luigi's own cap emblem ---
        {
            var band = R(15, 0, H * .975f, 0, 56, 50, 10);
            var mid = R(15, 0, H * 1.015f, 0, 46, 40, 10);
            var top = R(15, 0, H * 1.04f, 0, 20, 20, 10);
            Chain(15, [band, mid, top], Gold, false, true);

            var jewelBase = vertices[15].Count;
            vertices[15].Add(new Vector3(-12, H * .985f, 46)); vertices[15].Add(new Vector3(12, H * .985f, 46));
            vertices[15].Add(new Vector3(10, H * .965f, 42)); vertices[15].Add(new Vector3(-10, H * .965f, 42));
            var jewelTip = vertices[15].Count;
            vertices[15].Add(new Vector3(0, H * .978f, 62));
            faces.Add((15, [jewelBase + 1, jewelBase, jewelTip], Pink));
            faces.Add((15, [jewelBase + 2, jewelBase + 1, jewelTip], Pink));
            faces.Add((15, [jewelBase + 3, jewelBase + 2, jewelTip], Pink));
            faces.Add((15, [jewelBase, jewelBase + 3, jewelTip], Pink));
        }

        // Empty accessory bones (1, 16, 17, 18) still need one point each so every engine animation group is valid
        // -- same requirement Humanoid.Build/MarioCustom.cs/LuigiCustom.cs all document for their own unused bones.
        // Bone 16 (a child of the crown bone) is left empty here, same as Mario's/Luigi's own unused bone 16.
        var origins = new Vector3[19];
        origins[0] = Vector3.Zero; origins[1] = new Vector3(0, H * .455f, 0); origins[2] = new Vector3(0, H * .455f, 0);
        origins[3] = new Vector3(0, H * .58f, 0); origins[4] = new Vector3(145, H * .775f, 0); origins[5] = new Vector3(145, H * .62f, 0);
        origins[6] = new Vector3(-145, H * .775f, 0); origins[7] = new Vector3(-145, H * .62f, 0);
        origins[8] = new Vector3(70, H * .455f, 0); origins[9] = new Vector3(70, H * .29f, 0); origins[10] = new Vector3(70, H * .095f, 0);
        origins[11] = new Vector3(-70, H * .455f, 0); origins[12] = new Vector3(-70, H * .29f, 0); origins[13] = new Vector3(-70, H * .095f, 0);
        origins[14] = new Vector3(0, H * .815f, 0); origins[15] = new Vector3(0, H * .975f, 0);
        origins[16] = new Vector3(0, H * 1.04f, 0); origins[17] = new Vector3(0, H * .58f, 0); origins[18] = new Vector3(0, H * .58f, 0);
        for (var i = 0; i < 19; i++) if (vertices[i].Count == 0) vertices[i].Add(origins[i]);

        // Each child bone needs one pivot vertex in its PARENT's own range, at that child's own world attachment
        // point (origins[child]) -- exactly Humanoid.Build's own pattern (Humanoid.cs), just with hand-placed
        // origins instead of ones read from a donor body's own rest pose.
        var pivots = new int[19];
        for (var i = 1; i < 19; i++) { pivots[i] = vertices[Parents[i]].Count; vertices[Parents[i]].Add(origins[i]); }

        var body = new Body { Game = game, Header = (byte[])donor.Header.Clone(), Lit = true };
        var starts = new int[19];
        for (var i = 0; i < 19; i++)
        {
            starts[i] = body.Vertices.Count;
            body.Vertices.AddRange(vertices[i]);
            var pivot = i == 0 ? 0 : starts[Parents[i]] + pivots[i];
            // 38 bytes: an LBA1 bone record's real width -- see MarioCustom.cs's own comment on this
            // (required for LBA1 output; harmless filler for LBA2).
            body.Bones.Add(new(starts[i], vertices[i].Count, pivot, Parents[i], new byte[38]));
        }
        foreach (var f in faces) body.Faces.Add(new(f.Points.Select(p => p + starts[f.Bone]).ToArray(), f.Colour));
        body.SetWorld(body.Vertices.ToArray());
        body.Validate();
        return body;
    }
}
