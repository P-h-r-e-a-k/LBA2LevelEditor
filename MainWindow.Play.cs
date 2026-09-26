using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LBAAssembler.Lba1;
using LBAAssembler.LbaScript;
using LBAAssembler.Scenes;

namespace LBAAssembler;

// Playing a scene inside the main window. LBA2: the community engine's own window (a separate process) is embedded in the view
// (EmbeddedGameHost); LBA1: the C# play view (Lba1PlayView) is shown there. The game takes the area below the top bar until it
// is stopped (or, for LBA2, quits itself). The scene played is the one that is open: Lba2SceneToPlay / the LBA1 scene on screen.
public partial class MainWindow
{
    // Fixed rather than dynamically chosen: only one LBA2 Play session runs at a time in this
    // editor, and --listen is bound to 127.0.0.1 only (see CONTROL_SERVER.CPP), so there's no
    // real collision risk worth the extra complexity of hunting a free port.
    private const int Lba2BreakpointsPort = 27015;

    private EmbeddedGameHost? gameHost;
    private Lba1PlayView? lba1Play;
    private Lba2ControlClient? lba2Control;
    private int lba2ControlScene;
    private bool playing;
    private GameKind playingGame;

    private void PlayScene_Click(object sender, RoutedEventArgs e)
    {
        if (placing) { CompletePlacement(); return; }            // "START HERE": Twinsen stays where he is on the map
        StartPlay(currentGame);
    }

    private void Lba2Play_Click(object sender, RoutedEventArgs e) => StartPlay(GameKind.Lba2);

    private void Lba1Play_Click(object sender, RoutedEventArgs e) => StartPlay(GameKind.Lba1);

    private void StopPlay_Click(object sender, RoutedEventArgs e)
    {
        if (placing) CancelPlacement();
        else StopPlay();
    }

    private void RestartPlay_Click(object sender, RoutedEventArgs e)
    {
        var game = playingGame;
        StopPlay();
        StartPlay(game);
    }

    private void PlayEveryItem_Click(object sender, RoutedEventArgs e)
    {
        // the inventory is game variables 0..40; money (8) is a count, not an item
        var lines = Enumerable.Range(0, 41).Where(i => i != 8).Select(i => $"vargame {i} 1");
        var existing = PlayCommandsBox.Text.Trim();
        PlayCommandsBox.Text = string.Join(";", lines) + (existing.Length > 0 ? ";" + existing : "");
    }

    private void PlayClearCommands_Click(object sender, RoutedEventArgs e) => PlayCommandsBox.Clear();

    // Play pressed: the scene that is open is shown with Twinsen on it to be put where the game should start (unless that is switched
    // off), then the game starts.
    private void StartPlay(GameKind game)
    {
        if (playing) StopPlay();
        if (placing) EndPlacement();
        SaveAudioIfDirty();
        if (game != currentGame)
        {
            SwitchGame(game);
            if (currentGame != game) return;
        }
        if (PlacementCheck.IsChecked == true && (game == GameKind.Lba2 ? BeginLba2Placement() : BeginLba1Placement())) return;
        LaunchPlay(game, null, null);
    }

    // `spawn` is where the hero starts (in the scene's own coordinates), `scene` overrides the scene that is open.
    private async void LaunchPlay(GameKind game, (int X, int Y, int Z)? spawn, int? scene)
    {
        try
        {
            if (game == GameKind.Lba1) StartLba1Play(spawn, scene);
            else await StartLba2Play(spawn, scene);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            DebugLog.Log($"MainWindow: play failed: {error}");
            StopPlay();
            MessageBox.Show(this, $"Couldn't start the scene: {error.Message}", "Play scene", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowPlayOverlay(UIElement content, string status)
    {
        PlayHostBorder.Child = content;
        PlayOverlay.Visibility = Visibility.Visible;
        PlayButton.Visibility = Visibility.Collapsed;
        PlayRunningPanel.Visibility = Visibility.Visible;
        ActivatePanel(PlayTab);
        playing = true;
        PlayStatus.Text = status;
        PlayOverlay.UpdateLayout();
    }

    // Ends the game and gives the area back to the editor.
    private void StopPlay()
    {
        if (!playing && gameHost is null && lba1Play is null) return;
        playing = false;
        SaveAudioIfDirty();
        if (gameHost is not null) { gameHost.GameExited -= OnGameExited; gameHost.Stop(); }
        PlayHostBorder.Child = null;
        gameHost = null;
        lba1Play?.Stop();
        lba1Play = null;
        StopLba2Control();
        PlayOverlay.Visibility = Visibility.Collapsed;
        PlayButton.Visibility = Visibility.Visible;
        PlayRunningPanel.Visibility = Visibility.Collapsed;
        UpdatePlayButton();
        Keyboard.Focus(this);
    }

    private void OnGameExited()
    {
        if (!playing) return;
        StopPlay();
        FileLabel.Text = "The game ended.";
    }

    // ---- zone boxes and actor paths over the game ---------------------------------------------------------------------------------------

    // Bit n set = zone type n is ticked in the Zones tab.
    private int ZoneMask()
    {
        var mask = 0;
        for (var type = 0; type < 30; type++) if (ZoneShown(type)) mask |= 1 << type;
        return mask;
    }

    // The Zones tab changed while a scene is playing: LBA1's view redraws its zones, the LBA2 engine picks up the new overlay file.
    private void SyncPlayOverlay()
    {
        if (!playing) return;
        if (playingGame == GameKind.Lba1) { lba1Play?.RefreshZones(); return; }
        if (Lba2Play.UserDirectory(out _) is { } user) Lba2Play.WriteOverlay(user, ZoneMask(), pathsVisible);
    }

    // ---- LBA1 ------------------------------------------------------------------------------------------------------------------------------

    private void StartLba1Play((int X, int Y, int Z)? spawn, int? sceneOverride = null)
    {
        var directory = EditorSettings.Current.Lba1Directory;
        if (!Lba1Game.IsInstalled(directory))
        {
            MessageBox.Show(this, "The LBA1 game folder isn't set. Choose it under File > Settings.", "LBA1", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var start = sceneOverride ?? (lba1CurrentTiles is { Count: > 0 } tiles ? tiles[0].Scene : 0);
        var game = new Lba1Game(directory);          // read again, so a scene the editor has just saved is what plays
        lba1Play = new Lba1PlayView(game, new Lba1ActorImages(game), directory) { ZoneFilter = ZoneShown, Audio = EditorSettings.Current.Lba1Audio };      // draws the zone types ticked in the Zones tab
        playingGame = GameKind.Lba1;
        ShowPlayOverlay(lba1Play, lba1Session.EditedScenes.Any()
            ? "Playing what is saved on disk; scripts you haven't saved yet aren't included."
            : "Arrow keys walk and turn, Space is the action key, F1-F4 change behaviour.");
        lba1Play.ApplyAudio();
        lba1Play.Start(start, spawn);
    }

    // ---- LBA2 ------------------------------------------------------------------------------------------------------------------------------

    private async Task StartLba2Play((int X, int Y, int Z)? spawn, int? sceneOverride)
    {
        if (!Lba2Engine.IsGameFolder(gameRoot))
        {
            MessageBox.Show(this, "The LBA2 game folder isn't set. Choose it under File > Settings.", "LBA2", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (terrainEditor is { Dirty: true } editor)
        {
            var answer = MessageBox.Show(this, $"The game plays what is saved on disk, and {editor.CurrentName} has unsaved changes.\n\nSave them first?", "Play the scene", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || answer == MessageBoxResult.Yes && !editor.Save()) return;
        }

        var scene = sceneOverride ?? Lba2SceneToPlay();
        var options = Lba2Play.LastOptions is { } last ? last.WithScene(scene) : new Lba2PlayOptions { Scene = scene };
        options.Audio = EditorSettings.Current.Lba2Audio;      // (Launch writes the volumes to the engine's cfg and mutes it when asked)
        options.KeepFocus = true;                              // the game sits in the editor's window and must keep running while the side panel has the focus
        options.Commands = PlayCommandsBox.Text;
        options.Spawn = spawn;
        options.ZoneMask = ZoneMask();
        options.Paths = pathsVisible;
        options.ListenPort = Lba2BreakpointsPort;
        options.FallbackMusic = ResolveLba2MusicFallback(scene);

        var label = allSceneEntries.FirstOrDefault(s => s.Option.Index == scene)?.Option.Display ?? $"scene {scene}";
        var host = new EmbeddedGameHost();
        gameHost = host;
        playingGame = GameKind.Lba2;
        host.GameExited += OnGameExited;
        host.WantsResize += (w, h) => _ = OnGameWantsResize(host, w, h);
        ShowPlayOverlay(host, $"Starting {label} ...");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
        if (!ReferenceEquals(gameHost, host)) return;

        (options.Width, options.Height) = host.FitSize();
        string? problem = null;
        PlayStatus.Text = $"Entering {label} ...";
        var process = await Task.Run(() => Lba2Play.Launch(gameRoot, options, out problem, embedded: true));   // makes the scene's save first: a few seconds
        if (process is null)
        {
            StopPlay();
            MessageBox.Show(this, problem ?? "The game didn't start.", "LBA2: play scene", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var attached = await host.AttachAsync(process);
        if (!ReferenceEquals(gameHost, host)) return;      // stopped while it was starting
        if (!attached)
        {
            StopPlay();
            MessageBox.Show(this, "The game started but its window didn't appear in the editor.", "LBA2: play scene", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PlayStatus.Text = $"Playing {label}. It plays what is saved on disk. Click the game to give it the keyboard.";
        lba2ControlScene = scene;
        _ = StartLba2Control(scene);
    }

    // EmbeddedGameHost's own WantsResize: the host area changed enough (debounced) to be worth a live engine
    // resolution switch. `resolution WxH` is a normal console verb (CONSOLE_CMD.CPP's cmd_resolution) that
    // re-runs Init3DView on success (Res_SwitchEx, RES_SWITCH.CPP) and, since the native fix that added
    // Console_ApplyPendingResSwitch, is safe to call from here even mid-frame (it defers itself by up to one
    // frame rather than tearing down the renderer texture while a present still has it locked -- see that
    // function's own comment for the crash this replaced). Two things can make it a no-op rather than a
    // failure: no control connection yet (lba2Control null -- the socket connects a moment after the window
    // is already up, see StartLba2Control), or the engine refusing because it's mid-cinematic/dialogue/
    // inventory/holomap (Res_SwitchAllowedReason) -- both are left alone rather than retried, since the next
    // resize (or CheckSizeNow, once the socket does connect) will try again.
    private async Task OnGameWantsResize(EmbeddedGameHost host, int w, int h)
    {
        if (!ReferenceEquals(gameHost, host) || lba2Control is not { } client) return;
        try
        {
            // res_do_switch's own success line is "Resolution: WxH" (CONSOLE_CMD.CPP); anything else means
            // Res_SwitchAllowedReason rejected it or Res_Switch itself failed -- either way, nothing to confirm.
            var response = await client.SendAsync($"resolution {w}x{h}");
            if (!ReferenceEquals(gameHost, host) || lba2Control != client) return;
            if (!response.Contains($"Resolution: {w}x{h}", StringComparison.Ordinal)) return;
            // A real switch always arms a "keep this resolution? reverts in 15s" modal (Res_BeginRevertCountdown).
            // `key enter` reaches modal loops (it drives the same input layer MyGetInput reads, unlike `input`),
            // with a small delay so the press lands after the dialog's own entry-latch clears rather than
            // being drained by it -- the dialog opens on the next tick, not synchronously within this response.
            await client.SendAsync("key enter 90 3");
            if (ReferenceEquals(gameHost, host) && lba2Control == client) host.ConfirmResize(w, h);
        }
        catch (IOException) { }
    }

    // A scene's own Music/CubeJingle byte can be 255 (SceneModel.Music == -1, the sign-extended read of that
    // same byte -- see SceneSerializer.ParseLba2): the native engine's own OBJECT.CPP skips PlayMusic entirely
    // for that value, by design, so a scene reached the normal way (walking in from a neighbouring cube) just
    // keeps whatever that cube's own music already was. "Play scene" starts every scene in total isolation --
    // a cold process with nothing playing yet -- so a 255 scene played this way sits in dead silence instead
    // of the ambient theme a real playthrough would have carried into it (confirmed live: roughly two thirds
    // of scenes 0-40 are 255). Read a byte, that's most plausibly what a tester calls "the wrong music" (no
    // music at all where the scene should have some) rather than a mis-picked track.
    //
    // There's no room graph here to find which cube a player would actually have arrived from, so this uses
    // the nearest available stand-in: the scene's own island's first exterior scene (the same "the game's own
    // entry point" scene BuildSceneEntries/Lba2SceneToPlay already treat as that island's default), which is
    // where most players are actually carrying that island's theme from when they wander into a 255 room.
    // Null (no change from today) when the scene already has its own real jingle, its island can't be
    // resolved, or that fallback scene is itself 255.
    private int? ResolveLba2MusicFallback(int scene)
    {
        if (ReadLba2SceneMusic(scene) is not -1) return null;
        var target = allSceneEntries.FirstOrDefault(s => s.Option.Index == scene);
        if (target?.IslandFile is not { } island) return null;
        var fallback = allSceneEntries.FirstOrDefault(s => !s.IsInterior && s.Option.Index != scene && string.Equals(s.IslandFile, island, StringComparison.OrdinalIgnoreCase));
        if (fallback is null) return null;
        var fallbackMusic = ReadLba2SceneMusic(fallback.Option.Index);
        return fallbackMusic == -1 ? null : fallbackMusic;
    }

    private int ReadLba2SceneMusic(int scene)
    {
        var scenePath = Path.Combine(gameRoot, "SCENE.HQR");
        if (!File.Exists(scenePath)) return -1;
        var archive = HqrArchive.Open(scenePath);
        var hqrIndex = scene + 1;
        if (!archive.IsValid(hqrIndex)) return -1;
        return SceneSerializer.Parse(SceneGame.Lba2, archive.Read(hqrIndex)).Music;
    }

    // ---- LBA2 script breakpoints (the --listen control socket) ------------------------------------------------------------------------

    // Connects once the game's window is already up and running; fire-and-forget from StartLba2Play
    // so a slow or failed connection (the engine takes a moment to start listening; breakpoints are
    // simply unavailable if it never does) doesn't hold up "Playing ..." showing. Breakpoints set for
    // a different scene than the one now playing are not sent -- actor slot numbers are only
    // meaningful within whichever scene the engine currently has loaded.
    private async Task StartLba2Control(int scene)
    {
        var client = await Lba2ControlClient.ConnectAsync(Lba2BreakpointsPort, CancellationToken.None);
        if (!playing || playingGame != GameKind.Lba2 || lba2ControlScene != scene) { client?.Dispose(); return; }
        if (client is null) { DebugLog.Log("MainWindow: LBA2 control socket didn't come up; script breakpoints are unavailable this session."); return; }
        lba2Control = client;
        // The socket only just came up, so this is the earliest a live resize could have taken effect -- check
        // now for drift between FitSize()'s original sample and the host's real area today (the launch this
        // connects for takes "a few seconds", during which the host area can genuinely have changed).
        gameHost?.CheckSizeNow();
        // A breakpoint hit during ordinary, unprompted play (not the result of Continue/Step,
        // which read their own outcome from the command's response instead -- see ResumeLba2).
        client.BreakpointHit += (actor, kind, offset) => Dispatcher.Invoke(() =>
            ScriptBreakpoints.ReportExternalPause(lba2ControlScene, actor, (ScriptKind)kind, offset));
        client.Disconnected += why => Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(lba2Control, client)) return;
            DebugLog.Log($"MainWindow: LBA2 control socket disconnected: {why}");
            lba2Control = null;
            if (ScriptBreakpoints.Current is { } c && c.Scene == lba2ControlScene) ScriptBreakpoints.ClearPause();
        });
        ScriptBreakpoints.ResumeRequested = singleStep => { if (lba2Control is { } c) _ = ResumeLba2(c, singleStep); };
        ScriptBreakpoints.Changed += SyncLba2Breakpoints;
        await SyncLba2BreakpointsAsync(client);
    }

    // "paused actor=.. kind=.. offset=.." | "running", the last line of continue/step's own
    // response (see CONSOLE_CMD.CPP's cmd_continue_or_step) -- read from there rather than the
    // async "! [breakpoint] paused ..." event, which races this same response down a separately
    // flushed channel.
    private static readonly System.Text.RegularExpressions.Regex PausedResponseLine =
        new(@"paused actor=(-?\d+) kind=(\d+) offset=(-?\d+)");

    private async Task ResumeLba2(Lba2ControlClient client, bool singleStep)
    {
        string response;
        try { response = await client.SendAsync(singleStep ? "step" : "continue"); }
        catch (IOException) { return; }
        if (lba2Control != client) return;      // a new session started while this was in flight
        var m = PausedResponseLine.Match(response);
        if (m.Success)
            ScriptBreakpoints.ReportExternalPause(lba2ControlScene, int.Parse(m.Groups[1].Value), (ScriptKind)int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        else if (ScriptBreakpoints.Current is { } c && c.Scene == lba2ControlScene)
            ScriptBreakpoints.ClearPause();
    }

    private void StopLba2Control()
    {
        ScriptBreakpoints.Changed -= SyncLba2Breakpoints;
        ScriptBreakpoints.ResumeRequested = null;
        lba2Control?.Dispose();
        lba2Control = null;
        if (ScriptBreakpoints.Current is { } c && c.Scene == lba2ControlScene) ScriptBreakpoints.ClearPause();
    }

    private void SyncLba2Breakpoints()
    {
        if (lba2Control is { } client) _ = SyncLba2BreakpointsAsync(client);
    }

    // Full resync rather than an incremental diff: breakpoint toggles are rare, manual UI actions,
    // so the simplicity is worth more than the (negligible) extra socket traffic.
    private async Task SyncLba2BreakpointsAsync(Lba2ControlClient client)
    {
        try
        {
            await client.SendAsync("breakpoint clear");
            foreach (var bp in ScriptBreakpoints.ForScene(lba2ControlScene))
                await client.SendAsync($"breakpoint add {bp.Actor} {(bp.Kind == ScriptKind.Track ? "track" : "life")} {bp.Offset}");
        }
        catch (IOException) { }
    }
}
