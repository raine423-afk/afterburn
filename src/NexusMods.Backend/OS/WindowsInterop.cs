using System.Diagnostics;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk;

namespace NexusMods.Backend.OS;

internal partial class WindowsInterop : IOSInterop
{
    private readonly IOSInformation _os;
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly IProcessRunner _processRunner;

    public WindowsInterop(IServiceProvider serviceProvider)
    {
        _os = serviceProvider.GetRequiredService<IOSInformation>();
        Debug.Assert(_os.IsWindows, "this interop only supports Windows");

        _logger = serviceProvider.GetRequiredService<ILogger<WindowsInterop>>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
        _processRunner = serviceProvider.GetRequiredService<IProcessRunner>();
    }

    public AbsolutePath GetRunningExecutablePath(out string rawPath)
    {
        rawPath = Environment.ProcessPath ?? throw new NotSupportedException("Unable to get process path");
        return _fileSystem.FromUnsanitizedFullPath(rawPath);
    }

    /// <summary>
    /// Afterburn: open a link or file with its associated app via the Windows shell directly, without going through
    /// cmd.exe (which would expand %VARIABLES% and interpret special characters in the target).
    /// </summary>
    private void ShellOpen(string target)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to open `{Target}`", target);
        }
    }


    public void OpenUri(Uri uri)
    {
        Debug.Assert(!uri.IsFile, $"use {nameof(OpenFile)} for opening file `{uri}`");
        if (!UriGuard.IsAllowed(uri))
        {
            _logger.LogWarning("Refusing to open a link with an unsupported scheme: `{Scheme}`", uri.IsAbsoluteUri ? uri.Scheme : "(relative)");
            return;
        }

        ShellOpen(uri.AbsoluteUri);
    }

    public void OpenFile(AbsolutePath filePath)
    {
        if (!Helper.AssertIsFile(filePath, _logger)) return;

        var nativePath = filePath.ToNativeSeparators(_fileSystem.OS);
        ShellOpen(nativePath);
    }

    public void OpenDirectory(AbsolutePath directoryPath)
    {
        if (!Helper.AssertIsDirectory(directoryPath, _logger)) return;

        // NOTE(erri120): Calling `explorer.exe` directly will return a stub process that
        // exists immediately. The process lifetime isn't tied to the window.
        // https://ss64.com/nt/explorer.html
        var nativePath = directoryPath.ToNativeSeparators(_fileSystem.OS);
        var command = Cli.Wrap("explorer.exe").WithArguments($@"""{nativePath}""")
            .WithValidation(CommandResultValidation.None);
        _processRunner.Run(command, logOutput: false);
    }

    public void OpenFileInDirectory(AbsolutePath filePath)
    {
        // NOTE(erri120): Calling `explorer.exe` directly will return a stub process that
        // exists immediately. The process lifetime isn't tied to the window.
        // https://ss64.com/nt/explorer.html
        var nativePath = filePath.ToNativeSeparators(_fileSystem.OS);
        var command = Cli.Wrap("explorer.exe").WithArguments($@"/select,""{nativePath}""")
            .WithValidation(CommandResultValidation.None);
        _processRunner.Run(command, logOutput: false);
    }

    private FileSystemMount[] GetFileSystemMountsImpl()
    {
        var mounts = DriveInfo
            .GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed)
            .Select(drive => new FileSystemMount(
                Source: drive.Name,
                Target: _fileSystem.FromUnsanitizedFullPath(drive.RootDirectory.FullName),
                Type: drive.DriveFormat,
                BytesTotal: Size.FromLong(drive.TotalSize),
                BytesAvailable: Size.FromLong(drive.AvailableFreeSpace)
            ))
            .ToArray();

        return mounts;
    }

    public ValueTask<FileSystemMount[]> GetFileSystemMounts(CancellationToken cancellationToken = default)
    {
        var mounts = GetFileSystemMountsImpl();
        return new ValueTask<FileSystemMount[]>(mounts);
    }

    public ValueTask<FileSystemMount?> GetFileSystemMount(AbsolutePath path, CancellationToken cancellationToken = default)
    {
        var mounts = GetFileSystemMountsImpl();
        var target = mounts.FirstOrDefault(mount => path.InFolder(mount.Target));
        return new ValueTask<FileSystemMount?>(target);
    }
}
