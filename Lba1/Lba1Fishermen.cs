using System.IO;
using System.Text;
using LBA2LevelEditor.LbaScript;
using LBA2LevelEditor.Scenes;

namespace LBA2LevelEditor.Lba1;

// The boat trips of LBA1's three fishermen, opened up for chapter 6.
//
// How a trip works (Principal Island's Port Belooga is scene 24, the White Leaf Desert's military camp 39, Proxima City 42): the fisherman
// asks where to go and takes 10 Kashes, his boat sails, and the hero's script then plays the trip on the holomap (holomap_traj) and puts
// Twinsen on a point of the scene (pos_point) that lies inside a cube-change zone: standing there is what carries him to the other island.
// Which destination was chosen is kept in the scene variable 0 (var_cube(0)); the scripts tested it for 1 (one destination) and anything
// else (the other), so a third destination is variable 0 = 2.
//
//   Port Belooga (scene 24, fisherman = actor 1): in chapter 5 he offers the White Leaf Desert (var 0 = 1; Proxima, var 0 = 0, is the
//     route left in the script but never offered). In chapter 6 he now asks "For 10 Kashes, where do you want to go, Twinsen?" (a new line
//     in TEXT.HQR) and offers Citadel Island (var 0 = 2, trip 44, then a landing of our own, below), the White Leaf Desert and Proxima Island.
//   Military camp (scene 39, fisherman = actor 2): offered Citadel (chapter 6 and later) and Principal Island; in chapter 6 he also offers
//     Proxima Island (var 0 = 2, trip 21, then a landing of our own).
//   Proxima City (scene 42, fisherman = actor 10): offered Principal and Citadel Island; in chapter 6 he also offers the White Leaf Desert
//     (var 0 = 2, trip 31, then change_cube(39)).
//
// The landings: the game's own zones for its chapter-6 ferry (Port Belooga -> the Citadel's second harbour, the camp -> Proxima's western
// edge, Proxima -> the desert's east edge) start arrival scenes that wait for a boat which only exists while var_game(68) is set (actors
// 5, 6 and 20): Twinsen stays hidden and frozen. So the trips end where the fishermen's own routes do. Port Belooga and the camp get a
// zone of their own (Landing) that arrives exactly where the game's zone into the Citadel's quay (scene 6) / Proxima's pier (scene 42)
// does, which starts the fisherman's boat sailing in; Proxima's trip loads the desert scene at its start position (zone 0 of the camp,
// where the boat sails in and the fisherman steps out), the way the desert's own opening does.
//
// Proxima's fisherman also gets a fix for a game bug: after sitting down facing the water (angle 0) he stood up and walked to his boat
// with the walk animation running before he had turned (goto_point turns gradually), so his first steps carried him off the pier's edge
// into the sea. His walk to the boat now starts with a turn towards it.
internal static class Lba1Fishermen
{
    public const int PrincipalScene = 24, DesertScene = 39, ProximaScene = 42;

    // The chapter-6 question on Principal Island. The retail line (island 1's text 45) is a whole speech ("If the Astronomer sent you ...
    // For 10 Kashes I'll take you where you want to go."); the trips need only the question, added as the last text of island 1's dialogue
    // file in every language (the last position is past every voice file's table, so it is silent and no voice shifts).
    public const int QuestionId = 286;
    public const string QuestionEnglish = "For 10 Kashes, where do you want to go, Twinsen?";
    private const int SourceTextFile = 6, SourceTextId = 8;

    public sealed record Plan(IReadOnlyList<SceneChange> Changes, IReadOnlyList<string> Notes)
    {
        public bool Changed => Changes.Count > 0;
    }

    // One text replacement in a script. Olds lists every text the script may have now (the game's own, and what earlier versions of this
    // tool wrote), so an older install is brought up to date in place.
    private sealed record Patch(string Name, string[] Olds, string New);

    // Every edit as (scene, actor, script, patches); a patch is skipped when the script already has its new text and refused when it has none of its old ones.
    private sealed record ScriptEdit(int Scene, int Actor, ScriptKind Kind, IReadOnlyList<Patch> Patches);

    private const string PrincipalOfferGame =
        "        if (6 == chapter() || 8 == chapter() || 9 == chapter() || 15 == chapter())\n        {\n            message(284);\n        }\n        set_comportement(comportement_1);\n";

    // Port Belooga's chapter-6 offer, asking with the given text.
    private static string PrincipalOffer(int question) =>
        "        if (8 == chapter() || 9 == chapter() || 15 == chapter())\n        {\n            message(284);\n        }\n" +
        "        if (6 == chapter())\n        {\n            add_choice(118);\n            add_choice(47);\n            add_choice(79);\n            add_choice(48);\n            ask_choice(" + question + ");\n" +
        "            if (118 == choice() || 47 == choice() || 79 == choice())\n            {\n                if (10 <= nb_gold_pieces())\n                {\n" +
        "                    if (47 == choice())\n                    {\n                        set_var_cube(0, 1);\n                    }\n" +
        "                    if (118 == choice())\n                    {\n                        set_var_cube(0, 2);\n                    }\n" +
        "                    give_gold_pieces(10);\n                    set_var_cube(2, 1);\n                    set_track(label_3);\n                    clr_holo_pos(24);\n                }\n" +
        "                else\n                {\n                    message(49);\n                    set_track(label_2);\n                }\n            }\n        }\n" +
        "        set_comportement(comportement_1);\n";

    // A trip chosen by var_cube(0): 1 and the default are the routes the game had, 2 the new third destination.
    private static string Destinations(int oneTrip, int onePoint, int newTrip, string newLanding, int elseTrip, int elsePoint) =>
        "        if (1 == var_cube(0))\n        {\n            holomap_traj(" + oneTrip + ");\n            pos_point(" + onePoint + ");\n        }\n" +
        "        else if (2 == var_cube(0))\n        {\n            holomap_traj(" + newTrip + ");\n            " + newLanding + ";\n        }\n" +
        "        else\n        {\n            holomap_traj(" + elseTrip + ");\n            pos_point(" + elsePoint + ");\n        }\n";

    private static string TwoDestinations(int oneTrip, int onePoint, int elseTrip, int elsePoint) =>
        "        if (1 == var_cube(0))\n        {\n            holomap_traj(" + oneTrip + ");\n            pos_point(" + onePoint + ");\n        }\n" +
        "        else\n        {\n            holomap_traj(" + elseTrip + ");\n            pos_point(" + elsePoint + ");\n        }\n";

    // ---- landings ----------------------------------------------------------------------------------------------------

    // A landing that plays the arrival the fishermen's own trips have: a cube-change zone into the destination island's quay, high above the
    // scene where nobody can walk into it, with a track point in it for pos_point (Twinsen arrives at the zone's destination corner plus his offset
    // inside the zone: the destination values here give the same arrival point as the game's own zone into that quay, which is what starts the boat
    // sailing in with the fisherman). The game's chapter-6 ferry zones (to Proxima's and the Citadel's other quays) belong to a boat that only
    // exists while var_game(68) is set: landing there with the fishermen leaves Twinsen hidden, waiting for it.
    private sealed record Landing(int Scene, int Destination, int ArrivalX, int ArrivalY, int ArrivalZ, int OffsetX, int OffsetZ, string What);

    private const int SkyY = 4096, SkyHeight = 1536, PointY = SkyY + 768, BoxX = 1024, BoxZ = 1024, BoxSize = 1024;

    private static readonly Landing[] Landings =
    {
        // Port Belooga -> the Citadel's quay (scene 6, zone 14's boat arrival), as Proxima City's fisherman lands him there
        new(PrincipalScene, 6, 15104, 1280, 21760, 784, 576, "the Citadel"),
        // the military camp -> Proxima's pier (scene 42, zone 7's boat arrival), as Port Belooga's zone 3 lands him there
        new(DesertScene, 42, 1792, 1024, 20736, 800, 560, "Proxima Island"),
    };

    private static SceneZoneModel LandingZone(Landing l) => new()
    {
        X0 = BoxX, Y0 = SkyY, Z0 = BoxZ, X1 = BoxX + BoxSize - 1, Y1 = SkyY + SkyHeight - 1, Z1 = BoxZ + BoxSize - 1,
        Type = 0, Info = new[] { l.Destination, l.ArrivalX, l.ArrivalY - (PointY - SkyY), l.ArrivalZ },
    };

    private static SceneTrackPoint LandingPoint(Landing l) => new(BoxX + l.OffsetX, PointY, BoxZ + l.OffsetZ);

    // Adds the landing's zone and point to the scene unless they are there; returns the point's number and whether anything was added.
    private static (int Point, bool Added) EnsureLanding(SceneModel model, Landing l)
    {
        var zone = LandingZone(l);
        var point = LandingPoint(l);
        var zoneThere = model.Zones.Any(z => z.Type == 0 && z.X0 == zone.X0 && z.Y0 == zone.Y0 && z.Z0 == zone.Z0 && z.X1 == zone.X1 && z.Y1 == zone.Y1 && z.Z1 == zone.Z1 && z.Info.SequenceEqual(zone.Info));
        var index = model.TrackPoints.IndexOf(point);
        if (zoneThere && index >= 0) return (index, false);
        if (model.Zones.Any(z => z.X0 <= zone.X1 && z.X1 >= zone.X0 && z.Y0 <= zone.Y1 && z.Y1 >= zone.Y0 && z.Z0 <= zone.Z1 && z.Z1 >= zone.Z0))
            throw new InvalidDataException($"Scene {l.Scene} already has a zone in the sky where the landing for {l.What} would go; not touching it.");
        if (!zoneThere) SceneOps.AddZone(model, zone);
        if (index < 0) index = SceneOps.AddTrackPoint(model, point);
        return (index, true);
    }

    // Every edit as a function of the landing points' numbers in their scenes.
    private static ScriptEdit[] Edits(IReadOnlyDictionary<int, int> landingPoint) => new ScriptEdit[]
    {
        new(PrincipalScene, 1, ScriptKind.Life, new[]
        {
            new Patch("chapter-6 trip offer", new[] { PrincipalOfferGame, PrincipalOffer(45) }, PrincipalOffer(QuestionId)),
        }),
        new(PrincipalScene, 0, ScriptKind.Life, new[]
        {
            new Patch("landing on the Citadel",
                new[] { TwoDestinations(17, 4, 18, 10), Destinations(17, 4, 44, "pos_point(16)", 18, 10) },
                Destinations(17, 4, 44, $"pos_point({landingPoint.GetValueOrDefault(PrincipalScene, -1)})", 18, 10)),
        }),
        new(DesertScene, 2, ScriptKind.Life, new[]
        {
            new Patch("Proxima Island in the menu",
                new[] { "    if (5 < chapter())\n    {\n        add_choice(10);\n    }\n    add_choice(6);\n    add_choice(7);\n    ask_choice(8);\n" },
                "    if (5 < chapter())\n    {\n        add_choice(10);\n    }\n    add_choice(6);\n    if (6 == chapter())\n    {\n        add_choice(17);\n    }\n    add_choice(7);\n    ask_choice(8);\n"),
            new Patch("the trip to Proxima Island",
                new[] { "            message(9);\n            set_comportement(comportement_1);\n        }\n    }\n    else\n    {\n        set_comportement(comportement_1);\n    }\n}\n" },
                "            message(9);\n            set_comportement(comportement_1);\n        }\n    }\n" +
                "    else if (17 == choice())\n    {\n        if (10 <= nb_gold_pieces())\n        {\n            set_var_cube(1, 1);\n            set_var_cube(0, 2);\n            if (0 == l_track())\n            {\n                set_track(label_1);\n            }\n            else\n            {\n                set_track(label_2);\n            }\n            give_gold_pieces(10);\n            set_comportement(comportement_3);\n        }\n" +
                "        else\n        {\n            message(9);\n            set_comportement(comportement_1);\n        }\n    }\n" +
                "    else\n    {\n        set_comportement(comportement_1);\n    }\n}\n"),
        }),
        new(DesertScene, 0, ScriptKind.Life, new[]
        {
            new Patch("landing on Proxima Island",
                new[] { TwoDestinations(14, 6, 72, 9), Destinations(14, 6, 21, "pos_point(13)", 72, 9) },
                Destinations(14, 6, 21, $"pos_point({landingPoint.GetValueOrDefault(DesertScene, -1)})", 72, 9)),
        }),
        new(ProximaScene, 10, ScriptKind.Life, new[]
        {
            new Patch("the desert in the menu",
                new[] { "    add_choice(5);\n    add_choice(6);\n    add_choice(7);\n    ask_choice(8);\n" },
                "    add_choice(5);\n    add_choice(6);\n    if (6 == chapter())\n    {\n        add_choice(62);\n    }\n    add_choice(7);\n    ask_choice(8);\n"),
            new Patch("the trip to the desert",
                new[] { "            message(9);\n            set_comportement(comportement_1);\n        }\n    }\n    else\n    {\n        set_comportement(comportement_1);\n    }\n}\n" },
                "            message(9);\n            set_comportement(comportement_1);\n        }\n    }\n" +
                "    else if (62 == choice())\n    {\n        if (10 <= nb_gold_pieces())\n        {\n            set_var_cube(0, 2);\n            give_gold_pieces(10);\n            if (0 == l_track())\n            {\n                set_track(label_1);\n            }\n            else\n            {\n                set_track(label_2);\n            }\n            set_comportement(comportement_4);\n        }\n" +
                "        else\n        {\n            message(9);\n            set_comportement(comportement_1);\n        }\n    }\n" +
                "    else\n    {\n        set_comportement(comportement_1);\n    }\n}\n"),
        }),
        new(ProximaScene, 10, ScriptKind.Track, new[]
        {
            // (his sit-down point 2 is within 500 units of where he stops, ~160 from the pier's edge, and he faces the water; the walk cycle's first
            // steps are 335 and 225 units forward)
            new Patch("turning towards his boat before he walks to it",
                new[] { "label(2);\nanim(1);\ngoto_point(3);\n" },
                "label(2);\nangle(704);\nanim(1);\ngoto_point(3);\n"),
        }),
        new(ProximaScene, 0, ScriptKind.Life, new[]
        {
            new Patch("landing in the desert",
                new[]
                {
                    "        if (1 == var_cube(0))\n        {\n            holomap_traj(15);\n            pos_point(4);\n        }\n        else\n        {\n            holomap_traj(16);\n            pos_point(5);\n        }\n",
                    "        if (1 == var_cube(0))\n        {\n            holomap_traj(15);\n            pos_point(4);\n        }\n        else if (2 == var_cube(0))\n        {\n            holomap_traj(31);\n            pos_point(30);\n        }\n        else\n        {\n            holomap_traj(16);\n            pos_point(5);\n        }\n",
                },
                "        if (1 == var_cube(0))\n        {\n            holomap_traj(15);\n            pos_point(4);\n        }\n        else if (2 == var_cube(0))\n        {\n            holomap_traj(31);\n            change_cube(39);\n        }\n        else\n        {\n            holomap_traj(16);\n            pos_point(5);\n        }\n"),
        }),
    };

    // What is in the game folder now and what has to change to open the trips up; nothing when they already are.
    public static Plan PlanFor(SceneStore store)
    {
        var changes = new List<SceneChange>();
        var notes = new List<string>();
        var scenes = new[] { PrincipalScene, DesertScene, ProximaScene };
        foreach (var scene in scenes)
        {
            var record = store.LoadRecord(scene);
            var done = new List<string>();

            // the landing zone and point (scenes 24 and 39), added to the scene before its scripts are compiled against it
            var points = new Dictionary<int, int>();
            foreach (var landing in Landings.Where(l => l.Scene == scene))
            {
                var model = SceneSerializer.Parse(SceneGame.Lba1, record);
                var (point, added) = EnsureLanding(model, landing);
                points[scene] = point;
                if (added)
                {
                    record = SceneSerializer.Write(model);
                    done.Add($"a landing zone for {landing.What} (point {point})");
                }
            }

            var scripts = SceneScripts.Load(record, scene, null, lba1: true);
            foreach (var edit in Edits(points).Where(e => e.Scene == scene))
            {
                var text = scripts.GetText(edit.Actor, edit.Kind);
                var edited = false;
                foreach (var patch in edit.Patches)
                {
                    // (a new text can end with an old one, so look for the new one first)
                    if (Occurrences(text, patch.New) == 1) continue;   // already opened up
                    var old = patch.Olds.FirstOrDefault(o => Occurrences(text, o) == 1);
                    if (old is null) throw new InvalidDataException($"Scene {scene}, actor {edit.Actor}'s {(edit.Kind == ScriptKind.Life ? "life" : "track")} script isn't what this edit expects ({patch.Name}); not touching it. If an earlier edit changed it, restore SCENE.HQR from the .bak copy first.");
                    text = text.Replace(old, patch.New);
                    done.Add($"actor {edit.Actor}: {patch.Name}");
                    edited = true;
                }
                if (edited) scripts.SetText(edit.Actor, edit.Kind, text);
            }
            if (done.Count == 0) continue;
            var built = scripts.Build();
            if (!built.Ok) throw new InvalidDataException($"Scene {scene}'s edited scripts don't compile: {string.Join("; ", built.Errors.Select(e => e.ToString()))}");
            changes.Add(new SceneChange(scene, SceneSerializer.Parse(SceneGame.Lba1, built.Record!)));
            notes.Add($"scene {scene}: {string.Join(", ", done)}");
        }
        return new Plan(changes, notes);
    }

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // ---- TEXT.HQR ------------------------------------------------------------------------------------------------

    // The chapter-6 question as the last text of Principal Island's dialogue file, in all five languages (English as written above; the others
    // copy the price line that Proxima's and the desert's fishermen ask in that language, "For 10 Kashes, where do you want to go, Twinsen ?").
    public static readonly Lba1DialogueText.AddedText Question = new(QuestionId, "the fisherman's question", QuestionEnglish, (texts, language) => SourceQuestion(texts, language));

    // Returns the TEXT.HQR entries to write, or nothing when it already has the text. A bank that has that id with other text, or no room left, is refused.
    public static IReadOnlyList<Scenes.HqrEntryStore.Edit> PlanText(string directory) => Lba1DialogueText.Plan(directory, new[] { Question });

    // The same language's price line of the island-3 fisherman, as the bytes stored (with the closing NUL); null when it isn't there.
    private static byte[]? SourceQuestion(HqrFile texts, int language)
    {
        var at = (language * Lba1DialogueText.FilesPerLanguage + SourceTextFile) * 2;
        if (at + 1 >= texts.Count || texts.IsEmpty(at) || texts.IsEmpty(at + 1)) return null;
        var (ids, strings) = Lba1DialogueText.Split(texts.Read(at), texts.Read(at + 1));
        var i = ids.IndexOf((ushort)SourceTextId);
        return i < 0 ? null : strings[i];
    }
}
