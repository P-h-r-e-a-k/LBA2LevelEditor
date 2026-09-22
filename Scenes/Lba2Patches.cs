using System.Buffers.Binary;
using LBAAssembler.LbaScript;

namespace LBAAssembler.Scenes;

// The patch table at the end of an LBA2 scene record (U32 count, then per patch S16 size + S16 offset).
//
// The engine saves and restores the bytes named by the table (PtrScene + offset) in saved games: they are the parts of
// the scripts it changes while running. Retail tables point at exactly:
//   * the opcode byte of every SWIF and ONEIF life instruction (the engine rewrites them to SNIF / NEVERIF and back),
//   * the run-time state field of eight track instructions: the timer of WAIT_NB_DIZIEME, WAIT_NB_SECOND and their _RND
//     variants (4 bytes), WAIT_NB_ANIM (1 byte), and the angle of ANGLE, ANGLE_RND and FACE_TWINSEN (2 bytes).
// Offsets are positions in the record, so every edit that moves a script (a longer script, another object, a new zone
// before it ...) changes the table. It is therefore rebuilt from the scripts whenever a record is written.
internal static class Lba2Patches
{
    private static readonly Dictionary<string, (int Delta, int Size)> TrackRules = new()
    {
        ["WAIT_NB_DIZIEME"] = (2, 4),
        ["WAIT_NB_SECOND"] = (2, 4),
        ["WAIT_NB_DIZIEME_RND"] = (2, 4),
        ["WAIT_NB_SECOND_RND"] = (2, 4),
        ["WAIT_NB_ANIM"] = (2, 1),
        ["ANGLE"] = (1, 2),
        ["ANGLE_RND"] = (3, 2),
        ["FACE_TWINSEN"] = (1, 2),
    };

    private static readonly HashSet<string> LifeRules = new() { "SWIF", "ONEIF", "SNIF", "NEVERIF" };

    // One object's script positions in the record being written.
    internal readonly record struct ScriptSite(int TrackStart, byte[] Track, int LifeStart, byte[] Life);

    // The whole tail: the count and the (size, offset) pairs, objects in order, each object's track script before its
    // life script (the order the compiler emitted them in; verified against the retail records).
    public static byte[] Build(IReadOnlyList<ScriptSite> sites)
    {
        var patches = new List<(int Size, int Offset)>();
        foreach (var site in sites)
        {
            foreach (var (size, offset) in TrackPatches(site.Track, site.TrackStart)) patches.Add((size, offset));
            foreach (var (size, offset) in LifePatches(site.Life, site.LifeStart)) patches.Add((size, offset));
        }

        var tail = new byte[4 + patches.Count * 4];
        BinaryPrimitives.WriteInt32LittleEndian(tail, patches.Count);
        for (var i = 0; i < patches.Count; i++)
        {
            if (patches[i].Offset > short.MaxValue) throw new SceneFormatException("The scene record is too long for its patch table (offsets are 16 bits).");
            BinaryPrimitives.WriteInt16LittleEndian(tail.AsSpan(4 + i * 4), (short)patches[i].Size);
            BinaryPrimitives.WriteInt16LittleEndian(tail.AsSpan(4 + i * 4 + 2), (short)patches[i].Offset);
        }
        return tail;
    }

    private static IEnumerable<(int Size, int Offset)> TrackPatches(byte[] code, int start)
    {
        if (code.Length == 0) yield break;
        List<Instr> instructions;
        try { instructions = Bytecode.DecodeTrack(code); }
        catch (ScriptFormatException e) { throw new SceneFormatException($"A track script doesn't decode, so the patch table can't be rebuilt: {e.Message}"); }
        foreach (var i in instructions)
            if (Opcodes.Track((byte)i.Op)?.Name is { } name && TrackRules.TryGetValue(name, out var rule))
                yield return (rule.Size, start + i.Offset + rule.Delta);
    }

    private static IEnumerable<(int Size, int Offset)> LifePatches(byte[] code, int start)
    {
        if (code.Length == 0) yield break;
        List<Instr> instructions;
        try { instructions = Bytecode.DecodeLife(code); }
        catch (ScriptFormatException e) { throw new SceneFormatException($"A life script doesn't decode, so the patch table can't be rebuilt: {e.Message}"); }
        foreach (var i in instructions)
            if (Opcodes.Life((byte)i.Op)?.Name is { } name && LifeRules.Contains(name))
                yield return (1, start + i.Offset);
    }
}
