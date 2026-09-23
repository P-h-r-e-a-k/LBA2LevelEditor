using System.IO;
using System.Linq;
using System.Numerics;

namespace LbaBodyStudio;

// Yoshi, hand-authored on the same 19-bone rig as MarioCustom.cs/LuigiCustom.cs/BowserCustom.cs
// (same Ring/Chain pattern, deliberately not shared code -- see MarioCustom.cs's own comment).
// Round, compact body, a long forward snout (much longer than Bowser's stubby one) with small
// back-of-head spikes standing in for his mane, big round shoes, and a small red saddle on his
// back (the shared cap-bone slot, same "repurpose bone 15 as a back accessory instead of a hat"
// technique BowserCustom.cs uses for its shell -- kept modest in size this time, having learned
// from Bowser's own first-pass shell dwarfing the head that a big bone-15 accessory positioned
// behind the shoulders needs to stay clearly smaller than the head or it visually swallows it
// from a straight front view). Every Chain() call here is interleaved with its own ring creation
// (build only the rings one call needs, call it, then build the next batch) -- see the Chain-
// ordering bug write-up in project-mario-roster/reference-anim-hqr-format memory for why that
// matters: building all of a bone's rings up front and calling Chain multiple times afterward
// silently points every call but the last at the wrong rings.
public static class YoshiCustom
{
    static readonly int[] Parents = [-1, 0, 1, 2, 3, 4, 3, 6, 2, 8, 9, 2, 11, 12, 3, 14, 15, 3, 17];

    public static Body Build(Body donor, int game = 2, int green = 133, int cream = 35, int saddle = 80, int white = 35)
    {
        const float H = 1150; // shorter and more compact than Mario -- Yoshi reads as squat and round
        var vertices = Enumerable.Range(0, 19).Select(_ => new List<Vector3>()).ToArray();
        var faces = new List<(int Bone, int[] Points, int Colour)>();

        // Colours reused from real, already-lit donor faces the same way every character in this
        // project picks them (see MarioCustom.cs's own comment). White defaults to Skin=35 (a warm
        // tan, not literally white -- this palette has no dedicated bright-white ramp any more than
        // it has a pink one, see PeachCustom.cs's own note on that); close enough for shoe/eye
        // highlights at this poly budget.
        int Green = green, Cream = cream, Saddle = saddle, White = white;

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
            // actually trailing" bug at build time instead of a silent bad render (found the hard
            // way in this file's own first-pass foot code, despite already knowing about the bug).
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

        // --- Legs: short, with big round shoes -- Yoshi's shoes are a defining silhouette feature ---
        void Leg(int thigh, int shin, int foot, int side)
        {
            float x = side * 90;
            var hip = R(thigh, x, H * .34f, 0, 72, 65, 8);
            var knee = R(thigh, x, H * .20f, 0, 58, 52, 8);
            Chain(thigh, [hip, knee], Green, true, false);
            var shinTop = R(shin, x, H * .20f, 0, 58, 52, 8);
            var ankle = R(shin, x, H * .09f, 0, 46, 42, 8);
            Chain(shin, [shinTop, ankle], Green, false, false);
            // Big round shoe -- wider than the shin, rounded toe, no separate sole taper (a single
            // bulbous ring shape reads more like Yoshi's own boots than a tapered human foot).
            var shoeTop = R(foot, x, H * .09f, 0, 62, 58, 8);
            var shoeBall = R(foot, x, H * .04f, H * .05f, 82, 76, 8);
            Chain(foot, [shoeTop, shoeBall], White, false, false);
            var toe = R(foot, x, H * .01f, H * .14f, 60, 66, 8);
            Chain(foot, [shoeBall, toe], White, false, true);
        }
        Leg(8, 9, 10, 1); Leg(11, 12, 13, -1);

        // --- Hip / lower torso (2): round green belly base ---
        {
            var waist = R(2, 0, H * .34f, 0, 130, 115, 10);
            var chest = R(2, 0, H * .46f, 10, 150, 130, 10);
            Chain(2, [waist, chest], Green, false, false);
        }
        // --- Upper torso / shoulders (3): rounds out into the chest/belly, cream underside, tapering to the neck ---
        {
            var bib = R(3, 0, H * .46f, 10, 150, 130, 10);
            var belly = R(3, 0, H * .56f, 20, 155, 135, 10);
            Chain(3, [bib, belly], Cream, false, false);
            var chest = R(3, 0, H * .66f, 0, 145, 125, 10);
            Chain(3, [belly, chest], Green, false, false);
            var shoulders = R(3, 0, H * .73f, 0, 120, 105, 10);
            var neck = R(3, 0, H * .765f, 0, 68, 60, 10);
            Chain(3, [chest, shoulders, neck], Green, false, false);
        }

        // --- Arms: small and stubby -- Yoshi's arms are a minor feature relative to his big head/body ---
        void Arm(int upper, int fore, int side)
        {
            float x = side * 150;
            var shoulder = R(upper, x, H * .73f, 0, 40, 38, 8);
            var elbow = R(upper, x, H * .62f, 0, 33, 31, 8);
            Chain(upper, [shoulder, elbow], Green, true, false);
            var foreTop = R(fore, x, H * .62f, 0, 33, 31, 8);
            var hand = R(fore, x, H * .53f, 0, 30, 28, 8);
            Chain(fore, [foreTop, hand], Green, false, true);
        }
        Arm(4, 5, 1); Arm(6, 7, -1);

        // --- Head (14): round, with a long forward snout and small back-of-head spikes ---
        {
            var neck = R(14, 0, H * .765f, 0, 70, 62, 10);
            var jaw = R(14, 0, H * .80f, 10, 92, 84, 10);
            var cheeks = R(14, 0, H * .845f, 15, 100, 92, 10);
            Chain(14, [neck, jaw, cheeks], Green, false, false);
            var brow = R(14, 0, H * .885f, 0, 88, 82, 10);
            var crown = R(14, 0, H * .915f, -10, 55, 55, 10);
            Chain(14, [cheeks, brow, crown], Green, false, true);
            // Long snout: a wide, elongated wedge extending well forward of the face (Yoshi's most
            // distinctive feature) -- same hand-wound-quad technique as Bowser's/Mario's own noses
            // (not routed through Chain, wound by hand to match Chain's own ring handedness).
            var snoutBase = vertices[14].Count;
            vertices[14].Add(new Vector3(-42, H * .85f, 95)); vertices[14].Add(new Vector3(42, H * .85f, 95));
            vertices[14].Add(new Vector3(36, H * .81f, 88)); vertices[14].Add(new Vector3(-36, H * .81f, 88));
            var snoutTip = vertices[14].Count;
            vertices[14].Add(new Vector3(0, H * .825f, 220));
            faces.Add((14, [snoutBase + 1, snoutBase, snoutTip], Green));
            faces.Add((14, [snoutBase + 2, snoutBase + 1, snoutTip], Green));
            faces.Add((14, [snoutBase + 3, snoutBase + 2, snoutTip], Green));
            faces.Add((14, [snoutBase, snoutBase + 3, snoutTip], Green));
            // Three small spikes along the back of the head/neck, standing in for Yoshi's mane.
            void Spike(float y, float z, float size)
            {
                var b = vertices[14].Count;
                vertices[14].Add(new Vector3(-size, y, z - size)); vertices[14].Add(new Vector3(size, y, z - size));
                vertices[14].Add(new Vector3(size, y, z + size)); vertices[14].Add(new Vector3(-size, y, z + size));
                var tip = vertices[14].Count;
                vertices[14].Add(new Vector3(0, y + size * 2f, z));
                faces.Add((14, [b + 1, b, tip], Saddle));
                faces.Add((14, [b + 2, b + 1, tip], Saddle));
                faces.Add((14, [b + 3, b + 2, tip], Saddle));
                faces.Add((14, [b, b + 3, tip], Saddle));
            }
            Spike(H * .90f, -35, 16); Spike(H * .86f, -55, 14); Spike(H * .80f, -68, 12);
        }

        // --- Saddle (15, the rig's own cap slot -- repurposed): small, on the upper back ---
        {
            // Kept deliberately modest (narrower than the head's own 100-wide cheeks, and lower/
            // shorter than Bowser's shell) -- Bowser's first pass made an oversized bone-15 back
            // accessory swallow the whole head from a front view; this stays clearly smaller.
            var rim = R(15, 0, H * .58f, -95, 78, 42, 10);
            var top = R(15, 0, H * .66f, -100, 82, 46, 10);
            Chain(15, [rim, top], Saddle, true, true);
        }

        // Empty accessory bones (1, 16, 17, 18) still need one point each -- same requirement every
        // character in this project documents for its own unused bones.
        var origins = new Vector3[19];
        origins[0] = Vector3.Zero; origins[1] = new Vector3(0, H * .34f, 0); origins[2] = new Vector3(0, H * .34f, 0);
        origins[3] = new Vector3(0, H * .46f, 0); origins[4] = new Vector3(150, H * .73f, 0); origins[5] = new Vector3(150, H * .62f, 0);
        origins[6] = new Vector3(-150, H * .73f, 0); origins[7] = new Vector3(-150, H * .62f, 0);
        origins[8] = new Vector3(90, H * .34f, 0); origins[9] = new Vector3(90, H * .20f, 0); origins[10] = new Vector3(90, H * .09f, 0);
        origins[11] = new Vector3(-90, H * .34f, 0); origins[12] = new Vector3(-90, H * .20f, 0); origins[13] = new Vector3(-90, H * .09f, 0);
        origins[14] = new Vector3(0, H * .765f, 0); origins[15] = new Vector3(0, H * .58f, -95);
        origins[16] = new Vector3(0, H * .66f, -100); origins[17] = new Vector3(0, H * .46f, 0); origins[18] = new Vector3(0, H * .46f, 0);
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
