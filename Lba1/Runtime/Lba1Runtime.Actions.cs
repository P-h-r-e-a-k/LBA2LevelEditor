using static LBAAssembler.Lba1.Runtime.Lba1Const;

namespace LBAAssembler.Lba1.Runtime;

// FICHE.C GereAnimAction: what an animation triggers on its key frames, listed with the animation in the entity file.
// Blows (the hit force an attack lands with), sounds and steps run; projectiles (throw, magic ball) are skipped, and only
// noted in the log.
internal sealed partial class Lba1Runtime
{
    private const int BaseStepSound = 126;
    private static readonly int[] MagicHitForce = { 2, 3, 4, 6, 8 };

    private void GereAnimAction(Lba1Object o, int numObj)
    {
        var d = o.AnimActions;
        if (d is null || d.Length == 0) return;
        var p = 0;
        int Byte() => d[p++];
        int Word() { var v = d[p] | d[p + 1] << 8; p += 2; return v; }

        int count = Byte();
        for (var n = 0; n < count && p < d.Length; n++)
        {
            switch (Byte())
            {
                case 5:   // hit
                    if (o.Frame == Byte() - 1)
                    {
                        o.HitForce = Byte();
                        o.WorkFlags |= OkHit;
                    }
                    else p++;
                    break;

                case 6:   // sample
                    if (o.Frame == Byte()) PlaySample(Word(), numObj);
                    else p += 2;
                    break;

                case 7:   // sample with a random pitch
                    if (o.Frame == Byte()) { PlaySample(Word(), numObj); p += 2; }
                    else p += 4;
                    break;

                case 8:   // throw
                case 12:  // throw, aimed up or down at the hero
                    if (o.Frame == Byte())
                    {
                        var kind = d[p - 2];
                        var alphaAim = 0;
                        if (kind == 12)
                        {
                            var dist = Lba1Trig.Distance2D(o.PosX, o.PosZ, Hero.PosX, Hero.PosZ);
                            alphaAim = Lba1Trig.GetAngle(o.PosY, 0, Hero.PosY, dist);
                        }
                        int point = (short)Word(), sprite = Byte(), alpha = alphaAim + Word(), beta = o.Beta + Word(), speed = Word(), weight = Byte(), force = Byte();
                        ThrowExtra(numObj, o.PosX, o.PosY + point, o.PosZ, sprite, alpha, beta, speed, weight, force);
                    }
                    else p += 11;
                    break;

                case 9:   // throw the magic ball
                    if (MagicBall == -1)
                    {
                        if (o.Frame == Byte())
                        {
                            int point = (short)Word(), alpha = Word(), speed = Word(), weight = Byte();
                            ThrowMagicBall(o.PosX, o.PosY + point, o.PosZ, alpha, o.Beta, speed, weight);
                        }
                        else p += 7;
                    }
                    else p += 8;
                    break;

                case 10:  // sample repeated
                    if (o.Frame == Byte()) { PlaySample(Word(), numObj); p += 2; }
                    else p += 4;
                    break;

                case 11:  // throw that searches
                    if (o.Frame == Byte())
                    {
                        int point = (short)Word(), sprite = Byte(), search = Byte(), speed = Word(), force = Byte();
                        ExtraSearch(numObj, o.PosX, o.PosY + point, o.PosZ, sprite, search, speed, force);
                    }
                    else p += 6;
                    break;

                case 13:  // stop a sample
                    Byte(); p += 2;
                    break;

                case 15:  // left step
                    if (o.Frame == Byte() && o.CodeJeu != 0xF0 && (o.CodeJeu & 0xF0) != 0xF0)
                        PlaySample((o.CodeJeu >> 4) + BaseStepSound, numObj);
                    break;

                case 16:  // right step
                    if (o.Frame == Byte() && o.CodeJeu != 0xF0 && (o.CodeJeu & 0xF0) != 0xF0)
                        PlaySample((o.CodeJeu & 0x0F) + BaseStepSound + 15, numObj);
                    break;

                case 17:  // hit of Twinsen's magic wand
                    if (o.Frame == Byte() - 1)
                    {
                        o.HitForce = MagicHitForce[Math.Clamp(MagicLevel, 0, MagicHitForce.Length - 1)];
                        o.WorkFlags |= OkHit;
                    }
                    break;

                case 18:  // throw from a point of the body
                case 19:  // ... aimed at the hero
                    if (o.Frame == Byte())
                    {
                        var kind = d[p - 2];
                        var alphaAim = 0;
                        if (kind == 19)
                        {
                            var dist = Lba1Trig.Distance2D(o.PosX, o.PosZ, Hero.PosX, Hero.PosZ);
                            alphaAim = Lba1Trig.GetAngle(o.PosY, 0, Hero.PosY, dist);
                        }
                        int px = (short)Word(), py = (short)Word(), pz = (short)Word();
                        var (rx, rz) = Lba1Trig.Rotate(px, pz, o.Beta);
                        int sprite = Byte(), alpha = alphaAim + Word(), beta = o.Beta + Word(), speed = Word(), weight = Byte(), force = Byte();
                        ThrowExtra(numObj, rx + o.PosX, py + o.PosY, rz + o.PosZ, sprite, alpha, beta, speed, weight, force);
                    }
                    else p += 15;
                    break;

                case 20:  // ... homing
                    if (o.Frame == Byte())
                    {
                        int px = (short)Word(), py = (short)Word(), pz = (short)Word();
                        var (rx, rz) = Lba1Trig.Rotate(px, pz, o.Beta);
                        int sprite = Byte(), search = Byte(), speed = Word(), force = Byte();
                        ExtraSearch(numObj, rx + o.PosX, py + o.PosY, rz + o.PosZ, sprite, search, speed, force);
                    }
                    else p += 11;
                    break;

                case 21:  // the magic ball from a point of the body
                    if (MagicBall == -1)
                    {
                        if (o.Frame == Byte())
                        {
                            int px = (short)Word(), py = (short)Word(), pz = (short)Word();
                            var (rx, rz) = Lba1Trig.Rotate(px, pz, o.Beta);
                            int alpha = Word(), speed = Word(), weight = Byte();
                            ThrowMagicBall(rx + o.PosX, py + o.PosY, rz + o.PosZ, alpha, o.Beta, speed, weight);
                        }
                        else p += 11;
                    }
                    else p += 12;
                    break;

                default:
                    return;
            }
        }
    }
}
