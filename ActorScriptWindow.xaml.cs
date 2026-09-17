using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LBA2LevelEditor;

// Non-modal window for reading/editing one actor's decoded life+track
// script. Replaces the old cramped, fixed-height, read-only TextBox that
// used to live in the main window's right sidebar (squeezed in next to the
// terrain-palette tools) with room to actually work, plus opcode/snippet
// autocomplete. MainWindow opens one of these per actor (keyed by actor
// index) rather than reusing a single instance, so several actors' scripts
// can be open side by side, each as its own taskbar entry; closing one only
// removes that actor's entry.
public partial class ActorScriptWindow : Window
{
    private sealed record SuggestionItem(string Signature, string Description, string InsertText);

    private readonly RendererLibraryApi? library;
    private List<SuggestionItem> suggestionPool = new();
    private int currentWordStart;
    private bool suppressTextChanged;
    private int actorIndex;

    internal ActorScriptWindow(RendererLibraryApi? library)
    {
        InitializeComponent();
        this.library = library;
    }

    // Called both to first open the window for an actor and to re-point an
    // already-open window at a newly re-selected actor.
    public void ShowActor(int actorIndex)
    {
        this.actorIndex = actorIndex;
        Title = $"Actor {actorIndex} Script";
        TitleLabel.Text = $"Actor {actorIndex}";
        StatsLabel.Text = "(actor data unavailable)";
        var script = "(no script)";

        if (library is not null && library.GetActor(actorIndex, out var x, out var y, out var z, out var waypointCount))
        {
            library.GetActorAttributes(actorIndex, out var beta, out var body, out var anim, out var lifePoint, out var armor, out var hitForce, out var move);
            StatsLabel.Text = $"pos ({x}, {y}, {z})   beta={beta} body={body} anim={anim}   " +
                               $"life={lifePoint} armor={armor} hit={hitForce} move={move}   waypoints={waypointCount}";
            script = library.GetActorScript(actorIndex);
        }

        // Rebuilt on every actor shown, not cached: it's cheap (pure string
        // scanning over scripts the native side already decoded once at
        // island load -- see ScriptSuggestionIndex's own comment) and keeps
        // snippet suggestions current if the user has since switched islands.
        var snippets = ScriptSuggestionIndex.Build(library);
        suggestionPool = Lba2ScriptOpcodes.All
            .Select(o => new SuggestionItem(o.Signature, o.Description, o.InsertText))
            .Concat(snippets.CommonLines.Select(l => new SuggestionItem(l.Text, $"Common pattern (seen {l.Occurrences}x across this island's actors)", l.Text)))
            .ToList();

        suppressTextChanged = true;
        ScriptTextBox.Text = script;
        ScriptTextBox.CaretIndex = 0;
        suppressTextChanged = false;
        SuggestionPopup.IsOpen = false;

        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ScriptTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressTextChanged) return;
        UpdateSuggestions(forceShowAll: false);
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

        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            UpdateSuggestions(forceShowAll: true);
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

    // The word currently being typed, scanned back from the caret to the
    // nearest whitespace/opening-bracket -- stops at '(' and '[' too so
    // e.g. "DISTANCE(obj=" still offers matches for what follows the bracket
    // rather than treating the whole bracketed expression as one word.
    private (int Start, string Word) GetCurrentWord()
    {
        var caret = ScriptTextBox.CaretIndex;
        var text = ScriptTextBox.Text;
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] != '(' && text[start - 1] != '[')
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

    private void AcceptSelection()
    {
        if (SuggestionList.SelectedItem is not SuggestionItem item) { SuggestionPopup.IsOpen = false; return; }
        var caret = ScriptTextBox.CaretIndex;
        var text = ScriptTextBox.Text;
        if (currentWordStart > caret || currentWordStart < 0) { SuggestionPopup.IsOpen = false; return; }

        suppressTextChanged = true;
        ScriptTextBox.Text = text[..currentWordStart] + item.InsertText + text[caret..];
        ScriptTextBox.CaretIndex = currentWordStart + item.InsertText.Length;
        suppressTextChanged = false;

        SuggestionPopup.IsOpen = false;
        ScriptTextBox.Focus();
    }

}
