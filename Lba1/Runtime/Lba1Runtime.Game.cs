using static LBA2LevelEditor.Lba1.Runtime.Lba1Const;

namespace LBA2LevelEditor.Lba1.Runtime;

// The player's side of the game: the inventory (game flags 0..27 say what Twinsen owns; text bank 2 names and describes
// each), what the keys do (PERSO.C: 1 magic ball, 2 sabre, 3 horn, 4 protopack, Shift the inventory), and the objects
// he finds.
internal sealed partial class Lba1Runtime
{
    // Set for one pass of the object loop when the player used an item from the inventory (USE_INVENTORY reads it).
    private int inventoryAction = -1;
    public int InventoryAction => inventoryAction;

    public bool Owns(int item) => item is >= 0 and < MaxInventory && FlagGame[item] == 1;

    // The name of an inventory slot from the game texts (its description is the text 100 + slot).
    public string ItemDescription(int item) => Owns(item) ? TextOf(item + 100, 2) : TextOf(128, 2);

    // LM_FOUND_OBJECT: a box announcing what Twinsen found (the game texts, text = the item's number).
    private void FoundObject(int item)
    {
        var text = TextOf(item, 2);
        if (!AutoCloseDialogues) SpeechRequested?.Invoke(2, item);
        Log($"Twinsen finds object {item}: {Shorten(text)}");
        if (!AutoCloseDialogues) dialogues.Enqueue(new Lba1Dialogue(0, 4, item, text, Array.Empty<(int, string)>()));
    }

    // Inventory > Enter on an item (PERSO.C InventoryAction).
    public void UseItem(int item)
    {
        if (!Owns(item) || (FlagGame[FlagConsigne] != 0 && item != FlagClover) || Hero.Body == -1 || Hero.Move != MoveManual) return;
        inventoryAction = item;
        switch (item)
        {
            case FlagHolomap: Log("the holomap (not shown)"); break;
            case FlagBalleMagique: SelectMagicBall(); break;
            case FlagSabreMagique: SelectSabre(); break;
            case FlagLivreBu:
                Log("Twinsen reads the Book of Bu");
                if (!AutoCloseDialogues) dialogues.Enqueue(new Lba1Dialogue(0, 15, 161, TextOf(161, 2), Array.Empty<(int, string)>()));
                break;
            case FlagProtopack: ToggleProtopack(); break;
            case FlagMecaPingouin: PlacePenguin(); break;
            case FlagClover:
                if (Hero.LifePoint < 50 && NbFourLeafClover != 0)
                {
                    NbFourLeafClover--;
                    MagicPoint = MagicLevel * 20;
                    Hero.LifePoint = 50;
                    Log("Twinsen uses a clover leaf");
                }
                break;
            case 26:
                if (!AutoCloseDialogues) dialogues.Enqueue(new Lba1Dialogue(0, 4, 162, TextOf(162, 2), Array.Empty<(int, string)>()));
                break;
        }
    }

    private void SelectMagicBall()
    {
        if (Weapon == 1) InitBody(GenBodyNormal, 0);
        Weapon = 0;
    }

    private void SelectSabre()
    {
        if (Hero.GenBody != GenBodySabre)
        {
            if (Comportement == CProtopack) SetComportement(CNormal);
            InitBody(GenBodySabre, 0);
            InitAnim(GenAnimDegaine, AnimThen, GenAnimRien, 0);
        }
        Weapon = 1;
    }

    private void ToggleProtopack()
    {
        Hero.GenBody = FlagGame[FlagMedaillon] != 0 ? GenBodyNormal : GenBodyTunique;
        SetComportement(Comportement == CProtopack ? CNormal : CProtopack);
        Weapon = 0;
    }

    // The mechanical penguin is put down 800 units in front of Twinsen (when there is room).
    private void PlacePenguin()
    {
        if (numPingouin <= 0 || numPingouin >= NbObjets) { Log("there is no penguin in this scene"); return; }
        var p = Objects[numPingouin];
        var (dx, dz) = Lba1Trig.Rotate(0, 800, Hero.Beta);
        p.PosX = Hero.PosX + dx; p.PosY = Hero.PosY; p.PosZ = Hero.PosZ + dz;
        p.Beta = Hero.Beta;
        p.LifePoint = 50;
        p.GenBody = NoBody;
        InitBody(GenBodyNormal, numPingouin);
        p.WorkFlags &= ~ObjDead;
        p.Col = 0;
        p.RealAngle.InitAngleConst(p.Beta, p.Beta, p.SRot, TimerRef);
        SetInfoTimer(p, TimerRef + 30 * 50);
        FlagGame[FlagMecaPingouin] = 0;
        Log("the mechanical penguin is put down");
    }

    // The number keys of PERSO.C: '1' magic ball, '2' sabre, '3' horn, '4' protopack.
    public void KeyCommand(char key)
    {
        if (Hero.Body == -1 || Hero.Move != MoveManual) return;
        switch (key)
        {
            case '1': if (FlagGame[FlagBalleMagique] != 0) SelectMagicBall(); break;
            case '2': if (FlagGame[FlagSabreMagique] != 0) SelectSabre(); break;
            case '3': if (FlagGame[FlagTrompe] != 0) inventoryAction = FlagTrompe; break;
            case '4': if (FlagGame[FlagProtopack] != 0) ToggleProtopack(); break;
        }
    }
}

// ---- music and ambience (AMBIANCE.C), and the state a test session starts with ----
internal sealed partial class Lba1Runtime
{
    // The MIDI number (MIDI_MI.HQR entry) a scene or script asked for; -1 stops the music.
    public event Action<int>? MusicRequested;
    public int Music { get; private set; } = -1;

    private void PlayMusic(int num)
    {
        if (num == Music) return;
        Music = num;
        Log(num < 0 ? "the music stops" : $"music {num}");
        MusicRequested?.Invoke(num);
    }

    // ---- films (PLAY_FLA): the game stops while one plays ----

    public event Action<string>? FilmRequested;
    private string? pendingFilm;
    public string? PendingFilm => pendingFilm;

    private void PlayFilm(string name)
    {
        Log($"film {name}");
        if (AutoCloseDialogues) return;
        pendingFilm = name;
        FilmRequested?.Invoke(name);
    }

    // The film is over (or skipped): the game goes on.
    public void FilmFinished() => pendingFilm = null;

    private int timerNextAmbiance, samplePlayed;

    // GereAmbiance: now and then one of the scene's four ambient sounds plays, none twice before all have played.
    private void GereAmbiance()
    {
        if (Scene is null || TimerRef < timerNextAmbiance) return;
        var pick = Rnd(4);
        for (var n = 0; n < 4; n++)
        {
            if ((samplePlayed & (1 << pick)) == 0)
            {
                samplePlayed |= 1 << pick;
                if (samplePlayed == 15) samplePlayed = 0;
                var sample = Scene.Ambient[pick];
                if (sample.Sample != -1 && sample.Sample != 0xFFFF)
                {
                    PlaySample(sample.Sample, -1);
                    break;
                }
            }
            pick = (pick + 1) & 3;
        }
        timerNextAmbiance = TimerRef + (Rnd(Math.Max(1, Scene.SecondEcart)) + Scene.SecondMin) * 50;
    }

    // What Twinsen owns and has, as the play window's loadout sets it (a test scene is entered without the save game
    // that would normally say). Items are the inventory flags 0..27; Flags are raw game flag values.
    public void ApplyLoadout(Lba1Loadout l)
    {
        for (var i = 0; i < MaxInventory; i++) FlagGame[i] = l.Items.Contains(i) ? 1 : 0;
        foreach (var (flag, value) in l.Flags) if ((uint)flag <= MaxFlagsGame) FlagGame[flag] = value;
        MagicLevel = Math.Clamp(l.MagicLevel, 0, 4);
        MagicPoint = Math.Clamp(l.MagicPoint, 0, MagicLevel * 20);
        NbGoldPieces = Math.Clamp(l.Gold, 0, 999);
        NbLittleKeys = Math.Clamp(l.Keys, 0, 99);
        NbCloverBox = Math.Clamp(l.CloverBoxes, 0, 10);
        NbFourLeafClover = Math.Clamp(l.CloverLeaves, 0, NbCloverBox);
        Chapter = l.Chapter;
        for (var i = 0; i < MaxInventory; i++) FlagInventory[i] = false;
        Hero.LifePoint = Math.Clamp(l.Life, 1, 50);
    }
}

// The starting state of a play session: items, magic, money, chapter ...
internal sealed class Lba1Loadout
{
    public HashSet<int> Items { get; set; } = new();
    public Dictionary<int, int> Flags { get; set; } = new();
    public int MagicLevel, MagicPoint, Gold, Keys, CloverBoxes = 2, CloverLeaves = 2, Chapter, Life = 50;

    // Everything Twinsen can have, for trying anything: all items, full magic, some money.
    public static Lba1Loadout Everything() => new()
    {
        Items = Enumerable.Range(0, Lba1Const.MaxInventory).Where(i => i != 26).ToHashSet(),
        MagicLevel = 4, MagicPoint = 80, Gold = 300, Keys = 5, CloverBoxes = 5, CloverLeaves = 5, Chapter = 5,
    };
}
