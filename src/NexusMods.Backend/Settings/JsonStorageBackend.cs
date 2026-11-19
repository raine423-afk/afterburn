using System.Text;
using System.Text.Json;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk.Settings;

namespace NexusMods.Backend;

[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
public sealed class JsonStorageBackend : IStorageBackend
{
    public StorageBackendId Id { get; } = StorageBackends.Json;

    private readonly ILogger _logger;
    private readonly Lazy<JsonSerializerOptions> _jsonOptions;
    private readonly AbsolutePath _configDirectory;

    /// <summary>
    /// Constructor.
    /// </summary>
    public JsonStorageBackend(
        ILogger<JsonStorageBackend> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _jsonOptions = new Lazy<JsonSerializerOptions>(serviceProvider.GetRequiredService<JsonSerializerOptions>);

        var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
        _configDirectory = GetConfigsFolderPath(fileSystem);
        _configDirectory.CreateDirectory();
    }

    private static AbsolutePath GetConfigsFolderPath(IFileSystem fileSystem)
    {
        var os = fileSystem.OS;
        var baseKnownPath = os.MatchPlatform(
            onWindows: () => KnownPath.LocalApplicationDataDirectory,
            onLinux: () => KnownPath.XDG_DATA_HOME,
            onOSX: () => KnownPath.LocalApplicationDataDirectory
        );

        // NOTE: OSX ".App" is apparently special, using _ instead of . to prevent weirdness
        // Afterburn: settings live with the rest of Afterburn's data. Program.MigrateLegacyConfigs copies any settings
        // left in the old NexusMods.App/Configs folder across on first start.
        var baseDirectoryName = "Afterburn/Configs";
        return fileSystem.GetKnownPath(baseKnownPath).Combine(baseDirectoryName);
    }

    private AbsolutePath GetConfigPath<T>(string? key)
    {
        var typeName = typeof(T).FullName ?? typeof(T).Name;
        var fileName = string.IsNullOrWhiteSpace(key) ? $"{typeName}.json" : $"{typeName}_{key}.json";
        return _configDirectory.Combine(fileName);
    }

    private T? Deserialize<T>(string json) where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, _jsonOptions.Value);
            return value;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while deserializing to `{Type}` with JSON `{Json}`", typeof(T), json);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Save<T>(T value, string? key) where T : class, ISettings, new()
    {
        var configPath = GetConfigPath<T>(key);

        // Afterburn: write to a temporary file and swap it in, so a crash or a failed save mid-write can't
        // leave an empty settings file behind (which reset that setting to its default on the next start).
        var tempPath = configPath.Parent.Combine(configPath.FileName + ".tmp");
        try
        {
            using (var stream = tempPath.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                if (!TrySerialize(stream, value)) return;
                if (stream is FileStream fileStream) fileStream.Flush(flushToDisk: true);
                else stream.Flush();
            }

            tempPath.FileSystem.MoveFile(tempPath, configPath, true);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't save settings `{Type}`", typeof(T));
        }
        finally
        {
            try { if (tempPath.FileExists) tempPath.Delete(); }
            catch { /* best effort */ }
        }
    }

    private bool TrySerialize<T>(Stream stream, T value) where T : class
    {
        try
        {
            JsonSerializer.Serialize(stream, value, _jsonOptions.Value);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while serializing `{Type}` with value `{Value}`", typeof(T), value);
            return false;
        }
    }

    /// <inheritdoc/>
    public T? Load<T>(string? key) where T : class, ISettings, new()
    {
        var configPath = GetConfigPath<T>(key);
        if (!configPath.FileExists) return null;

        using var stream = configPath.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sr = new StreamReader(stream, Encoding.UTF8);

        var json = sr.ReadToEnd();
        return Deserialize<T>(json);
    }

    /// <inheritdoc/>
    public void Dispose() {}
}
