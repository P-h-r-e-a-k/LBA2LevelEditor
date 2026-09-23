using System.IO;
using System.Linq;
using System.Numerics;

namespace LbaBodyStudio;

// Luigi, hand-authored the same way MarioCustom.cs is (see its own comment for why: placing rings directly rather
// than fitting a photo silhouette) -- same 19-bone Twinsen-topology rig, same ring/Chain/pivot construction pattern,
// deliberately NOT sharing code with MarioCustom.cs (this codebase's own precedent: MarioCustom.cs itself doesn't
// share code with Humanoid.cs's Loft either -- a parallel implementation, not a forced abstraction, since each
// character's own proportions are genuinely different data, not a parameterization of "the same shape"). Differs
// from Mario in the two ways his own character design actually differs: taller and narrower (H is 6% taller, torso/
// limb radii ~10% narrower), and green instead of red for the cap/shirt.
public static class LuigiCustom
{
    static readonly int[] Parents = [-1, 0, 1, 2, 3, 4, 3, 6, 2, 8, 9, 2, 11, 12, 3, 14, 15, 3, 17];

    // game/colour overrides: see MarioCustom.Build's own comment -- each game has its own RESS.HQR
    // palette, so a colour index from one means nothing in the other. Defaults are LBA2's own
    // proven values (unchanged from before this became a parameter).
    public static Body Build(Body donor, int game = 2, int green = 133, int skin = 35, int dark = 97, int blue = 197)
    {
        const float H = 1310; // ~6% taller than Mario's H=1240 (MarioCustom.cs) -- Luigi's own canonical height difference
        var vertices = Enumerable.Range(0, 19).Select(_ => new List<Vector3>()).ToArray();
        var faces = new List<(int Bone, int[] Points, int Colour)>();

        // Green=133 (LBA2 default): a real, already-correctly-lit ramp-start colour actually used by
        // 207 faces across LBA2's own BODY.HQR (found via BodyPipeline's `findcolour green 2`) -- the
        // same "reuse a real donor colour" approach MarioCustom.cs uses for Red/Skin/Dark/Blue, for
        // the same reason (naive nearest-RGB picking lands on the wrong position within a ramp, see
        // reference-body-lighting-and-flat-sheets memory). Its own whole ramp was checked smooth
        // (BodyPipeline's `ramp` command) before use -- see reference-anim-hqr-format memory's own
        // "check the whole ramp" bug writeup for why that check matters, not just the candidate's own
        // isolated RGB.
        int Green = green, Skin = skin, Dark = dark, Blue = blue, White = Skin;

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

        // --- Legs: thigh(8/11) -> shin(9/12) -> foot(10/13) -- narrower than Mario's, matching Luigi's leaner build ---
        void Leg(int thigh, int shin, int foot, int side)
        {
            float x = side * 82;
            var hip = R(thigh, x, H * .46f, 0, 66, 60, 8);
            var knee = R(thigh, x, H * .295f, 0, 53, 50, 8);
            Chain(thigh, [hip, knee], Blue, true, false);
            var shinTop = R(shin, x, H * .295f, 0, 53, 50, 8);
            var ankle = R(shin, x, H * .095f, 0, 43, 41, 8);
            Chain(shin, [shinTop, ankle], Blue, false, false);
            var footTop = R(foot, x, H * .095f, 0, 47, 45, 8);
            var toe = R(foot, x, H * .01f, H * .095f, 52, 50, 8);
            var sole = R(foot, x, H * .0f, H * .058f, 42, 44, 8);
            Chain(foot, [footTop, toe, sole], Dark, false, true);
        }
        Leg(8, 9, 10, 1); Leg(11, 12, 13, -1);

        // --- Hip / lower torso (2): blue overalls, waist up to the chest band -- narrower than Mario's ---
        {
            var waist = R(2, 0, H * .46f, 0, 132, 95, 10);
            var chest = R(2, 0, H * .585f, 0, 148, 103, 10);
            Chain(2, [waist, chest], Blue, false, false);
        }
        // --- Upper torso / shoulders (3): green shirt above the overalls bib line, tapering into the neck ---
        {
            // See MarioCustom.cs's own comment on this exact call shape -- Chain needs its own rings
            // to still be vertices[bone]'s trailing span, so this call must happen before
            // shoulders/neck exist, not after all four rings are built.
            var bib = R(3, 0, H * .585f, 0, 148, 103, 10);
            var chest = R(3, 0, H * .705f, 0, 154, 108, 10);
            Chain(3, [bib, chest], Blue, false, false);
            var shoulders = R(3, 0, H * .78f, 0, 140, 100, 10);
            var neck = R(3, 0, H * .818f, 0, 66, 58, 10);
            Chain(3, [chest, shoulders, neck], Green, false, false);
        }

        // --- Arms: upper(4/6) -> forearm+hand(5/7) -- narrower than Mario's ---
        void Arm(int upper, int fore, int side)
        {
            float x = side * 168;
            var shoulder = R(upper, x, H * .78f, 0, 53, 49, 8);
            var elbow = R(upper, x, H * .618f, 0, 44, 42, 8);
            Chain(upper, [shoulder, elbow], Green, true, false);
            var foreTop = R(fore, x, H * .618f, 0, 44, 42, 8);
            var wrist = R(fore, x, H * .48f, 0, 37, 36, 8);
            Chain(fore, [foreTop, wrist], Skin, false, false);
            var glove = R(fore, x, H * .40f, 0, 53, 50, 8);
            var fist = R(fore, x, H * .345f, 0, 43, 41, 8);
            Chain(fore, [wrist, glove, fist], White, false, true);
        }
        Arm(4, 5, 1); Arm(6, 7, -1);

        // --- Head (14): tan skin, narrower and slightly longer than Mario's, with a bump for the nose ---
        {
            var neck = R(14, 0, H * .818f, 0, 58, 51, 10);
            var jaw = R(14, 0, H * .855f, 0, 78, 71, 10);
            var cheeks = R(14, 0, H * .898f, 0, 85, 78, 10);
            var brow = R(14, 0, H * .94f, 0, 75, 70, 10);
            var crown = R(14, 0, H * .968f, 0, 47, 47, 10);
            Chain(14, [neck, jaw, cheeks, brow, crown], Skin, false, true);
            // Nose bump, same wedge shape MarioCustom.cs uses (see its own comment on winding: not routed through
            // Chain, so wound by hand to match Chain's own ring handedness) -- Luigi's is a touch longer/pointier.
            var noseBase = vertices[14].Count;
            vertices[14].Add(new Vector3(-27, H * .915f, 92)); vertices[14].Add(new Vector3(27, H * .915f, 92));
            vertices[14].Add(new Vector3(24, H * .875f, 87)); vertices[14].Add(new Vector3(-24, H * .875f, 87));
            var noseTip = vertices[14].Count;
            vertices[14].Add(new Vector3(0, H * .89f, 138));
            faces.Add((14, [noseBase + 1, noseBase, noseTip], Skin));
            faces.Add((14, [noseBase + 2, noseBase + 1, noseTip], Skin));
            faces.Add((14, [noseBase + 3, noseBase + 2, noseTip], Skin));
            faces.Add((14, [noseBase, noseBase + 3, noseTip], Skin));
            // Moustache, same placement pattern as Mario's own.
            var mBase = vertices[14].Count;
            vertices[14].Add(new Vector3(-43, H * .880f, 84)); vertices[14].Add(new Vector3(43, H * .880f, 84));
            vertices[14].Add(new Vector3(47, H * .862f, 77)); vertices[14].Add(new Vector3(-47, H * .862f, 77));
            faces.Add((14, [mBase + 3, mBase + 2, mBase + 1, mBase], Dark));
        }

        // --- Cap (15, a child of the head bone): dome + brim, green with a white front badge ---
        {
            var brimBack = R(15, 0, H * .958f, -17, 92, 82, 10);
            var brimFront = R(15, 0, H * .958f, 128, 96, 26, 10);
            var band = R(15, 0, H * .967f, -8, 82, 74, 10);
            var dome = R(15, 0, H * 1.018f, -8, 67, 60, 10);
            var top = R(15, 0, H * 1.058f, -8, 17, 17, 10);
            Chain(15, [band, dome, top], Green, false, true);
            var bb = vertices[15].Count - brimBack.Count - brimFront.Count - band.Count - dome.Count - top.Count;
            var bf = bb + brimBack.Count;
            var n2 = brimBack.Count;
            for (var j = 0; j < n2; j++) faces.Add((15, [bb + j, bb + (j + 1) % n2, bf + (j + 1) % n2, bf + j], Green));
            for (var j = 1; j < n2 - 2; j += 2) faces.Add((15, [bb, bb + j + 2, bb + j + 1, bb + j], Green));
            // White circle + "L" badge on the front of the cap (same flat-quad emblem approach as Mario's own).
            var badge = vertices[15].Count;
            vertices[15].Add(new Vector3(-33, H * .988f, 118)); vertices[15].Add(new Vector3(33, H * .988f, 118));
            vertices[15].Add(new Vector3(33, H * .932f, 113)); vertices[15].Add(new Vector3(-33, H * .932f, 113));
            faces.Add((15, [badge + 3, badge + 2, badge + 1, badge], White));
        }

        // Empty accessory bones (1, 16, 17, 18) still need one point each -- same requirement Humanoid.Build/
        // MarioCustom.cs both document for their own unused bones.
        var origins = new Vector3[19];
        origins[0] = Vector3.Zero; origins[1] = new Vector3(0, H * .46f, 0); origins[2] = new Vector3(0, H * .46f, 0);
        origins[3] = new Vector3(0, H * .585f, 0); origins[4] = new Vector3(168, H * .78f, 0); origins[5] = new Vector3(168, H * .618f, 0);
        origins[6] = new Vector3(-168, H * .78f, 0); origins[7] = new Vector3(-168, H * .618f, 0);
        origins[8] = new Vector3(82, H * .46f, 0); origins[9] = new Vector3(82, H * .295f, 0); origins[10] = new Vector3(82, H * .095f, 0);
        origins[11] = new Vector3(-82, H * .46f, 0); origins[12] = new Vector3(-82, H * .295f, 0); origins[13] = new Vector3(-82, H * .095f, 0);
        origins[14] = new Vector3(0, H * .818f, 0); origins[15] = new Vector3(0, H * .967f, -8);
        origins[16] = new Vector3(0, H * 1.058f, -8); origins[17] = new Vector3(0, H * .585f, 0); origins[18] = new Vector3(0, H * .585f, 0);
        for (var i = 0; i < 19; i++) if (vertices[i].Count == 0) vertices[i].Add(origins[i]);

        var pivots = new int[19];
        for (var i = 1; i < 19; i++) { pivots[i] = vertices[Parents[i]].Count; vertices[Parents[i]].Add(origins[i]); }

        var body = new Body { Game = game, Header = (byte[])donor.Header.Clone(), Lit = true };
        var starts = new int[19];
        for (var i = 0; i < 19; i++)
        {
            starts[i] = body.Vertices.Count;
            body.Vertices.AddRange(vertices[i]);
            var pivot = i == 0 ? 0 : starts[Parents[i]] + pivots[i];
            // 38 bytes: an LBA1 bone record's real width -- see MarioCustom.cs's own comment on this.
            body.Bones.Add(new(starts[i], vertices[i].Count, pivot, Parents[i], new byte[38]));
        }
        foreach (var f in faces) body.Faces.Add(new(f.Points.Select(p => p + starts[f.Bone]).ToArray(), f.Colour));
        body.SetWorld(body.Vertices.ToArray());
        body.Validate();
        return body;
    }
}
