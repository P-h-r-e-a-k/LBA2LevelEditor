using System.Text;

namespace LBA2LevelEditor.LbaScript;

// A plain listing of a life and a track script, one instruction per line with its byte offset: the
// "Disassembly" pane of the script window for games without the native decoder (LBA1).
internal static class Disassembly
{
    public static string Text(byte[] life, byte[] track, OpcodeSet dialect)
    {
        using var scope = Opcodes.Use(dialect);
        var sb = new StringBuilder();
        sb.Append("LIFE SCRIPT (").Append(life.Length).Append(" bytes)\n");
        Append(sb, ScriptKind.Life, life);
        sb.Append("\nTRACK SCRIPT (").Append(track.Length).Append(" bytes)\n");
        Append(sb, ScriptKind.Track, track);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, ScriptKind kind, byte[] code)
    {
        if (code.Length == 0) { sb.Append("  (none)\n"); return; }
        ScriptFormatException? error;
        var list = kind == ScriptKind.Life ? Bytecode.DecodeLife(code, out error) : Bytecode.DecodeTrack(code, out error);
        foreach (var ins in list) sb.Append($"{ins.Offset,5}: {Format(ins, kind)}\n");
        if (error is not null) sb.Append("  !! ").Append(error.Message).Append('\n');
    }

    private static string Format(Instr i, ScriptKind kind)
    {
        string Args(ArgDef[] defs) => string.Join(", ", defs.Select((d, k) => d.Type == ArgType.CStr ? $"\"{i.Str}\"" : $"{i.A[k]}"));
        string Fn(CondDef cd) => cd.OperandName is null ? cd.Name : $"{cd.Name}({i.FuncArg})";

        if (kind == ScriptKind.Track)
        {
            var d = Opcodes.Track(i.Op)!;
            return d.Args.Length == 0 ? d.Name : $"{d.Name}({Args(d.Args)})";
        }
        var def = Opcodes.Life(i.Op)!;
        return def.Form switch
        {
            LifeForm.Cond => $"{def.Name} {Fn(Opcodes.Cond(i.Func)!)} {Opcodes.TestSymbols[i.Test]} {i.Value}  else -> {i.Target}",
            LifeForm.Switch => $"SWITCH {Fn(Opcodes.Cond(i.Func)!)}",
            LifeForm.Case => $"{def.Name} {Opcodes.TestSymbols[i.Test]} {i.Value}  else -> {i.Target}",
            _ => def.Args.Length == 0 ? def.Name : $"{def.Name}({Args(def.Args)}{(i.A.Length > def.Args.Length ? ", " + string.Join(", ", i.A.Skip(def.Args.Length)) : "")})",
        };
    }
}
