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

The WPF viewport now tries the native renderer first for any island (`nativeViewActive`), and falls back to the editor's own movable CPU rasterizer (`SoftwareTerrainRenderer`) if a given island/cube combination fails on the native side -- e.g. the hardcoded default cube `(8, 9)` doesn't exist on every island.

## Source assets

The editor reads the original resources from `E:\GOG Games\Little Big Adventure 2 - Level viewer`:

- Root `.ILE` / `.OBL` files are island terrain and decor archives.
- `SCENE.HQR` contains scene records with objects, zones, and gameplay data.
- `VOX\*.VOX` contains the language-specific voice archives.
- `VIDEO\VIDEO.HQR` contains the CD video archive.

The current viewer uses the root island archives and scene index. VOX playback, video playback, and full scene rendering are tracked as separate resource integrations because their formats and runtime behavior differ from the terrain HQR records.
