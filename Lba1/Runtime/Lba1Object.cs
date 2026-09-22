namespace LBAAssembler.Lba1.Runtime;

// Constants of the LBA1 engine (COMMON.H, DEFINES.H, LIB_SYS.H).
internal static class Lba1Const
{
    public const int SizeBrickXZ = 512, SizeBrickY = 256, DemiBrickXZ = 256;

    // Flags
    public const int CheckObjCol = 1, CheckBrickCol = 2, CheckZone = 4, SpriteClip = 8, Pushable = 16, ColBasse = 32, CheckCodeJeu = 64,
        Invisible = 512, Sprite3D = 1024, ObjFallable = 2048, NoShadow = 4096, ObjBackground = 8192, ObjCarrier = 16384, MiniZv = 32768;

    // WorkFlags
    public const int WaitHitFrame = 1, OkHit = 2, AnimEnd = 4, NewFrame = 8, WasDrawn = 16, ObjDead = 32, AutoStopDoor = 64, AnimMasterRot = 128, Falling = 256;

    // FlagAnim
    public const int AnimRepeat = 0, AnimThen = 1, AnimAllThen = 2, AnimInsert = 3, AnimSet = 4;

    // moves
    public const int NoMove = 0, MoveManual = 1, MoveFollow = 2, MoveTrack = 3, MoveFollow2 = 4, MoveTrackAttack = 5, MoveSameXZ = 6, MoveRandom = 7;

    // behaviours
    public const int CNormal = 0, CSportif = 1, CAgressif = 2, CDiscret = 3, CProtopack = 4;

    // generic bodies / animations
    public const int NoBody = 255, NoAnim = 255, GenBodyNormal = 0, GenBodyTunique = 1, GenBodySabre = 2;
    public const int GenAnimRien = 0, GenAnimMarche = 1, GenAnimRecule = 2, GenAnimGauche = 3, GenAnimDroite = 4, GenAnimEncaisse = 5,
        GenAnimChoc = 6, GenAnimTombe = 7, GenAnimReception = 8, GenAnimReception2 = 9, GenAnimMort = 10, GenAnimAction = 11,
        GenAnimMonte = 12, GenAnimEchelle = 13, GenAnimSaute = 14, GenAnimLance = 15, GenAnimCache = 16, GenAnimCoup1 = 17,
        GenAnimCoup2 = 18, GenAnimCoup3 = 19, GenAnimTrouve = 20, GenAnimNoyade = 21, GenAnimChoc2 = 22, GenAnimSabre = 23, GenAnimDegaine = 24;

    // input
    public const int JUp = 1, JDown = 2, JLeft = 4, JRight = 8;
    public const int FSpace = 1, FReturn = 2, FCtrl = 4, FAlt = 8, FShift = 32;

    public const int MaxObjects = 100, MaxFlagsCube = 80, MaxFlagsGame = 255, MaxInventory = 28, FlagConsigne = 70;
}

// T_OBJET: one actor (or the hero, [0]) of the running scene. Positions, sizes and angles are 16-bit like the engine's
// WORD fields; the script buffers are private copies because scripts rewrite themselves while they run (SWIF / ONEIF
// change their own opcode, WAIT_NB_SECOND keeps its timer in the bytecode).
internal sealed class Lba1Object
{
    public int Number;

    public int GenBody, GenAnim, NextGenAnim;
    public int Col;
    public int Sprite;
    public int OffsetLabelTrack;

    public Lba1Entity? Entity;

    public int OptionFlags, NbBonus, Armure, CoulObj;

    // Body: the BODY.HQR index of a 3D actor, the sprite number of a sprite actor, -1 = none.
    public int Body;
    public int PosX, PosY, PosZ;
    public int OldPosX, OldPosY, OldPosZ;
    public int XMin, XMax, YMin, YMax, ZMin, ZMax;

    public int Beta;
    public int SRot;
    public Lba1RealValue RealAngle = new();
    public int Move;

    public byte[] Track = Array.Empty<byte>();
    public int OffsetTrack;
    public byte[] Life = Array.Empty<byte>();
    public int OffsetLife;

    public int Info, Info1, Info2, Info3;

    public int ObjCol, CarryBy, ZoneSce, LabelTrack, MemoLabelTrack;
    public int Flags, WorkFlags;
    public int HitBy, HitForce, LifePoint;

    public int AnimStepBeta, AnimStepX, AnimStepY, AnimStepZ;
    public int DoorWidth;

    public int Anim;            // ANIM.HQR index of the current animation, -1 = none
    public int Frame, FlagAnim;
    public int CodeJeu;

    // the animation's timing (the body structure's "offset source" / "memo ticks" in the engine)
    public bool AnimStarted;
    public int AnimMemoTicks;
    public Lba1Animation? Animation;
    public byte[]? AnimActions;   // the animation's frame actions (count byte, then entries), as in the entity file
    // The key frame the animation last reached (the engine's "offset source"): what the pose blends from. Null until the
    // actor has reached one, when the first frame is taken as it is.
    public IReadOnlyList<Lba1Animation.BoneFrame>? PoseFrom;

    public bool IsSprite => (Flags & Lba1Const.Sprite3D) != 0;
    public bool IsDead => (WorkFlags & Lba1Const.ObjDead) != 0;
}
