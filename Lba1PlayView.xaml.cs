using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LBAAssembler.Lba1;
using LBAAssembler.Lba1.Runtime;

namespace LBAAssembler;

// Plays an LBA1 scene without the game: Lba1Runtime (a port of the engine's game logic) runs the scene's scripts, the
// hero, doors and zones at the game's 50 Hz clock, and the isometric map (Lba1GridRenderer) is drawn with the animated
// actors, sprites (doors, keys ...), zones and speech on top. It reads the game files as they are on disk, so a scene,
// door or zone the editor has just changed can be tried at once. Arrow keys walk and turn, Space is the action key, F1-F4
// switch Twinsen's behaviour; walk into a scene-change zone and the next scene loads, as in the game.
public partial class Lba1PlayView : UserControl
{
    private const int MaxScenes = 120;
    private const double FrameMilliseconds = 40;   // one Frame() = 2 ticks of the 50 Hz clock

    private readonly Lba1Game game;
    private readonly Lba1ActorImages images;
    private readonly Lba1SpriteLibrary sprites;
    private readonly Lba1RuntimeData data;
    private Lba1Runtime runtime;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double owedMilliseconds;
    private long lastTick;
    private Lba1SceneImage? image;
    private int shownScene = -1;
    private int loggedEvents;
    private bool loadingCombo;
    private bool paused;
    private bool fireLatch;      // Space closed a dialogue box; it must be released before it counts as the action key again
    private int choiceIndex;
    // a new game's state (no items, no magic); "Twinsen's state..." sets up anything else
    private readonly Lba1Loadout loadout = new();
    private readonly Lba1MusicPlayer music = new();
    private bool inventoryOpen;
    private int inventorySelect;
    private int musicClock;

    internal Lba1PlayView(Lba1Game game, Lba1ActorImages images, string directory)
    {
        InitializeComponent();
        this.game = game;
        this.images = images;
        sprites = new Lba1SpriteLibrary(directory, game.Palette);
        data = new Lba1RuntimeData(directory);
        runtime = NewRuntime();

        loadingCombo = true;
        for (var scene = 0; scene < Math.Min(data.SceneCount, MaxScenes); scene++)
        {
            string? label;
            try
            {
                label = game.Description(scene) ?? Lba1Game.IslandNames.ElementAtOrDefault(data.Scene(scene).Island) ?? "scene";
            }
            catch (Exception error)
            {
                DebugLog.Log($"Lba1PlayView: scene {scene} unreadable: {error.Message}");
                continue;
            }
            SceneCombo.Items.Add(new SceneItem(scene, $"{scene}: {label}"));
        }
        loadingCombo = false;

        // below input priority, so key presses are never starved by drawing
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => Step();
    }

    // Starts the scene and the clock (call once the view is in the window).
    public void Start(int startScene, (int X, int Y, int Z)? spawn = null)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("LBA2_EDITOR_PLAY_SCENE"), out var testScene)) startScene = testScene;   // a hook for testing
        StartScene(startScene);
        if (spawn is { } put) runtime.Place(put.X, put.Y, put.Z, runtime.Hero.Beta);      // where the player dropped Twinsen
        lastTick = clock.ElapsedMilliseconds;
        timer.Start();
        // a hook for testing: start a film at once
        if (Environment.GetEnvironmentVariable("LBA2_EDITOR_PLAY_FILM") is { Length: > 0 } filmName) StartFilm(filmName);
        Focus();
    }

    // Key presses for the play view from the window that hosts it (they only reach it by themselves when it holds the keyboard focus).
    public void ForwardKey(KeyEventArgs e, bool down)
    {
        if (down) Window_PreviewKeyDown(this, e); else Window_PreviewKeyUp(this, e);
    }

    private sealed record SceneItem(int Scene, string Label)
    {
        public override string ToString() => Label;
    }

    private Lba1Runtime NewRuntime()
    {
        var created = new Lba1Runtime(data) { AutoCloseDialogues = false };
        created.SamplePlayed += (sample, _) => PlaySound(sample);
        created.MusicRequested += PlayMusicNumber;
        created.GridChanged += () => gridStale = true;
        created.SpeechRequested += PlaySpeech;
        created.FilmRequested += StartFilm;
        created.SpeechStopped += () => { try { voice.Stop(); } catch (InvalidOperationException) { } };
        created.ApplyLoadout(loadout);
        return created;
    }

    // ---- music ----

    private void PlayMusicNumber(int number)
    {
        if (MusicCheck.IsChecked != true) return;
        if (number is < 0 or >= 200) { music.Stop(); return; }
        // the tunes 1..9 are the CD's own recordings (tracks 2..10) when the game has the disc; the rest are MIDI
        if (number is >= 1 and <= 9 && data.HasCdMusic && data.CdTrackWav(number + 1) is { } track) { music.PlayCd(number, track); return; }
        if (data.MidiFile(number) is not { } midi) { music.Stop(); return; }
        music.Play(number, midi);
    }

    private void Music_Click(object sender, RoutedEventArgs e)
    {
        if (MusicCheck.IsChecked == true) PlayMusicNumber(runtime.Music);
        else music.Stop();
    }

    // ---- Twinsen's state ----

    private string ItemName(int item)
    {
        var text = data.Text(2)?.Get(100 + item);
        if (string.IsNullOrEmpty(text)) return $"item {item}";
        var cut = text.IndexOfAny(new[] { '.', ':', '(', '\n' });
        text = (cut > 0 ? text[..cut] : text).Trim();
        return text.Length > 26 ? text[..26] : text;
    }

    private void Loadout_Click(object sender, RoutedEventArgs e)
    {
        var window = new Lba1LoadoutWindow(loadout, ItemName) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true) return;
        runtime.ApplyLoadout(loadout);
    }

    private void StartScene(int scene)
    {
        runtime = NewRuntime();
        runtime.ChangeCube(scene);
        loggedEvents = 0;
        EventList.Items.Clear();
        SelectSceneInCombo(scene);
        ShowScene(scene);
        BehaviourCombo.SelectedIndex = 0;
    }

    private void SelectSceneInCombo(int scene)
    {
        loadingCombo = true;
        SceneCombo.SelectedItem = SceneCombo.Items.OfType<SceneItem>().FirstOrDefault(i => i.Scene == scene);
        loadingCombo = false;
    }

    private void ShowScene(int scene)
    {
        try
        {
            image = game.RenderScene(scene);
        }
        catch (Exception error)
        {
            StatusText.Text = $"Couldn't draw scene {scene}: {error.Message}";
            image = null;
            return;
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze();
        MapImage.Source = bitmap;
        MapImage.Width = image.Width; MapImage.Height = image.Height;
        OverlayCanvas.Width = image.Width; OverlayCanvas.Height = image.Height;
        ZoneCanvas.Width = image.Width; ZoneCanvas.Height = image.Height;
        shownScene = scene;
        posedCache.Clear();      // the light belongs to the scene
        gridStale = false;
        BuildZones();
    }

    // A grid fragment appeared or went: draw the map again from the run's own cells.
    private bool gridStale;

    private void RedrawMap()
    {
        try
        {
            image = game.RenderCells(runtime.NumCube, runtime.Cube.Cells);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            DebugLog.Log($"Lba1PlayWindow: redraw failed: {error.Message}");
            return;
        }
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
        bitmap.Freeze();
        MapImage.Source = bitmap;
        MapImage.Width = image.Width; MapImage.Height = image.Height;
    }

    // ---- films ----

    private Lba1Fla? film;
    private WriteableBitmap? filmBitmap;
    private double filmOwed;
    private long filmLast;

    // Watch any of the game's films (from the CD image, or a FLA folder).
    private void Films_Click(object sender, RoutedEventArgs e)
    {
        var names = new List<string>();
        if (data.Disc is { } disc)
            names.AddRange(disc.Files.Where(f => f.Path.EndsWith(".FLA", StringComparison.OrdinalIgnoreCase)).Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Path)));
        var folder = System.IO.Path.Combine(data.Directory, "FLA");
        if (Directory.Exists(folder)) names.AddRange(Directory.GetFiles(folder, "*.FLA").Select(p => System.IO.Path.GetFileNameWithoutExtension(p)));
        names = names.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        if (names.Count == 0) { StatusText.Text = "No films found: the game folder has no LBA.iso / LBA.GOG and no FLA folder."; return; }
        var items = names.Select((n, i) => (i, n)).ToList();
        if (ListPickWindow.Pick(Window.GetWindow(this)!, "Watch a film", items, 0, "The films come from the game's CD image.") is { } chosen) StartFilm(names[chosen]);
    }

    private void StartFilm(string name)
    {
        byte[]? bytes;
        try { bytes = data.Film(name); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { bytes = null; }
        if (bytes is null || SoundCheck is null)
        {
            StatusText.Text = $"The film {name} isn't on disk or on the CD image.";
            runtime.FilmFinished();
            return;
        }
        try { film = new Lba1Fla(bytes); }
        catch (InvalidDataException error)
        {
            DebugLog.Log($"Lba1PlayWindow: film {name}: {error.Message}");
            runtime.FilmFinished();
            return;
        }
        music.Stop();
        filmBitmap = new WriteableBitmap(film.Width, film.Height, 96, 96, PixelFormats.Bgra32, null);
        FilmImage.Source = filmBitmap;
        FilmBox.Visibility = Visibility.Visible;
        filmOwed = 0;
        filmLast = clock.ElapsedMilliseconds;
    }

    // Runs the film at its own frame rate; the game waits meanwhile.
    private void StepFilm()
    {
        var now = clock.ElapsedMilliseconds;
        filmOwed = Math.Min(filmOwed + (now - filmLast), 500);
        filmLast = now;
        var period = 1000.0 / film!.FramesPerSecond;
        var drawn = false;
        while (filmOwed >= period && film is not null)
        {
            filmOwed -= period;
            if (!film.NextFrame()) { EndFilm(); return; }
            drawn = true;
            foreach (var sound in film.Sounds) PlayFilmSound(sound);
            foreach (var stopped in film.StoppedSamples) if (stopped == -1) foreach (var v in voices) { try { v.Stop(); } catch (InvalidOperationException) { } }
            if (film.Infos.Contains(1) && MusicCheck.IsChecked == true && data.MidiFile(26) is { } flute) music.Play(26, flute);
        }
        if (!drawn || film is null) return;

        var f = film;
        var bgra = new byte[f.Width * f.Height * 4];
        for (var i = 0; i < f.Pixels.Length; i++)
        {
            int c = f.Pixels[i] * 3;
            bgra[i * 4] = f.Palette[c + 2]; bgra[i * 4 + 1] = f.Palette[c + 1]; bgra[i * 4 + 2] = f.Palette[c]; bgra[i * 4 + 3] = 255;
        }
        filmBitmap!.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), bgra, f.Width * 4, 0);
    }

    private void PlayFilmSound(Lba1Fla.Sound sound)
    {
        if (SoundCheck.IsChecked != true || data.FilmSampleWav(sound.Sample) is not { } wav) return;
        try
        {
            var voiceToUse = voices[nextVoice++ % voices.Length];
            voiceToUse.Stream = new MemoryStream(PcmVolume.Scale(wav, Audio.Effects / 100.0));
            if (sound.Repeat == 0) voiceToUse.PlayLooping(); else voiceToUse.Play();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException) { DebugLog.Log($"film sound {sound.Sample}: {error.Message}"); }
    }

    private void EndFilm()
    {
        foreach (var v in voices) { try { v.Stop(); } catch (InvalidOperationException) { } }
        film = null;
        FilmBox.Visibility = Visibility.Collapsed;
        runtime.FilmFinished();
        PlayMusicNumber(runtime.Music);
        lastTickReset = true;
    }

    private bool lastTickReset;


    // ---- the sound balance ----

    // The levels to play at (the main window hands over the game's saved balance; the sliders here change it live).
    public AudioLevels Audio { get; set; } = new();
    private bool audioLoading;

    // Shows the balance in the controls and applies it (sound and music switched on / off, the music's level).
    public void ApplyAudio()
    {
        audioLoading = true;
        try
        {
            MuteAllCheck.IsChecked = Audio.Mute;
            SoundCheck.IsChecked = MusicCheck.IsChecked = !Audio.Mute;
            MusicSlider.Value = Audio.Music; VoicesSlider.Value = Audio.Voices; EffectsSlider.Value = Audio.Effects;
        }
        finally { audioLoading = false; }
        music.Volume = Audio.Music / 100.0;
        if (Audio.Mute) music.Stop(); else if (runtime is not null) PlayMusicNumber(runtime.Music);
    }

    private void MuteAll_Click(object sender, RoutedEventArgs e)
    {
        Audio.Mute = MuteAllCheck.IsChecked == true;
        ApplyAudio();
    }

    private void AudioSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (audioLoading || MusicSlider is null || VoicesSlider is null || EffectsSlider is null || music is null) return;      // (the sliders raise this while the XAML loads)
        Audio.Music = (int)MusicSlider.Value; Audio.Voices = (int)VoicesSlider.Value; Audio.Effects = (int)EffectsSlider.Value;
        music.Volume = Audio.Music / 100.0;
    }
    // ---- voices ----

    private readonly SoundPlayer voice = new();

    // The spoken version of a text, when the game folder has the VOX files (one voice at a time, like the game).
    private void PlaySpeech(int textFile, int textId)
    {
        if (SoundCheck.IsChecked != true) return;
        try
        {
            if (data.Speech(textFile, textId) is not { } wav) return;
            voice.Stream = new MemoryStream(PcmVolume.Scale(wav, Audio.Voices / 100.0));
            voice.Play();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            DebugLog.Log($"Lba1PlayWindow: speech {textFile}/{textId} didn't play: {error.Message}");
        }
    }

    // ---- sound ----

    private readonly SoundPlayer[] voices = Enumerable.Range(0, 6).Select(_ => new SoundPlayer()).ToArray();
    private int nextVoice;
    private readonly Dictionary<int, long> lastPlayed = new();

    private void PlaySound(int sample)
    {
        if (SoundCheck.IsChecked != true) return;
        // a track that repeats a sample every frame would machine-gun it
        var now = clock.ElapsedMilliseconds;
        if (lastPlayed.TryGetValue(sample, out var at) && now - at < 120) return;
        lastPlayed[sample] = now;
        if (data.SampleWav(sample) is not { } wav) return;
        try
        {
            var voice = voices[nextVoice++ % voices.Length];
            voice.Stream = new MemoryStream(PcmVolume.Scale(wav, Audio.Effects / 100.0));
            voice.Play();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            DebugLog.Log($"Lba1PlayWindow: sample {sample} didn't play: {error.Message}");
        }
    }

    // ---- input ----

    private static bool Down(Key key) => Keyboard.IsKeyDown(key);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // the arrows and the space bar belong to Twinsen, not to whatever control has the focus
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Space or Key.Enter) e.Handled = true;
        if (e.Key == Key.System) { e.Handled = true; return; }        // Alt is the magic ball, not the window menu
        if (film is not null) { if (e.Key is Key.Escape or Key.Space or Key.Enter) EndFilm(); return; }
        var shift = e.Key is Key.LeftShift or Key.RightShift;

        if (inventoryOpen)
        {
            if (e.IsRepeat && e.Key is Key.Enter or Key.Escape) return;
            var oldSelect = inventorySelect;
            const int slots = Lba1Const.MaxInventory;
            switch (e.Key)
            {
                case Key.Down: inventorySelect = (inventorySelect + 1) % slots; break;
                case Key.Up: inventorySelect = (inventorySelect + slots - 1) % slots; break;
                case Key.Right: inventorySelect = (inventorySelect + 4) % slots; break;
                case Key.Left: inventorySelect = (inventorySelect + slots - 4) % slots; break;
                case Key.Enter:
                    runtime.UseItem(inventorySelect);
                    CloseInventory();
                    return;
                case Key.Escape:
                    CloseInventory();
                    return;
            }
            if (shift && !e.IsRepeat) { CloseInventory(); return; }
            if (oldSelect != inventorySelect) ShowInventory();
            return;
        }
        if (shift && !e.IsRepeat && runtime.Dialogue is null) { OpenInventory(); return; }

        if (runtime.Dialogue is { } box)
        {
            if (e.IsRepeat) return;
            if (box.Choices.Count > 0 && e.Key is Key.Up or Key.Down)
            {
                choiceIndex = (choiceIndex + (e.Key == Key.Down ? 1 : box.Choices.Count - 1)) % box.Choices.Count;
                ShowDialogue();
            }
            else if (e.Key is Key.Space or Key.Enter)
            {
                runtime.CloseDialogue(choiceIndex);
                choiceIndex = 0;
                fireLatch = true;
                ShowDialogue();
            }
            return;
        }

        if (e.Key == Key.P) TogglePause();
        if (e.Key is >= Key.F1 and <= Key.F4) BehaviourCombo.SelectedIndex = e.Key - Key.F1;
        if (e.Key is >= Key.D1 and <= Key.D4) runtime.KeyCommand((char)('1' + (e.Key - Key.D1)));
        if (e.Key is >= Key.NumPad1 and <= Key.NumPad4) runtime.KeyCommand((char)('1' + (e.Key - Key.NumPad1)));
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System) e.Handled = true;
    }

    // ---- the inventory ----

    private void OpenInventory()
    {
        if (runtime.Hero.Body == -1 || runtime.Hero.Move != Lba1Const.MoveManual) return;
        inventoryOpen = true;
        ShowInventory();
        InventoryBox.Visibility = Visibility.Visible;
    }

    private void CloseInventory()
    {
        inventoryOpen = false;
        InventoryBox.Visibility = Visibility.Collapsed;
        fireLatch = true;
    }

    private void ShowInventory()
    {
        InventoryGrid.Children.Clear();
        for (var i = 0; i < Lba1Const.MaxInventory; i++)
        {
            var owned = runtime.Owns(i);
            var cell = new Border
            {
                Margin = new Thickness(3), Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(3),
                Background = i == inventorySelect ? new SolidColorBrush(Color.FromRgb(0x1B, 0x6E, 0xC2)) : new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFA)),
                BorderBrush = i == inventorySelect ? Brushes.Gold : new SolidColorBrush(Color.FromRgb(0xA9, 0xC3, 0xE0)), BorderThickness = new Thickness(i == inventorySelect ? 2 : 1),
                Child = new TextBlock
                {
                    Text = owned ? ItemName(i) : "—", FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = owned ? (i == inventorySelect ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x10, 0x24, 0x3E))) : new SolidColorBrush(Color.FromRgb(0x7C, 0x93, 0xAC)),
                },
            };
            InventoryGrid.Children.Add(cell);
        }
        InventoryDesc.Text = runtime.ItemDescription(inventorySelect);
    }

    // ---- the loop ----

    private void Step()
    {
        var now = clock.ElapsedMilliseconds;
        owedMilliseconds = Math.Min(owedMilliseconds + (now - lastTick), FrameMilliseconds * 4);
        lastTick = now;
        if (++musicClock % 100 == 0) music.Tick();
        if (film is not null) { StepFilm(); lastTick = now; return; }
        if (lastTickReset) { lastTickReset = false; owedMilliseconds = 0; }
        if (paused || inventoryOpen) { owedMilliseconds = 0; return; }

        var frames = 0;
        while (owedMilliseconds >= FrameMilliseconds && frames < 4)
        {
            owedMilliseconds -= FrameMilliseconds;
            frames++;
            if (!RunFrame()) return;
        }
        if (frames == 0 && runtime.Dialogue is null) return;

        if (runtime.NumCube != shownScene)
        {
            SelectSceneInCombo(runtime.NumCube);
            ShowScene(runtime.NumCube);
        }
        else if (gridStale) RedrawMap();
        gridStale = false;
        ShowDialogue();
        Draw();
        Report();
    }

    private bool RunFrame()
    {
        if (!Down(Key.Space)) fireLatch = false;
        runtime.Joy = runtime.Dialogue is not null ? 0
                    : (Down(Key.Up) ? Lba1Const.JUp : 0) | (Down(Key.Down) ? Lba1Const.JDown : 0)
                    | (Down(Key.Left) ? Lba1Const.JLeft : 0) | (Down(Key.Right) ? Lba1Const.JRight : 0);
        runtime.Fire = runtime.Dialogue is not null || fireLatch ? 0
                     : (Down(Key.Space) ? Lba1Const.FSpace : 0) | (Down(Key.LeftAlt) || Down(Key.RightAlt) ? Lba1Const.FAlt : 0);

        try
        {
            if (!runtime.Frame())
            {
                StatusText.Text = runtime.GameOver ? "Game over. Restart the scene to try again." : "The scene ended.";
                paused = true;
                return false;
            }
        }
        catch (Exception error)
        {
            DebugLog.Log($"Lba1PlayWindow: the simulation stopped: {error}");
            StatusText.Text = $"The simulation stopped: {error.Message}";
            paused = true;
            return false;
        }
        return true;
    }

    private void Report()
    {
        var h = runtime.Hero;
        var behaviour = new[] { "normal", "sporty", "aggressive", "discreet", "protopack" }.ElementAtOrDefault(runtime.Comportement) ?? "?";
        LifeBar.Value = Math.Clamp(h.LifePoint, 0, 50);
        MagicRow.Visibility = runtime.MagicLevel > 0 ? Visibility.Visible : Visibility.Collapsed;
        MagicBar.Maximum = Math.Max(20, runtime.MagicLevel * 20);
        MagicBar.Value = runtime.MagicPoint;
        HudText.Text = $"{runtime.NbGoldPieces} kashes   {runtime.NbLittleKeys} keys   clover {runtime.NbFourLeafClover}/{runtime.NbCloverBox}" +
                       (runtime.Weapon == 1 ? "   sabre" : runtime.FlagGame[Lba1Runtime.FlagBalleMagique] != 0 ? "   magic ball" : "");
        StatusText.Text =
            $"scene {runtime.NumCube}  (t = {runtime.TimerRef / 50.0:F1} s)\n" +
            $"Twinsen ({behaviour}): life {h.LifePoint}/50  magic {runtime.MagicPoint}/{runtime.MagicLevel * 20}\n" +
            $"  gold {runtime.NbGoldPieces}   keys {runtime.NbLittleKeys}   clover boxes {runtime.NbCloverBox}   leaves {runtime.NbFourLeafClover}\n" +
            $"  cell ({(h.PosX + 256) / 512}, {h.PosY / 256}, {(h.PosZ + 256) / 512})   facing {h.Beta}\n" +
            $"  position ({h.PosX}, {h.PosY}, {h.PosZ})\n" +
            $"  animation {h.GenAnim}{((h.WorkFlags & Lba1Const.Falling) != 0 ? " (falling)" : "")}   zone {h.ZoneSce}\n" +
            $"actors {runtime.NbObjets - 1}, chapter {runtime.Chapter}";
        while (loggedEvents < runtime.Events.Count)
        {
            EventList.Items.Add(runtime.Events[loggedEvents++]);
            if (EventList.Items.Count > 400) EventList.Items.RemoveAt(0);
        }
        if (EventList.Items.Count > 0) EventList.ScrollIntoView(EventList.Items[^1]);
    }

    // ---- the text box ----

    private Color TextColour(int family)
    {
        var index = Math.Clamp(family * 16 + 8, 0, 255);
        var p = game.Palette;
        return Color.FromRgb(p[index * 3], p[index * 3 + 1], p[index * 3 + 2]);
    }

    private string Speaker(int actor)
        => actor == 0 ? "Twinsen" : $"Actor {actor}";

    private void ShowDialogue()
    {
        if (runtime.Dialogue is not { } box)
        {
            DialogueBox.Visibility = Visibility.Collapsed;
            return;
        }
        DialogueBox.Visibility = Visibility.Visible;
        DialogueSpeaker.Text = Speaker(box.Speaker);
        DialogueText.Text = box.Text;
        DialogueText.Foreground = new SolidColorBrush(TextColour(box.Colour));
        DialogueChoices.Children.Clear();
        for (var i = 0; i < box.Choices.Count; i++)
        {
            DialogueChoices.Children.Add(new TextBlock
            {
                Text = (i == choiceIndex ? "▶ " : "   ") + box.Choices[i].Text,
                FontSize = 16,
                Foreground = i == choiceIndex ? Brushes.White : new SolidColorBrush(TextColour(box.Colour)),
            });
        }
        DialogueHint.Text = box.Choices.Count > 0 ? "Up / Down choose   Space confirm" : "Space continue";
    }

    // ---- drawing ----

    private static Color ColourOf(int actor) => Color.FromRgb((byte)(80 + actor * 67 % 170), (byte)(120 + actor * 131 % 130), (byte)(90 + actor * 29 % 160));

    private void Draw()
    {
        if (image is null) return;
        var canvas = OverlayCanvas;
        canvas.Children.Clear();

        // shadows first: a dither of dark pixels on the ground under every actor that casts one (sprite 2 of the shadow set)
        if (ShadowsCheck.IsChecked == true && sprites.Shadow(2, data.Directory) is { } shadowPicture)
        {
            for (var s = 0; s < runtime.NbObjets; s++)
            {
                if (runtime.ShadowOf(runtime.Objects[s]) is not var (sx, sy, sz)) continue;
                var at = image.Project(sx, sy, sz);
                var img = new Image { Source = shadowPicture.Image, Width = shadowPicture.Image.PixelWidth, Height = shadowPicture.Image.PixelHeight, Stretch = Stretch.None };
                Canvas.SetLeft(img, at.X - img.Width / 2); Canvas.SetTop(img, at.Y - img.Height / 2);
                canvas.Children.Add(img);
            }
        }

        // far to near, so nearer actors cover farther ones
        foreach (var i in Enumerable.Range(0, runtime.NbObjets).OrderBy(i => runtime.Objects[i].PosX + runtime.Objects[i].PosZ))
        {
            var o = runtime.Objects[i];
            if (o.IsDead && i != 0) continue;
            if ((o.Flags & Lba1Const.Invisible) != 0 && i != 0) continue;
            var feet = image.Project(o.PosX, o.PosY, o.PosZ);
            var box = BoxCorners(o);

            if (o.IsSprite)
            {
                DrawSprite(canvas, o, i, feet, box);
                continue;
            }

            if (o.Body != -1 && images.GetMarker(o.Body) is { } marker)
            {
                var height = Math.Clamp(marker.HeightUnits * 15 / 256, 14, 260);
                var size = Math.Max(height / 0.8, 8);
                var img = new Image { Source = PosedImage(o) ?? marker.Image, Width = size, Height = size, Stretch = Stretch.Uniform };
                Canvas.SetLeft(img, feet.X - size / 2);
                Canvas.SetTop(img, feet.Y - size * 0.9);
                canvas.Children.Add(img);
            }
            else if (o.Body != -1 || i == 0)
            {
                var dot = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(ColourOf(i)) };
                Canvas.SetLeft(dot, feet.X - 7); Canvas.SetTop(dot, feet.Y - 14);
                canvas.Children.Add(dot);
            }
            if (BoxesCheck.IsChecked == true) DrawBox(canvas, box, Brushes.Cyan);
            if (i == 0)
            {
                // facing arrow
                var (dx, dz) = Lba1Trig.Rotate(0, 400, o.Beta);
                var tip = image.Project(o.PosX + dx, o.PosY, o.PosZ + dz);
                canvas.Children.Add(new Line { X1 = feet.X, Y1 = feet.Y, X2 = tip.X, Y2 = tip.Y, Stroke = Brushes.Gold, StrokeThickness = 2 });
            }
            else if (LabelsCheck.IsChecked == true) AddLabel(canvas, $"{i}", feet.X + 6, feet.Y - 22, new SolidColorBrush(ColourOf(i)));
        }

        // the small things: bonuses, projectiles, the magic ball (they flash when they are about to vanish)
        foreach (var extra in runtime.Extras)
        {
            if (extra.Sprite == -1 || sprites.Get(extra.Sprite) is not { } picture) continue;
            if ((extra.Flags & Lba1Runtime.ExtraFlash) != 0 && runtime.TimerRef > extra.Timer + extra.TimeOut - 250 && runtime.TimerRef / 8 % 2 == 0) continue;
            var at = image.Project(extra.PosX, extra.PosY, extra.PosZ);
            var (ox, oy) = data.SpriteOffset(extra.Sprite);
            var img = new Image { Source = picture.Image, Width = picture.Image.PixelWidth, Height = picture.Image.PixelHeight, Stretch = Stretch.None };
            Canvas.SetLeft(img, at.X + ox); Canvas.SetTop(img, at.Y + oy);
            canvas.Children.Add(img);
        }

        foreach (var bubble in runtime.Bubbles)
        {
            if (bubble.ExpireTick < runtime.TimerRef || bubble.Actor >= runtime.NbObjets) continue;
            var speaker = runtime.Objects[bubble.Actor];
            var above = image.Project(speaker.PosX, speaker.PosY + Math.Max(speaker.YMax, 600), speaker.PosZ);
            var text = new TextBlock
            {
                Text = bubble.Text, MaxWidth = 240, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                Foreground = new SolidColorBrush(TextColour(speaker.CoulObj)),
            };
            var border = new Border
            {
                Child = text, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(225, 30, 41, 64)), BorderBrush = new SolidColorBrush(TextColour(speaker.CoulObj)), BorderThickness = new Thickness(1),
            };
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(border, above.X - border.DesiredSize.Width / 2);
            Canvas.SetTop(border, above.Y - border.DesiredSize.Height - 4);
            canvas.Children.Add(border);
        }

        if (GameViewCheck.IsChecked == true)
        {
            // the game's own view: a 640 x 480 screen whose centre shows the camera's cell
            var scale = MapScale.ScaleX;
            var c = image.Project(runtime.CameraX * 512, runtime.CameraY * 256, runtime.CameraZ * 512);
            MapScroll.ScrollToHorizontalOffset(Math.Max(0, (c.X - Lba1Runtime.ProjectionCentreX) * scale));
            MapScroll.ScrollToVerticalOffset(Math.Max(0, (c.Y - Lba1Runtime.ProjectionCentreY) * scale));
        }
        else if (FollowCheck.IsChecked == true)
        {
            var h = image.Project(runtime.Hero.PosX, runtime.Hero.PosY, runtime.Hero.PosZ);
            var scale = MapScale.ScaleX;
            MapScroll.ScrollToHorizontalOffset(Math.Max(0, h.X * scale - MapScroll.ViewportWidth / 2));
            MapScroll.ScrollToVerticalOffset(Math.Max(0, (h.Y - 30) * scale - MapScroll.ViewportHeight / 2));
        }
    }

    // Zones never move, so they are drawn once per scene, on a layer of their own under the actors.
    private void BuildZones()
    {
        ZoneCanvas.Children.Clear();
        if (image is null || ZonesCheck.IsChecked != true) return;
        foreach (var z in runtime.Zones)
        {
            if (ZoneFilter is { } wanted && !wanted(z.Type)) continue;      // the zone types ticked in the main window's Zones tab
            // the top face of the box
            var corners = new[] { (z.X0, z.Z0), (z.X1, z.Z0), (z.X1, z.Z1), (z.X0, z.Z1) };
            var points = new PointCollection(corners.Select(c => image.Project(c.Item1, Math.Max(z.Y0, z.Y1), c.Item2)));
            var colour = ZoneStyle.ColorOf(z.Type);
            var polygon = new Polygon
            {
                Points = points,
                Stroke = new SolidColorBrush(Color.FromArgb(200, colour.R, colour.G, colour.B)),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(z.Type == 0 ? (byte)46 : (byte)18, colour.R, colour.G, colour.B)),
            };
            ZoneCanvas.Children.Add(polygon);
        }
    }

    private void Zones_Click(object sender, RoutedEventArgs e) => BuildZones();

    // The zone types to draw changed (the main window's Zones tab).
    public void RefreshZones() => BuildZones();

    // Which zone types to draw (the main window's Zones tab); null draws them all.
    public Func<int, bool>? ZoneFilter { get; set; }

    // The game camera shows exactly the game's 640 x 480 screen (at the zoom), centred in the window.
    private void GameView_Click(object sender, RoutedEventArgs e) => ApplyViewFrame();

    private void ApplyViewFrame()
    {
        if (GameViewCheck.IsChecked == true)
        {
            var scale = MapScale.ScaleX;
            MapScroll.HorizontalAlignment = HorizontalAlignment.Center;
            MapScroll.VerticalAlignment = VerticalAlignment.Center;
            MapScroll.Width = Lba1Runtime.ScreenWidth * scale;
            MapScroll.Height = Lba1Runtime.ScreenHeight * scale;
            MapScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            MapScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }
        else
        {
            MapScroll.HorizontalAlignment = HorizontalAlignment.Stretch;
            MapScroll.VerticalAlignment = VerticalAlignment.Stretch;
            MapScroll.Width = double.NaN;
            MapScroll.Height = double.NaN;
            MapScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            MapScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
    }

    // A sprite actor (a door, a key, a box ...): its picture at its position, cut to its clip window when it has one.
    private void DrawSprite(Canvas canvas, Lba1Object o, int number, Point feet, IReadOnlyList<Point> box)
    {
        var door = (o.Flags & Lba1Const.SpriteClip) != 0;
        if (sprites.Get(o.Body) is { } sprite)
        {
            var img = new Image { Source = sprite.Image, Width = sprite.Image.PixelWidth, Height = sprite.Image.PixelHeight, Stretch = Stretch.None };
            var (offsetX, offsetY) = data.SpriteOffset(o.Body);
            double left = feet.X + offsetX, top = feet.Y + offsetY;
            if (door)
            {
                // the clip rectangle is in screen coordinates measured from the projection of the world origin (OBJECT.C:
                // SetClip(Info + XpOrgw, ...))
                img.Clip = new RectangleGeometry(new Rect(image!.OriginX + o.Info - left, image.OriginY + o.Info1 - top, o.Info2 - o.Info, o.Info3 - o.Info1));
            }
            Canvas.SetLeft(img, left); Canvas.SetTop(img, top);
            canvas.Children.Add(img);
        }
        else DrawBox(canvas, box, door ? Brushes.SandyBrown : Brushes.YellowGreen, fill: true);
        if (BoxesCheck.IsChecked == true) DrawBox(canvas, box, door ? Brushes.SandyBrown : Brushes.YellowGreen);
        if (LabelsCheck.IsChecked == true) AddLabel(canvas, $"{number}", feet.X + 8, feet.Y - 30, Brushes.SandyBrown);
    }

    // The 8 corners of an actor's collision box, projected.
    private IReadOnlyList<Point> BoxCorners(Lba1Object o)
    {
        var points = new List<Point>(8);
        if (image is null) return points;
        for (var c = 0; c < 8; c++)
            points.Add(image.Project(o.PosX + ((c & 1) != 0 ? o.XMax : o.XMin), o.PosY + ((c & 2) != 0 ? o.YMax : o.YMin), o.PosZ + ((c & 4) != 0 ? o.ZMax : o.ZMin)));
        return points;
    }

    private static void DrawBox(Canvas canvas, IReadOnlyList<Point> pts, Brush stroke, bool fill = false)
    {
        if (pts.Count < 8) return;
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        var rect = new Rectangle
        {
            Width = Math.Max(4, maxX - minX), Height = Math.Max(4, maxY - minY), Stroke = stroke, StrokeThickness = 1,
            Fill = fill ? new SolidColorBrush(Color.FromArgb(110, 0xC8, 0x8A, 0x3C)) : Brushes.Transparent,
        };
        Canvas.SetLeft(rect, minX); Canvas.SetTop(rect, minY);
        canvas.Children.Add(rect);
    }

    // An actor's body in its current pose, facing where the simulation says. Rendering is the slow part, so a picture is
    // kept for each (body, key frames, step between them, facing rounded to 15 degrees).
    private readonly Dictionary<(int, int, int, int, int), BitmapSource?> posedCache = new();
    private const int YawSteps = 24, BlendSteps = 3;

    private BitmapSource? PosedImage(Lba1Object o)
    {
        if (o.Animation is not { } animation || runtime.CurrentPose(o) is not { } pose) return null;
        var dest = animation.Frames[Math.Min(o.Frame, animation.Frames.Count - 1)];
        var t = o.PoseFrom is null ? 1.0 : Math.Clamp((runtime.TimerRef - o.AnimMemoTicks) / (double)Math.Max(1, dest.Length), 0, 1);
        var blend = (int)Math.Round(t * BlendSteps);
        var yawStep = (int)Math.Round((o.Beta & 1023) * YawSteps / 1024.0) % YawSteps;
        var key = (o.Body, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(dest.Bones),
                   o.PoseFrom is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o.PoseFrom), blend, yawStep);
        if (posedCache.TryGetValue(key, out var cached)) return cached;
        if (posedCache.Count > 8000) posedCache.Clear();
        var yaw = (float)((yawStep * 1024.0 / YawSteps - 128) * Math.PI * 2 / 1024);
        // the game's lighting: the actor's facing (as drawn, rounded to the same steps) and the scene's light
        var lit = ((int)(yawStep * 1024.0 / YawSteps), runtime.Scene?.AlphaLight ?? 0, runtime.Scene?.BetaLight ?? 0);
        return posedCache[key] = images.RenderPosed(o.Body, pose, yaw, lit);
    }

    private static void AddLabel(Canvas canvas, string text, double x, double y, Brush brush)
    {
        var label = new TextBlock { Text = text, Foreground = brush, FontSize = 10, FontFamily = new FontFamily("Consolas") };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y);
        canvas.Children.Add(label);
    }

    // ---- controls ----

    private void SceneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingCombo || SceneCombo.SelectedItem is not SceneItem item) return;
        paused = false;
        PauseButton.Content = "Pause";
        StartScene(item.Scene);
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        paused = false;
        PauseButton.Content = "Pause";
        StartScene(runtime.NumCube >= 0 ? runtime.NumCube : 0);
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        paused = !paused;
        PauseButton.Content = paused ? "Resume" : "Pause";
    }

    private void Behaviour_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (runtime is null || BehaviourCombo.SelectedIndex < 0) return;
        runtime.SetBehaviour(BehaviourCombo.SelectedIndex);
    }

    private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MapScale is not null) { MapScale.ScaleX = MapScale.ScaleY = e.NewValue; ApplyViewFrame(); }
    }

    // Click on the map: put Twinsen there (the click is taken to be at his current height).
    private void Map_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ClickCheck.IsChecked != true || image is null) return;
        var p = e.GetPosition(MapImage);
        var y = runtime.Hero.PosY;
        double u = (p.X - image.OriginX) * 512 / 24;                       // x - z
        double v = (p.Y - image.OriginY + y * 15.0 / 256) * 512 / 12;      // x + z
        var x = (int)Math.Round((u + v) / 2);
        var z = (int)Math.Round((v - u) / 2);
        if (x < 0 || z < 0 || x > 63 * 512 || z > 63 * 512) return;
        runtime.Place(x, y, z, runtime.Hero.Beta);
    }

    // Ends the play session: the clock, the music and the sounds stop. The control is not used again after this.
    public void Stop()
    {
        timer.Stop();
        music.Dispose();
        foreach (var voice in voices) voice.Dispose();
    }
}

// The LBA1 play view in a window of its own, for the scene editor's Play button (the main window shows it in its own view).
internal sealed class Lba1PlayHostWindow : Window
{
    public Lba1PlayHostWindow(Lba1Game game, Lba1ActorImages images, string directory, int scene)
    {
        Title = "LBA1 - play scene";
        Width = 1180; Height = 780; MinWidth = 760; MinHeight = 480;
        Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        var view = new Lba1PlayView(game, images, directory);
        Content = view;
        Loaded += (_, _) => view.Start(scene);
        Closed += (_, _) => view.Stop();
    }
}
