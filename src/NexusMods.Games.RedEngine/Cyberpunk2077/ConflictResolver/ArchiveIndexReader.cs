using System.Buffers;

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
    private const int EntriesPerChunk = 1024;

    /// <summary>
    /// Returns the 64-bit name hashes of every resource in the archive, sorted and without duplicates, or null if it
    /// can't be read. Only the entry table is read, in small chunks through one pooled buffer.
    /// </summary>
    public static ulong[]? TryReadResourceHashes(string path)
    {
        byte[]? buffer = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
            if (stream.Length < HeaderSize) return null;

            Span<byte> header = stackalloc byte[HeaderSize];
            stream.ReadExactly(header);
            if (BitConverter.ToUInt32(header[..4]) != Magic) return null;
            var indexPosition = BitConverter.ToUInt64(header.Slice(8, 8));

            if (indexPosition + IndexHeaderSize > (ulong)stream.Length) return null;
            stream.Position = (long)indexPosition;

            Span<byte> indexHeader = stackalloc byte[IndexHeaderSize];
            stream.ReadExactly(indexHeader);
            var entryCount = BitConverter.ToUInt32(indexHeader.Slice(16, 4));

            if (entryCount > 5_000_000) return null;
            var tableSize = (long)entryCount * FileEntrySize;
            if (stream.Position + tableSize > stream.Length) return null;

            // Read the table in small chunks, keeping only the 8-byte hash of each 56-byte entry.
            buffer = ArrayPool<byte>.Shared.Rent(EntriesPerChunk * FileEntrySize);
            var hashes = new ulong[entryCount];
            for (var done = 0; done < hashes.Length;)
            {
                var count = Math.Min(EntriesPerChunk, hashes.Length - done);
                stream.ReadExactly(buffer, 0, count * FileEntrySize);
                for (var i = 0; i < count; i++)
                    hashes[done + i] = BitConverter.ToUInt64(buffer, i * FileEntrySize);
                done += count;
            }

            Array.Sort(hashes);
            var unique = 0;
            for (var i = 0; i < hashes.Length; i++)
                if (i == 0 || hashes[i] != hashes[unique - 1]) hashes[unique++] = hashes[i];
            return unique == hashes.Length ? hashes : hashes[..unique];
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
