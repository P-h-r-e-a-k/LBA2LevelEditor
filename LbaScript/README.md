# LBA2 script translation (C-style dialect)

LBA2 actors are driven by two bytecode languages stored per actor in `SCENE.HQR`:

* **life script** – behaviour: conditions, dialogue, state changes, switching comportement.
  Interpreted by `GERELIFE.CPP` (`DoLife` / `DoFuncLife` / `DoTest`).
* **track script** – movement/animation sequence. Interpreted by `GERETRAK.CPP` (`DoTrack`).

This folder translates both **to C-style text and back**, losslessly. The engine has
no `while`, no boolean operators, and no named blocks – only conditional jumps and
byte offsets – so the dialect lowers those onto the real opcodes.

```
SCENE.HQR record ──SceneRecord──▶ life/track bytes ──Bytecode──▶ Instr list
                                                                  │  ▲
                            LifeText/TrackText.Decompile ─────────┘  │ Assembler
                                          ▼                          │
                                      C text  ──── Lexer/Parser ─────┘
```

| file | role |
|---|---|
| `Opcodes.cs` | Authoritative opcode/operand tables (LM_/LF_/TM_), transcribed from the interpreter's `case` bodies |
| `Bytecode.cs` | `Instr` model, decode/encode of both languages |
| `SceneRecord.cs` | Parses one scene record (as `LoadScene` does) and rebuilds it with replaced scripts |
| `Lexer.cs`, `Assembler.cs`, `Operands.cs` | Tokenizer, label/fixup assembler, shared operand printing/parsing |
| `LifeExpr.cs` | `&&` / `\|\|` / `!` conditions ⇄ `AND_IF`/`OR_IF`/`IF` chains |
| `LifeDecompiler.cs`, `LifeCompiler.cs` | Life language: structuring decompiler (with self-check) and compiler |
| `TrackText.cs` | Track language, both directions |
| `SceneScripts.cs` | Whole-scene layer: edit text, recompile, re-point cross-actor references, rebuild the record |
| `../HqrWriter.cs` | Replaces one HQR entry (stored/uncompressed) leaving every other entry byte-identical |
| `Comments.cs`, `CommentStore.cs` | Comments kept apart from the bytecode: anchoring, fingerprints, alignment, JSON sidecar |
| `../ScriptSession.cs` | Editor working set: actor → scene/slot, edits, comments, **Save to SCENE.HQR** (with `.bak`) |

## Life language

```c
void comportement_0() {
    if (ZONE_OBJ(0) == 2) {
        SET_DOOR_DOWN(1024);
        SET_COMPORTEMENT(comportement_1);
        SET_TRACK(label_100);
    } else if (NB_LITTLE_KEYS() > 0 && (COL() == 8 || DISTANCE(0) < 300)) {
        SET_TRACK_OBJ(8, label_0);
    }
}
```

| C | engine |
|---|---|
| `void comportement_N() { … }` | a block ending in `END_COMPORTEMENT` (implicit at `}`) |
| `SET_COMPORTEMENT(comportement_N)` | operand = byte offset of that block. Selects what runs **next tick** – *not a call* |
| statements after the last function | the tail (code after the final `END_COMPORTEMENT`); the closing `END` is implicit |
| `if (c) {A} else {B}` | `IF c→else; A; ELSE→end; B` |
| `swif` / `oneif` / `snif` / `neverif` | the other IF-family terminals |
| `while (c) {A}` | `IF c→exit; A; OFFSET→top` (`OFFSET` is the engine's unconditional jump) |
| `a && b`, `a \|\| b`, `(a\|\|b) && c`, `!` | `AND_IF`/`OR_IF` chains, closing with `IF`; `!` flips the comparison (De Morgan) |
| `switch (F(x)) { case 1: case 2: … break; case > 5: … default: … }` | `SWITCH`, `OR_CASE…CASE`, `BREAK`, `DEFAULT`, `END_SWITCH`. A case that isn't `break`ed continues into the **next case's test** (engine behaviour), not C fall-through |
| `return;` / `break;` / `goto L;` / `L:` | `RETURN` / `BREAK` / `OFFSET` / a jump target |
| `NAME(args);` | any other opcode, e.g. `ANIM(12)`, `SET_VAR_GAME(60, 3)`, `SET_DIR(MOVE_FOLLOW, 6)` |
| `IF(cond, L);` `ELSE(L);` `CASE(5, L);` … | the flat, low-level forms – always available |

Conditions are one comparison each: `FUNC(operand) <op> value`, e.g. `DISTANCE(0) < 500`,
`ZONE() == 1`. Comparison values are typed per condition (`L_TRACK`, `VAR_CUBE`, `RND`,
`COL_DECORS` are **unsigned** bytes; most others signed; many 16-bit).

Cross-script operands are names, not offsets: `SET_TRACK(label_3)`, `SET_TRACK_OBJ(8, label_0)`,
`SET_COMPORTEMENT_OBJ(5, comportement_2)`. `@123` is a raw byte offset (an escape hatch; it
does not follow edits).

## Track language

One opcode call per line; `LABEL(n);` defines the jump symbol `label_n` (label ids repeat in
shipped scripts, so a repeat becomes `label_n_2`, `label_n_3`…). `GOTO(label_n)`, `LOOP(count, label_n)`.
Runtime scratch bytes the interpreter keeps inside the bytecode (wait timers, loop counters,
`ANGLE_RND` state) are synthesized with the values the engine resets them to (`CleanTrack`).

## Comments

The bytecode has nowhere to keep comments and the text is regenerated from the bytes on every load, so comments
are stored **separately** in `SCENE.HQR.comments.json` (next to `SCENE.HQR`) and matched back onto the script that
is loaded (`Comments.cs`, `CommentStore.cs`):

* A comment is anchored to an **instruction** (not a text position), with a placement: above the statement, trailing
  on its line, below it (after a closing brace), inside an empty block, above/on a function header, or at the end.
* The sidecar keeps, per script, a **fingerprint of every instruction** the comments were written against
  (opcode + meaningful operands; jump targets and `SET_TRACK`/`SET_COMPORTEMENT` offsets are ignored because they shift
  when other scripts change).
* On load the stored fingerprints are **aligned** (prefix/suffix, LCS, then in-place edits) against the loaded script.
  Identical scripts restore every comment exactly; a script changed underneath (another tool, an edit elsewhere)
  carries its comments along to the statements they were on.
* A comment whose statement no longer exists is **never dropped**: it is listed at the end of the script under
  `// (the comments below lost their statement ...)`, and the script window says so.
* A save that only changes comments leaves `SCENE.HQR` untouched (no rewrite, no `.bak`).
* A sidecar that can't be read is reported and preserved as `.bad` before a new one is written.

Details: `//` and `/* */` comments are both read; block comments are stored as `//` lines. Blank lines are not kept.
The generated first line (`// scene N, actor M - ... script`) is never stored. A comment written after a lone
closing brace (`} // end`) is kept as a comment at the end of that block.

## What the real data taught us (why the tables differ from the old disassembler)

Everything below was found by decoding all 3115 actors' scripts and is enforced by tests:

* `SET_DIR` / `SET_DIR_OBJ` take an **extra operand byte** for `MOVE_FOLLOW`, `SAME_XZ`, `SAME_XZ_BETA`, `CIRCLE`, `CIRCLE2` (`AdjustDirObject`).
* `INC_CLOVER_BOX` takes **no** operand.
* There is **no `COMPORTEMENT` header opcode** in shipped scripts; blocks are identified only by offset.
* `SET_TRACK*` always target a track `LABEL`; `SET_COMPORTEMENT_OBJ` always targets a block start.
* `AND_IF` always shares the terminating `IF`'s false target; `OR_IF` targets the body start.
* `OFFSET` (the loop jump) never occurs – `while` is new.
* A few `switch`es omit `END_SWITCH`, and some nest `CASE` groups inside a case body.

## Guarantees and limits

* **Verified against the game data** (`tools/ScriptRoundTrip`): all 3115 life + 3115 track scripts decompile to C and
  recompile to the *identical bytes*; the decompiler recompiles its own output before returning it and, if a
  segment can't be structured or doesn't round-trip, writes just that block in flat `goto`/label form.
  ~1.3% of scripts (40) contain a flat block, mostly nested `CASE` groups.
* Editing: only edited scripts are recompiled. Untouched scripts keep their original bytes; only their
  cross-actor offset operands are re-pointed if a target moved. A reference whose target no longer exists is an error.
* Formatting is not stored (only bytes and comments are): after **Save** the text is regenerated from the bytes in
  the canonical layout, with your comments put back next to their statements (see *Comments*).
* The scene record's `Checksum` field (a scenario stamp saves compare) is left untouched.
* Scripts are limited to 32767 bytes each (S16 length prefix).

## Running the checks

```
cd tools/ScriptRoundTrip
dotnet run -c Release -- roundtrip all    # every script: bytes -> C -> bytes
dotnet run -c Release -- selftest         # compiler on hand-written C (while, ||/&&, switch, errors…)
dotnet run -c Release -- scenetests       # edits, cross-reference re-pointing, HQR write-back (temp copy)
dotnet run -c Release -- commenttests     # comments: every script, plus save/load/realign/detach on real files
dotnet run -c Release -- nativemap        # actor -> scene/slot mapping vs the real native library
dotnet run -c Release -- show <scene> <actor> life|track
dotnet run -c Release -- dump <scene> <actor> life|track    # raw hex + linear decode
```

`tools/UiSmoke` parses the script window's XAML in a real WPF process (and can render it to a PNG).
