using System.IO;
using LBA2LevelEditor.LbaScript;

namespace LBA2LevelEditor;

// Where a UI actor's scripts live in SCENE.HQR: scene number (HQR entry - 1)
// and object slot inside that scene's record (0 = hero, 1.. = scene actors).
internal readonly record struct ActorSource(int Scene, int Slot);

internal sealed record SessionSaveResult(bool Ok, string Message, IReadOnlyList<ScriptDiagnostic> Errors);

// The editor's working set of scene scripts: loads scenes from SCENE.HQR on
// demand, keeps each scene's edits (as C text) for the whole session, and writes
// edited scenes back to SCENE.HQR on Save.
internal sealed class ScriptSession
{
    private readonly Func<string> gameRoot;
    private readonly Dictionary<int, SceneScripts> scenes = new();
    private readonly Dictionary<int, List<ActorSource>> exteriorMaps = new();
    private readonly string? commentsPathOverride;
    private CommentStore? commentStore;

    // commentsPath: where comments are kept; by default a sidecar next to SCENE.HQR.
    public ScriptSession(Func<string> gameRoot, string? commentsPath = null)
    {
        this.gameRoot = gameRoot;
        commentsPathOverride = commentsPath;
    }

    public string HqrPath => Path.Combine(gameRoot(), "SCENE.HQR");

    public string CommentsPath => commentsPathOverride ?? HqrPath + ".comments.json";

    // Loaded on first use. If the file exists but could not be read, LoadError says
    // why and nothing is overwritten without keeping a copy first.
    private CommentStore Comments
    {
        get
        {
            if (commentStore is null || commentStore.Path != CommentsPath) commentStore = CommentStore.Load(CommentsPath);
            return commentStore;
        }
    }

    public string? CommentsLoadError => Comments.LoadError;

    // Number of scene entries in SCENE.HQR (entry 0 is metadata, not a scene).
    private int SceneEntryCount() => Math.Max(0, HqrArchive.CountEntries(HqrPath) - 1);

    public SceneScripts? GetScene(int scene)
    {
        if (scenes.TryGetValue(scene, out var loaded)) return loaded;
        try
        {
            if (!File.Exists(HqrPath)) return null;
            var archive = HqrArchive.Open(HqrPath);
            if (scene < 0 || scene >= SceneEntryCount() || !archive.IsValid(scene + 1)) return null;
            var s = SceneScripts.Load(archive.Read(scene + 1), scene, (actor, kind) => Comments.Get(scene, actor, kind));
            scenes[scene] = s;
            return s;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ScriptFormatException or UnauthorizedAccessException)
        {
            DebugLog.Log($"ScriptSession: could not load scene {scene}: {e.Message}");
            return null;
        }
    }

    // Reprint every loaded scene's unedited scripts (after a ScriptStyle change).
    public void RefreshStyle()
    {
        foreach (var s in scenes.Values) s.InvalidateTextCache();
    }

    public bool HasUnsavedEdits => scenes.Values.Any(s => s.HasEdits);

    public IReadOnlyList<int> EditedScenes => scenes.Where(p => p.Value.HasEdits).Select(p => p.Key).Order().ToList();

    // The exterior actor list the native scan builds for an island (see
    // RendererScanIslandActors): scenes 0..221 in order, keeping those whose
    // record says Island == islandIndex and CubeMode == exterior, and for each
    // its objects 1..N-1. Actors the editor adds later are appended after these
    // and have no scene record yet, so they resolve to null.
    public ActorSource? ExteriorActor(int islandIndex, int actorIndex)
    {
        if (islandIndex < 0 || actorIndex < 0) return null;
        if (!exteriorMaps.TryGetValue(islandIndex, out var map)) exteriorMaps[islandIndex] = map = BuildExteriorMap(islandIndex);
        return actorIndex < map.Count ? map[actorIndex] : null;
    }

    public int ExteriorActorCount(int islandIndex)
    {
        if (!exteriorMaps.TryGetValue(islandIndex, out var map)) exteriorMaps[islandIndex] = map = BuildExteriorMap(islandIndex);
        return map.Count;
    }

    private List<ActorSource> BuildExteriorMap(int islandIndex)
    {
        var map = new List<ActorSource>();
        try
        {
            if (!File.Exists(HqrPath)) return map;
            var archive = HqrArchive.Open(HqrPath);
            var last = Math.Min(221, SceneEntryCount() - 1);
            for (var scene = 0; scene <= last; scene++)
            {
                if (!archive.IsValid(scene + 1)) continue;
                var rec = SceneRecord.Parse(archive.Read(scene + 1));
                if (rec.CubeMode != 1 || rec.Island != islandIndex) continue;
                for (var slot = 1; slot < rec.Actors.Count; slot++) map.Add(new ActorSource(scene, slot));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ScriptFormatException or UnauthorizedAccessException)
        {
            DebugLog.Log($"ScriptSession: could not map island {islandIndex}: {e.Message}");
            map.Clear();
        }
        return map;
    }

    // Compiles every edited scene and writes the result into SCENE.HQR. Nothing
    // is written unless every edited script compiles and every cross-reference
    // resolves. The first save keeps the untouched original as SCENE.HQR.bak.
    public SessionSaveResult SaveAll()
    {
        var edited = EditedScenes;
        if (edited.Count == 0) return new SessionSaveResult(true, "No edited scripts to save.", Array.Empty<ScriptDiagnostic>());

        var built = new List<(int Scene, byte[] Record, IReadOnlyDictionary<(int Actor, ScriptKind Kind), CommentSet?> Comments)>();
        var errors = new List<ScriptDiagnostic>();
        foreach (var scene in edited)
        {
            var r = scenes[scene].Build();
            if (r.Ok) built.Add((scene, r.Record!, r.Comments ?? new Dictionary<(int, ScriptKind), CommentSet?>()));
            else errors.AddRange(r.Errors.Select(e => e with { Scene = scene }));
        }
        if (errors.Count > 0)
            return new SessionSaveResult(false, $"Not saved: {errors.Count} script problem(s). First: {errors[0]}", errors);

        try
        {
            var path = HqrPath;
            var archive = HqrArchive.Open(path);

            // Only scenes whose record really changed need SCENE.HQR rewritten: a save
            // that only touched comments leaves the game file alone.
            var changed = built.Where(b => !archive.Read(b.Scene + 1).AsSpan().SequenceEqual(b.Record)).ToList();

            // Comments first. They are low-risk, and if writing them fails nothing else has been touched.
            var store = Comments;
            foreach (var (scene, _, comments) in built)
                foreach (var ((actor, kind), set) in comments)
                    store.Set(scene, actor, kind, set);
            var commentsChanged = store.Dirty;
            store.Save();

            var parts = new List<string>();
            if (changed.Count > 0)
            {
                var updated = File.ReadAllBytes(path);
                foreach (var (scene, record, _) in changed)
                    updated = HqrWriter.ReplaceEntry(updated, scene + 1, HqrWriter.StoredEntry(record));

                var backup = path + ".bak";
                if (!File.Exists(backup)) File.Copy(path, backup);

                // Write beside the target, verify it reads back, then swap it in.
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, updated);
                var check = HqrArchive.Open(temp);
                foreach (var (scene, record, _) in changed)
                    if (!check.Read(scene + 1).AsSpan().SequenceEqual(record))
                        throw new InvalidDataException($"Verification of scene {scene} failed after writing; SCENE.HQR was not modified.");
                File.Move(temp, path, overwrite: true);
                parts.Add($"scene {string.Join(", ", changed.Select(b => b.Scene))} written to {Path.GetFileName(path)} (backup: {Path.GetFileName(backup)})");
            }
            else parts.Add("script bytes unchanged, SCENE.HQR left alone");
            if (commentsChanged) parts.Add($"comments saved to {Path.GetFileName(store.Path)}");

            // What was saved is the new baseline for those scenes.
            foreach (var (scene, _, _) in built) scenes.Remove(scene);
            exteriorMaps.Clear();
            return new SessionSaveResult(true, "Saved: " + string.Join("; ", parts) + ".", Array.Empty<ScriptDiagnostic>());
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ScriptFormatException)
        {
            try { File.Delete(HqrPath + ".tmp"); } catch (IOException) { }
            return new SessionSaveResult(false, $"Not saved: {e.Message}", Array.Empty<ScriptDiagnostic>());
        }
    }
}
