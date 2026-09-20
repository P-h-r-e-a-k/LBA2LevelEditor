namespace LBA2LevelEditor.Lba1.Runtime;

// What the player sees a script say: a dialogue box (MESSAGE, zone texts, BIG_MESSAGE, choices) or a bubble over an actor
// (SAY_MESSAGE). Colour is a palette family (0..15; the text is drawn from colour * 16 to colour * 16 + 12).
internal sealed record Lba1Dialogue(int Speaker, int Colour, int TextId, string Text, IReadOnlyList<(int Id, string Text)> Choices, bool Big = false);

internal sealed record Lba1Bubble(int Actor, int TextId, string Text, int ExpireTick);

internal sealed partial class Lba1Runtime
{
    // Dialogue boxes freeze the game (the engine saves the timer, runs the box and restores it), so Frame() does nothing
    // while one is open. Headless runs (tests) close them at once; the play window shows them and calls CloseDialogue.
    public bool AutoCloseDialogues { get; set; } = true;
    private readonly Queue<Lba1Dialogue> dialogues = new();
    public Lba1Dialogue? Dialogue => dialogues.Count > 0 ? dialogues.Peek() : null;
    public List<Lba1Bubble> Bubbles { get; } = new();

    // GameChoice: the text id of the choice the player took last, read by scripts through the CHOICE function.
    public int GameChoice { get; private set; }
    private readonly List<int> gameListChoice = new();

    // Samples scripts play (sample number in SAMPLES.HQR, the actor); the play window turns them into sound.
    public event Action<int, int>? SamplePlayed;

    private void PlaySample(int sample, int actor)
    {
        Log($"actor {actor} plays sample {sample}");
        SamplePlayed?.Invoke(sample, actor);
    }

    // The text bank of the running scene's island (InitDial(START_FILE_ISLAND + Island)), or the general one.
    private string TextOf(int id, int bank = -1)
    {
        var text = Data.Text(bank >= 0 ? bank : StartFileIsland + Island)?.Get(id);
        return text ?? $"(text {id})";
    }

    private const int StartFileIsland = 3;

    // A voice to play with a text: (text file, text id). The file is the island's, or 2 for the game texts.
    public event Action<int, int>? SpeechRequested;

    private void SayDialogue(int speaker, int colour, int textId, bool big = false)
    {
        var text = TextOf(textId);
        if (!AutoCloseDialogues) SpeechRequested?.Invoke(StartFileIsland + Island, textId);
        Log($"actor {speaker} says text {textId}: {Shorten(text)}");
        if (!AutoCloseDialogues) dialogues.Enqueue(new Lba1Dialogue(speaker, colour, textId, text, Array.Empty<(int, string)>(), big));
    }

    private void SayBubble(int actor, int textId)
    {
        var text = TextOf(textId);
        if (!AutoCloseDialogues) SpeechRequested?.Invoke(StartFileIsland + Island, textId);
        Log($"actor {actor} says (in a bubble) text {textId}: {Shorten(text)}");
        Bubbles.RemoveAll(b => b.Actor == actor);
        // about as long as it takes to read it
        Bubbles.Add(new Lba1Bubble(actor, textId, text, TimerRef + 100 + text.Length * 5));
    }

    private void AskChoice(int speaker, int colour, int textId)
    {
        var choices = gameListChoice.Select(id => (id, TextOf(id))).ToList();
        gameListChoice.Clear();
        Log($"actor {speaker} asks: {Shorten(TextOf(textId))} [{string.Join(" | ", choices.Select(c => Shorten(c.Item2)))}]");
        if (choices.Count == 0) return;
        if (AutoCloseDialogues) GameChoice = choices[0].id;
        else
        {
            SpeechRequested?.Invoke(StartFileIsland + Island, textId);
            dialogues.Enqueue(new Lba1Dialogue(speaker, colour, textId, TextOf(textId), choices));
        }
    }

    // The player dismissed the open dialogue box (choice = index in its Choices when it had some).
    public event Action? SpeechStopped;

    public void CloseDialogue(int choice = 0)
    {
        if (dialogues.Count == 0) return;
        SpeechStopped?.Invoke();
        var box = dialogues.Dequeue();
        if (box.Choices.Count > 0)
        {
            GameChoice = box.Choices[Math.Clamp(choice, 0, box.Choices.Count - 1)].Id;
            Log($"Twinsen chose: {Shorten(box.Choices[Math.Clamp(choice, 0, box.Choices.Count - 1)].Text)}");
        }
    }

    private static string Shorten(string text)
    {
        text = text.Replace("\n", " ");
        return text.Length <= 60 ? text : text[..57] + "...";
    }
}
