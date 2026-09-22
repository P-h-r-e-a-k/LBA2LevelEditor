using static LBAAssembler.Lba1.Runtime.Lba1Const;

namespace LBAAssembler.Lba1.Runtime;

// GERELIFE.C (life scripts) and GERETRAK.C (track scripts). Scripts run on each actor's own copy of its bytecode, because
// the engine rewrites it while it runs (SWIF / ONEIF change their opcode, WAIT_NB_SECOND keeps its timer in the bytes).
internal sealed partial class Lba1Runtime
{
    // LM_* / LF_* / LT_* / TM_* numbers (COMMON.H)
    private static class Lm
    {
        public const int End = 0, Nop = 1, Snif = 2, Offset = 3, NeverIf = 4, Label = 10, Return = 11, If = 12, SwIf = 13, OneIf = 14, Else = 15,
            EndIf = 16, Body = 17, BodyObj = 18, Anim = 19, AnimObj = 20, SetLife = 21, SetLifeObj = 22, SetTrack = 23, SetTrackObj = 24,
            Message = 25, Fallable = 26, SetDir = 27, SetDirObj = 28, CamFollow = 29, ComportementHero = 30, SetFlagCube = 31,
            Comportement = 32, SetComportement = 33, SetComportementObj = 34, EndComportement = 35, SetFlagGame = 36, KillObj = 37,
            Suicide = 38, UseOneLittleKey = 39, GiveGoldPieces = 40, EndLife = 41, StopLTrack = 42, RestoreLTrack = 43, MessageObj = 44,
            IncChapter = 45, FoundObject = 46, SetDoorLeft = 47, SetDoorRight = 48, SetDoorUp = 49, SetDoorDown = 50, GiveBonus = 51,
            ChangeCube = 52, ObjCol = 53, BrickCol = 54, OrIf = 55, Invisible = 56, Zoom = 57, PosPoint = 58, SetMagicLevel = 59,
            SubMagicPoint = 60, SetLifePointObj = 61, SubLifePointObj = 62, HitObj = 63, PlayFla = 64, PlayMidi = 65, IncCloverBox = 66,
            SetUsedInventory = 67, AddChoice = 68, AskChoice = 69, BigMessage = 70, InitPingouin = 71, SetHoloPos = 72, ClrHoloPos = 73,
            AddFuel = 74, SubFuel = 75, SetGrm = 76, SayMessage = 77, SayMessageObj = 78, FullPoint = 79, Beta = 80, GrmOff = 81,
            FadePalRed = 82, FadeAlarmRed = 83, FadeAlarmPal = 84, FadeRedPal = 85, FadeRedAlarm = 86, FadePalAlarm = 87, ExplodeObj = 88,
            BulleOn = 89, BulleOff = 90, AskChoiceObj = 91, SetDarkPal = 92, SetNormalPal = 93, MessageSendell = 94, AnimSet = 95,
            HolomapTraj = 96, GameOver = 97, TheEnd = 98, MidiOff = 99, PlayCdTrack = 100, ProjIso = 101, Proj3D = 102, Text = 103,
            ClearText = 104, BrutalExit = 105;
    }

    private static class Lf
    {
        public const int Col = 0, ColObj = 1, Distance = 2, Zone = 3, ZoneObj = 4, Body = 5, BodyObj = 6, Anim = 7, AnimObj = 8, LTrack = 9,
            LTrackObj = 10, FlagCube = 11, ConeView = 12, HitBy = 13, Action = 14, FlagGame = 15, LifePoint = 16, LifePointObj = 17,
            NbLittleKeys = 18, NbGoldPieces = 19, ComportementHero = 20, Chapter = 21, Distance3D = 22, MagicLevel = 23, MagicPoint = 24,
            UseInventory = 25, Choice = 26, Fuel = 27, CarryBy = 28, CdRom = 29;
    }

    private const int LtEqual = 0, LtSup = 1, LtLess = 2, LtSupEqual = 3, LtLessEqual = 4, LtDifferent = 5;

    private static class Tm
    {
        public const int End = 0, Nop = 1, Body = 2, Anim = 3, GotoPoint = 4, WaitAnim = 5, Loop = 6, Angle = 7, PosPoint = 8, Label = 9,
            Goto = 10, Stop = 11, GotoSymPoint = 12, WaitNbAnim = 13, Sample = 14, GotoPoint3D = 15, Speed = 16, Background = 17,
            WaitNbSecond = 18, NoBody = 19, Beta = 20, OpenLeft = 21, OpenRight = 22, OpenUp = 23, OpenDown = 24, Close = 25, WaitDoor = 26,
            SampleRnd = 27, SampleAlways = 28, SampleStop = 29, PlayFla = 30, RepeatSample = 31, SimpleSample = 32, FaceTwinkel = 33, AngleRnd = 34;
    }

    private static int S16At(byte[] d, int at) => (short)(d[at] | d[at + 1] << 8);
    private static int U32At(byte[] d, int at) => d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24;
    private static void SetU32(byte[] d, int at, int v) { d[at] = (byte)v; d[at + 1] = (byte)(v >> 8); d[at + 2] = (byte)(v >> 16); d[at + 3] = (byte)(v >> 24); }
    private static void SetS16(byte[] d, int at, int v) { d[at] = (byte)v; d[at + 1] = (byte)(v >> 8); }

    // ---------------------------------------------------------------------------------------------- GERELIFE.C

    private int value;          // "Value": the result of a life function
    private int typeAnswer;     // RET_BYTE 0 / RET_WORD 1
    private byte[] prg = Array.Empty<byte>();
    private int pc;

    private int Next() => prg[pc++];
    private int Word() { var v = S16At(prg, pc); pc += 2; return v; }

    // DoFuncLife: evaluates the function named by the next byte (with its operand) into Value.
    private void DoFuncLife(Lba1Object o)
    {
        typeAnswer = 0;
        switch (Next())
        {
            case Lf.Col: value = o.LifePoint <= 0 ? -1 : o.ObjCol; break;
            case Lf.Chapter: value = Chapter; break;
            case Lf.LifePoint: value = o.LifePoint; break;
            case Lf.HitBy: value = o.HitBy; break;
            case Lf.Action: value = actionNormal ? 1 : 0; break;
            case Lf.LifePointObj: value = Objects[Next()].LifePoint; break;
            case Lf.ColObj:
                {
                    var num = Next();
                    value = Objects[num].LifePoint <= 0 ? -1 : Objects[num].ObjCol;
                }
                break;
            case Lf.Distance:
                {
                    var num = Next();
                    typeAnswer = 1;
                    var t = Objects[num];
                    if ((t.WorkFlags & ObjDead) != 0) { value = 32000; break; }
                    if (Math.Abs(t.PosY - o.PosY) < 1500)
                    {
                        var distance = Lba1Trig.Distance2D(o.PosX, o.PosZ, t.PosX, t.PosZ);
                        value = distance > 32000 ? 32000 : distance;
                    }
                    else value = 32000;
                }
                break;
            case Lf.Distance3D:
                {
                    var num = Next();
                    typeAnswer = 1;
                    var t = Objects[num];
                    if ((t.WorkFlags & ObjDead) != 0) { value = 32000; break; }
                    var distance = Lba1Trig.Distance3D(o.PosX, o.PosY, o.PosZ, t.PosX, t.PosY, t.PosZ);
                    value = distance > 32000 ? 32000 : distance;
                }
                break;
            case Lf.ConeView:
                {
                    var num = Next();
                    typeAnswer = 1;
                    var t = Objects[num];
                    if ((t.WorkFlags & ObjDead) != 0) { value = 32000; break; }
                    var angle = 0;
                    if (Math.Abs(t.PosY - o.PosY) < 1500)
                    {
                        angle = Lba1Trig.GetAngle(o.PosX, o.PosZ, t.PosX, t.PosZ);
                        if (Lba1Trig.Distance > 32000) Lba1Trig.Distance = 32000;
                    }
                    else Lba1Trig.Distance = 32000;

                    var inCone = (((o.Beta + 1024 + 128) - (angle + 1024)) & 1023) <= 256;
                    if (num == 0)
                        value = Comportement == CDiscret ? (inCone ? Lba1Trig.Distance : 32000) : Lba1Trig.Distance;
                    else value = inCone ? Lba1Trig.Distance : 32000;
                }
                break;
            case Lf.Zone: value = o.ZoneSce; break;
            case Lf.NbGoldPieces: value = NbGoldPieces; typeAnswer = 1; break;
            case Lf.NbLittleKeys: value = NbLittleKeys; break;
            case Lf.ComportementHero: value = Comportement; break;
            case Lf.MagicLevel: value = MagicLevel; break;
            case Lf.MagicPoint: value = MagicPoint; break;
            case Lf.Choice: value = GameChoice; typeAnswer = 1; break;
            case Lf.Fuel: value = Fuel; break;
            case Lf.LTrack: value = o.LabelTrack; break;
            case Lf.ZoneObj: value = Objects[Next()].ZoneSce; break;
            case Lf.FlagCube: value = FlagCube[Next()]; break;
            case Lf.FlagGame:
                {
                    var num = Next();
                    if (FlagGame[FlagConsigne] == 0 || num >= MaxInventory) value = FlagGame[num];
                    else value = num == FlagConsigne ? FlagGame[num] : 0;
                }
                break;
            case Lf.UseInventory:
                {
                    var num = Next();
                    if (num >= MaxInventory || FlagGame[FlagConsigne] != 0) value = 0;
                    else value = inventoryAction == num || (FlagInventory[num] && FlagGame[num] == 1) ? 1 : 0;
                    if (value == 1 && num < MaxInventory) Log($"Twinsen uses inventory item {num}");
                }
                break;
            case Lf.LTrackObj: value = Objects[Next()].LabelTrack; break;
            case Lf.Body: value = o.GenBody; break;
            case Lf.BodyObj: value = Objects[Next()].GenBody; break;
            case Lf.Anim: value = o.GenAnim; break;
            case Lf.AnimObj: value = Objects[Next()].GenAnim; break;
            case Lf.CarryBy: value = o.CarryBy; break;
            case Lf.CdRom: value = 0; break;
        }
    }

    // DoTest: compares Value with the byte or word that follows (the comparison operator first).
    private bool DoTest()
    {
        var test = Next();
        int operand;
        if (typeAnswer == 0) operand = (sbyte)Next();
        else operand = Word();
        return test switch
        {
            LtEqual => value == operand,
            LtSup => value > operand,
            LtLess => value < operand,
            LtSupEqual => value >= operand,
            LtLessEqual => value <= operand,
            LtDifferent => value != operand,
            _ => false,
        };
    }

    private void DoLife(int numObj)
    {
        var o = Objects[numObj];
        var flag = 0;
        prg = o.Life;
        pc = o.OffsetLife;

        while (flag != -1)
        {
            if (pc < 0 || pc >= prg.Length) { o.OffsetLife = -1; return; }
            var macroAt = pc;
            switch (Next())
            {
                case Lm.End:
                case Lm.EndLife:
                    o.OffsetLife = -1;
                    flag = -1;
                    break;

                case Lm.Return:
                case Lm.EndComportement:
                    flag = -1;
                    break;

                case Lm.Label:
                case Lm.Comportement:
                    pc++;
                    break;

                case Lm.Fallable:
                    o.Flags &= ~ObjFallable;
                    o.Flags |= Next() * ObjFallable;
                    break;

                case Lm.ComportementHero:
                    InitAnim(GenAnimRien, AnimRepeat, NoAnim, 0);
                    SetComportement(Next());
                    break;

                case Lm.SetMagicLevel:
                    MagicLevel = Next();
                    MagicPoint = MagicLevel * 20;
                    break;

                case Lm.SubMagicPoint:
                    MagicPoint -= Next();
                    if (MagicPoint < 0) MagicPoint = 0;
                    break;

                case Lm.CamFollow:
                    {
                        var num = Next();
                        if (num != NumObjFollow)
                        {
                            NumObjFollow = num;
                            Log($"camera follows actor {num}");
                        }
                    }
                    break;

                case Lm.InitPingouin:
                    {
                        var num = Next();
                        numPingouin = num;
                        Objects[num].WorkFlags |= ObjDead;
                        Objects[num].Body = -1;
                        Objects[num].ZoneSce = -1;
                    }
                    break;

                case Lm.KillObj:
                    {
                        var num = Next();
                        CheckCarrier(num);
                        var k = Objects[num];
                        k.WorkFlags |= ObjDead;
                        k.Body = -1;
                        k.ZoneSce = -1;
                        k.LifePoint = 0;
                    }
                    break;

                case Lm.Suicide:
                    CheckCarrier(numObj);
                    o.WorkFlags |= ObjDead;
                    o.Body = -1;
                    o.ZoneSce = -1;
                    o.LifePoint = 0;
                    break;

                case Lm.SetDir:
                    o.Move = Next();
                    if (o.Move == MoveFollow) o.Info3 = Next();
                    break;

                case Lm.SetDirObj:
                    {
                        var obj = Next();
                        var move = Objects[obj].Move = Next();
                        if (move == MoveFollow) Objects[obj].Info3 = Next();
                    }
                    break;

                case Lm.SetLife:
                case Lm.SetComportement:
                    o.OffsetLife = Word();
                    break;

                case Lm.SetLifeObj:
                case Lm.SetComportementObj:
                    {
                        var num = Next();
                        Objects[num].OffsetLife = Word();
                    }
                    break;

                case Lm.SetLifePointObj:
                    {
                        var num = Next();
                        Objects[num].LifePoint = Next();
                    }
                    break;

                case Lm.SubLifePointObj:
                    {
                        var num = Next();
                        Objects[num].LifePoint -= Next();
                        if (Objects[num].LifePoint < 0) Objects[num].LifePoint = 0;
                    }
                    break;

                case Lm.HitObj:
                    {
                        var num = Next();
                        HitObj(numObj, num, Next(), Objects[num].Beta);
                    }
                    break;

                case Lm.SetTrack:
                    o.OffsetTrack = Word();
                    break;

                case Lm.SetTrackObj:
                    {
                        var num = Next();
                        Objects[num].OffsetTrack = Word();
                    }
                    break;

                case Lm.StopLTrack:
                    o.MemoLabelTrack = o.OffsetLabelTrack;
                    o.OffsetTrack = -1;
                    break;

                case Lm.RestoreLTrack:
                    o.OffsetTrack = o.MemoLabelTrack;
                    break;

                case Lm.If:
                    DoFuncLife(o);
                    if (!DoTest()) pc = Word(); else pc += 2;
                    break;

                case Lm.OrIf:
                    DoFuncLife(o);
                    if (DoTest()) pc = Word(); else pc += 2;
                    break;

                case Lm.SwIf:
                    DoFuncLife(o);
                    if (!DoTest()) pc = Word();
                    else
                    {
                        prg[macroAt] = Lm.Snif;
                        pc += 2;
                    }
                    break;

                case Lm.Snif:
                    DoFuncLife(o);
                    if (!DoTest()) prg[macroAt] = Lm.SwIf;
                    pc = Word();
                    break;

                case Lm.OneIf:
                    DoFuncLife(o);
                    if (!DoTest()) pc = Word();
                    else
                    {
                        pc += 2;
                        prg[macroAt] = Lm.NeverIf;
                    }
                    break;

                case Lm.NeverIf:
                    DoFuncLife(o);
                    DoTest();
                    pc = Word();
                    break;

                case Lm.Offset:
                case Lm.Else:
                    pc = Word();
                    break;

                case Lm.Body:
                    InitBody(Next(), numObj);
                    break;

                case Lm.BodyObj:
                    {
                        var num = Next();
                        InitBody(Next(), num);
                    }
                    break;

                case Lm.AnimObj:
                    {
                        var num = Next();
                        InitAnim(Next(), AnimRepeat, 0, num);
                    }
                    break;

                case Lm.Anim:
                    InitAnim(Next(), AnimRepeat, 0, numObj);
                    break;

                case Lm.AnimSet:
                    o.GenAnim = NoAnim;
                    o.Anim = -1;
                    InitAnim(Next(), AnimRepeat, 0, numObj);
                    break;

                case Lm.MessageObj:
                    {
                        var num = Next();
                        SayDialogue(num, Objects[num].CoulObj, Word());
                    }
                    break;

                case Lm.Message:
                    SayDialogue(numObj, o.CoulObj, Word());
                    break;

                case Lm.SayMessageObj:
                    {
                        var num = Next();
                        SayBubble(num, Word());
                    }
                    break;

                case Lm.SayMessage:
                    SayBubble(numObj, Word());
                    break;

                case Lm.SetFlagCube:
                    {
                        var num = Next();
                        FlagCube[num] = Next();
                    }
                    break;

                case Lm.SetFlagGame:
                    {
                        var num = Next();
                        FlagGame[num] = Next();
                    }
                    break;

                case Lm.SetUsedInventory:
                    {
                        var num = Next();
                        if (num < 24) FlagInventory[num] = true;
                    }
                    break;

                case Lm.GiveGoldPieces:
                    NbGoldPieces -= Word();
                    if (NbGoldPieces < 0) NbGoldPieces = 0;
                    break;

                case Lm.UseOneLittleKey:
                    NbLittleKeys--;
                    if (NbLittleKeys < 0) NbLittleKeys = 0;
                    break;

                case Lm.IncChapter:
                    Chapter++;
                    break;

                case Lm.FoundObject:
                    FoundObject(Next());
                    break;

                case Lm.SetDoorLeft:
                    o.Beta = 768;
                    o.PosX = o.AnimStepX - Word();
                    o.WorkFlags &= ~AutoStopDoor;
                    o.SRot = 0;
                    break;

                case Lm.SetDoorRight:
                    o.Beta = 256;
                    o.PosX = o.AnimStepX + Word();
                    o.WorkFlags &= ~AutoStopDoor;
                    o.SRot = 0;
                    break;

                case Lm.SetDoorUp:
                    o.Beta = 512;
                    o.PosZ = o.AnimStepZ - Word();
                    o.WorkFlags &= ~AutoStopDoor;
                    o.SRot = 0;
                    break;

                case Lm.SetDoorDown:
                    o.Beta = 0;
                    o.PosZ = o.AnimStepZ + Word();
                    o.WorkFlags &= ~AutoStopDoor;
                    o.SRot = 0;
                    break;

                case Lm.GiveBonus:
                    if ((o.OptionFlags & ExtraMask) != 0) GiveExtraBonus(o);
                    if (Next() != 0) o.OptionFlags |= ExtraGiveNothing;    // gives nothing any more
                    break;

                case Lm.ChangeCube:
                    newCube = Next();
                    flagChgCube = 2;
                    Log($"script: change to scene {newCube}");
                    break;

                case Lm.PlayMidi: PlayMusic(Next()); break;
                case Lm.AddFuel: Fuel = Math.Min(100, Fuel + Next()); break;
                case Lm.SubFuel: Fuel = Math.Max(0, Fuel - Next()); break;
                case Lm.SetHoloPos: Log($"holomap position {Next()} set"); break;
                case Lm.ClrHoloPos: Log($"holomap position {Next()} cleared"); break;
                case Lm.SetGrm: indexGrm = Next(); IncrustGrm(indexGrm); break;
                case Lm.GrmOff:
                    if (indexGrm != -1) { indexGrm = zoneGrm = -1; Cube.Restore(); GridChanged?.Invoke(); Log("grid fragment off"); }
                    break;
                case Lm.IncCloverBox: if (NbCloverBox < 10) NbCloverBox++; break;

                case Lm.ObjCol:
                    if (Next() != 0) o.Flags |= CheckObjCol; else o.Flags &= ~CheckObjCol;
                    break;

                case Lm.Invisible:
                    if (Next() != 0) o.Flags |= Invisible; else o.Flags &= ~Invisible;
                    break;

                case Lm.BrickCol:
                    {
                        var num = Next();
                        o.Flags &= ~(CheckBrickCol + ColBasse);
                        if (num == 1) o.Flags |= CheckBrickCol;
                        if (num == 2) o.Flags |= CheckBrickCol + ColBasse;
                    }
                    break;

                case Lm.Zoom: pc++; break;

                case Lm.PosPoint:
                    {
                        var index = Next();
                        var p = TrackPoints[index];
                        o.PosX = p.X; o.PosY = p.Y; o.PosZ = p.Z;
                    }
                    break;

                case Lm.PlayFla:
                    {
                        var start = pc;
                        while (prg[pc] != 0) pc++;
                        pc++;
                        PlayFilm(System.Text.Encoding.ASCII.GetString(prg, start, pc - start - 1));
                    }
                    break;

                case Lm.AddChoice: gameListChoice.Add(Word()); break;
                case Lm.AskChoice: AskChoice(numObj, o.CoulObj, Word()); break;
                case Lm.AskChoiceObj:
                    {
                        var num = Next();
                        AskChoice(num, Objects[num].CoulObj, Word());
                    }
                    break;
                case Lm.BigMessage: SayDialogue(numObj, o.CoulObj, Word(), big: true); break;

                case Lm.FullPoint:
                    Hero.LifePoint = 50;
                    MagicPoint = MagicLevel * 20;
                    break;

                case Lm.Beta:
                    o.Beta = Word();
                    ClearRealAngle(o);
                    break;

                case Lm.ExplodeObj: pc++; break;
                case Lm.BulleOn: bulle = true; break;
                case Lm.BulleOff: bulle = false; break;
                case Lm.HolomapTraj: Log($"holomap trajectory {Next()}"); break;

                case Lm.GameOver:
                    Hero.LifePoint = 0;
                    Hero.WorkFlags |= AnimEnd;
                    NbFourLeafClover = 0;
                    flag = -1;
                    break;

                case Lm.TheEnd:
                    NbFourLeafClover = 0;
                    Hero.LifePoint = 50;
                    MagicPoint = 80;
                    theEnd = true;
                    Log("the end");
                    flag = -1;
                    break;

                case Lm.BrutalExit:
                    theEnd = true;
                    flag = -1;
                    break;

                case Lm.PlayCdTrack: pc++; break;
                case Lm.Text: Log($"text {Word()}"); break;
            }
        }
        // a script that left a comportement running keeps its position; one that ran off the end is finished
        if (o.OffsetLife >= prg.Length) o.OffsetLife = -1;
    }

    // ---------------------------------------------------------------------------------------------- GERETRAK.C

    private void DoTrack(int numObj)
    {
        var o = Objects[numObj];
        var flag = true;
        while (flag)
        {
            if (o.OffsetTrack < 0 || o.OffsetTrack >= o.Track.Length) { o.OffsetTrack = -1; return; }
            var memoOffsetTrack = o.OffsetTrack;
            var track = o.Track;
            var p = o.OffsetTrack + 1;      // first operand
            var macro = track[o.OffsetTrack];
            o.OffsetTrack++;

            switch (macro)
            {
                case Tm.Sample: PlaySample(S16At(track, p), numObj); o.OffsetTrack += 2; break;
                case Tm.SampleRnd: PlaySample(S16At(track, p), numObj); o.OffsetTrack += 2; break;
                case Tm.SampleAlways: PlaySample(S16At(track, p), numObj); o.OffsetTrack += 2; break;
                case Tm.SampleStop: o.OffsetTrack += 2; break;
                case Tm.RepeatSample: PlaySample(S16At(track, p), numObj); o.OffsetTrack += 2; break;
                case Tm.SimpleSample: PlaySample(S16At(track, p), numObj); o.OffsetTrack += 2; break;

                case Tm.PlayFla:
                    {
                        var n = 0;
                        while (track[p + n] != 0) n++;
                        var film = System.Text.Encoding.ASCII.GetString(track, p, n);
                        n++;
                        o.OffsetTrack += n;
                        PlayFilm(film);
                    }
                    break;

                case Tm.Body:
                    InitBody(track[p], numObj);
                    o.OffsetTrack++;
                    break;

                case Tm.NoBody:
                    InitBody(NoBody, numObj);
                    break;

                case Tm.Anim:
                    if (!InitAnim(track[p], AnimRepeat, 0, numObj))
                    {
                        o.OffsetTrack = memoOffsetTrack;
                        flag = false;
                    }
                    else o.OffsetTrack++;
                    break;

                case Tm.WaitAnim:
                    if ((o.WorkFlags & AnimEnd) == 0)
                    {
                        o.OffsetTrack--;
                        flag = false;
                    }
                    else
                    {
                        flag = false;
                        ClearRealAngle(o);
                    }
                    break;

                case Tm.WaitNbAnim:
                    o.OffsetTrack += 2;
                    if ((o.WorkFlags & AnimEnd) == 0) flag = false;
                    else
                    {
                        track[p + 1]++;
                        if (track[p + 1] == track[p]) track[p + 1] = 0;
                        else flag = false;
                    }
                    if (!flag) o.OffsetTrack -= 3;
                    break;

                case Tm.WaitNbSecond:
                    o.OffsetTrack += 5;
                    if (U32At(track, p + 1) == 0) SetU32(track, p + 1, TimerRef + track[p] * 50);
                    if ((uint)TimerRef < (uint)U32At(track, p + 1))
                    {
                        o.OffsetTrack -= 6;
                        flag = false;
                    }
                    else SetU32(track, p + 1, 0);
                    break;

                case Tm.GotoPoint:
                    {
                        o.OffsetTrack++;
                        var pt = TrackPoints[track[p]];
                        var angle = Lba1Trig.GetAngle(o.PosX, o.PosZ, pt.X, pt.Z);
                        if ((o.Flags & Sprite3D) != 0) o.Beta = angle;
                        else o.RealAngle.InitAngleConst(o.Beta, angle, o.SRot, TimerRef);
                        if (Lba1Trig.Distance > 500)
                        {
                            o.OffsetTrack -= 2;
                            flag = false;
                        }
                    }
                    break;

                case Tm.GotoPoint3D:
                    o.OffsetTrack++;
                    if ((o.Flags & Sprite3D) != 0)
                    {
                        var pt = TrackPoints[track[p]];
                        o.Beta = Lba1Trig.GetAngle(o.PosX, o.PosZ, pt.X, pt.Z);
                        var distance2D = Lba1Trig.Distance;
                        o.FlagAnim = Lba1Trig.GetAngle(o.PosY, 0, pt.Y, distance2D);
                        if (Lba1Trig.Distance > 100)
                        {
                            o.OffsetTrack -= 2;
                            flag = false;
                        }
                        else
                        {
                            o.PosX = pt.X; o.PosY = pt.Y; o.PosZ = pt.Z;
                        }
                    }
                    break;

                case Tm.GotoSymPoint:
                    {
                        o.OffsetTrack++;
                        var pt = TrackPoints[track[p]];
                        var angle = 512 + Lba1Trig.GetAngle(o.PosX, o.PosZ, pt.X, pt.Z);
                        if ((o.Flags & Sprite3D) != 0) o.Beta = angle;
                        else o.RealAngle.InitAngleConst(o.Beta, angle, o.SRot, TimerRef);
                        if (Lba1Trig.Distance > 500)
                        {
                            o.OffsetTrack -= 2;
                            flag = false;
                        }
                    }
                    break;

                case Tm.Angle:
                    o.OffsetTrack += 2;
                    if ((o.Flags & Sprite3D) == 0)
                    {
                        var target = S16At(track, p);
                        if (o.RealAngle.Time == 0) o.RealAngle.InitAngleConst(o.Beta, target, o.SRot, TimerRef);
                        if (o.Beta != target)
                        {
                            o.OffsetTrack -= 3;
                            flag = false;
                        }
                        else ClearRealAngle(o);
                    }
                    break;

                case Tm.FaceTwinkel:
                    o.OffsetTrack += 2;
                    if ((o.Flags & Sprite3D) == 0)
                    {
                        var target = S16At(track, p);
                        if (target == -1 && o.RealAngle.Time == 0)
                        {
                            target = Lba1Trig.GetAngle(o.PosX, o.PosZ, Hero.PosX, Hero.PosZ);
                            o.RealAngle.InitAngleConst(o.Beta, target, o.SRot, TimerRef);
                            SetS16(track, p, target);
                        }
                        if (o.Beta != target)
                        {
                            o.OffsetTrack -= 3;
                            flag = false;
                        }
                        else
                        {
                            ClearRealAngle(o);
                            SetS16(track, p, -1);
                        }
                    }
                    break;

                case Tm.AngleRnd:
                    o.OffsetTrack += 4;
                    if ((o.Flags & Sprite3D) == 0)
                    {
                        var target = S16At(track, p + 2);
                        if (target == -1 && o.RealAngle.Time == 0)
                        {
                            var range = S16At(track, p);
                            if ((Rand() & 1) != 0) target = (o.Beta + 256 + range / 2 - Rnd(range)) & 1023;
                            else target = (o.Beta - 256 - range / 2 + Rnd(range)) & 1023;
                            o.RealAngle.InitAngleConst(o.Beta, target, o.SRot, TimerRef);
                            SetS16(track, p + 2, target);
                        }
                        if (o.Beta != target)
                        {
                            o.OffsetTrack -= 5;
                            flag = false;
                        }
                        else
                        {
                            ClearRealAngle(o);
                            SetS16(track, p + 2, -1);
                        }
                    }
                    break;

                case Tm.OpenLeft:
                case Tm.OpenRight:
                case Tm.OpenUp:
                case Tm.OpenDown:
                    o.OffsetTrack += 2;
                    if ((o.Flags & (Sprite3D + SpriteClip)) == Sprite3D + SpriteClip)
                    {
                        o.Beta = macro switch { Tm.OpenLeft => 768, Tm.OpenRight => 256, Tm.OpenUp => 512, _ => 0 };
                        o.DoorWidth = S16At(track, p);
                        o.WorkFlags |= AutoStopDoor;
                        o.SRot = 1000;
                        o.RealAngle.InitValue(0, 1000, 50, TimerRef);
                    }
                    break;

                case Tm.Close:
                    if ((o.Flags & (Sprite3D + SpriteClip)) == Sprite3D + SpriteClip)
                    {
                        o.WorkFlags |= AutoStopDoor;
                        o.DoorWidth = 0;
                        o.SRot = -1000;
                        o.RealAngle.InitValue(0, -1000, 50, TimerRef);
                    }
                    break;

                case Tm.WaitDoor:
                    if ((o.Flags & (Sprite3D + SpriteClip)) == Sprite3D + SpriteClip && o.SRot != 0)
                    {
                        o.OffsetTrack--;
                        flag = false;
                    }
                    break;

                case Tm.Beta:
                    o.OffsetTrack += 2;
                    o.Beta = S16At(track, p);
                    if ((o.Flags & Sprite3D) == 0) ClearRealAngle(o);
                    break;

                case Tm.PosPoint:
                    {
                        o.OffsetTrack++;
                        var pt = TrackPoints[track[p]];
                        if ((o.Flags & Sprite3D) != 0) o.SRot = 0;
                        o.PosX = pt.X; o.PosY = pt.Y; o.PosZ = pt.Z;
                    }
                    break;

                case Tm.Label:
                    o.LabelTrack = track[p];
                    o.OffsetTrack++;
                    o.OffsetLabelTrack = o.OffsetTrack - 2;
                    break;

                case Tm.Goto:
                    o.OffsetTrack = S16At(track, p);
                    break;

                case Tm.Loop:
                    o.OffsetTrack = 0;
                    break;

                case Tm.Speed:
                    o.OffsetTrack += 2;
                    o.SRot = S16At(track, p);
                    if ((o.Flags & Sprite3D) != 0) o.RealAngle.InitValue(0, o.SRot, 50, TimerRef);
                    break;

                case Tm.Background:
                    o.OffsetTrack++;
                    if (track[p] != 0) o.Flags |= ObjBackground; else o.Flags &= ~ObjBackground;
                    break;

                case Tm.End:
                case Tm.Stop:
                    o.OffsetTrack = -1;
                    flag = false;
                    break;
            }
        }
    }
}
