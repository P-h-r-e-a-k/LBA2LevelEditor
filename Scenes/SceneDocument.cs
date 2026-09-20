namespace LBA2LevelEditor.Scenes;

// One open scene with undo / redo and dirty tracking. Edits go through Edit / EditGrid, which work on a copy and only
// keep it if the change succeeds; every edit is a snapshot on the undo stack (a SceneModel is small, and grids are
// never modified in place, so snapshots share them).
//
// AutoSave writes each edit, undo and redo to the game files straight away (how the editor's windows behave today: an
// Apply is on disk at once), still leaving every step undoable. A failed save rolls the edit back.
internal sealed class SceneDocument
{
    private sealed record Snapshot(string Description, SceneModel Scene, byte[]? Grid);

    private readonly List<Snapshot> undo = new();
    private readonly List<Snapshot> redo = new();
    private SceneModel current;
    private byte[]? currentGrid;
    private string? lastMergeKey;
    private long version;
    private SceneModel savedScene;      // what is on disk: the document is clean while it is showing exactly these
    private byte[]? savedGrid;

    public SceneStore Store { get; }
    public int SceneNumber { get; }
    public bool AutoSave { get; set; }
    public int UndoLimit { get; set; } = 200;

    // Raised after every edit, undo, redo, save and revert.
    public event EventHandler? Changed;

    private SceneDocument(SceneStore store, int scene, SceneModel model, byte[]? grid)
    {
        Store = store;
        SceneNumber = scene;
        current = model;
        currentGrid = grid;
        savedScene = model;
        savedGrid = grid;
    }

    public static SceneDocument Open(SceneStore store, int scene, bool withGrid = false)
        => new(store, scene, store.Load(scene), withGrid && store.Game == SceneGame.Lba1 ? store.LoadGrid(scene) : null);

    // The scene as it stands. Treat it as read-only: change it through Edit so the change can be undone.
    public SceneModel Scene => current;
    public byte[]? Grid => currentGrid;

    public bool IsDirty => !ReferenceEquals(current, savedScene) || !ReferenceEquals(currentGrid, savedGrid);
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string? UndoDescription => undo.Count > 0 ? undo[^1].Description : null;
    public string? RedoDescription => redo.Count > 0 ? redo[^1].Description : null;

    // Applies `change` to a copy of the scene. Edits with the same non-null `mergeKey` in a row (a drag, typing in a
    // field) share one undo step.
    public void Edit(string description, Action<SceneModel> change, string? mergeKey = null)
    {
        var next = current.Clone();
        change(next);
        Commit(description, next, currentGrid, mergeKey);
    }

    public void EditGrid(string description, Func<byte[], byte[]> change, string? mergeKey = null)
    {
        if (currentGrid is null) throw new InvalidOperationException("This document was opened without its grid.");
        Commit(description, current, change(currentGrid), mergeKey);
    }

    // Scene and grid together (one undo step), e.g. adding a door: its actor and the cells it stands in.
    public void EditBoth(string description, Action<SceneModel> changeScene, Func<byte[], byte[]> changeGrid, string? mergeKey = null)
    {
        if (currentGrid is null) throw new InvalidOperationException("This document was opened without its grid.");
        var next = current.Clone();
        changeScene(next);
        Commit(description, next, changeGrid(currentGrid), mergeKey);
    }

    private void Commit(string description, SceneModel scene, byte[]? grid, string? mergeKey)
    {
        var before = new Snapshot(description, current, currentGrid);
        var mergeable = mergeKey is not null && mergeKey == lastMergeKey && undo.Count > 0;
        var previousScene = current;
        var previousGrid = currentGrid;
        var previousVersion = version;
        current = scene;
        currentGrid = grid;
        version++;
        try
        {
            if (AutoSave) Store.Save(SceneNumber, current, currentGrid);
        }
        catch
        {
            current = previousScene;
            currentGrid = previousGrid;
            version = previousVersion;
            throw;
        }
        if (!mergeable)
        {
            undo.Add(before);
            if (undo.Count > UndoLimit) undo.RemoveAt(0);
        }
        redo.Clear();
        lastMergeKey = mergeKey;
        if (AutoSave) { savedScene = current; savedGrid = currentGrid; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo() => Step(undo, redo);
    public bool Redo() => Step(redo, undo);

    private bool Step(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0) return false;
        var target = from[^1];
        var here = new Snapshot(target.Description, current, currentGrid);
        var previousScene = current;
        var previousGrid = currentGrid;
        var previousVersion = version;
        current = target.Scene;
        currentGrid = target.Grid;
        version++;
        try
        {
            if (AutoSave) Store.Save(SceneNumber, current, currentGrid);
        }
        catch
        {
            current = previousScene;
            currentGrid = previousGrid;
            version = previousVersion;
            throw;
        }
        from.RemoveAt(from.Count - 1);
        to.Add(here);
        lastMergeKey = null;
        if (AutoSave) { savedScene = current; savedGrid = currentGrid; }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public SceneSaveResult Save(bool allowErrors = false)
    {
        var result = Store.Save(SceneNumber, current, currentGrid, allowErrors);
        savedScene = current; savedGrid = currentGrid;
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    // Throws the unsaved changes away and reads the scene from the game folder again (the undo history goes too).
    public void Revert()
    {
        current = Store.Load(SceneNumber);
        if (currentGrid is not null) currentGrid = Store.LoadGrid(SceneNumber);
        undo.Clear();
        redo.Clear();
        lastMergeKey = null;
        version++;
        savedScene = current; savedGrid = currentGrid;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Saves the scene under another number (only the same game): "save as" for scenes, which live in fixed slots.
    public SceneSaveResult SaveAs(int scene, bool allowErrors = false)
    {
        if (!Store.SceneExists(scene)) throw new InvalidOperationException($"Scene {scene} isn't a slot in this game's SCENE.HQR (scenes can't be added, only replaced).");
        return Store.Save(scene, current, currentGrid, allowErrors);
    }
}
