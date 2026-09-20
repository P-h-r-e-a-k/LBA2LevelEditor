namespace LBA2LevelEditor.LbaScript;

// Scene is -1 until a caller that knows which scene the script belongs to fills it in.
public sealed record ScriptDiagnostic(int Actor, ScriptKind Kind, int Line, int Column, string Message, int Scene = -1)
{
    public override string ToString() =>
        $"{(Scene >= 0 ? $"scene {Scene}, " : "")}actor {Actor} {Kind.ToString().ToLowerInvariant()} script{(Line > 0 ? $", line {Line}" : "")}: {Message}";
}

// Comments: for every script that was recompiled, the comment set to store for it
// (null = it has no comments, so any stored set should be removed).
public sealed record SceneBuildResult(byte[]? Record, IReadOnlyList<ScriptDiagnostic> Errors,
    IReadOnlyDictionary<(int Actor, ScriptKind Kind), CommentSet?>? Comments = null)
{
    public bool Ok => Record is not null && Errors.Count == 0;
}

// All of one scene's life and track scripts as C text, plus the machinery to
// turn edited text back into a scene record.
//
// Scripts in a scene point at each other by raw byte offset (SET_TRACK_OBJ into
// another actor's track script, SET_COMPORTEMENT_OBJ into another's life
// script, SET_TRACK into its own). The text uses names instead, so:
//   * an edited script is recompiled from its text and its names are resolved
//     against the *current* layout of whatever it points at;
//   * an unedited script is never recompiled -- its original bytes are kept and
//     only those offset operands are re-pointed if their target moved (so an
//     untouched script stays byte-identical unless it has to change);
//   * a name that no longer exists (the target script was edited and lost it)
//     is reported as an error on the script that refers to it.
public sealed class SceneScripts
{
    private sealed class Entry
    {
        public required byte[] Original { get; init; }
        public required Dictionary<int, string> OriginalNames { get; init; }
        public string? OriginalText;
        public int Detached;            // stored comments that lost their statement (see CommentWeaver)
        public string? EditedText;
        public bool ForceRecompile;
        public CompiledScript? Compiled;
        public bool Edited => EditedText is not null || ForceRecompile;
    }

    private readonly SceneRecord record;
    private readonly OpcodeSet dialect;
    private readonly int sceneNumber;
    private readonly Func<int, ScriptKind, CommentSet?> commentSource;
    private readonly Entry[] life;
    private readonly Entry[] track;
    private readonly Symbols symbols;

    public int ActorCount => life.Length;

    internal OpcodeSet Dialect => dialect;

    private IDisposable Scope() => Opcodes.Use(dialect);

    private SceneScripts(SceneRecord record, OpcodeSet dialect, int sceneNumber, Func<int, ScriptKind, CommentSet?>? comments)
    {
        this.record = record;
        this.dialect = dialect;
        this.sceneNumber = sceneNumber;
        using var scope = Scope();
        commentSource = comments ?? ((_, _) => null);
        var n = record.Actors.Count;
        life = new Entry[n];
        track = new Entry[n];
        for (var i = 0; i < n; i++)
        {
            var a = record.Actors[i];
            var lifeBytes = record.Life(a).ToArray();
            var trackBytes = record.Track(a).ToArray();
            life[i] = new Entry { Original = lifeBytes, OriginalNames = LifeText.FunctionNames(Bytecode.DecodeLife(lifeBytes)) };
            track[i] = new Entry { Original = trackBytes, OriginalNames = TrackText.LabelNames(Bytecode.DecodeTrack(trackBytes)) };
        }
        symbols = new Symbols(this);
    }

    // `comments` supplies the stored comments for (actor, kind) of this scene, if any.
    public static SceneScripts Load(byte[] sceneRecordBytes, int sceneNumber = -1, Func<int, ScriptKind, CommentSet?>? comments = null, bool lba1 = false) =>
        lba1 ? new(SceneRecord.ParseLba1(sceneRecordBytes), Opcodes.Lba1, sceneNumber, comments)
             : new(SceneRecord.Parse(sceneRecordBytes), Opcodes.Lba2, sceneNumber, comments);

    private Entry EntryOf(int actor, ScriptKind kind)
    {
        if ((uint)actor >= (uint)life.Length) throw new ArgumentOutOfRangeException(nameof(actor));
        return kind == ScriptKind.Life ? life[actor] : track[actor];
    }

    // ---- text ---------------------------------------------------------------

    // The script as C text: the user's edit if there is one, otherwise the
    // (cached) decompilation of the original bytes.
    public string GetText(int actor, ScriptKind kind)
    {
        var e = EntryOf(actor, kind);
        return e.EditedText ?? OriginalText(actor, kind);
    }

    public string OriginalText(int actor, ScriptKind kind)
    {
        var e = EntryOf(actor, kind);
        if (e.OriginalText is not null) return e.OriginalText;
        using var scope = Scope();
        var header = Header(actor, kind);
        var decompiled = kind == ScriptKind.Life
            ? LifeText.DecompileMapped(e.Original, actor, symbols, header)
            : TrackText.DecompileMapped(e.Original, header);
        var woven = CommentWeaver.Apply(decompiled, commentSource(actor, kind), kind);
        e.Detached = woven.Detached;
        e.OriginalText = woven.Text;
        return e.OriginalText;
    }

    // The generated first line of a script's text. Regenerated on every load, so
    // it is never stored as a comment.
    private string Header(int actor, ScriptKind kind) =>
        $"{(sceneNumber >= 0 ? $"scene {sceneNumber}, " : "")}actor {actor} - {kind.ToString().ToLowerInvariant()} script";

    // Stored comments that could not be placed because their statement is gone
    // (they are listed at the end of the script text instead).
    public int DetachedComments(int actor, ScriptKind kind)
    {
        OriginalText(actor, kind);
        return EntryOf(actor, kind).Detached;
    }

    // Stores edited text. Text identical to the original decompilation counts
    // as "not edited", so simply opening and closing a script changes nothing.
    public void SetText(int actor, ScriptKind kind, string text)
    {
        var e = EntryOf(actor, kind);
        e.Compiled = null;
        e.EditedText = text == OriginalText(actor, kind) ? null : text;
    }

    public void RevertText(int actor, ScriptKind kind)
    {
        var e = EntryOf(actor, kind);
        e.EditedText = null;
        e.ForceRecompile = false;
        e.Compiled = null;
    }

    public bool IsEdited(int actor, ScriptKind kind) => EntryOf(actor, kind).Edited;

    public bool HasEdits => life.Any(e => e.Edited) || track.Any(e => e.Edited);

    // Size in bytes of the script as currently stored in the scene record.
    public int OriginalSize(int actor, ScriptKind kind) => EntryOf(actor, kind).Original.Length;

    // Testing aid: treat a script as edited even if its text is unchanged, so
    // the full compile path (rather than "keep the original bytes") is exercised.
    public void ForceRecompile(int actor, ScriptKind kind) => EntryOf(actor, kind).ForceRecompile = true;

    // ---- checking one script on its own ---------------------------------------

    // Compiles `text` for `actor` against the current state of the rest of the
    // scene and returns the size it would occupy, or the first error. Does not
    // change any stored state.
    public (int Size, ScriptDiagnostic? Error) CheckText(int actor, ScriptKind kind, string text)
    {
        var r = CheckTextFull(actor, kind, text);
        return (r.Size, r.Error);
    }

    // As CheckText, plus the compiler's warnings (which don't stop it compiling).
    public (int Size, ScriptDiagnostic? Error, IReadOnlyList<ScriptDiagnostic> Warnings) CheckTextFull(int actor, ScriptKind kind, string text)
    {
        using var scope = Scope();
        try
        {
            CompiledScript c;
            if (kind == ScriptKind.Track) c = TrackText.Compile(text);
            else
            {
                c = LifeText.Compile(text, actor, symbols);
                c.ResolveExternals(r => LifeText.ResolveExternal(r, actor, symbols, c));
            }
            var warnings = c.Warnings.Select(w => new ScriptDiagnostic(actor, kind, w.Line, w.Column, w.Message, sceneNumber)).ToList();
            return (c.Bytes.Length, null, warnings);
        }
        catch (ScriptCompileException e)
        {
            return (0, new ScriptDiagnostic(actor, kind, e.Line, e.Column, StripPosition(e.Message)), Array.Empty<ScriptDiagnostic>());
        }
    }

    // Forgets the cached decompilations so they are printed again (after a
    // ScriptStyle change). Edited text is the user's own and is left alone.
    public void InvalidateTextCache()
    {
        foreach (var e in life.Concat(track)) e.OriginalText = null;
    }

    private static string StripPosition(string message)
    {
        // ScriptCompileException prefixes "line L, col C: "; the diagnostic carries those separately.
        var ix = message.IndexOf(": ", StringComparison.Ordinal);
        return message.StartsWith("line ", StringComparison.Ordinal) && ix > 0 ? message[(ix + 2)..] : message;
    }

    // ---- building ------------------------------------------------------------

    // Recompiles every edited script and rebuilds the scene record. Returns the
    // errors instead of a record if anything fails to compile or a reference no
    // longer resolves.
    public SceneBuildResult Build()
    {
        using var scope = Scope();
        var errors = new List<ScriptDiagnostic>();
        var n = ActorCount;

        // 1. tracks first (nothing in a track refers to anything else).
        for (var a = 0; a < n; a++)
        {
            var e = track[a];
            e.Compiled = null;
            if (!e.Edited) continue;
            try { e.Compiled = TrackText.Compile(TextForCompile(a, ScriptKind.Track)); }
            catch (ScriptCompileException ex) { errors.Add(Diagnostic(a, ScriptKind.Track, ex)); }
        }

        // 2. life scripts: parse + lay out every edited one before resolving any
        //    cross references, since two edited scripts can refer to each other.
        for (var a = 0; a < n; a++)
        {
            var e = life[a];
            e.Compiled = null;
            if (!e.Edited) continue;
            try { e.Compiled = LifeText.Compile(TextForCompile(a, ScriptKind.Life), a, symbols); }
            catch (ScriptCompileException ex) { errors.Add(Diagnostic(a, ScriptKind.Life, ex)); }
        }
        for (var a = 0; a < n; a++)
        {
            var c = life[a].Compiled;
            if (c is null) continue;
            var actor = a;
            try { c.ResolveExternals(r => LifeText.ResolveExternal(r, actor, symbols, c)); }
            catch (ScriptCompileException ex) { errors.Add(Diagnostic(a, ScriptKind.Life, ex)); }
        }
        if (errors.Count > 0) return new SceneBuildResult(null, errors);

        // 3. gather replacements: edited scripts, plus unedited life scripts whose
        //    offset operands had to follow a moved target.
        var replacements = new Dictionary<(int Actor, ScriptKind Kind), byte[]>();
        for (var a = 0; a < n; a++)
        {
            if (track[a].Compiled is { } tc) replacements[(a, ScriptKind.Track)] = tc.Bytes;
            if (life[a].Compiled is { } lc) replacements[(a, ScriptKind.Life)] = lc.Bytes;
            else
            {
                var patched = RepatchReferences(a, errors);
                if (patched is not null) replacements[(a, ScriptKind.Life)] = patched;
            }
        }
        if (errors.Count > 0) return new SceneBuildResult(null, errors);

        // 4. the comments of every recompiled script, anchored to the instructions it compiled to.
        var comments = new Dictionary<(int Actor, ScriptKind Kind), CommentSet?>();
        for (var a = 0; a < n; a++)
        {
            foreach (var kind in new[] { ScriptKind.Life, ScriptKind.Track })
            {
                var c = (kind == ScriptKind.Life ? life[a] : track[a]).Compiled;
                if (c is null) continue;
                var set = CommentExtractor.Extract(TextForCompile(a, kind), Header(a, kind), c.Asm.Code, c.Blocks, kind);
                comments[(a, kind)] = set.IsEmpty ? null : set;
            }
        }

        try
        {
            return new SceneBuildResult(record.Rebuild(replacements), errors, comments);
        }
        catch (ScriptFormatException ex)
        {
            errors.Add(new ScriptDiagnostic(0, ScriptKind.Life, 0, 0, ex.Message));
            return new SceneBuildResult(null, errors);
        }
    }

    private string TextForCompile(int actor, ScriptKind kind) => EntryOf(actor, kind).EditedText ?? OriginalText(actor, kind);

    private static ScriptDiagnostic Diagnostic(int actor, ScriptKind kind, ScriptCompileException ex) =>
        new(actor, kind, ex.Line, ex.Column, StripPosition(ex.Message));

    // For an unedited life script: re-point SET_TRACK / SET_TRACK_OBJ /
    // SET_COMPORTEMENT_OBJ operands whose target script was edited and moved
    // them. Returns the patched bytes, or null if nothing had to change.
    private byte[]? RepatchReferences(int actor, List<ScriptDiagnostic> errors)
    {
        var e = life[actor];
        var code = Bytecode.DecodeLife(e.Original);
        var changed = false;

        foreach (var ins in code)
        {
            if (ins.Op is not (LifeText.OpSetTrack or LifeText.OpSetTrackObj or LifeText.OpSetComportementObj)) continue;

            var def = Opcodes.Life(ins.Op)!;
            var offsetIx = def.Args.Length - 1;
            var isTrack = ins.Op is LifeText.OpSetTrack or LifeText.OpSetTrackObj;
            var target = ins.Op == LifeText.OpSetTrack ? actor : (int)ins.A[0];
            if ((uint)target >= (uint)life.Length) continue;
            if (!isTrack && target == actor) continue;            // own life script: never edited here

            var targetEntry = isTrack ? track[target] : life[target];
            if (!targetEntry.Edited) continue;                     // target unchanged -> operand still valid

            var oldOffset = (int)ins.A[offsetIx];
            if (!targetEntry.OriginalNames.TryGetValue(oldOffset, out var name)) continue; // raw @offset: leave alone
            var newOffset = isTrack ? symbols.TrackOffset(target, name) : symbols.LifeOffset(target, name);
            if (newOffset is null)
            {
                errors.Add(new ScriptDiagnostic(actor, ScriptKind.Life, 0, 0,
                    $"{def.Name} refers to '{name}' in actor {target}'s {(isTrack ? "track" : "life")} script, which no longer exists there. " +
                    $"Edit actor {actor}'s life script to point somewhere else, or restore '{name}'."));
                continue;
            }
            if (newOffset.Value != oldOffset) { ins.A[offsetIx] = newOffset.Value; changed = true; }
        }

        return changed ? Bytecode.EncodeLife(code) : null;
    }

    // ---- cross-script name resolution ------------------------------------------

    private sealed class Symbols : ISymbolSource
    {
        private readonly SceneScripts owner;
        public Symbols(SceneScripts owner) => this.owner = owner;

        // offset -> name always answers about the ORIGINAL layout (what the
        // existing bytes mean); name -> offset answers about the CURRENT one.
        public string? TrackName(int actor, int offset) => Name(owner.track, actor, offset);
        public string? LifeName(int actor, int offset) => Name(owner.life, actor, offset);
        public int? TrackOffset(int actor, string name) => Offset(owner.track, actor, name);
        public int? LifeOffset(int actor, string name) => Offset(owner.life, actor, name);

        private static string? Name(Entry[] table, int actor, int offset) =>
            (uint)actor < (uint)table.Length && table[actor].OriginalNames.TryGetValue(offset, out var n) ? n : null;

        private static int? Offset(Entry[] table, int actor, string name)
        {
            if ((uint)actor >= (uint)table.Length) return null;
            var e = table[actor];
            if (e.Compiled is not null) return e.Compiled.Symbols.TryGetValue(name, out var off) ? off : null;
            foreach (var (off2, n) in e.OriginalNames) if (n == name) return off2;
            return null;
        }
    }
}
