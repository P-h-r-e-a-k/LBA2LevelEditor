using System.Text;
using System.Xml;
using LBA2LevelEditor.Lba1;

namespace ScriptRoundTrip;

// elfxliff <out folder>: the pink elf's greeting (Lba1PinkElf.GreetingEnglish, text 287 of Principal Island's dialogue) as XLIFF 1.2, one file per
// language the game has besides English (French, German, Spanish, Italian), the targets empty, for a translator. The translations go back into
// Lba1PinkElf.Translations (by language number: 1 French, 2 German, 3 Spanish, 4 Italian).
internal static class ElfXliff
{
    private static readonly (string Code, string Name, int Language)[] Targets =
    {
        ("fr", "French", 1), ("de", "German", 2), ("es", "Spanish", 3), ("it", "Italian", 4),
    };

    public static int Run(string[] args)
    {
        var folder = args.Length > 1 ? args[1] : "translations";
        Directory.CreateDirectory(folder);
        foreach (var (code, name, language) in Targets)
        {
            var path = Path.Combine(folder, $"lba1-floppy-greeting.{code}.xlf");
            Write(path, code, name);
            Console.WriteLine($"{path} (game language {language}: {name})");
        }
        return 0;
    }

    public static void Write(string path, string code, string name)
    {
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false), NewLineChars = "\n" };
        using var w = XmlWriter.Create(path, settings);
        const string ns = "urn:oasis:names:tc:xliff:document:1.2";
        w.WriteStartDocument();
        w.WriteStartElement("xliff", ns);
        w.WriteAttributeString("version", "1.2");
        w.WriteStartElement("file", ns);
        w.WriteAttributeString("original", "Little Big Adventure 1, TEXT.HQR: Principal Island dialogue, text 287");
        w.WriteAttributeString("source-language", "en");
        w.WriteAttributeString("target-language", code);
        w.WriteAttributeString("datatype", "plaintext");
        w.WriteStartElement("header", ns);
        w.WriteElementString("note", ns,
            $"Little Big Adventure (1994), the original game: one line of dialogue to translate into {name}. It is spoken by Floppy, a pink elf who lives in a hidden room " +
            "(the game's other elves are Joe and Raymond), to Twinsen, the hero, the first time Twinsen walks into that room, and again whenever Twinsen talks to him. " +
            "The tone is friendly and a little wistful: he has been alone in there for a very long time.");
        w.WriteEndElement();    // header
        w.WriteStartElement("body", ns);
        w.WriteStartElement("trans-unit", ns);
        w.WriteAttributeString("id", "lba1-island1-287");
        w.WriteAttributeString("resname", "floppy-greeting");
        w.WriteStartElement("source", ns);
        w.WriteAttributeString("xml", "lang", null, "en");
        w.WriteString(Lba1PinkElf.GreetingEnglish);
        w.WriteEndElement();
        w.WriteStartElement("target", ns);
        w.WriteAttributeString("xml", "lang", null, code);
        w.WriteAttributeString("state", "needs-translation");
        w.WriteEndElement();
        w.WriteStartElement("note", ns);
        w.WriteAttributeString("from", "developer");
        w.WriteAttributeString("priority", "1");
        w.WriteString(
            "Keep the names Floppy and Twinsen as they are. \"Floppy the third elf\" means the third of the elves (after Joe and Raymond). " +
            "Please keep it about as long as the English (under 200 characters) and as ONE line: no line breaks, and no @ character (the game reads it as a line or page break). " +
            "The game's font only has the letters of the old DOS Western European code page (CP 850): use the normal accented letters of the language, but write quotation marks " +
            "and apostrophes as plain straight ' and \", and avoid typographic dashes, the ellipsis character, the euro sign and emoji.");
        w.WriteEndElement();
        w.WriteEndElement();    // trans-unit
        w.WriteEndElement();    // body
        w.WriteEndElement();    // file
        w.WriteEndElement();    // xliff
        w.WriteEndDocument();
    }
}
