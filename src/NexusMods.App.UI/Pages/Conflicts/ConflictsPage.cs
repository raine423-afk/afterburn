using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.Conflicts;

/// <summary>
/// Afterburn: the Conflicts page (full debugging details for every file conflict).
/// </summary>
[JsonName("NexusMods.App.UI.Pages.Conflicts.ConflictsPageContext")]
public record ConflictsPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class ConflictsPageFactory : APageFactory<IConflictsPageViewModel, ConflictsPageContext>
{
    public ConflictsPageFactory(IServiceProvider serviceProvider) : base(serviceProvider) { }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("8f4b2c61-3d9e-4a7f-b0c5-6e1d2a9f7b43"));
    public override PageFactoryId Id => StaticId;

    public override IConflictsPageViewModel CreateViewModel(ConflictsPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IConflictsPageViewModel>();
        vm.LoadoutId = context.LoadoutId;
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = "Conflicts",
            Icon = IconValues.Swap,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new ConflictsPageContext { LoadoutId = loadoutContext.LoadoutId },
            },
        };
    }
}
