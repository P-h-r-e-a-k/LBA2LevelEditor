using System.Buffers.Binary;
using System.IO;

namespace LBAAssembler.Lba1;

// Structural edits to an LBA1 scene record (the size changes, unlike Lba1ActorRecord's in-place patches).
internal static class Lba1SceneEdit
{
    // A copy of `record` with a new actor appended after the last one (so every existing actor keeps its number,
    // which other scripts refer to). The engine holds at most 100 actors (MAX_OBJETS). `header` is the 35-byte
    // attribute header (layout in Lba1Scene.Parse), followed in the record by the track and life scripts.
    public static byte[] AddActor(byte[] record, byte[] header, byte[] track, byte[] life)
    {
        var scene = Lba1Scene.Parse(0, record);
        if (scene.BytesParsed != record.Length) throw new InvalidDataException("The scene record has bytes this editor doesn't understand; refusing to rewrite it.");
        if (header.Length != 35) throw new ArgumentException("An LBA1 actor header is 35 bytes.", nameof(header));
        if (scene.Actors.Count >= 100) throw new InvalidDataException("The scene already has the most actors the engine allows (100).");

        var insertAt = scene.ZoneCountOffset;
        var block = new byte[header.Length + 2 + track.Length + 2 + life.Length];
        header.CopyTo(block, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(header.Length), checked((ushort)track.Length));
        track.CopyTo(block, header.Length + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(header.Length + 2 + track.Length), checked((ushort)life.Length));
        life.CopyTo(block, header.Length + 2 + track.Length + 2);

        var result = new byte[record.Length + block.Length];
        record.AsSpan(0, insertAt).CopyTo(result);
        block.CopyTo(result.AsSpan(insertAt));
        record.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + block.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(scene.ActorCountOffset), (ushort)(scene.Actors.Count + 1));
        return result;
    }

    // A copy of `record` with `zone` appended to its zone list (the count is bumped; the track points that
    // follow the zones simply move up). Coordinates are world units; Info/Snap as in Lba1Zone.
    public static byte[] AddZone(byte[] record, Lba1Zone zone)
    {
        var scene = Lba1Scene.Parse(0, record);
        if (scene.BytesParsed != record.Length) throw new InvalidDataException("The scene record has bytes this editor doesn't understand; refusing to rewrite it.");
        if (zone.Info.Length != 4) throw new ArgumentException("An LBA1 zone has four info words.", nameof(zone));

        var count = scene.Zones.Count;
        var insertAt = scene.ZoneCountOffset + 2 + count * 24;
        var bytes = new byte[24];
        var p = 0;
        void S16(int v) { BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(p), checked((short)v)); p += 2; }
        S16(zone.X0); S16(zone.Y0); S16(zone.Z0); S16(zone.X1); S16(zone.Y1); S16(zone.Z1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(p), (ushort)zone.Type); p += 2;
        foreach (var info in zone.Info) S16(info);
        S16(zone.Snap);

        var result = new byte[record.Length + 24];
        record.AsSpan(0, insertAt).CopyTo(result);
        bytes.CopyTo(result.AsSpan(insertAt));
        record.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + 24));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(scene.ZoneCountOffset), (ushort)(count + 1));
        return result;
    }
}
