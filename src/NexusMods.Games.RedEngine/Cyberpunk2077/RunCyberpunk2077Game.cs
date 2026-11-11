using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.RedEngine.Cyberpunk2077.ConflictResolver;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

public class RunCyberpunk2077Game(IServiceProvider provider, Cyberpunk2077Game game) : RunGameTool<Cyberpunk2077Game>(provider, game)
{
    public override async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken, string[]? commandLineArgs)
    {
        if (commandLineArgs == null)
        {
            commandLineArgs = ["-modded"];
        }
        else
        {
            commandLineArgs = commandLineArgs.Append("-modded").ToArray();
        }

        // Afterburn: automatic pre-launch conflict check. Never blocks the launch if it fails.
        await RunConflictResolver(loadout, cancellationToken);

        await base.Execute(loadout, cancellationToken, commandLineArgs);
    }

    private async Task RunConflictResolver(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        try
        {
            var resolver = provider.GetService<CyberpunkConflictResolver>();
            if (resolver is null) return;

            var summary = await resolver.RunAsync(loadout, cancellationToken);
            if (!summary.ShouldNotify) return;

            var notifications = provider.GetService<IWindowNotificationService>();
            if (notifications is null) return;

            var text = summary.ToShortText();
            var variant = summary.BrokenMods.Count > 0 ? ToastNotificationVariant.Failure : ToastNotificationVariant.Success;
            Dispatcher.UIThread.Post(() => notifications.ShowToast(text, variant, TimeSpan.FromSeconds(12)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            provider.GetService<ILogger<RunCyberpunk2077Game>>()?.LogWarning(e, "Afterburn conflict check failed; launching anyway");
        }
    }
}
