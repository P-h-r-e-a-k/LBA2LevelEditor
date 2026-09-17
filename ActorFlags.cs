namespace LBA2LevelEditor;

// T_OBJET.Flags (COMMON.H) -- the designer-authored subset only. Excludes
// SPRITE_3D/ANIM_3DS (structural: they change which other on-disk fields
// exist for that object, not safe to flip casually) and the two bits that
// are purely runtime/transient state the engine recomputes every tick
// (POS_INVALIDE, OBJ_IN_WATER) rather than anything stored per-actor on
// disk -- toggling those in the editor would have no lasting effect and
// get overwritten within a frame.
internal static class ActorFlags
{
    internal sealed record FlagInfo(string Name, uint Bit, string Description);

    public static readonly IReadOnlyList<FlagInfo> All = new[]
    {
        new FlagInfo("Collides with other objects", 0x1, "CHECK_OBJ_COL -- tests collisions/hits against other actors."),
        new FlagInfo("Collides with terrain/walls", 0x2, "CHECK_BRICK_COL -- tests collisions against decor and terrain bricks."),
        new FlagInfo("Reacts to trigger zones", 0x4, "CHECK_ZONE -- tests scenario/trigger zones under this actor."),
        new FlagInfo("Fixed clip zone (doors)", 0x8, "SPRITE_CLIP -- used for door-style fixed clip regions."),
        new FlagInfo("Pushable", 0x10, "PUSHABLE -- the hero can push this actor."),
        new FlagInfo("Low collision mode", 0x20, "COL_BASSE -- skips high collision tests (paired with terrain collision)."),
        new FlagInfo("Reacts to water/lava/gas/escalators", 0x40, "CHECK_CODE_JEU -- if off, this actor ignores water, lava, gas, electric floors, and escalators entirely."),
        new FlagInfo("Floor collision only", 0x80, "CHECK_ONLY_FLOOR -- skips wall collision tests, floor only."),
        new FlagInfo("Invisible", 0x200, "INVISIBLE -- not drawn, but still simulated."),
        new FlagInfo("Affected by gravity", 0x800, "OBJ_FALLABLE -- this actor falls under gravity."),
        new FlagInfo("No shadow", 0x1000, "NO_SHADOW -- disables the automatic drop-shadow."),
        new FlagInfo("Merges into background", 0x2000, "OBJ_BACKGROUND -- merges into static decor the first time it's drawn."),
        new FlagInfo("Can carry other objects", 0x4000, "OBJ_CARRIER -- can carry/move another object (platform/vehicle-style)."),
        new FlagInfo("Smaller bounding box", 0x8000, "MINI_ZV -- uses a square, smaller-side bounding box."),
        new FlagInfo("No impact reaction", 0x20000, "NO_CHOC -- doesn't play a shock/impact animation when hit."),
        new FlagInfo("Don't pre-clip (large objects)", 0x80000, "NO_PRE_CLIP -- skips pre-clipping, intended for very large objects."),
        new FlagInfo("Z-buffered display (exterior)", 0x100000, "OBJ_ZBUFFER -- uses Z-buffered rendering order in exterior scenes."),
    };
}
