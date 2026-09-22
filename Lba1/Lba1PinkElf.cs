using System.Buffers.Binary;
using System.IO;
using LBAAssembler.LbaScript;
using LBAAssembler.Scenes;

namespace LBAAssembler.Lba1;

// A pink copy of Raymond the Elf for the bedroom (scene 61), built from the game's own data:
//   * body: BODY.HQR entry 88 (Raymond, blue) cloned with only the colour bytes of the blue outfit changed - the hat and
//     tunic (colour 64, palette ramp 4) and the teal sleeve cuffs (160, ramp 10) become the first colour of the pink
//     ramp (224, ramp 14); the engine adds its light steps to that, as it does to every body colour. Face, eyes, nose,
//     hands, shoes, trousers, hat band and bobble keep their colours. Every point, bone and normal is byte-identical, so
//     the body works with all of the Elf entity's animations;
//   * registration: the clone is appended to BODY.HQR (the next free entry, stored uncompressed) and FILE3D.HQR's Elf
//     entity (49) gets a BODY record for it under body id 42. An actor's body number is an id inside its entity, not a
//     BODY.HQR index: the engine finds the entry through the entity's record (FICHE.C SearchBody);
//   * actor: scene 61 gets an elf with that entity and body id, standing on the floor between the beds, that turns to
//     face Twinsen when he comes within 2500 units and looks away again beyond 3000. Its text is drawn in the pink
//     ramp (the speaker's colour, actor field CoulObj = ramp number), and greets him (a new text, below), once when he first comes near and again
//     whenever he presses action beside it.
// Limits checked against the engine source: a clone stays inside every renderer buffer the source body fits (24 of 30
// bones, 132 of 500 points); the entry index stays below 32768 (bit 15 of a body reference marks "already loaded"); the
// loader reads stored (method 0) entries.
internal static class Lba1PinkElf
{
    public const int SourceBody = 88;       // BODY.HQR: Raymond the Elf (Joe, entry 87, is green)
    public const int Entity = 49;           // FILE3D.HQR: Elf, bodies 0 -> 87 (Joe) and 41 -> 88 (Raymond), 4 animations
    public const int BodyId = 42;           // the pink body's id in that entity
    public const int DialogueColour = 14;   // ramp 14 = the pink ramp (palette 224..239)

    private const byte BlueOutfit = 64, TealCuffs = 160, PinkRamp = 224;

    // Cell (57, 55) at floor level (layer 2's top): three cells in from where Twinsen arrives from Lupin Burg.
    private const int X = 57 * 512, Y = 768, Z = 55 * 512, Facing = 256 /* +x, towards the door */;

    // What the tool writes, or nothing when the game files already hold the pink elf. Texts names the new body
    // in BODY.HQD (see HqdWriter), so it isn't just an unlabelled number the next time someone looks.
    public sealed record Plan(int BodyIndex, IReadOnlyList<HqrEntryStore.Edit> Files, IReadOnlyList<HqrEntryStore.TextEdit> Texts)
    {
        public bool Changed => Files.Count > 0;
    }

    // ---- the body ------------------------------------------------------------------------------------------------

    public static byte[] Recolour(byte[] raymond)
    {
        var pink = (byte[])raymond.Clone();
        int blue = 0, teal = 0;
        foreach (var header in PolygonHeaders(raymond))
        {
            switch (pink[header + 2])
            {
                case BlueOutfit: pink[header + 2] = PinkRamp; blue++; break;
                case TealCuffs: pink[header + 2] = PinkRamp; teal++; break;
            }
        }
        if (blue != 39 || teal != 6)
            throw new InvalidDataException($"BODY.HQR entry {SourceBody} isn't Raymond the Elf (expected 39 blue and 6 teal polygons, found {blue} and {teal}).");
        return pink;
    }

    // Where each polygon's header (type, corner count, colour bytes) starts in an animated LBA1 body:
    // 16-byte header + skipped bytes, points (6 bytes), bones (38), normals (8), then the polygons.
    private static List<int> PolygonHeaders(byte[] d)
    {
        try
        {
            int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
            if ((U16(0) & 2) == 0) throw new InvalidDataException("not an animated body");
            var p = 16 + U16(14);
            p += 2 + U16(p) * 6;
            p += 2 + U16(p) * 38;
            p += 2 + U16(p) * 8;
            var count = U16(p); p += 2;
            var headers = new List<int>(count);
            for (var i = 0; i < count; i++)
            {
                int type = d[p], corners = d[p + 1];
                headers.Add(p);
                p += type >= 9 ? 4 + 4 * corners : type >= 7 ? 6 + 2 * corners : 4 + 2 * corners;
            }
            // lines, spheres and the two trailing bytes must account for the rest of the entry
            var lines = U16(p); p += 2 + lines * 8;
            var spheres = U16(p); p += 2 + spheres * 8;
            if (p + 2 != d.Length) throw new InvalidDataException("the polygon data doesn't end where the body does");
            return headers;
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("The body's layout isn't what LBA1 bodies have.", error);
        }
    }

    // ---- BODY.HQR and FILE3D.HQR ---------------------------------------------------------------------------------

    private sealed record Record(int Type, int Id, int Start, int End, int Hqr);

    // An entity's records: type (1 body, 3 animation), id, size byte, then size - 1 more bytes; 255 ends the list.
    private static List<Record> Records(byte[] entity)
    {
        var records = new List<Record>();
        var p = 0;
        while (p < entity.Length && entity[p] != 0xFF)
        {
            if (p + 3 > entity.Length || entity[p + 2] < 1 || p + 2 + entity[p + 2] > entity.Length) throw new InvalidDataException("FILE3D entity 49 has a broken record.");
            var end = p + 2 + entity[p + 2];
            var hqr = entity[p] is 1 or 3 && end - p >= 5 ? entity[p + 3] | entity[p + 4] << 8 : -1;
            records.Add(new Record(entity[p], entity[p + 1], p, end, hqr));
            p = end;
        }
        if (p >= entity.Length) throw new InvalidDataException("FILE3D entity 49 has no end marker.");
        return records;
    }

    public static Plan PlanFiles(string directory)
    {
        var bodyPath = Path.Combine(directory, "BODY.HQR");
        var entityPath = Path.Combine(directory, "FILE3D.HQR");
        if (!File.Exists(bodyPath) || !File.Exists(entityPath)) throw new InvalidDataException("BODY.HQR and FILE3D.HQR must be in the game folder.");
        var bodies = HqrFile.Parse(File.ReadAllBytes(bodyPath));
        var entities = HqrFile.Parse(File.ReadAllBytes(entityPath));

        if (Entity >= entities.Count || entities.IsEmpty(Entity)) throw new InvalidDataException("FILE3D.HQR has no Elf entity (49).");
        var entity = entities.Read(Entity);
        var records = Records(entity);
        var bodyRecords = records.Where(r => r.Type == 1).ToList();
        if (!bodyRecords.Any(r => r.Id == 41 && r.Hqr == SourceBody)) throw new InvalidDataException("FILE3D.HQR entity 49 isn't the Elf (Raymond, body 41 -> entry 88, is missing).");
        if (SourceBody >= bodies.Count || bodies.IsEmpty(SourceBody)) throw new InvalidDataException($"BODY.HQR has no entry {SourceBody}.");
        var pink = Recolour(bodies.Read(SourceBody));

        // already there (this tool ran before, or the same edit was made by hand)? then it must be exactly this body
        if (bodyRecords.FirstOrDefault(r => r.Id == BodyId) is { } existing)
        {
            if (existing.Hqr < 0 || existing.Hqr >= bodies.Count || bodies.IsEmpty(existing.Hqr) || !bodies.Read(existing.Hqr).AsSpan().SequenceEqual(pink))
                throw new InvalidDataException($"The Elf entity already has a body with id {BodyId} that isn't the pink elf. Restore BODY.HQR and FILE3D.HQR from the .bak copies and run this again.");
            return new Plan(existing.Hqr, Array.Empty<HqrEntryStore.Edit>(), Array.Empty<HqrEntryStore.TextEdit>());
        }

        var index = bodies.Count;
        if (index >= 0x8000) throw new InvalidDataException("BODY.HQR has no room for another body (the engine keeps bit 15 of a body reference for its own use).");
        var record = new byte[] { 1, BodyId, 4, (byte)(index & 255), (byte)(index >> 8), 0 };   // BODY, id, size 4, HQR index, no extra data
        var last = bodyRecords[^1];
        var newEntity = entity[..last.End].Concat(record).Concat(entity[last.End..]).ToArray();

        var files = new[]
        {
            new HqrEntryStore.Edit("BODY.HQR", index, pink),
            new HqrEntryStore.Edit("FILE3D.HQR", Entity, newEntity),
        };
        var texts = new[] { new HqrEntryStore.TextEdit(HqdWriter.SidecarName("BODY.HQR"), HqdWriter.Describe(directory, "BODY.HQR", SceneGame.Lba1, index, "Pink elf (Floppy), added by the level editor for the bedroom, scene 61")) };
        return new Plan(index, files, texts);
    }

    // ---- scene 61 ------------------------------------------------------------------------------------------------

    // Adds the pink elf to the bedroom, or (if it is there already) makes sure it speaks in pink. Returns what it did,
    // or null when the scene already had it right.
    public static string? EditRoom(SceneModel room)
    {
        var existing = room.Actors.Skip(1).FirstOrDefault(a => !a.IsSprite && a.Entity == Entity && a.Body == BodyId);
        if (existing is not null)
        {
            var did = new List<string>();
            if (existing.CoulObj != DialogueColour)
            {
                existing.CoulObj = DialogueColour;
                did.Add($"now speaks in pink (colour {DialogueColour})");
            }
            // the first version (no greeting) is brought up to date; a script anyone has changed since is left alone
            var index = room.Actors.IndexOf(existing);
            var built = Build(index);
            if (!existing.Life.AsSpan().SequenceEqual(built.Life) && existing.Life.AsSpan().SequenceEqual(Build(index, greeting: false).Life))
            {
                existing.Life = built.Life;
                did.Add("greets Twinsen");
            }
            return did.Count == 0 ? null : $"scene {RoomScene}: the pink elf " + string.Join(" and ", did);
        }
        var added = SceneOps.AddActor(room, Build(room.Actors.Count));
        return $"scene {RoomScene}: the pink elf added as actor {added}";
    }

    private const int RoomScene = 61;

    // ---- what the elf says -----------------------------------------------------------------------------------------

    // The elf's greeting: text 287 of Principal Island's dialogue (the last text of the bank, after the fisherman's question), said once, when Twinsen first comes near
    // (game flag 226: nothing in the game's own scripts uses 220..254), and again whenever Twinsen presses action within 1500 units of him.
    // Only English is written here: the other four languages hold it too until their translations are in Translations.
    public const int GreetingId = 287, GreetingFlag = 226;
    public const string GreetingEnglish = "Hi, I'm Floppy the third elf, after all these years somebody has finally found me. Please help yourself to anything you find here.";
    private const int PressLatch = 13;      // a scene variable (var_cube) held while action is down, so that one press says it once

    // The translations, by language (1 French, 2 German, 3 Spanish, 4 Italian), in the DOS code page of the game's text.
    public static readonly IReadOnlyDictionary<int, string> Translations = new Dictionary<int, string>
    {
        // Translator's line is plain ASCII throughout, so it needs no CP 850 remapping.
        [4] = "Ciao, sono Floppy, il terzo elfo! Finalmente dopo tutti questi anni qualcuno mi ha trovato. Prendi pure tutto quello che trovi qui in giro.",
    };

    public static readonly Lba1DialogueText.AddedText Greeting = new(GreetingId, "the pink elf's greeting", GreetingEnglish,
        (_, language) => Translations.TryGetValue(language, out var text) ? Lba1DialogueText.Bytes(text) : null, Own: true);

    public static SceneActorModel Build(int index, bool greeting = true)
    {
        using var scope = Opcodes.Use(Opcodes.Lba1);
        var track = TrackText.Compile("label(0);\nanim(0);\nface_twinsen();\nlabel(10);\nstop();\n");
        var says = greeting
            ? $"    if (0 == var_game({GreetingFlag}))\n    {{\n        set_var_game({GreetingFlag}, 1);\n        message({GreetingId});\n    }}\n" +
              $"    if (1 == action())\n    {{\n        if (0 == var_cube({PressLatch}))\n        {{\n            set_var_cube({PressLatch}, 1);\n            if (1500 > distance(0))\n            {{\n                message({GreetingId});\n            }}\n        }}\n    }}\n    else\n    {{\n        set_var_cube({PressLatch}, 0);\n    }}\n"
            : "";
        var life = LifeText.Compile(
            "void comportement_0()\n{\n    if (2500 > distance(0))\n    {\n        set_track(label_0);\n        set_comportement(comportement_1);\n    }\n}\n\n" +
            "void comportement_1()\n{\n" + says + "    if (3000 < distance(0))\n    {\n        set_comportement(comportement_0);\n    }\n}\n",
            index, NoSymbols.Instance);
        // set_track(label_n) is the label's byte offset in this actor's own track script
        life.ResolveExternals(r => track.Symbols.TryGetValue(r.Symbol, out var offset) ? offset : null);
        return new SceneActorModel
        {
            // the flags, hit force, speed, bonus, armour and life the game's own elves have (scene 66's Raymond):
            // CHECK_OBJ_COL | CHECK_BRICK_COL | OBJ_FALLABLE; hit by Twinsen it takes no damage
            Flags = 0x0803, Entity = Entity, Body = BodyId, Anim = 0, Sprite = 0,
            X = X, Y = Y, Z = Z, Beta = Facing, SRot = 35, Move = 0,
            HitForce = 0, OptionFlags = 0x100, Info = new[] { -1, -1, -1, -1 },
            NbBonus = 1, CoulObj = DialogueColour, Armor = 51, LifePoints = 1,
            Track = track.Bytes, Life = life.Bytes,
        };
    }
}
