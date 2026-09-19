using LBA2LevelEditor;
using LBA2LevelEditor.LbaScript;

namespace ScriptRoundTrip;

// Corpus-wide structural facts about the shipped scripts. The C-style
// translation is designed against these numbers (what the original authoring
// tool actually emits) rather than against assumptions about how DoLife runs.
internal static class Facts
{
    private static readonly SortedDictionary<string, int> counters = new();
    private static readonly Dictionary<string, List<string>> samples = new();

    private static void Count(string key, string? sample = null)
    {
        counters[key] = counters.GetValueOrDefault(key) + 1;
        if (sample is null) return;
        if (!samples.TryGetValue(key, out var l)) samples[key] = l = new();
        if (l.Count < 4) l.Add(sample);
    }

    public static int Run(HqrArchive archive)
    {
        foreach (var (scene, rec) in Program.Scenes(archive))
        {
            var lives = new List<List<Instr>>();
            var tracks = new List<List<Instr>>();
            foreach (var a in rec.Actors)
            {
                lives.Add(Bytecode.DecodeLife(rec.Life(a)));
                tracks.Add(Bytecode.DecodeTrack(rec.Track(a)));
            }

            for (var ai = 0; ai < rec.Actors.Count; ai++)
            {
                var who = $"scene {scene} actor {ai}";
                Track(who, tracks[ai], rec.Track(rec.Actors[ai]).Length);
                Life(who, lives[ai], rec.Life(rec.Actors[ai]).Length, ai, lives, tracks);
            }
        }

        foreach (var (k, v) in counters)
        {
            Console.WriteLine($"{v,8}  {k}");
            if (samples.TryGetValue(k, out var s)) foreach (var x in s) Console.WriteLine($"            e.g. {x}");
        }
        return 0;
    }

    // -------------------------------------------------------------------
    private static void Track(string who, List<Instr> code, int length)
    {
        Count(code.Count > 0 && code[^1].Op == 0 ? "track: ends with END" : "track: does NOT end with END", who);

        var byOffset = code.ToDictionary(i => i.Offset);
        var labelIds = new Dictionary<int, int>();
        foreach (var i in code)
        {
            switch (i.Op)
            {
                case 9: // LABEL
                    labelIds[(int)i.A[0]] = labelIds.GetValueOrDefault((int)i.A[0]) + 1;
                    break;
                case 13: // WAIT_NB_ANIM counter
                    Count(i.A[1] == 0 ? "track hidden: WAIT_NB_ANIM counter==0" : "track hidden: WAIT_NB_ANIM counter!=0", who);
                    break;
                case 18: case 36: case 39: case 49:
                    Count(i.A[1] == 0 ? "track hidden: WAIT_NB_* timer==0" : "track hidden: WAIT_NB_* timer!=0", who);
                    break;
                case 34:
                    Count(i.A[1] == -1 ? "track hidden: ANGLE_RND state==-1" : "track hidden: ANGLE_RND state!=-1", who + $" state={i.A[1]}");
                    break;
                case 6:
                    Count(i.A[1] == i.A[0] ? "track hidden: LOOP counter==count" : "track hidden: LOOP counter!=count", who + $" count={i.A[0]} counter={i.A[1]}");
                    break;
                case 33:
                    Count(i.A[0] == -1 ? "track: FACE_TWINSEN angle==-1" : "track: FACE_TWINSEN angle!=-1", who + $" angle={i.A[0]}");
                    break;
                case 7:
                    Count((i.A[0] & 0x8000) == 0 && i.A[0] >= 0 ? "track: ANGLE in 0..32767" : "track: ANGLE out of range", who + $" angle={i.A[0]}");
                    break;
            }

            if (Bytecode.TryGetTarget(ScriptKind.Track, i, out var t))
            {
                var name = Opcodes.Track(i.Op)!.Name;
                if (t == length) Count($"track: {name} targets end of script", who);
                else
                {
                    var target = byOffset[t];
                    Count(target.Op == 9 ? $"track: {name} targets a LABEL" : $"track: {name} targets a non-LABEL (op {Opcodes.Track(target.Op)!.Name})", who);
                    Count(t <= i.Offset ? $"track: {name} jumps backward" : $"track: {name} jumps forward");
                }
            }
        }
        foreach (var (id, n) in labelIds)
            if (n > 1) Count("track: DUPLICATE label id within one track", $"{who} label {id} x{n}");
        Count(labelIds.Count > 0 ? "track: has LABELs" : "track: has no LABELs");
    }

    // -------------------------------------------------------------------
    private static bool IsChainPrefix(byte op) => op is 55 or 112;              // OR_IF, AND_IF
    private static bool IsTerminal(byte op) => op is 12 or 13 or 14 or 2 or 4;  // IF SWIF ONEIF SNIF NEVERIF

    private static void Life(string who, List<Instr> code, int length, int actor, List<List<Instr>> lives, List<List<Instr>> tracks)
    {
        if (code.Count == 0) { Count("life: empty script"); return; }
        Count(code[^1].Op == 0 ? "life: ends with END" : "life: does NOT end with END", who);
        Count(code.Count >= 2 && code[^2].Op == 35 ? "life: END preceded by END_COMPORTEMENT" : "life: END NOT preceded by END_COMPORTEMENT", who);

        var byOffset = code.ToDictionary(i => i.Offset);
        var indexByOffset = code.Select((ins, ix) => (ins.Offset, ix)).ToDictionary(p => p.Offset, p => p.ix);
        var blockStarts = BlockStarts(code);

        // ---- block-level facts
        foreach (var i in code)
        {
            if (i.Op == 32) Count("life: COMPORTEMENT header opcode present", who);
            if (i.Op is 41 or 0 && i != code[^1]) Count($"life: {Opcodes.Life(i.Op)!.Name} occurs mid-script (not the final END)");
        }

        // ---- cross references
        foreach (var i in code)
        {
            switch (i.Op)
            {
                case 33: // SET_COMPORTEMENT (own)
                    Count(blockStarts.Contains((int)i.A[0]) ? "life: SET_COMPORTEMENT targets a block start" : "life: SET_COMPORTEMENT targets a NON-block-start", who + $" -> {i.A[0]}");
                    break;
                case 34: // SET_COMPORTEMENT_OBJ
                {
                    var obj = (int)i.A[0];
                    if (obj >= lives.Count) { Count("life: SET_COMPORTEMENT_OBJ obj out of range", who); break; }
                    Count(BlockStarts(lives[obj]).Contains((int)i.A[1]) ? "life: SET_COMPORTEMENT_OBJ targets a block start of obj" : "life: SET_COMPORTEMENT_OBJ targets a NON-block-start of obj", who + $" obj {obj} -> {i.A[1]}");
                    break;
                }
                case 23: // SET_TRACK (own track)
                    TrackTarget("SET_TRACK", tracks[actor], (int)i.A[0], who);
                    break;
                case 24:
                {
                    var obj = (int)i.A[0];
                    if (obj >= tracks.Count) { Count("life: SET_TRACK_OBJ obj out of range", who); break; }
                    TrackTarget("SET_TRACK_OBJ", tracks[obj], (int)i.A[1], who + $" obj {obj}");
                    break;
                }
                case 3: // OFFSET
                    Count((int)i.A[0] <= i.Offset ? "life: OFFSET jumps backward (loop)" : "life: OFFSET jumps forward", who);
                    break;
                case 117: // BREAK
                    Count(byOffset.TryGetValue((int)i.A[0], out var bt) && bt.Op == 118 ? "life: BREAK targets END_SWITCH" : "life: BREAK targets something else", who);
                    break;
            }
        }

        // ---- condition chains
        for (var ix = 0; ix < code.Count; ix++)
        {
            var i = code[ix];
            if (IsChainPrefix(i.Op) || IsTerminal(i.Op))
            {
                // find the terminal of this chain
                var j = ix;
                while (j < code.Count && IsChainPrefix(code[j].Op)) j++;
                if (j >= code.Count || !IsTerminal(code[j].Op))
                {
                    Count("life: chain prefix not followed by a terminal IF", who);
                    continue;
                }
                if (ix != j && (ix == 0 || !IsChainPrefix(code[ix - 1].Op))) // start of a multi-cond chain
                {
                    var term = code[j];
                    var after = term.Offset + term.Length;
                    for (var k = ix; k < j; k++)
                    {
                        var p = code[k];
                        if (p.Op == 112) Count(p.Target == term.Target ? "life chain: AND_IF targets same F as terminal" : "life chain: AND_IF targets DIFFERENT than terminal", who);
                        else Count(p.Target == after ? "life chain: OR_IF targets body start (after terminal)" : "life chain: OR_IF targets elsewhere", who + $" @{p.Offset}: target {p.Target}, body start {after}");
                    }
                    Count($"life chain: length {j - ix + 1}");
                }
                ix = j;
                continue;
            }
        }

        // ---- IF family shape
        for (var ix = 0; ix < code.Count; ix++)
        {
            var i = code[ix];
            if (!IsTerminal(i.Op)) continue;
            var name = Opcodes.Life(i.Op)!.Name;
            if (i.Target <= i.Offset) { Count($"life: {name} jumps backward", who); continue; }
            if (i.Target == length) { Count($"life: {name} targets end of script", who); continue; }
            if (!indexByOffset.TryGetValue(i.Target, out var fi)) { Count($"life: {name} bad target", who); continue; }
            var prev = code[fi - 1];
            if (prev.Op == 15 && fi - 1 > ix)
            {
                Bytecode.TryGetTarget(ScriptKind.Life, prev, out var elseEnd);
                Count(elseEnd > i.Target ? $"life: {name} if/else, else-end forward" : $"life: {name} F preceded by ELSE with backward end", who);
                var endOp = elseEnd == length ? "end-of-script" : Opcodes.Life(byOffset[elseEnd].Op)!.Name;
                Count($"life: if/else else-end lands on {endOp}");
            }
            else if (prev.Op == 3 && (int)prev.A[0] <= code[ix].Offset)
                Count($"life: {name} F preceded by backward OFFSET (while loop)", who);
            else
                Count($"life: {name} plain-if");
        }

        // ---- ELSE pairing: every ELSE is the instruction before some IF's F
        var falseTargets = new HashSet<int>(code.Where(c => IsTerminal(c.Op)).Select(c => c.Target));
        foreach (var i in code)
            if (i.Op == 15)
                Count(falseTargets.Contains(i.Offset + i.Length) ? "life: ELSE paired with an IF" : "life: ELSE unpaired (no IF targets the next instruction)", who + $" @{i.Offset}");

        // ---- switch shape
        for (var ix = 0; ix < code.Count; ix++)
        {
            if (code[ix].Op != 113) continue;
            Count("life: SWITCH");
            var j = ix + 1;
            var sawDefault = false;
            var depth = 0;
            for (; j < code.Count; j++)
            {
                var op = code[j].Op;
                if (op == 113) depth++;
                if (op == 118) { if (depth == 0) break; depth--; }
                if (op == 116) sawDefault = true;
            }
            Count(j < code.Count ? "life: SWITCH has matching END_SWITCH" : "life: SWITCH without END_SWITCH", who);
            Count(sawDefault ? "life: SWITCH has DEFAULT" : "life: SWITCH without DEFAULT");
        }
        foreach (var i in code)
            if (i.Op is 115 or 114)
            {
                if (i.Target <= i.Offset) Count("life: CASE jumps backward", who);
                else if (i.Target == length) Count("life: CASE targets end of script", who);
                else
                {
                    var tgt = byOffset[i.Target];
                    Count($"life: {Opcodes.Life(i.Op)!.Name} target op {Opcodes.Life(tgt.Op)!.Name}");
                }
            }
    }

    // Offsets where a COMPORTEMENT block begins: 0, and the instruction after
    // every END_COMPORTEMENT.
    private static HashSet<int> BlockStarts(List<Instr> code)
    {
        var s = new HashSet<int> { 0 };
        for (var i = 0; i + 1 < code.Count; i++)
            if (code[i].Op == 35) s.Add(code[i + 1].Offset);
        return s;
    }

    private static void TrackTarget(string name, List<Instr> track, int offset, string who)
    {
        var ins = track.FirstOrDefault(t => t.Offset == offset);
        if (ins is null) Count($"life: {name} target is not an instruction boundary", who + $" -> {offset}");
        else Count(ins.Op == 9 ? $"life: {name} targets a track LABEL" : $"life: {name} targets non-LABEL {Opcodes.Track(ins.Op)!.Name}", who + $" -> {offset}");
    }
}

// Extra probes for the irregular corners (kept separate so `facts` output stays scannable).
internal static class Odd
{
    public static int Run(HqrArchive archive)
    {
        var tailLens = new SortedDictionary<int, int>();
        var tailOps = new SortedDictionary<string, int>();
        var shown = 0;
        foreach (var (scene, rec) in Program.Scenes(archive))
        {
            var lives = rec.Actors.Select(a => Bytecode.DecodeLife(rec.Life(a))).ToList();
            for (var ai = 0; ai < lives.Count; ai++)
            {
                var code = lives[ai];
                var lastEc = code.FindLastIndex(i => i.Op == 35);
                var tail = code.Skip(lastEc + 1).SkipLast(1).ToList(); // between last END_COMPORTEMENT and the final END
                tailLens[tail.Count] = tailLens.GetValueOrDefault(tail.Count) + 1;
                if (tail.Count > 0)
                {
                    var key = string.Join(" ", tail.Take(6).Select(t => Opcodes.Life(t.Op)!.Name));
                    tailOps[key] = tailOps.GetValueOrDefault(key) + 1;
                }

                // non-block-start SET_COMPORTEMENT
                var starts = new HashSet<int> { 0 };
                for (var i = 0; i + 1 < code.Count; i++) if (code[i].Op == 35) starts.Add(code[i + 1].Offset);
                foreach (var ins in code.Where(c => c.Op == 33 && !starts.Contains((int)c.A[0])))
                {
                    var tgt = code.FirstOrDefault(c => c.Offset == (int)ins.A[0]);
                    if (shown++ < 12)
                        Console.WriteLine($"non-block SET_COMPORTEMENT: scene {scene} actor {ai} @{ins.Offset} -> {ins.A[0]} ({(tgt is null ? "NOT an instruction" : Opcodes.Life(tgt.Op)!.Name)}) blocks={string.Join(",", starts.Order())}");
                }
            }
        }
        Console.WriteLine("instructions between last END_COMPORTEMENT and final END, by count:");
        foreach (var (k, v) in tailLens) Console.WriteLine($"  {k,3} instrs: {v} scripts");
        Console.WriteLine("most common non-empty tails:");
        foreach (var (k, v) in tailOps.OrderByDescending(p => p.Value).Take(12)) Console.WriteLine($"  {v,5}  {k}");
        return 0;
    }
}
