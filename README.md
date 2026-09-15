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

The editor uses the vendored native renderer at `native/lba2-classic-community` for `CITADEL` and `DESERT`. It loads the original resources from the configured game directory and renders directly through `liblba2_renderer.dll` into the WPF viewport. Camera updates do not launch the playable game or generate PNG screenshots.

The native support library excludes `PERSO.CPP` and the playable game loop. Build it with MSYS2 UCRT64, CMake, and Ninja; the generated DLL is placed under `native/lba2-classic-community/out/build/windows_ucrt64/SOURCES/3DEXT/`.

Other islands fall back to the editor's CPU terrain renderer until matching native reference saves are available.

## Source assets

The editor reads the original resources from `E:\GOG Games\Little Big Adventure 2 - Level viewer`:

- Root `.ILE` / `.OBL` files are island terrain and decor archives.
- `SCENE.HQR` contains scene records with objects, zones, and gameplay data.
- `VOX\*.VOX` contains the language-specific voice archives.
- `VIDEO\VIDEO.HQR` contains the CD video archive.

The current viewer uses the root island archives and scene index. VOX playback, video playback, and full scene rendering are tracked as separate resource integrations because their formats and runtime behavior differ from the terrain HQR records.
