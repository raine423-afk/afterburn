using NexusMods.UI.Sdk.Dialog.Enums;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Dialog;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Sdk.Settings;
using NexusMods.Abstractions.NexusLinks;
using NexusMods.App.UI.Controls.BugReport;
using NexusMods.App.UI.Controls.Navigation;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Pages.Changelog;
using NexusMods.App.UI.Pages.Settings;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.CrossPlatform;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.UI.Sdk;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using ReactiveCommand = ReactiveUI.ReactiveCommand;
using Unit = System.Reactive.Unit;

namespace NexusMods.App.UI.Controls.TopBar;

[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
public class TopBarViewModel : AViewModel<ITopBarViewModel>, ITopBarViewModel
{
    private readonly ILoginManager _loginManager;
    private readonly IAvaloniaInterop _avaloniaInterop;
    private readonly IWindowManager _windowManager;
    private readonly ILogger<TopBarViewModel> _logger;
    private readonly IWindowNotificationService _notificationService;

    [Reactive] public string ActiveWorkspaceTitle { get; [UsedImplicitly] set; } = string.Empty;
    [Reactive] public string ActiveWorkspaceSubtitle { get; [UsedImplicitly] set; } = string.Empty;

    public ReactiveUI.ReactiveCommand<NavigationInformation, Unit> OpenSettingsCommand { get; }

    public ReactiveUI.ReactiveCommand<NavigationInformation, Unit> ViewChangelogCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> ViewAppLogsCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> ShowWelcomeMessageCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> ReportBugCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> OpenGitHubCommand { get; }
    public R3.ReactiveCommand<R3.Unit, R3.Unit> LoginCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> LogoutCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> OpenNexusModsProfileCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> OpenNexusModsPremiumCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> OpenNexusModsAccountSettingsCommand { get; }
    public ReactiveUI.ReactiveCommand<Unit, Unit> NewTabCommand { get; }

    [Reactive] public bool IsLoggedIn { get; [UsedImplicitly] set; }
    [Reactive] public UserRole UserRole { get; [UsedImplicitly] set; }

    [Reactive] public IImage? Avatar { get; private set; }

    [Reactive] public string? Username { get; set; } = string.Empty;

    [Reactive] public IAddPanelDropDownViewModel AddPanelDropDownViewModel { get; set; } = null!;

    [Reactive] public IPanelTabViewModel? SelectedTab { get; set; }

    public TopBarViewModel(
        IServiceProvider serviceProvider,
        ILogger<TopBarViewModel> logger,
        ILoginManager loginManager,
        IWindowManager windowManager,
        IOverlayController overlayController,
        IOSInterop osInterop,
        ISettingsManager settingsManager,
        IFileSystem fileSystem,
        IWindowNotificationService notificationService,
        IAvaloniaInterop avaloniaInterop)
    {
        _logger = logger;
        _loginManager = loginManager;
        _avaloniaInterop = avaloniaInterop;
        _windowManager = windowManager;
        _notificationService = notificationService;

        var workspaceController = windowManager.ActiveWorkspaceController;

        OpenSettingsCommand = ReactiveCommand.Create<NavigationInformation>(info =>
            {
                var page = new PageData
                {
                    Context = new SettingsPageContext(),
                    FactoryId = SettingsPageFactory.StaticId,
                };

                var behavior = workspaceController.GetOpenPageBehavior(page, info);
                var workspace = workspaceController.ChangeOrCreateWorkspaceByContext<HomeContext>(() => page);
                workspaceController.OpenPage(workspace.Id, page, behavior);
            }
        );

        ViewChangelogCommand = ReactiveCommand.Create<NavigationInformation>(info =>
            {
                var page = new PageData
                {
                    Context = new ChangelogPageContext
                    {
                        TargetVersion = null,
                    },
                    FactoryId = ChangelogPageFactory.StaticId,
                };

                var behavior = workspaceController.GetOpenPageBehavior(page, info);
                workspaceController.OpenPage(workspaceController.ActiveWorkspace.Id, page, behavior);

            }
        );

        ViewAppLogsCommand = ReactiveCommand.Create(() =>
        {
            var loggingSettings = settingsManager.Get<LoggingSettings>();
            var logPath = loggingSettings.MainProcessLogFilePath.ToPath(fileSystem);
            osInterop.OpenFileInDirectory(logPath);

        });

        ShowWelcomeMessageCommand = ReactiveCommand.Create(() =>
        {
            var welcomeOverlayViewModel = serviceProvider.GetRequiredService<IWelcomeOverlayViewModel>();
            overlayController.Enqueue(welcomeOverlayViewModel);

        });

        var canLogin = this.WhenAnyValue(x => x.IsLoggedIn).Select(isLoggedIn => !isLoggedIn).ToObservable();
        LoginCommand = canLogin.ToReactiveCommand<R3.Unit, R3.Unit>(async (_, _) =>
            {
                await Login();
                return R3.Unit.Default;
            }, awaitOperation: AwaitOperation.Parallel, configureAwait: false
        );

        var canLogout = this.WhenAnyValue(x => x.IsLoggedIn);
        LogoutCommand = ReactiveCommand.CreateFromTask(Logout, canLogout);

        OpenNexusModsProfileCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var userInfo = await _loginManager.GetUserInfoAsync();
            if (userInfo is null) return;

            var uri = NexusModsUrlBuilder.GetProfileUri(userInfo.UserId);
            osInterop.OpenUri(uri);
        });

        NewTabCommand = ReactiveCommand.Create(() =>
            {
                var workspaceId = workspaceController.ActiveWorkspaceId;
                var panelId = workspaceController.ActiveWorkspace.SelectedPanel.Id;

                workspaceController.OpenPage(workspaceId,
                    Optional<PageData>.None,
                    new OpenPageBehavior.NewTab(panelId)
                );
            }
        );

        OpenNexusModsAccountSettingsCommand = ReactiveCommand.Create(() => osInterop.OpenUri(NexusModsUrlBuilder.UserSettingsUri));
        OpenNexusModsPremiumCommand = ReactiveCommand.Create( () => osInterop.OpenUri(NexusModsUrlBuilder.UpgradeToPremiumUri));
        ReportBugCommand = ReactiveCommand.CreateFromTask(() => BugReporter.ReportAsync(avaloniaInterop, osInterop, settingsManager, fileSystem, logger));
        OpenGitHubCommand = ReactiveCommand.Create( () => osInterop.OpenUri(ConstantLinks.GitHubUri));

        this.WhenActivated(d =>
            {
                _loginManager.AvatarObservable
                    .SelectMany(LoadImage)
                    .OnUI()
                    .SubscribeWithErrorLogging(image => Avatar = image)
                    .DisposeWith(d);

                _loginManager.IsLoggedInObservable
                    .OnUI()
                    .BindToVM(this, vm => vm.IsLoggedIn)
                    .DisposeWith(d);

                _loginManager.UserRoleObservable
                    .OnUI()
                    .BindToVM(this, vm => vm.UserRole)
                    .DisposeWith(d);

                _loginManager.UserInfoObservable
                    .Select(u => u?.Name)
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(name => Username = name ?? "");

                workspaceController.WhenAnyValue(controller => controller.ActiveWorkspace.Title)
                    .BindToVM(this, vm => vm.ActiveWorkspaceTitle)
                    .DisposeWith(d);

                workspaceController.WhenAnyValue(controller => controller.ActiveWorkspace.Subtitle)
                    .BindToVM(this, vm => vm.ActiveWorkspaceSubtitle)
                    .DisposeWith(d);

                workspaceController.WhenAnyValue(controller => controller.ActiveWorkspace.SelectedTab)
                    .BindToVM(this, vm => vm.SelectedTab)
                    .DisposeWith(d);
            }
        );
    }

    private async Task<IImage?> LoadImage(Uri? uri)
    {
        if (uri is null) return null;

        try
        {
            var client = new HttpClient();
            var stream = await client.GetByteArrayAsync(uri);
            return new Bitmap(new MemoryStream(stream));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load image: {Uri}", uri);
            return null;
        }
    }

    private async Task Login()
    {
        // Afterburn: until Nexus Mods approves Afterburn, the Nexus Mods login (OAuth) is not used. Logging in works
        // with the user's personal API key. It used to be read silently from the clipboard, so a wrong clipboard just
        // showed a toast that was easy to miss. Now a dialog asks for the key (filled in from the clipboard when it
        // looks like one), says clearly what went wrong, and offers to restart Afterburn so the login takes effect.
        _logger.LogInformation("Logging in with a personal API key");
        string? clipboard = null;
        try { clipboard = await _avaloniaInterop.GetClipboardTextAsync(); } catch { /* ignore */ }
        // Only pre-fill something that looks like an API key, so other things on the clipboard (passwords, links)
        // are never shown in the dialog.
        var trimmed = clipboard?.Trim() ?? string.Empty;
        var prefill = trimmed.Length is >= 20 and <= 400 && trimmed.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '=' or '+' or '/' or '.')
            ? trimmed
            : string.Empty;

        var message = "Paste your Personal API Key from nexusmods.com/users/myaccount?tab=api (scroll to the bottom, \"Personal API Key\").";
        while (true)
        {
            var dialog = DialogFactory.CreateStandardDialog(
                "Log in to Nexus Mods",
                new StandardDialogParameters
                {
                    Text = message,
                    InputLabel = "Personal API Key",
                    InputWatermark = "Paste your key here",
                    InputText = prefill,
                },
                [
                    DialogStandardButtons.Cancel,
                    new DialogButtonDefinition("Log in", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary),
                ],
                DialogWindowSize.Medium
            );

            var result = await _windowManager.ShowDialog(dialog, DialogWindowType.Modal);
            if (result.ButtonId != ButtonDefinitionId.Accept) return;

            var (success, resultMessage) = await ApiKeyStore.ValidateAndSaveAsync(result.InputText);
            _logger.LogInformation("Personal API key login: {Success} ({Message})", success, resultMessage);
            if (!success)
            {
                // Show the problem in the same dialog and let them try again.
                message = resultMessage;
                prefill = result.InputText;
                continue;
            }

            var restart = new DialogButtonDefinition("Restart now", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary);
            var done = await _windowManager.ShowDialog(DialogFactory.CreateStandardDialog(
                "Key saved",
                new StandardDialogParameters { Text = "Your key works and has been saved. Afterburn needs to restart to finish logging in." },
                [ new DialogButtonDefinition("Later", ButtonDefinitionId.Cancel, ButtonAction.Reject), restart ],
                DialogWindowSize.Small
            ), DialogWindowType.Modal);

            if (done.ButtonId == ButtonDefinitionId.Accept) RestartAfterburn();
            return;
        }
    }

    /// <summary>
    /// Starts a new Afterburn and closes this one. The new copy waits for this one to finish closing.
    /// </summary>
    private void RestartAfterburn()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            start.Environment["AFTERBURN_RESTARTING"] = "1";
            System.Diagnostics.Process.Start(start);
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Couldn't restart Afterburn");
            _notificationService.ShowToast("Couldn't restart automatically. Close Afterburn and open it again to finish logging in.", ToastNotificationVariant.Neutral, TimeSpan.FromSeconds(12));
        }
    }

    private async Task Logout()
    {
        _logger.LogInformation("Logging out of Nexus Mods");
        await _loginManager.Logout();
    }
}
