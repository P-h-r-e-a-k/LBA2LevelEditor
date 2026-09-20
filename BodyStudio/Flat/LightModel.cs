namespace LbaBodyStudio;

// How the games shade a lit polygon: its palette index is the bottom of a 16-step ramp (dark to light) and the light adds some
// steps to it (LBA1 up to ~11, LBA2 up to ~9; on a lit surface facing the light about 8 and 7 -- measured against Twinsen in the engine: his navy jeans, ramp position 2, show at position ~9). A flat picture shows the colour the character should
// have on screen ("display" colour, base + average light); the body stores the base. Measured on the games' own bodies: most of their
// polygons use the first entry of a ramp as their base.
public static class LightModel
{
    public static int Average(int game) => game == 1 ? 8 : 7;
    public static int Max(int game) => game == 1 ? 11 : 9;

    // The colour a lit polygon with base colour `index` shows on average.
    public static int DisplayOf(int index, int game) => Math.Min((index & ~15) + 15, index + Average(game));

    // The base colour whose average lit look is the display colour `index`, kept low enough that the brightest light stays in its ramp.
    public static int BaseOf(int display, int game)
    {
        int bank = display & ~15, position = display & 15;
        var basePosition = Math.Min(Math.Max(0, position - Average(game)), 15 - Max(game));
        return Math.Max(1, bank + basePosition);
    }

    // Is this polygon lit by the game (so its stored colour is a base rather than what shows)? Read bodies know their polygon type;
    // bodies built by the generator carry Body.Lit instead.
    public static bool IsLit(Face face, int game, bool bodyLit)
    {
        if (face.Material < 0) return bodyLit;
        return game == 1 ? face.Material >= 7 : face.Material is not (0 or 3);
    }
}
