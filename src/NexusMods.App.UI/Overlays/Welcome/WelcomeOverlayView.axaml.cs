using Avalonia.ReactiveUI;
using R3;
using ReactiveUI;

namespace NexusMods.App.UI.Overlays;

public partial class WelcomeOverlayView : ReactiveUserControl<IWelcomeOverlayViewModel>
{
    public WelcomeOverlayView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.BindCommand(ViewModel, vm => vm.CommandOpenDiscord, view => view.ButtonOpenDiscord)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandOpenForum, view => view.ButtonOpenForum)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandOpenGitHub, view => view.ButtonOpenGitHub)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandLogIn, view => view.ButtonLogIn)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandLogOut, view => view.ButtonLogOut)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandClose, view => view.ButtonClose)
                .AddTo(disposables);

            this.WhenAnyValue(view => view.ViewModel!.IsLoggedIn.Value)
                .Subscribe(isLoggedIn =>
                {
                    // Afterburn: Nexus Mods login is unavailable until Afterburn is approved; use the API key button in the top bar.
                    ButtonLogIn.IsVisible = false;
                    ButtonLogOut.IsVisible = isLoggedIn;
                    ButtonClose.Text = isLoggedIn ? "Close" : "Guest";
                })
                .AddTo(disposables);
        });
    }
}

