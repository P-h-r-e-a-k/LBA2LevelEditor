using LBAAssembler;
using LBAAssembler.Assets;
using LBAAssembler.Grids;
using LBAAssembler.Lba1;
using LBAAssembler.Lba1.Runtime;
using LBAAssembler.LbaScript;
using LBAAssembler.Scenes;

namespace ScriptRoundTrip;

// SceneStore, SceneHistory, SceneDocument, ActorPrefabs and the door tool, all on temporary copies of the retail files.
internal static class StoreTests
{
    private static readonly string Lba1Dir = Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure";
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";
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
        if (what is "store1" or "all") Store1();
        if (what is "store2" or "all") Store2();
        if (what is "session" or "all") SessionSave();
        if (what is "document" or "all") Document();
        if (what is "prefab" or "all") Prefabs();
        if (what is "doormod" or "all") DoorMod();
        if (what is "surprise" or "all") Surprise();
        if (what is "ops" or "all") Ops();
        if (what is "blank" or "all") Blank();
        if (what is "historylimits" or "all") HistoryLimits();
        if (what is "hqrentryundo" or "all") HqrEntryUndo();
        if (what is "lba2newactor" or "all") Lba2NewActor();
        Console.WriteLine(failures == 0 ? $"store tests: all {checks} checks passed" : $"store tests: {failures} of {checks} checks FAILED");
        return failures == 0 ? 0 : 1;
    }

    // Adding and deleting actors, zones and track points, on every retail scene of both games.
    private static void Ops()
    {
        foreach (var (game, dir, count) in new[] { (SceneGame.Lba1, Lba1Dir, 120), (SceneGame.Lba2, Lba2Dir, 222) })
        {
            var store = new SceneStore(game, dir);
            int deletions = 0, refused = 0, roundTrips = 0, bad = 0, ambiguous = 0;
            for (var s = 0; s < count; s++)
            {
                var record = store.LoadRecord(s);
                var scene = SceneSerializer.Parse(game, record);
                // add an actor and delete it again: the record must come out exactly as it went in
                var probe = SceneSerializer.Parse(game, record);
                var index = SceneOps.AddActor(probe, SceneOps.BlankActor(game, 512, 256, 512));
                try
                {
                    SceneOps.DeleteActor(probe, index);
                    if (SceneSerializer.Write(probe).AsSpan().SequenceEqual(SceneSerializer.Write(scene))) roundTrips++; else { bad++; Console.WriteLine($"  {game} scene {s}: add+delete changed the record"); }
                }
                catch (SceneEditException)
                {
                    // a script compares with the number the new actor got (retail scripts do that with numbers that name no actor)
                    ambiguous++;
                }

                // delete a middle actor with references re-pointed to the hero: no script may refer past the new end
                if (scene.Actors.Count > 3)
                {
                    var victim = scene.Actors.Count / 2;
                    var before = scene.Actors.Count;
                    try { SceneOps.DeleteActor(scene, victim); deletions++; }
                    catch (SceneEditException) { refused++; SceneOps.DeleteActor(scene, victim, retarget: 0); deletions++; }
                    Check(scene.Actors.Count == before - 1, $"{game} scene {s}: an actor is gone");
                    var issues = SceneValidator.Validate(scene, store.ValidationOptions()).Where(i => i.Severity == SceneIssueSeverity.Error).ToList();
                    if (issues.Count > 0) { bad++; Console.WriteLine($"  {game} scene {s}: {issues[0]}"); }
                    var stale = SceneOps.ReferencesTo(scene, LBAAssembler.LbaScript.ArgRole.Obj, before - 1);
                    Check(stale.Count == 0, $"{game} scene {s}: nothing refers to the old last actor number after the shift");
                }
            }
            Check(bad == 0, $"{game}: {roundTrips} add+delete round trips exact ({ambiguous} skipped), {deletions} deletions ({refused} needed a retarget) all valid");
            Console.WriteLine($"  {game}: {roundTrips} exact round trips, {ambiguous} ambiguous, {deletions} deletions, {refused} needed a retarget");
        }

        // a door's own number is a value in its script (`28 == col_obj(0)`): it must follow the door when an earlier actor goes
        {
            var store = new SceneStore(SceneGame.Lba1, Lba1Dir);
            var scene = store.Load(13);
            var index = ActorPrefabs.Place(scene, ActorPrefabs.DoorEast, 51 * 512, 256, 12 * 512 - 256);
            SceneOps.DeleteActor(scene, 8, retarget: 0);
            using var _ = LBAAssembler.LbaScript.Opcodes.Use(LBAAssembler.LbaScript.Opcodes.Lba1);
            var life = LBAAssembler.LbaScript.Bytecode.DecodeLife(scene.Actors[index - 1].Life);
            var own = life.Where(i => i.Func == 1).Select(i => i.Value).ToList();
            Check(own.Count > 0 && own.All(v => v == index - 1), $"the door's own number follows it ({index} -> {index - 1}): values {string.Join(",", own)}");
            var withDoor = store.Load(13);
            var doorIndex = ActorPrefabs.Place(withDoor, ActorPrefabs.DoorEast, 51 * 512, 256, 12 * 512 - 256);
            var refs = SceneOps.ReferencesTo(withDoor, LBAAssembler.LbaScript.ArgRole.Obj, doorIndex);
            Check(refs.Count > 0 && refs.All(r => r.Actor == doorIndex), "the door's references to itself are found");
            var refused = false;
            try { SceneOps.DeleteActor(withDoor, 5); } catch (SceneEditException) { refused = true; }
            Check(withDoor.Actors.Count == doorIndex + 1 || !refused, "a refused deletion changes nothing");
        }

        // zones and track points
        {
            var store = new SceneStore(SceneGame.Lba1, Lba1Dir);
            var scene = store.Load(5);
            var zones = scene.Zones.Count;
            var z = SceneOps.AddZone(scene, new SceneZoneModel { X0 = 0, Y0 = 0, Z0 = 0, X1 = 512, Y1 = 512, Z1 = 512, Type = 2 });
            Check(z == zones && scene.Zones.Count == zones + 1, "a zone is added at the end");
            SceneOps.DeleteZone(scene, z);
            Check(scene.Zones.Count == zones, "and deleted");

            var withPoints = Enumerable.Range(0, 120).Select(store.Load).First(m => m.TrackPoints.Count >= 3 && SceneOps.ReferencesTo(m, LBAAssembler.LbaScript.ArgRole.Point, 2).Count > 0);
            var before = withPoints.TrackPoints.Count;
            var refused = false;
            try { SceneOps.DeleteTrackPoint(withPoints, 0); } catch (SceneEditException) { refused = true; }
            Check(refused || SceneOps.ReferencesTo(withPoints, LBAAssembler.LbaScript.ArgRole.Point, 0).Count == 0, "deleting a used track point is refused");
            SceneOps.DeleteTrackPoint(withPoints, 0, retarget: 1);
            Check(withPoints.TrackPoints.Count == before - 1 && SceneOps.ReferencesTo(withPoints, LBAAssembler.LbaScript.ArgRole.Point, before - 1).Count == 0, "track points after the deleted one are renumbered");
        }
    }

    // A blank scene in any slot: valid, saved, and playable (Twinsen stands on the floor and can walk on it).
    private static void Blank()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lba1_blank_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in new[] { "LBA_BLL.HQR", "LBA_BRK.HQR", "FILE3D.HQR", "BODY.HQR", "ANIM.HQR", "RESS.HQR" }) File.Copy(Path.Combine(Lba1Dir, f), Path.Combine(dir, f));
            File.Copy(Path.Combine(Lba1Dir, "SCENE.HQR.bak"), Path.Combine(dir, "SCENE.HQR"));
            File.Copy(Path.Combine(Lba1Dir, "LBA_GRI.HQR.bak"), Path.Combine(dir, "LBA_GRI.HQR"));
            var store = new SceneStore(SceneGame.Lba1, dir);

            var noFloor = new List<int>();
            for (var s = 0; s < 120; s++)
                if (Lba1BlankScene.PickFloor(store.LoadGrid(s), store.LoadLibrary(s)) is null) noFloor.Add(s);
            Console.WriteLine($"  {120 - noFloor.Count} of 120 slots have a floor block of their own" + (noFloor.Count > 0 ? $" (none in: {string.Join(", ", noFloor)})" : ""));
            Check(noFloor.Count < 30, "most slots have a plain floor block to build a blank scene from");

            var tested = 0;
            foreach (var slot in Enumerable.Range(0, 120).Where(s => !noFloor.Contains(s)).Where((_, i) => i % 9 == 0))
            {
                var (scene, grid) = Lba1BlankScene.Create(store, slot);
                var gridReport = Lba1GridValidator.Validate(grid, store.LoadLibrary(slot));
                Check(!gridReport.Issues.Any(i => i.Severity == SceneIssueSeverity.Error), $"blank scene {slot}: the grid passes the engine's rules ({gridReport.Issues.FirstOrDefault()})");
                Check(Lba1GridEdit.UsedBlocksListed(grid), $"blank scene {slot}: the used-blocks bitmap lists the floor block");
                store.Save(slot, scene, grid);
                tested++;

                var data = new LBAAssembler.Lba1.Runtime.Lba1RuntimeData(dir);
                var runtime = new LBAAssembler.Lba1.Runtime.Lba1Runtime(data);
                runtime.ChangeCube(slot);
                runtime.Run(100);
                Check(runtime.Hero.PosY == 256 && runtime.NumCube == slot, $"blank scene {slot}: Twinsen stands on the floor (y {runtime.Hero.PosY})");
                var z0 = runtime.Hero.PosZ;
                runtime.Joy = LBAAssembler.Lba1.Runtime.Lba1Const.JUp;
                runtime.Run(60);
                Check(Math.Abs(runtime.Hero.PosZ - z0) > 400 && runtime.Hero.PosY == 256, $"blank scene {slot}: Twinsen walks on it (moved {runtime.Hero.PosZ - z0})");
            }
            Console.WriteLine($"  {tested} blank scenes built, saved and walked on");
        }
        finally { Cleanup(dir); }
    }

    // A temp copy of the original LBA1 files (the .bak files hold the untouched originals).
    private static string TempLba1()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lba1_store_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(Lba1Dir, "SCENE.HQR.bak"), Path.Combine(dir, "SCENE.HQR"));
        File.Copy(Path.Combine(Lba1Dir, "LBA_GRI.HQR.bak"), Path.Combine(dir, "LBA_GRI.HQR"));
        File.Copy(Path.Combine(Lba1Dir, "LBA_BLL.HQR"), Path.Combine(dir, "LBA_BLL.HQR"));
        File.Copy(Path.Combine(Lba1Dir, "LBA_BRK.HQR"), Path.Combine(dir, "LBA_BRK.HQR"));
        return dir;
    }

    private static string TempLba2()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lba2_store_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(Lba2Dir, "SCENE.HQR"), Path.Combine(dir, "SCENE.HQR"));
        return dir;
    }

    private static void Cleanup(string dir)
    {
        // only ever remove the temp folders made above
        if (dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(dir).StartsWith("lba", StringComparison.Ordinal))
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static bool EntriesEqualExcept(string a, string b, params int[] except)
    {
        var x = HqrFile.Parse(File.ReadAllBytes(a)); var y = HqrFile.Parse(File.ReadAllBytes(b));
        if (x.Count != y.Count) return false;
        for (var i = 0; i < x.Count; i++)
        {
            if (except.Contains(i)) continue;
            if (x.IsEmpty(i) != y.IsEmpty(i)) return false;
            if (!x.IsEmpty(i) && !x.Read(i).AsSpan().SequenceEqual(y.Read(i))) return false;
        }
        return true;
    }

    private static void Store1()
    {
        SceneHistory.Clear();
        var dir = TempLba1();
        try
        {
            var original = File.ReadAllBytes(Path.Combine(dir, "SCENE.HQR"));
            var store = new SceneStore(SceneGame.Lba1, dir);
            Check(store.SceneCount == 120 && store.SceneExists(61) && !store.SceneExists(120), "LBA1 store: 120 scenes");

            var scene = store.Load(5);
            var beforeX = scene.Actors[1].X;
            scene.Actors[1].X += 512;
            var result = store.Save(5, scene, description: "Move actor 1 of scene 5");
            Check(!result.Issues.Any(i => i.Severity == SceneIssueSeverity.Error), "LBA1 store: a clean scene saves");
            Check(store.Load(5).Actors[1].X == beforeX + 512, "LBA1 store: the edit is on disk");
            Check(File.ReadAllBytes(Path.Combine(dir, "SCENE.HQR.bak")).AsSpan().SequenceEqual(original), "LBA1 store: .bak holds the original file");
            Check(EntriesEqualExcept(Path.Combine(dir, "SCENE.HQR.bak"), Path.Combine(dir, "SCENE.HQR"), 5), "LBA1 store: every other scene is unchanged");

            Check(SceneHistory.UndoDescription == "Move actor 1 of scene 5", "history: the save is on the log");
            SceneHistory.Undo();
            Check(store.LoadRecord(5).AsSpan().SequenceEqual(HqrFile.Parse(original).Read(5)), "history: undo puts the original record back exactly");
            SceneHistory.Redo();
            Check(store.Load(5).Actors[1].X == beforeX + 512, "history: redo re-applies it");
            SceneHistory.Undo();

            var tooMany = store.Load(5);
            while (tooMany.Actors.Count <= 100) tooMany.Actors.Add(tooMany.Actors[1].Clone());
            var refused = false;
            var before = File.ReadAllBytes(Path.Combine(dir, "SCENE.HQR"));
            try { store.Save(5, tooMany); } catch (SceneValidationException e) { refused = e.Issues.Any(i => i.Message.Contains("100")); }
            Check(refused && File.ReadAllBytes(Path.Combine(dir, "SCENE.HQR")).AsSpan().SequenceEqual(before), "LBA1 store: 101 actors are refused and nothing is written");

            var s13 = store.Load(13);
            var grid = store.LoadGrid(13);
            var edited = Lba1GridEdit.SetCells(grid, new[] { new Lba1GridCell(20, 0, 20, 22, 0) });
            s13.Actors[1].Y += 256;
            store.SaveMany(new[] { new SceneChange(13, s13, edited), new SceneChange(61, store.Load(61)) }, description: "test");
            Check(Lba1GridEdit.Get(store.LoadGrid(13), 20, 0, 20).Block == 22 && store.Load(13).Actors[1].Y == s13.Actors[1].Y, "LBA1 store: a scene and its grid are saved together");
            Check(EntriesEqualExcept(Path.Combine(dir, "LBA_GRI.HQR.bak"), Path.Combine(dir, "LBA_GRI.HQR"), 13), "LBA1 store: every other grid is unchanged");
            SceneHistory.Undo();
            Check(store.LoadGrid(13).AsSpan().SequenceEqual(HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "LBA_GRI.HQR.bak"))).Read(13)), "history: undo restores the grid too");
        }
        finally { Cleanup(dir); SceneHistory.Clear(); }
    }

    private static void Store2()
    {
        SceneHistory.Clear();
        var dir = TempLba2();
        try
        {
            var store = new SceneStore(SceneGame.Lba2, dir);
            var path = Path.Combine(dir, "SCENE.HQR");
            Check(store.SceneCount == 222, "LBA2 store: 222 scenes");
            int Largest() => BitConverter.ToInt32(HqrArchive.Open(path).Read(0), 0);
            var start = Largest();
            var biggestRecord = Enumerable.Range(1, 222).Max(i => HqrArchive.Open(path).Read(i).Length);
            Console.WriteLine($"  LBA2 entry 0 says {start}; the largest record is {biggestRecord}");
            Check(start >= biggestRecord, "LBA2 store: entry 0 covers the biggest record to begin with");

            var scene = store.Load(40);
            store.Save(40, scene);
            Check(store.LoadRecord(40).AsSpan().SequenceEqual(HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "SCENE.HQR.bak"))).Read(41)), "LBA2 store: an unedited scene saves as the same record");
            Check(Largest() == start, "LBA2 store: entry 0 is left alone when nothing outgrows it");

            // growing the hero's script pushes every later script (and its patch offsets) along
            var growth = 25000;
            scene = store.Load(3);
            var oldPatches = ReadPatches(scene.Tail);
            using (Opcodes.Use(Opcodes.Lba2))
            {
                var padding = new byte[growth + 1];
                Array.Fill(padding, (byte)1);   // NOP
                padding[^1] = 0;                // END
                var decoded = Bytecode.DecodeLife(padding);
                Check(decoded.Count == growth + 1, "the padding script decodes (NOPs then END)");
                scene.Actors[0].Life = scene.Actors[0].Life.Concat(padding).ToArray();
            }
            store.Save(3, scene);
            var grown = store.LoadRecord(3);
            Check(grown.Length > start, "the grown record is bigger than the old largest size");
            Check(Largest() >= grown.Length, "LBA2 store: entry 0 grew with it (the engine sizes its scene buffer from it)");
            var reread = store.Load(3);
            var newPatches = ReadPatches(reread.Tail);
            Check(newPatches.Count == oldPatches.Count, "patch count is unchanged by a longer hero script");
            // patches inside the hero's own scripts (before the added bytes) stay; every patch after them moves by the growth
            var oldRecord = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "SCENE.HQR.bak"))).Read(4);
            var hero = SceneRecord.Parse(oldRecord).Actors[0];
            var growthPoint = hero.LifePos + hero.LifeLen;
            var expected = oldPatches.Select(p => p.Offset >= growthPoint ? p.Offset + growth + 1 : p.Offset).ToList();
            Check(expected.SequenceEqual(newPatches.Select(p => p.Offset)) && oldPatches.Count(p => p.Offset >= growthPoint) > 50,
                $"patches after the hero's scripts moved by exactly the growth, those inside them stayed ({oldPatches.Count(p => p.Offset >= growthPoint)} moved of {oldPatches.Count})");
            Check(SceneSerializer.Write(reread).AsSpan().SequenceEqual(grown), "the saved record is canonical");
            Check(EntriesEqualExcept(Path.Combine(dir, "SCENE.HQR.bak"), path, 0, 4), "LBA2 store: other scenes untouched");
        }
        finally { Cleanup(dir); SceneHistory.Clear(); }
    }

    // The script editor's save path (ScriptSession.SaveAll) now goes through the store: a longer script must move the
    // patch table and land on the undo log.
    private static void SessionSave()
    {
        SceneHistory.Clear();
        var dir = TempLba2();
        try
        {
            var path = Path.Combine(dir, "SCENE.HQR");
            var oldRecord = HqrFile.Parse(File.ReadAllBytes(path)).Read(4);
            var oldPatches = ReadPatches(SceneSerializer.Parse(SceneGame.Lba2, oldRecord).Tail);

            var session = new ScriptSession(() => dir, Path.Combine(dir, "comments.json"), lba1: false);
            var scene = session.GetScene(3)!;
            var text = scene.GetText(0, ScriptKind.Life) + "\nvoid comportement_60()\n{\n    set_var_cube(3, 1);\n}\n";
            var (_, error) = scene.CheckText(0, ScriptKind.Life, text);
            if (error is not null) { Console.WriteLine($"  (session save test skipped: {error})"); return; }
            scene.SetText(0, ScriptKind.Life, text);
            var result = session.SaveAll();
            Check(result.Ok, "session: SaveAll succeeded: " + result.Message);

            var newRecord = HqrFile.Parse(File.ReadAllBytes(path)).Read(4);
            var oldHero = SceneRecord.Parse(oldRecord).Actors[0];
            var newHero = SceneRecord.Parse(newRecord).Actors[0];
            var delta = newHero.LifeLen - oldHero.LifeLen;
            Check(delta > 0 && newRecord.Length == oldRecord.Length + delta, $"session: the record grew by the script's growth ({delta} bytes)");
            var expected = oldPatches.Select(p => p.Offset >= oldHero.LifePos + oldHero.LifeLen ? p.Offset + delta : p.Offset).ToList();
            var actual = ReadPatches(SceneSerializer.Parse(SceneGame.Lba2, newRecord).Tail).Select(p => p.Offset).ToList();
            Check(expected.SequenceEqual(actual), "session: the patch table moved with the scripts (this used to be left stale)");
            Check(SceneHistory.UndoDescription == "Save scripts of scene 3", "session: the save is on the undo log");
            SceneHistory.Undo();
            Check(HqrFile.Parse(File.ReadAllBytes(path)).Read(4).AsSpan().SequenceEqual(oldRecord), "session: undo puts the old record back");
        }
        finally { Cleanup(dir); SceneHistory.Clear(); }
    }

    private static List<(int Size, int Offset)> ReadPatches(byte[] tail)
    {
        var n = BitConverter.ToInt32(tail, 0);
        return Enumerable.Range(0, n).Select(i => ((int)BitConverter.ToInt16(tail, 4 + i * 4), (int)BitConverter.ToInt16(tail, 4 + i * 4 + 2))).ToList();
    }

    private static void Document()
    {
        SceneHistory.Clear();
        var dir = TempLba1();
        try
        {
            var store = new SceneStore(SceneGame.Lba1, dir);
            var doc = SceneDocument.Open(store, 5, withGrid: true);
            var changes = 0;
            doc.Changed += (_, _) => changes++;
            var x0 = doc.Scene.Actors[1].X;
            Check(!doc.IsDirty && !doc.CanUndo && !doc.CanRedo, "document: starts clean");

            doc.Edit("Move actor", s => s.Actors[1].X = x0 + 100);
            Check(doc.IsDirty && doc.CanUndo && doc.UndoDescription == "Move actor" && changes == 1, "document: an edit makes it dirty and undoable");
            Check(store.Load(5).Actors[1].X == x0, "document: nothing is on disk until it is saved");
            doc.Undo();
            Check(!doc.CanUndo && doc.CanRedo && doc.Scene.Actors[1].X == x0, "document: undo");
            doc.Redo();
            Check(doc.Scene.Actors[1].X == x0 + 100, "document: redo");

            var threw = false;
            try { doc.Edit("Broken", s => { s.Actors[1].X = 999; throw new InvalidOperationException("no"); }); } catch (InvalidOperationException) { threw = true; }
            Check(threw && doc.Scene.Actors[1].X == x0 + 100, "document: an edit that throws leaves the scene as it was");

            doc.Edit("Drag", s => s.Actors[1].X = x0 + 1, "drag-1");
            doc.Edit("Drag", s => s.Actors[1].X = x0 + 2, "drag-1");
            doc.Edit("Drag", s => s.Actors[1].X = x0 + 3, "drag-1");
            doc.Undo();
            Check(doc.Scene.Actors[1].X == x0 + 100, "document: three edits with one merge key undo in one step");

            doc.EditBoth("Cell + actor", s => s.Actors[1].Y += 256, g => Lba1GridEdit.SetCells(g, new[] { new Lba1GridCell(1, 0, 1, 22, 0) }));
            Check(Lba1GridEdit.Get(doc.Grid!, 1, 0, 1).Block == 22, "document: EditBoth changes the grid");
            doc.Undo();
            Check(Lba1GridEdit.Get(doc.Grid!, 1, 0, 1).Block != 22, "document: undo restores the grid with the scene");
            doc.Redo();

            doc.Save();
            Check(!doc.IsDirty && store.Load(5).Actors[1].X == x0 + 100 && Lba1GridEdit.Get(store.LoadGrid(5), 1, 0, 1).Block == 22, "document: Save writes scene and grid, and clears the dirty flag");
            doc.Edit("Again", s => s.Actors[1].X = x0 + 7);
            Check(doc.IsDirty, "document: dirty again after another edit");
            doc.Revert();
            Check(!doc.IsDirty && !doc.CanUndo && doc.Scene.Actors[1].X == x0 + 100, "document: Revert re-reads the saved scene");

            doc.AutoSave = true;
            doc.Edit("Auto", s => s.Actors[1].X = x0 + 55);
            Check(store.Load(5).Actors[1].X == x0 + 55 && !doc.IsDirty, "document: AutoSave writes the edit at once");
            doc.Undo();
            Check(store.Load(5).Actors[1].X == x0 + 100, "document: AutoSave writes undo too");
            var refused = false;
            try { doc.Edit("Too many", s => { while (s.Actors.Count <= 100) s.Actors.Add(s.Actors[1].Clone()); }); } catch (SceneValidationException) { refused = true; }
            Check(refused && doc.Scene.Actors.Count < 100 && store.Load(5).Actors.Count < 100, "document: a refused auto-save rolls the edit back");

            doc.AutoSave = false;
            Check(doc.SaveAs(7, allowErrors: true) is not null, "document: SaveAs writes another slot");
            var noSuchSlot = false;
            try { doc.SaveAs(500); } catch (InvalidOperationException) { noSuchSlot = true; }
            Check(noSuchSlot, "document: SaveAs refuses a scene number the game doesn't have");
        }
        finally { Cleanup(dir); SceneHistory.Clear(); }
    }

    private static bool SameActor(SceneActorModel a, SceneActorModel b, bool scripts = true)
        => a.Flags == b.Flags && a.Entity == b.Entity && a.Body == b.Body && a.Anim == b.Anim && a.Sprite == b.Sprite
           && a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.HitForce == b.HitForce && a.OptionFlags == b.OptionFlags && a.Beta == b.Beta
           && a.SRot == b.SRot && a.Move == b.Move && a.Info.SequenceEqual(b.Info) && a.NbBonus == b.NbBonus && a.CoulObj == b.CoulObj
           && a.Armor == b.Armor && a.LifePoints == b.LifePoints && (!scripts || (a.Track.AsSpan().SequenceEqual(b.Track) && a.Life.AsSpan().SequenceEqual(b.Life)));

    private static void Prefabs()
    {
        var original = SceneSerializer.Parse(SceneGame.Lba1, HqrArchive.Open(Path.Combine(Lba1Dir, "SCENE.HQR.bak")).Read(13));

        // headers reproduce the retail doors they were measured from (the scripts differ from those doors' own, which have extras)
        var scene = original.Clone();
        var east = ActorPrefabs.DoorEast.Build(9, 18944, 256, 26880);
        Check(SameActor(east, original.Actors[9], scripts: false), "door-east built where retail actor 9 stands has actor 9's header, clip rectangle included");
        var south = ActorPrefabs.DoorSouth.Build(10, 5376, 1024, 28160);
        south.CoulObj = original.Actors[10].CoulObj;   // retail door 10 has dialogue colour 8, its neighbours 7: irrelevant for a door
        Check(SameActor(south, original.Actors[10], scripts: false), "door-south built where retail actor 10 stands has actor 10's header, clip rectangle included");

        using (Opcodes.Use(Opcodes.Lba1))
        {
            var door = ActorPrefabs.DoorEast.Build(28, 26112, 256, 5888);
            Check(Bytecode.DecodeLife(door.Life).Count > 0 && Bytecode.DecodeTrack(door.Track).Count > 0, "prefab scripts decode");
            var text = LifeText.Decompile(door.Life, 28, NoSymbols.Instance);
            Check(text.Contains("28 == col_obj(0)") && text.Contains("set_door_up(1550)") && text.Contains("3000 < distance(0)"), "prefab life script names its own actor number");
            var west = ActorPrefabs.DoorSouth.Build(3, 0, 256, 0);
            Check(LifeText.Decompile(west.Life, 3, NoSymbols.Instance).Contains("set_door_left(1550)"), "the south door slides left");
        }

        var placed = scene.Clone();
        var index = ActorPrefabs.Place(placed, ActorPrefabs.DoorEast, 26112, 256, 5888);
        Check(index == scene.Actors.Count && placed.Actors.Count == scene.Actors.Count + 1, "Place appends the actor and returns its number");
        Check(!SceneValidator.Validate(placed).Any(i => i.Severity == SceneIssueSeverity.Error), "a scene with the placed door validates");
        var wrongGame = false;
        try { ActorPrefabs.Place(SceneSerializer.Parse(SceneGame.Lba2, HqrArchive.Open(Path.Combine(Lba2Dir, "SCENE.HQR")).Read(4)), ActorPrefabs.DoorEast, 0, 0, 0); } catch (InvalidOperationException) { wrongGame = true; }
        Check(wrongGame, "a prefab for one game can't be placed in the other's scene");
        Check(ActorPrefabs.Find("lba1-door-east") == ActorPrefabs.DoorEast && ActorPrefabs.For(SceneGame.Lba2).Count == 0, "prefabs are looked up by id and per game");

        // the door the game files hold after the door tool ran (only checked if the tool has been applied there)
        try
        {
            var live = SceneSerializer.Parse(SceneGame.Lba1, HqrArchive.Open(Path.Combine(Lba1Dir, "SCENE.HQR")).Read(13));
            if (live.Actors.Count == original.Actors.Count + 1)
            {
                // (the tool makes its door's clip rectangle 28 pixels taller at the bottom than the retail one the prefab has)
                var built = ActorPrefabs.DoorEast.Build(live.Actors.Count - 1, 26112, 256, 5888);
                // (and the door tool's key lock changes its life script: the prefab's door with the lock put in is what the game files should hold then)
                if (Lba1DoorLock.IsLocked(live, 13, live.Actors.Count - 1))
                {
                    var scratch = SceneSerializer.Parse(SceneGame.Lba1, SceneSerializer.Write(original));
                    var at = ActorPrefabs.Place(scratch, ActorPrefabs.DoorEast, 26112, 256, 5888);
                    built = Lba1DoorLock.Apply(scratch, 13, at).Scene.Actors[at];
                }
                var lift = live.Actors[^1].Info[3] - built.Info[3];
                if (lift is 0 or 28) built.Info[3] += lift;
                Check(SameActor(live.Actors[^1], built), "the prefab reproduces the door actor already in the game files, scripts included (bar the taller clip rectangle)");
            }
        }
        catch (IOException) { }
    }

    private static void DoorMod()
    {
        SceneHistory.Clear();
        var dir = TempLba1();
        try
        {
            var pristine = new SceneStore(SceneGame.Lba1, dir);
            var sceneOriginal = File.ReadAllBytes(Path.Combine(dir, "SCENE.HQR"));

            var result = Lba1RoomDoorMod.Apply(dir);
            Check(result.Changed, "door tool: applies to the original files");
            Check(!pristine.Validate(13, pristine.Load(13), pristine.LoadGrid(13)).Any(i => i.Severity == SceneIssueSeverity.Error), "door tool: scene 13 and its grid validate");
            Check(!pristine.Validate(61, pristine.Load(61)).Any(i => i.Severity == SceneIssueSeverity.Error), "door tool: scene 61 validates");
            Check(EntriesEqualExcept(Path.Combine(dir, "SCENE.HQR.bak"), Path.Combine(dir, "SCENE.HQR"), 13, 61), "door tool: only scenes 13 and 61 changed");
            Check(EntriesEqualExcept(Path.Combine(dir, "LBA_GRI.HQR.bak"), Path.Combine(dir, "LBA_GRI.HQR"), 13, 61), "door tool: only grids 13 and 61 changed (the bedroom's floor hole is filled)");

            // the game files the earlier version of the tool produced (and the game was tested with) hold the same entries
            // (scene 61 is left out: the game folder's copy also holds the pink elf, see Surprise)
            var live = Path.Combine(Lba1Dir, "SCENE.HQR");
            // (only once the game folder has the current version of the arch and door; the fishermen's scenes are the surprise changes' business too)
            var liveGrid = HqrFile.Parse(File.ReadAllBytes(Path.Combine(Lba1Dir, "LBA_GRI.HQR"))).Read(13);
            bool LiveDoorIsLocked() { try { return Lba1DoorLock.IsLocked(new SceneStore(SceneGame.Lba1, Lba1Dir).Load(13), 13, 28); } catch (Exception e) when (e is ArgumentException or InvalidDataException or IndexOutOfRangeException) { return false; } }
            if (HqrFile.Parse(File.ReadAllBytes(live)).Read(13).Length != HqrFile.Parse(sceneOriginal).Read(13).Length && Lba1RoomDoorMod.ArchIsComplete(liveGrid) && LiveDoorIsLocked())
            {
                Check(EntriesEqualExcept(live, Path.Combine(dir, "SCENE.HQR"), 24, 39, 42, 61), "door tool: scenes match the ones already in the game folder, entry for entry");
                // (grid 13 by its cells: a grid completed in place keeps the columns it wrote before, so its bytes lay out differently from a fresh run's)
                Check(EntriesEqualExcept(Path.Combine(Lba1Dir, "LBA_GRI.HQR"), Path.Combine(dir, "LBA_GRI.HQR"), 13), "door tool: the other grids match the ones already in the game folder, entry for entry");
                Check(Lba1GridCodec.Decode(liveGrid).AsSpan().SequenceEqual(Lba1GridCodec.Decode(HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "LBA_GRI.HQR"))).Read(13))), "door tool: grid 13's cells match the game folder's");
            }
            else Console.WriteLine("  (the game folder has an earlier version of the door or none; not compared)");

            Check(!Lba1RoomDoorMod.Apply(dir).Changed, "door tool: a second run changes nothing");

            Check(SceneHistory.UndoDescription == Lba1RoomDoorMod.HistoryName, "door tool: it is one step on the undo log");
            SceneHistory.Undo();
            Check(EntriesEqualExcept(Path.Combine(dir, "SCENE.HQR.bak"), Path.Combine(dir, "SCENE.HQR")) && EntriesEqualExcept(Path.Combine(dir, "LBA_GRI.HQR.bak"), Path.Combine(dir, "LBA_GRI.HQR")), "door tool: undo puts scenes 13, 61 and grid 13 back");
            SceneHistory.Redo();
            Check(pristine.Load(13).Actors.Count == 29, "door tool: redo brings the door back");
        }
        finally { Cleanup(dir); SceneHistory.Clear(); }
    }

    // The untouched copy of a game file: the .bak the editor keeps on the first change, else the file itself.
    private static string PristineFile(string name)
    {
        var live = Path.Combine(Lba1Dir, name);
        return File.Exists(live + ".bak") ? live + ".bak" : live;
    }

    // Tools > LBA1: Make surprise changes: the door plus the pink elf (Lba1PinkElf, Lba1SurpriseChanges), on temp copies.
    private static void Surprise()
    {
        SceneHistory.Clear();
        var dir = TempLba1();
        try
        {
            foreach (var name in new[] { "BODY.HQR", "FILE3D.HQR", "TEXT.HQR" }) File.Copy(PristineFile(name), Path.Combine(dir, name));
            var retailBodies = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR")));
            var retailEntities = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "FILE3D.HQR")));
            var bodyBytes = File.ReadAllBytes(Path.Combine(dir, "BODY.HQR"));
            var store = new SceneStore(SceneGame.Lba1, dir);
            Check(retailBodies.Count == 132 && retailEntities.Count == 82, "surprise: the source BODY.HQR / FILE3D.HQR are the retail ones (132 bodies, 82 entities)");

            // the body: Raymond with only the outfit polygons' colour bytes changed
            var raymond = retailBodies.Read(Lba1PinkElf.SourceBody);
            var pink = Lba1PinkElf.Recolour(raymond);
            var changedBytes = Enumerable.Range(0, raymond.Length).Where(i => raymond[i] != pink[i]).ToList();
            Check(pink.Length == raymond.Length && changedBytes.Count == 45 && changedBytes.All(i => raymond[i] is 64 or 160 && pink[i] == 224), "pink elf: exactly the 45 outfit polygons' colour bytes change (64 and 160 -> 224)");
            var refused = false;
            try { Lba1PinkElf.Recolour(retailBodies.Read(87)); } catch (InvalidDataException) { refused = true; }
            Check(refused, "pink elf: a body that isn't Raymond (Joe) is refused as the source");

            var result = Lba1SurpriseChanges.Apply(dir);
            Check(result.Changed, "surprise: applies to the original files");
            Console.WriteLine("  " + result.Message.Replace("\n", "\n  "));

            // BODY.HQR: one new entry, everything else as it was
            var bodies = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR")));
            var mushroomBody = Lba1SecretRoomExtras.BuildBody(retailBodies.Read(Lba1SecretRoomExtras.DonorBody));
            Check(bodies.Count == 134 && bodies.Read(132).AsSpan().SequenceEqual(pink) && bodies.Read(133).AsSpan().SequenceEqual(mushroomBody), "surprise: BODY.HQR gained entry 132, the pink elf, and 133, the mushroom");
            Check(Enumerable.Range(0, 132).All(i => bodies.Read(i).AsSpan().SequenceEqual(retailBodies.Read(i))), "surprise: the 132 retail bodies are unchanged");
            Check(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR.bak")).AsSpan().SequenceEqual(bodyBytes), "surprise: BODY.HQR.bak holds the original file");
            Check(bodies.ToBytes().Length == HqrFile.Parse(bodyBytes).ToBytes().Length + 2 * (4 + 10) + pink.Length + mushroomBody.Length, "surprise: the file grew by two table slots and two stored entries");

            // BODY.HQD: a new sidecar naming both new bodies (line N+1 describes entry N; line 0 is the header),
            // seeded from the retail BODY1.HQD's own descriptions for entries 0..131
            var hqdLines = File.ReadAllLines(Path.Combine(dir, "BODY.HQD"), System.Text.Encoding.Latin1);
            var retailHqdLines = HqdDescriptions.LoadLines("BODY1.HQD");
            Check(hqdLines.Length == 135, "hqd: BODY.HQD has one line per BODY.HQR entry (134), plus the header");
            Check(hqdLines[133].Contains("Pink elf") && hqdLines[134].Contains("Mushroom"), "hqd: the two new bodies (entries 132 and 133) are named in BODY.HQD");
            Check(retailHqdLines.Count > 100 && Enumerable.Range(1, 132).All(i => hqdLines[i] == retailHqdLines[i]), "hqd: the 132 retail bodies (entries 0..131) keep the names the embedded BODY1.HQD already gives them");

            // FILE3D.HQR: the Elf entity has one more record, BODY id 42 -> entry 132, and nothing else changed
            var entities = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "FILE3D.HQR")));
            Check(EntriesEqualExcept(Path.Combine(dir, "FILE3D.HQR.bak"), Path.Combine(dir, "FILE3D.HQR"), Lba1PinkElf.Entity, Lba1SecretRoomExtras.MushroomEntity), "surprise: only the Elf entity and the ID card entity (the mushroom's) changed in FILE3D.HQR");
            var before = retailEntities.Read(Lba1PinkElf.Entity); var after = entities.Read(Lba1PinkElf.Entity);
            var record = new byte[] { 1, Lba1PinkElf.BodyId, 4, 132, 0, 0 };
            Check(after.Length == before.Length + 6 && after.AsSpan(12, 6).SequenceEqual(record) && after.AsSpan(0, 12).SequenceEqual(before.AsSpan(0, 12)) && after.AsSpan(18).SequenceEqual(before.AsSpan(12)),
                "surprise: the Elf entity gained the record 01 2A 04 84 00 00 after its two bodies, animations untouched");

            // scene 61: the elf is the last actor, in pink text, and the scene validates
            var room = store.Load(61);
            var elf = room.Actors[2];
            Check(room.Actors.Count == 3 + 22 && elf.Entity == 49 && elf.Body == 42 && elf.Anim == 0 && !elf.IsSprite && elf.CoulObj == 14, "surprise: scene 61 has the pink elf as actor 2 (entity 49, body 42, animation 0, colour 14)");
            Check(elf.Life.Length > 0 && elf.Track.Length > 0, "surprise: the elf has its scripts");
            var elfText = SceneScripts.Load(store.LoadRecord(61), 61, null, lba1: true).GetText(2, ScriptKind.Life);
            Check(elfText.Contains($"message({Lba1PinkElf.GreetingId})") && elfText.Split($"message({Lba1PinkElf.GreetingId})").Length == 3 && elfText.Contains($"var_game({Lba1PinkElf.GreetingFlag})") && elfText.Contains("if (1 == action())") && elfText.Contains("1500 > distance(0)") && elfText.Contains("2500 > distance(0)"),
                "surprise: the elf greets Twinsen (message 287) once when he first comes near (game flag 226) and again when he presses action within 1500 units, one press one greeting");

            // the room's other extras: a meca penguin, twelve mushrooms as two smiley faces, five clover boxes and the hero's script; the rewards come from the
            // actors' own scripts (no bonus zones of ours), and the floor's hole is filled
            var mushrooms = room.Actors.Where(a => !a.IsSprite && a.Entity == Lba1SecretRoomExtras.MushroomEntity).ToList();
            var penguin = room.Actors.Skip(3).First(a => !a.IsSprite && a.Entity == 9);
            var boxes = room.Actors.Where(a => a.IsSprite && a.Sprite == 41).ToList();
            var coins = room.Actors.Where(a => a.IsSprite && a.Sprite == 3).ToList();
            var penguinAt = room.Actors.IndexOf(penguin);
            Check(mushrooms.Count == 12 && mushrooms.Select(m => m.Body).Distinct().Count() == 1 && boxes.Count == 5 && coins.Count == 4 && penguinAt == 3, "surprise: scene 61 has a meca penguin (actor 3), twelve mushrooms, five clover boxes and four coins");
            (int X, int Z) Cell(SceneActorModel a) => (a.X / 512, a.Z / 512);
            // on the picture: the left face (larger z) has its eyes at (58, 59) and (60, 57), its nose at (60, 59) and a mouth of five along the lower edge of the 5 x 5 square,
            // (58, 61), (60, 61), (62, 61), (62, 59), (62, 57), ends up: two eyes level with each other, the nose between and under them, a smile under that; the right face
            // is the same shifted 8 cells in z
            static bool Same(IEnumerable<(int X, int Z)> got, params (int X, int Z)[] wanted) => got.SequenceEqual(wanted);
            Check(Same(mushrooms.Take(5).Select(Cell), (58, 61), (60, 61), (62, 61), (62, 59), (62, 57)) && Cell(mushrooms[5]) == (60, 59)
                  && Same(mushrooms.Skip(6).Take(5).Select(Cell), (58, 53), (60, 53), (62, 53), (62, 51), (62, 49)) && Cell(mushrooms[11]) == (60, 51)
                  && Same(coins.Select(Cell), (58, 59), (60, 57), (58, 51), (60, 49)),
                "surprise: two smiley faces, five by five cells, either side of the door lane: coins for the eyes, a mushroom for the nose and five for the mouth");
            var things = mushrooms.Concat(coins).Concat(boxes).ToList();
            Check(things.All(t => t.Y == 768) && things.All(t => t.Z <= 53 * 512 || t.Z >= 57 * 512), "surprise: everything stands on the floor and keeps 512 units or more from the door lane's rows (z 54..56), where Twinsen comes in");
            Check(mushrooms.Take(5).All(m => m.OptionFlags == 256 && m.NbBonus == 1) && mushrooms[5].OptionFlags == 32 && mushrooms[5].NbBonus == 50 && mushrooms[11].OptionFlags == 64 && mushrooms[11].NbBonus == 80 && mushrooms.Skip(6).Take(5).All(m => m.OptionFlags == 0),
                "surprise: the left face gives clovers with a heart worth 50 for its nose, the right face has the magic bottle worth 80 for its nose and clover boxes for its mouth");
            Check(coins.All(c => c.OptionFlags == 16 && c.NbBonus == 50 && (c.Flags & 0x400) != 0), "surprise: each eye is a kash coin (the game's own sprite 3, a sprite actor that stays) worth 50");
            Check(!room.Zones.Any(z => z.Type == 4 && z.Y0 == 768 && z.Y1 == 768 + 640) && room.Zones.Count(z => z.Type == 4) == 5, "surprise: the rewards come from the actors' scripts: scene 61 has only the game's own five bonus zones");
            var roomScripts = SceneScripts.Load(store.LoadRecord(61), 61, null, lba1: true);
            var heroText = roomScripts.GetText(0, ScriptKind.Life);
            Check(heroText.Contains($"var_game({Lba1SecretRoomExtras.PenguinFlag})") && heroText.Contains($"{penguinAt} == col()") && heroText.Contains("found_object(14)") && heroText.Split("set_var_cube(").Length == 63 && heroText.Contains("if (1 == action())") && heroText.Contains("if (0 == var_cube(12))") && heroText.Contains("set_var_cube(12, 0)") && new[] { 500, 550, 600, 650, 700 }.All(r => heroText.Split($"{r} > distance(").Length == 13),
                "surprise: the hero's script takes the penguin on touch (flag 14, found_object) and asks the nearest mushroom (within 700) once per press");
            var clover = roomScripts.GetText(4, ScriptKind.Life);
            var boxMushroom = roomScripts.GetText(10, ScriptKind.Life);
            Check(clover.Contains("give_bonus(1)") && clover.Contains("suicide()") && clover.Contains("var_cube(0)") && boxMushroom.Contains("var_game(221)") && boxMushroom.Contains("suicide()") && !boxMushroom.Contains("give_bonus"),
                "surprise: a bonus mushroom pops its reward out and suicides; a box mushroom suicides when its box has been given (flag 221) or shown");
            var coinAt = room.Actors.IndexOf(coins[0]);
            var coinText = roomScripts.GetText(coinAt, ScriptKind.Life);
            Check(coinAt == 21 && coinText.Contains($"{coinAt} == col_obj(0)") && coinText.Contains("give_bonus(1)") && coinText.Contains("suicide()") && !coinText.Contains("var_game"), "surprise: touching a coin (actor 21..24) pops its 50 kash out and the coin is used up; no game flag: it is back next visit");
            var boxText = roomScripts.GetText(room.Actors.IndexOf(boxes[0]), ScriptKind.Life);
            var boxMushrooms = mushrooms.Skip(6).Take(5).ToList();
            Check(boxText.Contains("inc_clover_box()") && boxText.Contains("set_var_game(221, 1)") && boxes.Select(b => b.Z).SequenceEqual(boxMushrooms.Select(m => m.Z)) && boxes.Select(b => b.X).SequenceEqual(boxMushrooms.Select(m => m.X)),
                "surprise: each clover box gives a clover box once (its own game flag, 221 to 225) where its mushroom stood");
            Check(room.TrackPoints.Count >= 2 && penguin.X == 56 * 512 && !roomScripts.GetText(penguinAt, ScriptKind.Track).Contains("goto_point(-1)"), "surprise: the penguin walks between two track points of the room, along x 56 (clear of the elf and the faces)");
            Check(!Lba1SecretRoomExtras.FloorHasHole(store.LoadGrid(61)) && Lba1SecretRoomExtras.FloorHasHole(HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "LBA_GRI.HQR.bak"))).Read(61)), "surprise: the hole in the bedroom's floor (x 59, z 62) is filled; the game's own grid has it");
            var flagUse = new List<string>();
            {
                var retail = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "SCENE.HQR.bak")));
                for (var s = 0; s < 120; s++)
                {
                    if (retail.IsEmpty(s)) continue;
                    var scripts = SceneScripts.Load(retail.Read(s), s, null, lba1: true);
                    var model = SceneSerializer.Parse(SceneGame.Lba1, retail.Read(s));
                    for (var a = 0; a < model.Actors.Count; a++)
                    {
                        var text = scripts.GetText(a, ScriptKind.Life);
                        foreach (var n in Enumerable.Range(220, 35)) if (text.Contains($"var_game({n})") || text.Contains($"var_game({n},")) flagUse.Add($"scene {s} actor {a} flag {n}");
                    }
                }
            }
            Check(flagUse.Count == 0, "surprise: none of the game's own scripts reads or writes game flags 220..254 (the door's 220 and the boxes' 221..225 are free)" + (flagUse.Count > 0 ? ": " + flagUse[0] : ""));
            Check(!store.Validate(61, room).Any(i => i.Severity == SceneIssueSeverity.Error) && !store.Validate(13, store.Load(13), store.LoadGrid(13)).Any(i => i.Severity == SceneIssueSeverity.Error), "surprise: scenes 61 and 13 validate");
            Check(EntriesEqualExcept(Path.Combine(dir, "SCENE.HQR.bak"), Path.Combine(dir, "SCENE.HQR"), 13, 24, 39, 42, 61), "surprise: only scenes 13, 24, 39, 42 and 61 changed");
            Check(EntriesEqualExcept(Path.Combine(dir, "LBA_GRI.HQR.bak"), Path.Combine(dir, "LBA_GRI.HQR"), 13, 61), "surprise: only grids 13 and 61 changed");

            // the door: the doorway copied whole (pillars, floor, side walls), lined with grey stone, its floor carried to the far pillar, and a taller clip rectangle
            var doorGrid = store.LoadGrid(13);
            Check(Lba1RoomDoorMod.ArchIsComplete(doorGrid) && Lba1GridEdit.UsedBlocksListed(doorGrid), "door: the arch is complete and every block it uses is listed in the grid's used-blocks table");
            Check(Lba1GridEdit.Get(doorGrid, 46, 3, 13).Block == 2 && Lba1GridEdit.Get(doorGrid, 49, 5, 11).Block == 2 && Lba1GridEdit.Get(doorGrid, 50, 0, 15).Block == 232, "door: grey stone (block 2) behind and beside the recess, floor under the far pillar (232)");
            Check(Enumerable.Range(1, 9).All(y => Lba1GridEdit.Get(doorGrid, 51, y, 11) == new Lba1GridCell(51, y, 11, 150, y - 1) && Lba1GridEdit.Get(doorGrid, 51, y, 15) == new Lba1GridCell(51, y, 15, 149, y - 1)), "door: both legs of the arch are the smooth arch stones (blocks 150 and 149) from the floor up, as the bricked arch and the game's other entrances have them");
            var brickFile = HqrArchive.Open(store.BrickPath);
            var report = Lba1GridValidator.Validate(doorGrid, store.LoadLibrary(13), i => brickFile.IsValid(i) ? brickFile.Read(i).Length : 0);
            Check(report.BrickBytes <= Lba1GridValidator.MaxBrickBytes, $"door: grid 13 still fits the engine's brick memory ({report.BrickBytes} of {Lba1GridValidator.MaxBrickBytes} bytes)");
            var doorActor = store.Load(13).Actors.Single(a => a.IsSprite && a.Sprite == 11 && a.X == 51 * 512 && a.Z == 12 * 512 - 256);
            var retailClip = ActorPrefabs.DoorEast.Build(0, doorActor.X, doorActor.Y, doorActor.Z).Info;
            Check(doorActor.Info[0] == retailClip[0] && doorActor.Info[1] == retailClip[1] && doorActor.Info[2] == retailClip[2] && doorActor.Info[3] == retailClip[3] + 28, "door: the clip rectangle is the retail one, 28 pixels taller at the bottom (the whole 140-pixel sprite)");
            // the lamp post at the west corner: Lupin Burg's own lamp block (117, a column of ten cells) on the curb cap, and the key zone around it
            Check(Lba1LampPost.Cells().All(c => Lba1GridEdit.Get(doorGrid, c.X, c.Y, c.Z) == c) && Lba1GridEdit.Get(doorGrid, 0, 8, 63).Block == 135 && Lba1LampPost.Cells().Count() == 10,
                "lamp: ten cells of block 117 stand on the curb cap at the map's west corner (x 0, z 63, layers 9..18)");
            var lampScene = store.Load(13);
            var lampZone = lampScene.Zones.Where(z => z.Type == 4 && z.Info[1] == 128).ToList();   // (scene 13 has seven bonus zones of its own: money, life, magic, clover)
            Check(lampZone.Count == 1 && Lba1LampPost.ZoneIsThere(lampScene) && lampZone[0].Info.SequenceEqual(new[] { 0, 128, 1, 0 }) && lampZone[0].X0 < 0 && lampZone[0].X1 > 512 && (lampZone[0].Z0 + lampZone[0].Z1) / 2 == 32256 && lampZone[0].Y0 == 2048,
                "lamp: one new bonus zone around it gives one little key (words 0, 128, 1, 0: the game reads which bonuses from the second, as its own giver zones do), on the floor of the platform's cobbles");            var doorCells = Lba1GridCodec.Decode(doorGrid);
            (int Block, int Pos) DoorCell(int x, int z) { var i = ((z * 64 + x) * 25) * 2; return (doorCells[i], doorCells[i + 1]); }
            var pristineStreet = Lba1GridCodec.Decode(HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "LBA_GRI.HQR.bak"))).Read(13));
            Check(Enumerable.Range(11, 5).All(z => Enumerable.Range(52, 5).All(x => { var i = ((z * 64 + x) * 25) * 2; return doorCells[i] == pristineStreet[i] && doorCells[i + 1] == pristineStreet[i + 1]; })), "door: the street in front of the door (x 52..55 and its curb at 56, z 11..15) is the game's own ochre dirt, cell for cell");

            // the fishermen: the chapter-6 offers are in their scripts (which still compile from their own text), the landing points are in the right zones
            string Life(int scene, int actor) => SceneScripts.Load(store.LoadRecord(scene), scene, null, lba1: true).GetText(actor, ScriptKind.Life);
            string Track(int scene, int actor) => SceneScripts.Load(store.LoadRecord(scene), scene, null, lba1: true).GetText(actor, ScriptKind.Track);
            Check(Life(24, 1).Contains("add_choice(118);") && Life(24, 0).Contains("holomap_traj(44);"), "fishermen: Port Belooga offers the Citadel in chapter 6 and lands there");
            Check(Life(24, 1).Contains($"ask_choice({Lba1Fishermen.QuestionId});"), "fishermen: Port Belooga's chapter-6 question is the new text, not the retail speech (text 45)");
            Check(Life(39, 2).Contains("add_choice(17);") && Life(39, 0).Contains("holomap_traj(21);"), "fishermen: the military camp offers Proxima Island in chapter 6 and lands there");
            Check(Life(42, 10).Contains("add_choice(62);") && Life(42, 0).Contains("holomap_traj(31);"), "fishermen: Proxima City offers the White Leaf Desert in chapter 6 and lands there");
            Check(Life(42, 0).Contains("else if (2 == var_cube(0))\n        {\n            holomap_traj(31);\n            change_cube(39);\n        }"), "fishermen: the trip from Proxima ends by loading the desert scene at its start (its east-edge zone waits for a boat that isn't there)");
            Check(Track(42, 10).Contains("label(2);\nangle(704);\nanim(1);\ngoto_point(3);"), "fishermen: Proxima's fisherman turns towards his boat before the walk animation starts (he used to walk off the pier)");

            // the new question: last text of island 1's dialogue in all five languages, everything else in TEXT.HQR untouched
            var texts = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "TEXT.HQR")));
            var retailTexts = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "TEXT.HQR.bak")));
            Check(Enumerable.Range(0, 140).All(i => (i % 28) / 2 == 4 || texts.Read(i).AsSpan().SequenceEqual(retailTexts.Read(i))), "text: every dialogue file but island 1's is unchanged");
            var questionOk = true;
            for (var language = 0; language < 5; language++)
            {
                var at = language * 28 + 8;
                var bank = Lba1TextBank.Load(HqrArchive.Open(Path.Combine(dir, "TEXT.HQR")), language, 4)!;
                var old = Lba1TextBank.Load(HqrArchive.Open(Path.Combine(dir, "TEXT.HQR.bak")), language, 4)!;
                var source = Lba1TextBank.Load(HqrArchive.Open(Path.Combine(dir, "TEXT.HQR.bak")), language, 6)!;
                var expected = language == 0 ? Lba1Fishermen.QuestionEnglish : source.Get(8);
                var expectedGreeting = Lba1PinkElf.Translations.TryGetValue(language, out var greetingText) ? greetingText : Lba1PinkElf.GreetingEnglish;
                questionOk &= bank.Count == old.Count + 2 && bank.IndexOf(Lba1Fishermen.QuestionId) == old.Count && bank.Get(Lba1Fishermen.QuestionId) == expected && bank.IndexOf(Lba1PinkElf.GreetingId) == old.Count + 1 && bank.Get(Lba1PinkElf.GreetingId) == expectedGreeting
                    && old.Ids.All(id => bank.Get(id) == old.Get(id)) && texts.Read(at + 1).Length <= 25000 && bank.Count <= 512;
            }
            Check(questionOk, "text: the question and then the elf's greeting are the last two texts of island 1's dialogue in every language (the question: English as written, the others the price line of the island-3 fisherman; the greeting: its own translation where Lba1PinkElf.Translations has one, English otherwise), the old texts read back unchanged, and the bank fits the engine's buffers");
            Check(new Lba1TextBank(texts.Read(8), texts.Read(9)).Get(Lba1Fishermen.QuestionId) == "For 10 Kashes, where do you want to go, Twinsen?", "text: English reads \"For 10 Kashes, where do you want to go, Twinsen?\"");
            foreach (var fishScene in new[] { 24, 39, 42 })
            {
                var fishRecord = store.LoadRecord(fishScene);
                var built = SceneScripts.Load(fishRecord, fishScene, null, lba1: true).Build();
                Check(built.Ok && built.Record!.AsSpan().SequenceEqual(fishRecord), $"fishermen: scene {fishScene}'s scripts recompile from their text to the same bytes");
                Check(!store.Validate(fishScene, store.Load(fishScene)).Any(i => i.Severity == SceneIssueSeverity.Error), $"fishermen: scene {fishScene} validates");
            }
            bool LandsIn(int scene, int point, int destination)
            {
                var model = store.Load(scene);
                var p = model.TrackPoints[point];
                return model.Zones.Any(z => z.Type == 0 && z.Info[0] == destination && p.X >= z.X0 && p.X <= z.X1 && p.Y >= z.Y0 && p.Y <= z.Y1 && p.Z >= z.Z0 && p.Z <= z.Z1);
            }
            // (the new landings are the last zone and last point of scenes 24 and 39; where a landing puts Twinsen must equal where the game's own zone into that quay puts him)
            (int X, int Y, int Z) ArrivalOf(int scene, int point)
            {
                var model = store.Load(scene);
                var p = model.TrackPoints[point];
                var z = model.Zones.First(z => z.Type == 0 && p.X >= z.X0 && p.X <= z.X1 && p.Y >= z.Y0 && p.Y <= z.Y1 && p.Z >= z.Z0 && p.Z <= z.Z1);
                return (z.Info[1] + p.X - z.X0, z.Info[2] + p.Y - z.Y0, z.Info[3] + p.Z - z.Z0);
            }
            var lastPoint24 = store.Load(24).TrackPoints.Count - 1;
            var lastPoint39 = store.Load(39).TrackPoints.Count - 1;
            Check(LandsIn(24, lastPoint24, 6) && LandsIn(24, 4, 39) && LandsIn(24, 10, 42) && Life(24, 0).Contains($"pos_point({lastPoint24});"), "fishermen: Port Belooga's landing points lie in the zones to the Citadel's quay (new zone), the desert and Proxima");
            Check(LandsIn(39, lastPoint39, 42) && LandsIn(39, 6, 24) && LandsIn(39, 9, 6) && Life(39, 0).Contains($"pos_point({lastPoint39});"), "fishermen: the camp's landing points lie in the zones to Proxima's pier (new zone), Principal Island and the Citadel");
            Check(LandsIn(42, 4, 24) && LandsIn(42, 5, 6), "fishermen: Proxima City's landing points 4 and 5 lie in the zones to Principal Island and the Citadel");
            Check(ArrivalOf(24, lastPoint24) == ArrivalOf(42, 5) && ArrivalOf(39, lastPoint39) == ArrivalOf(24, 10), "fishermen: the new landings put Twinsen exactly where the game's own zones into the Citadel's quay and Proxima's pier do (so the same arrival with the fisherman's boat plays)");
            foreach (var landScene in new[] { 24, 39 })
            {
                var landModel = store.Load(landScene);
                var newZone = landModel.Zones[^1];
                Check(landModel.Zones.Count(z => z.X0 <= newZone.X1 && z.X1 >= newZone.X0 && z.Y0 <= newZone.Y1 && z.Y1 >= newZone.Y0 && z.Z0 <= newZone.Z1 && z.Z1 >= newZone.Z0) == 1 && newZone.Y0 >= 4096,
                    $"fishermen: scene {landScene}'s landing zone is high above the scene and overlaps no other zone (nobody can walk into it)");
            }

            // what the game folder holds, when the tool has been applied there
            var liveBody = Path.Combine(Lba1Dir, "BODY.HQR");
            if (HqrFile.CountSlots(File.ReadAllBytes(liveBody)) == 134)
            {
                Check(EntriesEqualExcept(liveBody, Path.Combine(dir, "BODY.HQR")), "surprise: BODY.HQR matches the game folder's, entry for entry");
                Check(EntriesEqualExcept(Path.Combine(Lba1Dir, "FILE3D.HQR"), Path.Combine(dir, "FILE3D.HQR")), "surprise: FILE3D.HQR matches the game folder's, entry for entry");
                // (the fishermen part is newer than the pink elf: only compare the scenes once the game folder has had it too)
                var liveHasFishermen = false;
                try
                {
                    var liveOutside = new SceneStore(SceneGame.Lba1, Lba1Dir).Load(13);
                    liveHasFishermen = !Lba1Fishermen.PlanFor(new SceneStore(SceneGame.Lba1, Lba1Dir)).Changed && Lba1LampPost.ZoneIsThere(liveOutside) && Lba1DoorLock.IsLocked(liveOutside, 13, 28);
                }
                catch (InvalidDataException) { }
                if (liveHasFishermen) Check(EntriesEqualExcept(Path.Combine(Lba1Dir, "SCENE.HQR"), Path.Combine(dir, "SCENE.HQR"), 61), "surprise: SCENE.HQR matches the game folder's, entry for entry (but the bedroom, upgraded in place: it keeps two unused track points of the first version)");
                else Console.WriteLine("  (the game folder doesn't have the chapter-6 fishermen yet; SCENE.HQR not compared)");
                Console.WriteLine($"  game folder BODY.HQR byte-identical to the tool's: {File.ReadAllBytes(liveBody).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR")))}");
            }

            // a second run changes nothing
            var second = Lba1SurpriseChanges.Apply(dir);
            if (second.Changed) Console.WriteLine("  second run: " + second.Message.Replace("\n", "\n  "));
            Check(!second.Changed, "surprise: a second run changes nothing");

            // one undo step: the scenes, the grid, and the new BODY.HQR/FILE3D.HQR/TEXT.HQR entries all go back
            // (BODY.HQR keeps its slot count -- the two new slots are emptied, not removed, since HqrEntryStore
            // never renumbers a slot table -- but FILE3D.HQR and TEXT.HQR come back byte for byte identical to
            // their originals, since those entries were replaced, not added). Redo brings everything back.
            Check(SceneHistory.UndoDescription == Lba1SurpriseChanges.HistoryName, "surprise: it is one step on the undo log");
            SceneHistory.Undo();
            Check(store.Load(61).Actors.Count == 2 && store.Load(13).Actors.Count == 28, "surprise: undo takes the elf and the door out of the scenes");
            var undoneBodies = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR")));
            Check(undoneBodies.Count == 134 && undoneBodies.IsEmpty(132) && undoneBodies.IsEmpty(133), "surprise: undo also empties the new body slots in BODY.HQR (kept, not renumbered, so nothing else shifts)");
            // Whole-file bytes are the wrong check here (a rebuilt HQR archive can differ in padding/layout from
            // the original even when every entry decodes identically -- see the earlier session's note on this);
            // EntriesEqualExcept compares decoded entries instead, with no exceptions since every one should match.
            Check(EntriesEqualExcept(Path.Combine(dir, "FILE3D.HQR.bak"), Path.Combine(dir, "FILE3D.HQR")), "surprise: undo restores every entry of FILE3D.HQR to its original, not just the scenes");
            Check(EntriesEqualExcept(Path.Combine(dir, "TEXT.HQR.bak"), Path.Combine(dir, "TEXT.HQR")), "surprise: undo restores every entry of TEXT.HQR to its original too");
            SceneHistory.Redo();
            Check(store.Load(61).Actors[2].Body == 42 && store.Load(61).Actors.Count == 3 + 22, "surprise: redo brings the elf and the extras back");
            Check(HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR"))).Read(132).AsSpan().SequenceEqual(pink), "surprise: redo brings the pink elf's body back too");

            // an elf made before the colour was set (the first version spoke in teal): the tool repairs it in place
            var older = store.Load(61); older.Actors[2].CoulObj = 10; store.Save(61, older);
            var repair = Lba1SurpriseChanges.Apply(dir);
            var repaired = store.Load(61);
            Check(repair.Changed && repaired.Actors.Count == 3 + 22 && repaired.Actors[2].CoulObj == 14, "surprise: an elf with another text colour is set to pink (14) without adding a second one");
            Check(HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR"))).Count == 134, "surprise: the repair doesn't add another body");

            // the door as an earlier version of this tool left it (a 5 x 3 doorway copy, no lining, the retail clip rectangle) is completed in place
            var pristineGrid = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "LBA_GRI.HQR.bak"))).Read(13);
            var oldCells = Lba1GridCodec.Decode(pristineGrid);
            var oldEdits = new List<Lba1GridCell>();
            for (var dx = 0; dx < 5; dx++)
                for (var dz = 0; dz < 3; dz++)
                    for (var y = 0; y < 10; y++)
                    {
                        var i = (((53 + dz) * 64 + 33 + dx) * 25 + y) * 2;
                        oldEdits.Add(new Lba1GridCell(47 + dx, y, 12 + dz, oldCells[i], oldCells[i + 1]));
                    }
            var oldGrid = Lba1GridEdit.SetCells(pristineGrid, oldEdits);
            var oldOutside = store.Load(13);
            var oldDoor = oldOutside.Actors.Single(a => a.IsSprite && a.Sprite == 11 && a.X == 51 * 512 && a.Z == 12 * 512 - 256);
            oldDoor.Info = ActorPrefabs.DoorEast.Build(0, oldDoor.X, oldDoor.Y, oldDoor.Z).Info;
            store.Save(13, oldOutside, oldGrid);
            Check(!Lba1RoomDoorMod.ArchIsComplete(store.LoadGrid(13)), "door: the earlier version's arch counts as incomplete");
            var completed = Lba1SurpriseChanges.Apply(dir);
            var completedGrid = store.LoadGrid(13);
            var completedDoor = store.Load(13).Actors.Single(a => a.IsSprite && a.Sprite == 11 && a.X == 51 * 512 && a.Z == 12 * 512 - 256);
            Check(completed.Changed && Lba1RoomDoorMod.ArchIsComplete(completedGrid) && Lba1GridCodec.Decode(completedGrid).AsSpan().SequenceEqual(Lba1GridCodec.Decode(doorGrid)) && completedDoor.Info.SequenceEqual(doorActor.Info), "door: an earlier version's arch and clip rectangle are completed in place, giving the same cells as a fresh run");
            Check(store.Load(13).Actors.Count == 29 && store.Load(61).Actors.Count == 3 + 22, "door: completing it adds neither a second door nor a second elf");
            Check(!Lba1SurpriseChanges.Apply(dir).Changed, "door: and then there is nothing left to do");

            // the door as the third version left it (the street paved with a doorstep slab and cobbles, stone lining where the arch's legs should be):
            // put right in place, giving the same cells as a fresh run
            var thirdVersion = new List<Lba1GridCell>();
            for (var z = 11; z <= 15; z++)
            {
                thirdVersion.Add(new Lba1GridCell(51, 0, z, 6, 0));
                for (var x = 52; x <= 55; x++) thirdVersion.Add(new Lba1GridCell(x, 0, z, 22, (z % 2 == 1 ? 2 : 0) + (x % 2 == 1 ? 0 : 1)));
            }
            for (var y = 1; y <= 4; y++) { thirdVersion.Add(new Lba1GridCell(51, y, 11, 2, (y - 1) % 2)); thirdVersion.Add(new Lba1GridCell(51, y, 15, 2, (y - 1) % 2)); }
            store.Save(13, store.Load(13), Lba1GridEdit.SetCells(store.LoadGrid(13), thirdVersion));
            Check(!Lba1RoomDoorMod.ArchIsComplete(store.LoadGrid(13)), "door: the third version's paved street and stone-lined legs count as incomplete");
            var restored = Lba1SurpriseChanges.Apply(dir);
            Check(restored.Changed && Lba1GridCodec.Decode(store.LoadGrid(13)).AsSpan().SequenceEqual(Lba1GridCodec.Decode(doorGrid)) && Lba1GridEdit.UsedBlocksListed(store.LoadGrid(13)), "door: the third version is put right in place (dirt street, smooth legs), the same cells as a fresh run");
            Check(!Lba1SurpriseChanges.Apply(dir).Changed, "door: and again there is nothing left to do");

            // the lamp's key zone as its first version placed it (a box on the platform, not centred on the lamp): moved, not duplicated
            var lampBefore = store.Load(13);
            lampBefore.Zones.RemoveAll(z => z.Type == 4 && z.Info[1] == 128);
            lampBefore.Zones.Add(new SceneZoneModel { X0 = 0, Y0 = 2048, Z0 = 30464, X1 = 1279, Y1 = 3327, Z1 = 32511, Type = 4, Info = new[] { 128, 1, 0, 0 } });   // (and with the bonus bits in the zone's number word, where the game never looks)
            store.Save(13, lampBefore);
            var lampMoved = Lba1SurpriseChanges.Apply(dir);
            var lampAfter = store.Load(13);
            Check(lampMoved.Changed && lampAfter.Zones.Count(z => z.Type == 4 && (z.Info[1] == 128 || z.Info[0] == 128)) == 1 && Lba1LampPost.ZoneIsThere(lampAfter), "lamp: the first version's key zone is replaced by the new one, not added to");

            // the door's key lock: a fresh run's door needs a little key the first time and keeps the fact in game flag 220; a door that opens for anyone
            // (the earlier versions) gets the lock in place, ending with the same script bytes; a door script that is neither is refused
            {
                var scene13 = store.Load(13);
                var doorAt = scene13.Actors.FindIndex(a => a.IsSprite && a.Sprite == 11 && a.X == 51 * 512 && a.Z == 12 * 512 - 256);
                Check(doorAt == 28 && Lba1DoorLock.IsLocked(scene13, 13, doorAt), "door lock: the door tool's door needs a little key");
                var lockedLife = scene13.Actors[doorAt].Life.ToArray();
                var lockText = SceneScripts.Load(SceneSerializer.Write(scene13), 13, null, lba1: true).GetText(doorAt, ScriptKind.Life);
                Check(lockText.Contains("var_game(220)") && lockText.Contains("nb_little_keys()") && lockText.Contains("use_one_little_key()") && lockText.Contains("set_var_game(220, 1)"),
                    "door lock: the script spends a key and sets flag 220 (\"Door unlocked\")");
                Check(Lba1DoorLock.Flag == 220 && Lba1DoorLock.FlagName == "Door unlocked", "door lock: the flag is 220, \"Door unlocked\"");
                scene13.Actors[doorAt].Life = ActorPrefabs.DoorEast.Build(doorAt, scene13.Actors[doorAt].X, scene13.Actors[doorAt].Y, scene13.Actors[doorAt].Z).Life;
                store.Save(13, scene13);
                Check(!Lba1DoorLock.IsLocked(store.Load(13), 13, doorAt), "door lock: (test setup) the standard door script has no lock");
                var locked = Lba1SurpriseChanges.Apply(dir);
                var lockedScene = store.Load(13);
                Check(locked.Changed && locked.Message.Contains("little key") && Lba1DoorLock.IsLocked(lockedScene, 13, doorAt) && lockedScene.Actors[doorAt].Life.AsSpan().SequenceEqual(lockedLife),
                    "door lock: a door that opens for anyone gets the lock in place, with the same script as a fresh run");
                Check(lockedScene.Actors.Count == 29 && !Lba1SurpriseChanges.Apply(dir).Changed, "door lock: adding it makes no second door, and a second run changes nothing");
                var odd = store.Load(13);
                var oddScripts = SceneScripts.Load(SceneSerializer.Write(odd), 13, null, lba1: true);
                oddScripts.SetText(doorAt, ScriptKind.Life, oddScripts.GetText(doorAt, ScriptKind.Life).Replace("set_track(label_0);", "set_track(label_1);"));
                var oddBuilt = oddScripts.Build();
                var lockRefused = false;
                try { Lba1DoorLock.Apply(SceneSerializer.Parse(SceneGame.Lba1, oddBuilt.Record!), 13, doorAt); } catch (InvalidDataException) { lockRefused = true; }
                Check(oddBuilt.Ok && lockRefused, "door lock: a door script that is neither the standard one nor the locked one is lockRefused, not overwritten");
            }

            // the fishermen as the first version of this tool left them (the retail speech as Port Belooga's question, Proxima's trip ending at the desert's
            // east-edge zone, no turn before the walk to the boat, no new text): brought up to date in place, ending exactly where a fresh run does
            {
                var freshRecords = new[] { 24, 39, 42 }.ToDictionary(s => s, store.LoadRecord);
                var freshText = File.ReadAllBytes(Path.Combine(dir, "TEXT.HQR"));
                var freshZones24 = store.Load(24).Zones.Count;
                var freshRoom = store.LoadRecord(61);
                var downgraded = new List<SceneChange>();
                foreach (var s in new[] { 24, 39, 42 })
                {
                    var downRecord = store.LoadRecord(s);
                    if (s != 42)
                    {
                        // (the first version had no landing zone: it sent Twinsen to the game's ferry zones, points 16 and 13)
                        var model = SceneSerializer.Parse(SceneGame.Lba1, downRecord);
                        var landingPoint = model.TrackPoints.Count - 1;
                        model.Zones.RemoveAt(model.Zones.Count - 1);
                        model.TrackPoints.RemoveAt(landingPoint);
                        downRecord = SceneSerializer.Write(model);
                        var olderScripts = SceneScripts.Load(downRecord, s, null, lba1: true);
                        olderScripts.SetText(0, ScriptKind.Life, olderScripts.GetText(0, ScriptKind.Life).Replace($"pos_point({landingPoint});", s == 24 ? "pos_point(16);" : "pos_point(13);"));
                        downRecord = olderScripts.Build().Record!;
                    }
                    var scripts = SceneScripts.Load(downRecord, s, null, lba1: true);
                    if (s == 24) scripts.SetText(1, ScriptKind.Life, scripts.GetText(1, ScriptKind.Life).Replace($"ask_choice({Lba1Fishermen.QuestionId});", "ask_choice(45);"));
                    else if (s == 42)
                    {
                        scripts.SetText(0, ScriptKind.Life, scripts.GetText(0, ScriptKind.Life).Replace("change_cube(39);", "pos_point(30);"));
                        scripts.SetText(10, ScriptKind.Track, scripts.GetText(10, ScriptKind.Track).Replace("angle(704);\n", ""));
                    }
                    var rebuilt = scripts.Build();
                    Check(rebuilt.Ok, $"fishermen upgrade: the first version's scene {s} builds");
                    downgraded.Add(new SceneChange(s, SceneSerializer.Parse(SceneGame.Lba1, rebuilt.Record!)));
                }
                var olderRoom = store.Load(61);
                olderRoom.Actors[2].Life = Lba1PinkElf.Build(2, greeting: false).Life;
                downgraded.Add(new SceneChange(61, olderRoom));
                store.SaveMany(downgraded, description: "test: fishermen as the first version left them");
                File.Copy(Path.Combine(dir, "TEXT.HQR.bak"), Path.Combine(dir, "TEXT.HQR"), true);
                Check(Life(24, 1).Contains("ask_choice(45);") && Life(24, 0).Contains("pos_point(16);") && Life(39, 0).Contains("pos_point(13);") && Life(42, 0).Contains("pos_point(30);") && !Track(42, 10).Contains("angle(704);") && store.Load(24).Zones.Count == freshZones24 - 1, "fishermen upgrade: the first version's scripts are in place");
                var upgraded = Lba1SurpriseChanges.Apply(dir);
                Check(upgraded.Changed && upgraded.Message.Contains("TEXT.HQR") && upgraded.Message.Contains("scene 24") && upgraded.Message.Contains("scene 39") && upgraded.Message.Contains("scene 42"), "fishermen upgrade: the first version is brought up to date (scenes 24, 39 and 42, and TEXT.HQR)");
                Check(new[] { 24, 39, 42 }.All(s => store.LoadRecord(s).AsSpan().SequenceEqual(freshRecords[s])) && File.ReadAllBytes(Path.Combine(dir, "TEXT.HQR")).AsSpan().SequenceEqual(freshText), "fishermen upgrade: the result is byte for byte what a fresh run gives (scenes 24, 39, 42 and TEXT.HQR)");
                Check(upgraded.Message.Contains("greets Twinsen") && store.LoadRecord(61).AsSpan().SequenceEqual(freshRoom), "elf greeting upgrade: the first version's elf (no greeting) gets its greeting, and scene 61 is byte for byte what a fresh run gives");
                Check(!Lba1SurpriseChanges.Apply(dir).Changed, "fishermen upgrade: and then there is nothing left to do");

                // a text already using the new id for something else is refused, nothing written
                var clashTexts = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "TEXT.HQR")));
                var clashBank = Lba1TextBank.Load(HqrArchive.Open(Path.Combine(dir, "TEXT.HQR")), 0, 4)!;
                var clashText = clashTexts.Read(9);
                // (change the new text's first character)
                var textStart = BitConverter.ToUInt16(clashText, clashBank.IndexOf(Lba1Fishermen.QuestionId) * 2);
                clashText[textStart] = (byte)'X';
                clashTexts.SetStored(9, clashText);
                File.WriteAllBytes(Path.Combine(dir, "TEXT.HQR"), clashTexts.ToBytes());
                var textRefused = false;
                try { Lba1Fishermen.PlanText(dir); } catch (InvalidDataException) { textRefused = true; }
                Check(textRefused, "text: a different text under the question's id is refused");
                File.WriteAllBytes(Path.Combine(dir, "TEXT.HQR"), freshText);

                // a translation replaces the English text standing in for it, in place (the greeting's text is "own"), in the game's DOS code page.
                // (Language 4 already has its own real translation from the Apply() above, so it must be echoed back unchanged here, or this
                // French-only stand-in would collide with it the same way a stale placeholder would collide with a genuinely different text.)
                var withFrench = new Dictionary<int, string>(Lba1PinkElf.Translations) { [1] = "Salut, je suis Floppy, l'élfe à la pomme ça" };
                var french = new Lba1DialogueText.AddedText(Lba1PinkElf.GreetingId, "the greeting", Lba1PinkElf.GreetingEnglish, (_, l) => withFrench.TryGetValue(l, out var t) ? Lba1DialogueText.Bytes(t) : null, Own: true);
                var translated = Lba1DialogueText.Plan(dir, new[] { Lba1Fishermen.Question, french });
                Check(translated.Count == 2, "text: a translation of the greeting is planned as the one changed language's two dialogue entries (order + text)");
                if (translated.Count == 2)
                {
                    var tt = HqrFile.Parse(freshText);
                    foreach (var edit in translated) tt.SetStored(edit.Entry, edit.Payload);
                    var fr = new Lba1TextBank(tt.Read(1 * 28 + 8), tt.Read(1 * 28 + 9));
                    var en = new Lba1TextBank(tt.Read(8), tt.Read(9));
                    var was = new Lba1TextBank(HqrFile.Parse(freshText).Read(1 * 28 + 8), HqrFile.Parse(freshText).Read(1 * 28 + 9));
                    var bytes = tt.Read(1 * 28 + 9);
                    Check(fr.IndexOf(Lba1PinkElf.GreetingId) == was.IndexOf(Lba1PinkElf.GreetingId) && fr.Count == was.Count && en.Get(Lba1PinkElf.GreetingId) == Lba1PinkElf.GreetingEnglish && Enumerable.Range(0, 5).Where(l => l != 1).All(l => tt.Read(l * 28 + 9).AsSpan().SequenceEqual(HqrFile.Parse(freshText).Read(l * 28 + 9))) && bytes.AsSpan().IndexOf(new byte[] { 0x82, 0x6C, 0x66, 0x65 }) >= 0,
                        "text: the translation keeps the greeting's position, only that language changes, and e-acute is written as 0x82 (the game's code page)");
                }
            }

            // a different body already using id 42 is refused, and nothing is written
            var clash = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR")));
            clash.SetStored(132, raymond);
            File.WriteAllBytes(Path.Combine(dir, "BODY.HQR"), clash.ToBytes());
            var sceneNow = File.ReadAllBytes(Path.Combine(dir, "SCENE.HQR"));
            var clashRefused = false;
            try { Lba1SurpriseChanges.Apply(dir); } catch (InvalidDataException) { clashRefused = true; }
            Check(clashRefused && File.ReadAllBytes(Path.Combine(dir, "SCENE.HQR")).AsSpan().SequenceEqual(sceneNow), "surprise: a body that isn't the pink elf under id 42 is refused and nothing is written");
        }
        finally { Cleanup(dir); SceneHistory.Clear(); }
    }

    // SceneHistory's configurable step/byte caps and its disk persistence (undo/redo settings, this session).
    // MaxSteps/MaxBytes/PersistPath/ConfirmClearWhenFull are process-wide, so every path through this test
    // restores them to the library's own defaults before returning -- leaving them changed would silently
    // corrupt whichever history test happens to run after this one.
    private static void HistoryLimits()
    {
        SceneHistory.Clear();
        var dir = TempLba1();
        var persistFile = Path.Combine(Path.GetTempPath(), "lba1_history_" + Guid.NewGuid().ToString("N")[..8] + ".dat");
        try
        {
            var store = new SceneStore(SceneGame.Lba1, dir);

            // Step cap: with room set for 3, a 4th save evicts the 1st (oldest), FIFO -- same as the old
            // hardcoded 100-step cap, just at a number small enough to hit in a test.
            SceneHistory.MaxSteps = 3;
            SceneHistory.MaxBytes = 50 * 1024 * 1024;
            for (var i = 1; i <= 4; i++)
            {
                var scene = store.Load(5);
                scene.Actors[1].X += 16;
                store.Save(5, scene, description: $"step {i}");
            }
            Check(SceneHistory.UndoCount == 3, "history limits: MaxSteps trims the log to 3 steps");
            Check(SceneHistory.UndoDescription == "step 4", "history limits: the newest step is kept");

            // Lowering MaxSteps below the current count trims immediately, not just on the next Record().
            SceneHistory.MaxSteps = 1;
            Check(SceneHistory.UndoCount == 1 && SceneHistory.UndoDescription == "step 4", "history limits: lowering MaxSteps trims the log right away, keeping the newest");
            SceneHistory.MaxSteps = 100;

            // Byte cap, declining the clear: the oldest step(s) are evicted one at a time until the new one fits.
            SceneHistory.Clear();
            // Before always carries the grid too (Snapshot loads it unconditionally for LBA1); After here doesn't,
            // since this Save doesn't pass one -- so one step's real size is 2x the record plus one grid, not 2x
            // (record + grid). SizeOf also counts the tiny Description/Directory strings; +512 covers those.
            var stepBytes = 2L * store.LoadRecord(5).Length + store.LoadGrid(5).Length + 512;
            SceneHistory.MaxBytes = stepBytes * 3; // room for ~3 steps
            var confirmCalls = 0;
            SceneHistory.ConfirmClearWhenFull = (_, _) => { confirmCalls++; return false; }; // decline every time: fall back to trimming
            for (var i = 1; i <= 5; i++)
            {
                var scene = store.Load(5);
                scene.Actors[1].X += 16;
                store.Save(5, scene, description: $"byte step {i}");
            }
            Check(confirmCalls > 0, "history limits: nearing MaxBytes asks ConfirmClearWhenFull");
            Check(SceneHistory.UndoCount is >= 1 and <= 3, "history limits: declining the clear trims to fit MaxBytes instead");
            Check(SceneHistory.UndoDescription == "byte step 5", "history limits: the newest byte-capped step is kept");
            Check(SceneHistory.CurrentBytes <= SceneHistory.MaxBytes, "history limits: the log fits back under MaxBytes");

            // Byte cap, accepting the clear: the whole log is emptied to make room.
            SceneHistory.ConfirmClearWhenFull = (_, _) => true;
            {
                var scene = store.Load(5);
                scene.Actors[1].X += 16;
                store.Save(5, scene, description: "byte step 6, clears");
            }
            Check(SceneHistory.UndoCount == 1 && SceneHistory.UndoDescription == "byte step 6, clears", "history limits: accepting the clear empties the log before storing the new step");
            SceneHistory.ConfirmClearWhenFull = null;
            SceneHistory.MaxBytes = 50 * 1024 * 1024;

            // Disk persistence: what's on the log survives a simulated restart, and the restored step still
            // undoes correctly because the game files on disk still match what it expects.
            SceneHistory.Clear();
            SceneHistory.PersistPath = persistFile;
            var beforeX = store.Load(5).Actors[1].X;
            {
                var scene = store.Load(5);
                scene.Actors[1].X += 16;
                store.Save(5, scene, description: "persisted step");
            }
            Check(File.Exists(persistFile), "history limits: a save with PersistPath set writes the history file");
            Check(SceneHistory.UndoCount == 1, "history limits: sanity, one step recorded before the restart");
            var savedUndo = SceneHistory.UndoDescription;

            // Simulate a restart: drop the in-memory log without touching the file just written (PersistPath is
            // off for this Clear(), so nothing here overwrites it), then reload it exactly as MainWindow does at
            // startup.
            SceneHistory.PersistPath = null;
            SceneHistory.Clear();
            SceneHistory.PersistPath = persistFile;
            SceneHistory.Load();
            Check(SceneHistory.UndoCount == 1 && SceneHistory.UndoDescription == savedUndo, "history limits: the log is restored from disk after a simulated restart");
            SceneHistory.Undo();
            Check(store.Load(5).Actors[1].X == beforeX, "history limits: a step restored from disk still undoes correctly");
        }
        finally
        {
            Cleanup(dir);
            SceneHistory.Clear();
            SceneHistory.PersistPath = null;
            SceneHistory.ConfirmClearWhenFull = null;
            SceneHistory.MaxSteps = 100;
            SceneHistory.MaxBytes = 50 * 1024 * 1024;
            try { File.Delete(persistFile); } catch (IOException) { }
        }
    }

    // Grid/block-library editors, the object browser's Replace and the brick/sprite editor all route raw HQR-entry
    // saves through HqrEntryStore now instead of a one-time-.bak-only FileTransaction -- this checks each names
    // its own step on the one shared undo log and that undo restores exactly the touched entry.
    private static void HqrEntryUndo()
    {
        SceneHistory.Clear();
        var dir1 = TempLba1();
        try
        {
            // Lba1GridBackend also reads RESS.HQR (for its own palette); the object-replace-equivalent check below needs BODY.HQR too.
            foreach (var name in new[] { "RESS.HQR", "BODY.HQR" }) File.Copy(Path.Combine(Lba1Dir, name), Path.Combine(dir1, name));
            var libraryPath = Path.Combine(dir1, "LBA_BLL.HQR");
            var originalLibrary = HqrFile.Parse(File.ReadAllBytes(libraryPath)).Read(13);
            var editedLibrary = (byte[])originalLibrary.Clone();
            editedLibrary[0] ^= 0xFF;
            new Lba1GridBackend(dir1).SaveLibrary(13, editedLibrary);
            Check(SceneHistory.UndoDescription == "Edit block library 13", "hqr entries: LBA1 SaveLibrary names its own undo step");
            Check(HqrFile.Parse(File.ReadAllBytes(libraryPath)).Read(13).AsSpan().SequenceEqual(editedLibrary), "hqr entries: LBA1 SaveLibrary wrote the new library");
            SceneHistory.Undo();
            Check(HqrFile.Parse(File.ReadAllBytes(libraryPath)).Read(13).AsSpan().SequenceEqual(originalLibrary), "hqr entries: undo restores the LBA1 library exactly, not a copy of the whole file");
            SceneHistory.Redo();
            Check(HqrFile.Parse(File.ReadAllBytes(libraryPath)).Read(13).AsSpan().SequenceEqual(editedLibrary), "hqr entries: redo re-applies the LBA1 library edit");

            // the object browser's Replace: a direct HqrEntryStore.Save call, the same shape it uses
            var bodyPath = Path.Combine(dir1, "BODY.HQR");
            var originalBody = HqrFile.Parse(File.ReadAllBytes(bodyPath)).Read(5);
            var replacementBody = (byte[])originalBody.Clone();
            replacementBody[0] ^= 0xFF;
            HqrEntryStore.Save(SceneGame.Lba1, dir1, "Replace BODY.HQR entry 5", new[] { new HqrEntryStore.Edit("BODY.HQR", 5, replacementBody) });
            Check(SceneHistory.UndoDescription == "Replace BODY.HQR entry 5", "hqr entries: a direct Replace-style save names itself too");
            SceneHistory.Undo();
            Check(HqrFile.Parse(File.ReadAllBytes(bodyPath)).Read(5).AsSpan().SequenceEqual(originalBody), "hqr entries: undo restores the replaced body exactly");
        }
        finally { Cleanup(dir1); SceneHistory.Clear(); }

        var dir2 = Path.Combine(Path.GetTempPath(), "lba2_hqrentry_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir2);
        try
        {
            File.Copy(Path.Combine(Lba2Dir, "LBA_BKG.HQR"), Path.Combine(dir2, "LBA_BKG.HQR"));
            File.Copy(Path.Combine(Lba2Dir, "RESS.HQR"), Path.Combine(dir2, "RESS.HQR"));
            var backend = new Lba2GridBackend(dir2);
            var gridId = backend.Grids[0].Id;
            var originalGrid = backend.LoadGrid(gridId);
            var originalLibrary2 = backend.LoadLibrary(gridId);

            var editedGrid = (byte[])originalGrid.Clone(); editedGrid[0] ^= 0xFF;
            backend.SaveGrid(gridId, editedGrid);
            Check(SceneHistory.UndoDescription == $"Edit grid {gridId}", "hqr entries: LBA2 SaveGrid names its own undo step");
            Check(new Lba2GridBackend(dir2).LoadGrid(gridId).AsSpan().SequenceEqual(editedGrid), "hqr entries: LBA2 SaveGrid wrote the new grid");
            SceneHistory.Undo();
            Check(new Lba2GridBackend(dir2).LoadGrid(gridId).AsSpan().SequenceEqual(originalGrid), "hqr entries: undo restores the LBA2 grid exactly, not a copy of LBA_BKG.HQR");
            SceneHistory.Redo();
            Check(new Lba2GridBackend(dir2).LoadGrid(gridId).AsSpan().SequenceEqual(editedGrid), "hqr entries: redo re-applies the LBA2 grid edit");
            SceneHistory.Undo(); // back to original before touching the library, so the two edits don't share a slot

            var editedLibrary2 = (byte[])originalLibrary2.Clone(); editedLibrary2[0] ^= 0xFF;
            new Lba2GridBackend(dir2).SaveLibrary(gridId, editedLibrary2);
            Check(new Lba2GridBackend(dir2).LoadLibrary(gridId).AsSpan().SequenceEqual(editedLibrary2), "hqr entries: LBA2 SaveLibrary wrote the new library");
            SceneHistory.Undo();
            Check(new Lba2GridBackend(dir2).LoadLibrary(gridId).AsSpan().SequenceEqual(originalLibrary2), "hqr entries: undo restores the LBA2 library exactly");

            // the brick/sprite editor (GphLibrary): a plain replace (never an insert) is entry-level too
            var bricks = GphLibrary.Lba2Bricks(dir2);
            var brickNumber = bricks.Numbers().First();
            var originalBrick = bricks.Read(brickNumber);
            var replacement = new GphImage(originalBrick.Width, originalBrick.Height);
            for (var y = 0; y < replacement.Height; y++) for (var x = 0; x < replacement.Width; x++) replacement.Set(x, y, 7);
            bricks.Replace(brickNumber, replacement);
            bricks.Save(new[] { brickNumber }, $"Replace brick {brickNumber}");
            Check(SceneHistory.UndoDescription == $"Replace brick {brickNumber}", "hqr entries: GphLibrary.Save names a plain replace as an entry-level step");
            SceneHistory.Undo();
            var reverted = GphLibrary.Lba2Bricks(dir2).Read(brickNumber);
            Check(reverted.Pixels.AsSpan().SequenceEqual(originalBrick.Pixels) && reverted.Opaque.AsSpan().SequenceEqual(originalBrick.Opaque), "hqr entries: undo restores the replaced brick picture exactly");
        }
        finally { Cleanup(dir2); SceneHistory.Clear(); }
    }

    // ActorAttributesWindow's new Entity picker lets a brand-new LBA2 actor (Add Actor Here) be saved to the
    // scene file instead of staying session-only forever -- Lba2ActorPersistence.Save's own logic for that,
    // tested directly here (no native renderer needed: a "new actor" is just indexInScene landing one past the
    // scene's last real actor, exactly as Locate would report for one).
    private static void Lba2NewActor()
    {
        SceneHistory.Clear();
        var dir = Path.Combine(Path.GetTempPath(), "lba2_newactor_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in new[] { "SCENE.HQR", "RESS.HQR" }) File.Copy(Path.Combine(Lba2Dir, name), Path.Combine(dir, name));
            var store = new SceneStore(SceneGame.Lba2, dir);
            var entityTable = Lba2EntityTable.Load(dir);
            var entity = entityTable!.Entities.First(e => e.Bodies.Count > 0 && e.Anims.Count > 0);
            var body = entity.Bodies[0].Body;
            var anim = entity.Anims[0].Anim;

            const int scene = 5;
            var before = store.Load(scene).Actors.Count;
            var indexInScene = before - 1; // lands exactly at the file's own next slot (Locate reports this for a real new actor too)
            var blank = new Lba2ActorPersistence.Snapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var placed = new Lba2ActorPersistence.Snapshot(1024, 512, 2048, 100, body, anim, 200, 50, 10, 1, 0x0806);

            var noEntity = Lba2ActorPersistence.Save(dir, scene, indexInScene, blank, placed, out _);
            Check(noEntity is not null && noEntity.Contains("kind of actor"), "lba2 new actor: refused with no entity chosen, and nothing added");
            Check(store.Load(scene).Actors.Count == before, "lba2 new actor: the refusal added nothing");

            var badBody = Lba2ActorPersistence.Save(dir, scene, indexInScene, blank, placed with { Body = 99999 }, out _, entity.Id);
            Check(badBody is not null && badBody.Contains("Body"), "lba2 new actor: refused when the body isn't one of the entity's own");

            var gap = Lba2ActorPersistence.Save(dir, scene, indexInScene + 1, blank, placed, out _, entity.Id);
            Check(gap is not null && gap.Contains("order"), "lba2 new actor: refused when an earlier new actor of the same scene hasn't been saved yet");

            var ok = Lba2ActorPersistence.Save(dir, scene, indexInScene, blank, placed, out var note, entity.Id);
            Check(ok is null && note is null, $"lba2 new actor: a real entity/body/anim combination saves cleanly ({ok ?? note})");
            var afterSave = store.Load(scene);
            var added = afterSave.Actors[^1];
            // LifePoints is a signed byte on disk (see Lba2ActorPersistence's own Signed helper): 200 the window
            // shows (matching the native session's unsigned-looking convention) reads back as unchecked (sbyte)200.
            Check(afterSave.Actors.Count == before + 1 && added.Entity == entity.Id && added.Body == entity.Bodies[0].Generic && added.Anim == entity.Anims[0].Generic
                  && added.X == 1024 && added.Y == 512 && added.Z == 2048 && added.Beta == 100 && added.LifePoints == unchecked((sbyte)200) && added.Move == 1,
                "lba2 new actor: the new actor lands at the end with the entity's own generic body/anim ids and every field applied");
            Check(SceneHistory.UndoDescription == $"Add actor {before} to scene {scene}", "lba2 new actor: it is one named step on the undo log");

            SceneHistory.Undo();
            Check(store.Load(scene).Actors.Count == before, "lba2 new actor: undo removes it again");
            SceneHistory.Redo();
            Check(store.Load(scene).Actors.Count == before + 1 && store.Load(scene).Actors[^1].Entity == entity.Id, "lba2 new actor: redo brings it back");

            // A real bug this feature's own UI testing found: an exterior (island) scene stores an actor's
            // position relative to its own cube, but the native session (and so this Snapshot) reports the
            // world-absolute position -- Lba2ActorPersistence.Save has to subtract the cube's own offset
            // (SceneModel.CubeX/CubeY * 32768) back out before writing, for both a new actor and an edited
            // existing one, or the write either fails the S16 range check (loud, for most cubes) or -- for a
            // scene whose cube happens to be (0, 0), where the offset is zero either way -- would have looked
            // fine while quietly being right only by coincidence.
            SceneModel? TryLoad(int s) { try { return store.Load(s); } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { return null; } }
            var exteriorScene = Enumerable.Range(0, 222).Select(s => (s, m: TryLoad(s)))
                .FirstOrDefault(x => x.m is { CubeMode: 1 } m && (m.CubeX > 0 || m.CubeY > 0) && m.Actors.Count > 1);
            if (exteriorScene.m is null) Console.WriteLine("  (no exterior scene with a non-zero cube found; skipping the cube-offset checks)");
            else
            {
                var (extScene, extModel) = (exteriorScene.s, exteriorScene.m);
                var worldOffsetX = extModel.CubeX * 32768; var worldOffsetZ = extModel.CubeY * 32768;
                Console.WriteLine($"  exterior cube-offset check: scene {extScene}, cube ({extModel.CubeX},{extModel.CubeY})");

                // an existing actor's position, edited: the world-absolute value the window would show/send back
                var existingLocalX = extModel.Actors[1].X; var existingLocalZ = extModel.Actors[1].Z;
                var existingWorld = new Lba2ActorPersistence.Snapshot(worldOffsetX + existingLocalX, extModel.Actors[1].Y, worldOffsetZ + existingLocalZ,
                    extModel.Actors[1].Beta, 0, 0, 0, 0, 0, 0, 0);
                var movedWorld = existingWorld with { X = existingWorld.X + 256, Z = existingWorld.Z + 256 };
                var moveError = Lba2ActorPersistence.Save(dir, extScene, 0, existingWorld, movedWorld, out var moveNote);
                Check(moveError is null && moveNote is null, $"cube offset: moving an existing exterior actor by a world-absolute delta saves cleanly ({moveError ?? moveNote})");
                var movedActor = store.Load(extScene).Actors[1];
                Check(movedActor.X == existingLocalX + 256 && movedActor.Z == existingLocalZ + 256,
                    "cube offset: the file keeps the scene-local position (world-absolute minus the cube's own offset), not the world-absolute one");
                SceneHistory.Undo(); // back to the original position

                // a new actor placed at a world-absolute position on this same exterior scene
                var extBefore = store.Load(extScene).Actors.Count;
                var extBlank = new Lba2ActorPersistence.Snapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                var extPlaced = new Lba2ActorPersistence.Snapshot(worldOffsetX + 4096, 512, worldOffsetZ + 8192, 0, body, anim, 255, 0, 0, 0, 0x0806);
                var extError = Lba2ActorPersistence.Save(dir, extScene, extBefore - 1, extBlank, extPlaced, out var extNote, entity.Id);
                Check(extError is null && extNote is null, $"cube offset: adding a new exterior actor at a world-absolute position saves cleanly ({extError ?? extNote})");
                var extAdded = store.Load(extScene).Actors[^1];
                Check(extAdded.X == 4096 && extAdded.Z == 8192, "cube offset: the new actor's file position is scene-local (world-absolute minus the cube's own offset)");
            }
        }
        finally { Cleanup(dir); SceneHistory.Clear(); }
    }
}
