using LBAAssembler;
using LBAAssembler.Scenes;

namespace ScriptRoundTrip;

// Checks of the shared foundation against the retail game files: the HQR layer (HqrFile, HqrLz, FileTransaction) and
// the scene model / serializer for both games. Read-only: everything that writes uses temp copies.
//   foundation hqr | lz | model | all
internal static class FoundationTests
{
    private static readonly string Lba1Dir = Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure";
    private static readonly string Lba2Dir = Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer";

    private static int failures, checks;

    private static void Check(bool ok, string what)
    {
        checks++;
        if (ok) return;
        failures++;
        if (failures <= 40) Console.WriteLine($"  FAIL {what}");
    }

    public static int Run(string[] args)
    {
        var what = args.Length > 1 ? args[1] : "all";
        if (what is "hqr" or "all") HqrRoundTrip();
        if (what is "lz" or "all") LzRoundTrip();
        if (what is "ops" or "all") HqrOperations();
        if (what is "tx" or "all") Transactions();
        if (what is "model" or "all") ModelRoundTrip();
        Console.WriteLine(failures == 0 ? $"foundation: all {checks} checks passed" : $"foundation: {failures} of {checks} checks FAILED");
        return failures == 0 ? 0 : 1;
    }

    // Parse -> ToBytes must reproduce every retail HQR file exactly.
    private static void HqrRoundTrip()
    {
        foreach (var dir in new[] { Lba1Dir, Lba2Dir })
        {
            int same = 0, different = 0, unsupported = 0;
            foreach (var path in Directory.EnumerateFiles(dir, "*.HQR").Concat(Directory.EnumerateFiles(dir, "*.hqr")).Distinct())
            {
                var bytes = File.ReadAllBytes(path);
                try
                {
                    var rebuilt = HqrFile.Parse(bytes).ToBytes();
                    if (rebuilt.AsSpan().SequenceEqual(bytes)) same++;
                    else { different++; Console.WriteLine($"  not identical after rebuild: {Path.GetFileName(path)} ({bytes.Length} -> {rebuilt.Length} bytes)"); }
                }
                catch (Exception e) { unsupported++; Console.WriteLine($"  cannot parse {Path.GetFileName(path)}: {e.Message}"); }
            }
            Console.WriteLine($"hqr {Path.GetFileName(dir)}: {same} rebuilt identically, {different} differ, {unsupported} unsupported");
            Check(different == 0 && unsupported == 0, $"every HQR in {dir} rebuilds byte-for-byte");
        }
    }

    // Compressing then decoding gives back the data, for both methods; in-place safety is reported.
    private static void LzRoundTrip()
    {
        long raw = 0, packed = 0;
        int entries = 0, safe = 0, stored = 0;
        foreach (var file in new[] { Path.Combine(Lba1Dir, "SCENE.HQR.bak"), Path.Combine(Lba2Dir, "SCENE.HQR"), Path.Combine(Lba1Dir, "LBA_GRI.HQR.bak") })
        {
            if (!File.Exists(file)) continue;
            var archive = HqrArchive.Open(file);
            var count = HqrArchive.CountEntries(file);
            for (var i = 0; i < count; i++)
            {
                if (!archive.IsValid(i)) continue;
                var data = archive.Read(i);
                foreach (var method in new[] { 1, 2 })
                {
                    var entry = HqrWriter.CompressedEntry(data, method);
                    entries++;
                    Check(HqrArchive.DecodeEntry(entry).AsSpan().SequenceEqual(data), $"{Path.GetFileName(file)} entry {i}: method {method} round trip");
                    if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(8)) == 0) stored++;
                    else { safe++; raw += data.Length; packed += entry.Length - 10; }
                }
            }
        }
        Console.WriteLine($"lz: {entries} round trips; {safe} compressed in-place-safe, {stored} kept stored; compressed payloads are {(raw == 0 ? 0 : 100 * packed / raw)}% of their originals");
    }

    // Replace / add / clear / remove / alias handling on in-memory copies.
    private static void HqrOperations()
    {
        var original = File.ReadAllBytes(Path.Combine(Lba1Dir, "SCENE.HQR.bak"));
        var file = HqrFile.Parse(original);
        var slots = file.Count;

        file.SetStored(5, new byte[] { 1, 2, 3 });
        var after = HqrFile.Parse(file.ToBytes());
        Check(after.Count == slots && after.Read(5).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }), "replaced entry reads back");
        var reference = HqrFile.Parse(original);
        var untouched = Enumerable.Range(0, slots).Where(i => i != 5 && !reference.IsEmpty(i)).All(i => reference.Read(i).AsSpan().SequenceEqual(after.Read(i)));
        Check(untouched, "the other entries are unchanged by a replace");

        var index = after.Add(new byte[] { 9, 9 });
        var afterAdd = HqrFile.Parse(after.ToBytes());
        Check(index == slots && afterAdd.Count == slots + 1 && afterAdd.Read(index).Length == 2, "appended entry gets the next slot");

        afterAdd.Clear(3);
        var afterClear = HqrFile.Parse(afterAdd.ToBytes());
        Check(afterClear.Count == slots + 1 && afterClear.IsEmpty(3) && !afterClear.IsEmpty(4), "cleared slot is empty, others keep their numbers");

        afterClear.RemoveAt(3);
        var afterRemove = HqrFile.Parse(afterClear.ToBytes());
        Check(afterRemove.Count == slots && afterRemove.Read(3).AsSpan().SequenceEqual(reference.Read(4)), "removing a slot renumbers the later ones");

        // aliases: two slots with one offset stay one copy, and replacing one detaches it
        var alias = HqrFile.Parse(BuildWithAlias());
        Check(alias.Slots[2].AliasOf == 1 && alias.Read(2).AsSpan().SequenceEqual(new byte[] { 7, 7, 7 }), "aliased slot reads the shared entry");
        Check(alias.ToBytes().AsSpan().SequenceEqual(BuildWithAlias()), "aliased file rebuilds identically");
        alias.SetStored(1, new byte[] { 5 });
        var detached = HqrFile.Parse(alias.ToBytes());
        Check(detached.Read(1).AsSpan().SequenceEqual(new byte[] { 5 }) && detached.Read(2).AsSpan().SequenceEqual(new byte[] { 7, 7, 7 }), "replacing an aliased entry leaves the other slot's data");

        // the older single-entry helper and HqrFile agree
        var viaWriter = HqrWriter.ReplaceEntry(original, 5, HqrWriter.StoredEntry(new byte[] { 1, 2, 3 }));
        var viaFile = HqrFile.Parse(original);
        viaFile.SetStored(5, new byte[] { 1, 2, 3 });
        Check(viaWriter.AsSpan().SequenceEqual(viaFile.ToBytes()), "HqrWriter.ReplaceEntry and HqrFile.SetStored write the same file");
    }

    private static byte[] BuildWithAlias()
    {
        // slots: 0 = {1}, 1 = {7,7,7}, 2 = same data as 1
        var e0 = HqrWriter.StoredEntry(new byte[] { 1 });
        var e1 = HqrWriter.StoredEntry(new byte[] { 7, 7, 7 });
        var table = 4 * 4;
        var o0 = table; var o1 = o0 + e0.Length; var end = o1 + e1.Length;
        var bytes = new byte[end];
        void W(int at, int v) => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), v);
        W(0, o0); W(4, o1); W(8, o1); W(12, end);
        e0.CopyTo(bytes, o0); e1.CopyTo(bytes, o1);
        return bytes;
    }

    private static void Transactions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lba_tx_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.bin"); var b = Path.Combine(dir, "b.bin");
            File.WriteAllBytes(a, new byte[] { 1 }); File.WriteAllBytes(b, new byte[] { 2 });

            new FileTransaction().Write(a, new byte[] { 10 }).Write(b, new byte[] { 20 }).Commit();
            Check(File.ReadAllBytes(a)[0] == 10 && File.ReadAllBytes(b)[0] == 20, "commit writes every file");
            Check(File.ReadAllBytes(a + ".bak")[0] == 1 && File.ReadAllBytes(b + ".bak")[0] == 2, "the first commit keeps a .bak of each");

            new FileTransaction().Write(a, new byte[] { 11 }).Commit();
            Check(File.ReadAllBytes(a + ".bak")[0] == 1, "later commits leave the .bak alone");

            var failed = false;
            try { new FileTransaction().Write(a, new byte[] { 99 }).Write(b, new byte[] { 98 }, _ => "rejected by the check.").Commit(); }
            catch (InvalidDataException) { failed = true; }
            Check(failed && File.ReadAllBytes(a)[0] == 11 && File.ReadAllBytes(b)[0] == 20, "a failed verification changes no file");
            Check(!File.Exists(a + ".tmp") && !File.Exists(b + ".tmp"), "no temporary files are left behind");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Every retail LBA1 grid must survive a full re-encode of every column with all cells, collision codes of empty
    // cells included, and keep its used-blocks bitmap.
    private static void GridRoundTrip()
    {
        var archive = HqrArchive.Open(Path.Combine(Lba1Dir, "LBA_GRI.HQR.bak"));
        int grids = 0;
        for (var i = 0; i < 120; i++)
        {
            if (!archive.IsValid(i)) continue;
            grids++;
            var grid = archive.Read(i);
            var cells = LBAAssembler.Lba1.Lba1GridCodec.Decode(grid);
            var edits = new List<LBAAssembler.Lba1.Lba1GridCell>();
            for (var z = 0; z < 64; z++)
                for (var x = 0; x < 64; x++)
                {
                    var b = (z * 64 + x) * 25 * 2;
                    edits.Add(new LBAAssembler.Lba1.Lba1GridCell(x, 0, z, cells[b], cells[b + 1]));
                }
            var rewritten = LBAAssembler.Lba1.Lba1GridEdit.SetCells(grid, edits);
            Check(LBAAssembler.Lba1.Lba1GridCodec.Decode(rewritten).AsSpan().SequenceEqual(cells), $"grid {i}: every cell, collision code of empty cells included, survives a full re-encode");
            Check(grid.AsSpan(grid.Length - 32).SequenceEqual(rewritten.AsSpan(rewritten.Length - 32)), $"grid {i}: the used-blocks bitmap is unchanged");
            for (var c = 0; c < 4096; c++) if (LBAAssembler.Lba1.Lba1GridCodec.ColumnCells(rewritten, c % 64, c / 64) != 25) { Check(false, $"grid {i}: column {c} doesn't hold 25 cells"); break; }
        }
        Console.WriteLine($"grid round trip: {grids} grids re-encoded");
    }

    // Every retail scene must survive parse -> write byte for byte.
    private static void ModelRoundTrip()
    {
        GridRoundTrip();
        Scenes(SceneGame.Lba1, Path.Combine(Lba1Dir, "SCENE.HQR.bak"), firstEntry: 0);
        Scenes(SceneGame.Lba2, Path.Combine(Lba2Dir, "SCENE.HQR"), firstEntry: 1);
    }

    private static void Scenes(SceneGame game, string path, int firstEntry)
    {
        var archive = HqrArchive.Open(path);
        var count = HqrArchive.CountEntries(path);
        int scenes = 0, actors = 0, zones = 0, points = 0, tail = 0;
        for (var i = firstEntry; i < count; i++)
        {
            if (!archive.IsValid(i)) continue;
            var record = archive.Read(i);
            scenes++;
            SceneModel model;
            try { model = SceneSerializer.Parse(game, record); }
            catch (Exception e) { Check(false, $"{game} entry {i}: parse failed: {e.Message}"); continue; }
            actors += model.Actors.Count; zones += model.Zones.Count; points += model.TrackPoints.Count; tail += model.Tail.Length;
            byte[] written;
            try { written = SceneSerializer.Write(model); }
            catch (Exception e) { Check(false, $"{game} entry {i}: write failed: {e.Message}"); continue; }
            Check(written.AsSpan().SequenceEqual(record), $"{game} entry {i}: written record differs ({record.Length} -> {written.Length} bytes)");

            var copy = model.Clone();
            Check(SceneSerializer.Write(copy).AsSpan().SequenceEqual(record), $"{game} entry {i}: a clone writes the same record");
            copy.Actors[0].X += 1;
            Check(model.Actors[0].X != copy.Actors[0].X, $"{game} entry {i}: editing a clone leaves the original alone");
        }
        Console.WriteLine($"model {game}: {scenes} scenes, {actors} actors (hero included), {zones} zones, {points} track points, {tail} tail bytes");
    }
}

// What the LBA2 patch table points at (see SceneModel.Tail).
internal static class PatchStudy
{
    public static int Run()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer", "SCENE.HQR");
        var archive = HqrArchive.Open(path);
        var count = HqrArchive.CountEntries(path);
        var histogram = new SortedDictionary<string, int>();
        var instrCounts = new SortedDictionary<string, int>();
        int scenes = 0, patches = 0, badTail = 0;
        for (var i = 1; i < count; i++)
        {
            if (!archive.IsValid(i)) continue;
            var record = archive.Read(i);
            var model = SceneSerializer.Parse(SceneGame.Lba2, record);
            var rec = LBAAssembler.LbaScript.SceneRecord.Parse(record);
            scenes++;
            var n = BitConverter.ToInt32(model.Tail, 0);
            if (model.Tail.Length != 4 + 4 * n) { badTail++; Console.WriteLine($"scene {i - 1}: tail {model.Tail.Length} bytes but count {n}"); continue; }
            var tailStart = record.Length - model.Tail.Length;

            // instruction start -> op, per blob
            var decoded = new List<(int Start, int Len, LBAAssembler.LbaScript.ScriptKind Kind, int Actor, Dictionary<int, int> Ops)>();
            foreach (var a in rec.Actors)
            {
                foreach (var kind in new[] { LBAAssembler.LbaScript.ScriptKind.Life, LBAAssembler.LbaScript.ScriptKind.Track })
                {
                    var pos = kind == LBAAssembler.LbaScript.ScriptKind.Life ? a.LifePos : a.TrackPos;
                    var len = kind == LBAAssembler.LbaScript.ScriptKind.Life ? a.LifeLen : a.TrackLen;
                    var code = (kind == LBAAssembler.LbaScript.ScriptKind.Life ? rec.Life(a) : rec.Track(a)).ToArray();
                    var ops = new Dictionary<int, int>();
                    try
                    {
                        var ins = kind == LBAAssembler.LbaScript.ScriptKind.Life ? LBAAssembler.LbaScript.Bytecode.DecodeLife(code) : LBAAssembler.LbaScript.Bytecode.DecodeTrack(code);
                        foreach (var x in ins) ops[x.Offset] = x.Op;
                    }
                    catch { }
                    decoded.Add((pos, len, kind, a.Index, ops));
                }
            }
            foreach (var d in decoded)
                foreach (var op in d.Ops.Values.Where(o => d.Kind == LBAAssembler.LbaScript.ScriptKind.Life))
                {
                    var name = LBAAssembler.LbaScript.Opcodes.Life((byte)op)?.Name ?? $"op{op}";
                    if (name is "SWIF" or "SNIF" or "ONEIF" or "NEVERIF" or "OR_IF" or "IF" or "SET_VAR_CUBE" or "SET_VAR_GAME") instrCounts[name] = instrCounts.GetValueOrDefault(name) + 1;
                }

            for (var k = 0; k < n; k++)
            {
                var size = BitConverter.ToInt16(model.Tail, 4 + k * 4);
                var offset = BitConverter.ToInt16(model.Tail, 4 + k * 4 + 2);
                patches++;
                string key;
                var hit = decoded.FirstOrDefault(d => offset >= d.Start && offset < d.Start + d.Len);
                if (hit.Ops is null) key = $"outside every script (size {size})";
                else
                {
                    var rel = offset - hit.Start;
                    if (hit.Ops.TryGetValue(rel, out var op))
                        key = $"{hit.Kind} opcode {(hit.Kind == LBAAssembler.LbaScript.ScriptKind.Life ? LBAAssembler.LbaScript.Opcodes.Life((byte)op)?.Name : LBAAssembler.LbaScript.Opcodes.Track((byte)op)?.Name)} size {size}";
                    else
                    {
                        var before = hit.Ops.Keys.Where(o => o < rel).DefaultIfEmpty(-1).Max();
                        var name = before >= 0 ? (hit.Kind == LBAAssembler.LbaScript.ScriptKind.Life ? LBAAssembler.LbaScript.Opcodes.Life((byte)hit.Ops[before])?.Name : LBAAssembler.LbaScript.Opcodes.Track((byte)hit.Ops[before])?.Name) : "?";
                        key = $"{hit.Kind} inside an instruction: operand of {name}, +{rel - before} size {size}";
                    }
                }
                histogram[key] = histogram.GetValueOrDefault(key) + 1;
            }
        }
        Console.WriteLine($"{scenes} scenes, {patches} patches, {badTail} bad tails");
        foreach (var kv in histogram.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
        Console.WriteLine("life instructions of interest: " + string.Join(", ", instrCounts.Select(k => $"{k.Key}={k.Value}")));
        return 0;
    }
}

// The validator over every retail scene: retail must produce no errors; the warnings are listed to judge the rules.
internal static class ValidatorStudy
{
    public static int Run()
    {
        var total = 0;
        foreach (var (game, path, first) in new[]
        {
            (SceneGame.Lba1, Path.Combine(Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure", "SCENE.HQR.bak"), 0),
            (SceneGame.Lba2, Path.Combine(Environment.GetEnvironmentVariable("LBA2_DIR") ?? @"E:\GOG Games\Little Big Adventure 2 - Level viewer", "SCENE.HQR"), 1),
        })
        {
            var archive = HqrArchive.Open(path);
            var count = HqrArchive.CountEntries(path);
            var sceneCount = count - first;
            int errors = 0, warnings = 0, scenes = 0;
            var kinds = new SortedDictionary<string, int>();
            for (var i = first; i < count; i++)
            {
                if (!archive.IsValid(i)) continue;
                scenes++;
                var model = SceneSerializer.Parse(game, archive.Read(i));
                var issues = SceneValidator.Validate(model, new SceneValidationOptions { SceneCount = sceneCount });
                foreach (var issue in issues)
                {
                    if (issue.Severity == SceneIssueSeverity.Error) { errors++; if (errors <= 15) Console.WriteLine($"  {game} scene {i - first}: {issue}"); }
                    else
                    {
                        warnings++;
                        var key = System.Text.RegularExpressions.Regex.Replace(issue.Where.Split(' ')[0] + ": " + issue.Message, @"\d+", "#");
                        kinds[key] = kinds.GetValueOrDefault(key) + 1;
                    }
                }
            }
            Console.WriteLine($"{game}: {scenes} scenes, {errors} errors, {warnings} warnings");
            foreach (var kv in kinds.OrderByDescending(k => k.Value).Take(12)) Console.WriteLine($"    {kv.Value,5}  {kv.Key}");
            total += errors;
        }
        return total == 0 ? 0 : 1;
    }
}

// The grid validator over every retail LBA1 grid (needs LBA_BLL.HQR and LBA_BRK.HQR from the same folder).
internal static class GridValidatorStudy
{
    public static int Run()
    {
        var dir = Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure";
        var grids = HqrArchive.Open(Path.Combine(dir, "LBA_GRI.HQR.bak"));
        var libraries = HqrArchive.Open(Path.Combine(dir, "LBA_BLL.HQR"));
        var bricks = HqrArchive.Open(Path.Combine(dir, "LBA_BRK.HQR"));
        var sizes = new Dictionary<int, int>();
        int SizeOf(int b) => sizes.TryGetValue(b, out var s) ? s : sizes[b] = bricks.DecodedSize(b);
        int errors = 0, warnings = 0, scenes = 0;
        long biggest = 0;
        var kinds = new SortedDictionary<string, int>();
        for (var i = 0; i < 120; i++)
        {
            if (!grids.IsValid(i) || !libraries.IsValid(i)) continue;
            scenes++;
            var report = LBAAssembler.Lba1.Lba1GridValidator.Validate(grids.Read(i), libraries.Read(i), SizeOf);
            biggest = Math.Max(biggest, report.BrickBytes);
            foreach (var issue in report.Issues)
            {
                if (issue.Severity == SceneIssueSeverity.Error) { errors++; if (errors <= 15) Console.WriteLine($"  grid {i}: {issue}"); }
                else { warnings++; var key = System.Text.RegularExpressions.Regex.Replace(issue.Where + ": " + issue.Message, @"\d+", "#"); kinds[key] = kinds.GetValueOrDefault(key) + 1; }
            }
        }
        Console.WriteLine($"grids: {scenes} checked, {errors} errors, {warnings} warnings; the largest brick load is {biggest} of {LBAAssembler.Lba1.Lba1GridValidator.MaxBrickBytes} bytes");
        foreach (var kv in kinds.OrderByDescending(k => k.Value).Take(10)) Console.WriteLine($"    {kv.Value,5}  {kv.Key}");
        return errors == 0 ? 0 : 1;
    }
}

// Empty cells (block 0) whose second byte is not 0: WorldColBrick returns that byte as the cell's collision code.
internal static class EmptyPosStudy
{
    public static int Run()
    {
        var dir = Environment.GetEnvironmentVariable("LBA1_DIR") ?? @"E:\GOG Games\Little Big Adventure";
        var grids = HqrArchive.Open(Path.Combine(dir, "LBA_GRI.HQR.bak"));
        long empties = 0, coded = 0;
        var codes = new SortedDictionary<int, long>();
        var perGrid = new List<string>();
        for (var i = 0; i < 120; i++)
        {
            if (!grids.IsValid(i)) continue;
            var cells = LBAAssembler.Lba1.Lba1GridCodec.Decode(grids.Read(i));
            long c = 0;
            for (var k = 0; k < cells.Length; k += 2)
            {
                if (cells[k] != 0) continue;
                empties++;
                if (cells[k + 1] != 0) { coded++; c++; codes[cells[k + 1]] = codes.GetValueOrDefault(cells[k + 1]) + 1; }
            }
            if (c > 0 && (i == 13 || perGrid.Count < 6)) perGrid.Add($"grid {i}: {c}");
        }
        Console.WriteLine($"empty cells: {empties}, of which with a non-zero second byte: {coded}");
        Console.WriteLine("  codes: " + string.Join(", ", codes.Select(k => $"{k.Key}:{k.Value}")));
        Console.WriteLine("  " + string.Join("; ", perGrid));
        return 0;
    }
}

// Cells that differ between two versions of one grid.
internal static class GridDiffStudy
{
    public static int Run(string[] args)
    {
        var a = LBAAssembler.Lba1.Lba1GridCodec.Decode(HqrArchive.Open(args[1]).Read(int.Parse(args[3])));
        var b = LBAAssembler.Lba1.Lba1GridCodec.Decode(HqrArchive.Open(args[2]).Read(int.Parse(args[3])));
        int changed = 0, lostCodes = 0, newCodes = 0;
        for (var z = 0; z < 64; z++) for (var x = 0; x < 64; x++) for (var y = 0; y < 25; y++)
        {
            var i = ((z * 64 + x) * 25 + y) * 2;
            if (a[i] == b[i] && a[i + 1] == b[i + 1]) continue;
            changed++;
            if (a[i] == 0 && a[i + 1] != 0 && !(b[i] == 0 && b[i + 1] == a[i + 1])) { lostCodes++; if (lostCodes <= 12) Console.WriteLine($"  lost code at x{x} y{y} z{z}: was ({a[i]},{a[i + 1]}) now ({b[i]},{b[i + 1]})"); }
            if (b[i] == 0 && b[i + 1] != 0 && a[i + 1] != b[i + 1]) newCodes++;
        }
        Console.WriteLine($"cells changed {changed}; collision codes of empty cells lost {lostCodes}, new {newCodes}");
        return 0;
    }
}
