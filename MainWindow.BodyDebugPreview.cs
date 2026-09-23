using System.Buffers.Binary;
using System.IO;

namespace LBAAssembler;

// ActorAttributesWindow's "Load Debug Body" button (see BodyStudio/TestBodies/mario.hqr): swaps an entry from an
// external, project-local test archive into a throwaway BODY.HQR copy the native renderer is pointed at instead of
// the real game folder (LiveDataRoot, the same technique MainWindow.Terrain.cs's StartLive uses for unsaved terrain
// edits), so the body preview + animation playback goes through the real engine without ever touching the user's
// actual install. Uses its own LiveDataRoot folder (BodyPreviewFolderName) so it can't collide on disk with a
// terrain edit's own live folder, but the native renderer itself only tracks one data-root override at a time, so
// the two still can't both be active -- StartBodyPreviewLive refuses while a terrain live preview is running, and
// vice versa (MainWindow.Terrain.cs's StartLive).
//
// BODY.HQR's own resource cache (HQR_Bodys) is opened exactly once, at native renderer process start, and
// BeginLive's data-root redirect (which ordinary island/terrain reads *do* follow) never reopens it on its own --
// so after writing the swapped-in body, LoadDebugBody explicitly calls CommunityRendererBackend.ReloadBodies to
// repoint that cache at the live copy (RENDERER_API.CPP's lba2_renderer_reload_bodies, using the retail engine's
// own HQR_Change_Ressource -- the same primitive LOADISLE.CPP already uses for every per-island resource swap on
// island load, not a new pattern). An earlier attempt at this (2026-09-22) was wrongly abandoned as unsafe: it kept
// crashing in testing, but that crash turned out to be a *different*, pre-existing bug (lba2_renderer_set_data_root
// itself re-running InitDirectories() on every live-preview start, hitting its own one-shot-boot assert) that fired
// on the BeginLive call this feature already made before ever reaching the reload -- see
// [[project-actor-attributes-and-debug-bodies]] and build-and-ui-testing's cdb recipe for how that was found. With
// that separate bug fixed, this reload works as originally designed.
public partial class MainWindow
{
    private LiveDataRoot? bodyPreviewLive;

    private bool StartBodyPreviewLive()
    {
        if (bodyPreviewLive is not null) return true;
        if (live is not null) return false; // a terrain edit's own live preview already owns the native renderer's one override slot
        if (!nativeRenderer.DirectRendererReady) return false;
        bodyPreviewLive = LiveDataRoot.Create(gameRoot, "BODY.HQR", LiveDataRoot.BodyPreviewFolderName);
        if (bodyPreviewLive is null) return false;
        nativeRenderer.BeginLive(bodyPreviewLive.Directory);
        return true;
    }

    public void EndBodyPreviewLive()
    {
        if (bodyPreviewLive is null) return;
        nativeRenderer.EndLive();
        bodyPreviewLive.Dispose();
        bodyPreviewLive = null;
        if (nativeViewActive && !interiorSceneActive) RenderNativeCamera();
    }

    // Reads the real, on-disk BODY.HQR fresh every call (so picking a different debug body never stacks changes on
    // top of a previous one) and REPLACES its last real entry with `entry` (a raw body payload, e.g. from
    // LbaBodyStudio.Hqr.Read). Returns that entry's own index, or null if the live preview couldn't be started (a
    // terrain edit is live, or the native renderer isn't ready).
    //
    // Compressed (CompressedEntry), NOT stored: the real BODY.HQR's own entries are 100% compressed (checked
    // against a retail install -- 465 method 2, 4 method 1, zero method 0/stored). CompressedEntry falls back to
    // stored only if compression doesn't help, which real body geometry never hits in practice.
    //
    // REPLACE an existing entry, not APPEND a new one past the archive's original count: appending crashed the
    // native renderer for real (confirmed 2026-09-22 with LBA2_RENDERER_CRASHLOG -- an access violation inside
    // ExpandLZ, reached via AffichageBodyPreview -> HQR_Get -> HQF_LoadClose). Matches the documented pattern of
    // this renderer's own embedding (see reference-renderer-native-crash-log memory): the full game boot does some
    // one-time setup this leaner renderer embedding skips, and a fixed-size internal table sized from the archive's
    // entry count *at process start* -- before any BeginLive ever repoints it -- is the most likely explanation for
    // why a genuinely new index (past the original archive's own count) reads garbage. Replacing an existing index
    // (tools/BodyPipeline's own `enginebody` command already proved this pattern works, including full in-game
    // animation, by replacing the hero's own body 0) stays within whatever the renderer allocated at boot and does
    // not hit this.
    public int? LoadDebugBody(byte[] entry)
    {
        if (!StartBodyPreviewLive()) return null;
        var real = File.ReadAllBytes(Path.Combine(gameRoot, "BODY.HQR"));
        var index = (int)(BinaryPrimitives.ReadUInt32LittleEndian(real) / 4) - 2; // last real entry; -1 would be the trailing sentinel slot
        bodyPreviewLive!.WriteIsland(HqrWriter.ReplaceEntry(real, index, HqrWriter.CompressedEntry(entry)));
        // See this file's own top comment: without this, the native HQR_Bodys cache keeps reading whatever was on
        // disk at process start regardless of the write above, and the preview never visibly changes.
        var reloaded = nativeRenderer.ReloadBodies(Path.Combine(bodyPreviewLive.Directory, "BODY.HQR"));
        DebugLog.Log($"LoadDebugBody: ReloadBodies({Path.Combine(bodyPreviewLive.Directory, "BODY.HQR")}) -> {reloaded}");
        return index;
    }

    // Same idea as LoadDebugBody, for ANIM.HQR instead of BODY.HQR -- swaps a hand-authored clip
    // (e.g. AnimGenerator's walk/run/idle/jump) into the SAME live-preview folder a debug body
    // already uses (the native renderer only tracks one data-root override at a time, so this must
    // share bodyPreviewLive rather than start its own -- WriteIsland is scoped to BODY.HQR
    // specifically, so this writes straight into the live folder instead, the same effective
    // technique). Requires a debug body to already be active (StartBodyPreviewLive is a no-op if
    // one is, but this deliberately does NOT start one on its own -- an anim preview with no body
    // loaded has nothing useful to show, so callers should load a body first).
    public int? LoadDebugAnim(byte[] entry)
    {
        if (bodyPreviewLive is null && !StartBodyPreviewLive()) return null;
        var real = File.ReadAllBytes(Path.Combine(gameRoot, "ANIM.HQR"));
        var index = (int)(BinaryPrimitives.ReadUInt32LittleEndian(real) / 4) - 2;
        var target = Path.Combine(bodyPreviewLive!.Directory, "ANIM.HQR");
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, HqrWriter.ReplaceEntry(real, index, HqrWriter.CompressedEntry(entry)));
        File.Move(temp, target, overwrite: true);
        var reloaded = nativeRenderer.ReloadAnims(target);
        DebugLog.Log($"LoadDebugAnim: ReloadAnims({target}) -> {reloaded}");
        return index;
    }
}
