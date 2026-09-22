using static LBAAssembler.Lba1.Runtime.Lba1Const;

namespace LBAAssembler.Lba1.Runtime;

// OBJECT.C: collisions, DoAnim (movement), DoDir (what an actor wants to do), CheckZoneSce; GRILLE_A.C: the brick lookups.
internal sealed partial class Lba1Runtime
{
    // OBJECT.C globals shared by these routines
    private int nxw, nyw, nzw;
    private int saveNxw, saveNyw, saveNzw;
    private int oldX, oldY, oldZ;
    private int col1;
    private int xMap, yMap, zMap;
    private Lba1Object aPtObj = null!;
    private int animNumObj;

    private int rngState = 12345;
    private int Rand() { rngState = unchecked(rngState * 1103515245 + 12345); return (rngState >> 16) & 0x7FFF; }
    private int Rnd(int n) => n <= 0 ? 0 : Rand() % n;

    // ---------------------------------------------------------------------------------------------- GRILLE_A.C

    private int CellShape(int xm, int ym, int zm)
    {
        var (block, second) = Cube.Cell(xm, ym, zm);
        return Cube.ShapeOf(block, second);
    }

    // WorldColBrick: the collision shape of the map cell at a world position (0 free, 1 solid, 2..13 ramps).
    private int WorldColBrick(int xw, int yw, int zw)
    {
        int xm = (xw + DemiBrickXZ) >> 9, ym = yw >> 8, zm = (zw + DemiBrickXZ) >> 9;
        xMap = xm; yMap = ym; zMap = zm;
        if (xm < 0 || xm >= 64 || zm < 0 || zm >= 64) return 0;
        if (ym <= -1) return 1;
        if (ym < 0 || ym > 24) return 0;
        return CellShape(xm, ym, zm);
    }

    // WorldColBrickFull: like WorldColBrick, but a body `ymax` tall is solid as soon as any cell above the feet is occupied.
    private int WorldColBrickFull(int xw, int yw, int zw, int ymax)
    {
        int xm = (xw + DemiBrickXZ) >> 9, ym = yw >> 8, zm = (zw + DemiBrickXZ) >> 9;
        xMap = xm; yMap = ym; zMap = zm;
        if (xm < 0 || xm >= 64 || zm < 0 || zm >= 64) return 0;
        if (ym <= -1) return 1;

        // the engine reads the cell buffer linearly; a layer above 24 runs into the next column
        int Index(int y) => ((zm * 64 + xm) * 25 + y) * 2;
        bool Occupied(int y)
        {
            var i = Index(y);
            return i >= 0 && i + 1 < Cube.Cells.Length && (Cube.Cells[i] != 0 || Cube.Cells[i + 1] != 0);
        }
        int Result()
        {
            var i = Index(ym);
            if (i < 0 || i + 1 >= Cube.Cells.Length) return 0;
            var block = Cube.Cells[i];
            return block != 0 ? Cube.ShapeOf(block, Cube.Cells[i + 1]) : Cube.Cells[i + 1];
        }
        var result = Result();
        var heightCheck = (ymax + 255) >> 8;
        var y2 = ym;
        while (y2 < 24 && heightCheck > 0)
        {
            y2++;
            if (Occupied(y2)) return 1;
            heightCheck--;
        }
        return result;
    }

    // WorldCodeBrick: the game code (water, sound ...) of the block under a position; 0xF0 = nothing.
    private int WorldCodeBrick(int xw, int yw, int zw)
    {
        int xm = (xw + DemiBrickXZ) >> 9;
        xMap = xm;
        if (yw <= -1) return 0xF0;
        int ym = yw >> 8;
        yMap = ym;
        int zm = (zw + DemiBrickXZ) >> 9;
        zMap = zm;
        if (xm < 0 || xm >= 64 || zm < 0 || zm >= 64 || ym < 0 || ym > 24) return 0xF0;
        var (block, _) = Cube.Cell(xm, ym, zm);
        return block != 0 ? Cube.GameCodeOf(block) : 0xF0;
    }

    // ---------------------------------------------------------------------------------------------- OBJECT.C: collisions

    private void ReajustPos(int col)
    {
        if (col == 0) return;
        var xw = xMap * SizeBrickXZ - DemiBrickXZ;
        var yw = yMap * SizeBrickY;
        var zw = zMap * SizeBrickXZ - DemiBrickXZ;

        switch (col)
        {
            case 6: col = nxw - xw < nzw - zw ? 3 : 2; break;
            case 7: col = nxw - xw < nzw - zw ? 5 : 4; break;
            case 10: col = nxw - xw < nzw - zw ? 2 : 3; break;
            case 11: col = nxw - xw < nzw - zw ? 4 : 5; break;
            case 8: col = SizeBrickXZ - (nxw - xw) > nzw - zw ? 4 : 2; break;
            case 9: col = SizeBrickXZ - (nxw - xw) > nzw - zw ? 5 : 3; break;
            case 12: col = SizeBrickXZ - (nxw - xw) > nzw - zw ? 2 : 4; break;
            case 13: col = SizeBrickXZ - (nxw - xw) > nzw - zw ? 3 : 5; break;
        }

        switch (col)
        {
            case 2: nyw = yw + Lba1Trig.BoundRegleTrois(0, SizeBrickY, SizeBrickXZ, nxw - xw); break;
            case 3: nyw = yw + Lba1Trig.BoundRegleTrois(0, SizeBrickY, SizeBrickXZ, nzw - zw); break;
            case 4: nyw = yw + Lba1Trig.BoundRegleTrois(SizeBrickY, 0, SizeBrickXZ, nzw - zw); break;
            case 5: nyw = yw + Lba1Trig.BoundRegleTrois(SizeBrickY, 0, SizeBrickXZ, nxw - xw); break;
        }
    }

    private void ReceptionObj()
    {
        if (animNumObj == 0)
        {
            if (startYFalling - nyw >= 16 * SizeBrickY)
            {
                aPtObj.LifePoint = 0;
                InitAnim(GenAnimReception2, AnimAllThen, GenAnimRien, animNumObj);
            }
            else if (startYFalling - nyw >= 8 * SizeBrickY)
            {
                aPtObj.LifePoint--;
                InitAnim(GenAnimReception2, AnimAllThen, GenAnimRien, animNumObj);
            }
            else if (startYFalling - nyw > 1) InitAnim(GenAnimReception, AnimAllThen, GenAnimRien, animNumObj);
            else InitAnim(GenAnimRien, AnimRepeat, 0, animNumObj);
            startYFalling = 0;
        }
        else InitAnim(GenAnimReception, AnimAllThen, aPtObj.NextGenAnim, animNumObj);
        aPtObj.WorkFlags &= ~Falling;
    }

    private void DoCornerReajust(int nx, int ny, int nz, int coin)
    {
        var orgCol = WorldColBrick(nxw, nyw, nzw);
        nxw += nx; nyw += ny; nzw += nz;
        if (nxw < 0 || nzw < 0 || nxw > 63 * SizeBrickXZ || nzw > 63 * SizeBrickXZ) { RestoreCorner(); return; }

        ReajustPos(orgCol);
        var col = WorldColBrick(nxw, nyw, nzw);
        if (col != 0 && col == 1)
        {
            col1 |= coin;
            if (WorldColBrick(nxw, nyw, oldZ + nz) == 1)
            {
                if (WorldColBrick(oldX + nx, nyw, nzw) != 1) saveNxw = oldX;
            }
            else saveNzw = oldZ;
        }
        RestoreCorner();
    }

    private void DoCornerReajustTwinkel(int nx, int ny, int nz, int coin)
    {
        var orgCol = WorldColBrick(nxw, nyw, nzw);
        nxw += nx; nyw += ny; nzw += nz;
        if (nxw < 0 || nzw < 0 || nxw > 63 * SizeBrickXZ || nzw > 63 * SizeBrickXZ) { RestoreCorner(); return; }

        ReajustPos(orgCol);
        var col = WorldColBrickFull(nxw, nyw, nzw, aPtObj.YMax);
        if (col != 0 && col == 1)
        {
            col1 |= coin;
            if (WorldColBrickFull(nxw, nyw, oldZ + nz, aPtObj.YMax) == 1)
            {
                if (WorldColBrickFull(oldX + nx, nyw, nzw, aPtObj.YMax) != 1) saveNxw = oldX;
            }
            else saveNzw = oldZ;
        }
        RestoreCorner();
    }

    private void RestoreCorner()
    {
        nxw = saveNxw; nyw = saveNyw; nzw = saveNzw;
    }

    private bool CheckZvOnZv(int numObj, int numObjT)
    {
        var o = Objects[numObj];
        var t = Objects[numObjT];
        int x0 = nxw + o.XMin, x1 = nxw + o.XMax, y0 = nyw + o.YMin, y1 = nyw + o.YMax, z0 = nzw + o.ZMin, z1 = nzw + o.ZMax;
        int xt0 = t.PosX + t.XMin, xt1 = t.PosX + t.XMax, yt0 = t.PosY + t.YMin, yt1 = t.PosY + t.YMax, zt0 = t.PosZ + t.ZMin, zt1 = t.PosZ + t.ZMax;
        return x0 < xt1 && x1 > xt0 && y0 <= yt1 + 1 && y0 > yt1 - SizeBrickY && y1 > yt0 && z0 < zt1 && z1 > zt0;
    }

    private void CheckCarrier(int numObj)
    {
        if ((Objects[numObj].Flags & ObjCarrier) == 0) return;
        for (var n = 0; n < NbObjets; n++)
            if (Objects[n].CarryBy == numObj) Objects[n].CarryBy = -1;
    }

    // HitObj: numHitter strikes actor `num` with `hitForce` (a simplification: the reaction animations run, the
    // stars and sounds don't).
    private void HitObj(int numHitter, int num, int hitForce, int beta)
    {
        var t = Objects[num];
        if (t.LifePoint <= 0) return;
        t.HitBy = numHitter;
        if (t.Armure <= hitForce)
        {
            if (t.GenAnim == GenAnimChoc || t.GenAnim == GenAnimChoc2)
            {
                // already reeling: the blow's sound (the animation's actions at frame 1) plays again
                var memo = t.Frame;
                t.Frame = 1;
                if (t.AnimActions is not null) GereAnimAction(t, num);
                t.Frame = memo;
            }
            else
            {
                if (beta != -1) t.RealAngle.InitAngle(beta, beta, 0, TimerRef);
                InitAnim((Rand() & 1) == 0 ? GenAnimChoc : GenAnimChoc2, AnimInsert, NoAnim, num);
            }
            if (num == 0) lastJoyFlag = true;
            t.LifePoint -= hitForce;
            if (t.LifePoint < 0) t.LifePoint = 0;
        }
        else InitAnim(GenAnimEncaisse, AnimInsert, NoAnim, num);
    }

    private int CheckObjectCollisions(int numObj)
    {
        var o = Objects[numObj];
        int x0 = nxw + o.XMin, x1 = nxw + o.XMax, y0 = nyw + o.YMin, y1 = nyw + o.YMax, z0 = nzw + o.ZMin, z1 = nzw + o.ZMax;
        o.ObjCol = -1;

        for (var n = 0; n < NbObjets; n++)
        {
            var t = Objects[n];
            if (n == numObj || t.Body == -1 || (o.Flags & Invisible) != 0 || t.CarryBy == numObj) continue;

            int xt0 = t.PosX + t.XMin, xt1 = t.PosX + t.XMax, yt0 = t.PosY + t.YMin, yt1 = t.PosY + t.YMax, zt0 = t.PosZ + t.ZMin, zt1 = t.PosZ + t.ZMax;
            if (!(x0 < xt1 && x1 > xt0 && y0 < yt1 && y1 > yt0 && z0 < zt1 && z1 > zt0)) continue;

            o.ObjCol = n;
            if ((t.Flags & ObjCarrier) != 0)
            {
                if ((o.WorkFlags & Falling) != 0)
                {
                    nyw = yt1 - o.YMin + 1;
                    o.CarryBy = n;
                    continue;
                }
                if (CheckZvOnZv(numObj, n))
                {
                    nyw = yt1 - o.YMin + 1;
                    o.CarryBy = n;
                    continue;
                }
            }
            else if (CheckZvOnZv(numObj, n)) HitObj(numObj, n, 1, -1);

            var angle = Lba1Trig.GetAngle(nxw, nzw, t.PosX, t.PosZ);

            if ((t.Flags & Pushable) != 0 && (o.Flags & Pushable) == 0)
            {
                t.AnimStepY = 0;
                if ((t.Flags & MiniZv) != 0)
                {
                    if (angle >= 128 && angle < 384 && o.Beta >= 128 && o.Beta < 384) t.AnimStepX = SizeBrickXZ / 4 + SizeBrickXZ / 8;
                    if (angle >= 384 && angle < 640 && o.Beta >= 384 && o.Beta < 640) t.AnimStepZ = -SizeBrickXZ / 4 + SizeBrickXZ / 8;
                    if (angle >= 640 && angle < 896 && o.Beta >= 640 && o.Beta < 896) t.AnimStepX = -SizeBrickXZ / 4 + SizeBrickXZ / 8;
                    if ((angle >= 896 || angle < 128) && (o.Beta >= 896 || o.Beta < 128)) t.AnimStepZ = SizeBrickXZ / 4 + SizeBrickXZ / 8;
                }
                else
                {
                    t.AnimStepX = nxw - o.OldPosX;
                    t.AnimStepZ = nzw - o.OldPosZ;
                }
            }

            if (t.XMax - t.XMin == t.ZMax - t.ZMin && o.XMax - o.XMin == o.ZMax - o.ZMin)
            {
                // both boxes are square: slide along the edge that was hit
                if (angle >= 128 && angle < 384) nxw = xt0 - o.XMax;
                if (angle >= 384 && angle < 640) nzw = zt1 - o.ZMin;
                if (angle >= 640 && angle < 896) nxw = xt1 - o.XMin;
                if (angle >= 896 || angle < 128) nzw = zt0 - o.ZMax;
            }
            else if ((o.WorkFlags & Falling) == 0)
            {
                nxw = oldX; nyw = oldY; nzw = oldZ;
            }
        }

        if ((o.WorkFlags & OkHit) != 0)
        {
            var (rx, rz) = Lba1Trig.Rotate(0, 200, o.Beta);
            x0 = nxw + o.XMin + rx; x1 = nxw + o.XMax + rx;
            y0 = nyw + o.YMin; y1 = nyw + o.YMax;
            z0 = nzw + o.ZMin + rz; z1 = nzw + o.ZMax + rz;
            for (var n = 0; n < NbObjets; n++)
            {
                var t = Objects[n];
                if (n == numObj || t.Body == -1 || (o.Flags & Invisible) != 0 || t.CarryBy == numObj) continue;
                int xt0 = t.PosX + t.XMin, xt1 = t.PosX + t.XMax, yt0 = t.PosY + t.YMin, yt1 = t.PosY + t.YMax, zt0 = t.PosZ + t.ZMin, zt1 = t.PosZ + t.ZMax;
                if (x0 < xt1 && x1 > xt0 && y0 < yt1 && y1 > yt0 && z0 < zt1 && z1 > zt0)
                {
                    HitObj(numObj, n, o.HitForce, o.Beta + 512);
                    o.WorkFlags &= ~OkHit;
                }
            }
        }
        return o.ObjCol;
    }

    // ---------------------------------------------------------------------------------------------- OBJECT.C: DoAnim

    private void ClearRealAngle(Lba1Object o) => o.RealAngle.InitAngle(o.Beta, o.Beta, 0, TimerRef);

    // What SetInterDepObjet computes for the current frame: how far the animation has moved the actor and turned him
    // since the frame began (AnimStepX/Y/Z, AnimStepBeta), and whether the frame is complete.
    private bool SetInterDepObjet(Lba1Object o, out int stepX, out int stepY, out int stepZ, out int stepBeta, out int masterRot)
    {
        stepX = stepY = stepZ = stepBeta = masterRot = 0;
        var animation = o.Animation!;
        var frame = animation.Frames[Math.Min(o.Frame, animation.Frames.Count - 1)];
        var root = frame.Bones[0];
        var time = frame.Length;
        var elapsed = TimerRef - o.AnimMemoTicks;

        masterRot = (short)root.Type;
        if ((uint)elapsed >= (uint)time)
        {
            stepX = (short)frame.StepX; stepY = (short)frame.StepY; stepZ = (short)frame.StepZ;
            stepBeta = (short)root.Y;
            o.AnimMemoTicks = TimerRef;
            return true;
        }
        var t = (short)time;
        var e = (short)elapsed;
        stepBeta = (short)((short)root.Y * e / t);
        stepX = (short)((short)frame.StepX * e / t);
        stepY = (short)((short)frame.StepY * e / t);
        stepZ = (short)((short)frame.StepZ * e / t);
        return false;
    }

    // The bones' pose right now, as SetInterAnimObjet blends it: from the key frame last reached towards the current one,
    // in proportion to the ticks elapsed over the frame's length. Null when the actor has no animation.
    public (int Type, double X, double Y, double Z)[]? CurrentPose(Lba1Object o)
    {
        var animation = o.Animation;
        if (animation is null || o.IsSprite) return null;
        var frame = animation.Frames[Math.Min(o.Frame, animation.Frames.Count - 1)];
        var from = o.PoseFrom;
        var t = from is null ? 1.0 : Math.Clamp((double)(TimerRef - o.AnimMemoTicks) / Math.Max(1, frame.Length), 0, 1);
        var result = new (int, double, double, double)[frame.Bones.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var y = frame.Bones[i];
            var x = from is not null && i < from.Count ? from[i] : y;
            if (y.Type != 0)
            {
                result[i] = (y.Type, x.X + (y.X - x.X) * t, x.Y + (y.Y - x.Y) * t, x.Z + (y.Z - x.Z) * t);
                continue;
            }
            static double Angle(int p, int q, double t)
            {
                p &= 1023; q &= 1023;
                var delta = q - p;
                if (delta < -512) delta += 1024; else if (delta > 512) delta -= 1024;
                return p + delta * t;
            }
            result[i] = (0, Angle(x.X, y.X, t), Angle(x.Y, y.Y, t), Angle(x.Z, y.Z, t));
        }
        return result;
    }

    private void DoAnim(int numObj)
    {
        var o = aPtObj = Objects[animNumObj = numObj];
        if (o.Body == -1) return;

        oldX = o.OldPosX; oldY = o.OldPosY; oldZ = o.OldPosZ;

        if ((o.Flags & Sprite3D) != 0)
        {
            // ---- sprites (doors, keys, boxes ...) ----
            if (o.HitForce != 0) o.WorkFlags |= OkHit;

            nxw = o.PosX; nyw = o.PosY; nzw = o.PosZ;

            if ((o.WorkFlags & Falling) == 0)
            {
                if (o.SRot != 0)
                {
                    var n = o.RealAngle.GetValue(TimerRef);
                    if (n == 0) n = o.RealAngle.End > 0 ? 1 : -1;

                    var (x0, y0) = Lba1Trig.Rotate(n, 0, o.FlagAnim);   // alpha
                    nyw = o.PosY - y0;
                    (x0, y0) = Lba1Trig.Rotate(0, x0, o.Beta);
                    nxw = o.PosX + x0;
                    nzw = o.PosZ + y0;

                    o.RealAngle.InitValue(0, o.SRot, 50, TimerRef);

                    if ((o.WorkFlags & AutoStopDoor) != 0)
                    {
                        if (o.DoorWidth != 0)
                        {
                            // maximum opening
                            if (Lba1Trig.Distance2D(nxw, nzw, o.AnimStepX, o.AnimStepZ) >= o.DoorWidth)
                            {
                                switch (o.Beta)
                                {
                                    case 768: nxw = o.AnimStepX - o.DoorWidth; break;
                                    case 256: nxw = o.AnimStepX + o.DoorWidth; break;
                                    case 512: nzw = o.AnimStepZ - o.DoorWidth; break;
                                    case 0: nzw = o.AnimStepZ + o.DoorWidth; break;
                                }
                                o.WorkFlags &= ~AutoStopDoor;
                                o.SRot = 0;
                            }
                        }
                        else
                        {
                            // closing
                            var closed = o.Beta switch
                            {
                                768 => nxw >= o.AnimStepX,
                                256 => nxw <= o.AnimStepX,
                                512 => nzw >= o.AnimStepZ,
                                0 => nzw <= o.AnimStepZ,
                                _ => false,
                            };
                            if (closed)
                            {
                                nxw = o.AnimStepX; nyw = o.AnimStepY; nzw = o.AnimStepZ;
                                o.WorkFlags &= ~AutoStopDoor;
                                o.SRot = 0;
                            }
                        }
                    }
                }

                if ((o.Flags & Pushable) != 0)
                {
                    nxw += o.AnimStepX; nyw += o.AnimStepY; nzw += o.AnimStepZ;
                    if ((o.Flags & MiniZv) != 0)
                    {
                        nxw = nxw / (SizeBrickXZ / 4) * (SizeBrickXZ / 4);
                        nzw = nzw / (SizeBrickXZ / 4) * (SizeBrickXZ / 4);
                    }
                    o.AnimStepX = o.AnimStepY = o.AnimStepZ = 0;
                }
            }
        }
        else if (o.Anim != -1 && o.Animation is not null)
        {
            // ---- 3D actors: the animation moves and turns them ----
            var flag = SetInterDepObjet(o, out var stepX, out var stepY, out var stepZ, out var stepBeta, out var masterRot);
            if (masterRot != 0) o.WorkFlags |= AnimMasterRot; else o.WorkFlags &= ~AnimMasterRot;
            o.Beta = (o.Beta + stepBeta - o.AnimStepBeta) & 1023;
            o.AnimStepBeta = stepBeta;

            var (rx, rz) = Lba1Trig.Rotate(stepX, stepZ, o.Beta);
            stepX = (short)rx; stepZ = (short)rz;

            nxw = o.PosX + stepX - o.AnimStepX;
            nyw = o.PosY + stepY - o.AnimStepY;
            nzw = o.PosZ + stepZ - o.AnimStepZ;

            o.AnimStepX = stepX; o.AnimStepY = stepY; o.AnimStepZ = stepZ;

            o.WorkFlags &= ~(AnimEnd + NewFrame);
            if (flag)
            {
                o.PoseFrom = o.Animation.Frames[Math.Min(o.Frame, o.Animation.Frames.Count - 1)].Bones;
                o.Frame++;
                o.WorkFlags |= NewFrame;
                if (o.AnimActions is not null) GereAnimAction(o, numObj);

                if (o.Frame == o.Animation.Frames.Count)
                {
                    o.WorkFlags &= ~OkHit;
                    if (o.FlagAnim == AnimRepeat) o.Frame = o.Animation.LoopFrame;
                    else
                    {
                        o.GenAnim = o.NextGenAnim;
                        var next = SearchAnim(o.GenAnim, numObj);
                        if (next is null)
                        {
                            next = SearchAnim(GenAnimRien, numObj);
                            o.GenAnim = GenAnimRien;
                        }
                        o.Anim = next?.Hqr ?? -1;
                        o.Animation = o.Anim >= 0 ? Data.Animation(o.Anim) : null;
                        o.AnimActions = next?.Actions;
                        o.FlagAnim = AnimRepeat;
                        o.Frame = 0;
                        o.HitForce = 0;
                    }
                    if (o.AnimActions is not null) GereAnimAction(o, numObj);
                    o.WorkFlags |= AnimEnd;
                }
                o.AnimStepBeta = 0; o.AnimStepX = 0; o.AnimStepY = 0; o.AnimStepZ = 0;
            }
        }

        // ---- carried by a moving platform / by somebody ----
        if (o.CarryBy != -1)
        {
            var c = Objects[o.CarryBy];
            nxw -= c.OldPosX; nyw -= c.OldPosY; nzw -= c.OldPosZ;
            nxw += c.PosX; nyw += c.PosY; nzw += c.PosZ;
            if (!CheckZvOnZv(numObj, o.CarryBy)) o.CarryBy = -1;
        }

        // ---- falling ----
        if ((o.WorkFlags & Falling) != 0)
        {
            nxw = oldX;
            nyw = oldY + stepFalling;
            nzw = oldZ;
        }

        // ---- collisions with the map and the other actors ----
        if ((o.Flags & CheckBrickCol) != 0)
        {
            yMap = 0;
            int col;
            if ((col = WorldColBrick(oldX, oldY, oldZ)) != 0)
            {
                if (col == 1)
                {
                    nyw = nyw / SizeBrickY * SizeBrickY + SizeBrickY;
                    o.PosY = nyw;
                }
                else ReajustPos(col);
            }

            if ((o.Flags & CheckObjCol) != 0) CheckObjectCollisions(numObj);

            if (o.CarryBy != -1 && (o.WorkFlags & Falling) != 0) ReceptionObj();

            saveNxw = nxw; saveNyw = nyw; saveNzw = nzw;
            col1 = 0;

            if (numObj == 0 && (o.Flags & ColBasse) == 0)
            {
                DoCornerReajustTwinkel(o.XMin, o.YMin, o.ZMin, 1);
                DoCornerReajustTwinkel(o.XMax, o.YMin, o.ZMin, 2);
                DoCornerReajustTwinkel(o.XMax, o.YMin, o.ZMax, 4);
                DoCornerReajustTwinkel(o.XMin, o.YMin, o.ZMax, 8);
            }
            else
            {
                DoCornerReajust(o.XMin, o.YMin, o.ZMin, 1);
                DoCornerReajust(o.XMax, o.YMin, o.ZMin, 2);
                DoCornerReajust(o.XMax, o.YMin, o.ZMax, 4);
                DoCornerReajust(o.XMin, o.YMin, o.ZMax, 8);
            }

            // hitting a wall at a run hurts the athletic behaviour
            if (col1 != 0 && (o.WorkFlags & Falling) == 0 && animNumObj == 0 && Comportement == CSportif && o.GenAnim == GenAnimMarche)
            {
                var (cx, cz) = Lba1Trig.Rotate(o.XMin, o.ZMin, o.Beta + 896 + 512);
                cx += nxw; cz += nzw;
                if (cx >= 0 && cz >= 0 && cx <= 63 * SizeBrickXZ && cz <= 63 * SizeBrickXZ && WorldColBrick(cx, nyw + 256, cz) != 0)
                {
                    InitAnim(GenAnimChoc, AnimAllThen, GenAnimRien, animNumObj);
                    lastJoyFlag = true;
                    o.LifePoint -= 1;
                }
            }

            col = WorldColBrick(nxw, nyw, nzw);
            o.Col = col;
            if (col != 0)
            {
                if (col == 1)
                {
                    if ((o.WorkFlags & Falling) != 0)
                    {
                        ReceptionObj();
                        nyw = yMap * SizeBrickY + SizeBrickY;
                    }
                    else
                    {
                        if (numObj == 0 && Comportement == CSportif && o.GenAnim == GenAnimMarche)
                        {
                            InitAnim(GenAnimChoc, AnimAllThen, GenAnimRien, animNumObj);
                            lastJoyFlag = true;
                            o.LifePoint -= 1;
                        }
                        // slide: try the move along x alone, then along z alone
                        if (WorldColBrick(nxw, nyw, oldZ) != 0)
                        {
                            if (WorldColBrick(oldX, nyw, nzw) != 0) return;   // the move isn't accepted
                            nxw = oldX;
                        }
                        else nzw = oldZ;
                    }
                }
                else
                {
                    if ((o.WorkFlags & Falling) != 0) ReceptionObj();
                    ReajustPos(col);
                }
                o.WorkFlags &= ~Falling;
            }
            else
            {
                // nothing under the feet: fall, or settle on a ramp below
                if ((o.Flags & ObjFallable) != 0 && o.CarryBy == -1)
                {
                    col = WorldColBrick(nxw, nyw - 1, nzw);
                    if (col != 0)
                    {
                        if ((o.WorkFlags & Falling) != 0) ReceptionObj();
                        ReajustPos(col);
                    }
                    else if ((o.WorkFlags & AnimMasterRot) == 0)
                    {
                        o.WorkFlags |= Falling;
                        if (numObj == 0 && startYFalling == 0) startYFalling = nyw;
                        InitAnim(GenAnimTombe, AnimRepeat, NoAnim, numObj);
                    }
                }
            }

            if (yMap == -1) o.LifePoint = 0;
        }
        else if ((o.Flags & CheckObjCol) != 0) CheckObjectCollisions(numObj);

        if (col1 != 0) o.Col |= 128;

        // stay inside the cube
        if (nxw < 0) nxw = 0;
        if (nzw < 0) nzw = 0;
        if (nyw < 0) nyw = 0;
        if (nxw > 63 * SizeBrickXZ) nxw = 63 * SizeBrickXZ;
        if (nzw > 63 * SizeBrickXZ) nzw = 63 * SizeBrickXZ;

        o.PosX = nxw; o.PosY = nyw; o.PosZ = nzw;
    }

    // ---------------------------------------------------------------------------------------------- OBJECT.C: DoDir

    private void ManualRealAngle(Lba1Object o)
    {
        var angle = 0;
        if ((myJoy & JLeft) != 0) angle = +256;
        if ((myJoy & JRight) != 0) angle = -256;
        o.RealAngle.InitAngleConst(o.Beta, o.Beta + angle, o.SRot, TimerRef);
    }

    // A 32-bit timer kept in an actor's Info / Info1 (MOVE_RANDOM)
    private static int InfoTimer(Lba1Object o) => (ushort)o.Info | (o.Info1 << 16);
    private static void SetInfoTimer(Lba1Object o, int value) { o.Info = (short)value; o.Info1 = (short)(value >> 16); }

    private void DoDir(int numObj)
    {
        var o = Objects[numObj];
        if (o.Body == -1) return;

        if ((o.WorkFlags & Falling) != 0)
        {
            if (o.Move == MoveManual)
            {
                ManualRealAngle(o);
                lastMyJoy = myJoy;
            }
            return;
        }

        if ((o.Flags & Sprite3D) == 0 && o.Move != MoveManual) o.Beta = o.RealAngle.GetAngle(TimerRef);

        switch (o.Move)
        {
            case MoveManual:
                if (numObj == 0)
                {
                    actionNormal = false;
                    switch (Comportement)
                    {
                        case CNormal:
                            if ((myFire & FSpace) != 0) actionNormal = true;
                            break;
                        case CSportif:
                            if ((myFire & FSpace) != 0) InitAnim(GenAnimSaute, AnimThen, GenAnimRien, numObj);
                            break;
                        case CAgressif:
                            if ((myFire & FSpace) != 0)
                            {
                                if ((myJoy & JRight) != 0) InitAnim(GenAnimCoup2, AnimThen, GenAnimRien, numObj);
                                if ((myJoy & JLeft) != 0) InitAnim(GenAnimCoup3, AnimThen, GenAnimRien, numObj);
                                if ((myJoy & JUp) != 0) InitAnim(GenAnimCoup1, AnimThen, GenAnimRien, numObj);
                            }
                            break;
                        case CDiscret:
                            if ((myFire & FSpace) != 0) InitAnim(GenAnimCache, AnimRepeat, NoAnim, numObj);
                            break;
                    }
                    // Alt: throw the magic ball, or draw the sword
                    if ((myFire & FAlt) != 0 && FlagGame[FlagConsigne] == 0)
                    {
                        if (Weapon == 0)
                        {
                            if (FlagGame[FlagBalleMagique] == 1)
                            {
                                if (MagicBall == -1) InitAnim(GenAnimLance, AnimThen, GenAnimRien, numObj);
                                lastJoyFlag = true;
                                o.Beta = o.RealAngle.GetAngle(TimerRef);
                            }
                        }
                        else if (FlagGame[FlagSabreMagique] == 1)
                        {
                            if (o.GenBody != GenBodySabre) InitBody(GenBodySabre, 0);
                            InitAnim(GenAnimSabre, AnimThen, GenAnimRien, numObj);
                            lastJoyFlag = true;
                            o.Beta = o.RealAngle.GetAngle(TimerRef);
                        }
                    }
                }

                if (myFire == 0 || actionNormal)
                {
                    if ((myJoy & (JUp + JDown)) != 0) lastJoyFlag = false;
                    if ((myJoy != lastMyJoy || myFire != lastMyFire) && lastJoyFlag)
                        InitAnim(GenAnimRien, AnimRepeat, NoAnim, numObj);
                    lastJoyFlag = false;

                    if ((myJoy & JUp) != 0)
                    {
                        if (!flagClimbing) InitAnim(GenAnimMarche, AnimRepeat, NoAnim, numObj);
                        lastJoyFlag = true;
                    }
                    if ((myJoy & JDown) != 0)
                    {
                        InitAnim(GenAnimRecule, AnimRepeat, NoAnim, numObj);
                        lastJoyFlag = true;
                    }
                    if ((myJoy & JLeft) != 0)
                    {
                        lastJoyFlag = true;
                        if (o.GenAnim == GenAnimRien) InitAnim(GenAnimGauche, AnimRepeat, NoAnim, numObj);
                        else if ((o.WorkFlags & AnimMasterRot) == 0) o.Beta = o.RealAngle.GetAngle(TimerRef);
                    }
                    if ((myJoy & JRight) != 0)
                    {
                        lastJoyFlag = true;
                        if (o.GenAnim == GenAnimRien) InitAnim(GenAnimDroite, AnimRepeat, NoAnim, numObj);
                        else if ((o.WorkFlags & AnimMasterRot) == 0) o.Beta = o.RealAngle.GetAngle(TimerRef);
                    }
                }

                ManualRealAngle(o);
                lastMyJoy = myJoy;
                lastMyFire = myFire;
                break;

            case MoveFollow:
                {
                    var target = Objects[o.Info3];
                    var angle = Lba1Trig.GetAngle(o.PosX, o.PosZ, target.PosX, target.PosZ);
                    if ((o.Flags & Sprite3D) != 0) o.Beta = angle;
                    else o.RealAngle.InitAngleConst(o.Beta, angle, o.SRot, TimerRef);
                }
                break;

            case MoveRandom:
                if ((o.WorkFlags & AnimMasterRot) == 0)
                {
                    if ((o.Col & 128) != 0)
                    {
                        var angle = (o.Beta + (Rand() & 511) - 256 + 512) & 1023;
                        o.RealAngle.InitAngleConst(o.Beta, angle, o.SRot, TimerRef);
                        SetInfoTimer(o, TimerRef + Rnd(300) + 300);
                        InitAnim(GenAnimRien, AnimRepeat, NoAnim, numObj);
                    }
                    if (o.RealAngle.Time == 0)
                    {
                        InitAnim(GenAnimMarche, AnimRepeat, NoAnim, numObj);
                        if (TimerRef > InfoTimer(o))
                        {
                            var angle = (o.Beta + (Rand() & 511) - 256) & 1023;
                            o.RealAngle.InitAngleConst(o.Beta, angle, o.SRot, TimerRef);
                            SetInfoTimer(o, TimerRef + Rnd(300) + 300);
                        }
                    }
                }
                break;

            case MoveTrack:
                if (o.OffsetTrack == -1) o.OffsetTrack = 0;
                break;

            case MoveSameXZ:
                {
                    var target = Objects[o.Info3];
                    o.PosX = target.PosX;
                    o.PosZ = target.PosZ;
                }
                break;
        }
    }

    // ---------------------------------------------------------------------------------------------- OBJECT.C: CheckZoneSce

    private void CheckZoneSce(Lba1Object o, int numObj)
    {
        int x = o.PosX, y = o.PosY, z = o.PosZ;
        o.ZoneSce = -1;
        if (numObj == 0) flagClimbing = false;
        var flagGrm = false;

        for (var n = 0; n < Zones.Count; n++)
        {
            var zone = Zones[n];
            if (!(x >= zone.X0 && x <= zone.X1 && y >= zone.Y0 && y <= zone.Y1 && z >= zone.Z0 && z <= zone.Z1)) continue;

            // T_ZONE: Num = Info[0], Info0..2 = Info[1..3]
            int num = zone.Info[0], info0 = zone.Info[1], info1 = zone.Info[2], info2 = zone.Info[3];
            switch (zone.Type)
            {
                case 0:   // change scene
                    if (numObj == 0 && o.LifePoint > 0)
                    {
                        newCube = num;
                        newPosX = o.PosX - zone.X0 + info0;
                        newPosY = o.PosY - zone.Y0 + info1;
                        newPosZ = o.PosZ - zone.Z0 + info2;
                        flagChgCube = 1;
                        Log($"zone {n}: change to scene {num}");
                        return;
                    }
                    break;

                case 1:   // camera position
                    if (numObj == NumObjFollow)
                    {
                        cameraZone = true;
                        CameraX = info0; CameraY = info1; CameraZ = info2;
                    }
                    break;

                case 2:   // scenaric zone
                    o.ZoneSce = num;
                    break;

                case 3:   // grid fragment (GRM): the map changes while the followed actor stands in the zone
                    if (numObj == NumObjFollow)
                    {
                        flagGrm = true;
                        if (zoneGrm != num)
                        {
                            if (zoneGrm != -1) Cube.Restore();
                            zoneGrm = num;
                            indexGrm = n;
                            IncrustGrm(num);
                        }
                    }
                    break;

                case 4:   // giver
                    if (numObj == 0 && actionNormal)
                    {
                        InitAnim(GenAnimAction, AnimThen, GenAnimRien, 0);
                        ZoneGiveExtraBonus(zone);
                    }
                    break;

                case 5:   // message
                    if (numObj == 0 && actionNormal && dialogues.Count == 0) SayDialogue(0, info0, num);
                    break;

                case 6:   // ladder
                    if (numObj == 0 && Comportement != CProtopack
                        && (o.GenAnim == GenAnimMarche || o.GenAnim == GenAnimEchelle || o.GenAnim == GenAnimMonte))
                    {
                        var (lx, lz) = Lba1Trig.Rotate(o.XMin, o.ZMin, o.Beta + 896 + 512);
                        lx += nxw; lz += nzw;
                        if (lx >= 0 && lz >= 0 && lx <= 63 * SizeBrickXZ && lz <= 63 * SizeBrickXZ && WorldColBrick(lx, o.PosY + 256, lz) != 0)
                        {
                            flagClimbing = true;
                            if (o.PosY >= (zone.Y0 + zone.Y1) / 2) InitAnim(GenAnimEchelle, AnimAllThen, GenAnimRien, numObj);
                            else InitAnim(GenAnimMonte, AnimRepeat, NoAnim, numObj);
                        }
                    }
                    break;
            }
        }

        // leaving every grid-fragment zone puts the map back
        if (!flagGrm && numObj == NumObjFollow && zoneGrm != -1)
        {
            indexGrm = zoneGrm = -1;
            Cube.Restore();
            GridChanged?.Invoke();
        }
    }

    private int zoneGrm = -1, indexGrm = -1;

    // Raised when the map's cells change (a grid fragment appears or goes), so a view can redraw it.
    public event Action? GridChanged;

    // IncrustGrm: mix grid fragment `num` into the map.
    private void IncrustGrm(int num)
    {
        if (Data.GridFragment(num) is not { } fragment) { Log($"grid fragment {num} is missing"); return; }
        Cube.Mix(fragment);
        Log($"grid fragment {num} appears");
        GridChanged?.Invoke();
    }
}
