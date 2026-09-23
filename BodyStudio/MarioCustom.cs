using System.IO;
using System.Linq;
using System.Numerics;

namespace LbaBodyStudio;

// A hand-authored body, not derived from any reference photo -- Humanoid.Build's own Loft reads a photo's silhouette
// row by row; this instead places each ring's centre/radius directly, chosen to match Mario's actual proportions
// (big head, short legs, stocky torso) rather than being bounded to whatever shape a reshaped Twinsen donor allows.
// Keeps the SAME 19-bone hierarchy/parent structure Humanoid.Build uses (COMMON.H's own Twinsen rig, animations bind
// to a body purely by bone count -- see ENGINE_FILE_FORMATS.md) so the retail engine's whole existing animation
// library still drives it, and bones 15/16 (empty "accessory" slots in every New-humanoid body -- children of the
// head bone, 14) hold the cap instead of nothing, matching how the rig already expects head-attached accessories.
public static class MarioCustom
{
    static readonly int[] Parents = [-1, 0, 1, 2, 3, 4, 3, 6, 2, 8, 9, 2, 11, 12, 3, 14, 15, 3, 17];

    // game/colour overrides let the same proportions/topology target either title: each game has
    // its own RESS.HQR palette, so a colour INDEX from one means nothing in the other (verified via
    // BodyPipeline's `findcolour` against both archives -- e.g. LBA1's own equivalent "Twinsen red"
    // sits at a completely different index than LBA2's). Defaults are LBA2's own proven values
    // (unchanged from before this became a parameter, so every existing caller keeps working as-is);
    // pass game:1 with LBA1-specific indices (also reused from a real donor body, same technique) to
    // build the LBA1 variant instead.
    public static Body Build(Body donor, int game = 2, int red = 80, int skin = 35, int dark = 97, int blue = 197)
    {
        const float H = 1240; // Twinsen's own total height (COMMON.H/verified this session) -- keeps Mario a normal in-game NPC scale
        var vertices = Enumerable.Range(0, 19).Select(_ => new List<Vector3>()).ToArray();
        var faces = new List<(int Bone, int[] Points, int Colour)>();

        // A lit face's stored colour is the darkest end of its own ramp -- the engine brightens it at render time
        // by walking FORWARD within that same ramp (colour + light step, see reference-body-lighting-and-flat-
        // sheets memory). Ramps in this specific palette are NOT reliably 16-aligned (tried restricting NearestColour
        // to index%16==0 first -- it "fixed" white but broke red/skin/etc, since plenty of this palette's own real
        // ramps start off that grid), and there's no cheap way to detect an arbitrary ramp's own start purely from
        // RGB distance to a bright target colour (the ramp start is dark almost by definition, so it never looks
        // much like the bright colour it eventually ramps up to). Sidesteps guessing entirely: these are literally
        // Twinsen's own donor body's real face colours (dumped via `twinsencolours`), already proven to render
        // correctly once lit, reused directly for the equivalent material instead of computing a fresh match.
        int Red = red, Skin = skin, Dark = dark, Blue = blue, White = Skin, Brown = Dark;

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
            // begin/offsets assume `rings` is the CURRENT TRAILING SPAN of vertices[bone] -- verified,
            // not just assumed, after a real bug (2026-09-23, see this file's own comment at the
            // bone-3 call site) where building all of a bone's rings up front then chaining a
            // non-trailing subset silently pointed a call at the wrong rings. Building right before
            // each Chain() call (not batching rings ahead) is what keeps this true; this check turns
            // a future slip into an immediate, obvious exception instead of a silent bad render.
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

        // --- Legs: thigh(8/11) -> shin(9/12) -> foot(10/13), one side at a time (side=+1 right, -1 left) ---
        void Leg(int thigh, int shin, int foot, int side)
        {
            float x = side * 95;
            var hip = R(thigh, x, H * .455f, 0, 78, 70, 8);
            var knee = R(thigh, x, H * .29f, 0, 62, 58, 8);
            Chain(thigh, [hip, knee], Blue, true, false);
            var shinTop = R(shin, x, H * .29f, 0, 62, 58, 8);
            var ankle = R(shin, x, H * .095f, 0, 50, 48, 8);
            Chain(shin, [shinTop, ankle], Blue, false, false);
            var footTop = R(foot, x, H * .095f, 0, 55, 52, 8);
            var toe = R(foot, x, H * .01f, H * .10f, 60, 58, 8);
            var sole = R(foot, x, H * .0f, H * .06f, 48, 50, 8);
            Chain(foot, [footTop, toe, sole], Brown, false, true);
        }
        Leg(8, 9, 10, 1); Leg(11, 12, 13, -1);

        // --- Hip / lower torso (2): blue overalls, waist up to the chest band ---
        {
            var waist = R(2, 0, H * .455f, 0, 155, 110, 10);
            var chest = R(2, 0, H * .58f, 0, 175, 120, 10);
            Chain(2, [waist, chest], Blue, false, false);
        }
        // --- Upper torso / shoulders (3): red shirt above the overalls bib line, tapering into the neck ---
        {
            // Chain assumes its own rings are the CURRENT TRAILING span of vertices[bone] -- calling
            // it before shoulders/neck exist (rather than after building all four rings up front) is
            // required, not stylistic: with all four built first, this call's [bib,chest] would no
            // longer be the tail (shoulders/neck would be), so its offsets would silently point at
            // the WRONG rings -- a real bug found live 2026-09-23 (PeachCustom.cs's own build caught
            // it: a hole/mislabelled-colour band between bib and chest, present here too once checked).
            var bib = R(3, 0, H * .58f, 0, 175, 120, 10);
            var chest = R(3, 0, H * .70f, 0, 182, 128, 10);
            Chain(3, [bib, chest], Blue, false, false);
            var shoulders = R(3, 0, H * .775f, 0, 165, 118, 10);
            var neck = R(3, 0, H * .815f, 0, 78, 68, 10);
            Chain(3, [chest, shoulders, neck], Red, false, false);
        }

        // --- Arms: upper(4/6) -> forearm+hand(5/7), side=+1 right, -1 left ---
        void Arm(int upper, int fore, int side)
        {
            float x = side * 195;
            var shoulder = R(upper, x, H * .775f, 0, 62, 58, 8);
            var elbow = R(upper, x, H * .615f, 0, 52, 50, 8);
            Chain(upper, [shoulder, elbow], Red, true, false);
            var foreTop = R(fore, x, H * .615f, 0, 52, 50, 8);
            var wrist = R(fore, x, H * .48f, 0, 44, 42, 8);
            Chain(fore, [foreTop, wrist], Skin, false, false);
            var glove = R(fore, x, H * .40f, 0, 62, 58, 8);
            var fist = R(fore, x, H * .345f, 0, 50, 48, 8);
            Chain(fore, [wrist, glove, fist], White, false, true);
        }
        Arm(4, 5, 1); Arm(6, 7, -1);

        // --- Head (14): tan skin, rounded, with a bump for the nose ---
        {
            var neck = R(14, 0, H * .815f, 0, 68, 60, 10);
            var jaw = R(14, 0, H * .855f, 0, 92, 84, 10);
            var cheeks = R(14, 0, H * .90f, 0, 100, 92, 10);
            var brow = R(14, 0, H * .945f, 0, 88, 82, 10);
            var crown = R(14, 0, H * .975f, 0, 55, 55, 10);
            Chain(14, [neck, jaw, cheeks, brow, crown], Skin, false, true);
            // A short nose bump: a standalone 4-point base (not tied to any ring's own vertex order/angle
            // convention -- Ring's own angle 0 points +X, not +Z/"front") plus a tip poking further forward (+Z).
            // Wound so the Newell normal points toward +Z (out of the face, toward the viewer) -- matches Chain's
            // own ring winding (proven via the rest of this body rendering the right way out), reversed here since
            // these aren't going through Chain at all.
            var noseBase = vertices[14].Count;
            vertices[14].Add(new Vector3(-32, H * .915f, 108)); vertices[14].Add(new Vector3(32, H * .915f, 108));
            vertices[14].Add(new Vector3(28, H * .878f, 102)); vertices[14].Add(new Vector3(-28, H * .878f, 102));
            var noseTip = vertices[14].Count;
            vertices[14].Add(new Vector3(0, H * .898f, 150));
            faces.Add((14, [noseBase + 1, noseBase, noseTip], Skin));
            faces.Add((14, [noseBase + 2, noseBase + 1, noseTip], Skin));
            faces.Add((14, [noseBase + 3, noseBase + 2, noseTip], Skin));
            faces.Add((14, [noseBase, noseBase + 3, noseTip], Skin));
            // A moustache patch: a small quad in front of the jaw/cheek band, at the nose's own base height.
            var mBase = vertices[14].Count;
            vertices[14].Add(new Vector3(-50, H * .882f, 98)); vertices[14].Add(new Vector3(50, H * .882f, 98));
            vertices[14].Add(new Vector3(54, H * .862f, 90)); vertices[14].Add(new Vector3(-54, H * .862f, 90));
            faces.Add((14, [mBase + 3, mBase + 2, mBase + 1, mBase], Dark));
        }

        // --- Cap (15, a child of the head bone -- one of the rig's own empty accessory slots): dome + brim ---
        {
            var brimBack = R(15, 0, H * .965f, -20, 108, 96, 10);
            var brimFront = R(15, 0, H * .965f, 150, 112, 30, 10);
            var band = R(15, 0, H * .975f, -10, 96, 86, 10);
            var dome = R(15, 0, H * 1.03f, -10, 78, 70, 10);
            var top = R(15, 0, H * 1.075f, -10, 20, 20, 10);
            Chain(15, [band, dome, top], Red, false, true);
            // Brim as its own short flat wedge (front ring only meaningfully separate from the band -- back ring
            // mostly coincides with the head/band silhouette and is there only so the brim has a back edge to cap).
            var bb = vertices[15].Count - brimBack.Count - brimFront.Count - band.Count - dome.Count - top.Count;
            var bf = bb + brimBack.Count;
            var n2 = brimBack.Count;
            for (var j = 0; j < n2; j++) faces.Add((15, [bb + j, bb + (j + 1) % n2, bf + (j + 1) % n2, bf + j], Red));
            for (var j = 1; j < n2 - 2; j += 2) faces.Add((15, [bb, bb + j + 2, bb + j + 1, bb + j], Red));
            // White circle + red "M" badge on the front of the cap, a single flat quad (palette-limited, this is as
            // close as a ~500-poly budget gets to the real emblem without spending faces better used on silhouette).
            var badge = vertices[15].Count;
            vertices[15].Add(new Vector3(-38, H * .995f, 138)); vertices[15].Add(new Vector3(38, H * .995f, 138));
            vertices[15].Add(new Vector3(38, H * .935f, 132)); vertices[15].Add(new Vector3(-38, H * .935f, 132));
            faces.Add((15, [badge + 3, badge + 2, badge + 1, badge], White));
        }

        // Empty accessory bones (1, 16, 17, 18) still need one point each so every engine animation group is valid --
        // same requirement Humanoid.Build documents for its own unused bones.
        var origins = new Vector3[19];
        origins[0] = Vector3.Zero; origins[1] = new Vector3(0, H * .455f, 0); origins[2] = new Vector3(0, H * .455f, 0);
        origins[3] = new Vector3(0, H * .58f, 0); origins[4] = new Vector3(195, H * .775f, 0); origins[5] = new Vector3(195, H * .615f, 0);
        origins[6] = new Vector3(-195, H * .775f, 0); origins[7] = new Vector3(-195, H * .615f, 0);
        origins[8] = new Vector3(95, H * .455f, 0); origins[9] = new Vector3(95, H * .29f, 0); origins[10] = new Vector3(95, H * .095f, 0);
        origins[11] = new Vector3(-95, H * .455f, 0); origins[12] = new Vector3(-95, H * .29f, 0); origins[13] = new Vector3(-95, H * .095f, 0);
        origins[14] = new Vector3(0, H * .815f, 0); origins[15] = new Vector3(0, H * .975f, -10);
        origins[16] = new Vector3(0, H * 1.075f, -10); origins[17] = new Vector3(0, H * .58f, 0); origins[18] = new Vector3(0, H * .58f, 0);
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
            // 38 bytes, not 8: harmless filler for the LBA2 write path (which builds its own bone
            // bytes from Start/Count/Pivot/Parent and never reads Record at all) but required by
            // the LBA1 write path, which clones/patches specific offsets within Record directly
            // (Body.Write's Game==1 branch, Array.Clear at offsets 8 and 18) -- Body.Read's own
            // LBA1 branch confirms 38 bytes is a real LBA1 bone record's actual width (`b[q..(q+38)]`).
            body.Bones.Add(new(starts[i], vertices[i].Count, pivot, Parents[i], new byte[38]));
        }
        foreach (var f in faces) body.Faces.Add(new(f.Points.Select(p => p + starts[f.Bone]).ToArray(), f.Colour));
        body.SetWorld(body.Vertices.ToArray());
        body.Validate();
        return body;
    }
}
