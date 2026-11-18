using Avalonia.ReactiveUI;
using JetBrains.Annotations;

namespace NexusMods.App.UI.Pages.Storage;

[UsedImplicitly]
public partial class StoragePageView : ReactiveUserControl<IStoragePageViewModel>
{
    public StoragePageView()
    {
        InitializeComponent();
    }
}
