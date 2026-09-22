using LBAAssembler;
using LBAAssembler.Lba1.Runtime;
using LBAAssembler.Lba1;
using LBAAssembler.LbaScript;
using LBAAssembler.Scenes;

namespace ScriptRoundTrip;

// The LBA1 simulation (Lba1/Runtime) against the real game files: the math it is built on, every scene started and run,
// and scripted walks (through the doors, between scenes).
//   runtime math | smoke | walk | doors | all
internal static class RuntimeTests
{
    private static readonly string Lba1Dir = Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure";
    private static int failures, checks;

    private static void Check(bool ok, string what)
    {
        checks++;
        if (ok) return;
        failures++;
        Console.WriteLine($"  FAIL {what}");
    }

    public static int Run(string[] args)
    {
        var what = args.Length > 1 ? args[1] : "all";
        var data = new Lba1RuntimeData(Lba1Dir);
        if (what is "math" or "all") Math_();
        if (what is "smoke" or "all") Smoke(data);
        if (what is "walk" or "all") Walk(data);
        if (what is "doors" or "all") { Doors(data); RetailBonusZone(data); DoorLock(); SecretRoom(); }
        if (what is "fishermen" or "all") Fishermen();
        if (what is "text" or "all") Text(data);
        if (what is "dialogue" or "all") Dialogue(data);
        if (what is "extras" or "all") Extras(data);
        if (what is "films" or "all") Films(data);
        if (what == "talk")
        {
            for (var scene = 0; scene < 120; scene++)
            {
                var r = new Lba1Runtime(data);
                r.ChangeCube(scene);
                r.Run(300);
                var says = r.Events.Where(e => e.Contains("says") || e.Contains("asks")).Take(2).ToList();
                if (says.Count > 0) Console.WriteLine($"scene {scene}: {string.Join(" || ", says)}");
            }
        }
        if (what == "hero")
        {
            var groups = new Dictionary<string, int>();
            for (var s = 0; s < 120; s++) { var h = data.Scene(s).Hero; var key = $"life {BitConverter.ToString(h.Life)} track {BitConverter.ToString(h.Track)} flags {h.Flags:X} entity {h.Entity} body {h.Body} anim {h.Anim}"; groups[key] = groups.GetValueOrDefault(key) + 1; }
            foreach (var (k, v) in groups.OrderByDescending(g => g.Value).Take(6)) Console.WriteLine($"{v,3}x  {k}");
        }
        if (what == "items")
        {
            var bank = data.Text(2)!;
            foreach (var id in new[] { 0, 1, 2, 3, 12, 27, 100, 101, 102, 103, 112, 127, 128, 161, 162 })
                Console.WriteLine($"  text {id}: {bank.Get(id)?.Replace("\n", " / ")}");
        }
        if (what == "shadow")
        {
            var res = HqrArchive.Open(Path.Combine(Lba1Dir, "RESS.HQR"));
            var d = res.Read(4);
            Console.WriteLine($"RESS 4: {d.Length} bytes: {BitConverter.ToString(d, 0, Math.Min(64, d.Length))}");
            Console.WriteLine("light (alpha, beta): " + string.Join("  ", new[] { 0, 1, 5, 11, 13, 25, 61 }.Select(s => $"{s}:({data.Scene(s).AlphaLight},{data.Scene(s).BetaLight})")));
        }
        if (what == "vox")
        {
            using var fs = File.OpenRead(Path.Combine(Lba1Dir, "VOX", "EN_003.VOX"));
            var br = new BinaryReader(fs);
            var first = br.ReadUInt32();
            fs.Position = 0;
            var table = new uint[first / 4];
            for (var i = 0; i < table.Length; i++) table[i] = br.ReadUInt32();
            Console.WriteLine($"EN_003: table {table.Length} entries, first {table[0]}, then {string.Join(",", table.Skip(1).Take(5))}, bank 3 has {data.Text(3)!.Count} texts");
            foreach (var n in new[] { 0, 1, 2 })
            {
                fs.Position = table[n];
                var size = br.ReadUInt32(); var packed = br.ReadUInt32(); var method = br.ReadUInt16();
                var head = br.ReadBytes(24);
                Console.WriteLine($"  entry {n} at {table[n]}: size {size} packed {packed} method {method} head {BitConverter.ToString(head)}");
            }
        }
        if (what == "media")
        {
            var mi = HqrArchive.Open(Path.Combine(Lba1Dir, "MIDI_MI.HQR"));
            Console.WriteLine($"MIDI_MI.HQR: {mi.Count} entries");
            foreach (var n in mi.ValidIndices.Take(4)) { var d = mi.Read(n); Console.WriteLine($"  {n}: {d.Length} bytes {BitConverter.ToString(d, 0, Math.Min(16, d.Length))} {System.Text.Encoding.ASCII.GetString(d, 0, Math.Min(4, d.Length))}"); }
            foreach (var f in Directory.GetFiles(Path.Combine(Lba1Dir, "VOX")).Take(3)) { var d = File.ReadAllBytes(f); Console.WriteLine($"{Path.GetFileName(f)}: {d.Length} bytes {BitConverter.ToString(d, 0, 24)}"); }
            Console.WriteLine(string.Join(" ", Directory.GetFiles(Path.Combine(Lba1Dir, "VOX")).Select(Path.GetFileName)));
        }
        if (what == "sprites")
        {
            var sp = HqrArchive.Open(Path.Combine(Lba1Dir, "SPRITES.HQR"));
            foreach (var n in new[] { 0, 1, 2, 11, 12, 13 })
                if (sp.IsValid(n)) { var d = sp.Read(n); Console.WriteLine($"sprite {n}: {d.Length} bytes: {BitConverter.ToString(d, 0, Math.Min(24, d.Length))}"); }
                else Console.WriteLine($"sprite {n}: missing");
            Console.WriteLine($"entries: {sp.Count}");
        }
        Console.WriteLine(failures == 0 ? $"runtime tests: all {checks} checks passed" : $"runtime tests: {failures} of {checks} checks FAILED");
        return failures == 0 ? 0 : 1;
    }

    // Every text id the scripts and message zones refer to exists in their island's text bank; dialogues come out readable.
    private static void Text(Lba1RuntimeData data)
    {
        var missing = 0;
        var used = 0;
        for (var scene = 0; scene < 120; scene++)
        {
            var model = data.Scene(scene);
            var bank = data.Text(3 + model.Island);
            Check(bank is not null, $"scene {scene}: text bank of island {model.Island}");
            if (bank is null) continue;
            foreach (var zone in model.Zones.Where(z => z.Type == 5))
            {
                used++;
                if (bank.Get(zone.Info[0]) is null) { missing++; Console.WriteLine($"  scene {scene}: message zone text {zone.Info[0]} not in bank {3 + model.Island}"); }
            }
        }
        Check(missing == 0, $"all {used} message zones have text");
        var first = data.Text(3)?.Get(data.Text(3)!.Ids[0]);
        Console.WriteLine($"  bank 3, first text: {first?.Replace("\n", " / ")}");
        var sounds = 0;
        for (var s = 0; s < 200; s++) if (data.SampleWav(s) is { Length: > 44 }) sounds++;
        Check(sounds > 50, $"sound effects convert to WAV ({sounds} of the first 200 entries)");
        if (Directory.Exists(Path.Combine(Lba1Dir, "VOX")))
        {
            // the first spoken line of Citadel Island's texts (file 3) is a real voice sample
            var bank = data.Text(3)!;
            var spoken = 0;
            foreach (var id in bank.Ids.Take(12)) if (data.Speech(3, id) is { Length: > 1000 }) spoken++;
            Check(spoken >= 8, $"spoken dialogue is found for the texts ({spoken} of the first 12)");
        }
        int songs = 0, notes = 0;
        for (var m = 0; m < 136; m++)
        {
            if (data.MidiFile(m) is not { } smf) continue;
            songs++;
            Check(smf.Length > 30 && smf[0] == (byte)'M' && smf[1] == (byte)'T' && smf[14] == (byte)'M', $"music {m} is a well-formed MIDI file");
            for (var i = 22; i + 2 < smf.Length; i++) if ((smf[i] & 0xF0) == 0x90 && smf[i + 2] != 0 && smf[i + 1] < 128) notes++;
        }
        Check(songs > 30 && notes > 1000, $"music converts from XMIDI ({songs} pieces, ~{notes} note events)");
        Console.WriteLine($"  bank 3 has {data.Text(3)?.Count} texts, bank 0 {data.Text(0)?.Count}, bank 2 {data.Text(2)?.Count}");
    }

    // The films on the CD image decode frame by frame, and the CD's music tracks come out as audio.
    private static void Films(Lba1RuntimeData data)
    {
        if (data.Disc is null) { Console.WriteLine("  (no CD image: films skipped)"); return; }
        var decoded = 0;
        foreach (var name in new[] { "BAFFE", "INTROD", "EXPLOD", "THE_END", "DRAGON3" })
        {
            var bytes = data.Film(name);
            Check(bytes is not null, $"film {name} is on the disc");
            if (bytes is null) continue;
            var fla = new Lba1Fla(bytes);
            var frames = 0;
            var painted = 0;
            var sounds = 0;
            while (true)
            {
                try { if (!fla.NextFrame()) break; }
                catch (Exception error) { Console.WriteLine($"  film {name}: frame {fla.FrameIndex} failed: {error.GetType().Name} {error.StackTrace?.Split('\n').FirstOrDefault()}"); break; }
                frames++;
                sounds += fla.Sounds.Count;
                if (frames % 20 == 0 && fla.Pixels.Distinct().Count() > 8) painted++;
            }
            Check(frames >= fla.FrameCount - 1 && frames > 20, $"film {name}: {frames} frames decoded of {fla.FrameCount} ({fla.FramesPerSecond} a second)");
            Check(painted > 0, $"film {name}: the pictures have content ({painted} sampled frames with more than 8 colours)");
            if (sounds > 0 && Environment.GetEnvironmentVariable("RT_VERBOSE") == "1")
            {
                var iso = data.Disc!;
                var e = iso.Files.First(f => f.Path.EndsWith("FLASAMP.HQR", StringComparison.OrdinalIgnoreCase));
                var hq = HqrFile.Parse(iso.Read(e));
                var n0 = fla.SampleNumbers[0];
                Console.WriteLine($"  FLASAMP: {hq.Count} entries; sample {n0}: empty {hq.IsEmpty(n0)}; head {BitConverter.ToString(hq.Read(n0), 0, 24)}");
            }
            if (sounds > 0) Check(fla.SampleNumbers.Any(n => data.FilmSampleWav(n) is { Length: > 44 }), $"film {name}: its sound effects convert ({sounds} cues)");
            decoded++;
        }
        Console.WriteLine($"  {decoded} films decoded");
        if (data.HasCdMusic)
        {
            var track = data.CdTrackWav(2);
            Check(track is { Length: > 5_000_000 }, $"CD track 2 is audio ({track?.Length} bytes)");
        }
    }

    // Bonuses dropped by a creature and picked up, and Twinsen's magic ball: thrown, flying, and coming back.
    private static void Extras(Lba1RuntimeData data)
    {
        // a creature that gives money dies on scene 13's plaza
        var r = new Lba1Runtime(data);
        r.ChangeCube(13);
        r.Run(20);
        var victim = Enumerable.Range(1, r.NbObjets - 1).First(i => r.Objects[i].Body != -1 && !r.Objects[i].IsSprite);
        var vic = r.Objects[victim];
        vic.OptionFlags = Lba1Runtime.ExtraGiveMoney;
        vic.NbBonus = 5;
        vic.LifePoint = 0;
        var seen = false;
        for (var i = 0; i < 40 && !seen; i++) { r.Frame(); seen = r.Extras.Any(e => e.Sprite == 3); }
        Check(seen, "a dying creature drops a bonus");
        for (var i = 0; i < 100; i++) r.Frame();      // it lands
        var money = r.Extras.First(e => e.Sprite == 3);
        Check((money.Flags & Lba1Runtime.ExtraFly) == 0, "the bonus has landed");
        var before = r.NbGoldPieces;
        r.Place(money.PosX, money.PosY, money.PosZ, 0);
        for (var i = 0; i < 6; i++) r.Frame();
        Check(r.NbGoldPieces == before + 5, $"Twinsen picks the money up (gold {before} -> {r.NbGoldPieces})");

        // the magic ball
        r = new Lba1Runtime(data);
        r.ChangeCube(13);
        r.FlagGame[Lba1Runtime.FlagBalleMagique] = 1;
        r.MagicLevel = 2; r.MagicPoint = 40;
        r.Run(30);
        r.Fire = Lba1Const.FAlt;
        var flew = false;
        for (var i = 0; i < 80; i++)
        {
            r.Frame(); if (r.MagicBall != -1) flew = true; if (i == 60) r.Fire = 0;     // Alt is held while he winds up
            if (Environment.GetEnvironmentVariable("RT_VERBOSE") == "1") Console.WriteLine($"  frame {i}: anim {r.Hero.GenAnim} frame {r.Hero.Frame} actions {(r.Hero.AnimActions is null ? "none" : BitConverter.ToString(r.Hero.AnimActions))} ball {r.MagicBall} hero body {r.Hero.Body} move {r.Hero.Move} flags {r.Hero.Flags}");
        }
        Check(flew, "Alt throws the magic ball");
        for (var i = 0; i < 300 && r.MagicBall != -1; i++) r.Frame();
        Check(r.MagicBall == -1, "the ball has come back");
        Check(r.MagicPoint < 40, $"it cost magic ({r.MagicPoint} left of 40)");

        // the camera starts on the hero and follows once he leaves the inner part of the 640 x 480 screen
        r = new Lba1Runtime(data);
        r.ChangeCube(13);
        Check(r.CameraX == (r.Hero.PosX + 256) / 512 && r.CameraZ == (r.Hero.PosZ + 256) / 512, "the camera starts on the hero's cell");
        r.Run(5);
        Check(r.CameraPinned && (r.CameraX, r.CameraZ) == (56, 52), $"a camera zone (scene 13's) pins the camera to its own cell ({r.CameraX},{r.CameraZ})");
        // in an open scene with no camera zones the camera follows: scene 1 (outside the citadel) has none near its start
        r = new Lba1Runtime(data);
        r.ChangeCube(0);
        var startCam = (r.CameraX, r.CameraZ);
        r.Run(5);
        Check(!r.CameraPinned || (r.CameraX, r.CameraZ) != startCam, "scene 0: the camera is on the hero, or pinned by a zone");

        // grid fragments: a grid zone changes the map while Twinsen stands in it, and puts it back when he leaves
        for (var s = 0; s < 120; s++)
        {
            var zones = data.Scene(s).Zones;
            var index = zones.FindIndex(z => z.Type == 3);
            if (index < 0) continue;
            var z3 = zones[index];
            r = new Lba1Runtime(data);
            r.ChangeCube(s);
            var changes = 0;
            r.GridChanged += () => changes++;
            var cellsBefore = (byte[])r.Cube.Cells.Clone();
            r.Place((z3.X0 + z3.X1) / 2, z3.Y0, (z3.Z0 + z3.Z1) / 2, 0);
            r.Run(4);
            var mixed = !r.Cube.Cells.AsSpan().SequenceEqual(cellsBefore);
            r.Place(0, 256, 0, 0);
            r.Run(4);
            var restored = r.Cube.Cells.AsSpan().SequenceEqual(cellsBefore);
            Check(mixed && restored, $"scene {s}: the grid fragment of zone {index} appears in the zone and goes when he leaves it (changed {mixed}, restored {restored}, {changes} redraws)");
            break;
        }
    }

    // A dialogue box stops the game's clock until it is closed, and a choice comes back through the CHOICE function.
    // Scene 11 (the harbour): a guard asks Twinsen who he is, three answers.
    private static void Dialogue(Lba1RuntimeData data)
    {
        var r = new Lba1Runtime(data) { AutoCloseDialogues = false };
        r.ChangeCube(11);
        for (var i = 0; i < 400 && r.Dialogue is null; i++) r.Frame();
        Check(r.Dialogue is { Choices.Count: 3 }, "scene 11 asks a question with three answers");
        if (r.Dialogue is not { } box) return;
        Check(box.Text.StartsWith("What are you doing there"), $"the question is read from the text bank ({box.Text})");
        var clock = r.TimerRef;
        for (var i = 0; i < 20; i++) r.Frame();
        Check(r.TimerRef == clock, "the clock stops while the box is open");
        r.CloseDialogue(1);
        Check(r.GameChoice == box.Choices[1].Id, "the chosen answer is what CHOICE reads");
        for (var i = 0; i < 20; i++) r.Frame();
        Check(r.TimerRef > clock, "the game goes on once the box is closed");
    }

    private static void Math_()
    {
        // directions: 0 = +z, 256 = +x, 512 = -z, 768 = -x
        Check(Lba1Trig.GetAngle(0, 0, 0, 1000) == 0, "GetAngle: +z is 0");
        Check(Lba1Trig.GetAngle(0, 0, 1000, 0) == 256, "GetAngle: +x is 256");
        Check(Lba1Trig.GetAngle(0, 0, 0, -1000) == 512, "GetAngle: -z is 512");
        Check(Lba1Trig.GetAngle(0, 0, -1000, 0) == 768, "GetAngle: -x is 768");
        Check(Lba1Trig.Distance == 1000, "GetAngle sets Distance");
        var diag = Lba1Trig.GetAngle(0, 0, 1000, 1000);
        Check(Math.Abs(diag - 128) <= 1, $"GetAngle: the +x+z diagonal is 128 (got {diag})");
        Check(Lba1Trig.GetAngle(5, 5, 5, 5) == 0 && Lba1Trig.Distance == 0, "GetAngle: the same point is angle 0, distance 0");

        // GetAngle and Rotate agree: rotating a vector of known length by an angle and asking for its direction gives the angle back
        var worst = 0;
        for (var a = 0; a < 1024; a += 7)
        {
            var (x, z) = Lba1Trig.Rotate(0, 3000, a);
            var back = Lba1Trig.GetAngle(0, 0, x, z);
            var error = Math.Min((back - a) & 1023, (a - back) & 1023);
            worst = Math.Max(worst, error);
        }
        Check(worst <= 2, $"GetAngle inverts Rotate to within 2 units (worst {worst})");

        Check(Lba1Trig.Rotate(0, 200, 0) == (0, 200), "Rotate: angle 0 leaves a vector alone");
        var quarter = Lba1Trig.Rotate(0, 200, 256);
        Check(quarter == (200, 0), $"Rotate: a quarter turn takes +z to +x (got {quarter})");
        Check(Lba1Trig.Sqr(0) == 0 && Lba1Trig.Sqr(1) == 1 && Lba1Trig.Sqr(3) == 1 && Lba1Trig.Sqr(4) == 2 && Lba1Trig.Sqr(1000000) == 1000, "Sqr is the floor square root");
        Check(Lba1Trig.Distance2D(0, 0, 3, 4) == 5 && Lba1Trig.Distance3D(0, 0, 0, 2, 3, 6) == 7, "Distance2D / Distance3D");
        Check(Lba1Trig.BoundRegleTrois(0, 256, 512, 256) == 128 && Lba1Trig.BoundRegleTrois(0, 256, 512, -5) == 0 && Lba1Trig.BoundRegleTrois(0, 256, 512, 999) == 256, "BoundRegleTrois interpolates and clamps");

        // a turn takes |angle| * speed / 256 ticks and goes the short way round
        var turn = new Lba1RealValue();
        turn.InitAngleConst(10, 266, 40, timerRef: 1000);
        Check(turn.Time == 40, $"a quarter turn at speed 40 takes 40 ticks (got {turn.Time})");
        Check(turn.GetAngle(1000) == 10 && turn.GetAngle(1020) == 138 && turn.GetAngle(1040) == 266 && turn.Time == 0, "the angle moves evenly over the turn and then stays");
        var wrap = new Lba1RealValue();
        wrap.InitAngleConst(1000, 30, 40, 0);
        Check(wrap.GetAngle(wrap.Time / 2) is >= 1000 or <= 30, "a turn from 1000 to 30 goes through 0 (the short way)");
        var fall = new Lba1RealValue();
        fall.InitValue(0, -256, 5, 100);
        Check(fall.GetValue(102) == -102 && fall.GetValue(105) == -256, "a value interpolates from start to end");
    }

    // Every scene is started and run for a while: nothing may throw, run away or leave the hero underground.
    private static void Smoke(Lba1RuntimeData data)
    {
        int started = 0, cubeChanges = 0, actors = 0;
        long events = 0;
        for (var scene = 0; scene < 120; scene++)
        {
            Lba1Runtime runtime;
            try
            {
                runtime = new Lba1Runtime(data);
                runtime.ChangeCube(scene);
                runtime.Run(300);
            }
            catch (Exception e)
            {
                Check(false, $"scene {scene}: the simulation threw {e.GetType().Name}: {e.Message.Split('\n')[0]}\n{e.StackTrace?.Split('\n').FirstOrDefault()}");
                continue;
            }
            started++;
            actors += runtime.NbObjets;
            events += runtime.Events.Count;
            if (runtime.CubeHistory.Count > 1) cubeChanges++;
            var hero = runtime.Hero;
            Check(hero.PosY >= 0 && hero.PosX >= 0 && hero.PosZ >= 0 && hero.PosX <= 63 * 512 && hero.PosZ <= 63 * 512, $"scene {scene}: the hero is inside the map after 300 frames ({hero.PosX}, {hero.PosY}, {hero.PosZ})");
            Check(hero.Body != -1 || scene is 0, $"scene {scene}: the hero has a body");
        }
        Console.WriteLine($"smoke: {started} scenes run for 300 frames each, {actors} actors, {events} script events, {cubeChanges} scenes changed scene on their own");
    }

    private static void Walk(Lba1RuntimeData data)
    {
        // Twinsen's house, scene 0: he starts on the ground and stays there when left alone
        var rt = new Lba1Runtime(data);
        rt.ChangeCube(0);
        rt.Run(50);
        var y0 = rt.Hero.PosY;
        Check((rt.Hero.WorkFlags & Lba1Const.Falling) == 0 && rt.Hero.GenAnim == Lba1Const.GenAnimRien, "scene 0: left alone Twinsen stands (animation 'nothing')");
        rt.Run(100);
        Check(rt.Hero.PosY == y0, "scene 0: he doesn't sink or float while standing");

        // walking forward moves him along his facing, at the animation's pace
        var startX = rt.Hero.PosX; var startZ = rt.Hero.PosZ;
        rt.Place(startX, y0, startZ, 256);       // facing +x
        rt.Joy = Lba1Const.JUp;
        rt.Run(60);
        Check(rt.Hero.GenAnim == Lba1Const.GenAnimMarche, "holding up plays the walking animation");
        var moved = rt.Hero.PosX - startX;
        Console.WriteLine($"  walking 60 frames ({60 * rt.TicksPerFrame / 50.0:F1} s): moved {moved} units along +x, z drift {rt.Hero.PosZ - startZ}");
        Check(moved > 500, "walking forward covers ground");

        // turning: holding left changes his facing
        rt.Joy = Lba1Const.JLeft;
        var before = rt.Hero.Beta;
        rt.Run(20);
        Check(rt.Hero.Beta != before, "holding left turns him");
        rt.Joy = 0;
        rt.Run(60);
        Check(rt.Hero.GenAnim == Lba1Const.GenAnimRien, "released, he goes back to standing");
    }

    // A bonus zone of the game's own (scene 13's zone 2, words 0, 48, 1, 0: money or a heart): pressing action in it lets a bonus pop out. This is how the
    // engine reads a giver zone (which bonuses from the second word, how many from the third, taken from the fourth), and the lamp's key zone is written to match.
    private static void RetailBonusZone(Lba1RuntimeData data)
    {
        var zone = data.Scene(13).Zones.Where(z => z.Type == 4 && z.Info[1] is 48).First();
        var rt = new Lba1Runtime(data);
        rt.ChangeCube(13);
        rt.Run(30);
        rt.Place((zone.X0 + zone.X1) / 2, zone.Y0, (zone.Z0 + zone.Z1) / 2, 0);
        rt.Fire = Lba1Const.FSpace;
        rt.Run(4);
        rt.Fire = 0;
        rt.Run(10);
        Check(rt.Extras.Any(e => e.Sprite is 3 or 4), $"a retail giver zone (words {string.Join(",", zone.Info)}) lets a coin or a heart pop out when action is pressed in it");
    }

    // Where Twinsen stands to ask a mushroom (actors 4..15): 500 away on the side that is furthest from the other mushrooms (one asks the first in its list within reach).
    private static (int X, int Z) Beside(LBAAssembler.Scenes.SceneModel room, int mushroom)
    {
        var m = room.Actors[mushroom];
        (int X, int Z) best = (m.X - 500, m.Z);
        var bestGap = -1.0;
        foreach (var (dx, dz) in new[] { (-500, 0), (500, 0), (0, -500), (0, 500), (-360, -360), (360, -360), (-360, 360), (360, 360) })
        {
            var (x, z) = (m.X + dx, m.Z + dz);
            var gap = Enumerable.Range(4, 12).Where(i => i != mushroom).Min(i => Math.Sqrt(Math.Pow(room.Actors[i].X - x, 2) + Math.Pow(room.Actors[i].Z - z, 2)));
            if (gap > bestGap) { bestGap = gap; best = (x, z); }
        }
        return best;
    }

    // The bedroom's extras (Lba1SecretRoomExtras) in the simulation, on a temp copy of the game folder with the surprise changes applied: the penguin walks
    // and is taken on touch, each reward mushroom of the two smiley faces (clovers, the heart nose, the bottle nose) pops its reward out when action is pressed
    // beside it and then suicides, and a clover-box mushroom shows its box and suicides; the box gives a clover box once (its game flag stays set), and a
    // mushroom whose box has been given is gone on the next visit.
    private static void SecretRoom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lba1_room_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var file in Directory.GetFiles(Lba1Dir, "*.HQR")) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            foreach (var name in new[] { "SCENE.HQR", "LBA_GRI.HQR", "BODY.HQR", "FILE3D.HQR", "TEXT.HQR" })
                if (File.Exists(Path.Combine(Lba1Dir, name + ".bak"))) File.Copy(Path.Combine(Lba1Dir, name + ".bak"), Path.Combine(dir, name), true);
            SceneHistory.Clear();
            Lba1SurpriseChanges.Apply(dir);
            SceneHistory.Clear();
            var data = new Lba1RuntimeData(dir);
            var room = data.Scene(61);
            var penguinAt = 3;
            Check(room.Actors.Count == 25 && !room.Actors[penguinAt].IsSprite && room.Actors[penguinAt].Entity == 9, "secret room: the scene has its penguin, twelve mushrooms, five boxes and four coins");

            // the pink elf greets Twinsen once when he first comes near, and again when he presses action close to it
            var elfAt = 2;
            var elfActor = room.Actors[elfAt];
            var greet = new Lba1Runtime(data) { AutoCloseDialogues = false };
            greet.ChangeCube(61);
            greet.Place(elfActor.X + 2700, 768, elfActor.Z, 768);
            greet.Run(20);
            Check(greet.Dialogue is null && greet.FlagGame[Lba1PinkElf.GreetingFlag] == 0, $"secret room: the elf says nothing while Twinsen is far from it (2700 units away)");
            greet.Place(elfActor.X + 2200, 768, elfActor.Z, 768);
            greet.Run(30);
            Check(greet.Dialogue is { } hello && hello.TextId == Lba1PinkElf.GreetingId && hello.Speaker == elfAt && hello.Colour == 14 && hello.Text == Lba1PinkElf.GreetingEnglish && greet.FlagGame[Lba1PinkElf.GreetingFlag] == 1,
                $"secret room: the elf greets Twinsen when he comes within 2500 units, in pink, with the greeting text ({greet.Dialogue?.Text}; hero {greet.Hero.PosX},{greet.Hero.PosY},{greet.Hero.PosZ}; flag {greet.FlagGame[Lba1PinkElf.GreetingFlag]})");
            greet.CloseDialogue();
            greet.Run(60);
            Check(greet.Dialogue is null, "secret room: and does not greet him again by itself");
            greet.Place(elfActor.X + 2000, 768, elfActor.Z, 768);
            greet.Fire = Lba1Const.FSpace;
            greet.Run(6);
            greet.Fire = 0;
            greet.Run(6);
            Check(greet.Dialogue is null, "secret room: pressing action 2000 units from the elf says nothing");
            greet.Place(elfActor.X + 1100, 768, elfActor.Z, 768);
            greet.Fire = Lba1Const.FSpace;
            greet.Run(6);
            Check(greet.Dialogue is { TextId: Lba1PinkElf.GreetingId }, "secret room: pressing action within 1500 units of the elf makes it say the greeting again");
            greet.CloseDialogue();
            greet.Run(30);
            Check(greet.Dialogue is null, "secret room: holding action down does not repeat it");
            greet.Fire = 0;
            greet.Run(4);
            greet.Fire = Lba1Const.FSpace;
            greet.Run(6);
            Check(greet.Dialogue is { TextId: Lba1PinkElf.GreetingId }, "secret room: letting go of action and pressing it again does");
            greet.Fire = 0;


            // the penguin walks up and down the room, along x 56
            var rt = new Lba1Runtime(data);
            rt.ChangeCube(61);
            rt.Run(20);
            var penguin = rt.Objects[penguinAt];
            var start = (penguin.PosX, penguin.PosZ);
            rt.Place(rt.Hero.PosX, rt.Hero.PosY, rt.Hero.PosZ, rt.Hero.Beta);
            rt.Run(240);
            Check((penguin.PosX, penguin.PosZ) != start && penguin.PosX == 56 * 512 && penguin.PosZ >= 48 * 512 && penguin.PosZ <= 61 * 512, $"secret room: the penguin walks about the room ({start} -> ({penguin.PosX}, {penguin.PosZ}))");

            // taken on touch: game flag 14 is set and the penguin is gone
            Check(rt.FlagGame[14] == 0, "secret room: Twinsen has no penguin at first");
            rt.Place(penguin.PosX, penguin.PosY, penguin.PosZ, 0);
            rt.Run(60);
            Check(rt.FlagGame[14] == 1, "secret room: touching the penguin takes it (game flag 14)");

            // a reward mushroom: action beside it lets its reward pop out, and the mushroom is gone (dead, no body) - for this visit only
            (string What, int Sprite, int Worth, int Mushroom)[] rewards = { ("clover", 7, 1, 4), ("clover", 7, 1, 8), ("heart", 4, 50, 9), ("bottle", 5, 80, 15) };
            foreach (var (what, sprite, worth, mushroom) in rewards)
            {
                var r = new Lba1Runtime(data);
                r.ChangeCube(61);
                r.Run(30);
                r.MagicLevel = 1;      // (with no magic yet the engine turns a magic bottle into a heart)
                var m = data.Scene(61).Actors[mushroom];
                var mushObj = r.Objects[mushroom];
                Check(!mushObj.IsDead && mushObj.Body != -1, $"secret room: the {what} mushroom (actor {mushroom}) is standing at first");
                var (hx, hz) = Beside(room, mushroom); r.Place(hx, 768, hz, 768);
                r.Fire = Lba1Const.FSpace;
                r.Run(4);
                r.Fire = 0;
                r.Run(8); Console.WriteLine($"    diag m{mushroom} spot ({hx},{hz}) mushroom ({m.X},{m.Z}) hero now ({r.Hero.PosX},{r.Hero.PosZ}) dead {r.Objects[mushroom].IsDead} extras {string.Join(",", r.Extras.Where(e => e.Sprite != -1).Select(e => e.Sprite))}");
                var extra = r.Extras.FirstOrDefault(e => e.Sprite == sprite);
                Check(extra is not null && extra.Divers == worth, $"secret room: pressing action beside the {what} mushroom (actor {mushroom}) lets a {what} worth {worth} pop out ({extra?.Sprite}, {extra?.Divers})");
                Check(mushObj.IsDead && mushObj.Body == -1, $"secret room: the {what} mushroom (actor {mushroom}) suicides after giving its bonus");
                var others = Enumerable.Range(4, 12).Where(i => i != mushroom).Count(i => r.Objects[i].IsDead);
                Check(others == 0, $"secret room: only that mushroom is gone ({others} others are dead: {string.Join(",", Enumerable.Range(4, 12).Where(i => i != mushroom && r.Objects[i].IsDead))})");
                r.Place(hx, 768, hz, 768);
                var before = r.Extras.Count(e => e.Sprite != -1);
                r.Fire = Lba1Const.FSpace;
                r.Run(4);
                r.Fire = 0;
                r.Run(8);
                Check(r.Extras.Count(e => e.Sprite != -1) <= before, $"secret room: a second action beside the gone {what} mushroom gives nothing more");
                r.ChangeCube(61);
                r.Run(10);
                Check(!r.Objects[mushroom].IsDead, $"secret room: the {what} mushroom is back on the next visit");
            }

            // the eyes are coins: sprite actors that stay (a popped-out coin is taken away after 20 seconds); touching one pops a 50-kash coin out towards Twinsen and the sprite is used up
            foreach (var coinAt in new[] { 21, 24 })
            {
                var c = new Lba1Runtime(data);
                c.ChangeCube(61);
                c.Run(30);
                var spot = data.Scene(61).Actors[coinAt];
                var coinObj = c.Objects[coinAt];
                Check(!coinObj.IsDead && coinObj.Sprite == 3 && (coinObj.Flags & Lba1Const.Invisible) == 0, $"secret room: coin {coinAt} is there at first (the kash sprite; box x {coinObj.XMin}..{coinObj.XMax}, y {coinObj.YMin}..{coinObj.YMax}, z {coinObj.ZMin}..{coinObj.ZMax}; hero box x {c.Hero.XMin}..{c.Hero.XMax})");
                // a coin dropped by a monster is gone after 20 seconds; these are not: 40 seconds on (2000 ticks) it is still there
                var until = c.TimerRef + 2000;
                var g = 0;
                while (c.TimerRef < until && g++ < 5000) c.Frame();
                Check(!coinObj.IsDead && (coinObj.Flags & Lba1Const.Invisible) == 0, $"secret room: coin {coinAt} is still there after 40 seconds");
                c.NbGoldPieces = 0;
                c.Place(spot.X - 300, 768, spot.Z, 768);
                c.Run(6);
                var popped = c.Extras.FirstOrDefault(e => e.Sprite == 3);
                Check(coinObj.IsDead && (popped is not null && popped.Divers == 50 || c.NbGoldPieces == 50), $"secret room: touching coin {coinAt} pops a coin worth 50 out ({popped?.Divers}, kash {c.NbGoldPieces})");
                c.Run(200);
                Check(c.NbGoldPieces == 50, $"secret room: Twinsen picks the popped-out coin up: 50 kashes ({c.NbGoldPieces})");
                Check(Enumerable.Range(21, 4).Count(i => c.Objects[i].IsDead) == 1, "secret room: only that coin is used up");
                c.ChangeCube(61);
                c.Run(10);
                Check(!c.Objects[coinAt].IsDead, $"secret room: coin {coinAt} is back on the next visit");
            }

            // a clover-box mushroom: nothing at first, then its box appears beside it when action is pressed (and the mushroom suicides), and touching the box gives a clover box, once
            var run = new Lba1Runtime(data);
            run.ChangeCube(61);
            run.Run(30);
            var boxActor = 16;      // (the first clover box: after the penguin (3), the twelve mushrooms (4..15))
            var mushAt = 10;        // the first clover-box mushroom (the right face's mouth, at 58, 53)
            var mush = data.Scene(61).Actors[mushAt];
            var box = run.Objects[boxActor];
            Check((box.Flags & Lba1Const.Invisible) != 0 && !run.Objects[mushAt].IsDead, "secret room: a clover box is hidden until its mushroom is asked");
            var boxes = run.NbCloverBox;
            var (rx, rz) = Beside(room, mushAt); run.Place(rx, 768, rz, 768);
            run.Fire = Lba1Const.FSpace;
            run.Run(4);
            run.Fire = 0;
            run.Run(10);
            Check((box.Flags & Lba1Const.Invisible) == 0, "secret room: pressing action beside a clover-box mushroom shows its box");
            Check(run.Objects[mushAt].IsDead && !run.Extras.Any(e => e.Sprite is 3 or 4 or 5 or 7), "secret room: the clover-box mushroom suicides and pops nothing out itself (" + string.Join(",", run.Extras.Where(e => e.Sprite != -1).Select(e => e.Sprite)) + "; dead " + string.Join(",", Enumerable.Range(4, 12).Where(i => run.Objects[i].IsDead)) + ")");
            run.Place(box.PosX, box.PosY, box.PosZ, 0);
            run.Run(40);
            Check(run.NbCloverBox == boxes + 1 && run.FlagGame[221] == 1, $"secret room: touching the box gives a clover box ({boxes} -> {run.NbCloverBox}) and sets its game flag (221)");

            // and never again: the flag is set, so on the next visit the mushroom and the box are both gone
            var again = new Lba1Runtime(data);
            again.ChangeCube(61);
            again.FlagGame[221] = 1;
            again.ChangeCube(61);
            again.Run(30);
            Check(again.Objects[mushAt].IsDead, "secret room: a clover-box mushroom whose box has been given is gone on the next visit");
            Check(!again.Objects[mushAt + 1].IsDead, "secret room: the other clover-box mushrooms are still there");
            var (ax, az) = Beside(room, mushAt); again.Place(ax, 768, az, 768);
            again.Fire = Lba1Const.FSpace;
            again.Run(4);
            again.Fire = 0;
            again.Run(30);
            var b2 = again.NbCloverBox;
            var box2 = again.Objects[boxActor];
            again.Place(box2.PosX, box2.PosY, box2.PosZ, 0);
            again.Run(40);
            Check(again.NbCloverBox == b2, "secret room: a clover box already given is not given again");

            // every line of the doorway: Twinsen walks in from the street and stays in the room (a clover box once stood where he arrives on one line, pushed him into the
            // doorway's zone and sent him straight back out, over and over)
            foreach (var z in new[] { 6144, 6400, 6656, 6912, 7168 })
            {
                var w = new Lba1Runtime(data);
                w.ChangeCube(13);
                w.NbLittleKeys = 1;
                w.Run(5);
                w.Place(54 * 512, 256, z, 768);
                w.Joy = Lba1Const.JUp;
                var scenes = 0;
                var lastCube = w.NumCube;
                for (var f = 0; f < 700; f++)
                {
                    w.Frame();
                    if (w.NumCube != lastCube) { scenes++; lastCube = w.NumCube; }
                }
                Check(w.NumCube == 61 && scenes == 1, $"secret room: walking in along z {z / 512.0:0.0} of the doorway ends in the room after one scene change, not thrown out again (scene {w.NumCube}, {scenes} changes)");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
    // The bedroom door's key lock (Lba1DoorLock) in the runtime, on a temp copy of the game folder with the surprise changes applied: without a key the
    // door stays shut, one little key opens it and sets game flag 220, and afterwards it opens with no key at all (the flag outlives the scene).
    private static void DoorLock()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lba1_lock_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var file in Directory.GetFiles(Lba1Dir, "*.HQR")) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            foreach (var name in new[] { "SCENE.HQR", "LBA_GRI.HQR", "BODY.HQR", "FILE3D.HQR", "TEXT.HQR" })
                if (File.Exists(Path.Combine(Lba1Dir, name + ".bak"))) File.Copy(Path.Combine(Lba1Dir, name + ".bak"), Path.Combine(dir, name), true);
            SceneHistory.Clear();
            Lba1SurpriseChanges.Apply(dir);
            SceneHistory.Clear();
            var data = new Lba1RuntimeData(dir);

            var rt = new Lba1Runtime(data);
            rt.ChangeCube(13);
            var door = rt.Objects[28];
            rt.Run(60);
            var shut = (door.PosX, door.PosZ);
            Check(rt.FlagGame[Lba1DoorLock.Flag] == 0, "door lock: the flag starts clear");

            // no key: pushing against the door for a long time does nothing
            rt.Place(54 * 512, 256, 13 * 512, 768);
            rt.Joy = Lba1Const.JUp;
            var slid = false;
            for (var f = 0; f < 300; f++)
            {
                rt.Frame();
                if ((door.PosX, door.PosZ) != shut) slid = true;
            }
            Check(!slid && rt.NumCube == 13 && rt.FlagGame[Lba1DoorLock.Flag] == 0, "door lock: without a key the door stays shut and the flag stays clear");
            rt.Joy = 0;

            // one key: it is spent, the flag is set and the door opens
            rt.NbLittleKeys = 1;
            rt.Place(54 * 512, 256, 13 * 512, 768);
            rt.Joy = Lba1Const.JUp;
            for (var f = 0; f < 400 && rt.NumCube == 13; f++)
            {
                rt.Frame();
                if ((door.PosX, door.PosZ) != shut) slid = true;
            }
            Check(slid && rt.NumCube == 61, $"door lock: with a key the door slides open and Twinsen walks into the bedroom (scene {rt.NumCube})");
            Check(rt.FlagGame[Lba1DoorLock.Flag] == 1, "door lock: opening it sets the flag (\"Door unlocked\")");
            rt.Joy = 0;

            // the second time, with no key (keys are lost on entering a scene anyway): the door opens for nothing
            rt.ChangeCube(13);
            Check(rt.NbLittleKeys == 0 && rt.FlagGame[Lba1DoorLock.Flag] == 1, "door lock: the flag survives the scene change and Twinsen has no key");
            door = rt.Objects[28];
            rt.Run(60);
            shut = (door.PosX, door.PosZ);
            slid = false;
            rt.Place(54 * 512, 256, 13 * 512, 768);
            rt.Joy = Lba1Const.JUp;
            for (var f = 0; f < 400 && rt.NumCube == 13; f++)
            {
                rt.Frame();
                if ((door.PosX, door.PosZ) != shut) slid = true;
            }
            Check(slid && rt.NumCube == 61 && rt.NbLittleKeys == 0, "door lock: once unlocked it opens without a key, and no key is spent");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }


    // The chapter-6 boat trips of the three fishermen (Lba1Fishermen), on a copy of the game files that the surprise changes have been applied to:
    // the menu each one offers, and that every offer really carries Twinsen to the island it names (the dialogue box is answered, the boat is
    // boarded, the trip runs and the landing point sits in the cube-change zone of the other island).
    private static void Fishermen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lba1_fish_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var file in Directory.GetFiles(Lba1Dir, "*.HQR")) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
            foreach (var name in new[] { "SCENE.HQR", "LBA_GRI.HQR", "BODY.HQR", "FILE3D.HQR", "TEXT.HQR" })
                if (File.Exists(Path.Combine(Lba1Dir, name + ".bak"))) File.Copy(Path.Combine(Lba1Dir, name + ".bak"), Path.Combine(dir, name), true);
            SceneHistory.Clear();
            Lba1SurpriseChanges.Apply(dir);
            SceneHistory.Clear();
            var data = new Lba1RuntimeData(dir);

            // scene, fisherman, boat, where the fisherman's own part ends (his track label), chapter, and (choice text id -> scene reached)
            var trips = new (string Where, int Scene, int Fisherman, int Boat, int DoneLabel, (int X, int Z, int Beta) Stand, (int Choice, int Reaches, string Name)[] Offers)[]
            {
                ("Port Belooga", 24, 1, 4, 112, (23232, 16384, 256), new[] { (118, 6, "Citadel Island"), (47, 39, "White Leaf Desert"), (79, 42, "Proxima Island") }),
                ("the military camp", 39, 2, 1, 100, (9368, 3928, 768), new[] { (10, 6, "Citadel Island"), (6, 24, "Principal Island"), (17, 42, "Proxima Island") }),
                ("Proxima City", 42, 10, 9, 100, (4096, 21728, 768), new[] { (6, 6, "Citadel Island"), (5, 24, "Principal Island"), (62, 39, "White Leaf Desert") }),
            };
            foreach (var trip in trips)
            {
                var offered = Ride(data, trip.Scene, 6, trip.Fisherman, trip.Boat, trip.DoneLabel, trip.Stand, trip.Offers[0].Choice, out _, out _, out _);
                var expected = trip.Offers.Select(o => o.Choice).ToList();
                Check(expected.All(offered.Contains), $"{trip.Where}, chapter 6: he offers {string.Join(", ", trip.Offers.Select(o => o.Name))} (choices {string.Join(",", offered)})");
                foreach (var offer in trip.Offers)
                {
                    Ride(data, trip.Scene, 6, trip.Fisherman, trip.Boat, trip.DoneLabel, trip.Stand, offer.Choice, out _, out var reached, out var control);
                    Check(reached == offer.Reaches, $"{trip.Where}, chapter 6: the trip to {offer.Name} arrives in scene {offer.Reaches} (got {reached})");
                    Check(control, $"{trip.Where}, chapter 6: after the trip to {offer.Name} Twinsen is visible and can walk (no softlock at the landing)");
                }
            }

            // chapter 5 is as the game had it: Port Belooga offers the desert only (and nothing at all before the Astronomer has been asked)
            var five = Ride(data, 24, 5, 1, 4, 112, (23232, 16384, 256), 47, out _, out _, out _);

            // the check itself: the first version's landing from Proxima (a point in the desert's east-edge zone, which waits for the chapter-6 boat) is the softlock
            var store = new SceneStore(SceneGame.Lba1, dir);
            var landing = SceneScripts.Load(store.LoadRecord(42), 42, null, lba1: true);
            landing.SetText(0, ScriptKind.Life, landing.GetText(0, ScriptKind.Life).Replace("holomap_traj(31);\n            change_cube(39);", "holomap_traj(31);\n            pos_point(30);"));
            store.SaveMany(new[] { new SceneChange(42, SceneSerializer.Parse(SceneGame.Lba1, landing.Build().Record!)) }, description: "test: the first version's landing");
            SceneHistory.Clear();
            Ride(new Lba1RuntimeData(dir), 42, 6, 10, 9, 100, (4096, 21728, 768), 62, out _, out var oldReached, out var oldControl);
            Check(oldReached == 39 && !oldControl, $"Proxima City, the first version's landing: the trip reaches the desert but Twinsen is stuck there (scene {oldReached}, control {oldControl}): the check sees the softlock");
            Check(!five.Contains(118) && !five.Contains(79), $"Port Belooga, chapter 5: no Citadel or Proxima offer (choices {string.Join(",", five)})");
        }
        finally
        {
            if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // Talks to a fisherman in the scene in the chapter, takes the choice when it is offered, then boards his boat; returns the choices offered.
    private static List<int> Ride(Lba1RuntimeData data, int scene, int chapter, int fisherman, int boat, int doneLabel, (int X, int Z, int Beta) stand, int choice, out List<string> events, out int reached, out bool control)
    {
        var offered = new List<int>();
        var rt = new Lba1Runtime(data)
        {
            // a headless run answers questions at once: with the offered choice it is asked for (the last, "not now", otherwise)
            ChoicePolicy = ids => { offered = ids.ToList(); return ids.Contains(choice) ? choice : ids[^1]; },
        };
        rt.ChangeCube(scene);
        rt.Chapter = chapter;
        rt.NbGoldPieces = 100;
        rt.Run(scene == 24 ? 20 : 900);   // (the other two scenes open with the boat coming in: Twinsen gets control when it has landed)
        var f = rt.Objects[fisherman];
        rt.Place(stand.X, f.PosY, stand.Z, stand.Beta);
        rt.Fire = Lba1Const.FSpace;
        rt.Frame();
        rt.Fire = 0;
        var boarding = false;
        for (var frame = 0; frame < 6000 && rt.NumCube == scene; frame++)
        {
            // (Twinsen stood on Proxima's pier where the fisherman sits: he steps out of the way of the walk to the boat, which is what the player would do too)
            if (scene == 42 && frame == 5 && rt.NbGoldPieces < 100) rt.Place(4300, rt.Hero.PosY, 20700, 768);
            var aboard = boarding && (rt.Hero.Flags & Lba1Const.Invisible) != 0;   // (the script hides him once he is in the boat)
            if (aboard) rt.Joy = 0;
            else if (rt.NbGoldPieces < 100 && (boarding || f.LabelTrack == doneLabel))
            {
                // his part is over: Twinsen walks straight to the boat (in scene 24 from the dock next to it), turning to it now and then
                var b = rt.Objects[boat];
                if (!boarding && scene == 24) rt.Place(b.PosX, b.PosY, b.PosZ + 900, 512);
                else if (frame % 10 == 0) rt.Place(rt.Hero.PosX, rt.Hero.PosY, rt.Hero.PosZ, Lba1Trig.GetAngle(rt.Hero.PosX, rt.Hero.PosZ, b.PosX, b.PosZ));
                rt.Joy = Lba1Const.JUp;
                boarding = true;
            }
            rt.Frame();
        }
        rt.Joy = 0;
        events = rt.Events.TakeLast(6).ToList();
        reached = rt.NumCube;
        control = reached != scene && HasControl(rt);
        return offered;
    }

    // After a trip: lets the arrival play out (the boat sails in, the camera returns), then Twinsen must be visible and walk when the stick is pushed
    // (any of the four headings: the landing may put him against a wall). A landing that waits for something that never comes leaves him hidden and frozen.
    private static bool HasControl(Lba1Runtime rt)
    {
        rt.Run(1500);
        if (Environment.GetEnvironmentVariable("LBA_DEBUG") == "1") Console.WriteLine($"    arrival: scene {rt.NumCube} hero ({rt.Hero.PosX},{rt.Hero.PosY},{rt.Hero.PosZ}) flags {rt.Hero.Flags:X} move {rt.Hero.Move} comportement {rt.Comportement} cam ({rt.CameraX},{rt.CameraZ}) labels {string.Join(",", rt.Objects.Take(8).Select(o => o.LabelTrack))} history {string.Join(">", rt.CubeHistory)} events {string.Join(" / ", rt.Events.TakeLast(3))}");
        if ((rt.Hero.Flags & Lba1Const.Invisible) != 0) return false;
        foreach (var beta in new[] { 0, 256, 512, 768 })
        {
            var scene = rt.NumCube;
            var (x, z) = (rt.Hero.PosX, rt.Hero.PosZ);
            rt.Place(x, rt.Hero.PosY, z, beta);
            rt.Joy = Lba1Const.JUp;
            rt.Run(30);
            rt.Joy = 0;
            var moved = System.Math.Abs(rt.Hero.PosX - x) + System.Math.Abs(rt.Hero.PosZ - z);
            if (moved > 200 || rt.NumCube != scene) return true;
        }
        return false;
    }

    private static void Doors(Lba1RuntimeData data)
    {
        // the retail door of house 58 (Lupin Burg, scene 13, actor 8): walk into it from the street and the scene changes
        var rt = new Lba1Runtime(data);
        rt.ChangeCube(13);
        var door = rt.Objects[8];
        Check((door.Flags & Lba1Const.Sprite3D) != 0 && (door.Flags & Lba1Const.SpriteClip) != 0, "scene 13 actor 8 is a sprite door");
        rt.Run(60);
        var closedAt = (door.PosX, door.PosZ);
        Check(door.SRot == 0 && (door.PosX, door.PosZ) == (door.AnimStepX, door.AnimStepZ), "the door is closed while Twinsen is far away");

        // west along the street towards the arch at x = 55 (cell), z = 41..43: start at x = 57, z = 42, facing west (768)
        rt.Place(57 * 512, 256, 42 * 512, 768);
        rt.Joy = Lba1Const.JUp;
        var opened = false;
        for (var f = 0; f < 200 && rt.NumCube == 13; f++)
        {
            rt.Frame();
            if (door.SRot > 0 || (door.PosX, door.PosZ) != closedAt) opened = true;
        }
        Check(opened, "walking into the door makes it slide open");
        Console.WriteLine($"  scenes visited: {string.Join(" -> ", rt.CubeHistory)}; events: {string.Join(" | ", rt.Events.TakeLast(4))}");
        Check(rt.NumCube == 58, "walking through the door of house 58 changes to scene 58 (the house with the TV)");

        // the door built into Lupin Burg by the door tool (needs the tool to have been applied to these game files)
        var modded = data.Scene(13).Actors.Count > 28 && data.Scene(61).Zones.Any(z => z.Type == 0 && z.Info[0] == 13);
        if (!modded) { Console.WriteLine("  (the bedroom door isn't in these game files; skipping)"); return; }

        rt = new Lba1Runtime(data);
        rt.ChangeCube(13);
        var newDoor = rt.Objects[28];
        Check((newDoor.Flags & Lba1Const.SpriteClip) != 0 && newDoor.Sprite == 11, "scene 13 actor 28 is the new sliding door");
        rt.Run(60);
        Check(newDoor.SRot == 0 && (newDoor.PosX, newDoor.PosZ) == (newDoor.AnimStepX, newDoor.AnimStepZ), "the new door starts closed");
        var doorClosed = (newDoor.PosX, newDoor.PosZ);

        // arch at x = 51 (cell), z = 12..14: approach from the street, facing west (with a key, in case the door has been locked: see DoorLock)
        rt.NbLittleKeys = 1;
        rt.Place(54 * 512, 256, 13 * 512, 768);
        rt.Joy = Lba1Const.JUp;
        var slid = false;
        for (var f = 0; f < 400 && rt.NumCube == 13; f++)
        {
            rt.Frame();
            if ((newDoor.PosX, newDoor.PosZ) != doorClosed) slid = true;
        }
        Check(slid, "the new door slides open when Twinsen walks into it");
        Console.WriteLine($"  scenes visited: {string.Join(" -> ", rt.CubeHistory)}; hero now at ({rt.Hero.PosX}, {rt.Hero.PosY}, {rt.Hero.PosZ}); events: {string.Join(" | ", rt.Events.TakeLast(3))}");
        Check(rt.NumCube == 61, "Twinsen walks through the new door into the bedroom (scene 61)");

        // in the bedroom he arrives at the doorway; walking out (east) leads back to the street
        var arrival = (rt.Hero.PosX, rt.Hero.PosY, rt.Hero.PosZ);
        Check(rt.Hero.PosX < 32000 && rt.Hero.PosY >= 768, $"he arrives inside the room, before its doorway zone ({arrival})");
        rt.Place(rt.Hero.PosX, rt.Hero.PosY, rt.Hero.PosZ, 256);   // face +x, towards the room's doorway
        rt.Joy = Lba1Const.JUp;
        for (var f = 0; f < 400 && rt.NumCube == 61; f++) rt.Frame();
        Console.WriteLine($"  scenes visited: {string.Join(" -> ", rt.CubeHistory)}; hero now at ({rt.Hero.PosX}, {rt.Hero.PosY}, {rt.Hero.PosZ})");
        Check(rt.NumCube == 13, "walking out of the bedroom returns to Lupin Burg");
        Check(rt.Hero.PosX >= 51 * 512 - 256 && rt.Hero.PosX <= 54 * 512, "he comes out at the arch, not somewhere else in the town");

        // the lamp post's key: pressing the action key inside its zone lets a key pop out, and Twinsen has one more when he has picked it up
        if (data.Scene(13).Zones.Any(z => z.Type == 4 && z.Info[1] == 128))
        {
            rt = new Lba1Runtime(data);
            rt.ChangeCube(13);
            rt.Run(30);
            var keysBefore = rt.NbLittleKeys;
            rt.Place(768, 2048, 32000, 256);
            rt.Fire = Lba1Const.FSpace;
            rt.Run(4);
            rt.Fire = 0;
            rt.Run(60);
            // the key has flown out of the lamp and landed on the cobbles, not off the platform; Twinsen walks over to it
            var key = rt.Extras.FirstOrDefault(e => e.Sprite == 6);
            Check(key is not null && key.PosX > 256 && key.PosZ < 32256 && key.PosY >= 2048, $"the key lands on the platform's cobbles ({key?.PosX},{key?.PosY},{key?.PosZ})");
            if (key is not null) rt.Place(key.PosX, 2048, key.PosZ, 0);
            rt.Run(60);
            Check(rt.NbLittleKeys == keysBefore + 1, $"the lamp post gives Twinsen a key when he presses action beside it ({keysBefore} -> {rt.NbLittleKeys})");
            Check(data.Scene(13).Zones.Count(z => z.Type == 4 && z.Info[1] == 128) == 1, "scene 13 has exactly one key-only bonus zone (the lamp's)");
        }
        // the pink elf of the surprise changes, when they have been applied to these game files
        var roomActors = data.Scene(61).Actors;
        var pinkElfIndex = Enumerable.Range(1, roomActors.Count - 1).FirstOrDefault(i => !roomActors[i].IsSprite && roomActors[i].Entity == 49 && roomActors[i].Body == 42);
        if (pinkElfIndex == 0) { Console.WriteLine("  (the pink elf isn't in these game files; skipping)"); return; }
        var pinkElf = roomActors[pinkElfIndex];
        rt = new Lba1Runtime(data);
        rt.ChangeCube(61);
        var elf = rt.Objects[pinkElfIndex];
        Check(elf.GenBody == 42 && elf.Body != -1 && elf.Anim != -1, $"the pink elf gets a body (body id 42 -> {elf.Body}) and an animation ({elf.Anim}) from the Elf entity");
        var facing = elf.Beta;
        rt.Run(120);
        Check(elf.Beta != facing, $"the pink elf turns to face Twinsen when he is near (facing {facing} -> {elf.Beta})");
        Check(elf.PosY == pinkElf.Y, "the pink elf stands on the floor where it was put");
    }
}
