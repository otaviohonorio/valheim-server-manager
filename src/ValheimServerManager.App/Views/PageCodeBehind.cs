using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ValheimServerManager.App.ViewModels;

namespace ValheimServerManager.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        ViewModel = App.GetService<DashboardViewModel>();
        InitializeComponent();
        ViewModel.NavigateRequested += (_, key) => App.GetService<MainWindow>().Navigate(key);
        Loaded += (_, _) =>
        {
            ViewModel.Activate();
            ViewModel.StartClock();
        };
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public DashboardViewModel ViewModel { get; }

    private void OnAdjustClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string section)
        {
            ViewModel.RequestNavigation(section);
        }
    }
}

/// <summary>A page that edits the profile and can have unsaved changes.</summary>
public interface IEditorPage
{
    ProfileEditorViewModel Editor { get; }
}

public sealed partial class ServerSettingsPage : Page, IEditorPage
{
    public ServerSettingsPage()
    {
        ViewModel = App.GetService<ServerSettingsViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Activate();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public ServerSettingsViewModel ViewModel { get; }

    public ProfileEditorViewModel Editor => ViewModel;

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.Password != ViewModel.Password)
        {
            ViewModel.Password = box.Password;
        }
    }
}

public sealed partial class WorldPage : Page, IEditorPage
{
    public WorldPage()
    {
        ViewModel = App.GetService<WorldViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Activate();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public WorldViewModel ViewModel { get; }

    public ProfileEditorViewModel Editor => ViewModel;

    private void OnWorldBoxFocused(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox box && ViewModel.Worlds.Count > 0)
        {
            box.IsSuggestionListOpen = true;
        }
    }

    private void OnWorldChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string world)
        {
            sender.Text = world;
        }
    }
}

public sealed partial class BackupsPage : Page
{
    public BackupsPage()
    {
        ViewModel = App.GetService<BackupsViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Activate();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public BackupsViewModel ViewModel { get; }

    private void OnRestoreClick(object sender, RoutedEventArgs e) =>
        ViewModel.RestoreCommand.Execute((sender as FrameworkElement)?.Tag as BackupItemViewModel);

    private void OnVerifyClick(object sender, RoutedEventArgs e) =>
        ViewModel.VerifyCommand.Execute((sender as FrameworkElement)?.Tag as BackupItemViewModel);

    private void OnOpenClick(object sender, RoutedEventArgs e) =>
        ViewModel.OpenItemCommand.Execute((sender as FrameworkElement)?.Tag as BackupItemViewModel);

    private void OnDeleteClick(object sender, RoutedEventArgs e) =>
        ViewModel.DeleteCommand.Execute((sender as FrameworkElement)?.Tag as BackupItemViewModel);
}

public sealed partial class PlayersPage : Page
{
    public PlayersPage()
    {
        ViewModel = App.GetService<PlayersViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Activate();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public PlayersViewModel ViewModel { get; }
}

public sealed partial class LogPage : Page
{
    public LogPage()
    {
        ViewModel = App.GetService<LogViewModel>();
        InitializeComponent();
        ViewModel.LinesAppended += OnLinesAppended;
        Loaded += (_, _) => ViewModel.Activate();
        Unloaded += (_, _) =>
        {
            ViewModel.LinesAppended -= OnLinesAppended;
            ViewModel.Dispose();
        };
    }

    public LogViewModel ViewModel { get; }

    private void OnLinesAppended(object? sender, EventArgs e)
    {
        if (ViewModel.FollowTail && ViewModel.Lines.Count > 0)
        {
            LinesList.ScrollIntoView(ViewModel.Lines[^1]);
        }
    }

    private void OnArchivedClick(object sender, ItemClickEventArgs e) =>
        ViewModel.OpenArchivedCommand.Execute(e.ClickedItem as string);
}

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        ViewModel = App.GetService<AboutViewModel>();
        InitializeComponent();
    }

    public AboutViewModel ViewModel { get; }
}
