# LBA2 retail script bugs

A full scan of every retail life script (222 scenes, `E:\GOG Games\LBA2 - Death rooms\SCENE.HQR`,
5,435 mined `ListVarGame` events - see `docs/LBA2_GAME_FLAGS.md`) plus a dedicated one-shot-pickup
scanner (`tools/ScriptRoundTrip/PickupFlagScan.cs`, command `pickupflags`). These are data bugs baked
into the shipped `SCENE.HQR`, not engine bugs - fixing them means patching the actor life scripts.

The three confirmed bugs (#1-#3) can now be patched from the app itself: **Tools > "LBA2: Fix scripting
errors…"** (`Lba2ScriptFixes.cs`). It edits each actor's life-script C text through the same
`ScriptSession`/`SceneScripts` compiler the script editor window uses, only if the actor's script still
has the exact known-buggy text (idempotent - a second run, or running it on an already-fixed SCENE.HQR,
reports "nothing to change" instead of touching anything), and saves through `SceneStore` like any other
scene edit (one `.bak` backup, one Edit > Undo step covering all three scenes). The minor #4 finding is
left alone (no real gameplay impact, and there's nothing to unambiguously "fix" - see below).

Regenerate the supporting data with:
```
cd tools/ScriptRoundTrip
dotnet run -c Release -- gameflags "E:\GOG Games\LBA2 - Death rooms" <outdir>      # events.txt / summary.txt
dotnet run -c Release -- pickupflags "E:\GOG Games\LBA2 - Death rooms"             # duplicate/orphan flag candidates
```

## Confirmed bugs

### 1. Holomap bought at the White Leaf Desert Bazaar never appears in the inventory

- **Where:** scene 36 ("White Leaf Desert, Bazaar"), hero (actor 0) life script, `comportement_1`, the
  `distance_message(4)` branch (merchandise actor 4).
- **What the code does:**
  ```c
  if (0 < life_point_obj(4)) {
      ...
      if (1 == choice()) {
          if (10 <= nb_gold_pieces()) {
              give_gold_pieces(10);
              kill_obj(4);
              found_object(0);      // <-- FLAG_HOLOMAP item id, plays the "found object" cinematic
              // set_var_game(0, 1) is MISSING here
          }
          ...
      }
  }
  ```
- **Why it's a bug:** `FLAG_HOLOMAP` is `ListVarGame[0]`; the engine only treats the holomap as owned
  when that var is `1` (`PERSO.CPP`), and the merchandise prop itself (`scene 36, actor 4`) even checks
  `if (1 == var_game(0)) suicide();` at load to remove itself once bought. Every *other* purchase in the
  same script pairs `found_object(N)` with `set_var_game(N, ...)` or `add_var_game(N, 1)` (darts -> var 14,
  ID card -> var 2, meca-pengui -> var 17) - the holomap is the only one missing it. Compare with the
  Citadel Island shop (scene 14, actor 0, `comportement_1`, `distance_message(8)` branch) and the demo
  copy (scene 194), which both correctly do `found_object(0); set_var_game(0, 1);`.
- **Consequence:** the player pays 10 kashes, watches the found-object cinematic, and the holomap prop
  disappears for that visit, but `var_game(0)` stays `0` forever, so the holomap feature never unlocks.
  Because the "already sold" state is never persisted, leaving and re-entering the scene (or reloading a
  save) respawns the merchandise with full life points, so the shopkeeper will happily "sell" the same
  broken holomap again and again.
- **Likely fix:** add `set_var_game(0, 1);` right after `found_object(0);` in scene 36's hero script, to
  match scene 14 and scene 194.

### 2. Two different four-leaf-clover boxes share the same "already collected" flag

- **Where:** scene 129 ("Island Under Celebration, the hide out", actor 6) and scene 180 ("Island CX,
  Secret passage", actor 16) both use `var_game(244)` as their unique per-box collected flag.
- **What the code does:**
  ```c
  // scene 129, actor 6
  void comportement_0() { if (1 == var_game(244)) suicide(); else set_comportement(comportement_1); }
  void comportement_1() {
      if (6 == col_obj(0)) { sample(891); inc_clover_box(); set_var_game(244, 1); suicide(); }
  }

  // scene 180, actor 16
  void comportement_0() {
      if (16 == col_obj(0)) {
          if (0 < var_game(244)) { give_bonus(0); }
          else { inc_clover_box(); set_var_game(244, 1); }
          suicide();
      }
  }
  ```
- **Why it's a bug:** every other retail clover box has its own dedicated flag in the same numeric block:
  scene 17 -> `var 240`, scene 26 -> `var 241`, scene 100 (see bug #3) -> `var 242`, scene 55 -> `var 243`,
  scene 13 -> `var 245`, scene 73 -> `var 246`. Scene 129 and scene 180 both landed on `var 244` instead of
  180 getting the next free slot (`var 247`, which is otherwise unused) - almost certainly a copy/paste
  numbering mistake when the second box was authored.
- **Consequence (order-dependent):**
  - Collect scene 129's box first -> `var_game(244)` becomes `1`. Later touching scene 180's box takes the
    `give_bonus(0)` branch instead of `inc_clover_box()`: the player gets a random bonus-box drop instead
    of a clover, and never gets the extra max-life point that box should award.
  - Collect scene 180's box first -> `var_game(244)` becomes `1`. Scene 129's actor then suicides in
    `comportement_0` on load, before the player can ever interact with it - that clover is simply lost,
    with no fallback reward at all.
  - Either way the player can only ever get one of the two clover life-boosts, never both.
- **Likely fix:** give scene 180's box its own flag (`var_game(247)`, currently unused/free) instead of
  reusing 244.

### 3. A clover box's "already collected" flag is never set, so it can be farmed repeatedly

- **Where:** scene 100 ("Wannies Island, the mine, 2nd room", actor 5) - the clover awarded after the
  Dino-Fly minecart cutscene. Same bug reproduced in its demo copy, scene 214.
- **What the code does:**
  ```c
  void comportement_0() { if (1 == var_game(242)) suicide(); else set_comportement(comportement_1); }
  void comportement_1() { /* ... waits for the minecart anim/track state ... */ set_comportement(comportement_2); }
  void comportement_2() { /* ... waits for the cart to stop ... */ set_comportement(comportement_3); }
  void comportement_3() {
      if (500 > distance(0)) { inc_clover_box(); sample(662); sample(663); suicide(); }
  }
  ```
- **Why it's a bug:** confirmed against the full mined event list - `var_game(242)` is *tested* in exactly
  two places in the whole game (scene 100 and its demo twin scene 214) and is never the target of a
  `SET_VAR_GAME` / `ADD_VAR_GAME` anywhere in any script. `comportement_3` calls `inc_clover_box()` but
  never calls `set_var_game(242, 1)`, unlike every other clover box in the game (see bug #2's list).
- **Consequence:** the guard in `comportement_0` can never see `var_game(242) == 1`, so it never suicides
  permanently. `suicide()` only removes the actor for the current scene session; leaving and re-entering
  scene 100 (or reloading) respawns the actor and the whole minecart sequence, letting the player replay
  the cutscene and call `inc_clover_box()` again - an infinite, repeatable max-life farm at this one spot.
- **Likely fix:** add `set_var_game(242, 1);` alongside `inc_clover_box();` in `comportement_3`, matching
  the pattern used by every other clover box.

## Minor / low-confidence finding

### 4. Dead flag in the White Leaf Desert "car jump" scene

- **Where:** scene 62, actors 7 and 8 (`ent 76` / `ent 170`) both branch on `var_game(172)`.
- **What's odd:** `var_game(172)` is tested (`== 1` in actor 7, `== 0` in actor 8) but - like bug #3 -
  is never the target of a `SET`/`ADD` anywhere in the retail data. Unlike bug #3 this doesn't gate a
  reward: actor 7's `if (1 == var_game(172)) suicide();` branch is simply unreachable dead code, and
  actor 8's `else` branch (`set_life_point_obj(8, 255); set_track(label_30); end_life();`) never runs.
- **Impact:** looks like leftover/unfinished scripting for this scene rather than a player-facing bug -
  the normal paths in both actors still execute correctly. Listed for completeness since it fits the same
  "reads a flag that's never set" shape as bug #3, but with no observed gameplay consequence.

## Scan methodology and what was ruled out

`pickupflags` looks for the general one-shot-pickup shape (`if (VAR == 0) { ...reward...; SET VAR }`) across
every actor, then flags (a) the same flag guarding more than one physical actor, and (b) guards whose flag
is never written anywhere in scope. It found 143 candidate "shared VAR_GAME flag" groups and dozens of
"shared VAR_CUBE flag" groups (VAR_CUBE is a per-scene, engine-wide array, so *any* two actors in a scene
sharing an index show up as a "duplicate"). Both counts are dominated by false positives from two
intentional, very common LBA2 scripting patterns, confirmed by manually decompiling a representative
sample of each:

- **Sequential multi-stage quest counters.** One `var_game` index doubles as a story-progress counter
  across an entire questline (e.g. `var 61` "Becoming a wizard": 3 = paid, 4 = got the flower, 5 = got the
  slate, 7 = got the diploma - each stage's own reward looks like a "duplicate" of the same flag, but it's
  one linear counter by design). `var 83` (main story sub-stage) and `var 94` (Dino-Fly location) are the
  same pattern.
- **Hero/NPC handshake flags via `VAR_CUBE`.** A `var_cube` slot is deliberately shared as a one-tick
  signal between the hero's own proximity script and an NPC's dialogue script (hero sets `var_cube(0)=1`
  on approach, the NPC's script reads it and resets it to `0` once it reacts). This showed up as "two
  actors sharing cube var 0" in nearly every town scene scanned (e.g. scene 27's magic-school clerk) and is
  the standard way these two scripts talk to each other, not a bug.

Four "orphan-read" `var_game` candidates from an earlier, per-actor-only pass (`var 147`, `var 69`, and the
`var_cube(9)` procession-flag in scene 15) were all confirmed, by checking the full `events.txt` corpus (not
just the one actor), to be written elsewhere in the same scene or game - false positives of that narrower
check, not bugs. `var 251` (`FLAG_CLOVER`, the running clover-life-boost total) also shows as "tested but
never `SET`/`ADD`ed" in `summary.txt`; that's an artifact of the miner not counting the dedicated
`INC_CLOVER_BOX` opcode as a write - the value is in fact updated correctly, by design, every time a clover
box is collected.
