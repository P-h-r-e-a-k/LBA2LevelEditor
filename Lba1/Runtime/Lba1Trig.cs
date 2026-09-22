namespace LBAAssembler.Lba1.Runtime;

// The integer math LBA1's game logic runs on, ported from LIB386/LIB_3D (P_FUNC.ASM, P_TRIGO.ASM, P_ANIM.ASM):
// a 1024-step angle, a sine table scaled by 16384, floor square roots, and the interpolators the engine uses for
// turning, falling and sliding doors. Ported operation for operation so a simulated scene moves like the game does
// (integer division truncates towards zero, shifts are arithmetic, 16-bit fields wrap).
internal static class Lba1Trig
{
    // P_SinTab (P_SINTAB.ASM): sin(2*pi*i/1024) * 16384, i = 0..1023.
    private static readonly short[] SinTab =
    {
        0, 101, 201, 302, 402, 503, 603, 704, 804, 904, 1005, 1105, 1205, 1306, 1406, 1506,
        1606, 1706, 1806, 1906, 2006, 2105, 2205, 2305, 2404, 2503, 2603, 2702, 2801, 2900, 2999, 3098,
        3196, 3295, 3393, 3492, 3590, 3688, 3786, 3883, 3981, 4078, 4176, 4273, 4370, 4467, 4563, 4660,
        4756, 4852, 4948, 5044, 5139, 5235, 5330, 5425, 5520, 5614, 5708, 5803, 5897, 5990, 6084, 6177,
        6270, 6363, 6455, 6547, 6639, 6731, 6823, 6914, 7005, 7096, 7186, 7276, 7366, 7456, 7545, 7635,
        7723, 7812, 7900, 7988, 8076, 8163, 8250, 8337, 8423, 8509, 8595, 8680, 8765, 8850, 8935, 9019,
        9102, 9186, 9269, 9352, 9434, 9516, 9598, 9679, 9760, 9841, 9921, 10001, 10080, 10159, 10238, 10316,
        10394, 10471, 10549, 10625, 10702, 10778, 10853, 10928, 11003, 11077, 11151, 11224, 11297, 11370, 11442, 11514,
        11585, 11656, 11727, 11797, 11866, 11935, 12004, 12072, 12140, 12207, 12274, 12340, 12406, 12472, 12537, 12601,
        12665, 12729, 12792, 12854, 12916, 12978, 13039, 13100, 13160, 13219, 13279, 13337, 13395, 13453, 13510, 13567,
        13623, 13678, 13733, 13788, 13842, 13896, 13949, 14001, 14053, 14104, 14155, 14206, 14256, 14305, 14354, 14402,
        14449, 14497, 14543, 14589, 14635, 14680, 14724, 14768, 14811, 14854, 14896, 14937, 14978, 15019, 15059, 15098,
        15137, 15175, 15213, 15250, 15286, 15322, 15357, 15392, 15426, 15460, 15493, 15525, 15557, 15588, 15619, 15649,
        15679, 15707, 15736, 15763, 15791, 15817, 15843, 15868, 15893, 15917, 15941, 15964, 15986, 16008, 16029, 16049,
        16069, 16088, 16107, 16125, 16143, 16160, 16176, 16192, 16207, 16221, 16235, 16248, 16261, 16273, 16284, 16295,
        16305, 16315, 16324, 16332, 16340, 16347, 16353, 16359, 16364, 16369, 16373, 16376, 16379, 16381, 16383, 16384,
        16384, 16384, 16383, 16381, 16379, 16376, 16373, 16369, 16364, 16359, 16353, 16347, 16340, 16332, 16324, 16315,
        16305, 16295, 16284, 16273, 16261, 16248, 16235, 16221, 16207, 16192, 16176, 16160, 16143, 16125, 16107, 16088,
        16069, 16049, 16029, 16008, 15986, 15964, 15941, 15917, 15893, 15868, 15843, 15817, 15791, 15763, 15736, 15707,
        15679, 15649, 15619, 15588, 15557, 15525, 15493, 15460, 15426, 15392, 15357, 15322, 15286, 15250, 15213, 15175,
        15137, 15098, 15059, 15019, 14978, 14937, 14896, 14854, 14811, 14768, 14724, 14680, 14635, 14589, 14543, 14497,
        14449, 14402, 14354, 14305, 14256, 14206, 14155, 14104, 14053, 14001, 13949, 13896, 13842, 13788, 13733, 13678,
        13623, 13567, 13510, 13453, 13395, 13337, 13279, 13219, 13160, 13100, 13039, 12978, 12916, 12854, 12792, 12729,
        12665, 12601, 12537, 12472, 12406, 12340, 12274, 12207, 12140, 12072, 12004, 11935, 11866, 11797, 11727, 11656,
        11585, 11514, 11442, 11370, 11297, 11224, 11151, 11077, 11003, 10928, 10853, 10778, 10702, 10625, 10549, 10471,
        10394, 10316, 10238, 10159, 10080, 10001, 9921, 9841, 9760, 9679, 9598, 9516, 9434, 9352, 9269, 9186,
        9102, 9019, 8935, 8850, 8765, 8680, 8595, 8509, 8423, 8337, 8250, 8163, 8076, 7988, 7900, 7812,
        7723, 7635, 7545, 7456, 7366, 7276, 7186, 7096, 7005, 6914, 6823, 6731, 6639, 6547, 6455, 6363,
        6270, 6177, 6084, 5990, 5897, 5803, 5708, 5614, 5520, 5425, 5330, 5235, 5139, 5044, 4948, 4852,
        4756, 4660, 4563, 4467, 4370, 4273, 4176, 4078, 3981, 3883, 3786, 3688, 3590, 3492, 3393, 3295,
        3196, 3098, 2999, 2900, 2801, 2702, 2603, 2503, 2404, 2305, 2205, 2105, 2006, 1906, 1806, 1706,
        1606, 1506, 1406, 1306, 1205, 1105, 1005, 904, 804, 704, 603, 503, 402, 302, 201, 101,
        0, -101, -201, -302, -402, -503, -603, -704, -804, -904, -1005, -1105, -1205, -1306, -1406, -1506,
        -1606, -1706, -1806, -1906, -2006, -2105, -2205, -2305, -2404, -2503, -2603, -2702, -2801, -2900, -2999, -3098,
        -3196, -3295, -3393, -3492, -3590, -3688, -3786, -3883, -3981, -4078, -4176, -4273, -4370, -4467, -4563, -4660,
        -4756, -4852, -4948, -5044, -5139, -5235, -5330, -5425, -5520, -5614, -5708, -5803, -5897, -5990, -6084, -6177,
        -6270, -6363, -6455, -6547, -6639, -6731, -6823, -6914, -7005, -7096, -7186, -7276, -7366, -7456, -7545, -7635,
        -7723, -7812, -7900, -7988, -8076, -8163, -8250, -8337, -8423, -8509, -8595, -8680, -8765, -8850, -8935, -9019,
        -9102, -9186, -9269, -9352, -9434, -9516, -9598, -9679, -9760, -9841, -9921, -10001, -10080, -10159, -10238, -10316,
        -10394, -10471, -10549, -10625, -10702, -10778, -10853, -10928, -11003, -11077, -11151, -11224, -11297, -11370, -11442, -11514,
        -11585, -11656, -11727, -11797, -11866, -11935, -12004, -12072, -12140, -12207, -12274, -12340, -12406, -12472, -12537, -12601,
        -12665, -12729, -12792, -12854, -12916, -12978, -13039, -13100, -13160, -13219, -13279, -13337, -13395, -13453, -13510, -13567,
        -13623, -13678, -13733, -13788, -13842, -13896, -13949, -14001, -14053, -14104, -14155, -14206, -14256, -14305, -14354, -14402,
        -14449, -14497, -14543, -14589, -14635, -14680, -14724, -14768, -14811, -14854, -14896, -14937, -14978, -15019, -15059, -15098,
        -15137, -15175, -15213, -15250, -15286, -15322, -15357, -15392, -15426, -15460, -15493, -15525, -15557, -15588, -15619, -15649,
        -15679, -15707, -15736, -15763, -15791, -15817, -15843, -15868, -15893, -15917, -15941, -15964, -15986, -16008, -16029, -16049,
        -16069, -16088, -16107, -16125, -16143, -16160, -16176, -16192, -16207, -16221, -16235, -16248, -16261, -16273, -16284, -16295,
        -16305, -16315, -16324, -16332, -16340, -16347, -16353, -16359, -16364, -16369, -16373, -16376, -16379, -16381, -16383, -16384,
        -16384, -16384, -16383, -16381, -16379, -16376, -16373, -16369, -16364, -16359, -16353, -16347, -16340, -16332, -16324, -16315,
        -16305, -16295, -16284, -16273, -16261, -16248, -16235, -16221, -16207, -16192, -16176, -16160, -16143, -16125, -16107, -16088,
        -16069, -16049, -16029, -16008, -15986, -15964, -15941, -15917, -15893, -15868, -15843, -15817, -15791, -15763, -15736, -15707,
        -15679, -15649, -15619, -15588, -15557, -15525, -15493, -15460, -15426, -15392, -15357, -15322, -15286, -15250, -15213, -15175,
        -15137, -15098, -15059, -15019, -14978, -14937, -14896, -14854, -14811, -14768, -14724, -14680, -14635, -14589, -14543, -14497,
        -14449, -14402, -14354, -14305, -14256, -14206, -14155, -14104, -14053, -14001, -13949, -13896, -13842, -13788, -13733, -13678,
        -13623, -13567, -13510, -13453, -13395, -13337, -13279, -13219, -13160, -13100, -13039, -12978, -12916, -12854, -12792, -12729,
        -12665, -12601, -12537, -12472, -12406, -12340, -12274, -12207, -12140, -12072, -12004, -11935, -11866, -11797, -11727, -11656,
        -11585, -11514, -11442, -11370, -11297, -11224, -11151, -11077, -11003, -10928, -10853, -10778, -10702, -10625, -10549, -10471,
        -10394, -10316, -10238, -10159, -10080, -10001, -9921, -9841, -9760, -9679, -9598, -9516, -9434, -9352, -9269, -9186,
        -9102, -9019, -8935, -8850, -8765, -8680, -8595, -8509, -8423, -8337, -8250, -8163, -8076, -7988, -7900, -7812,
        -7723, -7635, -7545, -7456, -7366, -7276, -7186, -7096, -7005, -6914, -6823, -6731, -6639, -6547, -6455, -6363,
        -6270, -6177, -6084, -5990, -5897, -5803, -5708, -5614, -5520, -5425, -5330, -5235, -5139, -5044, -4948, -4852,
        -4756, -4660, -4563, -4467, -4370, -4273, -4176, -4078, -3981, -3883, -3786, -3688, -3590, -3492, -3393, -3295,
        -3196, -3098, -2999, -2900, -2801, -2702, -2603, -2503, -2404, -2305, -2205, -2105, -2006, -1906, -1806, -1706,
        -1606, -1506, -1406, -1306, -1205, -1105, -1005, -904, -804, -704, -603, -503, -402, -302, -201, -101,
    };

    private static readonly byte[] SinBytes = BuildBytes();

    private static byte[] BuildBytes()
    {
        var bytes = new byte[SinTab.Length * 2];
        for (var i = 0; i < SinTab.Length; i++) { bytes[i * 2] = (byte)SinTab[i]; bytes[i * 2 + 1] = (byte)(SinTab[i] >> 8); }
        return bytes;
    }

    public static int Sin(int angle) => SinTab[angle & 1023];
    public static int Cos(int angle) => SinTab[(angle + 256) & 1023];

    // Rotate(x, z, angle) of P_TRIGO.ASM: (X0, Y0) = (x*cos + z*sin, z*cos - x*sin) >> 14. The inputs are 16-bit.
    public static (int X, int Z) Rotate(int x, int z, int angle)
    {
        x = (short)x;
        z = (short)z;
        var t = (ushort)angle;
        if (t == 0) return (x, z);
        var s = Sin(t);
        var c = Cos(t);
        return ((x * c + z * s) >> 14, (z * c - x * s) >> 14);
    }

    // Sqr: the integer (floor) square root.
    public static int Sqr(uint value) => (int)Math.Floor(Math.Sqrt(value));

    public static int Distance2D(int x0, int y0, int x1, int y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        return Sqr(unchecked((uint)(dx * dx + dy * dy)));
    }

    public static int Distance3D(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var dz = z1 - z0;
        return Sqr(unchecked((uint)(dx * dx + dy * dy + dz * dz)));
    }

    // Set by GetAngle: the distance between its two points (the engine's global `Distance`).
    public static int Distance;

    private static int Word(int byteOffset) => (short)(SinBytes[byteOffset] | SinBytes[byteOffset + 1] << 8);

    // GetAngle(x0, z0, x1, z1): the direction from the first point to the second in 1024ths of a turn (0 = +z, 256 =
    // +x), found by binary search of the sine table exactly as the assembly does (including its reads of table words
    // at odd byte addresses).
    public static int GetAngle(int x0, int z0, int x1, int z1)
    {
        var edx = z1 - z0;
        var edi = edx;
        var eax = x1 - x0;
        var ebp = eax;

        var xx = unchecked((uint)(eax * eax));
        var zz = unchecked((uint)(edx * edx));
        if (xx >= zz) ebp &= -2;
        else
        {
            (ebp, edi) = (edi, ebp);
            ebp |= 1;
        }
        Distance = Sqr(unchecked(xx + zz));
        if (Distance == 0) return 0;

        var numerator = edi << 14;
        var ax = (short)(numerator / Distance);

        var esi = 384 * 2;
        var edi2 = esi + 256 * 2;
        while (true)
        {
            var ebx = (esi + edi2) >> 1;
            if (ax > Word(ebx))
            {
                edi2 = ebx;
                if (edi2 - esi - 1 == 0) break;
                continue;
            }
            esi = ebx;
            if (ax == Word(ebx)) { goto finish; }
            if (edi2 - esi - 1 == 0) break;
        }
        // the two remaining table entries: pick the nearer one
        if (((Word(esi) + Word(edi2)) >> 1) <= ax) esi = edi2;

    finish:
        var result = (esi - 256 * 2) >> 1;
        if (ebp < 0) result = -result;
        if ((ebp & 1) != 0) result = 256 - result;
        return result & 1023;
    }

    // BoundRegleTrois: value1 + (value2 - value1) * step / steps, held at the ends.
    public static int BoundRegleTrois(int value1, int value2, int steps, int step)
    {
        if (step <= 0) return value1;
        if (step >= steps) return value2;
        return (value2 - value1) * step / steps + value1;
    }
}

// T_REAL_VALUE: a value moving from Start to End over Time ticks of the 50 Hz timer, sampled by GetRealValue /
// GetRealAngle. Used for turning (angles), falling and door speed.
internal sealed class Lba1RealValue
{
    public short Start, End, Time;
    public int MemoTicks;

    public void InitValue(int start, int end, int time, int timerRef)
    {
        Start = (short)start; End = (short)end; Time = (short)time;
        MemoTicks = timerRef;
    }

    public void InitAngle(int start, int end, int time, int timerRef)
    {
        Start = (short)(start & 1023); End = (short)(end & 1023); Time = (short)time;
        MemoTicks = timerRef;
    }

    // InitRealAngleConst: a turn of constant speed. `speed` is the actor's SRot; the time it takes is proportional
    // to the angle to turn: |shortest distance| * speed / 256 ticks.
    public void InitAngleConst(int start, int end, int speed, int timerRef)
    {
        Start = (short)(start & 1023);
        End = (short)(end & 1023);
        var delta = (short)((ushort)(Start - End) << 6);
        var distance = (ushort)(delta < 0 ? -delta : delta) >> 6;   // 0..512: the short way round
        Time = (short)(int)((uint)(distance * speed) >> 8);
        MemoTicks = timerRef;
    }

    public int GetValue(int timerRef)
    {
        if (Time == 0) return End;
        var elapsed = (ushort)(timerRef - MemoTicks);
        if (elapsed >= (ushort)Time) { Time = 0; return End; }
        var delta = (short)(End - Start);
        return (short)(delta * (short)elapsed / Time + Start);
    }

    // GetRealAngle: the current angle, taking the short way round; may be outside 0..1023 (16-bit) until it arrives.
    public int GetAngle(int timerRef)
    {
        if (Time == 0) return (ushort)End;
        var elapsed = (ushort)(timerRef - MemoTicks);
        if (elapsed >= (ushort)Time) { Time = 0; return (ushort)End; }
        int delta = (short)(End - Start);
        if (delta < -512) delta += 1024;
        else if (delta > 512) delta -= 1024;
        return (ushort)(short)(delta * (short)elapsed / Time + Start);
    }
}
