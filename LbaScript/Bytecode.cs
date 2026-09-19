using System.Buffers.Binary;

namespace LBA2LevelEditor.LbaScript;

public sealed class ScriptFormatException : Exception
{
    public int Offset { get; }
    public ScriptFormatException(string message, int offset = -1) : base(offset >= 0 ? $"{message} (at byte {offset})" : message) => Offset = offset;
}

public enum ScriptKind { Life, Track }

// One decoded bytecode instruction. This is the lossless, offset-addressed
// form of a script: decode(bytes) -> Instr list -> encode() reproduces the
// original bytes exactly (jump operands hold absolute byte offsets; the
// higher-level C text works on labels instead and lowers to this).
internal sealed class Instr
{
    public int Offset;      // byte offset in the script it was decoded from (-1 if synthesized)
    public int Length;      // encoded size, filled by decode/encode
    public byte Op;
    public int Line;        // 1-based source line it was compiled from (0 = none / decoded from bytes)

    // Plain instructions: one value per ArgDef, in table order (Hidden args
    // and Jump args included; a Jump arg holds a byte offset).
    public long[] A = Array.Empty<long>();
    public string? Str;     // PLAY_ACF name

    // Life conditions (IF family) and CASE/OR_CASE.
    public byte Func;       // LF_* id (IF family, SWITCH)
    public int FuncArg = -1;// operand byte of Func, -1 if that condition has none
    public byte Test;       // LT_* id
    public int Value;       // comparison value
    public int Target;      // jump target (byte offset)

    public override string ToString() => $"@{Offset} op={Op}";
}

// Reads/writes little-endian primitives with bounds checks that report the
// script offset of the failure.
internal ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> data;
    public int Pos;

    public ByteReader(ReadOnlySpan<byte> data) { this.data = data; Pos = 0; }
    public readonly int Length => data.Length;
    public readonly bool AtEnd => Pos >= data.Length;

    private void Need(int n)
    {
        if (Pos + n > data.Length) throw new ScriptFormatException("Script ends in the middle of an instruction", Pos);
    }

    public byte U8() { Need(1); return data[Pos++]; }
    public sbyte S8() { Need(1); return (sbyte)data[Pos++]; }
    public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(Pos)); Pos += 2; return v; }
    public short S16() { Need(2); var v = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(Pos)); Pos += 2; return v; }
    public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(Pos)); Pos += 4; return v; }

    public string CStr()
    {
        var start = Pos;
        while (Pos < data.Length && data[Pos] != 0) Pos++;
        if (Pos >= data.Length) throw new ScriptFormatException("Unterminated string", start);
        var s = System.Text.Encoding.Latin1.GetString(data.Slice(start, Pos - start));
        Pos++;
        return s;
    }

    public long Read(ArgType t) => t switch
    {
        ArgType.U8 => U8(),
        ArgType.S8 => S8(),
        ArgType.U16 => U16(),
        ArgType.S16 => S16(),
        ArgType.U32 => U32(),
        ArgType.Jump => S16(),
        _ => throw new InvalidOperationException(),
    };

    public int ReadValue(ValueKind k) => k switch
    {
        ValueKind.S8 => S8(),
        ValueKind.U8 => U8(),
        _ => S16(),
    };
}

internal sealed class ByteWriter
{
    private readonly List<byte> bytes = new();
    public int Position => bytes.Count;
    public byte[] ToArray() => bytes.ToArray();

    public void U8(int v) => bytes.Add(unchecked((byte)v));
    public void S16(int v) { bytes.Add(unchecked((byte)v)); bytes.Add(unchecked((byte)(v >> 8))); }
    public void U32(long v) { for (var i = 0; i < 4; i++) bytes.Add(unchecked((byte)(v >> (8 * i)))); }

    public void CStr(string s)
    {
        bytes.AddRange(System.Text.Encoding.Latin1.GetBytes(s));
        bytes.Add(0);
    }

    public void Write(ArgType t, long v)
    {
        switch (t)
        {
            case ArgType.U8: case ArgType.S8: U8((int)v); break;
            case ArgType.U16: case ArgType.S16: case ArgType.Jump: S16((int)v); break;
            case ArgType.U32: U32(v); break;
            default: throw new InvalidOperationException();
        }
    }

    public void Value(ValueKind k, int v)
    {
        if (k == ValueKind.S16) S16(v); else U8(v);
    }

    // Overwrites an already-written S16 (used to back-patch jump targets).
    public void PatchS16(int at, int v)
    {
        bytes[at] = unchecked((byte)v);
        bytes[at + 1] = unchecked((byte)(v >> 8));
    }
}

internal static class Bytecode
{
    // ---- decode -----------------------------------------------------------

    public static List<Instr> DecodeLife(ReadOnlySpan<byte> code)
    {
        var list = DecodeLife(code, out var error);
        return error is null ? list : throw error;
    }

    // Tolerant form: returns every instruction decoded before a format error
    // (reported through `error`) instead of throwing, for diagnostics.
    public static List<Instr> DecodeLife(ReadOnlySpan<byte> code, out ScriptFormatException? error)
    {
        var r = new ByteReader(code);
        var list = new List<Instr>();
        var switchKind = ValueKind.S8;
        error = null;

        try
        {
            while (!r.AtEnd)
            {
                var ins = new Instr { Offset = r.Pos, Op = r.U8() };
                var def = Opcodes.Life(ins.Op) ?? throw new ScriptFormatException($"Unknown life opcode {ins.Op}", ins.Offset);
                switch (def.Form)
                {
                    case LifeForm.Plain:
                        ReadArgs(ref r, def.Args, ins);
                        break;

                    case LifeForm.Dir:
                        ReadArgs(ref r, def.Args, ins);
                        if (Opcodes.MoveTakesParam(ins.A[^1]))
                        {
                            Array.Resize(ref ins.A, ins.A.Length + 1);
                            ins.A[^1] = r.U8();
                        }
                        break;

                    case LifeForm.Cond:
                        ReadCondHead(ref r, ins, out var cd);
                        ins.Test = ReadTest(ref r);
                        ins.Value = r.ReadValue(cd.Value);
                        ins.Target = r.S16();
                        break;

                    case LifeForm.Switch:
                        ReadCondHead(ref r, ins, out var sd);
                        switchKind = sd.Value;
                        break;

                    case LifeForm.Case:
                        ins.Target = r.S16();
                        ins.Test = ReadTest(ref r);
                        ins.Value = r.ReadValue(switchKind);
                        break;
                }
                ins.Length = r.Pos - ins.Offset;
                list.Add(ins);
            }
        }
        catch (ScriptFormatException e)
        {
            error = e;
        }
        return list;
    }

    public static List<Instr> DecodeTrack(ReadOnlySpan<byte> code)
    {
        var list = DecodeTrack(code, out var error);
        return error is null ? list : throw error;
    }

    public static List<Instr> DecodeTrack(ReadOnlySpan<byte> code, out ScriptFormatException? error)
    {
        var r = new ByteReader(code);
        var list = new List<Instr>();
        error = null;
        try
        {
            while (!r.AtEnd)
            {
                var ins = new Instr { Offset = r.Pos, Op = r.U8() };
                var def = Opcodes.Track(ins.Op) ?? throw new ScriptFormatException($"Unknown track opcode {ins.Op}", ins.Offset);
                ReadArgs(ref r, def.Args, ins);
                ins.Length = r.Pos - ins.Offset;
                list.Add(ins);
            }
        }
        catch (ScriptFormatException e)
        {
            error = e;
        }
        return list;
    }

    private static void ReadArgs(ref ByteReader r, ArgDef[] args, Instr ins)
    {
        if (args.Length == 0) return;
        ins.A = new long[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Type == ArgType.CStr) ins.Str = r.CStr();
            else ins.A[i] = r.Read(args[i].Type);
        }
    }

    private static void ReadCondHead(ref ByteReader r, Instr ins, out CondDef cd)
    {
        var at = r.Pos;
        ins.Func = r.U8();
        cd = Opcodes.Cond(ins.Func) ?? throw new ScriptFormatException($"Unknown life condition {ins.Func}", at);
        ins.FuncArg = cd.OperandName is null ? -1 : r.U8();
    }

    private static byte ReadTest(ref ByteReader r)
    {
        var at = r.Pos;
        var t = r.U8();
        if (t >= Opcodes.TestSymbols.Length) throw new ScriptFormatException($"Invalid comparison operator {t}", at);
        return t;
    }

    // ---- encode -----------------------------------------------------------

    public static byte[] EncodeLife(IReadOnlyList<Instr> code)
    {
        var w = new ByteWriter();
        var switchKind = ValueKind.S8;
        foreach (var ins in code)
        {
            ins.Offset = w.Position;
            w.U8(ins.Op);
            var def = Opcodes.Life(ins.Op) ?? throw new InvalidOperationException($"Unknown life opcode {ins.Op}");
            switch (def.Form)
            {
                case LifeForm.Plain:
                    WriteArgs(w, def.Args, ins);
                    break;
                case LifeForm.Dir:
                    WriteArgs(w, def.Args, ins);
                    if (Opcodes.MoveTakesParam(ins.A[def.Args.Length - 1]))
                    {
                        if (ins.A.Length <= def.Args.Length) throw new InvalidOperationException($"{def.Name} with move mode {ins.A[def.Args.Length - 1]} needs a parameter");
                        w.U8((int)ins.A[def.Args.Length]);
                    }
                    break;
                case LifeForm.Cond:
                    var cd = WriteCondHead(w, ins);
                    w.U8(ins.Test);
                    w.Value(cd.Value, ins.Value);
                    w.S16(ins.Target);
                    break;
                case LifeForm.Switch:
                    switchKind = WriteCondHead(w, ins).Value;
                    break;
                case LifeForm.Case:
                    w.S16(ins.Target);
                    w.U8(ins.Test);
                    w.Value(switchKind, ins.Value);
                    break;
            }
            ins.Length = w.Position - ins.Offset;
        }
        return w.ToArray();
    }

    public static byte[] EncodeTrack(IReadOnlyList<Instr> code)
    {
        var w = new ByteWriter();
        foreach (var ins in code)
        {
            ins.Offset = w.Position;
            w.U8(ins.Op);
            var def = Opcodes.Track(ins.Op) ?? throw new InvalidOperationException($"Unknown track opcode {ins.Op}");
            WriteArgs(w, def.Args, ins);
            ins.Length = w.Position - ins.Offset;
        }
        return w.ToArray();
    }

    private static void WriteArgs(ByteWriter w, ArgDef[] args, Instr ins)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Type == ArgType.CStr) w.CStr(ins.Str ?? "");
            else w.Write(args[i].Type, ins.A[i]);
        }
    }

    private static CondDef WriteCondHead(ByteWriter w, Instr ins)
    {
        var cd = Opcodes.Cond(ins.Func) ?? throw new InvalidOperationException($"Unknown life condition {ins.Func}");
        w.U8(ins.Func);
        if (cd.OperandName is not null) w.U8(ins.FuncArg);
        return cd;
    }

    // ---- jump helpers -----------------------------------------------------

    // Every jump-target offset a life/track instruction carries, or null if it
    // has none. Used by validation and by label assignment.
    public static bool TryGetTarget(ScriptKind kind, Instr ins, out int target)
    {
        target = 0;
        if (kind == ScriptKind.Life)
        {
            var def = Opcodes.Life(ins.Op)!;
            if (def.Form is LifeForm.Cond or LifeForm.Case) { target = ins.Target; return true; }
            for (var i = 0; i < def.Args.Length; i++)
                if (def.Args[i].Type == ArgType.Jump) { target = (int)ins.A[i]; return true; }
            return false;
        }
        var tdef = Opcodes.Track(ins.Op)!;
        for (var i = 0; i < tdef.Args.Length; i++)
            if (tdef.Args[i].Type == ArgType.Jump) { target = (int)ins.A[i]; return true; }
        return false;
    }

    public static void SetTarget(ScriptKind kind, Instr ins, int target)
    {
        if (kind == ScriptKind.Life)
        {
            var def = Opcodes.Life(ins.Op)!;
            if (def.Form is LifeForm.Cond or LifeForm.Case) { ins.Target = target; return; }
            for (var i = 0; i < def.Args.Length; i++)
                if (def.Args[i].Type == ArgType.Jump) { ins.A[i] = target; return; }
            return;
        }
        var tdef = Opcodes.Track(ins.Op)!;
        for (var i = 0; i < tdef.Args.Length; i++)
            if (tdef.Args[i].Type == ArgType.Jump) { ins.A[i] = target; return; }
    }
}
