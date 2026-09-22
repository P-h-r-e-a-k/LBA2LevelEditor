using System.IO;
using LBAAssembler.Scenes;

namespace LBAAssembler;

// Writes an LBA2 actor's edited attributes (from ActorAttributesWindow) into its own scene record in SCENE.HQR,
// through the same SceneStore/SceneSerializer path every other saved LBA2 edit (zones, LBA1's own actor window,
// the joined-map tooling) already uses -- one save, one undo-log entry (SceneHistory), the same validation and
// .bak-keeping every one of those gets. Session-only Apply (SetActorAttributes et al., the native renderer)
// still runs first and is what the live view redraws from; this is the separate step that makes the change
// outlive the session.
internal static class Lba2ActorPersistence
{
    // Where actorIndex (RendererLibraryApi's own flat, native actor-scan index) sits: which scene it belongs to,
    // and its position within that scene's own actor list (0-based, counting only the real actors -- Actors[0]
    // is always the hero, so SceneModel.Actors[IndexInScene + 1] is this actor). Both native scans (the
    // whole-island overview and one interior scene) list a scene's own actors contiguously and in the scene
    // file's own order (confirmed empirically, not just by reading the native source: `lba2actorlocate` in
    // ScriptRoundTrip checked all 714 exterior actors of 5 islands and 98 interior actors of 7 scenes against
    // SceneStore's own parse of the same files, with zero mismatches), so a caller can recover this the same way
    // MainWindow's own zone overlay already locates a zone within its scene: count how many earlier entries of
    // the flat list share the same scene.
    public readonly record struct Located(int Scene, int IndexInScene);

    public static Located? Locate(RendererLibraryApi library, int actorIndex)
    {
        var scene = library.GetActorScene(actorIndex);
        if (scene < 0) return null;
        var ordinal = 0;
        for (var i = 0; i < actorIndex; i++) if (library.GetActorScene(i) == scene) ordinal++;
        return new Located(scene, ordinal);
    }

    // The fields ActorAttributesWindow edits, as plain (unsigned-looking, 0..255 where relevant) numbers -- the
    // same convention lba2_renderer_get/set_actor_attributes and the window's own text boxes use. Body/Anim are
    // raw BODY.HQR/ANIM.HQR indices (what the window's pickers list), not the small per-entity generic ids the
    // file itself stores.
    public readonly record struct Snapshot(int X, int Y, int Z, int Beta, int Body, int Anim, int LifePoints, int Armor, int HitForce, int Move, uint Flags);

    // Writes only the fields that actually changed between `original` (what the window loaded) and `updated`
    // (what is being applied now), leaving every other field exactly as the file already has it -- not just the
    // ones this window doesn't touch, but also anything a hidden difference between the file's own on-disk
    // convention and what the native side reports could otherwise silently overwrite. That gap is real, not
    // hypothetical: `lba2actorlocate` found the file's LifePoints for the first real actor of every interior
    // scene checked stored as -1 while the live native session already reports 0 for it (some engine-side
    // normalization at scene load, not a bug in this mapping) -- writing back "whatever the native side
    // currently shows" for a field nobody asked to change would have quietly turned that -1 into a 0 on disk.
    // Comparing against the snapshot taken when the window opened avoids that regardless of which field, or
    // which future engine quirk, it turns out to affect.
    //
    // Returns an error message on failure, or null on success; bodyAnimNote is set (success or not) when a
    // requested body/animation change couldn't be written (see below) so the caller can still report it.
    // newActorEntity: the "kind of actor" chosen in the window's Entity picker, needed only for an actor added
    // this session (see below) -- ignored otherwise.
    public static string? Save(string gameRoot, int scene, int indexInScene, Snapshot original, Snapshot updated, out string? bodyAnimNote, int? newActorEntity = null)
    {
        bodyAnimNote = null;
        var store = new SceneStore(SceneGame.Lba2, gameRoot);
        SceneModel model;
        try { model = store.Load(scene); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { return $"Couldn't read scene {scene}: {e.Message}"; }

        var actorPos = indexInScene + 1;   // the hero is Actors[0]

        // SceneSerializer stores LifePoints/Armor/HitForce/Move as signed bytes (S8): the file's own byte for
        // "255" (the native side's own, unsigned-looking convention, matching what the box shows) reads back as
        // -1, so that is the value that has to go back in for the same byte to come out again.
        static int Signed(int v) => unchecked((sbyte)v);

        // An exterior (island) scene stores an actor's position relative to its own cube, while the native
        // session -- and so this Snapshot's X/Y/Z -- reports the world-absolute position (the cube's own offset
        // added in, always a clean multiple of 32768 per axis: SceneModel.CubeX/CubeY, the same fields
        // MainWindow's own placement code adds in the other direction). An interior scene needs no conversion:
        // its world and scene-local coordinates are the same thing (CubeMode 0). Height (Y) is never offset by
        // the cube either way. Getting this wrong doesn't fail loudly for every exterior scene -- only for a
        // scene whose cube isn't (0, 0), where the offset is zero regardless -- so it's worth getting right
        // rather than trusting the S16 range check on save to catch every case.
        var cubeOffsetX = model.CubeMode == 1 ? model.CubeX * 32768 : 0;
        var cubeOffsetZ = model.CubeMode == 1 ? model.CubeY * 32768 : 0;

        // An actor added this session (AddActor) isn't in the file at all yet: SceneOps.AddActor always appends,
        // so as long as this is the first unsaved new actor of this scene, actorPos lands exactly at the file's
        // own next slot. Everything is written fresh here (there is no "original" in the file to diff against),
        // once a "kind of actor" (entity) is chosen and it already offers the picked body/animation -- exactly
        // the same requirement an existing actor's body/anim change has, just checked up front instead of
        // silently staying session-only, since a brand new actor has no earlier saved state to fall back to.
        if (actorPos >= model.Actors.Count)
        {
            if (actorPos > model.Actors.Count)
                return "Other new actors in this scene haven't been saved yet; save them in the order they were added.";
            if (newActorEntity is not { } entityId)
                return "This actor was added this session and isn't in the scene file yet: choose a \"kind of actor\" above, then Apply, to save it.";
            var newActorEntityTable = Lba2EntityTable.Load(gameRoot);
            var newActorEntityRecord = newActorEntityTable?.Entities.FirstOrDefault(e => e.Id == entityId);
            if (newActorEntityRecord is null) return $"Entity {entityId} isn't in this game's kind-of-actor table.";
            var newBodyGeneric = newActorEntityRecord.Bodies.Where(b => b.Body == updated.Body).Select(b => (int?)b.Generic).FirstOrDefault();
            var newAnimGeneric = newActorEntityRecord.Anims.Where(a => a.Anim == updated.Anim).Select(a => (int?)a.Generic).FirstOrDefault();
            if (newBodyGeneric is not { } newBody) return $"Body {updated.Body} isn't one of entity {entityId}'s own bodies; pick one from the list.";
            if (newAnimGeneric is not { } newAnim) return $"Animation {updated.Anim} isn't one of entity {entityId}'s own animations; pick one from the list.";

            var added = SceneOps.BlankActor(SceneGame.Lba2, updated.X - cubeOffsetX, updated.Y, updated.Z - cubeOffsetZ, entityId);
            added.Body = newBody; added.Anim = newAnim; added.Beta = updated.Beta;
            added.LifePoints = Signed(updated.LifePoints); added.Armor = Signed(updated.Armor);
            added.HitForce = Signed(updated.HitForce); added.Move = Signed(updated.Move); added.Flags = updated.Flags;
            SceneOps.AddActor(model, added);
            try { store.Save(scene, model, description: $"Add actor {actorPos} to scene {scene}"); }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { return $"Not saved: {e.Message}"; }
            return null;
        }
        var actor = model.Actors[actorPos];

        var changed = false;
        if (updated.X != original.X) { actor.X = updated.X - cubeOffsetX; changed = true; }
        if (updated.Y != original.Y) { actor.Y = updated.Y; changed = true; }
        if (updated.Z != original.Z) { actor.Z = updated.Z - cubeOffsetZ; changed = true; }
        if (updated.Beta != original.Beta) { actor.Beta = updated.Beta; changed = true; }
        if (updated.LifePoints != original.LifePoints) { actor.LifePoints = Signed(updated.LifePoints); changed = true; }
        if (updated.Armor != original.Armor) { actor.Armor = Signed(updated.Armor); changed = true; }
        if (updated.HitForce != original.HitForce) { actor.HitForce = Signed(updated.HitForce); changed = true; }
        if (updated.Move != original.Move) { actor.Move = Signed(updated.Move); changed = true; }
        if (updated.Flags != original.Flags) { actor.Flags = updated.Flags; changed = true; }

        // An animation moves the bones of the body it was made for, and the file stores a body/animation as a
        // small id relative to the actor's own kind of actor (FILE3D entity), not the raw archive index the
        // picker shows (see ActorAttributesWindow's own comment on this) -- so a raw index is only writeable
        // when it is one this actor's own entity already lists (switching between a character's own body/anim
        // variants, e.g.); anything else is refused here (kept session-only) rather than guessed at, since a
        // wrong generic id would point the file at the wrong body for every future load of this actor, not just
        // a display glitch.
        var entityTable = Lba2EntityTable.Load(gameRoot);
        var entity = entityTable?.Entities.FirstOrDefault(e => e.Id == actor.Entity);
        if (updated.Body != original.Body)
        {
            var generic = entity?.Bodies.Where(b => b.Body == updated.Body).Select(b => (int?)b.Generic).FirstOrDefault();
            if (generic is { } g) { actor.Body = g; changed = true; }
            else bodyAnimNote = Append(bodyAnimNote, $"body {updated.Body} isn't one of this kind of actor's own bodies");
        }
        if (updated.Anim != original.Anim)
        {
            var generic = entity?.Anims.Where(a => a.Anim == updated.Anim).Select(a => (int?)a.Generic).FirstOrDefault();
            if (generic is { } g) { actor.Anim = g; changed = true; }
            else bodyAnimNote = Append(bodyAnimNote, $"animation {updated.Anim} isn't one of this kind of actor's own animations");
        }

        if (!changed) return null;
        try { store.Save(scene, model, description: $"Edit actor {actorPos} of scene {scene}"); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { return $"Not saved: {e.Message}"; }
        return null;
    }

    private static string Append(string? note, string more) => note is null ? more : note + "; " + more;
}
