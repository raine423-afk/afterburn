using Microsoft.Extensions.Logging;
using NexusMods.Backend.FileExtractor.Extractors;
using NexusMods.Backend.FileExtractor.FileSignatures;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.IO;

namespace NexusMods.Backend.FileExtractor;

/// <summary>
/// Utility used for extracting files.
/// This utility is usually created via DI container.
/// See <see cref="Services.AddFileExtractors"/>; and then ask the DI container nicely to give you an instance of this :)
/// </summary>
public class FileExtractor : IFileExtractor
{
    /// <summary>
    /// Used to check for file types by scanning file header signatures.
    /// </summary>
    // ReSharper disable once MemberCanBePrivate.Global
    public SignatureChecker SignatureChecker { get; }

    private readonly ILogger<FileExtractor> _logger;
    private readonly IEnumerable<IExtractor> _extractors;

    /// <summary>
    /// Creates a new instance of a File Extractor
    /// </summary>
    /// <param name="logger">Logger used to report back.</param>
    /// <param name="extractors">Extractors used to unpack files to disk.</param>
    /// <remarks>
    ///    This method is usually called via DI container.
    ///    See <see cref="Services.AddFileExtractors"/>; and then ask the DI container nicely to give you an instance of this.
    /// </remarks>
    public FileExtractor(ILogger<FileExtractor> logger, IEnumerable<IExtractor> extractors)
    {
        _logger = logger;
        _extractors = extractors.ToArray();
        SignatureChecker = new SignatureChecker(_extractors.SelectMany(e => e.SupportedSignatures).Distinct().ToArray());
    }

    /// <summary>
    /// Extracts all files to disk in an asynchronous fashion.
    /// </summary>
    /// <param name="path">The path to the source file.</param>
    /// <param name="dest">The folder where the file is to be extracted.</param>
    /// <param name="token">Used for cancellation of the operation.</param>
    public async Task ExtractAllAsync(AbsolutePath path, AbsolutePath dest, CancellationToken token = default)
    {
        CheckFreeSpace(path, dest);
        await ExtractAllAsync(new NativeFileStreamFactory(path), dest, token);
    }

    /// <summary>
    /// Extracts all files to disk in an asynchronous fashion.
    /// </summary>
    /// <param name="sFn">The source stream.</param>
    /// <param name="dest">Destination file path.</param>
    /// <param name="token">Used for cancellation of the operation.</param>
    /// <exception cref="FileExtractionException"></exception>
    public async Task ExtractAllAsync(IStreamFactory sFn, AbsolutePath dest, CancellationToken token = default)
    {
        var extractors = await FindExtractors(sFn);
        foreach (var extractor in extractors)
        {
            try
            {
                await extractor.ExtractAllAsync(sFn, dest, token);
                return;
            }
            catch (PathException e)
            {
                _logger.LogError(e, "Path exception while extracting via {Extractor}", extractor);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "While extracting via {Extractor}", extractor);
                if (IsDiskFull(ex)) throw new FileExtractionException($"Not enough free disk space to unpack {sFn.FileName}. Free up space on the drive that holds the temp folder and try again.");
            }
        }

        throw new FileExtractionException($"No Extractors found for file {sFn.FileName}");
    }

    /// <inheritdoc/>
    public ValueTask<bool> CanExtract(AbsolutePath path)
    {
        return CanExtract(new NativeFileStreamFactory(path));
    }

    /// <inheritdoc/>
    public async ValueTask<bool> CanExtract(IStreamFactory sFn)
    {
        return await CanExtract(await sFn.GetStreamAsync());
    }

    /// <inheritdoc/>
    public async ValueTask<bool> CanExtract(Stream stream)
    {
        return await SignatureChecker.MatchesAnyAsync(stream);
    }

    private static bool IsDiskFull(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is IOException io && ((io.HResult & 0xFFFF) == 0x70 || (io.HResult & 0xFFFF) == 0x27)) return true;
        }
        return false;
    }

    // Stops early, with a clear message, when the drive is almost full, instead of failing halfway through unpacking.
    private static void CheckFreeSpace(AbsolutePath source, AbsolutePath dest)
    {
        try
        {
            var size = new System.IO.FileInfo(source.GetFullPath()).Length;
            var root = System.IO.Path.GetPathRoot(dest.GetFullPath());
            if (string.IsNullOrEmpty(root)) return;
            var free = new System.IO.DriveInfo(root).AvailableFreeSpace;
            if (free < size * 2)
                throw new FileExtractionException($"Only {free / 1048576} MB free on {root}, which is too little to unpack {System.IO.Path.GetFileName(source.GetFullPath())} ({size / 1048576} MB download). Free up space and try again.");
        }
        catch (FileExtractionException) { throw; }
        catch { /* can't tell, carry on */ }
    }
    private async ValueTask<IExtractor[]> FindExtractors(IStreamFactory sFn)
    {
        await using var archive = await sFn.GetStreamAsync();
        var sig = await SignatureChecker.MatchesAsync(archive);

        return _extractors.Select(e => (Extractor: e, Result: e.DeterminePriority(sig)))
            .Where(e => e.Result != Priority.None)
            .OrderBy(e => e.Result)
            .Select(e => e.Extractor)
            .ToArray();
    }
}
