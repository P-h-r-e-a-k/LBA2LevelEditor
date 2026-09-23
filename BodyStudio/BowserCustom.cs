using System.IO;
using System.Linq;
using System.Numerics;

namespace LbaBodyStudio;

// Bowser, hand-authored on the same 19-bone rig as MarioCustom.cs/LuigiCustom.cs (same Ring/Chain
// pattern, deliberately not shared code -- see MarioCustom.cs's own comment on why). The biggest
// stretch of this rig so far: Bowser is bulky, scaly, and has a spiked shell and horns that don't
// map onto "torso + cap" the way a shirt-and-baseball-cap character does. Approach: a much wider,
// squatter build throughout (big torso, thick limbs, short wide legs) with green "scaly" skin
// covering everything, a cream belly patch as its own coloured band on the torso, horns as
// standalone hand-wound quads on the head (same technique as Mario's nose/moustache), and the
// existing cap-bone slot (15, a child of the head) repurposed as a spiked shell -- positioned
// behind the head/upper back (negative Z, this rig's own "front" is +Z per MarioCustom.cs's own
// nose-placement comment) rather than on top, with a few pyramid spikes as more standalone quads.
public static class BowserCustom
{
    static readonly int[] Parents = [-1, 0, 1, 2, 3, 4, 3, 6, 2, 8, 9, 2, 11, 12, 3, 14, 15, 3, 17];

    public static Body Build(Body donor, int game = 2, int green = 133, int cream = 35, int shellColour = 80, int dark = 97)
    {
        const float H = 1520; // notably taller AND much wider than Mario/Luigi -- a big, bulky build
        var vertices = Enumerable.Range(0, 19).Select(_ => new List<Vector3>()).ToArray();
        var faces = new List<(int Bone, int[] Points, int Colour)>();

        // Colours reused from real, already-lit donor faces the same way every other character in
        // this project picks them (see MarioCustom.cs's own comment) -- green/skin/dark are the same
        // proven LBA2 indices Mario/Luigi already use; shellColour defaults to Mario's own Red=80 for
        // a classic orange-red shell.
        int Green = green, Cream = cream, Shell = shellColour, Dark = dark;

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

        // --- Legs: short, thick, squat -- Bowser's legs read as stubby relative to his huge torso ---
        void Leg(int thigh, int shin, int foot, int side)
        {
            float x = side * 130;
            var hip = R(thigh, x, H * .30f, 0, 105, 95, 8);
            var knee = R(thigh, x, H * .16f, 0, 85, 78, 8);
            Chain(thigh, [hip, knee], Green, true, false);
            var shinTop = R(shin, x, H * .16f, 0, 85, 78, 8);
            var ankle = R(shin, x, H * .05f, 0, 72, 68, 8);
            Chain(shin, [shinTop, ankle], Green, false, false);
            var footTop = R(foot, x, H * .05f, 0, 78, 72, 8);
            var toe = R(foot, x, H * .005f, H * .13f, 90, 82, 8);
            var sole = R(foot, x, H * .0f, H * .08f, 65, 68, 8);
            Chain(foot, [footTop, toe, sole], Dark, false, true);
        }
        Leg(8, 9, 10, 1); Leg(11, 12, 13, -1);

        // --- Hip / lower torso (2): green scaly skin, waist up to the chest band ---
        {
            var waist = R(2, 0, H * .30f, 0, 210, 160, 10);
            var chest = R(2, 0, H * .44f, 0, 250, 185, 10);
            Chain(2, [waist, chest], Green, false, false);
        }
        // --- Upper torso / shoulders (3): a broad, barrel chest with a cream belly band, tapering to the neck ---
        {
            // Chain needs its own rings to still be vertices[3]'s trailing span at call time -- each
            // pair here is built and chained immediately, before the next pair exists (see
            // MarioCustom.cs's own comment on this exact bug: building all rings up front, then
            // chaining non-trailing subsets, silently points a call's offsets at the WRONG rings).
            var bib = R(3, 0, H * .44f, 0, 250, 185, 10);
            var belly = R(3, 0, H * .52f, 40, 245, 175, 10); // belly patch bulges slightly forward (+Z)
            Chain(3, [bib, belly], Cream, false, false);     // cream belly band
            var chest = R(3, 0, H * .62f, 0, 235, 175, 10);
            Chain(3, [belly, chest], Green, false, false);   // back to scaly green above the belly
            var shoulders = R(3, 0, H * .70f, 0, 225, 165, 10);
            var neck = R(3, 0, H * .745f, 0, 95, 85, 10);
            Chain(3, [chest, shoulders, neck], Green, false, false);
        }

        // --- Arms: big, thick, clawed hands -- Bowser's arms are heavy and muscular ---
        void Arm(int upper, int fore, int side)
        {
            float x = side * 260;
            var shoulder = R(upper, x, H * .70f, 0, 88, 82, 8);
            var elbow = R(upper, x, H * .56f, 0, 72, 68, 8);
            Chain(upper, [shoulder, elbow], Green, true, false);
            var foreTop = R(fore, x, H * .56f, 0, 72, 68, 8);
            var wrist = R(fore, x, H * .44f, 0, 62, 58, 8);
            Chain(fore, [foreTop, wrist], Green, false, false);
            var hand = R(fore, x, H * .38f, 0, 78, 72, 8);
            var claws = R(fore, x, H * .32f, 0, 55, 52, 8);
            Chain(fore, [wrist, hand, claws], Dark, false, true);
        }
        Arm(4, 5, 1); Arm(6, 7, -1);

        // --- Head (14): large, wide, with a pronounced snout and jaw ---
        {
            var neck = R(14, 0, H * .745f, 0, 88, 78, 10);
            var jaw = R(14, 0, H * .78f, 20, 125, 115, 10);
            var cheeks = R(14, 0, H * .825f, 30, 145, 130, 10);
            var brow = R(14, 0, H * .875f, 10, 130, 118, 10);
            var crown = R(14, 0, H * .91f, -5, 75, 72, 10);
            Chain(14, [neck, jaw, cheeks, brow, crown], Green, false, true);
            // Snout: a wide, blunt wedge extending forward from the jaw/cheek line (bigger and blunter
            // than Mario's own pointed nose bump, same hand-wound-quad technique -- see MarioCustom.cs's
            // own comment on why these need reversed winding: not routed through Chain).
            var snoutBase = vertices[14].Count;
            vertices[14].Add(new Vector3(-55, H * .845f, 145)); vertices[14].Add(new Vector3(55, H * .845f, 145));
            vertices[14].Add(new Vector3(48, H * .79f, 138)); vertices[14].Add(new Vector3(-48, H * .79f, 138));
            var snoutTip = vertices[14].Count;
            vertices[14].Add(new Vector3(0, H * .818f, 195));
            faces.Add((14, [snoutBase + 1, snoutBase, snoutTip], Green));
            faces.Add((14, [snoutBase + 2, snoutBase + 1, snoutTip], Green));
            faces.Add((14, [snoutBase + 3, snoutBase + 2, snoutTip], Green));
            faces.Add((14, [snoutBase, snoutBase + 3, snoutTip], Green));
            // Two horns, small cones jutting up/back from the brow.
            void Horn(float x)
            {
                var b = vertices[14].Count;
                vertices[14].Add(new Vector3(x - 14, H * .895f, 30)); vertices[14].Add(new Vector3(x + 14, H * .895f, 30));
                vertices[14].Add(new Vector3(x + 10, H * .895f, 5)); vertices[14].Add(new Vector3(x - 10, H * .895f, 5));
                var tip = vertices[14].Count;
                vertices[14].Add(new Vector3(x, H * .97f, 15));
                faces.Add((14, [b + 1, b, tip], Dark));
                faces.Add((14, [b + 2, b + 1, tip], Dark));
                faces.Add((14, [b + 3, b + 2, tip], Dark));
                faces.Add((14, [b, b + 3, tip], Dark));
            }
            Horn(70); Horn(-70);
        }

        // --- Shell (15, the rig's own cap slot -- repurposed): a big spiked dome on the upper back ---
        {
            // Positioned behind the shoulders (negative Z is "back" -- see this file's own top
            // comment), lower/smaller than the first pass: full-head-height and 220-wide made it
            // visually swallow the whole head from a straight front view (confirmed live -- the
            // profile/three-quarter renders looked right, but front-on showed solid shell where the
            // green head should be) -- centred at shoulder height instead of head height, and
            // narrower than the head's own widest point (145) so it reads as looming behind the
            // shoulders rather than replacing the head silhouette.
            var rim = R(15, 0, H * .52f, -140, 150, 75, 10);
            var lower = R(15, 0, H * .60f, -175, 165, 92, 10);
            var mid = R(15, 0, H * .70f, -182, 150, 85, 10);
            var top = R(15, 0, H * .77f, -155, 50, 45, 10);
            Chain(15, [rim, lower, mid, top], Shell, true, true);
            // Four pyramid spikes along the shell's own ridge line.
            void Spike(float y, float z, float size)
            {
                var b = vertices[15].Count;
                vertices[15].Add(new Vector3(-size, y, z - size)); vertices[15].Add(new Vector3(size, y, z - size));
                vertices[15].Add(new Vector3(size, y, z + size)); vertices[15].Add(new Vector3(-size, y, z + size));
                var tip = vertices[15].Count;
                vertices[15].Add(new Vector3(0, y + size * 2.2f, z));
                faces.Add((15, [b + 1, b, tip], Dark));
                faces.Add((15, [b + 2, b + 1, tip], Dark));
                faces.Add((15, [b + 3, b + 2, tip], Dark));
                faces.Add((15, [b, b + 3, tip], Dark));
            }
            Spike(H * .70f, -215, 32); Spike(H * .78f, -222, 36); Spike(H * .86f, -205, 30); Spike(H * .62f, -195, 26);
        }

        // Empty accessory bones (1, 16, 17, 18) still need one point each -- same requirement every
        // character in this project documents for its own unused bones.
        var origins = new Vector3[19];
        origins[0] = Vector3.Zero; origins[1] = new Vector3(0, H * .30f, 0); origins[2] = new Vector3(0, H * .30f, 0);
        origins[3] = new Vector3(0, H * .44f, 0); origins[4] = new Vector3(260, H * .70f, 0); origins[5] = new Vector3(260, H * .56f, 0);
        origins[6] = new Vector3(-260, H * .70f, 0); origins[7] = new Vector3(-260, H * .56f, 0);
        origins[8] = new Vector3(130, H * .30f, 0); origins[9] = new Vector3(130, H * .16f, 0); origins[10] = new Vector3(130, H * .05f, 0);
        origins[11] = new Vector3(-130, H * .30f, 0); origins[12] = new Vector3(-130, H * .16f, 0); origins[13] = new Vector3(-130, H * .05f, 0);
        origins[14] = new Vector3(0, H * .745f, 0); origins[15] = new Vector3(0, H * .60f, -170);
        origins[16] = new Vector3(0, H * .88f, -190); origins[17] = new Vector3(0, H * .44f, 0); origins[18] = new Vector3(0, H * .44f, 0);
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
            body.Bones.Add(new(starts[i], vertices[i].Count, pivot, Parents[i], new byte[38]));
        }
        foreach (var f in faces) body.Faces.Add(new(f.Points.Select(p => p + starts[f.Bone]).ToArray(), f.Colour));
        body.SetWorld(body.Vertices.ToArray());
        body.Validate();
        return body;
    }
}
