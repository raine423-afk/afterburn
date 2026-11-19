using NexusMods.Abstractions.Steam;
using NexusMods.Paths;

namespace NexusMods.Networking.Steam;


internal class AppDirectoryAuthStorage(IFileSystem fileSystem) : IAuthStorage
{
    private readonly AbsolutePath _storagePath = fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory)
                                                 / (fileSystem.OS.IsOSX ? "NexusMods_App" : "Afterburn")
                                                 / "steam/auth";
    
    public async Task<(bool Success, byte[] Data)> TryLoad()
    {
        try
        {
            if (!_storagePath.FileExists)
                return (false, []);

            return (true, await _storagePath.ReadAllBytesAsync());
        }
        catch
        {
            return (false, []);
        }

    }

    public async Task SaveAsync(byte[] data)
    {
        if (!_storagePath.Parent.DirectoryExists())
            _storagePath.Parent.CreateDirectory();

        // Afterburn: the file holds a Steam refresh token, so on Linux/macOS create it readable by this user only.
        if (OperatingSystem.IsWindows())
        {
            await _storagePath.WriteAllBytesAsync(data);
            return;
        }

        var nativePath = _storagePath.ToNativeSeparators(fileSystem.OS);
        await using (var stream = new FileStream(nativePath, new FileStreamOptions
                     {
                         Mode = FileMode.Create,
                         Access = FileAccess.Write,
                         UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                     }))
        {
            await stream.WriteAsync(data);
        }
        File.SetUnixFileMode(nativePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
