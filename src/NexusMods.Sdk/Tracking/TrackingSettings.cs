using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Sdk.Settings;

namespace NexusMods.Sdk.Tracking;

public record TrackingSettings : ISettings
{
    public static readonly Uri Link = new("https://help.nexusmods.com/article/20-privacy-policy");

    public bool EnableTracking { get; [UsedImplicitly] set; }

    public Guid DeviceId { get; set; }

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureDefault(CreateDefault);
    }

    private static TrackingSettings CreateDefault(IServiceProvider serviceProvider)
    {
        var timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;

        return new TrackingSettings
        {
            EnableTracking = false,
            DeviceId = Guid.CreateVersion7(timeProvider.GetUtcNow()),
        };
    }
}
