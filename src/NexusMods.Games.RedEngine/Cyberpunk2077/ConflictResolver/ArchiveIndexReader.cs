namespace NexusMods.Games.RedEngine.Cyberpunk2077.ConflictResolver;

/// <summary>
/// Afterburn: reads only the resource-hash table of a Cyberpunk 2077 .archive (RDAR) file, without reading any
/// resource data, so checking hundreds of mods stays fast.
/// </summary>
internal static class ArchiveIndexReader
{
    private const uint Magic = 0x52414452; // "RDAR"
    private const int HeaderSize = 40;
    private const int IndexHeaderSize = 32;
    private const int FileEntrySize = 56;

    /// <summary>Returns the 64-bit name hashes of every resource in the archive, or null if it can't be read.</summary>
    public static HashSet<ulong>? TryReadResourceHashes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            using var reader = new BinaryReader(stream);
            if (stream.Length < HeaderSize) return null;

            if (reader.ReadUInt32() != Magic) return null;
            _ = reader.ReadUInt32();                 // version
            var indexPosition = reader.ReadUInt64();
            _ = reader.ReadUInt32();                 // index size

            if (indexPosition + IndexHeaderSize > (ulong)stream.Length) return null;
            stream.Position = (long)indexPosition;

            _ = reader.ReadUInt32();                 // file table offset
            _ = reader.ReadUInt32();                 // file table size
            _ = reader.ReadUInt64();                 // crc
            var entryCount = reader.ReadUInt32();
            _ = reader.ReadUInt32();                 // segment count
            _ = reader.ReadUInt32();                 // dependency count

            if (entryCount > 5_000_000) return null;
            if (stream.Position + (long)entryCount * FileEntrySize > stream.Length) return null;

            var hashes = new HashSet<ulong>((int)entryCount);
            var entry = new byte[FileEntrySize];
            for (var i = 0; i < entryCount; i++)
            {
                if (stream.Read(entry, 0, FileEntrySize) != FileEntrySize) return null;
                hashes.Add(BitConverter.ToUInt64(entry, 0));
            }

            return hashes;
        }
        catch
        {
            return null;
        }
    }
}
