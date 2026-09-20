using LBA2LevelEditor;
using LBA2LevelEditor.Lba1.Runtime;

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
        if (what is "doors" or "all") Doors(data);
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

        // arch at x = 51 (cell), z = 12..14: approach from the street, facing west
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
    }
}
