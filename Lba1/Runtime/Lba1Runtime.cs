using LBA2LevelEditor.Scenes;
using static LBA2LevelEditor.Lba1.Runtime.Lba1Const;

namespace LBA2LevelEditor.Lba1.Runtime;

// A headless simulation of LBA1's game logic: the scene's actors run their life and track scripts, the hero walks under
// scripted input, animations move actors, bricks and actors collide, and zones change scene, exactly as the engine's
// main loop does (PERSO.C MainLoop, OBJECT.C, GERELIFE.C, GERETRAK.C), ported function for function from the LBA1
// source. Anything that is only presentation (drawing, sound, dialogue, the inventory, holomap, films) is not run; it
// is reported through Events so a test or the editor can see what a script asked for.
//
// Time is the game's 50 Hz timer (TimerRef); Frame() advances it by TicksPerFrame and runs one pass of the object loop.
internal sealed partial class Lba1Runtime
{
    public Lba1RuntimeData Data { get; }

    // ---- time ----
    public int TimerRef { get; private set; } = 1000;
    public int TicksPerFrame { get; set; } = 2;
    public int FrameCount { get; private set; }

    // ---- input (MyJoy / MyFire of the current frame) ----
    public int Joy, Fire;
    private int myJoy, myFire, lastMyJoy, lastMyFire;
    private bool lastJoyFlag;

    // ---- the scene ----
    public int NumCube { get; private set; } = -1;
    private int newCube = -1;
    private int flagChgCube;
    private int newPosX, newPosY, newPosZ;
    private int sceneStartX = -1, sceneStartY, sceneStartZ;
    private int cubeStartX, cubeStartY, cubeStartZ;
    public int Island { get; private set; }
    public int GameOverCube { get; private set; }
    public SceneModel? Scene { get; private set; }
    public Lba1Cube Cube { get; private set; } = null!;
    public Lba1Object[] Objects { get; } = Enumerable.Range(0, MaxObjects).Select(i => new Lba1Object { Number = i }).ToArray();
    public int NbObjets { get; private set; }
    public IReadOnlyList<SceneZoneModel> Zones { get; private set; } = Array.Empty<SceneZoneModel>();
    public IReadOnlyList<SceneTrackPoint> TrackPoints { get; private set; } = Array.Empty<SceneTrackPoint>();

    // ---- game state ----
    public int[] FlagGame { get; } = new int[MaxFlagsGame + 1];
    public int[] FlagCube { get; } = new int[MaxFlagsCube];
    public bool[] FlagInventory { get; } = new bool[MaxInventory];
    public int Chapter { get; set; }
    public int NbGoldPieces, NbLittleKeys, MagicLevel, MagicPoint, NbCloverBox, NbFourLeafClover, Weapon, Fuel;
    public int Comportement { get; private set; }
    private int saveComportement, saveBeta;
    public int NumObjFollow { get; private set; }
    private bool actionNormal, flagClimbing;
    private int stepFalling, startYFalling;
    private readonly Lba1RealValue realFalling = new();
    private int flagWater;
    private bool bulle = true;
    private int numPingouin;

    public Lba1Object Hero => Objects[0];

    // What scripts asked for that the simulation doesn't perform: dialogue, samples, music, films, camera ...
    public List<string> Events { get; } = new();
    public List<int> CubeHistory { get; } = new();

    public Lba1Runtime(Lba1RuntimeData data)
    {
        Data = data;
        InitPerso();
    }

    private void Log(string what) => Events.Add($"t{TimerRef}: {what}");

    // ---------------------------------------------------------------------------------------------- OBJECT.C: objects

    private void InitObject(int n)
    {
        var o = Objects[n];
        o.GenBody = GenBodyNormal;
        o.GenAnim = GenAnimRien;
        o.PosX = 0; o.PosY = SizeBrickY; o.PosZ = 0;
        o.XMin = o.XMax = o.YMin = o.YMax = o.ZMin = o.ZMax = 0;
        o.Beta = 0;
        o.SRot = 40;
        o.Move = NoMove;
        o.Info = o.Info1 = o.Info2 = o.Info3 = 0;
        o.Col = 0;
        o.ObjCol = -1;
        o.CarryBy = -1;
        o.ZoneSce = -1;
        o.Flags = 0;
        o.WorkFlags = 0;
        o.LifePoint = 50;
        o.Armure = 1;
        o.HitBy = -1;
        o.AnimStepBeta = o.AnimStepX = o.AnimStepY = o.AnimStepZ = 0;
        o.Body = -1;
        o.Anim = -1;
        o.FlagAnim = 0;
        o.Frame = 0;
        o.RealAngle.InitAngle(0, 0, 0, TimerRef);
        o.OffsetTrack = -1;
        o.OffsetLife = 0;
        o.AnimStarted = false;
        o.Animation = null;
        o.PoseFrom = null;
    }

    private void InitPerso()
    {
        InitObject(0);
        var o = Hero;
        o.GenBody = GenBodyNormal;
        o.LifePoint = 50;
        o.CoulObj = 4;
        NbGoldPieces = 0; NbLittleKeys = 0; MagicPoint = 0; NbCloverBox = 2; NbFourLeafClover = 2; Weapon = 0;
        Comportement = CNormal;
        saveComportement = CNormal;
    }

    // The hero's file3d depends on his behaviour: FILE_3D_NORMAL .. FILE_3D_PROTOPACK are entities 0..4.
    private void SetComportement(int comportement)
    {
        var o = Hero;
        if (comportement is >= CNormal and <= CProtopack)
        {
            Comportement = comportement;
            o.Entity = Data.Entity(comportement);
        }
        var memoGenBody = o.GenBody;
        o.GenBody = NoBody;
        o.Body = -1;
        InitBody(memoGenBody, 0);
        o.GenAnim = NoAnim;
        o.FlagAnim = 0;
        InitAnim(GenAnimRien, AnimRepeat, NoAnim, 0);
    }

    // A behaviour chosen from outside (the play window's picker), as if the player had switched it and it stays for the scene.
    public void SetBehaviour(int comportement)
    {
        if (comportement is < CNormal or > CProtopack || comportement == Comportement) return;
        SetComportement(comportement);
        saveComportement = comportement;
    }

    // RestartPerso: reinitialise the hero without touching his position.
    private void RestartPerso()
    {
        var o = Hero;
        o.Move = MoveManual;
        o.WorkFlags = 0;
        o.Flags = ObjFallable + CheckZone + CheckObjCol + CheckBrickCol + CheckCodeJeu;
        o.Armure = 1;
        o.OffsetTrack = -1;
        o.LabelTrack = -1;
        o.OffsetLife = 0;
        o.ZoneSce = -1;
        o.Beta = saveBeta;
        o.RealAngle.InitAngle(o.Beta, o.Beta, 0, TimerRef);
        SetComportement(saveComportement);
        flagWater = 0;
    }

    // ---- bodies and animations (FICHE.C SearchBody / SearchAnim, OBJECT.C InitBody / InitAnim) ----

    private (int Hqr, (int, int, int, int, int, int)? Volume)? SearchBody(int genBody, int numObj)
    {
        var e = Objects[numObj].Entity;
        return e is not null && e.Bodies.TryGetValue(genBody, out var b) ? (b.Hqr, b.Volume) : null;
    }

    private (int Hqr, byte[]? Actions)? SearchAnim(int genAnim, int numObj)
    {
        var e = Objects[numObj].Entity;
        return e is not null && e.Anims.TryGetValue(genAnim, out var a) ? (a.Hqr, a.Actions) : null;
    }

    private void InitBody(int genNewBody, int numObj)
    {
        var o = Objects[numObj];
        if ((o.Flags & Sprite3D) != 0) return;

        if (numObj == 0 && Comportement == CProtopack && genNewBody != GenBodyNormal && genNewBody != GenBodyTunique)
            SetComportement(CNormal);

        (int Hqr, (int XMin, int YMin, int ZMin, int XMax, int YMax, int ZMax)? Volume)? found = genNewBody != NoBody ? SearchBody(genNewBody, numObj) : null;
        if (found is { } body)
        {
            if (body.Hqr != o.Body)
            {
                o.Body = body.Hqr;
                o.GenBody = genNewBody;
                var box = Data.BodyBox(body.Hqr);
                if (body.Volume is null)
                {
                    if (box is { } b)
                    {
                        int x0 = b.X0, x1 = b.X1, z0 = b.Z0, z1 = b.Z1;
                        o.YMin = b.YMin;
                        o.YMax = b.YMax;
                        int size = (o.Flags & MiniZv) != 0
                            ? (x1 - x0 < z1 - z0 ? (x1 - x0) / 2 : (z1 - z0) / 2)
                            : ((x1 - x0) + (z1 - z0)) / 4;
                        o.XMin = -size; o.XMax = +size; o.ZMin = -size; o.ZMax = +size;
                    }
                }
                else
                {
                    var v = body.Volume.Value;
                    o.XMin = v.XMin; o.XMax = v.XMax; o.YMin = v.YMin; o.YMax = v.YMax; o.ZMin = v.ZMin; o.ZMax = v.ZMax;
                }
            }
        }
        else
        {
            o.GenBody = NoBody;
            o.Body = -1;
            o.YMin = o.YMax = o.XMin = o.XMax = o.ZMin = o.ZMax = 0;
        }
    }

    private void InitSprite(int newSprite, int numObj)
    {
        var o = Objects[numObj];
        if ((o.Flags & Sprite3D) == 0) return;
        if (newSprite != -1 && newSprite != o.Body)
        {
            o.Body = newSprite;
            if (Data.SpriteVolume(newSprite) is { } v)
            {
                o.XMin = v.XMin; o.XMax = v.XMax; o.YMin = v.YMin; o.YMax = v.YMax; o.ZMin = v.ZMin; o.ZMax = v.ZMax;
            }
        }
    }

    // InitAnim: switch an actor to a generic animation. Returns false when it can't (no body, a sprite, or an
    // "insert" animation is still playing and the request was queued).
    private bool InitAnim(int genNewAnim, int flag, int genNextAnim, int numObj)
    {
        var o = Objects[numObj];
        if (o.Body == -1) return false;
        if ((o.Flags & Sprite3D) != 0) return false;

        if (genNewAnim == o.GenAnim && o.Anim != -1) return true;

        if (genNextAnim == NoAnim && o.FlagAnim != AnimAllThen) genNextAnim = o.GenAnim;

        var found = SearchAnim(genNewAnim, numObj);
        if (found is null) found = SearchAnim(GenAnimRien, numObj);
        var newAnim = found?.Hqr ?? -1;

        if (flag != AnimSet && o.FlagAnim == AnimAllThen)
        {
            o.NextGenAnim = genNewAnim;
            return false;
        }

        if (flag == AnimInsert)
        {
            flag = AnimAllThen;
            genNextAnim = o.GenAnim;
            if (genNextAnim is GenAnimLance or GenAnimTombe or GenAnimReception or GenAnimReception2) genNextAnim = GenAnimRien;
        }
        if (flag == AnimSet) flag = AnimAllThen;

        // SetAnimObjet / StockInterAnim: the new animation's clock starts now
        o.AnimStarted = true;
        o.AnimMemoTicks = TimerRef;

        // StockInterAnim keeps the pose the body is in now, and the new animation blends from it; the very first animation
        // (SetAnimObjet) starts from its own first frame
        Lba1Animation.BoneFrame[]? blendFrom = null;
        if (o.Anim != -1 && o.Animation is not null && CurrentPose(o) is { } current)
            blendFrom = current.Select(b => new Lba1Animation.BoneFrame(b.Type, (int)Math.Round(b.X), (int)Math.Round(b.Y), (int)Math.Round(b.Z))).ToArray();

        o.Anim = newAnim;
        o.Animation = newAnim >= 0 ? Data.Animation(newAnim) : null;
        o.PoseFrom = blendFrom ?? (o.Animation is { Frames.Count: > 0 } first ? first.Frames[0].Bones : null);
        o.AnimActions = found?.Actions;
        o.GenAnim = genNewAnim;
        o.NextGenAnim = genNextAnim;
        o.FlagAnim = flag;
        o.Frame = 0;
        o.WorkFlags &= ~(OkHit + AnimEnd);
        o.WorkFlags |= NewFrame;
        if (o.AnimActions is not null) GereAnimAction(o, numObj);
        o.AnimStepBeta = 0; o.AnimStepX = 0; o.AnimStepY = 0; o.AnimStepZ = 0;
        return true;
    }

    // StartInitObj: called for every actor when a scene starts.
    private void StartInitObj(int n)
    {
        var o = Objects[n];
        if ((o.Flags & Sprite3D) != 0)
        {
            if (o.HitForce != 0) o.WorkFlags |= OkHit;
            o.Body = -1;
            InitSprite(o.Sprite, n);
            o.RealAngle.InitAngle(0, 0, 0, TimerRef);
            if ((o.Flags & SpriteClip) != 0)
            {
                o.AnimStepX = o.PosX;
                o.AnimStepY = o.PosY;
                o.AnimStepZ = o.PosZ;
            }
        }
        else
        {
            o.Body = -1;
            InitBody(o.GenBody, n);
            o.Anim = -1;
            o.FlagAnim = 0;
            if (o.Body != -1) InitAnim(o.GenAnim, AnimRepeat, NoAnim, n);
            o.RealAngle.InitAngle(o.Beta, o.Beta, 0, TimerRef);
        }
        o.OffsetTrack = -1;
        o.LabelTrack = -1;
        o.OffsetLife = 0;
    }

    // ---------------------------------------------------------------------------------------------- DISKFUNC.C: LoadScene

    private void LoadScene(int numScene)
    {
        var scene = Data.Scene(numScene);
        Scene = scene;
        newCube = numScene;
        Island = scene.Island;
        GameOverCube = scene.GameOverScene;

        cubeStartX = scene.Actors[0].X; cubeStartY = scene.Actors[0].Y; cubeStartZ = scene.Actors[0].Z;
        var hero = Hero;
        hero.Track = (byte[])scene.Actors[0].Track.Clone();
        hero.Life = (byte[])scene.Actors[0].Life.Clone();

        NbObjets = scene.Actors.Count;
        for (var n = 1; n < NbObjets; n++)
        {
            var src = scene.Actors[n];
            InitObject(n);
            var o = Objects[n];
            o.Flags = (int)src.Flags;
            if ((o.Flags & Sprite3D) == 0) o.Entity = Data.Entity(src.Entity);
            else o.Entity = null;
            o.GenBody = src.Body < 0 ? NoBody : src.Body;
            o.GenAnim = src.Anim;
            o.Sprite = src.Sprite;
            o.OldPosX = o.PosX = src.X; o.OldPosY = o.PosY = src.Y; o.OldPosZ = o.PosZ = src.Z;
            o.HitForce = src.HitForce;
            o.OptionFlags = src.OptionFlags & ~1;      // EXTRA_GIVE_NOTHING
            o.Beta = src.Beta;
            o.SRot = src.SRot;
            o.Move = src.Move;
            o.Info = src.Info[0]; o.Info1 = src.Info[1]; o.Info2 = src.Info[2]; o.Info3 = src.Info[3];
            o.NbBonus = src.NbBonus;
            o.CoulObj = src.CoulObj;
            o.Armure = src.Armor;
            o.LifePoint = src.LifePoints;
            o.Track = (byte[])src.Track.Clone();
            o.Life = (byte[])src.Life.Clone();
        }
        Zones = scene.Zones.Select(z => z.Clone()).ToList();     // a run may change zones (a giver marks itself taken)
        ClearExtras();
        TrackPoints = scene.TrackPoints;
    }

    // ---------------------------------------------------------------------------------------------- OBJECT.C: ChangeCube

    // Starts (or restarts) a scene. `fromZone`: the hero arrives where the zone put him (his position relative to the
    // zone plus the zone's arrival point) instead of at the scene's start position.
    public void ChangeCube(int scene)
    {
        newCube = scene;
        ChangeCube();
    }

    private void ChangeCube()
    {
        var oldCube = NumCube;
        NumCube = newCube;
        CubeHistory.Add(NumCube);

        // ClearScene: the scene's flags start at 0 each time
        Array.Clear(FlagCube);

        Hero.Move = MoveManual;
        Hero.ZoneSce = -1;
        Hero.OffsetLife = 0;
        Hero.OffsetTrack = -1;
        Hero.LabelTrack = -1;

        LoadScene(NewCubeNumber());
        Cube = Data.Cube(NumCube).Copy();
        zoneGrm = indexGrm = -1;
        GridChanged?.Invoke();
        NumObjFollow = 0;

        if (flagChgCube == 1)
        {
            sceneStartX = newPosX; sceneStartY = newPosY; sceneStartZ = newPosZ;
        }
        if (flagChgCube == 2 || flagChgCube == 0)
        {
            sceneStartX = cubeStartX; sceneStartY = cubeStartY; sceneStartZ = cubeStartZ;
        }

        Hero.PosX = sceneStartX;
        Hero.PosY = startYFallingInit(sceneStartY);
        Hero.PosZ = sceneStartZ;

        if (NumCube != oldCube)
        {
            saveComportement = Comportement;
            saveBeta = Hero.Beta;
        }

        RestartPerso();

        for (var n = 1; n < NbObjets; n++) StartInitObj(n);

        NbLittleKeys = 0;
        lastJoyFlag = true;
        newCube = -1;
        flagChgCube = 0;
        NumObjFollow = 0;
        cameraZone = false;
        Bubbles.Clear();
        CenterCameraOnHero();
        PlayMusic(Scene?.Music ?? -1);
        timerNextAmbiance = TimerRef + (Rnd(Math.Max(1, Scene?.SecondEcart ?? 1)) + (Scene?.SecondMin ?? 0)) * 50;
        samplePlayed = 0;
        Log($"scene {NumCube} starts, hero at ({Hero.PosX}, {Hero.PosY}, {Hero.PosZ})");
    }

    private int NewCubeNumber() => NumCube;

    private int startYFallingInit(int y)
    {
        startYFalling = y;
        return y;
    }

    // ---------------------------------------------------------------------------------------------- PERSO.C: MainLoop

    private bool cameraZone;

    // One pass of the main loop's object section. Returns false once the game is over.
    public bool Frame()
    {
        if (dialogues.Count > 0 || pendingFilm is not null) return true;    // a dialogue box or a film: the engine has stopped its clock
        TimerRef += TicksPerFrame;
        FrameCount++;
        if (newCube != -1) ChangeCube();

        lastMyFire = myFire;
        myJoy = Joy;
        myFire = Fire;

        // StepFalling: how far things fall during this frame (proportional to the time since the last one)
        stepFalling = realFalling.GetValue(TimerRef);
        if (stepFalling == 0) stepFalling = 1;
        realFalling.InitValue(0, -256, 5, TimerRef);

        cameraZone = false;

        for (var i = 0; i < NbObjets; i++) Objects[i].HitBy = -1;

        GereExtras();
        GereAmbiance();

        for (var i = 0; i < NbObjets; i++)
        {
            var o = Objects[i];
            if ((o.WorkFlags & ObjDead) != 0) continue;

            if (o.LifePoint == 0)
            {
                if (i == 0)
                {
                    InitAnim(GenAnimMort, AnimSet, GenAnimRien, 0);
                    o.Move = NoMove;
                }
                else
                {
                    Log($"actor {i} dies");
                    PlaySample(37, i);
                    if (i == numPingouin) ExtraExplo(o.PosX, o.PosY, o.PosZ);
                }
                if ((o.OptionFlags & ExtraMask) != 0 && (o.OptionFlags & ExtraGiveNothing) == 0) GiveExtraBonus(o);
            }

            DoDir(i);

            o.OldPosX = o.PosX;
            o.OldPosY = o.PosY;
            o.OldPosZ = o.PosZ;

            if (o.OffsetTrack != -1) DoTrack(i);
            DoAnim(i);

            if ((o.Flags & CheckZone) != 0) CheckZoneSce(o, i);

            if (o.OffsetLife != -1) DoLife(i);

            if (theEnd) return false;

            if ((o.Flags & CheckCodeJeu) != 0)
            {
                o.CodeJeu = WorldCodeBrick(o.PosX, o.PosY - 1, o.PosZ);
                if ((o.CodeJeu & 0xF0) == 0xF0 && (o.CodeJeu & 0x0F) == 1)
                {
                    // water
                    if (i == 0)
                    {
                        if (!(Comportement == CProtopack && o.GenAnim == GenAnimMarche))
                        {
                            if (flagWater == 0) InitAnim(GenAnimNoyade, AnimSet, GenAnimRien, 0);
                            flagWater = 1;
                            o.Move = NoMove;
                            o.LifePoint = -1;
                            o.Flags |= NoShadow;
                            Log("Twinsen drowns");
                        }
                    }
                    else
                    {
                        // anything else dies in water
                        PlaySample(37, i);
                        if ((o.OptionFlags & ExtraMask) != 0 && (o.OptionFlags & ExtraGiveNothing) == 0) GiveExtraBonus(o);
                        o.LifePoint = 0;
                    }
                }
            }

            if (o.LifePoint <= 0)
            {
                if (i == 0)
                {
                    if ((o.WorkFlags & AnimEnd) != 0)
                    {
                        if (NbFourLeafClover > 0)
                        {
                            flagWater = 0;
                            NbFourLeafClover--;
                            Hero.PosX = sceneStartX; Hero.PosY = sceneStartY; Hero.PosZ = sceneStartZ;
                            newCube = NumCube;
                            flagChgCube = 3;
                            Hero.LifePoint = 50;
                            MagicPoint = MagicLevel * 20;
                            Log("Twinsen is revived by a clover leaf");
                            return true;
                        }
                        Log("game over");
                        GameOver = true;
                        return false;
                    }
                }
                else
                {
                    CheckCarrier(i);
                    o.WorkFlags |= ObjDead;
                    o.Body = -1;
                    o.ZoneSce = -1;
                }
            }

            if (newCube != -1) { inventoryAction = -1; return true; }   // "goto startloop": the scene changes at the top of the next frame
        }
        inventoryAction = -1;      // an item used from the inventory counts for one pass of the loop only
        GereCamera();
        return true;
    }

    // Test / editor hook: put the hero somewhere (world units, facing in 1024ths of a turn) and let him stand there.
    public void Place(int x, int y, int z, int beta)
    {
        var o = Hero;
        o.PosX = o.OldPosX = x; o.PosY = o.OldPosY = y; o.PosZ = o.OldPosZ = z;
        o.Beta = beta & 1023;
        o.RealAngle.InitAngle(o.Beta, o.Beta, 0, TimerRef);
        o.WorkFlags &= ~Falling;
    }

    public bool GameOver { get; private set; }
    private bool theEnd;

    // Runs frames until `frames` have passed, the game ends or `stop` says so.
    public void Run(int frames, Func<bool>? stop = null)
    {
        for (var i = 0; i < frames; i++)
        {
            if (!Frame()) break;
            if (stop?.Invoke() == true) break;
        }
    }
}
