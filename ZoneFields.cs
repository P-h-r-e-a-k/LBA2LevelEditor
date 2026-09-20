namespace LBA2LevelEditor;

// One editable value of a zone, with the label the inspector shows for it.
internal sealed record ZoneField(string Label, Func<ZoneData, int> Get, Action<ZoneData, int> Set, string? Hint = null);

// What each zone type's payload means (LBA2: docs/ZONES.md; LBA1: the same layout with four info words).
internal static class ZoneFields
{
    public static IReadOnlyList<ZoneField> For(ZoneData zone) => zone.Game == 1 ? Lba1(zone.Type) : Lba2(zone.Type);

    private static ZoneField Info(int slot, string label, string? hint = null)
        => new(label, z => z.Info[slot], (z, v) => z.Info[slot] = v, hint);

    private static ZoneField Num(string label, string? hint = null)
        => new(label, z => z.Num, (z, v) => z.Num = v, hint);

    private static IReadOnlyList<ZoneField> Lba1(int type)
    {
        switch (type)
        {
            case 0:
                return new[]
                {
                    Info(0, "Destination scene", "the scene this cube change leads to"),
                    Info(1, "Arrival X"), Info(2, "Arrival Y"), Info(3, "Arrival Z"),
                };
            case 1:
                return new[] { Info(0, "Camera X"), Info(1, "Camera Y"), Info(2, "Camera Z"), Info(3, "Info 3") };
            case 2:
                return new[] { Info(0, "Zone number", "read by life scripts"), Info(1, "Info 1"), Info(2, "Info 2"), Info(3, "Info 3") };
            case 5:
                return new[] { Info(0, "Text id"), Info(1, "Info 1"), Info(2, "Info 2"), Info(3, "Info 3") };
            default:
                return new[] { Info(0, "Info 0"), Info(1, "Info 1"), Info(2, "Info 2"), Info(3, "Info 3") };
        }
    }

    private static IReadOnlyList<ZoneField> Lba2(int type)
    {
        var flags = Info(7, "Flags", "1 on at load, 2 on now, 4 fired, 8 camera: every frame");
        switch (type)
        {
            case 0:
                return new[]
                {
                    Num("Destination scene", "the scene (cube) this cube change leads to"),
                    Info(0, "Arrival X"), Info(1, "Arrival Y", "relative to the zone's Y min"), Info(2, "Arrival Z"),
                    Info(5, "Door test", "1 = needs a door collision instead of firing on entry"),
                    Info(6, "Keep position", "1 = don't reposition Twinsen on arrival"),
                    flags,
                };
            case 1:
                return new[]
                {
                    Info(0, "Camera X"), Info(1, "Camera Y"), Info(2, "Camera Z"),
                    Info(3, "Alpha"), Info(4, "Beta"), Info(5, "Gamma"), Info(6, "Distance"),
                    flags,
                };
            case 2:
                return new[] { Num("Zone number", "read by life scripts"), flags };
            case 4:
                return new[] { Num("Number"), Info(0, "Bonus kind"), Info(1, "Amount"), Info(2, "Already taken"), flags };
            case 5:
                return new[] { Num("Message"), Info(2, "Facing edge", "1 north, 2 south, 4 east, 8 west"), flags };
            case 6:
                return new[] { Num("Number"), Info(1, "Enabled"), flags };
            case 7:
                return new[] { Num("Number"), Info(1, "Enabled"), Info(2, "Direction", "1 north, 2 south, 4 east, 8 west"), flags };
            case 8:
                return new[] { Num("Number"), Info(1, "Damage"), Info(2, "Cooldown", "fifths of a second"), flags };
            default:
                return new[] { Num("Number"), flags };
        }
    }
}
