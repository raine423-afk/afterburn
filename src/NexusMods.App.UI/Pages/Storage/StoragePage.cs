using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Ids;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.Storage;

/// <summary>
/// Afterburn: the Storage page (disk usage, clean-up, and the game-folder files Afterburn didn't install).
/// </summary>
[JsonName("NexusMods.App.UI.Pages.Storage.StoragePageContext")]
public record StoragePageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class StoragePageFactory : APageFactory<IStoragePageViewModel, StoragePageContext>
{
    public StoragePageFactory(IServiceProvider serviceProvider) : base(serviceProvider) { }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("5b3f2e8a-9c41-4d7e-b6a2-1f0c8d3e7a55"));
    public override PageFactoryId Id => StaticId;

    public override IStoragePageViewModel CreateViewModel(StoragePageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IStoragePageViewModel>();
        vm.LoadoutId = context.LoadoutId;
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = "Storage",
            Icon = IconValues.FolderEditOutline,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new StoragePageContext { LoadoutId = loadoutContext.LoadoutId },
            },
        };
    }
}
