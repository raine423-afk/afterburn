using System.Diagnostics;
using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.App.Commandline;


/// <summary>
/// These verbs are used for checking the status of the Nexus Mods App.
/// </summary>
public static class StatusVerbs
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    internal static IServiceCollection AddStatusVerbs(this IServiceCollection services) =>
        services.AddVerb(() => Heartbeat)
            .AddVerb(() => ShowWindow);

    public const string ShowWindowCommand = "show-window";

    /// <summary>
    /// Afterburn: asks the running Afterburn to show its window. Used when Afterburn is started a second time.
    /// </summary>
    [Verb(ShowWindowCommand, "Shows the window of the running Afterburn.")]
    private static Task<int> ShowWindow([Injected] NexusMods.Sdk.EventBus.IEventBus eventBus)
    {
        eventBus.Send(new NexusMods.CLI.CliMessages.ShowMainWindow());
        return Task.FromResult(0);
    }

    public const string HeartbeatCommand = "heartbeat";
    /// <summary>
    /// Returns the processId and the uptime of the Nexus Mods app process.
    /// </summary>
    /// <param name="renderer"></param>
    /// <returns></returns>
    [Verb(HeartbeatCommand, "Returns process uptime for Afterburn.")]
    private static async Task<int> Heartbeat([Injected] IRenderer renderer)
    {
        var process = Process.GetCurrentProcess();
        
        var startTime = process.StartTime;
        var uptime = DateTime.Now - startTime;
        
        await renderer.TextLine($"ProcessId: {process.Id} Uptime: {uptime.Humanize()}");

        return 0;
    }
}
