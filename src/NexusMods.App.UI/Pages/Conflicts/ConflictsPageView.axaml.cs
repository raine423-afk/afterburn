using Avalonia.ReactiveUI;
using JetBrains.Annotations;

namespace NexusMods.App.UI.Pages.Conflicts;

[UsedImplicitly]
public partial class ConflictsPageView : ReactiveUserControl<IConflictsPageViewModel>
{
    public ConflictsPageView()
    {
        InitializeComponent();
    }
}
