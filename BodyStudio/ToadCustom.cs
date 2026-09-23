using System.IO;
using System.Linq;
using System.Numerics;

namespace LbaBodyStudio;

// Toad, hand-authored the same way MarioCustom.cs/LuigiCustom.cs are (see MarioCustom.cs's own comment for why:
// placing rings directly rather than fitting a photo silhouette) -- same 19-bone Twinsen-topology rig, same
// ring/Chain/pivot construction pattern, deliberately NOT sharing code with either of them (this codebase's own
// precedent: a parallel implementation per character, not a forced abstraction, since each character's own
// proportions -- and, here, even which bones carry the "big" geometry -- are genuinely different data).
//
// The rig's own bone roles (see MarioCustom.cs) assume a normal head: bone 14 is a fairly ordinary head and bone 15,
// a child of it, is a small head-attached accessory (Mario/Luigi's own baseball cap). Toad inverts that entirely --
// his mushroom cap basically IS his head, and the "head" underneath it is a small, mostly-hidden detail. So bone 15
// here is NOT a small accessory: it is built far larger than bone 14 (a wide, domed mushroom cap that dwarfs the
// small round head it sits on), which is the only way to get Toad's actual silhouette out of a rig whose bone roles
// were fixed around Twinsen's own proportions. Short H (980-1050 range, well under Mario's 1240) plus stubby limb/
// torso radii cover the rest of "short and stocky."
public static class ToadCustom
{
    static readonly int[] Parents = [-1, 0, 1, 2, 3, 4, 3, 6, 2, 8, 9, 2, 11, 12, 3, 14, 15, 3, 17];

    // game/colour overrides: see MarioCustom.Build's own comment -- each game has its own RESS.HQR
    // palette, so a colour index from one means nothing in the other. Defaults are LBA2's own
    // proven values (unchanged from before this became a parameter).
    public static Body Build(Body donor, int game = 2, int red = 80, int blue = 197, int dark = 97, int white = 54, int cream = 38)
    {
        const float H = 1000; // ~19% shorter than Mario's H=1240 (MarioCustom.cs) -- Toad is famously short and stocky
        var vertices = Enumerable.Range(0, 19).Select(_ => new List<Vector3>()).ToArray();
        var faces = new List<(int Bone, int[] Points, int Colour)>();

        // Same "reuse a real, already-correctly-lit donor colour" approach MarioCustom.cs/LuigiCustom.cs use for
        // their own colours (see MarioCustom.cs's own comment: naive nearest-RGB picking lands on the wrong position
        // within a ramp and renders wrong once the engine's own light-step arithmetic walks forward from it).
        // Red/Blue/Dark are reused straight from Mario/Luigi (already proven). Cream and White are new: neither of
        // BodyPipeline's `findcolour`'s original red/green/blue filters can surface a pale, near-neutral colour (a
        // dominant-channel test never matches white/cream, since by definition no channel dominates), so this file
        // adds one more filter case to FindColour (tools/BodyPipeline/Program.cs) -- "white" -- that instead looks
        // for a real face colour whose three channels are all bright AND close together.
        //
        // That filter's own TOP hits (colour 63 = 255,255,255 and colour 239 = 235,223,215) turned out to be a real,
        // separate bug, not just a cosmetic one: Renderer.cs's own Lit path does `palette[f.Colour + shade]` (shade
        // 0..LightModel.Max(2)=9), walking FORWARD from the stored index -- so it needs base+9 to still land inside
        // the SAME ramp. Dumping the palette around both (BodyPipeline `dumpheader`-style, done by hand here) showed
        // colour 63 IS the very last entry of its own 16-long neutral ramp (48..63, dark gray up to white) and 239
        // is one step from the end of ITS OWN warm ramp (indices 240+ are a completely unrelated orange/black ramp)
        // -- so +shade overflowed into another hue entirely (this is exactly what a first render surfaced: the
        // "white" glove/spots came out dark red, and the "cream" legs banded yellow, both from wrapping into the
        // neighbouring ramp). The fix is a base with real headroom: White=54 (87,87,79, used by 338 real faces,
        // +9 lands on 63/255,255,255 -- still inside the same neutral ramp) and Cream=38 (195,147,115, used by 702
        // real faces, three steps paler than Mario's own Skin=35 in this SAME warm ramp, +9 lands on 47/255,251,207,
        // a pale warm off-white -- still inside that ramp too). Both re-verified by re-dumping every index in each
        // ramp (48-63 and 30-47) with real per-face usage counts before picking these two.
        int Red = red, Blue = blue, Dark = dark, White = white, Cream = cream;

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

        // A small flat quad, NOT routed through Chain, for a decal-like feature (a cap spot, an eye) sitting proud
        // of a ring-built surface -- the same technique MarioCustom.cs/LuigiCustom.cs use for their nose/moustache/
        // badge, generalised to any angle around the vertical axis instead of only "the front" (+Z).
        //
        // MarioCustom.cs's own badge is a proven-correct front-facing (+Z) quad: 4 corners inserted in the order
        // (top-left, top-right, bottom-right, bottom-left) relative to its own local "right"/"up"/"outward" axes,
        // then drawn with the face's own point order REVERSED (badge+3, badge+2, badge+1, badge) -- see its comment
        // on Chain's own winding handedness needing to be matched by hand for anything not going through Chain.
        // Reconstructing that badge's own local axes from its real numbers: outward (+Z) and "right" (+X) satisfy
        // Ut(theta) = (sin theta, 0, -cos theta) at theta = pi/2 (Ring's own angle convention: angle 0 = +X, so the
        // badge's world-front +Z corresponds to Ring-angle pi/2). Rotating that whole proven local recipe about the
        // Y axis by an arbitrary angle keeps it correctly wound for ANY theta: cross products commute with proper
        // rotations (det = +1), so if the theta = pi/2 case is right-side-out, every other theta is too -- no
        // separate winding case needed per spot, unlike Chain which only ever needed the one (already-verified) case.
        void Spot(int bone, float theta, float y, float outward, float halfWidth, float halfHeight, int colour)
        {
            float uoX = MathF.Cos(theta), uoZ = MathF.Sin(theta);
            float utX = MathF.Sin(theta), utZ = -MathF.Cos(theta);
            float ox = outward * uoX, oz = outward * uoZ;
            var b = vertices[bone].Count;
            vertices[bone].Add(new Vector3(ox - halfWidth * utX, y + halfHeight, oz - halfWidth * utZ));
            vertices[bone].Add(new Vector3(ox + halfWidth * utX, y + halfHeight, oz + halfWidth * utZ));
            vertices[bone].Add(new Vector3(ox + halfWidth * utX, y - halfHeight, oz + halfWidth * utZ));
            vertices[bone].Add(new Vector3(ox - halfWidth * utX, y - halfHeight, oz - halfWidth * utZ));
            faces.Add((bone, [b + 3, b + 2, b + 1, b], colour));
        }

        // --- Legs: thigh(8/11) -> shin(9/12) -> foot(10/13) -- short and stubby, little taper (a pudgy build) ---
        void Leg(int thigh, int shin, int foot, int side)
        {
            float x = side * 75;
            var hip = R(thigh, x, H * .40f, 0, 70, 64, 8);
            var knee = R(thigh, x, H * .26f, 0, 58, 54, 8);
            Chain(thigh, [hip, knee], Cream, true, false);
            var shinTop = R(shin, x, H * .26f, 0, 58, 54, 8);
            var ankle = R(shin, x, H * .09f, 0, 48, 46, 8);
            Chain(shin, [shinTop, ankle], Cream, false, false);
            var footTop = R(foot, x, H * .09f, 0, 52, 50, 8);
            var toe = R(foot, x, H * .01f, H * .09f, 58, 56, 8);
            var sole = R(foot, x, H * .0f, H * .055f, 46, 48, 8);
            Chain(foot, [footTop, toe, sole], Dark, false, true);
        }
        Leg(8, 9, 10, 1); Leg(11, 12, 13, -1);

        // --- Hip / lower torso (2): blue vest, waist up to the chest band -- wide relative to H (stocky) ---
        {
            var waist = R(2, 0, H * .40f, 0, 175, 140, 10);
            var chest = R(2, 0, H * .50f, 0, 190, 150, 10);
            Chain(2, [waist, chest], Blue, false, false);
        }
        // --- Upper torso / shoulders (3): vest continues up the chest, then bare cream shirt at the (sleeveless
        // vest's) shoulders/neck ---
        {
            // See MarioCustom.cs's own comment on this exact call shape -- Chain needs its own rings
            // to still be vertices[bone]'s trailing span, so this call must happen before
            // shoulders/neck exist, not after all four rings are built (a real bug found 2026-09-23).
            var bib = R(3, 0, H * .50f, 0, 190, 150, 10);
            var chest = R(3, 0, H * .60f, 0, 200, 158, 10);
            Chain(3, [bib, chest], Blue, false, false);
            var shoulders = R(3, 0, H * .66f, 0, 175, 140, 10);
            var neck = R(3, 0, H * .70f, 0, 70, 62, 10);
            Chain(3, [chest, shoulders, neck], Cream, false, false);
        }

        // --- Arms: upper(4/6) -> forearm+hand(5/7), side=+1 right, -1 left -- cream sleeves (the vest is
        // sleeveless), white gloves as a Mario/Luigi-style accent at the hand ---
        void Arm(int upper, int fore, int side)
        {
            float x = side * 150;
            var shoulder = R(upper, x, H * .66f, 0, 54, 50, 8);
            var elbow = R(upper, x, H * .52f, 0, 46, 44, 8);
            Chain(upper, [shoulder, elbow], Cream, true, false);
            var foreTop = R(fore, x, H * .52f, 0, 46, 44, 8);
            var wrist = R(fore, x, H * .40f, 0, 40, 38, 8);
            Chain(fore, [foreTop, wrist], Cream, false, false);
            var glove = R(fore, x, H * .34f, 0, 54, 50, 8);
            var fist = R(fore, x, H * .30f, 0, 44, 42, 8);
            Chain(fore, [wrist, glove, fist], White, false, true);
        }
        Arm(4, 5, 1); Arm(6, 7, -1);

        // --- Head (14): small, round, short -- deliberately unassuming, since the cap (15) is doing the real work
        // of reading as "Toad" ---
        {
            var neck = R(14, 0, H * .70f, 0, 60, 54, 10);
            var jaw = R(14, 0, H * .735f, 0, 68, 62, 10);
            var cheeks = R(14, 0, H * .76f, 0, 72, 66, 10);
            var brow = R(14, 0, H * .79f, 0, 64, 60, 10);
            var crown = R(14, 0, H * .815f, 0, 42, 42, 10);
            Chain(14, [neck, jaw, cheeks, brow, crown], Cream, false, true);
            // A tiny button-nose bump: the same standalone 4-point-base-plus-tip pyramid MarioCustom.cs uses (see
            // its own comment on winding -- reversed by hand to match Chain's own handedness), just much smaller
            // and kept the same colour as the skin (a shape highlight, not a separate coloured feature, matching
            // Mario's own nose treatment).
            var noseBase = vertices[14].Count;
            vertices[14].Add(new Vector3(-16, H * .762f, 46)); vertices[14].Add(new Vector3(16, H * .762f, 46));
            vertices[14].Add(new Vector3(14, H * .745f, 42)); vertices[14].Add(new Vector3(-14, H * .745f, 42));
            var noseTip = vertices[14].Count;
            vertices[14].Add(new Vector3(0, H * .752f, 62));
            faces.Add((14, [noseBase + 1, noseBase, noseTip], Cream));
            faces.Add((14, [noseBase + 2, noseBase + 1, noseTip], Cream));
            faces.Add((14, [noseBase + 3, noseBase + 2, noseTip], Cream));
            faces.Add((14, [noseBase, noseBase + 3, noseTip], Cream));
            // Eyes: two small dark dots via Spot, mirrored left/right of front (theta = pi/2) by the same angle.
            Spot(14, MathF.PI / 2 - 0.42f, H * .775f, 64, 8, 9, Dark);
            Spot(14, MathF.PI / 2 + 0.42f, H * .775f, 64, 8, 9, Dark);
        }

        // --- Cap (15, a child of the head bone -- one of the rig's own empty accessory slots): a huge domed
        // mushroom cap, built at the SAME height as the head's own crown (matching MarioCustom.cs's own convention
        // of the cap's first ring sitting level with the head it attaches to) but several times wider, so it reads
        // as draping over/dwarfing the small head beneath it purely through width, not by being physically lower ---
        {
            var skirt = R(15, 0, H * .815f, 0, 120, 112, 10);
            var waist = R(15, 0, H * .90f, 0, 195, 185, 10);
            var equator = R(15, 0, H * 1.00f, 0, 235, 225, 10);
            var upper = R(15, 0, H * 1.12f, 0, 150, 145, 10);
            var top = R(15, 0, H * 1.20f, 0, 35, 35, 10);
            Chain(15, [skirt, waist, equator, upper, top], Red, true, true);
            // Three large white spots (the classic mushroom-cap accent): one front-and-high (near the upper band),
            // two flanking it lower down on the widest part of the dome (the equator ring) -- placed via Spot at
            // angles either side of "straight front" (theta = pi/2) rather than only ever at the front, which is
            // what actually needs Spot's own generalised rotation instead of MarioCustom.cs's fixed +Z badge.
            Spot(15, MathF.PI / 2, H * 1.10f, 172, 46, 46, White);
            Spot(15, MathF.PI / 2 - 1.05f, H * 1.00f, 240, 55, 55, White);
            Spot(15, MathF.PI / 2 + 1.05f, H * 1.00f, 240, 55, 55, White);
        }

        // Empty accessory bones (1, 16, 17, 18) still need one point each so every engine animation group is valid --
        // same requirement Humanoid.Build/MarioCustom.cs/LuigiCustom.cs all document for their own unused bones.
        var origins = new Vector3[19];
        origins[0] = Vector3.Zero; origins[1] = new Vector3(0, H * .40f, 0); origins[2] = new Vector3(0, H * .40f, 0);
        origins[3] = new Vector3(0, H * .50f, 0); origins[4] = new Vector3(150, H * .66f, 0); origins[5] = new Vector3(150, H * .52f, 0);
        origins[6] = new Vector3(-150, H * .66f, 0); origins[7] = new Vector3(-150, H * .52f, 0);
        origins[8] = new Vector3(75, H * .40f, 0); origins[9] = new Vector3(75, H * .26f, 0); origins[10] = new Vector3(75, H * .09f, 0);
        origins[11] = new Vector3(-75, H * .40f, 0); origins[12] = new Vector3(-75, H * .26f, 0); origins[13] = new Vector3(-75, H * .09f, 0);
        origins[14] = new Vector3(0, H * .70f, 0); origins[15] = new Vector3(0, H * .815f, 0);
        origins[16] = new Vector3(0, H * 1.20f, 0); origins[17] = new Vector3(0, H * .50f, 0); origins[18] = new Vector3(0, H * .50f, 0);
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
