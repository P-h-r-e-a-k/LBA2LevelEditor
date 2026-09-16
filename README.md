# LBA2 Level Workshop

A native Windows WPF viewer and editor for Little Big Adventure 2.

## Run

Build and launch with the .NET 10 SDK or Visual Studio:

```text
dotnet build
dotnet run
```

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

## Source assets

The editor reads the original resources from `E:\GOG Games\Little Big Adventure 2 - Level viewer`:

- Root `.ILE` / `.OBL` files are island terrain and decor archives.
- `SCENE.HQR` contains scene records with objects, zones, and gameplay data.
- `VOX\*.VOX` contains the language-specific voice archives.
- `VIDEO\VIDEO.HQR` contains the CD video archive.

The current viewer uses the root island archives and scene index. VOX playback, video playback, and full scene rendering are tracked as separate resource integrations because their formats and runtime behavior differ from the terrain HQR records.
