using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.Issues;

/// <summary>
/// Afterburn: the Issues page (full details about file conflicts, Health Check problems and mod-log errors).
/// </summary>
[JsonName("NexusMods.App.UI.Pages.Issues.IssuesPageContext")]
public record IssuesPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class IssuesPageFactory : APageFactory<IIssuesPageViewModel, IssuesPageContext>
{
    public IssuesPageFactory(IServiceProvider serviceProvider) : base(serviceProvider) { }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("c2d7a915-4e6b-4f38-9a1e-7b52d0e4f3a1"));
    public override PageFactoryId Id => StaticId;

    public override IIssuesPageViewModel CreateViewModel(IssuesPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IIssuesPageViewModel>();
        vm.LoadoutId = context.LoadoutId;
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = "Issues",
            Icon = IconValues.WarningAmber,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new IssuesPageContext { LoadoutId = loadoutContext.LoadoutId },
            },
        };
    }
}
