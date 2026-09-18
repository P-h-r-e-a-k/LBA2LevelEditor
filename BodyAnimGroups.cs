using System.Buffers.Binary;
using System.IO;

namespace LBA2LevelEditor;

// Reads the "group count" every BODY.HQR/ANIM.HQR entry's own header
// carries -- ObjectInitAnim (ANIM.CPP) reads an animation's group count
// into the object's NbGroups and then walks the body's own per-group point
// data that many times (INTFRAME.CPP), with no clamp against how many
// groups the body itself actually has. There's no separate "which
// animations suit this body" table anywhere in the game data; the engine
// itself only ever finds out by trying, so this reads the same two headers
// it does and compares that one field, rather than guessing from names.
// See EXTFUNC.CPP's AffichageBodyPreview / RENDERER_ACTORS.CPP's
// ApplyRawBodyAnim for the native-side version of this same check (the
// two are independent -- one runs in-process here for filtering the
// dropdown live, the other guards the native renderer against a crash if
// something bypasses this filter, e.g. by typing a raw number).
internal static class BodyAnimGroups
{
    private static HqrArchive? bodyArchive;
    private static HqrArchive? animArchive;
    private static string? loadedForGameDirectory;
    private static readonly Dictionary<int, int> bodyGroupCache = new();
    private static readonly Dictionary<int, int> animGroupCache = new();

    // -1 for an index that doesn't resolve to a real entry.
    public static int BodyGroupCount(string gameDirectory, int bodyIndex)
    {
        EnsureLoaded(gameDirectory);
        if (bodyArchive is null) return -1;
        if (bodyGroupCache.TryGetValue(bodyIndex, out var cached)) return cached;
        // +1: BODY.HQR/ANIM.HQR's own offset table has one metadata slot
        // (offsetTable[0], its own byte length) ahead of the real entries
        // -- "body/anim index 0" as the editor's own pickers and HQD
        // description files number them (and as ActorAttributesWindow's
        // CountHqrEntries computes the usable count: slots - 1) is stored
        // at raw archive index 1, not 0.
        var value = ReadGroupCount(bodyArchive, bodyIndex + 1, headerOffset: 32, fieldSize: 4);
        bodyGroupCache[bodyIndex] = value;
        return value;
    }

    public static int AnimGroupCount(string gameDirectory, int animIndex)
    {
        EnsureLoaded(gameDirectory);
        if (animArchive is null) return -1;
        if (animGroupCache.TryGetValue(animIndex, out var cached)) return cached;
        var value = ReadGroupCount(animArchive, animIndex + 1, headerOffset: 2, fieldSize: 2);
        animGroupCache[animIndex] = value;
        return value;
    }

    // An animation authored for a bigger skeleton than the body provides
    // reads past the body's own group data (see this class's own comment);
    // one with fewer groups just leaves the body's remaining groups at
    // rest pose, which is harmless. Unknown group counts (an index this
    // couldn't read a header for) are treated as compatible rather than
    // hiding the option -- better to occasionally allow a mismatched pair
    // than to hide a legitimate one because of a parsing gap.
    public static bool IsCompatible(string gameDirectory, int bodyIndex, int animIndex)
    {
        var bodyGroups = BodyGroupCount(gameDirectory, bodyIndex);
        var animGroups = AnimGroupCount(gameDirectory, animIndex);
        if (bodyGroups < 0 || animGroups < 0) return true;
        return animGroups <= bodyGroups;
    }

    private static void EnsureLoaded(string gameDirectory)
    {
        if (bodyArchive is not null && string.Equals(loadedForGameDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase))
            return;

        try { bodyArchive = HqrArchive.Open(Path.Combine(gameDirectory, "BODY.HQR")); } catch { bodyArchive = null; }
        try { animArchive = HqrArchive.Open(Path.Combine(gameDirectory, "ANIM.HQR")); } catch { animArchive = null; }
        loadedForGameDirectory = gameDirectory;
        bodyGroupCache.Clear();
        animGroupCache.Clear();
    }

    // headerOffset/fieldSize select where the group count sits in each
    // entry's own header and how wide it is: BODY.HQR's T_BODY_HEADER
    // (LIB386/H/OBJECT/AFF_OBJ.H) has a 4-byte NbGroupes at byte offset 32
    // (Info(4) SizeHeader(2) Dummy(2) XMin/XMax/YMin/YMax/ZMin/ZMax(4 each)
    // = 32); an ANIM.HQR entry has a 2-byte NbGroups at byte offset 2
    // (animPtr[1] in ObjectInitAnim's own U16 array read, ANIM.CPP).
    private static int ReadGroupCount(HqrArchive archive, int rawIndex, int headerOffset, int fieldSize)
    {
        try
        {
            if (!archive.IsValid(rawIndex)) return -1;
            var data = archive.Read(rawIndex);
            if (data.Length < headerOffset + fieldSize) return -1;
            return fieldSize == 4
                ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(headerOffset))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(headerOffset));
        }
        catch
        {
            return -1;
        }
    }
}
