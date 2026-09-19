namespace LBA2LevelEditor.LbaScript;

// A position in the instruction stream that jumps can target. Bound once, to
// the index of the instruction that follows it (Code.Count when bound at the
// very end, i.e. "the end of the script").
internal sealed class Label
{
    public string Name;
    public int Index = -1;
    public bool Bound => Index >= 0;
    public Label(string name) => Name = name;
}

// A jump operand that still has to be pointed at a label once every
// instruction's byte offset is known.
internal readonly record struct Fixup(Instr Ins, int ArgIndex, Label Target);

// An operand that refers to another actor's script (SET_TRACK_OBJ,
// SET_COMPORTEMENT_OBJ): patched after every actor's layout is known.
internal sealed record ExternalRef(Instr Ins, int ArgIndex, ScriptKind TargetKind, int TargetActor, string Symbol, int Line, int Col);

// Collects instructions with symbolic jump targets and lowers them to bytes.
// Instruction sizes never depend on operand *values* (only on which opcode /
// SET_DIR mode is used), so offsets can be computed with placeholder targets
// and the real ones patched in afterwards.
internal sealed class Assembler
{
    public readonly List<Instr> Code = new();
    public readonly List<Label> Labels = new();
    private readonly List<Fixup> fixups = new();
    public readonly List<ExternalRef> External = new();

    // Symbols defined so far (functions / C labels / LABEL(n) instructions),
    // by name -> label, so references can be resolved after parsing.
    public readonly Dictionary<string, Label> Symbols = new(StringComparer.Ordinal);

    public Label NewLabel(string name = "")
    {
        var l = new Label(name);
        Labels.Add(l);
        return l;
    }

    // Named position: created on first mention (a forward reference may come
    // first) and bound where it is defined.
    public Label Reference(string name)
    {
        if (!Symbols.TryGetValue(name, out var l)) Symbols[name] = l = NewLabel(name);
        return l;
    }

    public Label Define(string name, Token at)
    {
        var l = Reference(name);
        if (l.Bound) throw TokenStream.Error($"'{name}' is already defined", at);
        Bind(l);
        return l;
    }

    public void Bind(Label l)
    {
        if (l.Bound) throw new InvalidOperationException($"Label {l.Name} bound twice");
        l.Index = Code.Count;
    }

    // Source line stamped onto every instruction emitted (set by the parsers).
    public int CurrentLine;

    public Instr Emit(Instr ins)
    {
        if (ins.Line == 0) ins.Line = CurrentLine;
        Code.Add(ins);
        return ins;
    }

    // Marks `ins` as jumping to `target` (its target field/arg is patched in Encode).
    public void Jump(Instr ins, Label target) => fixups.Add(new Fixup(ins, -1, target));

    // Points operand `argIndex` of `ins` (a TrackOffset / LifeOffset operand of
    // the same script) at `target`.
    public void Ref(Instr ins, int argIndex, Label target) => fixups.Add(new Fixup(ins, argIndex, target));

    // Lowers to bytes. Returns the encoded script and each instruction's offset
    // (Instr.Offset is filled in place). Throws if a referenced label was never bound.
    public byte[] Encode(ScriptKind kind, Func<Label, Token?>? locate = null)
    {
        foreach (var f in fixups)
            if (!f.Target.Bound)
                throw new ScriptCompileException($"Undefined label '{f.Target.Name}'", locate?.Invoke(f.Target)?.Line ?? 0, locate?.Invoke(f.Target)?.Col ?? 0);

        // Pass 1: offsets (targets are placeholders, sizes don't depend on them).
        Lower(kind);

        // Pass 2: real targets.
        var endOffset = Code.Count == 0 ? 0 : Code[^1].Offset + Code[^1].Length;
        foreach (var f in fixups)
        {
            var target = f.Target.Index >= Code.Count ? endOffset : Code[f.Target.Index].Offset;
            if (f.ArgIndex >= 0) f.Ins.A[f.ArgIndex] = target;
            else Bytecode.SetTarget(kind, f.Ins, target);
        }
        return Lower(kind);
    }

    public int OffsetOf(Label l) => l.Index >= Code.Count ? (Code.Count == 0 ? 0 : Code[^1].Offset + Code[^1].Length) : Code[l.Index].Offset;

    // Re-encodes with the current operand values (after external references were filled in).
    public byte[] Reencode(ScriptKind kind) => Lower(kind);

    private byte[] Lower(ScriptKind kind) => kind == ScriptKind.Life ? Bytecode.EncodeLife(Code) : Bytecode.EncodeTrack(Code);
}
