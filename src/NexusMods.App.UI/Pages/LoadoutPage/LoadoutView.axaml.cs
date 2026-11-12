using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DynamicData.Binding;
using Humanizer;
using Humanizer.Localisation;
using JetBrains.Annotations;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Resources;
using NexusMods.Collections;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Icons;
using R3;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.LoadoutPage;

[UsedImplicitly]
public partial class LoadoutView : R3UserControl<ILoadoutViewModel>
{
    public LoadoutView()
    {
        InitializeComponent();

        TreeDataGridViewHelper.SetupTreeDataGridAdapter<LoadoutView, ILoadoutViewModel, CompositeItemModel<EntityId>, EntityId>(this, TreeDataGrid, vm => vm.Adapter);

        this.WhenActivated(disposables =>
            {
                SearchControl.AttachKeyboardHandlers(this, disposables);

                // Bind search adapter
                this.OneWayBind(ViewModel, vm => vm.Adapter, view => view.SearchControl.Adapter)
                    .AddTo(disposables);

                // initially hidden
                ContextControlGroup.IsVisible = false;

                BindableViewModel.Subscribe(this, static (vm, view) =>
                    {
                        view.EmptyState.Header = vm?.EmptyStateTitleText ?? string.Empty;
                        // Afterburn: only build the Rules tab once it's shown.
                        view.SortingSelectionView.ViewModel = null;
                        if (vm is not null && vm.HasRulesSection && ReferenceEquals(view.RulesTabControl.SelectedItem, view.RulesTabItem))
                            view.SortingSelectionView.ViewModel = vm.RulesSectionViewModel;
                        view.RulesTabItem.IsVisible = vm?.HasRulesSection ?? false;
                        view.ConflictsTabItem.IsVisible = vm?.HasConflictReport ?? false;

                        var isCollection = vm?.IsCollection ?? true;
                        var enableCollectionSharing = vm?.EnableCollectionSharing ?? false;

                        view.AllPageHeader.IsVisible = !isCollection;
                        view.Statusbar.IsVisible = isCollection;
                        view.PanelShare.IsVisible = isCollection && enableCollectionSharing;
                        view.PanelPublish.IsVisible = isCollection && enableCollectionSharing;

                        view.ButtonShareCollection.IsVisible = isCollection;
                        view.WritableCollectionPageHeader.IsVisible = isCollection;

                        var selectedSubTab = vm?.SelectedSubTab;
                        if (selectedSubTab is not null)
                        {
                            view.RulesTabControl.SelectedItem = selectedSubTab switch
                            {
                                LoadoutPageSubTabs.Mods => view.ModsTabItem,
                                LoadoutPageSubTabs.Rules => view.RulesTabItem,
                                _ => throw new ArgumentOutOfRangeException(nameof(selectedSubTab), selectedSubTab, null)
                            };
                        }

                        if (vm is not null && vm.HasRulesSection && ReferenceEquals(view.RulesTabControl.SelectedItem, view.RulesTabItem))
                            view.SortingSelectionView.ViewModel = vm.RulesSectionViewModel;
                    }
                ).AddTo(disposables);

                // Afterburn: build the Rules tab the first time it is selected.
                Observable.FromEventHandler<SelectionChangedEventArgs>(
                        addHandler: handler => RulesTabControl.SelectionChanged += handler,
                        removeHandler: handler => RulesTabControl.SelectionChanged -= handler)
                    .Subscribe(this, static (_, view) =>
                    {
                        var vm = view.ViewModel;
                        if (vm is null || !vm.HasRulesSection) return;
                        if (!ReferenceEquals(view.RulesTabControl.SelectedItem, view.RulesTabItem)) return;
                        if (view.SortingSelectionView.ViewModel is null) view.SortingSelectionView.ViewModel = vm.RulesSectionViewModel;
                    })
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandOpenFilesPage, view => view.ViewFilesButton)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandOpenLibraryPage, view => view.ViewLibraryButton)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandRemoveItem, view => view.DeleteButton)
                    .AddTo(disposables);

                this.OneWayBind(ViewModel, vm => vm.Adapter.Source.Value, view => view.TreeDataGrid.Source)
                    .AddTo(disposables);

                this.OneWayBind(ViewModel, vm => vm.Adapter.IsSourceEmpty.Value, view => view.EmptyState.IsActive)
                    .AddTo(disposables);

                this.OneWayR3Bind(static view => view.BindableViewModel, static vm => vm.CollectionName, static (view, collectionName) => view.TitleTextBlock.Text = collectionName)
                    .AddTo(disposables);

                this.OneWayR3Bind(static view => view.BindableViewModel, static vm => vm.ItemCount, static (view, count) => view.ModsCount.Text = count.ToString())
                    .AddTo(disposables);

                this.OneWayR3Bind(static view => view.BindableViewModel,
                        static vm => vm.CollectionStatus,
                        static (view, status) =>
                        {
                            view.ButtonChangeVisibility.LeftIcon = status switch
                            {
                                CollectionStatus.Unlisted => IconValues.VisibilityUnlisted,
                                CollectionStatus.Listed => IconValues.VisibilityListed
                            };
                        }
                    )
                    .AddTo(disposables);

                this.OneWayR3Bind(static view => view.BindableViewModel,
                        static vm => vm.LastUploadedDate,
                        static (view, lastUploaded) =>
                        {
                            view.LastUploadedText.Text = $"{(DateTimeOffset.Now - lastUploaded).Humanize(minUnit: TimeUnit.Second)} ago";
                            ToolTip.SetTip(view.LastUploadedText, lastUploaded.ToLocalTime().ToString("F"));
                        }
                    )
                    .AddTo(disposables);

                this.OneWayR3Bind(static view => view.BindableViewModel,
                        static vm => vm.RevisionNumber,
                        static (view, revision) => view.RevisionText.Text = $"Revision {revision.ToString()}"
                    )
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandDeselectItems, view => view.DeselectItemsButton)
                    .AddTo(disposables);

                // Afterburn: Conflicts tab.
                this.BindCommand(ViewModel, vm => vm.CommandCheckConflicts, view => view.ButtonCheckConflicts)
                    .AddTo(disposables);

                this.OneWayR3Bind(static view => view.BindableViewModel,
                        static vm => vm.LatestConflictReport,
                        static (view, report) =>
                        {
                            if (report is null)
                            {
                                view.ConflictsSummaryText.Text = "No conflict check has run yet. Afterburn checks automatically each time you start the game, or press Check now.";
                                view.ConflictSections.ItemsSource = null;
                                view.ConflictsCountBorder.IsVisible = false;
                                return;
                            }

                            view.ConflictsSummaryText.Text = $"Last checked {report.CheckedAt.ToLocalTime():g}. {report.Summary}";
                            view.ConflictSections.ItemsSource = report.Sections;

                            var issues = report.Sections.Where(static s => s.Title != "Notes").Sum(static s => s.Items.Count);
                            view.ConflictsCount.Text = issues.ToString();
                            view.ConflictsCountBorder.IsVisible = issues > 0;
                        }
                    )
                    .AddTo(disposables);

                this.OneWayR3Bind(static view => view.BindableViewModel,
                        static vm => vm.IsCheckingConflicts,
                        static (view, isChecking) => view.ButtonCheckConflicts.Content = isChecking ? "Checking..." : "Check now"
                    )
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandShareCollection, view => view.ButtonShareCollection)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandUploadAndPublishRevision, view => view.SplitButtonPublishCollection)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandUploadDraftRevision, view => view.MenuItemUploadDraft)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandOpenRevisionUrl, view => view.ButtonOpenRevisionUrl)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandRenameGroup, view => view.MenuItemRenameCollection)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandDeleteGroup, view => view.MenuItemDeleteCollection)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandChangeVisibility, view => view.ButtonChangeVisibility)
                    .AddTo(disposables);

                this.BindCommand(ViewModel, vm => vm.CommandOpenRevisionUrl, view => view.ButtonAddTileImage)
                    .AddTo(disposables);

                this.ObserveViewModelProperty(static view => view.BindableViewModel, static vm => vm.IsCollectionUploaded)
                    .Subscribe(this, static (isCollectionUploaded, self) =>
                        {
                            var enableCollectionSharing = self.BindableViewModel.Value?.EnableCollectionSharing ?? false;

                            self.ButtonShareCollection.IsVisible = !isCollectionUploaded;
                            self.SplitButtonPublishCollection.IsVisible = isCollectionUploaded;
                            self.VisibilityButtonStack.IsVisible = isCollectionUploaded;
                            self.PanelPublish.IsVisible = isCollectionUploaded && enableCollectionSharing;

                            self.ButtonAddTileImage.IsVisible = isCollectionUploaded;
                            self.UnpublishedHeaderBorder.IsVisible = !isCollectionUploaded;
                        }
                    ).AddTo(disposables);

                this.ObserveViewModelProperty(static view => view.BindableViewModel, static vm => vm.SelectionCount)
                    .Subscribe(this, static (count, self) =>
                        {
                            self.ContextControlGroup.IsVisible = count != 0;
                            self.DeselectItemsButton.Text = count == 0 ? string.Empty : string.Format(Language.Library_DeselectItemsButton_Text, count);
                        }
                    )
                    .AddTo(disposables);

                this.ObserveViewModelProperty(static view => view.BindableViewModel, static vm => vm.Adapter.IsSourceEmpty)
                    .Subscribe(this,
                        static (b, view) => { ToolTip.SetTip(view.ButtonShareCollection, b ? "You can't share this collection until it has at least one installed mod." : null); }
                    )
                    .AddTo(disposables);
            }
        );
    }
}
