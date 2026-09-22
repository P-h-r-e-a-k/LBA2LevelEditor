# LBA2 Level Workshop

A native Windows WPF viewer and editor for Little Big Adventure 2.

## Run

Build and launch with the .NET 10 SDK or Visual Studio:

```text
dotnet build
dotnet run
```

## First run and game folders

The editor opens empty until a game folder is set under File > Settings. Each folder is optional (LBA1 and LBA2 are
independent, so owning only one is fine): the Game selector switches between whichever are set, and setting a folder
in Settings loads that game straight away (if nothing was open, or the open game lost its folder, it moves to the game
that is available).

Everything the editor keeps lives next to the executable, so the app is portable (copy the folder, keep everything):
`settings.json` (game folders and options), `native\` (the renderer DLL, unpacked from the exe on first launch and
replaced when the exe changes), `Body Exports\` (Body Studio's default output folder) and `editor.log` (a running
trace of what the UI and the native engine are each doing, timestamped and interleaved, rotated to `editor.log.old`
past 5 MB; `DebugLog`/`NativeDebugLog`, on in every build **while this is under test** - see that file's own comment
for turning it off before a formal release). If the folder isn't writable it falls back to
`%AppData%\LBA2LevelEditor` / `%LOCALAPPDATA%\LBA2LevelEditor`; a `settings.json` found only in `%AppData%` from an
earlier version is copied next to the exe on first run.

The one thing outside the folder is not ours: a single-file .NET app unpacks WPF's own native libraries to
`%TEMP%\.net\LBA2LevelEditor` on launch (a disposable cache the .NET host manages). To redirect it, set the
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` environment variable before starting the exe (for example from a `.cmd` file).

## Release build (single executable)

```text
# once, and after native changes: the statically linked renderer (MSYS2 UCRT64 shell tools on PATH)
cd native\lba2-classic-community
cmake --build out/build/windows_ucrt64_static --target lba2_renderer
cd ..\..
dotnet publish -p:PublishProfile=SingleFile
```

This writes `release\LBA2LevelEditor.exe`: self-contained (no .NET install needed), with the native renderer, the dummy
body and the scene/body/animation name lists embedded. Debug logging is off in that build unless
`LBA2_EDITOR_DEBUG_LOG` names a file.

The current first slice provides an editable exterior-map workspace with terrain painting, level selection, zoom, reset, and JSON draft export.

## Native renderer backend

The editor includes a vendored native renderer at `native/lba2-classic-community`, exposed to WPF through `liblba2_renderer.dll` (a `lba2_renderer`/`lba2_renderer_engine` CMake target that excludes `PERSO.CPP` and the playable game loop). Build it with MSYS2 UCRT64, CMake, and Ninja; the generated DLL is placed under `native/lba2-classic-community/out/build/windows_ucrt64/SOURCES/3DEXT/`.

The renderer-only bootstrap in `RENDERER_API.CPP`/`RENDERER_BOOT.CPP` now completes cleanly and produces real textured frames (terrain, decor, palette-correct lighting) without launching the playable game or generating PNG screenshots. Getting there required matching the retail boot sequence in several places the renderer-only build had diverged from it:

- `RendererBootAssets()` was loading FILE3D/anim/texture/goodie HQR data into private local buffers instead of the real engine globals (`BufferFile3D`, `BufferAnim`, `BufferTexture`, `PtrZvExtra`, `PtrZvExtraRaw`, `PtrZvAnim3DS`) that the rest of the engine reads through macros like `LoadFile3D()` — those globals stayed allocated-but-uninitialized.
- `PtrPolySea` (the animated-water polygon buffer) is normally allocated in `PERSO.CPP`; without it, a cube with an animated water polygon wrote through a null pointer.
- Camera angles must be wrapped to `0..4095` (`& 4095`) like everywhere else in the engine; an unwrapped negative `BetaCam` left `TerrainTri` uninitialized in `DrawHorizon2ZBuf()`.
- `IsleMapIndex`/`GroundTexture`/`ObjTexture`/`ListTriExt` are reassigned via the engine's own `Malloc`/`NormMalloc` during boot; the renderer API's `initialize()`/`shutdown()` used plain `malloc`/`free` on them, corrupting the heap on shutdown.
- `ClipXMin/ClipYMin/ClipXMax/ClipYMax` (and `ModeResX/ModeResY`) are established by `InitGraphics()` (window + video surface + screen buffers + clip rect), which the renderer-only path never called; without it every projected vertex was flagged out-of-bounds and nothing was ever rasterized.

The WPF viewport now tries the native renderer first for any island (`nativeViewActive`), and falls back to the editor's own movable CPU rasterizer (`SoftwareTerrainRenderer`) if the native renderer library itself is unavailable.

Panning across a whole island (not just orbiting a single fixed cube) is done through `lba2_renderer_set_view_target(worldX, worldY, worldZ)`, a native export that maps an absolute world position into the 16x16 cube grid (each cube spans 32768 world units, matching HOLO.H's `SCE`), loads whichever cube contains it if that differs from the one currently loaded, and sets the cube-local `VueOffsetX/Y/Z` camera target. Mouse drag, mouse wheel, the zoom buttons, and the arrow keys all update a single world-space `(targetX, targetY, targetZ)` in `MainWindow.xaml.cs`, which `CommunityRendererBackend.RenderIslandDirect()` feeds straight into `SetViewTarget` on every frame -- so terrain streams in continuously as the camera crosses cube boundaries. Panning is clamped per-axis against `IslandDocument.CubeAt()` so a drag that would leave the island's mapped cubes stops cleanly on that axis instead of handing the renderer a position with no data.

**LBA1 has no equivalent library.** `native/lba1-classic` is a vendored copy of LBALab's LBA1 source (see its
`VENDORED.txt`): an OpenWatcom DOS / Win9x build with no SDL or modern-Windows target. Its game logic (about 18k lines of
C: `GRILLE.C`, `OBJECT.C`, `GERELIFE.C`, `GERETRAK.C`, `DISKFUNC.C` ...) is C, but the software renderer and the 3D and
sound libraries are still MASM (about 23k lines in `LIB_3D` and `LIB_SVGA` alone, none translated yet), so it can't be
turned into a renderer DLL the way the LBA2 engine was. It is used as the reference for what the LBA1 engine expects
of game data (see `Lba1GridEdit`, `Lba1GridValidator`, `SceneValidator`).

## Little Big Adventure 1

The **Game** selector under the menu switches between LBA2 and LBA1; set the LBA1 install folder under File > Settings
(default `E:\GOG Games\Little Big Adventure`). LBA1 is read in C# (`Lba1/`), not through the native LBA2 engine:

- `SCENE.HQR` scenes are parsed by `Lba1Scene` (header, actors, 24-byte zones, track points); every one of the 120
  scenes parses to exactly its byte length. A scene's first byte is its island (the game's text bank), which groups the
  Island / Scene lists.
- `Lba1GridRenderer` draws scene N from `LBA_GRI` / `LBA_BLL` entry N and the shared `LBA_BRK` bricks with the same
  isometric projection as `GRILLE.CPP` (the grid, block and brick payloads match LBA2's), into the same viewport the
  LBA2 interiors use, with zones, patrol routes (`Lba1TrackScript`) and actor markers on top.
- Scene names come from LBAPackageManager's `SCENE1.HQD` (line 2 is scene 0).
- LBA1 scenes carry no indoor/outdoor flag (the header bytes are the island id and a game-over scene), so the
  **Join connected areas** option infers it from the map edges (`Lba1Areas`): a cube-change zone along a grid edge that
  lands at the opposite edge of a same-island scene, with a matching zone leading back, means the two grids continue
  into each other. Such scenes are drawn as one map, each grid offset by 64 cells plus the sideways shift measured from
  the zone/arrival positions (Citadel Island, Principal Island, the Hamalayi and Polar strips, ...). Everything else
  (interiors, single scenes) stays a separate scene. A zone and its arrival point are the same spot of the world, so
  the join also carries a height offset (e.g. the Citadel town floor is 15 layers below the prison yard's).
  Scenes that don't touch along an edge can be joined by hand in `Lba1Areas.ManualLinks`: the Citadel harbour comes
  from a west-edge link; Principal Island's water tower sits directly west of Peg Leg Street (64 cells, three layers up, 48
  rows north) so its ochre path arrives on the street's path, burying none of the street (`areatests` checks that the path rows
  meet and that no two scenes of a joined map share a brick cell; `overlaps <folder>` lists any);
  Port Belooga lies north of the military camp (the camp's north exit and Port Belooga's south gate share their columns
  and both scenes start Twinsen there); White Leaf Desert's Maze is placed from its only exit into the desert, slid
  east until it no longer shares columns with the military camp; Proxima's sea scenes (north of the city and the two
  rune stones) and Hamalayi Mountains (2) (village, sacred carrot, bunker and lake) are joined from their zones, and so are
  Hamalayi Mountains (1)'s prison (outside the prison, the entrance hall through its gate, the prison up the hall's stairs, and
  the backdoor's path at the prison's north end) and the transporter's inside (the building at the top of the outside scene's
  snowfield), and Tippet Island's village (74) with the bar (76) and the Twinsun Cafe (80) off the bar are one map (the shop, 101, is left out of it),
  `Village`, and its three secret passage scenes (75 in the middle, 77 below it, 79 above it) another, `Secret passage`: all from
  the zones, placed where the zones put them and then separated from the village (see below). Citadel Island's tavern (14) with its cellar (33), `Tavern`, and the sewer of the first scene (34) with
  the secret sewer (55), `Sewer`, are two more small maps from their zones (the sewers' zones back agree to a cell), and so is Brundle
  Island's inside of the teleportation building (95) with its secret room (98) and the telepods (99), `Teleportation` and Fortress Island's swimming pool (88) with the first secret
  passage scene (85) and the corridor near Zoe's cell (87) off it, `Underground Fortress passage`; the fortress's inside (83) with the cloning centre (89) from
  the zones and the rune stone (90) beyond the cloning centre's west wall, `Fortress`. The rune stone has no zone, the cloning centre's script
  changes to it (`change_cube(90)` in its scenaric zone number 1), so it is placed by hand with its start point inside that zone
  (`areatests` checks it). **No joined map has scenes inside one another**: the zones put some scenes on top of their neighbours
  (a room docks into the footprint of its town), so once a map is placed `Lba1Areas.Separations` moves those scenes by the smallest number of
  cells (found by `separate <folder> 60 columns`: along x and z) that leaves no shared plan column (a brick in the same x, z at any height); the doorways then no longer
  meet exactly. `areatests` requires zero overlap and checks the zone-derived placements without those moves. The Principal Island harbour (11)
  meets the fortress's outside (12) at a grid edge but is not part of that map (`NotJoined`). The two end sequence scenes (116, 117: the data
  says Polar Island) are filed under Citadel Island (`MapIsland`, editor lists only) and joined as `End Credits`. A joined map is listed by its
  name alone, with no scene numbers. `links <folder>` lists every pair of same-island scenes whose zones lead into each other with the offset each side
  gives (the pairs that agree are the candidates: mostly doors into interiors, which would overlap the town they belong to) and
  `arearender <folder> <png> <area|none> [scene=dx,dy,dz ...]` draws a candidate placement with each grid outlined before it is put
  into `ManualLinks`.
  On Proxima the rune-stone scenes (45 lower, 46 upper) are placed by eye rather than by a zone, so the two
  motorbikes line up (45) and the upper stone follows on from scene 47, "before the upper rune stone" (46).
  Every map is called `Outside` unless `Lba1Areas.AreaNames` names it (`Temple of Bú`, `Mutation centre`; the top bar adds its
  scene numbers). **Join connected areas** is on by default and kept in `settings.json` (`Lba1JoinConnectedAreas`; the old
  `Lba1JoinAreas` started off, so its value is ignored). Turning it off opens the scene the map was looking at
  (`Lba1Areas.FocusScene`: the scene with most ground in the middle half of the view, so the one zoomed in on, else the most
  central; equally central: the lower-numbered) instead of the island's first scene, and an island opens on its biggest outside map
  (`MainArea`) or, with joining off, on that map's most central scene (`CentralScene`).
- **Tools > LBA1: Make surprise changes** (`Lba1SurpriseChanges`; the door part is `Lba1RoomDoorMod`, the elf part
  `Lba1PinkElf`, the fishermen part `Lba1Fishermen`) edits the game files in three ways, saved together as one all-or-nothing
  transaction. First, so the
  otherwise unreachable "Some room (cut-out ?)" (scene 61) can be entered: Scene 13 (Lupin Burg) has a bricked-up arch on the east side of a
  house (cells x 51, z 11-15); the tool does to it what the game does for the Rabbibunny house next door (scene 28),
  moved to the new arch. In `LBA_GRI.HQR` (grid 13) the bricks become an open arch with a recess floor, copied from
  the Rabbibunny doorway. In `SCENE.HQR` scene 13 gets the standard sliding door: a sprite actor (sprite 11, flags
  0xD409) cloned from that house's door actor 9, with its screen clip rectangle (Info) moved by the isometric shift
  (24 px per cell along x-z, 12 along x+z), and the usual scripts (life: `set_door_up`, open on `col_obj(0)`, close when
  `distance(0)` > 3000; track: `open_up` / `close` / `wait_door`, sample 35). It is appended as actor 28, so no existing
  actor changes number. A cube-change zone in the recess (scene 13 -> 61) and one in the room's doorway
  (scene 61 -> 13) do the transition; the hero arrives at the destination corner plus his offset inside the zone. The
  first change to each file keeps a `.bak`, Edit > Undo takes the whole change back in one step; running it again does nothing (an older, faulty version of the edit is
  refused with instructions to restore the `.bak` files). Later versions of the edit are completed in place: the recess is lined
  with grey stone (block 2) so no black shows when the door opens, the door's clip rectangle is taller (Info[3] + 28, the whole
  140-pixel sprite), and the arch's legs are the smooth arch stones from the floor up (blocks 150 north at z = 11 and 149 south at
  z = 15, layers 1..9, as the bricked arch and house 58's entrance have them: the Rabbibunny doorway that is copied has stone / no
  lower legs because that house stands on raised ground, which left this arch's south leg showing the wall behind it). The street in
  front of the door stays the game's own ochre dirt (x 52..55, curb at 56; an earlier version paved it with a doorstep slab and
  cobbles, and the edit puts that back cell for cell). Look at it with `doorrender <folder> 13 28 <png>` (an emulation of OBJECT.C's
  door drawing; house 58's door is actor 8, `doorapron` renders candidate grounds).  The joined map already shows scene 61 docked behind the wall
  (`Lba1Areas.ManualLinks`) with or without the edit. The room has no camera zone or scripts of its own.
  What the door looks like in the game was checked by compositing the scene the way the engine does (`doorrender`, in
  `tools/ScriptRoundTrip`: the bricks in painter order, the sprite cut to its Info rectangle, then `DrawOverBrick3` putting the
  bricks in front of the door back over it). Two things showed, both fixed and both repaired in place on files an earlier
  version of the tool changed: (1) the retail clip rectangle stops 8 pixels below the door's base point, which the Rabbibunny
  house hides with raised ground, but beside this level street the door's lower left end (it runs down and left along z) was
  cut off and floated above the floor, so this door's rectangle is 28 pixels taller (the sprite is 140 pixels tall and reaches 36
  below its base point); (2) the recess was only 3 cells wide, so through the open door, and beside the closed one, the
  empty cells drew as black. The whole doorway (x 33-37, z 52-56: pillars, floor, invisible side walls) is now copied, the floor
  is carried to the far pillar (the reference is bare there) and grey stone (block 2, a 1 x 2 x 1 cobble block, layers 1-6) lines
  the back (x 46) and the north side (z 11) of the recess. Tools for that: `doorstudy`, `doorrender`, `doorvariant`, `blocksheet`
  (a contact sheet of a scene's block library), `blockinfo`, `surpriseapply <folder>` and `scenetext <folder> <scene> <actor>`.
  Edits follow what the engine relies on, taken from the LBA1 source: every grid column keeps all 25 cells; the grid
  entry's last 32 bytes stay the bitmap of blocks in use (the engine reads it from the end of the entry to decide which
  bricks to load; a grid that lost it loads the wrong bricks and runs slowly); and a door is a SPRITE_3D + SPRITE_CLIP
  actor, whose Info fields are its clip rectangle on screen.
  Second, the room gets a visitor, a pink elf. `BODY.HQR` entry 88 (Raymond the Elf, blue) is cloned with only the
  colour byte of his blue polygons changed - hat and tunic (colour 64, palette ramp 4) and sleeve cuffs (160, ramp 10)
  become the first colour of the pink ramp (224, ramp 14) - so face, eyes, nose, hands, shoes, trousers, hat band and
  bobble keep theirs, and every point, bone and lighting normal stays byte-identical (the body works with all of the
  Elf entity's animations). The clone is appended to `BODY.HQR` as a new stored entry (132) and `FILE3D.HQR`'s Elf
  entity (49) gets a BODY record `01 2A 04 84 00 00` for it: an actor's body number is an id inside its entity that the
  engine turns into a `BODY.HQR` entry through that record (`SearchBody` in FICHE.C), never an entry number itself, so
  the pink elf is body 42 of entity 49. Scene 61 gets it as its last actor (flags 0x0803, animation 0, on the floor at
  cell 57, 55 between the beds) with a small script that turns it to face Twinsen within 2500 units and lets go beyond
  3000; its dialogue colour (`CoulObj`) is 14, the pink ramp. A copy that already exists is left alone (its dialogue
  colour is set to 14 if it differs); a different body under id 42 is refused. Everything is saved through
  `SceneStore.SaveMany` with the two HQR files as `ExtraFile`s in the same transaction; Edit > Undo takes the scenes and
  the grid back and leaves the (then unused) body in `BODY.HQR`. Limits from the engine source: a clone stays inside the
  renderer's fixed buffers (30 bones, 500 points, 500 normals) and the 400,000-byte scene memory holds one copy of the
  body per elf actor. Tests: `store surprise` (temp copies, undo/redo, repair, refusals) and `runtime doors` (the
  elf's body resolves and it turns to face the hero).
  The elf, Floppy the third elf, greets Twinsen (`Lba1DialogueText`, `Lba1PinkElf.Greeting`): text 287 of Principal Island's dialogue ("Hi, I'm Floppy the third elf, after
  all these years somebody has finally found me. Please help yourself to anything you find here."), appended after the fisherman's question (286) to the bank of all five
  languages (English, French, German, Spanish, Italian; the four others hold the English until translated, and a translation later replaces it in place). The elf's script
  says it with `message(287)` once, the first time Twinsen is within 2500 units (game flag 226; nothing in the game's own scripts uses 220..254), and again whenever action is
  pressed within 1500 units, one press one greeting (a scene variable, 13, is held while the button is down). The game's text is in the DOS code page (CP 850, an
  e-acute is 0x82), which `Lba1DialogueText.Bytes` writes. The tool `elfxliff <folder>` writes the greeting as XLIFF 1.2, one file per language, with empty targets and notes
  for the translator (`translations/lba1-floppy-greeting.{fr,de,es,it}.xlf`); translations go into `Lba1PinkElf.Translations` (language number: 1 French, 2 German, 3 Spanish,
  4 Italian). Tests: `store surprise` (text banks, the elf's script, upgrade of a first-version elf, a translation replacing the placeholder) and `runtime doors`
  (the greeting once, again on action, not on a held button, not from far away).
  Third, the boat trips of the three fishermen are opened up for chapter 6. How a trip works: the fisherman asks where to go
  (`add_choice` / `ask_choice`), takes 10 Kashes and sets the scene variable 0 (`var_cube(0)`) to the destination; his boat
  sails, and the hero's script then plays the trip on the holomap (`holomap_traj`) and puts Twinsen on a scene point
  (`pos_point`) that lies inside a cube-change zone: standing in it carries him to the other island. The scripts tested that
  variable for 1 and "anything else", so a third destination is 2, and every destination text already exists in each island's
  dialogue file. Port Belooga (scene 24, actor 1) offered only the desert in chapter 5; in chapter 6 he now offers the Citadel
  (2), the desert (1) and Proxima Island (0), asking a new question (below). The military camp (scene 39, actor 2) and Proxima City
  (scene 42, actor 10) already offered the Citadel and Principal Island; in chapter 6 the camp also offers Proxima Island (2) and
  Proxima City the White Leaf Desert (2). Where each new trip lands matters: the game's zones for its chapter-6 ferry (Port Belooga
  -> the Citadel's second harbour, the camp -> Proxima's edge, Proxima -> the desert's east edge) start arrival scenes that wait for
  a boat that only exists while `var_game(68)` is set, which hides Twinsen and freezes him (the reported desert softlock). So Port
  Belooga and the camp get a landing of their own - a cube-change zone high above the scene (y 4096..5631, overlapping nothing, so
  nobody can walk into it) plus a track point in it, appended as the scene's last zone and point - whose destination values give the
  same arrival as the game's own zone into that quay (the Citadel's scene 6 zone 14, Proxima's scene 42 zone 7), which is what starts
  the fisherman's boat sailing in; Proxima City's trip ends with `change_cube(39)`, entering the desert at its start position, where the
  boat sails in and the fisherman steps out. Two more fixes: the new Principal Island question is a new text, "For 10 Kashes, where do
  you want to go, Twinsen?" (id 286, appended as the last text of island 1's `TEXT.HQR` dialogue file in all five languages - English
  as written, the others copy the island-3 fisherman's price line - so its position is past every voice file's table and no voice
  shifts; the chapter-5 speech, text 45, is unchanged), and Proxima's fisherman used to walk off his pier: after sitting down facing
  the water (`angle(0)`, ~250 units from the edge) he stands up (anim 55) and starts the walk cycle (anim 1, whose first steps are 335
  and 225 units forward) while `goto_point` is still turning him, so he steps into the sea; his track now turns first
  (`label(2); angle(704); anim(1); goto_point(3);`). The edit is text patches of the decompiled C scripts, recompiled through
  `SceneScripts` (so references between scripts are re-pointed), each refused unless the script is what it expects; earlier versions of
  the patches are recognised and brought up to date. Tests: `store surprise` (scripts, texts in every language and the bank limits,
  landing points and arrivals, recompile to the same bytes, upgrade from the first version, refusals) and `runtime fishermen`, which
  plays all nine chapter-6 trips in the C# simulation (talk, choose, pay, board, holomap trip, arrival scene, and that Twinsen is then
  visible and can walk: the check does see the old Proxima -> desert softlock; `Lba1Runtime.ChoicePolicy` answers a headless run's
  questions). The simulation turns instantly and doesn't reproduce the fall itself; the pier geometry comes from `scenemap`,
  `stacks` and `animsteps`.  Fourth, a street lamp at the west corner of Lupin Burg (`Lba1LampPost`): block 117, the column of ten cells Lupin Burg's own six lamps
  are, on the curb cap of the platform at grid cell x 0, z 63 (layers 9..18 in `LBA_GRI` entry 13), with a bonus zone (zone type 4, the
  engine's "giver": `ZoneGiveExtraBonus` in `EXTRA.C`) that gives a little key: Info0 128 (one bit from bit 4 per bonus: money, life,
  magic, key, clover), Info1 1. While Twinsen stands in it and presses action a key pops out of the middle of the zone at its top and
  flies (about a thousand units) towards him, so the zone is centred on the lamp's foot (the key can only fly out over the cobbles, never
  off the corner); the engine clears the zone's "taken" flag on every entry to the scene, so it gives one key per visit. The key check
  is in `store surprise` (the cells, the zone, the upgrade of the first version's zone) and `runtime doors` (pressing action in the zone
  in the simulation: the key lands on the platform and picking it up makes `NbLittleKeys` one more).
  Fifth, the door needs that key the first time (`Lba1DoorLock`): its `comportement_1` opens only when game flag 220 ("Door unlocked") is
  set, and if it isn't and Twinsen has a little key (`nb_little_keys()`), spends it (`use_one_little_key()`) and sets the flag
  (`set_var_game(220, 1)`); without one the door stays shut (silent, like the game's own key doors; keys are lost on entering a scene, so
  the lamp's key has to be used in Lupin Burg). The game saves all 255 flags in every save game, so the door stays unlocked in later saves.
  Flag 220: `var_game(n)` is read by the game's scripts for n = 0..219 (except 27, the clover, and 148), and never for 159..199 or
  220..254 (scanned in the decompiled scripts of all 120 scenes and in the engine's sources, which only touch 30, 90 and 134 by number), so
  any of those is free; 220 is the first of the untouched tail and is above the inventory range (0..27, which the "consigne" flag hides),
  with 221 and 222 left free. An installed door that opens for anyone is patched in place (the script text is replaced when it is the
  standard door's, anything else is refused). Tests: `store surprise` (script text, upgrade, refusal) and `runtime doors` (`DoorLock`: no key,
  door stays shut; one key, it opens, the key is spent and the flag set; later, with no key, it opens).
  The confirmation of Tools > LBA1 > *Make surprise changes* deliberately says nothing about what it changes.
  Sixth, the bedroom's extras (`Lba1SecretRoomExtras`): a **meca penguin** that walks between two track points (along x 56) and is taken on touch (scene 60's own penguin
  actor, entity 9, with its scripts; the hero's life script does what the rebel village's does: `set_var_game(14, 1); kill_obj; found_object(14)`, game flag 14
  being the penguin item), and **twelve mushrooms**, for which LBA1 has no model: BODY.HQR gets a new body built from points and polygons (a stem under a red cap with white
  spots, one bone, lit like the retail bodies, its cap's box 420 wide so that Twinsen fits between two of them; header and bone record from the ID card body 78, whose entity 42
  has the one-bone, one-frame animation such a body needs) and entity 42 a record for it (body id 1; an earlier shape of the mushroom is replaced in place). They stand as **two
  smiley faces**, five cells square, either side of the door lane (z 54..56 stays free; the elf is at 57, 55): two eyes, a nose and a mouth of five that curves up at both ends (two cells apart, so Twinsen can walk between them).
  The eyes are **kash coins worth 50** (see below). The left face (larger z, as the picture shows it) gives **clovers** with a **heart worth 50** for its nose, the right face **clover boxes** with the **magic bottle worth 80** for
  its nose. The rewards come from the actors' own scripts, not from bonus zones: the hero's life script sets a scene variable (`var_cube`, one per mushroom) for the nearest mushroom
  within 700 units (`distance` can only be compared with a constant, so it tries the mushrooms at 500, 550 ... 700 in turn), once per press of action (a variable of its own is held
  until action is let go). A clover, heart or bottle mushroom then does `give_bonus(1)` (the actor's `OptionFlags` say which bonus, `NbBonus` how much, the popped extra rises from the
  actor's top; with no magic yet the engine turns the bottle into a heart, `EXTRA.C`) and `suicide()`: the mushroom is gone for that visit and back on the next. A bonus cannot be a
  clover box, so those work like the game's own clover boxes (scene 25's sprite 41 actor, in the mushroom's own place: the mushroom is used up as the box shows): the box stays hidden (`invisible(1)`) until its
  mushroom is asked, the mushroom suicides, the box appears, and touching it does `inc_clover_box()` and sets one game flag (**221..225**, one per box) so each is given once, ever; a
  mushroom whose flag is set suicides as the scene starts, and so does its box. Game flags: the engine keeps 255 (`MAX_FLAGS_GAME`), saves and loads them all and clears them all for a
  new game, and none of the 120 scenes' own scripts touches 159..199 or 220..254 (checked by `store surprise`), so 220 (the door's "Door unlocked") and 221..225 are free. The same
  **The coins** (the eyes): a bonus the game pops out is taken away again after 20 seconds (`EXTRA.C`: `TimeOut = 50 * 20`, only the key, sprite 6, is exempt), so a coin that just lies
  there can't be a bonus. Each is a sprite actor (a clone of the clover box, sprite 3 = the game's kash) that stays; when Twinsen touches it (`col_obj(0)`) it does `give_bonus(1)`
  with `OptionFlags` 16 and `NbBonus` 50, the engine pops out a real coin worth 50 that flies to him (and respects the 999 limit), and `suicide()`. No game flag: like the clovers they
  are back on the next visit. (`give_gold_pieces` can only take kashes away; a negative amount would rely on the unsigned wrap of `NbGoldPieces`, which no retail script does.)
  **The doorway:** an earlier layout put a clover box one cell towards the door lane from its mushroom, which was exactly where Twinsen arrives when he walks in along the lane's
  north edge (the zone in the street arrives at destination + offset, so the recess's five lines of z map onto the room); the box pushed him east into the room's doorway zone
  and he was sent straight back out, over and over. Nothing stands within 512 units of those rows now (a `runtime doors` check walks in along every line of the doorway).
  tool fills the **hole in the bedroom's floor** (one column of the floor at x 59, z 62 had no brick: the cell gets block 1 position 6 like its neighbours). Tests: `store surprise`
  (actors, faces, scripts, files, flags, grid, undo/redo) and `runtime doors` (`SecretRoom`: the penguin walks and is taken, every reward pops out and its mushroom is gone, one
  press asks one mushroom, a box appears and is given once and its mushroom is gone next time). Tools: `mushroom <folder>` (the body's winding and size), `lba1entities` /
  `lba1onebone` / `lba1entity` (FILE3D records), `lba1sprites <folder> <png>` (a contact sheet of SPRITES.HQR), `lba1plan`, `lba1bodywinding`, `hqrcmp` (entries that differ).
- Actor bodies come from `FILE3D.HQR` (entity, body variant) -> `BODY.HQR`, rendered through Body Studio's renderer.
  Double-clicking an actor (or right-click > Edit Attributes) opens `Lba1ActorAttributesWindow`, laid out like the LBA2
  one: position and facing, entity / body / animation (listed as `index: description`, from the LBAPackageManager
  descriptions embedded in the exe), life, armour, hit force, move type and flags, and a rotating preview that plays the
  chosen animation (pause and rotation speed as in LBA2). LBA1 has no live renderer to hold session edits, so *Apply*
  writes the actor's header in `SCENE.HQR` (first save keeps `SCENE.HQR.bak`), like the zone inspector. *Edit Script...*
  opens the same C script editor as LBA2 (see below) on the LBA1 scripts.
  An animation only moves the bones of the body it was made for, so picking a body keeps the animation in step. LBA1: the list is the entity's; an animation with fewer bones
  than the chosen body (14 of 1832 pairs: entities 18, 19, 23, 45, 47, 48, 57, 81) is marked "not for this body" in the list and, when it is the current one, gives way to the
  entity's standing animation or the first that fits (`lba1bodyanim <folder>` lists them). LBA2: the window's body box takes any BODY.HQR entry, so picking one now rebuilds the
  animation list from the entities that have that body (`Lba2EntityTable`, RESS.HQR entry 44: F_BODY / F_ANIM records) - only those with as many groups as the body has bones
  (an entity can hold bodies of several skeletons, each with its own animations), then a separator and the other named animations with that many groups - and an animation that
  isn't one of them is replaced by the body's standing animation (generic animation 0) or the first; a body no entity has (one made with Body Studio) leaves the list alone.
  The native preview only refuses animations of more than 31 groups, so before this a mismatched pair drew the body with the wrong skeleton's motion.

**Saving an LBA2 actor edit (2026-09-22):** Apply always updates the live native session first, then also writes the edit into the actor's own scene record in
`SCENE.HQR` (`Lba2ActorPersistence`), through the same `SceneStore`/`SceneHistory` path every other saved LBA2 edit uses (zones, the door tool, LBA1's own actor
window) - one validated, `.bak`-kept save, one Undo/Redo entry, for free. Finding which scene record to patch, and where in it, starts from the native actor's
flat scan index (`lba2_renderer_get_actor_scene`, a new export mirroring `lba2_renderer_get_zone_scene`): both the whole-island scan and one interior scene's own
scan list a scene's actors contiguously and in the scene file's own order, so counting how many earlier flat-list entries share the same scene gives that actor's
position in `SceneModel.Actors` (the hero is `Actors[0]`, so add one). Checked empirically, not just from the native source, by `lba2actorlocate` in
ScriptRoundTrip: all 714 exterior actors of 5 islands and 98 interior actors of 7 scenes, cross-checked position/facing/life/armour/hit force/move against
`SceneStore`'s own parse, zero mismatches. Only the fields that actually *changed* (compared with what the window loaded, not with what the file has right now)
are written, everything else is left exactly as the file already has it - not just the fields this window doesn't expose, but any hidden gap between the file's
own convention and what the live session reports: LBA2's `LifePoints`/`Armor`/`HitForce`/`Move` are signed bytes (`S8`) on disk, so 255 there reads back as -1 (and
has to go back in as -1 for the same byte to come out again), and the file's own `LifePoints` for an interior scene's first actor was found to read -1 while the
live session already reports 0 for it (an engine-side normalisation, not a bug in this) - writing back "whatever the session currently shows" for a field nobody
touched would have quietly corrupted it. Body/animation are the one exception that can't always be saved: the file stores a small id relative to the actor's own
kind of actor (FILE3D entity), not the raw `BODY.HQR`/`ANIM.HQR` index the picker shows, so a raw index is only written when it is already one of that entity's own
(switching between a character's own body/animation variants); anything else stays session-only, and the status line says so rather than guessing. An exterior
(island) scene stores a position relative to its own cube, while the live session reports the world-absolute one (the cube's offset added in, `SceneModel.CubeX`/
`CubeY * 32768`); `Lba2ActorPersistence.Save` subtracts that back out before writing (a real bug this feature's own UI testing found: only visible for a scene
whose cube isn't `(0, 0)`, where the offset is zero regardless, so a plain byte-range check on save can't be trusted to catch every case). Tools:
`lba2actorlocate <folder> <ISLAND | interior <scene>...>`, `lba2actordump <folder> <scene> <indexInScene>`.

**Add Actor Here can now be saved too (2026-09-22):** a brand-new actor has no "kind of actor" yet (the file needs one to give it a body/animation at all, being
small ids relative to an entity, not raw archive indices), so `ActorAttributesWindow` gained an Entity picker (`EntityPanel`/`EntityCombo`, the same
`FilterableComboBox` as Body/Animation, shown only for a new actor and named after its first body's own `BODY2.HQD` name, e.g. "14: Nitro-meca-penguin" - there is
no separate LBA2 entity-name file to read instead) that narrows Body to the chosen entity's own bodies and re-syncs Animation the same way changing an existing
actor's body already did. Apply then adds the actor to the scene file (`SceneOps.AddActor`, appended - the file only has one to diff against once it already
exists, so a new actor's every field is written fresh) instead of refusing forever; the picker disappears once the actor is saved, since it isn't "new" any more.

**Window habits (2026-09-22):** `WindowLifecycle`/`WindowPlacement` give every substantial secondary window (both actor-attributes windows, the script editor, the
grid/object/asset editors, both scene editors, the LBA1 play window) the same two things, in one shared implementation rather than each window its own copy: it
remembers its own kind's last position and size (`EditorSettings.WindowPositions`, keyed by window kind so several open at once - one per actor, say - cascade off
that one remembered spot instead of landing on top of each other) and falls back to its ordinary centred placement when that spot is no longer on any currently
connected monitor (`Screen.AllScreens`, not just "somewhere in the combined virtual desktop", which would still say yes for a spot a since-unplugged monitor
happened to overlap); and closing the main window closes every one of them first, each running its own Closing handler (an unsaved script edit still asks before
it is lost) - if any refuses, the main window's own close is cancelled too rather than orphaning the rest. `UiBusy` (`MainWindow`'s new `BusyPanel`/`BusyLabel` in
the status strip) gives a slow-looking operation two tiers of feedback with one call: `UiBusy.Cursor()` (a short one, just the wait cursor: Apply in either actor
window) or `UiBusy.Progress(panel, label, text)` (also a small indeterminate strip: loading an island, drawing a joined map) - a `using` scope that forces one
render frame through before the blocking work starts, so the cursor/strip really do paint first, and disappear the instant the operation returns or throws.
`ActorAttributesWindow`'s own Body/Animation combos are the shared `FilterableComboBox` now (see the main window's Island/Scene pickers, or LBA1's own actor
window), not a second, separately-maintained implementation of the same type-to-filter behaviour.

## Modes: Explore, Build, Script

The main window has three modes, chosen with the buttons at the top of the right-hand column (or Ctrl+1 / Ctrl+2 / Ctrl+3, or
the View menu). The window title shows the game folder the editor is working in.

- **Explore** only looks: orbit (left drag), pan (middle drag or the scroll bars / arrow keys) and zoom (wheel) the scene, toggle
  zone / path layers, select actors and zones. Nothing can be changed (no add-actor menu, the zone fields are
  read-only); double-clicking an actor opens its attributes window to look at, with Apply off (in Build mode the same double-click
  edits, and the context menu's Edit Attributes always does).
- **Build** makes the changes. The **BUILD** tab offers two sets of tools. *Terrain* sculpts an LBA2 island **in the same 3D view
  as Explore, live**: pick a tool (`IslandEditorView`, see "LBA2: island terrain editor"), paint on the ground under the mouse
  and the view shows the edit as you make it; "Move the view" is the tool that just orbits. While a terrain tool is chosen the
  right button orbits and the middle button pans. *Actors and zones* is the ordinary view with right-click *Add Actor Here*,
  double-click an actor for its attributes and the zone data editable under DETAILS. Interiors and LBA1 scenes have the second
  set, plus buttons for the scene editor, the interior map (grid editor), bricks and sprites, objects and bodies and Body
  Studio. Unsaved terrain edits stay when you change mode; Edit > Undo / Redo, File > Save and the Ctrl keys act on the terrain
  editor while its tools are showing; switching island or game, or closing, asks about unsaved terrain edits (save / discard /
  stay).
- **Script** is for the actors' scripts: click an actor, or double-click it in the **SCRIPT** tab's list of the actors in view,
  to open its script window (right-click an actor in Build mode also offers *Edit Script...*, which switches to this mode).

**Colours and text.** The interface uses a light blue scheme shared with a sister project (windows #E8F0FA, surfaces #F3F8FF, inputs and
lists #FFFFFF, alternate rows #EAF3FD, buttons #D6E6F7 with hover #C3DBF5 and border #9FBEE0, headers #D2E3F6, menus #DCEAFA, borders
#A9C3E0, text #10243E, muted text #4E6B8A, disabled #7C93AC, selection and accent #1B6EC2 with white text). Windows' own title bars stay the
system's. Body and object views are drawn on the window colour; the actor markers keep the darker key colours their transparency is cut
from (`Renderer.KeyBackground`). The window XAML files carry those values inline; `Theme.xaml`
(merged in `App.xaml`) themes what they leave to the system: the system colours the default controls draw with, buttons, combo boxes
and their lists, scroll bars, tabs, tool tips and menus (`MenuItem` templates without a tick / icon column, so no menu shows check
marks or an empty border for them). Labels are sentence case ("Zones in view", "Play scene"), never all capitals.

**Joined LBA2 interiors.** LBA2's interior scenes that lead into one another through cube-change zones are drawn as one map too (`Lba2Areas`), 24 maps
in all (`lba2groups <folder>` lists every group of interiors whose zones lead into one another both ways): on Citadel Island Twinsen's house, Tralu, the tavern,
the baggage claim building and the sewer with the rooms off it; on the White Leaf Desert the Esmer base, the Turkish baths with the hacienda, the School of
Magic and the protection spell cave; Emerald Moon's Inside Base (Baldino's cell and the buildings around it); on Otringal the Imperial Hotel, the elevators, the prison, the casino, the bar, the souvenir
shop and the Emperor's palace (the sixteen rooms of the maze and the last room); Wannies Island's mine (its first three rooms and the entrance: the mine's temple and the box transport building do not fit with them and stay scenes of their own) and city; the Mosquibee queen's throne; Celebration Island's
Dark Monk Statue (five scenes really stacked one above another where their zones put them in the plan, each lifted so far above the one below (`Lba2Areas.Separations` Dy: 187 +87, 192 +107, 185 +162, 186 +194; `lba2screenlift <folder>` finds them from the scenes' screen silhouettes) that its picture stands clear of the lower one's, with black space between the levels; only shared cells could count there, not shared plan columns: `Lba2Areas.IsStacked`; the Celebration Island and Desert Island menu names are fixed in `Lba2IslandNames`; LBA2's scene descriptions say "White Leaf Desert" and are shown as "Desert Island", as the game itself calls it); Francos Island's Gazogem factory; and Island CX's control tower as two maps, the upper level (the stairs, the tower and the only outside scene)
and the lower level (the room in the emperor's palace and the secret passage: put in one picture the levels lay over one another). Links whose two zones
disagree (the temple's first two scenes, the Esmer shuttle, the departure room's space port) are left out. LBA2's scenes say nothing about which interiors
belong together, so the links are written down (`Lba2Areas.Links`: the second scene of a link sits where the first one's cube-change zone to it puts it, zone
corner minus arrival point). **No two scenes of a map share a plan column** (a brick in the same x, z at any height: one floor stood over another's rooms is an
overlap on the picture as much as one in the same cells): `lba2overlaps` lists the pairs, `lba2separate` finds the smallest sideways move of one scene of each
pair (`Lba2Areas.Separations`), and `areatests` requires none. The LBA1 maps follow the same rule now (`overlaps <folder> columns`, `separate <folder> 60 columns`).
A scene with two separate areas can be drawn in two pieces (`Lba2Areas.Parts`, a `CellWindow` of cell columns moved by some cells): Island CX's stairs scene
(177) is a tower at one corner of its grid and, at the other, the small room the top of the stairs leads into (the top zone arrives there at cell (56, 8, 54) from
(9, 25, 0)), so the room is drawn 17 layers up on top of the tower. The Emperor's palace rooms overlap their neighbours' walls by up to four cells where the zones put
them 13 cells apart, so that square is drawn 17 cells apart (`lba2pitch` finds the least pitch). The picture is drawn by the managed grid renderer (`Lba2Interiors`:
LBA2's grids, blocks and bricks are laid out like LBA1's, drawn with `RESS.HQR`'s palette; a single scene still goes through the native engine) with every actor's own body
(the entity's body from FILE3D, entry 44 of RESS.HQR, then BODY.HQR: `Lba2Interiors.BodyIndex`, drawn through the same renderer as LBA1's markers; a dummy figure (the placeholder body, `Assets/DummyBody.lm2`, rendered through the same marker code as every body: at its real size, its ground grid line made transparent) with a
pale glow where the actor has none; an actor goes with the piece of a split scene it stands in) and every zone on top, on a black background (all the scene pictures and the
minimap are on black, for contrast). The maps are listed under *Connected interiors* in an island's menu and first in its scene list; the same **Join connected areas** box turns
them on and off (one setting for both games) and an interior on screen becomes its map or its own scene. A joined LBA2 map is view only (Build and Script are refused in it,
Play starts at its first scene); double-clicking an actor opens that actor's scene on its own, where it can be edited. Commands: `lba2links <folder> <text>...` (the
cube-change zones of the scenes whose description has the text, with the offset each zone gives and the zone back), `lba2groups`, `lba2areas`, `lba2overlaps`,
`lba2separate`, `lba2pitch`, `lba2bodies <folder> <scene>...` (each actor's resolved body), `lba2actors`, `lba2footprint`, `lba2plan` (a scene's plan in text) and
`lba2render <folder> <png> <map> [outline]`; `areatests` checks the scenes, the offsets, that the maps never overlap and that they draw.

**Native renderer crash fixed (2026-09-21).** Opening a scene with a 3D-sprite animation (Citadel scene 13 has one: actor 12, `ANIM_3DS`) killed the editor: the renderer-only boot
never loaded `ListAnim3DS` (PERSO.CPP's `LoadListAnim3DS`), so `StartInitObj` read through a null table (Windows event 1000, `coreclr.dll` 0xc0000005, managed top
frame `RendererLibraryApi.LoadInteriorScene`; `LBA2_RENDERER_CRASHLOG` + `nm` named `StartInitObj`). `SOURCES/RENDERER_BOOT.CPP` (not the stale
`SOURCES/3DEXT/RENDERER_BOOT.CPP`) now loads the table. `interiorall <sandbox> [first] [last] [rounds]` loads and draws every interior scene through the native renderer
(after a first exterior frame, as the editor does) and names the scene it dies on: all 148 interiors survive three rounds. Use a hard-linked sandbox of the game files.
**Choosing what is open** is done with the **Scenes** menu: *Scenes > LBA2 (or LBA1) > Island > Area*, e.g. Scenes > LBA2 > White Leaf
Desert > "Temple of Bú". Islands carry the game's own names (LBA2 reads them from the scene descriptions in `SCENE2.HQD`), and an
area is listed as its description without the scene number, "(room #n)" or any island name (`SceneMenuNames`), first letter
capitalised. LBA2's islands list their outdoor areas (each one cube of
the island), then their interiors, or "The whole island"; a **Demo** entry lists the demo reel's scenes separately (they are
ordinary scenes of the game's islands and rooms, but the game runs them only in its attract mode). LBA1 lists each island's
connected outside maps first (the joined maps, or with joining off their scenes), a line, then its other scenes. Nothing in a
list is ticked (the menus have no tick column at all); the top bar shows the game, island and area.
(The old game / island / scene boxes are still in the window, hidden, and do the actual opening, so every path that changes
the open scene behaves the same.) Choosing an area while a scene is playing plays that area instead.

**Play a scene** is part of the window, not a window of its own. The **PLAY** tab on the right holds the **▶ PLAY SCENE n**
button (it names the scene it will start; **■ STOP** and **Restart** take its place while the game runs); Tools > LBA2: play
scene... and Tools > LBA1: play scene... do the same. The game replaces the view (the side panel stays, so the other tabs can be
used while it runs) until STOP or, for LBA2, the game's own quit: LBA2's engine (`lba2cc.exe`) runs with its window embedded in
the editor (`EmbeddedGameHost`: the engine's SDL window is re-parented into the view and fitted to the largest 4:3 rectangle; click
the game to give it the keyboard); LBA1 shows its play view (`Lba1PlayView`) there. The scene played is the one that is open: an
interior on screen is its own scene; on an island it is the scene of the cube the camera is over (each outdoor scene is one cube
of its island; picking an outdoor area moves the camera to its cube). Nothing else opens: the engine is a console program, so it
is started without a console window, and its own window is created off screen (`LBA2_WINDOW_POS`, a small addition to the
vendored engine's `WINDOW.CPP`) and taken over at once, so it is never seen as a window of its own.

**Where Twinsen starts**: with *Choose where Twinsen starts* ticked (the default), pressing PLAY SCENE first shows the scene with a
blue Twinsen marker on it, standing at the scene's own start; drag him anywhere on the ground (an island: the terrain under the
pointer, found by following the pointer's ray through the native camera against the island's heights, so he can be dropped in any
cube of the island and the scene of that cube is played; an interior: the highest floor under the pointer that Twinsen can stand on, from the native brick grid, so a walkway stays a
walkway, the floor below it can be picked where it shows and walls are never chosen; an LBA1 scene: the floor under the pointer at
his height) and release to start, or press START HERE (Esc / Cancel leaves without playing). LBA2 is started with the engine's
console command `teleport x y z` (cube-local coordinates) after the scene loads (the run is given a long `--tick` budget: without
one the engine's command harness disarms itself after the first tick and every `--exec-at` later than that is silently dropped);
LBA1's `Lba1PlayView` places the hero directly.

**Zone boxes and actor paths in the game.** Both games draw the layers of the Zones tab and the Paths button while they run. LBA1's
play view draws the zone types that are ticked. LBA2's engine has an editor overlay of its own (`EDITOR_OVERLAY.CPP`, called at the
end of `AffScene`): it reads the file named by the environment variable `LBA2_OVERLAY_FILE` (`zones=<bit mask of the zone types
0..9> paths=<0|1>`, checked every 15 frames, so ticking a box acts within a moment) and draws the scene's zones as boxes in
their type's colour and the actors' routes as lines with a marker at each point, projected with the engine's own camera.

The **PLAY** tab holds the **sound balance, per game**: mute all sound, and separate levels for music, voices and effects (the
games' own balance is off: the music drowns the speech, so the defaults are music 40 %, voices 90 %, effects 70 %; Reset balance
restores them). They are saved with the settings. LBA2's engine reads its volumes when it starts, so the levels are written to
`lba2.cfg` in its own folder (`WaveVolume` = effects, `VoiceVolume`, `MusicVolume` and `CDVolume` = music) before it is started
and muting starts it with `--no-audio`; the LBA1 play view applies them at once (samples and speech are scaled, the music is
scaled and started again at the new level, its MIDI channel volumes rewritten in `AudioLevels.cs`) and has the same controls in its
own toolbar. For LBA2 the tab also has console commands that run once the scene has loaded (the game runs whether or not it has
the keyboard focus, so it doesn't wait for a click). The game plays what is saved on disk; with unsaved terrain edits you are
asked to save first.

## Minimap and the Zones tab

The minimap shows the outdoor island; while an LBA2 interior or an LBA1 scene is open it shows that scene instead
(a thumbnail, a dot per actor and a box for the part in view; click it to recentre). The Zones tab toggles the scene
zones (all, or per type) and the actor patrol paths, in the main views and on the island minimap.

## Zone inspector (DETAILS tab)

Click a zone's outline in the map (or pick it from the list; the ↻ button re-reads what is in view) to select it: it is
drawn thick and white and its data opens on the DETAILS tab. The tab shows the zone's bounds (min/max, editable), its
size (width × height × depth in scene units and in cells) and its type-specific data: for a cube change, the scene it
leads to (the area code) and the arrival position; the other types name their own fields (camera shot, scenaric zone
number, giver bonus, hit damage, ...; LBA2's meanings are in `docs/ZONES.md`). *Go to destination* opens that scene.
*Apply* patches the zone record in `SCENE.HQR` in place (the first save keeps `SCENE.HQR.bak`; the file is written
beside the original, read back and swapped in) and redraws. Zone edits are refused while the scene has unsaved script
edits, and an outdoor LBA2 edit reloads the island (which drops unsaved actor edits), so close the actor windows first.

## Scenes: model, validation, saving and undo

Every write to `SCENE.HQR` / `LBA_GRI.HQR` goes through one layer (`Scenes/`), built so that scenes can be created and
edited as data rather than patched byte by byte:

- **`SceneModel` / `SceneSerializer`**: a whole scene record for either game, read and written field for field as the
  engine's `LoadScene` does (LBA1 `DISKFUNC.C`, LBA2 `DISKFUNC.CPP`): header, hero, every actor (attributes and both
  scripts), zones, track points. All 120 LBA1 and 222 LBA2 retail scenes parse and write back byte for byte.
  Scenes can't be added to the games (their scene tables are fixed), only replaced: `SceneDocument.SaveAs` saves a scene
  into another existing slot.
- **LBA2 patch table.** The end of an LBA2 record lists (size, offset) of bytes the engine saves and restores in saved
  games. They are exactly the `SWIF` / `ONEIF` opcodes of the life scripts and the run-time timer / angle fields of eight
  track instructions, so `Lba2Patches` rebuilds the table from the scripts on every write (checked against all 12,548
  retail patches). Before this, a longer script left every later offset stale. LBA2's `SCENE.HQR` entry 0 holds the size
  of the largest scene, from which the engine sizes its scene buffer once; the store raises it when a saved scene
  outgrows it.
- **`SceneValidator` / `Lba1GridValidator`**: the engine's limits, taken from its source: 100 actors, 255 zones, 255
  track points, script sizes, scripts that decode with every jump on an instruction boundary, actor and track-point
  references, cube-change targets; for LBA1 grids also 25 cells per column, the block-in-use bitmap and the 361,472-byte
  brick buffer (retail's largest scene uses 351,894). Retail data has no errors, so an error is a real problem; a save
  with errors is refused.
- **`SceneStore`**: loads and saves scenes (and LBA1 grids) as one verified `FileTransaction` (one-time `.bak`, written
  beside, read back, swapped in; all files or none). The zone editor, both actor-attributes windows (see above), the
  script editor and the door tool all save through it.
- **Edit > Undo / Redo** (Ctrl+Z / Ctrl+Y, from anywhere in the main window - a focused text box keeps its own undo
  instead) walks `SceneHistory`, the log of those saves; each step names what it did (the Edit menu shows it, e.g.
  "Undo Edit actor 3 of scene 93") and restores the exact previous bytes, for every game (an LBA2 actor save is on the
  same log as an LBA1 one). Before writing a step's recorded bytes back, it re-checks the game files still match what
  that step expects; if something else changed them since (another tool, or a step loaded from a previous session
  whose files were then edited outside the app), it refuses with a clear message rather than overwriting that change.
  `SceneDocument` is the in-memory counterpart for upcoming editors: copy-on-edit with merged undo steps, dirty
  tracking, save / revert / save as, and an auto-save mode.
- **Everything that edits a game file is on that one log now (2026-09-22, `HqrEntryStore`).** Grid and block-library
  edits (both games), the brick and sprite editor, the object browser's Replace, and the extra files a scene save
  bundles alongside it (`Lba1SurpriseChanges`/`Lba1RoomDoorMod`: a new body, a FILE3D record, new dialogue text) used
  to write straight through a `FileTransaction` with only a one-time `.bak` for safety - Undo could take a scene back
  but left those other files as they were (a real gap: undoing the pink elf used to leave its body sitting unused,
  harmlessly, in `BODY.HQR`/`FILE3D.HQR`/`TEXT.HQR`). `HqrEntryStore.Save` gives these the same one undo step and the
  same all-or-nothing transaction as a scene save, while only ever keeping the entries that actually changed for the
  log - never a copy of the archive they live in (`BODY.HQR`, `LBA_BKG.HQR` and `SPRITES.HQR` can run to several
  megabytes; a replaced entry is a few KB). The one exception, kept as a whole-file undo step instead: adding a brick
  to LBA2's palette inserts its slot before the table that follows the brick range, shifting every later entry's
  number by one - a real change to the archive's own layout that a plain replace-or-append can't express, so
  `GphLibrary.Save` falls back to `HqrEntryStore.SaveWholeFile` for that one operation only (`tools/ScriptRoundTrip
  newbrick` proves it end to end against the real engine, adding a brick and confirming Undo/Redo of it too).
- **A new HQR entry can be named too, not just added (`HqdWriter`).** LBAPackageManager's own `.HQD` text sidecars
  (`HqdDescriptions`, one description per line, seeded into the embedded `Assets/FileDesc/*.HQD` reference copies) are
  read-only there; `HqdWriter.Describe` writes a game folder's *own* sidecar (`BODY.HQD` next to `BODY.HQR`, etc.),
  seeded from the reference file the first time so retail entries keep their known names, and lands in the same
  transaction and undo step as the entry it names. Wired into the two places that already add a body of their own:
  the pink elf and the mushroom (`Lba1PinkElf`/`Lba1SecretRoomExtras`) - `BODY.HQD` gains "Pink elf (Floppy), added by
  the level editor..." and "Mushroom, added by the level editor..." lines for their new entries.
- **Undo history settings** (File > Settings): how many steps to remember and a total storage limit in MB, both
  configurable (defaults 100 steps / 50 MB). The log is kept on disk (`undo_history.dat`, next to the exe, same
  portable-path rules as `settings.json`) so it survives an app restart. Reaching the step limit drops the oldest step,
  same as it always did at a fixed 100; reaching the byte limit first asks whether to clear the whole log to make room
  or just trim the oldest steps one at a time (declining does the latter). "Clear undo history now" in Settings empties
  it on demand. `tools/ScriptRoundTrip` never sets a disk path for its own runs, so its thousands of test saves stay
  in memory only and never touch disk for this.
- **File > Save / Open** (Ctrl+S / Ctrl+O) run the same menu commands as clicking them; every window with its own
  meaningful "save" (both actor-attributes windows' Apply, the script editor, the grid/asset/scene editors) binds
  Ctrl+S to that instead while it has focus. Every menu item has its own access-key letter (Alt, then the underlined
  letter, opens or picks it), unique within its own menu.
- **`ActorPrefabs`**: ready-made actors modelled on retail ones (today the two LBA1 sliding doors), each tested to
  reproduce the actor it was measured from.
- **HQR layer**: `HqrFile` (slots: replace, add, clear, remove; rebuilds all 32 retail HQRs byte for byte), `HqrLz`
  (LBA's LZ compression, used only when it is smaller and the engine can decompress it in place), `FileTransaction`.

Tests (read-only against the game folders; anything that writes uses temporary copies):
`dotnet run --project tools/ScriptRoundTrip -- foundation all | store all | runtime all | validate | gridvalidate | patches`.

## LBA2: play mode and scene editor

**Tools > LBA2: play scene (the full engine)...** starts `lba2cc.exe`, the complete LBA2 community engine
(`native/lba2-classic-community`, all of its assembly is ported to C++), against the LBA2 game folder, so what is saved on
disk is what plays: the real game, with its renderer, scripts, combat, audio and cutscenes, not a re-implementation. The
engine is statically linked and embedded in the editor's exe like the renderer library (extracted to `native\` beside it
on first use; a development checkout uses its build output). `Lba2Engine` / `Lba2Play` build the command line:
`--game-dir` (the folder), `--user-dir` (saves, settings and log go in an `lba2-play` folder of their own), `--no-autosave`,
`--resolution`, and `--load EDITORPLAY` to enter the scene: the engine's own start is a new game whose opening dialogue blocks the
tick that `--exec-at 5 "cube N"` would run on (the game then stayed in scene 0), so `Lba2Play.PrepareSceneSave` first runs the
engine headless for a couple of seconds, enters the scene with `cube N` and writes a save there (`savebug`), and the visible run
loads that save; anything typed in the PLAY tab's console box runs once the scene has loaded (`give`, `vargame <n> <value>`, `behaviour`, `teleport`, `weapon` ...; *Every
item* fills in the inventory variables 0..40). It was chosen over a C# port because the engine is already a faithful,
maintained game and only a launcher was needed; the parts worth porting (the data layer) are in C#.

**Tools > LBA2: scene editor...** (`Lba2SceneEditorWindow`) is the LBA1 scene editor's counterpart for LBA2: a
`SceneDocument` over `SCENE.HQR` with a plan of the scene seen from above (x right, z down, a grid of cells) on which
actors (with their facing), zones (in their type's colour) and track points are drawn, picked, dragged, added, deleted
(references renumbered by `SceneOps`) and duplicated, their numbers edited (actors: body, animation, flags ...; zones: the
per-type fields; the Scene tab: island, cube, light, music, ambient sounds), with undo, validation on Save (the patch
table is rebuilt and the engine's scene buffer size in entry 0 is raised when needed), **Play scene** (starts the engine
with the last options), scripts in the C script editor, and *Save into another slot*. It has no terrain or interior map
(LBA2's are drawn by the native engine), and no blank-scene generator yet (an LBA2 interior's map lives in `LBA_BKG.HQR`).

Tests: `dotnet run --project tools/ScriptRoundTrip -- lba2play` starts the engine headless in six scenes across the
islands and checks it arrives in each; adds an actor to a scene with `SceneOps`, saves with `SceneStore`, and checks the
running engine has one more object.

## LBA2: island terrain editor

The terrain editor is part of the main window: **Build mode > Terrain** (Tools > LBA2: island terrain editor... jumps there),
and it edits **in the same 3D view as Explore, live**. `IslandEditorView` (over `Terrain/IslandFile`) supplies the tool panel of
the BUILD tab and an optional top-down map; the window turns the mouse position into a point on the ground (`NativeCameraModel`
fits a pinhole camera to the native renderer's own projection after every frame, and the ray of the pixel is followed down to the
terrain) and the editor paints there. Each change is written to a preview copy of the island (`LiveDataRoot`: a folder inside the
game folder holding hard links of every game file plus a real copy of the island, removed again afterwards) that the native
renderer reads instead of the game folder, so the view shows the unsaved edits about every 140 ms; only Save writes the game
folder (a `.bak` of the original). The *top-down map* option of the panel swaps the 3D view for the map, which can also show the
height, baked-light, *baked shadows*, game-code or water-depth views.
Left button applies the current tool with a round brush (radius / hardness / strength; the brush ring is drawn on the ground),
right drag orbits and the middle button pans while a tool is chosen (on the map: right or middle drag pans), the wheel
zooms, `[` `]` change the radius, Ctrl+Z / Ctrl+Y undo and redo (an undo step is one stroke; only the changed cubes are kept),
Ctrl+S saves (a `.bak` of the original, only the records that changed are rewritten, the rest byte for byte; the game folder
is in the title bar). On the map a strip under it plots the heights along the row under the pointer. Tests: `dotnet run --project tools/ScriptRoundTrip -- liveisland` (the native renderer draws an island written into a live folder while the game folder's island is untouched).

- **Height:** raise / lower / smooth / flatten to a level (Alt-click reads a level from the ground) / **level to plane** (fits a
  plane under the brush when the stroke starts and pulls the stroke onto it: the bumps go, the slope stays; tick *Horizontal*
  to level it flat, for Desert Island's uneven ground) / **ramp** between two clicks / terrace / relief scale. *Objects follow
  the ground* moves decors with the ground under them. *Weld cube borders* makes the border vertices shared by two cubes agree.
- **Light and shadows:** the stored per-vertex brightness (the ILE's LUM record) is a plain Lambert light of the terrain (azimuth =
  360° - BetaLight, elevation about AlphaLight, ~2 levels of 16 off) plus **shadows under objects**: the vertices inside a
  decor's bounding box are 4-6 levels darker in the retail islands. Tools: set light, add shadow, lighten, **remove shadows**
  (lifts vertices darker than the plain lighting back up), **cast shadows** (terrain / objects shade the ground for the light
  angle in *Bake settings*), blob shadow, **shadow under object / clear object shadow** (click an object, or the buttons on the
  selected object), *Shadows under objects* for all of them, *Bake all light* and *Remove all shadows*. The *Baked shadows* view
  paints the difference between the stored light and the plain lighting (purple = shadow), so the baked shadows can be seen.
  The high nibble of the same bytes is the **water depth** (Twinsen sinks 200 units per step on water / marsh cells): its own tool.
- **Ground:** pick a triangle (texture, game code, diagonal), paint it, paint a tile chosen on the ground atlas (drag a square on
  the atlas), paint a *game code* (water, lava, electric, conveyors ...), re-cut cell diagonals.
- **Objects:** select / drag / add / delete / duplicate decors, edit body, position, angle, game code and the hide variable
  (positions and ZVs are cube-local; the ZV moves with the object).

Tests: `dotnet run --project tools/ScriptRoundTrip -c Release -- island all` parses and rewrites all 14 retail islands byte for
byte, checks every operation (borders stay equal, levelling recovers a synthetic plane, ramps, footprints, undo restores the
file exactly, a saved island reloads); `-- islandengine` edits a sandbox copy of DESERT.ILE and renders a scene headless in the
real engine before / after (a raised hill and darkened ground change the frame as they should). Format notes are in the
project memory; the viewer's polygon layout (two triangles per cell are interleaved) and decor stride (48 bytes) were wrong and
are fixed.

## Bricks, sprites, objects and interior maps

**Tools > LBA1 / LBA2: bricks and sprites...** (`AssetEditorWindow`, over `Assets/GphLibrary`) lists the run-length pictures of
LBA1's `LBA_BRK.HQR` (8715 bricks) and `SPRITES.HQR` (118 sprites) and LBA2's bricks (`LBA_BKG.HQR`, 17903), `SPRITES.HQR` (425) and
`SPRIRAW.HQR` (167 raw sprites), shows a picture with the game's palette, and edits it: paint / erase pixels with any palette colour,
right-click picks a colour, undo, clear, **export PNG**, **import PNG** (every pixel becomes the nearest palette colour, alpha under
128 stays transparent), **new picture** (appended: a new brick or sprite; for LBA2 bricks the file's brick count and the scene -> grid
table behind the bricks are kept in step), then Save (a `.bak`, the other pictures untouched). `GphImage` decodes and re-encodes the
format (identical pixels for every picture of every file); `dotnet run --project tools/ScriptRoundTrip -c Release -- assets` checks
that, a replace and an add.

**Tools > LBA1 / LBA2: objects and bodies...** (`ObjectBrowserWindow`) lists every body of both games' `BODY.HQR` and LBA2's fixed objects
(`OBJFIX.HQR`: items, the holomap globes ...), drawn by Body Studio's renderer with the LBA2 textures (RESS.HQR entry 6 is the 256 x 256
texture page; a textured polygon carries a handle into the body's texture table and 8.8 fixed-point UVs) and the game's lighting; drag to
turn. Export the body as the game stores it or as an OBJ, replace an entry from a `.body` file (read back and checked first, a `.bak` of the
archive), or open Body Studio. Body Studio's `Body` now keeps textured polygons and static bodies, so a retail body written back keeps its
textures (verified in the engine: Twinsen rewritten with his textured jumper), and
`dotnet run --project tools/BodyPipeline -c Release -- bodyroundtrip` writes and reads back every body of both games.

**Tools > LBA1 / LBA2: interior grid editor...** (`GridEditorWindow`, over `Grids/`) edits the isometric map of an interior in both games:
the 64 x 25 x 64 grid of (block, position in block) cells, the block library (`LBA_BLL.HQR` / the libraries of `LBA_BKG.HQR`) and the bricks.
A plan of one layer (PgUp / PgDn) to paint on, the isometric picture beside it, the library's blocks with thumbnails; paint (a block's cell at
offset (i, j, k) is `(block, i + dx * (j + dy * k))`, measured on the retail grids), erase a whole block, fill a rectangle, right-click picks a
block, undo / redo; the library panel edits a block's brick and shape per cell and adds a **new block** (`GridPaint.AppendBlock`), so with *New
picture* a brand-new brick can be drawn, made a block and painted (engine-verified in LBA2: the engine draws the new brick). LBA2's grid has
the 34-byte header (style, fragment set, used-block bitmap) the LBA1 shape lacks; `Lba2GridBackend` converts. LBA1 grids save through the
scene store (validated against the library and bricks), LBA2's rewrite `LBA_BKG.HQR` (a `.bak`). Reachable from the LBA1 scene editor's
Scene menu and from the LBA2 scene editor's Scene tab, which also has **Make a blank interior** (a flat floor of the library's floor block and
Twinsen alone in the scene; `Lba2BlankScene`, engine-verified). Blocks 1..255 only (the grid's used-block bitmap has 256 bits), bricks under
20000 in LBA2 (the engine's renumbering table).

Tests: `-- grids` (both games' grids, painting, erasing, library edits, saves through the stores), `-- gridengine` / `-- newbrick` / `-- blankengine`
(the real LBA2 engine renders sandbox copies with painted blocks, a new brick and block, a blank interior), and `-- rebuild`: **a retail scene
is recreated from a blank one with editor operations only** (blank scene, add actors / zones / track points, paint the blocks of the grid) and comes
out byte-identical (LBA1 scene 28 and LBA2 scene 190: the scene records match to the byte, every grid cell matches; the invisible collision-only
cells are set cell by cell).

## Body Studio: flat pictures and game lighting

Body Studio (the image-to-body generator) gained the other direction of the pipeline: **body -> flat picture -> game-style
picture -> body**.

- **Export a flat sheet of the selected template:** any body of `BODY.HQR` (either game) drawn as the orthographic front + back
  picture the generator reads: flat palette colours, transparent background. It shows what a picture "for the game" looks like.
- **Convert the reference image to game style:** any picture of a character (a photo, a painting; a plain or transparent
  background, or a front + back pair) is cut out, smoothed, clustered into a few flat colours (default 14; the game's own bodies use
  a median of 7-8, at most 22) that are snapped to real palette entries **the game's own bodies use** (measured over all 132 LBA1 and
  461 LBA2 bodies), small specks merged away, and saved as a front + back sheet that becomes the reference image.
- **Game lighting:** the retail bodies are almost all lit (98% of LBA1 polygons, most of LBA2's): a polygon's colour is the *first
  entry of a 16-step ramp* (53% of the games' polygons) and the game adds up to ~11 (LBA1) / ~9 (LBA2) steps of light, so a body
  shows base + ~8 / ~7 on a lit surface. Generated bodies used to be flat and unlit (dark base colours in the game). They are now
  written with vertex normals and Gouraud polygons (`Body.Lit`, on by default): LBA2 type 4 with per-point normals of length
  10240, LBA1 type 9 with per-point normals of 63 / range 315, and the picture's colours are turned into ramp starts
  (`LightModel`). The preview shades lit bodies with a fixed light. Verified in the real LBA2 engine (Twinsen's body replaced in a
  sandbox `BODY.HQR`, scene 55 rendered headless: the retail body written back lit looks like the original, a generated one shows
  the artwork's colours shaded) and, for LBA1, against the game's own shading maths (`Lba1Shading`: the rewritten retail bodies get
  the same mean light within 3%).

Tests: `dotnet run --project tools/BodyPipeline -c Release -- stats|sheet|sheets|style|styletest|roundtrip|enginebody|lba1lit|formsmoke ...`
(`styletest` turns a body into shaded, noisy artwork and checks the converter recovers it: 86-98% silhouette overlap, 1-9 dE
colour error; `roundtrip` is body -> sheet -> generated body, ~75-80% silhouette overlap with the original for both rigs).

## LBA1 play mode (test a scene without DOSBox)

**Tools > LBA1: play scene...** (and the PLAY SCENE button, with LBA1 open) shows `Lba1PlayView` in the main window: the scene's isometric map with Twinsen and the actors animating on
it, doors and other sprites drawn from `SPRITES.HQR`, and the zones over the top, running live. It is driven by
`Lba1/Runtime`, a C# port of the LBA1 engine's game logic (`PERSO.C` main loop, `OBJECT.C`, `GERELIFE.C`, `GERETRAK.C`,
`FICHE.C`, `GRILLE.C` collisions, `Lba1Trig` = the engine's sine table, angle and interpolation maths) that reads the files
on disk, so whatever was last saved (an actor, a script, a door, a zone) is what plays. Arrow keys walk and turn, Space is
the action key, F1-F4 change Twinsen's behaviour (normal, sporty, aggressive, discreet: jump, punch, hide as in the game),
P pauses; walking into a scene-change zone loads the next scene as the game does; the pickers choose the scene;
*Click moves Twinsen* drops him anywhere; the side panel shows life, money, keys and clover leaves, and lists what the
scripts did.

What runs, following the engine's frame order: the life and track scripts of every actor (each on a private copy of its
bytecode, since scripts rewrite themselves), manual hero movement, animations with their key-frame blending (bodies are
drawn in their current pose, `CurrentPose`, as `SetInterAnimObjet` blends it) and root motion, the frame actions of the
entity file (blows and their force, footsteps, sounds; `Lba1Runtime.Actions`), gravity and landing, brick collisions and
the collision codes of empty cells, actor-actor and door collisions and pushing, zones (cube change, camera, message,
ladder, ...) and the hero's arrival rule for scene changes; dialogue boxes and speech bubbles with the real text
(`TEXT.HQR`, the scene's island bank), which freeze the clock like the game's and take Space to continue and Up / Down to
choose an answer (`CHOICE`); spoken dialogue (`VOX\EN_*.VOX`); sound effects, footsteps and the scene's ambient sounds
(`SAMPLES.HQR`); music (`MIDI_MI.HQR` XMIDI, converted to MIDI by `Lba1Xmi` and played through Windows' sequencer, looping;
the scene's jingle and PLAY_MIDI). The extras of `EXTRA.C` (`Lba1Runtime.Extras`): bonuses dropped by creatures, chests
and giver zones (kashes, hearts, magic, keys, clover leaves) that fly, land, flash and are picked up; projectiles thrown
by animation actions; Twinsen's magic ball (Alt, level and magic dependent, bouncing, homing back, fetching keys) and
sabre. The inventory (Shift; game flags 0..27, the names and descriptions from the game texts): Enter uses an item
(magic ball, sabre, protopack, Book of Bu, clover leaf, mechanical penguin), keys 1-4 select the weapon and protopack,
found objects announce themselves. Grid fragments (GRM zones and SET_GRM) change the map and the view redraws. The
camera is the game's own (`Lba1Runtime.Camera`: a 640 x 480 screen that recentres when Twinsen leaves its inner area,
camera zones pin it); *Game camera* shows exactly that frame. **Twinsen's state...** sets what he has (a test scene is
entered without the save game that would say): items, magic, kashes, keys, clover, life, chapter and any game flag; it
starts as a new game. **Films** (`PLAY_FLA`, and *Films...* to watch any of them) are decoded from the CD image
(`Lba1Iso` reads the raw MODE1 disc, `Lba1Fla` is a port of PLAYFLA.C: run-length and delta pictures, palettes, the
film's own sound effects from `FLASAMP.HQR`), and the CD's audio tracks (`LBA.DAT` is the cue sheet) play the tunes 1..9
as the game does when it has the disc. **Shadows** are the game's dithered ground shadows (`ShadowOf` = GetShadow,
RESS.HQR entry 4). **Actors are shaded as the game shades them**: Body Studio's renderer now reads LBA1 polygon materials
and normals, and lights flat and Gouraud polygons with the scene's light angles, each bone's turn and the actor's facing
(`Lba1Shading`, from P_OB_ISO.ASM). LBA1 itself has no weather (the engine source has none). What is still not simulated:
the holomap (its position events are logged), the intro / menu / save game screens, hit stars, and the projected
polygon materials 0..6 (dither and gradient fills, drawn flat).
Checks: the maths against the engine's own values (`runtime math`), all 120 scenes for 300 frames (`runtime smoke`), a
walk, the Lupin Burg door round trip 13 -> 61 -> 13 (`runtime walk | doors`), dialogue, text, voices, sounds and music
conversion (`runtime text | dialogue`), bonuses, the magic ball, the camera and grid fragments (`runtime extras`).

## LBA1 scene editor

**Tools > LBA1: scene editor...** (`Lba1SceneEditorWindow`) edits one scene as data: a `SceneDocument` over the game
files, so nothing is written until **Save** (which validates first, and keeps a `.bak`), and every step can be undone
(Ctrl+Z / Ctrl+Y). The scene's map is drawn with its actors (bodies and door sprites), zones and track points on top;
click one to select it, drag it to move it (it snaps to 1/4 cell and follows the floor), edit its numbers in the panel on
the right (the Scene tab holds the header: island, game-over scene, light, music, ambient samples). **Add** places a new 3D
actor (any FILE3D entity), a copy of the selected actor, one of the ready-made doors (`ActorPrefabs`), a zone of any type
or a track point where you click; **Delete** removes the selection, and for actors and track points renumbers every
reference in every script (`SceneOps`: the operand roles of the translator, plus the "which actor am I colliding with"
values scripts compare with; deleting something that is still referenced asks first and re-points the references to
Twinsen / point 0); **Duplicate** copies. *Full attributes* and *Edit script* hand the actor to the main window's dialogs
(the editor reloads when they save). **Play scene** runs what is saved. **Scene > Make this scene blank** replaces the scene
with an empty world (`Lba1BlankScene`): a flat 32 x 32 floor built from the slot's own most common solid floor block and
Twinsen standing on it, keeping the slot's island, music, light and block library; **Scene > Save into another slot**
copies the scene over another scene (the game's scene table is fixed, so a "new" scene always replaces one).

Tests: `dotnet run --project tools/ScriptRoundTrip -- store ops` (add / delete on every scene of both games, references
renumbered, tables valid) and `store blank` (blank scenes built, saved and walked on).

## Actor scripts as C

Actor life and track scripts open as C-style source in the actor script window (Life (C) / Track (C) tabs, with the read-only native disassembly in a pane on the left) with
live compile checking, and can be saved back into `SCENE.HQR` (first save keeps `SCENE.HQR.bak`). The translator is
verified byte-exact against every script in both games (LBA2: 6230 scripts, LBA1: all 1238 actors of the 120 scenes);
see [LbaScript/README.md](LbaScript/README.md) for the language, how it maps onto the engine's opcodes, and the test
tooling. LBA1 uses its own opcode tables (`Lba1Tables.cs`); it has no `switch`, and `a && b` compiles to consecutive
IFs (LBA1 has no AND_IF), so such conditions read back as nested `if`s.

## Selection highlight

The Zones tab has a *Highlight selected actor / zone* option (saved in `settings.json`): a yellow ring around the
selected actor and a thick white outline on the selected zone, drawn the same way in the LBA2 outdoor and indoor views,
the LBA1 view and the software view. Turn it off to see the plain markers.

**Debug builds only:** to run a second instance against a *copy* of the game files (for testing) without touching your
real settings, set `LBA2_EDITOR_SETTINGS_DIR` to a folder containing a `settings.json` with that copy as `GameDirectory`.
The override is compiled out of Release builds (`#if DEBUG` in `EditorSettings.cs`); unset, settings live in
`%AppData%\LBA2LevelEditor` as usual.

## Source assets

The editor reads the original resources from `E:\GOG Games\Little Big Adventure 2 - Level viewer`:

- Root `.ILE` / `.OBL` files are island terrain and decor archives.
- `SCENE.HQR` contains scene records with objects, zones, and gameplay data.
- `VOX\*.VOX` contains the language-specific voice archives.
- `VIDEO\VIDEO.HQR` contains the CD video archive.

The current viewer uses the root island archives and scene index. VOX playback, video playback, and full scene rendering are tracked as separate resource integrations because their formats and runtime behavior differ from the terrain HQR records.

**Native crashes:** the map viewer's renderer (`liblba2_renderer.dll`) logs every hardware and C++ exception raised in the
process, with module-relative return addresses, to the file named by the environment variable `LBA2_RENDERER_CRASHLOG`;
resolve the addresses with `nm -C -n` on the DLL (the image base is added to each offset). That is how a sporadic crash when
opening an interior scene was traced to the renderer's boot never setting up the engine's particle-flow tables
(`InitPartFlow`), which the animations of some actors use while a scene loads.
