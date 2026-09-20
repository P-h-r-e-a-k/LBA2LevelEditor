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
replaced when the exe changes) and `Body Exports\` (Body Studio's default output folder). If the folder isn't writable
it falls back to `%AppData%\LBA2LevelEditor` / `%LOCALAPPDATA%\LBA2LevelEditor`; a `settings.json` found only in
`%AppData%` from an earlier version is copied next to the exe on first run.

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
  from a west-edge link; Principal Island's water tower is placed so its motorbike actor sits on Peg Leg Street's;
  Port Belooga lies north of the military camp (the camp's north exit and Port Belooga's south gate share their columns
  and both scenes start Twinsen there); White Leaf Desert's Maze is placed from its only exit into the desert, slid
  east until it no longer shares columns with the military camp; Proxima's sea scenes (north of the city and the two
  rune stones) and Hamalayi Mountains (2) (village, sacred carrot, bunker and lake) are joined from their zones.
  On Proxima the rune-stone scenes (45 lower, 46 upper) are placed by eye rather than by a zone, so the two
  motorbikes line up (45) and the upper stone follows on from scene 47, "before the upper rune stone" (46).
  Every map is listed as `Outside (scenes ...)` unless `Lba1Areas.AreaNames` names it (`Temple of Bú`, `Mutation centre`).
- **Tools > LBA1: Make surprise changes** (`Lba1SurpriseChanges`; the door part is `Lba1RoomDoorMod`, the elf part
  `Lba1PinkElf`) edits the game files in two ways, saved together as one all-or-nothing transaction. First, so the
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
  refused with instructions to restore the `.bak` files). The joined map already shows scene 61 docked behind the wall
  (`Lba1Areas.ManualLinks`) with or without the edit. The room has no camera zone or scripts of its own.
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
- Actor bodies come from `FILE3D.HQR` (entity, body variant) -> `BODY.HQR`, rendered through Body Studio's renderer.
  Double-clicking an actor (or right-click > Edit Attributes) opens `Lba1ActorAttributesWindow`, laid out like the LBA2
  one: position and facing, entity / body / animation (listed as `index: description`, from the LBAPackageManager
  descriptions embedded in the exe), life, armour, hit force, move type and flags, and a rotating preview that plays the
  chosen animation (pause and rotation speed as in LBA2). LBA1 has no live renderer to hold session edits, so *Apply*
  writes the actor's header in `SCENE.HQR` (first save keeps `SCENE.HQR.bak`), like the zone inspector. *Edit Script...*
  opens the same C script editor as LBA2 (see below) on the LBA1 scripts.

## Modes: Explore, Build, Script

The main window has three modes, chosen with the buttons at the top of the right-hand column (or Ctrl+1 / Ctrl+2 / Ctrl+3, or
the View menu). The window title shows the game folder the editor is working in.

- **Explore** only looks: orbit (left drag), pan (middle drag or the scroll bars / arrow keys) and zoom (wheel) the scene, toggle
  zone / path layers, select actors and zones. Nothing can be changed (no add-actor menu, no double-click editing, the zone
  fields are read-only).
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

**Choosing what is open** is done with the **Scenes** menu: *Scenes > LBA2 (or LBA1) > Island > Area*, e.g. Scenes > LBA2 > White Leaf
Desert > "Temple of Bú". Islands carry the game's own names (LBA2 reads them from the scene descriptions in `SCENE2.HQD`), and an
area is listed without its island's name, which the menu already says. LBA2's islands list their outdoor areas (each one cube of
the island), then their interiors, or "The whole island"; a **Demo** entry lists the demo reel's scenes separately (they are
ordinary scenes of the game's islands and rooms, but the game runs them only in its attract mode). LBA1 lists the scenes of each
island, or the joined areas when that is ticked. The one that is open is ticked, and the top bar shows the game, island and area.
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
  beside, read back, swapped in; all files or none). The zone editor, the LBA1 actor dialog, the script editor and the
  door tool all save through it.
- **Edit > Undo / Redo** (Ctrl+Z / Ctrl+Y) walks `SceneHistory`, the log of those saves; each step names what it did and
  restores the exact previous bytes. `SceneDocument` is the in-memory counterpart for upcoming editors: copy-on-edit
  with merged undo steps, dirty tracking, save / revert / save as, and an auto-save mode.
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
