using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GC;
using NexusMods.Sdk.Settings;
using NexusMods.App.GarbageCollection.DataModel;
using NexusMods.CrossPlatform;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;

namespace NexusMods.DataModel;

/// <inheritdoc />
public class GarbageCollectorRunner(ISettingsManager settings, NxFileStore store, IConnection connection, ILogger<GarbageCollectorRunner> logger) : IGarbageCollectorRunner
{
    private readonly DataModelSettings _settings = settings.Get<DataModelSettings>();
    private readonly NxFileStore _store = store;
    private readonly IConnection _connection = connection;
    private readonly ILogger<GarbageCollectorRunner> _logger = logger;

    // Afterburn: the clean-up locks the file store for its whole run, so installs and applies wait for it. Background
    // runs are therefore held back until nothing has asked for one for a while, and several requests become one run.
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(3);
    private readonly object _scheduleGate = new();
    private long _lastRequestTicks;
    private bool _scheduled;

    private void ScheduleWhenIdle()
    {
        Interlocked.Exchange(ref _lastRequestTicks, Environment.TickCount64);
        lock (_scheduleGate)
        {
            if (_scheduled) return;
            _scheduled = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var waited = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastRequestTicks));
                    if (waited >= IdleDelay) break;
                    await Task.Delay(IdleDelay - waited);
                }

                // Requests that arrive from here on schedule another run after this one.
                lock (_scheduleGate) _scheduled = false;
                Run();
            }
            catch (Exception e)
            {
                lock (_scheduleGate) _scheduled = false;
                _logger.LogError(e, "The background storage clean-up failed");
            }
        });
    }

    /// <inheritdoc />
    public void Run()
    {
        RunGarbageCollector.Do(logger, _settings.ArchiveLocations, _store, _connection);
    }
    
    /// <inheritdoc />
    public Task RunAsync()
    {
        return Task.Run(Run);
    }
    
    /// <inheritdoc />
    public async Task RunWithMode(GarbageCollectorRunMode gcRunMode)
    {
        switch (gcRunMode)
        {
            case GarbageCollectorRunMode.RunSynchronously:
                Run();
                break;
            case GarbageCollectorRunMode.RunAsyncInBackground:
                ScheduleWhenIdle();
                break;
            case GarbageCollectorRunMode.RunAsynchronously:
                await RunAsync();
                break;
            case GarbageCollectorRunMode.DoNotRun:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(gcRunMode), gcRunMode, null);
        }
    }
}
