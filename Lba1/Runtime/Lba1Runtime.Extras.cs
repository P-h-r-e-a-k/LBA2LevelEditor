using LBAAssembler.Scenes;
using static LBAAssembler.Lba1.Runtime.Lba1Const;

namespace LBAAssembler.Lba1.Runtime;

// EXTRA.C: the small flying and lying things of a scene: bonuses (money, life, magic, keys, clover leaves) dropped by
// creatures and zones, projectiles thrown by actors, and Twinsen's magic ball with its homing return.
internal sealed class Lba1Extra
{
    public int Sprite = -1;             // SPRITES.HQR picture, -1 = slot free
    public int Flags;
    public int PosX, PosY, PosZ;
    public int OrgX, OrgY, OrgZ;
    public int Vx, Vy, Vz;
    public int Poids;                   // weight (gravity), or a homing extra's last angle
    public int Timer, TimeOut;          // start tick, life span (or owner for projectiles)
    public int HitForce;
    public int Divers = 1;              // how many (of a bonus)
    public readonly Lba1RealValue Real = new();
}

internal sealed partial class Lba1Runtime
{
    public const int MaxExtras = 50;

    public const int ExtraGiveNothing = 1, ExtraGiveMoney = 16, ExtraGiveLife = 32, ExtraGiveMagic = 64, ExtraGiveKey = 128, ExtraGiveClover = 256,
        ExtraMask = 16 + 32 + 64 + 128 + 256;
    public const int ExtraTimeOut = 1, ExtraFly = 2, ExtraEndObj = 4, ExtraEndCol = 8, ExtraStopCol = 16, ExtraTakable = 32, ExtraFlash = 64,
        ExtraSearchObj = 128, ExtraImpact = 256, ExtraSearchKey = 512, ExtraTimeIn = 1024, ExtraOneFrame = 2048, ExtraExplo_ = 4096,
        ExtraWaitNoCol = 8192, ExtraWaitSomeTime = 16384;

    public Lba1Extra[] Extras { get; } = Enumerable.Range(0, MaxExtras).Select(_ => new Lba1Extra()).ToArray();
    public int MagicBall { get; private set; } = -1;
    private int magicBallType, magicBallCount;

    // Game flags the engine tests for Twinsen's equipment (COMMON.H FLAG_*).
    public const int FlagHolomap = 0, FlagBalleMagique = 1, FlagSabreMagique = 2, FlagTrompe = 3, FlagTunique = 4, FlagLivreBu = 5, FlagMedaillon = 6,
        FlagProtopack = 12, FlagMecaPingouin = 14, FlagCarburant = 15, FlagClover = 27;

    private void ClearExtras()
    {
        foreach (var e in Extras) { e.Sprite = -1; e.Divers = 1; }
        MagicBall = -1;
    }

    private (int XMin, int XMax, int YMin, int YMax, int ZMin, int ZMax) ExtraVolume(int sprite)
        => Data.SpriteVolume(sprite) ?? (0, 0, 0, 0, 0, 0);

    private int FreeExtra()
    {
        for (var n = 0; n < MaxExtras; n++) if (Extras[n].Sprite == -1) return n;
        return -1;
    }

    private void InitFly(Lba1Extra e, int alpha, int beta, int vitesse, int poids)
    {
        e.Flags |= ExtraFly;
        e.OrgX = e.PosX; e.OrgY = e.PosY; e.OrgZ = e.PosZ;
        var (x0, y0) = Lba1Trig.Rotate(vitesse, 0, alpha);
        e.Vy = -y0;
        var (x1, y1) = Lba1Trig.Rotate(0, x0, beta);
        e.Vx = x1; e.Vz = y1;
        e.Poids = poids;
        e.Timer = TimerRef;
    }

    private void BounceExtra(Lba1Extra e, int oldx, int oldy, int oldz)
    {
        if (WorldColBrick(oldx, e.PosY, oldz) != 0) e.Vy = -e.Vy;
        if (WorldColBrick(e.PosX, oldy, oldz) != 0) e.Vx = -e.Vx;
        if (WorldColBrick(oldx, oldy, e.PosZ) != 0) e.Vz = -e.Vz;
        e.OrgX = e.PosX = oldx; e.OrgY = e.PosY = oldy; e.OrgZ = e.PosZ = oldz;
        e.Timer = TimerRef;
    }

    // A bonus (sprite 3 money, 4 life, 5 magic, 6 key, 7 clover) tossed out at a position.
    private int ExtraBonus(int x, int y, int z, int alpha, int beta, int sprite, int count)
    {
        var n = FreeExtra();
        if (n < 0) return -1;
        var e = Extras[n];
        e.Sprite = sprite;
        e.Flags = ExtraStopCol + ExtraWaitSomeTime + ExtraTakable;
        if (sprite != 6) e.Flags += ExtraTimeOut + ExtraFlash;   // keys never vanish
        e.PosX = x; e.PosY = y; e.PosZ = z;
        InitFly(e, alpha, beta, 40, 15);
        e.HitForce = 0;
        e.Timer = TimerRef;
        e.TimeOut = 50 * 20;
        e.Divers = count;
        return n;
    }

    private int ExtraExplo(int x, int y, int z)
    {
        var n = FreeExtra();
        if (n < 0) return -1;
        var e = Extras[n];
        e.Sprite = 97;
        e.Flags = ExtraTimeOut + ExtraExplo_;
        e.PosX = x; e.PosY = y; e.PosZ = z;
        e.HitForce = 0; e.Timer = TimerRef; e.TimeOut = 40;
        return n;
    }

    private int SearchBonusKey()
    {
        for (var n = 0; n < MaxExtras; n++) if (Extras[n].Sprite == 6) return n;
        return -1;
    }

    private int ExtraSearch(int owner, int x, int y, int z, int sprite, int numObj, int speed, int hitForce)
    {
        var n = FreeExtra();
        if (n < 0) return -1;
        var e = Extras[n];
        e.Sprite = sprite;
        e.Flags = ExtraSearchObj;
        e.Divers = 0;
        e.PosX = x; e.PosY = y; e.PosZ = z;
        e.TimeOut = owner;
        e.Timer = numObj;
        e.Vz = speed;
        e.HitForce = hitForce;
        e.Real.InitValue(0, speed, 50, TimerRef);
        e.Poids = Lba1Trig.GetAngle(x, z, Objects[numObj].PosX, Objects[numObj].PosZ);
        return n;
    }

    private int ExtraSearchKeyFor(int owner, int x, int y, int z, int sprite, int numExtra)
    {
        var n = FreeExtra();
        if (n < 0) return -1;
        var e = Extras[n];
        e.Sprite = sprite;
        e.Flags = ExtraSearchKey;
        e.Divers = 0;
        e.PosX = x; e.PosY = y; e.PosZ = z;
        e.TimeOut = owner;
        e.Timer = numExtra;
        e.Vz = 4000;
        e.HitForce = 0;
        e.Real.InitValue(0, 4000, 50, TimerRef);
        e.Poids = Lba1Trig.GetAngle(x, z, Extras[numExtra].PosX, Extras[numExtra].PosZ);
        return n;
    }

    private int ThrowExtra(int owner, int x, int y, int z, int sprite, int alpha, int beta, int vitesse, int poids, int hitForce)
    {
        var n = FreeExtra();
        if (n < 0) return -1;
        var e = Extras[n];
        e.Sprite = sprite;
        e.Flags = ExtraEndObj + ExtraEndCol + ExtraWaitNoCol + ExtraImpact;
        e.PosX = x; e.PosY = y; e.PosZ = z;
        InitFly(e, alpha, beta, vitesse, poids);
        e.HitForce = hitForce;
        e.TimeOut = owner;
        e.Timer = TimerRef;
        e.Divers = 0;
        return n;
    }

    // What an extra hits: the first actor (other than the owner) whose box it overlaps; a blow with force lands on it.
    private int ExtraCheckObjCol(Lba1Extra e, int owner)
    {
        var v = ExtraVolume(e.Sprite);
        int x0 = v.XMin + e.PosX, x1 = v.XMax + e.PosX, y0 = v.YMin + e.PosY, y1 = v.YMax + e.PosY, z0 = v.ZMin + e.PosZ, z1 = v.ZMax + e.PosZ;
        for (var n = 0; n < NbObjets; n++)
        {
            var t = Objects[n];
            if (t.Body == -1 || n == owner) continue;
            if (x0 < t.PosX + t.XMax && x1 > t.PosX + t.XMin && y0 < t.PosY + t.YMax && y1 > t.PosY + t.YMin && z0 < t.PosZ + t.ZMax && z1 > t.PosZ + t.ZMin)
            {
                if (e.HitForce != 0) HitObj(owner, n, e.HitForce, -1);
                return n;
            }
        }
        return -1;
    }

    private int ExtraCheckExtraCol(Lba1Extra e, int owner)
    {
        var v = ExtraVolume(e.Sprite);
        int x0 = v.XMin + e.PosX, x1 = v.XMax + e.PosX, y0 = v.YMin + e.PosY, y1 = v.YMax + e.PosY, z0 = v.ZMin + e.PosZ, z1 = v.ZMax + e.PosZ;
        for (var n = 0; n < MaxExtras; n++)
        {
            var t = Extras[n];
            if (t.Sprite == -1 || n == owner) continue;
            var tv = ExtraVolume(t.Sprite);
            if (x0 < tv.XMax + t.PosX && x1 > tv.XMin + t.PosX && y0 < tv.YMax + t.PosY && y1 > tv.YMin + t.PosY && z0 < tv.ZMax + t.PosZ && z1 > tv.ZMin + t.PosZ) return n;
        }
        return -1;
    }

    // Four samples along the path between two positions.
    private bool FullWorldColBrick(int oldx, int oldy, int oldz, int newx, int newy, int newz)
    {
        if (WorldColBrick(newx, newy, newz) != 0) return true;
        int x0 = (newx + oldx) / 2, y0 = (newy + oldy) / 2, z0 = (newz + oldz) / 2;
        if (WorldColBrick(x0, y0, z0) != 0) return true;
        if (WorldColBrick((newx + x0) / 2, (newy + y0) / 2, (newz + z0) / 2) != 0) return true;
        return WorldColBrick((x0 + oldx) / 2, (y0 + oldy) / 2, (z0 + oldz) / 2) != 0;
    }

    private static int CoulRetourBalle(Lba1Extra e) => e.Sprite == 42 ? 109 : e.Sprite == 43 ? 110 : 44;

    // A creature (or a chest, a zone) gives one of the bonuses its option flags allow.
    private void GiveExtraBonus(Lba1Object o)
    {
        var choices = new List<int>();
        for (var n = 0; n < 5; n++) if ((o.OptionFlags & (1 << (n + 4))) != 0) choices.Add(n);
        if (choices.Count == 0) return;
        var pick = choices[Rnd(choices.Count)];
        if (MagicLevel == 0 && pick == 2) pick = 1;    // no magic yet: a heart instead
        if ((o.WorkFlags & ObjDead) != 0)
        {
            ExtraBonus(o.PosX, o.PosY, o.PosZ, 256, 0, pick + 3, o.NbBonus);
            PlaySample(11, o.Number);
        }
        else
        {
            ExtraBonus(o.PosX, o.PosY + o.YMax, o.PosZ, 200, Lba1Trig.GetAngle(o.PosX, o.PosZ, Hero.PosX, Hero.PosZ), pick + 3, o.NbBonus);
            PlaySample(11, o.Number);
        }
    }

    private void ZoneGiveExtraBonus(SceneZoneModel z)
    {
        // (a zone's words: Info[0] is its number, then the engine's Info0 = which bonuses (Info[1]), Info1 = how many (Info[2]), Info2 = taken (Info[3]))
        if (z.Info[3] != 0) return;      // already taken
        var choices = new List<int>();
        for (var n = 0; n < 5; n++) if ((z.Info[1] & (1 << (n + 4))) != 0) choices.Add(n);
        if (choices.Count == 0) return;
        var pick = choices[Rnd(choices.Count)];
        if (MagicLevel == 0 && pick == 2) pick = 1;
        int x = (z.X0 + z.X1) / 2, zz = (z.Z0 + z.Z1) / 2;
        var p = ExtraBonus(x, z.Y1, zz, 180, Lba1Trig.GetAngle(x, zz, Hero.PosX, Hero.PosZ), pick + 3, z.Info[2]);
        if (p != -1)
        {
            Extras[p].Flags |= ExtraTimeIn;
            z.Info[3] = 1;               // marked as taken (this run only)
        }
    }

    // What the player sees flash up when picking something up: sprite and count.
    public event Action<int, int>? Picked;

    private void GereExtras()
    {
        for (var n = 0; n < MaxExtras; n++)
        {
            var e = Extras[n];
            if (e.Sprite == -1) continue;
            int oldx = 0, oldy = 0, oldz = 0;

            if ((e.Flags & ExtraTimeOut) != 0 && TimerRef >= e.Timer + e.TimeOut) { e.Sprite = -1; continue; }
            if ((e.Flags & ExtraOneFrame) != 0) { e.Sprite = -1; continue; }
            if ((e.Flags & ExtraExplo_) != 0) { e.Sprite = Lba1Trig.BoundRegleTrois(97, 100, 30, TimerRef - e.Timer); continue; }

            if ((e.Flags & ExtraFly) != 0)
            {
                var time = TimerRef - e.Timer;
                oldx = e.PosX; oldy = e.PosY; oldz = e.PosZ;
                e.PosX = (ushort)(e.Vx * time + e.OrgX);
                e.PosY = (ushort)(e.Vy * time + e.OrgY - e.Poids * time * time / 16);
                e.PosZ = (ushort)(e.Vz * time + e.OrgZ);
                if (e.PosX > SizeBrickXZ * 63 || e.PosZ > SizeBrickXZ * 63)
                {
                    if (n == MagicBall) MagicBall = ExtraSearch(-1, e.PosX, e.PosY, e.PosZ, CoulRetourBalle(e), 0, 10000, 0);
                    if ((e.Flags & ExtraTakable) != 0) e.Flags &= ~(ExtraFly + ExtraStopCol);
                    else e.Sprite = -1;
                    continue;
                }
            }

            if ((e.Flags & ExtraWaitSomeTime) != 0)
            {
                if (TimerRef - e.Timer > 40) e.Flags &= ~ExtraWaitSomeTime;
                continue;
            }

            if ((e.Flags & ExtraSearchObj) != 0)
            {
                int search = e.Timer, owner = e.TimeOut;
                var target = Objects[search];
                int tx = target.PosX, ty = target.PosY + 1000, tz = target.PosZ;
                var beta = Lba1Trig.GetAngle(e.PosX, e.PosZ, tx, tz);
                var angle = (beta - e.Poids) & 1023;
                if (angle < 600 && angle > 400)
                {
                    if (e.HitForce != 0) HitObj(owner, search, e.HitForce, -1);
                    if (n == MagicBall) MagicBall = -1;
                    e.Sprite = -1;
                    continue;
                }
                var alpha = Lba1Trig.GetAngle(e.PosY, 0, ty, Lba1Trig.Distance);
                var s = e.Real.GetValue(TimerRef);
                if (s == 0) s = 1;
                var (sx, sy) = Lba1Trig.Rotate(s, 0, alpha);
                e.PosY = (ushort)(e.PosY - sy);
                var (bx, bz) = Lba1Trig.Rotate(0, sx, beta);
                e.PosX = (ushort)(e.PosX + bx);
                e.PosZ = (ushort)(e.PosZ + bz);
                e.Real.InitValue(0, e.Vz, 50, TimerRef);
                if (ExtraCheckObjCol(e, owner) == search)
                {
                    if (n == MagicBall) MagicBall = -1;
                    e.Sprite = -1;
                    continue;
                }
            }

            if ((e.Flags & ExtraSearchKey) != 0)
            {
                int search = e.Timer;
                var target = Extras[search];
                var beta = Lba1Trig.GetAngle(e.PosX, e.PosZ, target.PosX, target.PosZ);
                var angle = (beta - e.Poids) & 1023;
                var found = angle < 600 && angle > 400;
                if (!found)
                {
                    var alpha = Lba1Trig.GetAngle(e.PosY, 0, target.PosY, Lba1Trig.Distance);
                    var s = e.Real.GetValue(TimerRef);
                    if (s == 0) s = 1;
                    var (sx, sy) = Lba1Trig.Rotate(s, 0, alpha);
                    e.PosY = (ushort)(e.PosY - sy);
                    var (bx, bz) = Lba1Trig.Rotate(0, sx, beta);
                    e.PosX = (ushort)(e.PosX + bx);
                    e.PosZ = (ushort)(e.PosZ + bz);
                    e.Real.InitValue(0, e.Vz, 50, TimerRef);
                    found = ExtraCheckExtraCol(e, MagicBall) == search;
                }
                if (found)
                {
                    PlaySample(97, 0);
                    NbLittleKeys += target.Divers;
                    Picked?.Invoke(6, target.Divers);
                    Log($"the magic ball fetches {target.Divers} key(s)");
                    target.Sprite = -1;
                    MagicBall = ExtraSearch(-1, e.PosX, e.PosY, e.PosZ, 6, 0, 8000, 0);
                    e.Sprite = -1;
                    continue;
                }
                if (target.Sprite == -1)      // the key was taken meanwhile
                {
                    MagicBall = ExtraSearch(-1, e.PosX, e.PosY, e.PosZ, CoulRetourBalle(e), 0, 8000, 0);
                    e.Sprite = -1;
                    continue;
                }
            }

            if ((e.Flags & ExtraEndObj) != 0 && ExtraCheckObjCol(e, e.TimeOut) != -1)
            {
                if (n == MagicBall) MagicBall = ExtraSearch(-1, e.PosX, e.PosY, e.PosZ, CoulRetourBalle(e), 0, 10000, 0);
                e.Sprite = -1;
                continue;
            }

            if ((e.Flags & ExtraEndCol) != 0)
            {
                var hitWall = false;
                if (FullWorldColBrick(oldx, oldy, oldz, e.PosX, e.PosY, e.PosZ)) { if ((e.Flags & ExtraWaitNoCol) == 0) hitWall = true; }
                else if ((e.Flags & ExtraWaitNoCol) != 0) e.Flags &= ~ExtraWaitNoCol;
                if (hitWall)
                {
                    if (n == MagicBall)
                    {
                        PlaySample(86, 0);
                        if (magicBallType == 0)
                        {
                            MagicBall = ExtraSearch(-1, e.PosX, e.PosY, e.PosZ, CoulRetourBalle(e), 0, 10000, 0);
                            e.Sprite = -1;
                            continue;
                        }
                        if (magicBallCount-- == 0)
                        {
                            MagicBall = ExtraSearch(-1, e.PosX, e.PosY, e.PosZ, CoulRetourBalle(e), 0, 10000, 0);
                            e.Sprite = -1;
                            continue;
                        }
                        BounceExtra(e, oldx, oldy, oldz);
                    }
                    else { e.Sprite = -1; continue; }
                }
            }

            if ((e.Flags & ExtraStopCol) != 0)
            {
                var landed = false;
                if (FullWorldColBrick(oldx, oldy, oldz, e.PosX, e.PosY, e.PosZ)) { if ((e.Flags & ExtraWaitNoCol) == 0) landed = true; }
                else if ((e.Flags & ExtraWaitNoCol) != 0) e.Flags &= ~ExtraWaitNoCol;
                if (landed)
                {
                    e.PosY = yMap * SizeBrickY + SizeBrickY - ExtraVolume(e.Sprite).YMin;
                    e.Flags &= ~(ExtraFly + ExtraStopCol);
                    continue;
                }
            }

            if ((e.Flags & ExtraTakable) != 0 && (e.Flags & ExtraFly) == 0 && ExtraCheckObjCol(e, -1) == 0)
            {
                PlaySample(97, 0);
                Picked?.Invoke(e.Sprite, e.Divers);
                switch (e.Sprite)
                {
                    case 3: NbGoldPieces = Math.Min(999, NbGoldPieces + e.Divers); Log($"Twinsen picks up {e.Divers} kashes"); break;
                    case 4: Hero.LifePoint = Math.Min(50, Hero.LifePoint + e.Divers); Log("Twinsen picks up a heart"); break;
                    case 5: if (MagicLevel > 0) MagicPoint = Math.Min(MagicLevel * 20, MagicPoint + e.Divers * 2); Log("Twinsen picks up magic"); break;
                    case 6: NbLittleKeys += e.Divers; Log($"Twinsen picks up {e.Divers} key(s)"); break;
                    case 7: NbFourLeafClover = Math.Min(NbCloverBox, NbFourLeafClover + e.Divers); Log("Twinsen picks up a clover leaf"); break;
                }
                e.Sprite = -1;
            }
        }
    }

    // FICHE.C ThrowMagicBall: what the ball is depends on the magic level, how it flies on the magic points left.
    private void ThrowMagicBall(int x, int y, int z, int alpha, int beta, int vitesse, int poids)
    {
        int sprite, force;
        switch (MagicLevel)
        {
            case 2: sprite = 42; force = 6; break;
            case 3: sprite = 43; force = 8; break;
            case 4: sprite = 13; force = 10; break;
            default: sprite = 1; force = 4; break;
        }
        magicBallType = (MagicPoint - 1) / 20 + 1;
        if (MagicPoint == 0) magicBallType = 0;
        if (SearchBonusKey() != -1) magicBallType = 5;

        switch (magicBallType)
        {
            case 0:
                MagicBall = ThrowExtra(0, x, y, z, sprite, alpha, beta, vitesse, poids, force);
                break;
            case 1:
                magicBallCount = 4;
                MagicBall = ThrowExtra(0, x, y, z, sprite, alpha, beta, vitesse, poids, force);
                break;
            case 2:
            case 3:
            case 4:
                magicBallType = 1;
                magicBallCount = 4;
                MagicBall = ThrowExtra(0, x, y, z, sprite, alpha, beta, vitesse, poids, force);
                break;
            case 5:
                MagicBall = ExtraSearchKeyFor(0, x, y, z, sprite, SearchBonusKey());
                break;
        }
        if (MagicPoint > 0) MagicPoint--;
        Log("Twinsen throws the magic ball");
    }
}
