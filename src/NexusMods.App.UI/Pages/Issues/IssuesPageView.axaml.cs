using Avalonia.ReactiveUI;
using JetBrains.Annotations;

namespace NexusMods.App.UI.Pages.Issues;

[UsedImplicitly]
public partial class IssuesPageView : ReactiveUserControl<IIssuesPageViewModel>
{
    public IssuesPageView()
    {
        InitializeComponent();
    }
}
