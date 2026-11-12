using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NexusMods.Sdk.Settings;

namespace NexusMods.Sdk.FileExtractor;

/// <summary>
/// Settings for the file extractor.
/// </summary>
[PublicAPI]
public record FileExtractorSettings : ISettings
{
    /// <summary>
    /// Locations where mods are temporarily unpacked. Only the first entry is used.
    /// Stored as an array so it can reuse the settings UI for <see cref="ConfigurablePath"/> lists.
    /// </summary>
    public ConfigurablePath[] TempFolderLocations { get; set; } = [];

    /// <summary>
    /// Location where the temporary folder will be stored.
    /// </summary>
    [JsonIgnore]
    public ConfigurablePath TempFolderLocation => TempFolderLocations.Length > 0
        ? TempFolderLocations[0]
        : new ConfigurablePath(KnownPath.TempDirectory, "Afterburn/Temp");

    /// <inheritdoc/>
    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(CreateDefault)
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.TempFolderLocations,
                new PropertyOptions<FileExtractorSettings, ConfigurablePath[]>
                {
                    Section = Sections.General,
                    DisplayName = "Temporary Unpack Location",
                    DescriptionFactory = currentPath => $"""
                          Mods are unpacked here before being added to your library:  
                          **{currentPath[0].ToPath(FileSystem.Shared)}**
                          
                          Large collections need a lot of free space here. Pick a folder on the same drive as your Storage Location to avoid filling up C: and to skip an extra copy.
                          """,
                    RequiresRestart = true,
                },
                new ConfigurablePathsContainerOption()
            );
    }

    /// <summary>
    /// Create default.
    /// </summary>
    public static FileExtractorSettings CreateDefault(IServiceProvider serviceProvider)
    {
        var os = serviceProvider.GetRequiredService<IFileSystem>().OS;

        // Note: The idiomatic place for this is Temporary Directory (/tmp on Linux, %TEMP% on Windows)
        //       however this can be dangerous to do on Linux, as /tmp is often a RAM disk, and can be
        //       too small to handle large files.
        var baseKnownPath = os.MatchPlatform(
            onWindows: () => KnownPath.TempDirectory,
            onLinux: () => KnownPath.XDG_STATE_HOME,
            onOSX: () => KnownPath.TempDirectory
        );

        return new FileExtractorSettings
        {
            TempFolderLocations = [new ConfigurablePath(baseKnownPath, "Afterburn/Temp")],
        };
    }
}
