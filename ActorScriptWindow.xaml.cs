using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LBAAssembler.LbaScript;

namespace LBAAssembler;

// Non-modal window for reading/editing one actor's life + track script.
//
// Two parts:
//   * Life (C) / Track (C) tabs: the script as C-style source (see LbaScript/), which
//     is editable and checked by the real compiler as you type. Edits live in a
//     ScriptSession (shared across windows) until "Save to SCENE.HQR" compiles
//     the affected scenes and writes them back.
//   * A Disassembly pane on the left: the native renderer's older read-only text
//     dump of both scripts, always visible for comparison. It is also the only
//     view of an actor with no scene record (e.g. one added in this session).
//
// MainWindow opens one of these per actor (keyed by actor index) rather than
// reusing a single instance, so several actors' scripts can be open side by
// side, each as its own taskbar entry.
public partial class ActorScriptWindow : Window
{
    private sealed record SuggestionItem(string Signature, string Description, string InsertText);

    // Row shape for the right-side reference list -- Kind is a plain string
    // rather than the raw category so the DataTemplate can bind it directly
    // without a converter.
    private sealed record KeywordItem(string Name, string Kind, string Description, string InsertText);

    private enum ScriptView { Life, Track }

    // Status severity colours come from the theme (Success/Warning/Error/Disabled) via SetResourceReference at
    // each assignment site below, not a precomputed Brush -- a frozen static Brush field would snapshot
    // whichever theme was active the first time it was touched and never follow a later theme switch.
    private readonly RendererLibraryApi? library;
    private readonly ScriptSession? session;
    private readonly Func<int, ActorSource?>? resolveActor;
    private readonly Func<int, (string Stats, string Disassembly)?>? describeActor;
    private readonly Action? afterSave;
    private readonly DispatcherTimer checkTimer;
    private readonly Dictionary<ScriptKind, IReadOnlyList<KeywordItem>> keywordCache = new();

    private List<SuggestionItem> suggestionPool = new();
    private IReadOnlyList<KeywordItem> currentKeywords = Array.Empty<KeywordItem>();
    private int currentWordStart;
    private bool suppressTextChanged;
    private bool switchingView;
    private int actorIndex;

    private static readonly Regex OffsetLineRegex = new(@"^\s*(\d+):", RegexOptions.Multiline);

    private string nativeScript = "(no script)";
    private SceneScripts? sceneScripts;
    private ActorSource source;
    private ScriptView view = ScriptView.Life;
    private int lastErrorLine;

    // Breakpoints/pause: the (scene, in-scene actor) key ScriptBreakpoints uses, resolved independently
    // of sceneScripts (which needs a loaded session and is null for e.g. an actor added this session).
    // -1 means this actor has no known scene position, so breakpoints can't be set here at all.
    private int debugScene = -1;
    private int debugActor = -1;
    // Whether nativeScript is in the "{offset,5}: " format (LBA1's shared Disassembly.cs) -- LBA2's
    // native disassembly (RENDERER_ACTORS.CPP) has no offsets in its text, so it can't be decorated
    // with breakpoint markers or clicked to set one; only the C pane (same compiler for both games)
    // can set LBA2 breakpoints.
    private bool disassemblyHasOffsets;

    // describeActor: the stats line and disassembly for games without the native renderer (LBA1);
    // afterSave: called once the session has written SCENE.HQR (the caller reloads what it shows).
    internal ActorScriptWindow(RendererLibraryApi? library, ScriptSession? session = null, Func<int, ActorSource?>? resolveActor = null,
        Func<int, (string Stats, string Disassembly)?>? describeActor = null, Action? afterSave = null)
    {
        InitializeComponent();
        this.library = library;
        this.session = session;
        this.resolveActor = resolveActor;
        this.describeActor = describeActor;
        this.afterSave = afterSave;

        checkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        checkTimer.Tick += (_, _) => { checkTimer.Stop(); RunCheck(); };

        ScriptBreakpoints.Changed += OnBreakpointsChanged;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Edits belong to the session, not the window: keep them.
        CommitEditor();
        checkTimer.Stop();
        ScriptBreakpoints.Changed -= OnBreakpointsChanged;
        base.OnClosing(e);
    }

    private ScriptKind CurrentKind => view == ScriptView.Track ? ScriptKind.Track : ScriptKind.Life;
    private bool IsCView => sceneScripts is not null;

    // WPF's TextBox reports newlines as \r\n; the compiler and the stored
    // decompilation use \n. Normalise so that merely viewing a script never
    // looks like an edit.
    private string EditorText => ScriptTextBox.Text.Replace("\r\n", "\n");

    // Called both to first open the window for an actor and to re-point an
    // already-open window at a newly re-selected actor.
    public void ShowActor(int actorIndex)
    {
        CommitEditor();
        this.actorIndex = actorIndex;
        Title = $"Actor {actorIndex} Script";
        TitleLabel.Text = $"Actor {actorIndex}";
        StatsLabel.Text = "(actor data unavailable)";
        nativeScript = "(no script)";

        if (library is not null && library.GetActor(actorIndex, out var x, out var y, out var z, out var waypointCount))
        {
            library.GetActorAttributes(actorIndex, out var beta, out var body, out var anim, out var lifePoint, out var armor, out var hitForce, out var move);
            StatsLabel.Text = $"pos ({x}, {y}, {z})   beta={beta} body={body} anim={anim}   " +
                               $"life={lifePoint} armor={armor} hit={hitForce} move={move}   waypoints={waypointCount}";
            nativeScript = library.GetActorScript(actorIndex);
        }
        else if (describeActor?.Invoke(actorIndex) is { } described)
        {
            StatsLabel.Text = described.Stats;
            nativeScript = described.Disassembly;
        }

        sceneScripts = null;
        debugScene = -1;
        debugActor = -1;
        var resolved = resolveActor?.Invoke(actorIndex);
        if (resolved is { } anySrc) { debugScene = anySrc.Scene; debugActor = anySrc.Slot; }
        if (resolved is { } src && session?.GetScene(src.Scene) is { } scene && src.Slot < scene.ActorCount)
        {
            sceneScripts = scene;
            source = src;
            TitleLabel.Text = $"Actor {actorIndex}   ·   scene {src.Scene}, object {src.Slot}";
        }

        var hasScene = sceneScripts is not null;
        ViewLifeButton.IsEnabled = hasScene;
        ViewTrackButton.IsEnabled = hasScene;
        RevertButton.IsEnabled = hasScene;
        SaveButton.IsEnabled = hasScene;

        disassemblyHasOffsets = OffsetLineRegex.IsMatch(nativeScript);
        DisassemblyTextBox.Text = DecorateDisassembly(nativeScript);
        DisassemblyTextBox.ScrollToHome();
        SelectView(view);
        RefreshDebugBar();

        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // After a script-style setting changed (see MainWindow.RefreshScriptStyle): first every
    // window stores what it shows, then the session regenerates its cached text, then every
    // window reloads -- so untouched scripts re-print in the new style without counting as edits.
    internal void CommitForRestyle() => CommitEditor();
    internal void ReloadForRestyle() { keywordCache.Clear(); LoadView(); }

    // ---- views ------------------------------------------------------------

    private void SelectView(ScriptView v)
    {
        view = v;
        switchingView = true;
        (v == ScriptView.Track ? ViewTrackButton : ViewLifeButton).IsChecked = true;
        switchingView = false;
        LoadView();
    }

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (switchingView) return;
        var next = ((RadioButton)sender).Tag is "track" ? ScriptView.Track : ScriptView.Life;
        if (next == view) return;
        CommitEditor();             // still holds the previous view's text
        view = next;
        LoadView();
    }

    private void LoadView()
    {
        checkTimer.Stop();
        string text;
        if (IsCView)
        {
            text = sceneScripts!.GetText(source.Slot, CurrentKind);
            ScriptTextBox.IsReadOnly = false;
            HintLabel.Text = "C source — comments are kept in SCENE.HQR.comments.json; edits stay in memory until you Save · F9 sets a breakpoint (unedited script only)";
        }
        else
        {
            text = "// No scene record for this actor (added this session?), so there is no C source to edit.\n// The disassembly on the left is the only view.\n";
            ScriptTextBox.IsReadOnly = true;
            HintLabel.Text = "no scene record for this actor — read-only";
        }

        suppressTextChanged = true;
        ScriptTextBox.Text = text;
        ScriptTextBox.CaretIndex = 0;
        ScriptTextBox.ScrollToHome();
        suppressTextChanged = false;
        SuggestionPopup.IsOpen = false;

        RefreshKeywordList();
        RefreshSuggestionPool();
        RunCheck();
    }

    // ---- breakpoints ----------------------------------------------------------

    // Prefixes every instruction line with a 2-character marker (breakpoint set / currently paused
    // there / neither) using the same offset the line already prints -- safe because the Disassembly
    // pane is read-only text this window generates itself. A no-op for LBA2 (see disassemblyHasOffsets).
    private string DecorateDisassembly(string raw)
    {
        if (!disassemblyHasOffsets || debugScene < 0) return raw;
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var kind = ScriptKind.Life;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("TRACK SCRIPT")) kind = ScriptKind.Track;
            else if (line.StartsWith("LIFE SCRIPT")) kind = ScriptKind.Life;
            var m = OffsetLineRegex.Match(line);
            if (!m.Success) { lines[i] = "  " + line; continue; }
            var offset = int.Parse(m.Groups[1].Value);
            var paused = ScriptBreakpoints.Current is { } c && c.Scene == debugScene && c.Actor == debugActor && c.Kind == kind && c.Offset == offset;
            var set = ScriptBreakpoints.IsSet(debugScene, debugActor, kind, offset);
            lines[i] = (paused ? "▶ " : set ? "● " : "  ") + line;
        }
        return string.Join("\n", lines);
    }

    private int FindDisassemblyLineForOffset(ScriptKind kind, int offset)
    {
        var lines = DisassemblyTextBox.Text.Replace("\r\n", "\n").Split('\n');
        var k = ScriptKind.Life;
        for (var i = 0; i < lines.Length; i++)
        {
            var body = lines[i].Length >= 2 ? lines[i][2..] : lines[i];
            if (body.StartsWith("TRACK SCRIPT")) k = ScriptKind.Track;
            else if (body.StartsWith("LIFE SCRIPT")) k = ScriptKind.Life;
            var m = OffsetLineRegex.Match(body);
            if (m.Success && k == kind && int.Parse(m.Groups[1].Value) == offset) return i;
        }
        return -1;
    }

    private void RefreshDebugBar()
    {
        if (ScriptBreakpoints.Current is not { } c)
        {
            DebugBar.Visibility = Visibility.Collapsed;
            return;
        }
        DebugBar.Visibility = Visibility.Visible;
        var here = c.Scene == debugScene && c.Actor == debugActor;
        DebugBarText.Text = $"Paused: scene {c.Scene}, actor {c.Actor} {c.Kind.ToString().ToLowerInvariant()} script, offset {c.Offset}" + (here ? "  (this window)" : "");
    }

    private void OnBreakpointsChanged()
    {
        RefreshDebugBar();
        DisassemblyTextBox.Text = DecorateDisassembly(nativeScript);
        if (ScriptBreakpoints.Current is { } c && c.Scene == debugScene && c.Actor == debugActor &&
            FindDisassemblyLineForOffset(c.Kind, c.Offset) is >= 0 and var line)
        {
            DisassemblyTextBox.ScrollToLine(line);
            DisassemblyTextBox.Select(DisassemblyTextBox.GetCharacterIndexFromLineIndex(line), DisassemblyTextBox.GetLineLength(line));
        }
    }

    private void Step_Click(object sender, RoutedEventArgs e) => ScriptBreakpoints.Resume(singleStep: true);
    private void Continue_Click(object sender, RoutedEventArgs e) => ScriptBreakpoints.Resume(singleStep: false);

    private void DisassemblyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F9 || !disassemblyHasOffsets || debugScene < 0) return;
        e.Handled = true;
        var caretLine = DisassemblyTextBox.GetLineIndexFromCharacterIndex(DisassemblyTextBox.CaretIndex);
        var kind = ScriptKind.Life;
        for (var i = 0; i <= caretLine; i++)
        {
            var text = DisassemblyTextBox.Text.Substring(DisassemblyTextBox.GetCharacterIndexFromLineIndex(i), DisassemblyTextBox.GetLineLength(i));
            if (text.Contains("TRACK SCRIPT")) kind = ScriptKind.Track;
            else if (text.Contains("LIFE SCRIPT")) kind = ScriptKind.Life;
        }
        var lineText = DisassemblyTextBox.Text.Substring(DisassemblyTextBox.GetCharacterIndexFromLineIndex(caretLine), DisassemblyTextBox.GetLineLength(caretLine));
        var body = lineText.Length >= 2 ? lineText[2..] : lineText;
        var m = OffsetLineRegex.Match(body);
        if (!m.Success) return;
        ScriptBreakpoints.Toggle(debugScene, debugActor, kind, int.Parse(m.Groups[1].Value));
    }

    // Breakpoint by C-source line: uses the same compiled bytecode's line/offset map for both games
    // (SceneScripts wraps the same decompiler regardless), but only while the script matches what's
    // actually stored -- an edited-but-unsaved script's eventual offsets aren't known yet.
    private void ToggleCPaneBreakpoint()
    {
        if (!IsCView || debugScene < 0) return;
        if (sceneScripts!.IsEdited(source.Slot, CurrentKind))
        {
            StatusText.Text = "Revert or save this script before setting a breakpoint by line here (line numbers may have shifted).";
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeWarningBrush");
            return;
        }
        var line = ScriptTextBox.GetLineIndexFromCharacterIndex(ScriptTextBox.CaretIndex);
        var offset = sceneScripts.OriginalOffsetForLine(source.Slot, CurrentKind, line);
        if (offset is null)
        {
            StatusText.Text = $"Line {line + 1} has no instruction to break on.";
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeWarningBrush");
            return;
        }
        var nowSet = !ScriptBreakpoints.IsSet(debugScene, debugActor, CurrentKind, offset.Value);
        ScriptBreakpoints.Toggle(debugScene, debugActor, CurrentKind, offset.Value);
        StatusText.Text = nowSet ? $"● breakpoint set at line {line + 1} (offset {offset.Value})" : $"breakpoint cleared at line {line + 1}";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeSuccessBrush");
    }

    // Stores the editor's text into the session for the actor/script being shown.
    private void CommitEditor()
    {
        if (!IsCView || ScriptTextBox.IsReadOnly) return;
        sceneScripts!.SetText(source.Slot, CurrentKind, EditorText);
    }

    // ---- compile check --------------------------------------------------------

    private void RunCheck()
    {
        lastErrorLine = 0;
        if (!IsCView)
        {
            StatusText.Text = session is { HasUnsavedEdits: true } ? UnsavedNote() : "";
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeDisabledBrush");
            return;
        }

        var text = EditorText;
        var (size, error, warnings) = sceneScripts!.CheckTextFull(source.Slot, CurrentKind, text);
        var edited = text != sceneScripts.OriginalText(source.Slot, CurrentKind);
        StatusText.ToolTip = null;
        if (error is not null)
        {
            lastErrorLine = error.Line;
            StatusText.Text = $"✗ line {error.Line}, col {error.Column}: {error.Message}   (click to jump)";
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeErrorBrush");
        }
        else
        {
            var stored = sceneScripts.OriginalSize(source.Slot, CurrentKind);
            // Stored comments whose statement is gone are listed at the end of the script under a marker line.
            var detached = text.Contains("lost their statement") ? sceneScripts.DetachedComments(source.Slot, CurrentKind) : 0;
            var summary = $"{size} bytes" + (size != stored ? $" (stored: {stored})" : "") + (edited ? " · edited" : "") +
                          (detached > 0 ? $" · {detached} comment(s) lost their statement — see end of script" : "") + UnsavedNote();
            if (warnings.Count > 0)
            {
                // Compiles, but the text isn't in the canonical style (e.g. a literal on the right of a comparison).
                lastErrorLine = warnings[0].Line;
                StatusText.Text = $"⚠ line {warnings[0].Line}: {warnings[0].Message}" + (warnings.Count > 1 ? $" (+{warnings.Count - 1} more)" : "") + $" · compiles, {summary}";
                StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeWarningBrush");
                StatusText.ToolTip = string.Join("\n", warnings.Select(w => $"line {w.Line}, col {w.Column}: {w.Message}"));
            }
            else
            {
                StatusText.Text = $"✓ compiles · {summary}";
                StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeSuccessBrush");
            }
        }

        RefreshSuggestionPool();
    }

    private string UnsavedNote()
    {
        if (session is null) return "";
        var scenes = session.EditedScenes.ToList();
        // The scene on screen only counts as edited in the session once its text is
        // committed, so add it here if the editor already differs from what is stored.
        if (IsCView && !scenes.Contains(source.Scene) && EditorText != sceneScripts!.OriginalText(source.Slot, CurrentKind))
            scenes.Add(source.Scene);
        return scenes.Count == 0 ? "" : $" · unsaved edits in scene {string.Join(", ", scenes.Order())}";
    }

    private void StatusText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (lastErrorLine <= 0 || lastErrorLine > ScriptTextBox.LineCount) return;
        var line = lastErrorLine - 1;
        ScriptTextBox.Focus();
        ScriptTextBox.ScrollToLine(line);
        ScriptTextBox.Select(ScriptTextBox.GetCharacterIndexFromLineIndex(line), Math.Max(0, ScriptTextBox.GetLineLength(line) - 1));
    }

    // ---- save / revert --------------------------------------------------------

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCView) return;
        sceneScripts!.RevertText(source.Slot, CurrentKind);
        LoadView();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (session is null) return;
        CommitEditor();
        var scenes = session.EditedScenes;
        if (scenes.Count == 0)
        {
            MessageBox.Show(this, "There are no edited scripts to save.", "Save scripts", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Compile the edited scripts and write scene {string.Join(", ", scenes)} into:\n\n{session.HqrPath}\n\n" +
            "The first save keeps your original as SCENE.HQR.bak. Comments are stored separately in SCENE.HQR.comments.json " +
            "(a save that only changes comments leaves SCENE.HQR alone). Reload the island afterwards to see script changes in the viewer.\n\nContinue?",
            "Save scripts to SCENE.HQR", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        var result = session.SaveAll();
        if (!result.Ok)
        {
            var detail = result.Errors.Count > 1 ? "\n\n" + string.Join("\n", result.Errors.Take(8)) : "";
            MessageBox.Show(this, result.Message + detail, "Scripts not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StatusText.Text = "✓ " + result.Message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeSuccessBrush");
        afterSave?.Invoke();
        // The session dropped the saved scenes so they reload from disk: point this window at the fresh copy.
        var keep = StatusText.Text;
        ShowActor(actorIndex);
        StatusText.Text = keep;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeSuccessBrush");
    }

    // ---- editor events ----------------------------------------------------------

    private void ScriptTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressTextChanged) return;
        if (IsCView)
        {
            checkTimer.Stop();
            checkTimer.Start();
            UpdateSuggestions(forceShowAll: false);
        }
    }

    private void ScriptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SuggestionPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down: MoveSelection(1); e.Handled = true; return;
                case Key.Up: MoveSelection(-1); e.Handled = true; return;
                case Key.Enter:
                case Key.Tab: AcceptSelection(); e.Handled = true; return;
                case Key.Escape: SuggestionPopup.IsOpen = false; e.Handled = true; return;
            }
        }

        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control && IsCView)
        {
            UpdateSuggestions(forceShowAll: true);
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && IsCView)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F9)
        {
            ToggleCPaneBreakpoint();
            e.Handled = true;
        }
    }

    private void ScriptTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Losing focus to the popup's own ListBox shouldn't dismiss it --
        // only close when focus actually left both the editor and the popup.
        if (!SuggestionPopup.IsKeyboardFocusWithin) SuggestionPopup.IsOpen = false;
    }

    private void SuggestionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionList.SelectedItem is not null) AcceptSelection();
    }

    // ---- autocomplete -------------------------------------------------------------

    private void RefreshSuggestionPool()
    {
        if (!IsCView) { suggestionPool = new(); return; }

        var life = view == ScriptView.Life ? EditorText : sceneScripts!.GetText(source.Slot, ScriptKind.Life);
        var trk = view == ScriptView.Track ? EditorText : sceneScripts!.GetText(source.Slot, ScriptKind.Track);
        suggestionPool = ScriptCompletions.For(CurrentKind, sceneScripts!.Dialect)
            .Concat(ScriptCompletions.Symbols(life, trk))
            .Select(c => new SuggestionItem(c.Signature, c.Description, c.InsertText))
            .ToList();
    }

    // The word currently being typed, scanned back from the caret to the
    // nearest whitespace/opening-bracket -- stops at '(' and '[' too so
    // e.g. "DISTANCE(" still offers matches for what follows the bracket
    // rather than treating the whole bracketed expression as one word.
    private (int Start, string Word) GetCurrentWord()
    {
        var caret = ScriptTextBox.CaretIndex;
        var text = ScriptTextBox.Text;
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] != '(' && text[start - 1] != '[' && text[start - 1] != ',')
            start--;
        return (start, text[start..caret]);
    }

    private void UpdateSuggestions(bool forceShowAll)
    {
        var (start, word) = GetCurrentWord();
        List<SuggestionItem> matches;
        if (forceShowAll)
        {
            matches = suggestionPool.Take(40).ToList();
        }
        else
        {
            if (word.Length == 0) { SuggestionPopup.IsOpen = false; return; }
            matches = suggestionPool
                .Where(s => s.InsertText.TrimEnd('(', '[', ' ', '"').StartsWith(word, StringComparison.OrdinalIgnoreCase)
                            || s.Signature.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .ToList();
        }

        if (matches.Count == 0) { SuggestionPopup.IsOpen = false; return; }

        currentWordStart = start;
        SuggestionList.ItemsSource = matches;
        SuggestionList.SelectedIndex = 0;

        var caretRect = ScriptTextBox.GetRectFromCharacterIndex(ScriptTextBox.CaretIndex);
        SuggestionPopup.HorizontalOffset = caretRect.Left;
        SuggestionPopup.VerticalOffset = caretRect.Bottom + 2;
        SuggestionPopup.IsOpen = true;
    }

    private void MoveSelection(int delta)
    {
        if (SuggestionList.Items.Count == 0) return;
        var next = (SuggestionList.SelectedIndex + delta + SuggestionList.Items.Count) % SuggestionList.Items.Count;
        SuggestionList.SelectedIndex = next;
        SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
    }

    // Replaces the word being typed with the chosen suggestion. Goes through
    // SelectedText rather than assigning Text so the editor's undo history survives.
    private void AcceptSelection()
    {
        if (SuggestionList.SelectedItem is not SuggestionItem item) { SuggestionPopup.IsOpen = false; return; }
        var caret = ScriptTextBox.CaretIndex;
        if (currentWordStart > caret || currentWordStart < 0) { SuggestionPopup.IsOpen = false; return; }

        suppressTextChanged = true;
        ScriptTextBox.Select(currentWordStart, caret - currentWordStart);
        ScriptTextBox.SelectedText = item.InsertText;
        ScriptTextBox.CaretIndex = currentWordStart + item.InsertText.Length;
        suppressTextChanged = false;

        SuggestionPopup.IsOpen = false;
        ScriptTextBox.Focus();
        checkTimer.Stop();
        checkTimer.Start();
    }

    // ---- keyword reference list -------------------------------------------------------

    private void RefreshKeywordList()
    {
        var kind = CurrentKind;
        if (!keywordCache.TryGetValue(kind, out var list))
        {
            list = ScriptCompletions.For(kind, sceneScripts?.Dialect)
                .Select(c => new KeywordItem(c.Name, c.Category, c.Description, c.InsertText))
                .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            keywordCache[kind] = list;
        }
        currentKeywords = list;
        KeywordFilterBox_TextChanged(KeywordFilterBox, null!);
    }

    private void KeywordFilterBox_TextChanged(object sender, TextChangedEventArgs? e)
    {
        var text = KeywordFilterBox.Text;
        KeywordFilterPlaceholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        KeywordList.ItemsSource = text.Length == 0
            ? currentKeywords
            : currentKeywords.Where(k => k.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // Enter from the filter box jumps straight to inserting the top (first
    // alphabetically, or first remaining after filtering) match, so a user
    // who knows roughly what they want doesn't have to leave the keyboard
    // to click into the list.
    private void KeywordFilterBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (KeywordList.Items.Count == 0) return;
        InsertKeyword((KeywordItem)KeywordList.Items[0]!);
        e.Handled = true;
    }

    private void KeywordList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (KeywordList.SelectedItem is KeywordItem item) InsertKeyword(item);
    }

    private void KeywordList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (KeywordList.SelectedItem is KeywordItem item) InsertKeyword(item);
        e.Handled = true;
    }

    // Inserts at the caret's current position in ScriptTextBox (replacing any
    // selection), then hands focus back to the editor so typing continues right
    // where the keyword landed. Does nothing in the read-only disassembly view.
    private void InsertKeyword(KeywordItem item)
    {
        if (ScriptTextBox.IsReadOnly) return;
        ScriptTextBox.SelectedText = item.InsertText;
        ScriptTextBox.CaretIndex = ScriptTextBox.SelectionStart + ScriptTextBox.SelectionLength;
        ScriptTextBox.Focus();
    }
}
