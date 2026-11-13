using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NexusMods.Sdk.Settings;

namespace NexusMods.Sdk;

/// <summary>
/// Afterburn: where crash logs are saved. Defaults to the Downloads folder.
/// </summary>
[PublicAPI]
public record CrashLogSettings : ISettings
{
    /// <summary>Only the first entry is used. Stored as an array so it can reuse the folder picker in Settings.</summary>
    public ConfigurablePath[] CrashLogFolders { get; set; } = [];

    [JsonIgnore]
    public ConfigurablePath CrashLogFolder => CrashLogFolders.Length > 0
        ? CrashLogFolders[0]
        : new ConfigurablePath(KnownPath.HomeDirectory, "Downloads");

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(CreateDefault)
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.CrashLogFolders,
                new PropertyOptions<CrashLogSettings, ConfigurablePath[]>
                {
                    Section = Sections.General,
                    DisplayName = "Crash Log Folder",
                    DescriptionFactory = currentPath => $"""
                          If Afterburn crashes, a text crash log is saved here so you can copy it or send it to the developer:  
                          **{currentPath[0].ToPath(FileSystem.Shared)}**
                          """,
                    RequiresRestart = true,
                },
                new ConfigurablePathsContainerOption()
            );
    }

    public static CrashLogSettings CreateDefault(IServiceProvider serviceProvider)
    {
        return new CrashLogSettings
        {
            CrashLogFolders = [new ConfigurablePath(KnownPath.HomeDirectory, "Downloads")],
        };
    }
}