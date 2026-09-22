using System.IO;

namespace LBAAssembler;

// LBA2's entity table (RESS.HQR entry 44: a table of U32 offsets, then one record list per entity; not an HQR archive): which bodies (BODY.HQR entries) and animations
// (ANIM.HQR entries) each entity - a kind of actor - has. An actor's body and animation only go together when they belong to the same entity: the body's bones
// are what the entity's animations move. The records (FICHE.CPP, F_BODY = 1 and F_ANIM = 3, ended by 255):
//   body      1, generic number (1 byte), size, BODY.HQR index (S16), ...     the next record starts 2 + size bytes on
//   animation 3, generic number (2 bytes), size, ANIM.HQR index (S16), ...    the next record starts 3 + size bytes on
//   anything else: the command, a one-byte number, a size, ...                 2 + size bytes on
internal sealed class Lba2EntityTable
{
    public sealed record Entity(int Id, IReadOnlyList<(int Generic, int Body)> Bodies, IReadOnlyList<(int Generic, int Anim)> Anims);

    public IReadOnlyList<Entity> Entities { get; }

    private Lba2EntityTable(IReadOnlyList<Entity> entities) => Entities = entities;

    // The table of a game folder, or null when RESS.HQR has none.
    public static Lba2EntityTable? Load(string gameDirectory)
    {
        try
        {
            var path = Path.Combine(gameDirectory, "RESS.HQR");
            return File.Exists(path) ? Parse(HqrArchive.Open(path).Read(44)) : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            DebugLog.Log($"Lba2EntityTable: {error.Message}");
            return null;
        }
    }

    public static Lba2EntityTable Parse(byte[] table)
    {
        var entities = new List<Entity>();
        var count = BitConverter.ToInt32(table, 0) / 4 - 1;      // (the first offset is where the records start)
        for (var id = 0; id < count; id++)
        {
            var start = BitConverter.ToInt32(table, id * 4);
            var end = id + 1 < count ? BitConverter.ToInt32(table, (id + 1) * 4) : table.Length;
            var bodies = new List<(int, int)>();
            var anims = new List<(int, int)>();
            for (var p = start; p + 2 < end && table[p] != 255;)
            {
                var command = table[p];
                if (command == 1 && p + 5 <= end) { bodies.Add((table[p + 1], table[p + 3] | table[p + 4] << 8)); p += 2 + table[p + 2]; }
                else if (command == 3 && p + 6 <= end)
                {
                    var hqr = (short)(table[p + 4] | table[p + 5] << 8);
                    if (hqr >= 0) anims.Add((table[p + 1] | table[p + 2] << 8, hqr));
                    p += 3 + table[p + 3];
                }
                else p += command == 3 ? 3 + table[p + 3] : 2 + table[p + 2];
            }
            entities.Add(new Entity(id, bodies, anims));
        }
        return new Lba2EntityTable(entities);
    }

    // The entities that have this BODY.HQR entry as one of their bodies.
    public IEnumerable<Entity> EntitiesWithBody(int bodyIndex) => Entities.Where(e => e.Bodies.Any(b => b.Body == bodyIndex));

    // The animations (ANIM.HQR indices, without repeats, in the table's order) of the entities that have this body, and the one that is each entity's standing
    // still (its generic animation 0), first, when there is one.
    public IReadOnlyList<int> AnimationsOfBody(int bodyIndex, out int? standing)
    {
        var owners = EntitiesWithBody(bodyIndex).ToList();
        standing = owners.SelectMany(e => e.Anims).Where(a => a.Generic == 0).Select(a => (int?)a.Anim).FirstOrDefault();
        return owners.SelectMany(e => e.Anims).Select(a => a.Anim).Distinct().ToList();
    }

    // What the animation list and the chosen animation should be once `body` is picked for an actor that had animation `currentAnim`: the animations of the
    // entities that have the body (only those with as many groups as the body has bones, when it is known and some do), and the current animation kept when it
    // is one of them or otherwise has as many groups as the body has bones, else the body's standing animation (generic animation 0), else the first.
    // Null when no entity has this body (one made with Body Studio, for instance): nothing to go by.
    public sealed record Choice(IReadOnlyList<int> Natural, int Chosen);

    public Choice? ChooseAnimations(int body, int? bones, Func<int, int?> groups, int currentAnim)
    {
        var owned = EntitiesWithBody(body).SelectMany(e => e.Anims).GroupBy(a => a.Anim).Select(g => g.First()).ToList();
        if (owned.Count == 0) return null;
        var fitting = bones is { } n ? owned.Where(a => groups(a.Anim) == n).ToList() : owned;
        if (fitting.Count == 0) fitting = owned;
        var natural = fitting.Select(a => a.Anim).ToList();
        var keep = natural.Contains(currentAnim) || bones is { } count && groups(currentAnim) == count;
        var chosen = keep ? currentAnim : fitting.Where(a => a.Generic == 0).Select(a => (int?)a.Anim).FirstOrDefault() ?? natural[0];
        return new Choice(natural, chosen);
    }
}
