using LBA2LevelEditor;
using LBA2LevelEditor.Lba1;
using LBA2LevelEditor.LbaScript;
using LBA2LevelEditor.Scenes;

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
                    var stale = SceneOps.ReferencesTo(scene, LBA2LevelEditor.LbaScript.ArgRole.Obj, before - 1);
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
            using var _ = LBA2LevelEditor.LbaScript.Opcodes.Use(LBA2LevelEditor.LbaScript.Opcodes.Lba1);
            var life = LBA2LevelEditor.LbaScript.Bytecode.DecodeLife(scene.Actors[index - 1].Life);
            var own = life.Where(i => i.Func == 1).Select(i => i.Value).ToList();
            Check(own.Count > 0 && own.All(v => v == index - 1), $"the door's own number follows it ({index} -> {index - 1}): values {string.Join(",", own)}");
            var withDoor = store.Load(13);
            var doorIndex = ActorPrefabs.Place(withDoor, ActorPrefabs.DoorEast, 51 * 512, 256, 12 * 512 - 256);
            var refs = SceneOps.ReferencesTo(withDoor, LBA2LevelEditor.LbaScript.ArgRole.Obj, doorIndex);
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

            var withPoints = Enumerable.Range(0, 120).Select(store.Load).First(m => m.TrackPoints.Count >= 3 && SceneOps.ReferencesTo(m, LBA2LevelEditor.LbaScript.ArgRole.Point, 2).Count > 0);
            var before = withPoints.TrackPoints.Count;
            var refused = false;
            try { SceneOps.DeleteTrackPoint(withPoints, 0); } catch (SceneEditException) { refused = true; }
            Check(refused || SceneOps.ReferencesTo(withPoints, LBA2LevelEditor.LbaScript.ArgRole.Point, 0).Count == 0, "deleting a used track point is refused");
            SceneOps.DeleteTrackPoint(withPoints, 0, retarget: 1);
            Check(withPoints.TrackPoints.Count == before - 1 && SceneOps.ReferencesTo(withPoints, LBA2LevelEditor.LbaScript.ArgRole.Point, before - 1).Count == 0, "track points after the deleted one are renumbered");
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

                var data = new LBA2LevelEditor.Lba1.Runtime.Lba1RuntimeData(dir);
                var runtime = new LBA2LevelEditor.Lba1.Runtime.Lba1Runtime(data);
                runtime.ChangeCube(slot);
                runtime.Run(100);
                Check(runtime.Hero.PosY == 256 && runtime.NumCube == slot, $"blank scene {slot}: Twinsen stands on the floor (y {runtime.Hero.PosY})");
                var z0 = runtime.Hero.PosZ;
                runtime.Joy = LBA2LevelEditor.Lba1.Runtime.Lba1Const.JUp;
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
                Check(SameActor(live.Actors[^1], ActorPrefabs.DoorEast.Build(live.Actors.Count - 1, 26112, 256, 5888)), "the prefab reproduces the door actor already in the game files, scripts included");
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
            Check(EntriesEqualExcept(Path.Combine(dir, "LBA_GRI.HQR.bak"), Path.Combine(dir, "LBA_GRI.HQR"), 13), "door tool: only grid 13 changed");

            // the game files the earlier version of the tool produced (and the game was tested with) hold the same entries
            // (scene 61 is left out: the game folder's copy also holds the pink elf, see Surprise)
            var live = Path.Combine(Lba1Dir, "SCENE.HQR");
            if (HqrFile.Parse(File.ReadAllBytes(live)).Read(13).Length != HqrFile.Parse(sceneOriginal).Read(13).Length)
            {
                Check(EntriesEqualExcept(live, Path.Combine(dir, "SCENE.HQR"), 61), "door tool: scenes match the ones already in the game folder, entry for entry");
                Check(EntriesEqualExcept(Path.Combine(Lba1Dir, "LBA_GRI.HQR"), Path.Combine(dir, "LBA_GRI.HQR")), "door tool: grids match the ones already in the game folder, entry for entry");
            }

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
            foreach (var name in new[] { "BODY.HQR", "FILE3D.HQR" }) File.Copy(PristineFile(name), Path.Combine(dir, name));
            var retailBodies = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR")));
            var retailEntities = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "FILE3D.HQR")));
            var bodyBytes = File.ReadAllBytes(Path.Combine(dir, "BODY.HQR"));
            var entityBytes = File.ReadAllBytes(Path.Combine(dir, "FILE3D.HQR"));
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
            Check(bodies.Count == 133 && bodies.Read(132).AsSpan().SequenceEqual(pink), "surprise: BODY.HQR gained entry 132, the pink elf");
            Check(Enumerable.Range(0, 132).All(i => bodies.Read(i).AsSpan().SequenceEqual(retailBodies.Read(i))), "surprise: the 132 retail bodies are unchanged");
            Check(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR.bak")).AsSpan().SequenceEqual(bodyBytes), "surprise: BODY.HQR.bak holds the original file");
            Check(bodies.ToBytes().Length == HqrFile.Parse(bodyBytes).ToBytes().Length + 4 + 10 + pink.Length, "surprise: the file grew by one table slot and one stored entry");

            // FILE3D.HQR: the Elf entity has one more record, BODY id 42 -> entry 132, and nothing else changed
            var entities = HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "FILE3D.HQR")));
            Check(EntriesEqualExcept(Path.Combine(dir, "FILE3D.HQR.bak"), Path.Combine(dir, "FILE3D.HQR"), Lba1PinkElf.Entity), "surprise: only the Elf entity changed in FILE3D.HQR");
            var before = retailEntities.Read(Lba1PinkElf.Entity); var after = entities.Read(Lba1PinkElf.Entity);
            var record = new byte[] { 1, Lba1PinkElf.BodyId, 4, 132, 0, 0 };
            Check(after.Length == before.Length + 6 && after.AsSpan(12, 6).SequenceEqual(record) && after.AsSpan(0, 12).SequenceEqual(before.AsSpan(0, 12)) && after.AsSpan(18).SequenceEqual(before.AsSpan(12)),
                "surprise: the Elf entity gained the record 01 2A 04 84 00 00 after its two bodies, animations untouched");

            // scene 61: the elf is the last actor, in pink text, and the scene validates
            var room = store.Load(61);
            var elf = room.Actors[^1];
            Check(room.Actors.Count == 3 && elf.Entity == 49 && elf.Body == 42 && elf.Anim == 0 && !elf.IsSprite && elf.CoulObj == 14, "surprise: scene 61 has the pink elf as actor 2 (entity 49, body 42, animation 0, colour 14)");
            Check(elf.Life.Length > 0 && elf.Track.Length > 0, "surprise: the elf has its scripts");
            Check(!store.Validate(61, room).Any(i => i.Severity == SceneIssueSeverity.Error) && !store.Validate(13, store.Load(13), store.LoadGrid(13)).Any(i => i.Severity == SceneIssueSeverity.Error), "surprise: scenes 61 and 13 validate");
            Check(EntriesEqualExcept(Path.Combine(dir, "SCENE.HQR.bak"), Path.Combine(dir, "SCENE.HQR"), 13, 61), "surprise: only scenes 13 and 61 changed");
            Check(EntriesEqualExcept(Path.Combine(dir, "LBA_GRI.HQR.bak"), Path.Combine(dir, "LBA_GRI.HQR"), 13), "surprise: only grid 13 changed");

            // what the game folder holds, when the tool has been applied there
            var liveBody = Path.Combine(Lba1Dir, "BODY.HQR");
            if (HqrFile.CountSlots(File.ReadAllBytes(liveBody)) == 133)
            {
                Check(EntriesEqualExcept(liveBody, Path.Combine(dir, "BODY.HQR")), "surprise: BODY.HQR matches the game folder's, entry for entry");
                Check(EntriesEqualExcept(Path.Combine(Lba1Dir, "FILE3D.HQR"), Path.Combine(dir, "FILE3D.HQR")), "surprise: FILE3D.HQR matches the game folder's, entry for entry");
                Check(EntriesEqualExcept(Path.Combine(Lba1Dir, "SCENE.HQR"), Path.Combine(dir, "SCENE.HQR")), "surprise: SCENE.HQR matches the game folder's, entry for entry");
                Console.WriteLine($"  game folder BODY.HQR byte-identical to the tool's: {File.ReadAllBytes(liveBody).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR")))}");
            }

            // a second run changes nothing
            Check(!Lba1SurpriseChanges.Apply(dir).Changed, "surprise: a second run changes nothing");

            // one undo step: the scenes and the grid go back (the new body stays, unused), redo brings the elf back
            Check(SceneHistory.UndoDescription == Lba1SurpriseChanges.HistoryName, "surprise: it is one step on the undo log");
            SceneHistory.Undo();
            Check(store.Load(61).Actors.Count == 2 && store.Load(13).Actors.Count == 28, "surprise: undo takes the elf and the door out of the scenes");
            Check(HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR"))).Count == 133, "surprise: undo leaves the new body in BODY.HQR (unused)");
            SceneHistory.Redo();
            Check(store.Load(61).Actors[^1].Body == 42, "surprise: redo brings the elf back");

            // an elf made before the colour was set (the first version spoke in teal): the tool repairs it in place
            var older = store.Load(61); older.Actors[^1].CoulObj = 10; store.Save(61, older);
            var repair = Lba1SurpriseChanges.Apply(dir);
            var repaired = store.Load(61);
            Check(repair.Changed && repaired.Actors.Count == 3 && repaired.Actors[^1].CoulObj == 14, "surprise: an elf with another text colour is set to pink (14) without adding a second one");
            Check(HqrFile.Parse(File.ReadAllBytes(Path.Combine(dir, "BODY.HQR"))).Count == 133, "surprise: the repair doesn't add another body");

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
}
