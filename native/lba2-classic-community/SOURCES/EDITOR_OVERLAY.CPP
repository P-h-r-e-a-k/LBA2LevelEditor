/* EDITOR_OVERLAY: the scene's zone boxes and actor paths drawn over the game, for the level editor that embeds this engine.
 *
 * Off unless the environment names a control file (LBA2_OVERLAY_FILE). The editor rewrites that file as its zone-type checkboxes
 * change and the engine looks at it every few frames, so what is drawn follows the editor while the game runs:
 *
 *     zones=<bit mask of the zone types to draw, bit n = type n> paths=<0|1>
 *
 * Zones are the scene's boxes in the colours the editor uses for each type; paths are the routes the actors' track scripts walk
 * (every point the GOTO_POINT family visits, joined by lines, with a small flag at each point). Drawn after the scene, before the
 * frame is shown, with the game's own projection, so they sit exactly on the picture in interiors and on islands alike.
 */
#include "C_EXTERN.H"
#include "COMMON.H"

#include <3D/LPROJ.H>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <sys/stat.h>

static const char *s_file = NULL;
static int s_checked = 0;       // frames since the file was last looked at
static long s_stamp = -1;       // its modification time when it was last read
static U32 s_zoneMask = 0;
static int s_paths = 0;

static void ReadControlFile() {
    struct stat info;
    if (stat(s_file, &info) != 0) return;
    if ((long)info.st_mtime == s_stamp && s_stamp != -1) return;
    s_stamp = (long)info.st_mtime;
    FILE *f = fopen(s_file, "r");
    if (!f) return;
    char text[256];
    size_t n = fread(text, 1, sizeof(text) - 1, f);
    fclose(f);
    text[n] = 0;
    unsigned long mask = 0;
    int paths = 0;
    const char *z = strstr(text, "zones=");
    if (z) mask = strtoul(z + 6, NULL, 0);
    const char *p = strstr(text, "paths=");
    if (p) paths = atoi(p + 6);
    s_zoneMask = (U32)mask;
    s_paths = paths;
}

/* The editor's colours per zone type (see ZoneStyle), matched to the nearest colour of the game's palette. */
static const U8 kZoneRgb[10][3] = {
    {0xF5, 0xA6, 0x23}, {0x5B, 0xC0, 0xEB}, {0xE0, 0x40, 0xFB}, {0x9E, 0x9E, 0x9E}, {0xFF, 0xD5, 0x4F},
    {0x66, 0xBB, 0x6A}, {0xA1, 0x88, 0x7F}, {0x26, 0xA6, 0x9A}, {0xEF, 0x53, 0x50}, {0x7E, 0x57, 0xC2},
};

static S32 NearestColour(int r, int g, int b) {
    const U8 *pal = PtrPalCurrent ? PtrPalCurrent : PalettePcx;
    if (!pal) return LBAWHITE;
    S32 best = 15;
    long bestDistance = 1L << 30;
    for (int i = 1; i < 256; i++) {
        long dr = pal[i * 3] - r, dg = pal[i * 3 + 1] - g, db = pal[i * 3 + 2] - b;
        long d = dr * dr + dg * dg + db * db;
        if (d < bestDistance) { bestDistance = d; best = i; }
    }
    return best;
}

/* Projects the eight corners of a box and draws its twelve edges; false when nothing of it is on screen. */
static bool DrawBox(S32 x0, S32 y0, S32 z0, S32 x1, S32 y1, S32 z1, S32 colour) {
    S32 sx[8], sy[8];
    ClearScreenMinMax();
    for (int i = 0; i < 8; i++) {
        PtrProjectPoint((i & 1) ? x1 : x0, (i & 2) ? y1 : y0, (i & 4) ? z1 : z0);
        AdjustScreenMinMax();
        sx[i] = Xp;
        sy[i] = Yp;
    }
    ScreenXMax++;
    SetClip(ScreenXMin, ScreenYMin, ScreenXMax, ScreenYMax);
    if (ClipXMin > ClipXMax || ClipYMin > ClipYMax) return false;
    static const int edges[12][2] = {{0, 1}, {2, 3}, {4, 5}, {6, 7}, {0, 2}, {1, 3}, {4, 6}, {5, 7}, {0, 4}, {1, 5}, {2, 6}, {3, 7}};
    for (int e = 0; e < 12; e++) Line(sx[edges[e][0]], sy[edges[e][0]], sx[edges[e][1]], sy[edges[e][1]], colour);
    return true;
}

/* The points an actor's track script goes to, in program order (the same walk the editor's renderer library does). */
static int TrackPoints(T_OBJET *obj, S32 *x, S32 *y, S32 *z, int max) {
    if (!obj->PtrTrack) return 0;
    int count = 0;
    S32 offset = 0;
    while (offset < 8192 && count < max) {
        U8 *ptr = obj->PtrTrack + offset;
        U8 macro = *ptr++;
        offset++;
        switch (macro) {
        case TM_GOTO_POINT: case TM_GOTO_POINT_3D: case TM_GOTO_SYM_POINT: case TM_POS_POINT: {
            U8 index = *ptr;
            if (index < NbBrickTrack) {
                x[count] = ListBrickTrack[index].X;
                y[count] = ListBrickTrack[index].Y;
                z[count] = ListBrickTrack[index].Z;
                count++;
            }
            offset++;
            break;
        }
        case TM_LABEL: case TM_BODY: case TM_BACKGROUND: case TM_SET_FRAME: case TM_SET_FRAME_3DS: case TM_SET_START_3DS:
        case TM_SET_END_3DS: case TM_START_ANIM_3DS: case TM_WAIT_FRAME_3DS: case TM_VOLUME:
            offset++;
            break;
        case TM_ANIM: case TM_SAMPLE: case TM_SAMPLE_RND: case TM_SAMPLE_ALWAYS: case TM_SAMPLE_STOP: case TM_REPEAT_SAMPLE:
        case TM_SIMPLE_SAMPLE: case TM_WAIT_NB_ANIM: case TM_ANGLE: case TM_FACE_TWINSEN: case TM_OPEN_LEFT: case TM_OPEN_RIGHT:
        case TM_OPEN_UP: case TM_OPEN_DOWN: case TM_BETA: case TM_GOTO: case TM_SPEED: case TM_SPRITE: case TM_DECALAGE:
        case TM_FREQUENCE:
            offset += 2;
            break;
        case TM_ANGLE_RND: case TM_LOOP:
            offset += 4;
            break;
        case TM_WAIT_NB_SECOND: case TM_WAIT_NB_SECOND_RND: case TM_WAIT_NB_DIZIEME: case TM_WAIT_NB_DIZIEME_RND:
            offset += 5;
            break;
        case TM_PLAY_ACF: {
            U8 c;
            do { c = *ptr++; offset++; } while (c != 0 && offset < 8192);
            break;
        }
        case TM_WAIT_ANIM: case TM_STOP:
            break;
        default:
            return count;           /* TM_END or an opcode this walk doesn't know */
        }
    }
    return count;
}

static void DrawLine3D(S32 x0, S32 y0, S32 z0, S32 x1, S32 y1, S32 z1, S32 colour) {
    PtrProjectPoint(x0, y0, z0);
    S32 ax = Xp, ay = Yp;
    PtrProjectPoint(x1, y1, z1);
    S32 bx = Xp, by = Yp;
    ClearScreenMinMax();
    ScreenXMin = ax < bx ? ax : bx; ScreenXMax = (ax < bx ? bx : ax) + 1;
    ScreenYMin = ay < by ? ay : by; ScreenYMax = (ay < by ? by : ay) + 1;
    SetClip(ScreenXMin, ScreenYMin, ScreenXMax, ScreenYMax);
    if (ClipXMin > ClipXMax || ClipYMin > ClipYMax) return;
    Line(ax, ay, bx, by, colour);
    BoxMovingAdd(ClipXMin, ClipYMin, ClipXMax, ClipYMax);
}

void EditorOverlay_Draw(void) {
    static int initialised = 0;
    if (!initialised) {
        initialised = 1;
        s_file = getenv("LBA2_OVERLAY_FILE");
        if (s_file && !s_file[0]) s_file = NULL;
        if (s_file) ReadControlFile();
    }
    if (!s_file) return;
    if (++s_checked >= 15) { s_checked = 0; ReadControlFile(); }
    if (!s_zoneMask && !s_paths) return;

    /* interiors project relative to the cube's start corner, islands in the cube's own coordinates */
    S32 xw = 0, yw = 0, zw = 0;
    if (CubeMode == CUBE_INTERIEUR) {
        xw = StartXCube * SIZE_BRICK_XZ;
        yw = StartYCube * SIZE_BRICK_Y;
        zw = StartZCube * SIZE_BRICK_XZ;
    }

    if (s_zoneMask) {
        T_ZONE *zone = ListZone;
        for (S32 n = 0; n < NbZones; n++, zone++) {
            if (zone->Type < 0 || zone->Type > 9 || !(s_zoneMask & (1u << zone->Type))) continue;
            const U8 *rgb = kZoneRgb[zone->Type];
            if (DrawBox(zone->X0 - xw, zone->Y0 - yw, zone->Z0 - zw, zone->X1 - xw, zone->Y1 - yw, zone->Z1 - zw,
                        NearestColour(rgb[0], rgb[1], rgb[2])))
                BoxMovingAdd(ClipXMin, ClipYMin, ClipXMax, ClipYMax);
        }
    }

    if (s_paths) {
        static S32 px[64], py[64], pz[64];
        for (S32 n = 1; n < NbObjets; n++) {
            T_OBJET *obj = &ListObjet[n];
            if (!obj->PtrTrack) continue;
            int count = TrackPoints(obj, px, py, pz, 64);
            if (count < 1) continue;
            /* the colour of the route follows the actor, so two routes that cross can be told apart */
            static const U8 palette[8][3] = {{255, 60, 60}, {255, 165, 0}, {255, 255, 0}, {255, 0, 255}, {0, 191, 255}, {0, 255, 0}, {255, 105, 180}, {255, 255, 255}};
            const U8 *c = palette[n & 7];
            S32 colour = NearestColour(c[0], c[1], c[2]);
            DrawLine3D(obj->Obj.X - xw, obj->Obj.Y - yw, obj->Obj.Z - zw, px[0] - xw, py[0] - yw, pz[0] - zw, colour);
            for (int i = 1; i < count; i++) DrawLine3D(px[i - 1] - xw, py[i - 1] - yw, pz[i - 1] - zw, px[i] - xw, py[i] - yw, pz[i] - zw, colour);
            for (int i = 0; i < count; i++) DrawLine3D(px[i] - xw, py[i] - yw, pz[i] - zw, px[i] - xw, py[i] - yw + 700, pz[i] - zw, colour);      /* a flag pole at each point */
        }
    }
    UnsetClip();
}
