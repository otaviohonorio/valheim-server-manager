using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ValheimServerManager.App.Localization;
using ValheimServerManager.App.Services;
using ValheimServerManager.App.ViewModels;
using ValheimServerManager.App.Views;
using ValheimServerManager.Core.Servers;
using Windows.Graphics;

namespace ValheimServerManager.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new(StringComparer.Ordinal)
    {
        ["dashboard"] = typeof(DashboardPage),
        ["server"] = typeof(ServerSettingsPage),
        ["world"] = typeof(WorldPage),
        ["backups"] = typeof(BackupsPage),
        ["players"] = typeof(PlayersPage),
        ["log"] = typeof(LogPage),
        ["about"] = typeof(AboutPage),
    };

    private const string NewServerKey = "new";

    private readonly ServerManager _manager;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainWindow> _logger;
    private readonly TrayIcon _tray;
    private bool _closingConfirmed;
    private bool _exitRequested;
    private bool _trayHintShown;
    private bool _revertingNavigation;
    private NavigationViewItem? _currentNavItem;

    public MainWindow(
        ShellViewModel viewModel, UiDispatcher ui, ServerManager manager, IDialogService dialogs, TrayIcon tray, ILogger<MainWindow> logger)
    {
        ViewModel = viewModel;
        _manager = manager;
        _dialogs = dialogs;
        _tray = tray;
        _logger = logger;
        InitializeComponent();
        ui.Initialize(DispatcherQueue);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ConfigureSize();

        AppWindow.Closing += OnClosing;
        InitializeTray();
        ViewModel.ConfirmLeaveAsync = ConfirmLeaveEditorAsync;
        ViewModel.NewServerRequested += async (_, _) => await OpenNewServerAsync();
        ContentFrame.Navigated += (_, e) => ViewModel.IsCreatingServer = e.SourcePageType == typeof(NewServerPage);
        Nav.SelectedItem = Nav.MenuItems[0];
        ViewModel.Start();
    }

    public ShellViewModel ViewModel { get; }

    public async Task OpenNewServerAsync()
    {
        if (ContentFrame.Content is NewServerPage || !await ConfirmLeaveEditorAsync())
        {
            return;
        }

        _revertingNavigation = true;
        Nav.SelectedItem = null;
        _currentNavItem = null;
        _revertingNavigation = false;
        ContentFrame.Navigate(typeof(NewServerPage));
    }

    public void Navigate(string key)
    {
        if (key == NewServerKey)
        {
            _ = OpenNewServerAsync();
            return;
        }

        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == key);
        if (item is null)
        {
            return;
        }

        if (ReferenceEquals(Nav.SelectedItem, item) && Pages.TryGetValue(key, out var page) && ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
            return;
        }

        Nav.SelectedItem = item;
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();
    }

    private void InitializeTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _tray.Initialize(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), "Valheim Server Manager");
        _logger.LogInformation("System tray icon visible: {Visible}", _tray.IsVisible);
        _tray.OpenRequested += (_, _) => DispatcherQueue.TryEnqueue(BringToFront);
        _tray.ExitRequested += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _exitRequested = true;
            BringToFront();
            Close();
        });
        _tray.SessionEnding += (_, _) => SaveServersBeforeWindowsEnds();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.ActiveCount))
            {
                _tray.SetShutdownBlockReason(ViewModel.ActiveCount > 0
                    ? ShellStrings.Tray_ShutdownBlockRunning
                    : null);
            }

            if (e.PropertyName == nameof(ShellViewModel.ActiveCount))
            {
                _tray.SetTooltip(ViewModel.ActiveCount switch
                {
                    0 => "Valheim Server Manager",
                    1 => ShellStrings.Tray_TooltipOne,
                    var n => string.Format(CultureInfo.CurrentCulture, ShellStrings.Tray_TooltipMany, n),
                });
            }
        };
    }

    /// <summary>
    /// Windows kills hidden console processes at logoff/shutdown without letting them save. Stop every
    /// server gracefully first; the shutdown screen shows the block reason meanwhile.
    /// </summary>
    private void SaveServersBeforeWindowsEnds()
    {
        var active = _manager.Controllers.Where(c => c.Status.IsActive).ToArray();
        if (active.Length == 0)
        {
            return;
        }

        _logger.LogWarning("Windows is ending the session; stopping {Count} server(s) safely", active.Length);
        _tray.SetShutdownBlockReason(ShellStrings.Tray_ShutdownBlockSaving);
        try
        {
            var stops = active.Select(c => Task.Run(() => c.StopAsync())).ToArray();
            if (!Task.WaitAll(stops, TimeSpan.FromSeconds(90)))
            {
                _logger.LogError("Not every server confirmed its save before the session ended");
            }
        }
        catch (AggregateException ex)
        {
            _logger.LogError(ex, "Failed to stop servers at the end of the session");
        }
        finally
        {
            _tray.SetShutdownBlockReason(null);
        }
    }

    private void ConfigureSize()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 620;
        }

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
        var width = (int)Math.Min(1320 * scale, area.WorkArea.Width * 0.92);
        var height = (int)Math.Min(900 * scale, area.WorkArea.Height * 0.92);
        AppWindow.MoveAndResize(new RectInt32(
            area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
            area.WorkArea.Y + ((area.WorkArea.Height - height) / 2),
            width,
            height));
    }

    private async void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_revertingNavigation || args.SelectedItem is not NavigationViewItem { Tag: string key } item ||
            !Pages.TryGetValue(key, out var page))
        {
            return;
        }

        if (ContentFrame.CurrentSourcePageType == page)
        {
            _currentNavItem = item;
            return;
        }

        if (!await ConfirmLeaveEditorAsync())
        {
            _revertingNavigation = true;
            Nav.SelectedItem = _currentNavItem;
            _revertingNavigation = false;
            return;
        }

        _currentNavItem = item;
        ContentFrame.Navigate(page, null, args.RecommendedNavigationTransitionInfo);
    }

    /// <summary>Asks what to do with unsaved edits on the current page. False means "stay here".</summary>
    private async Task<bool> ConfirmLeaveEditorAsync()
    {
        if (ContentFrame.Content is not IEditorPage { Editor: { IsDirty: true } editor })
        {
            return true;
        }

        var choice = await _dialogs.ChooseAsync(
            ShellStrings.Dialog_UnsavedTitle,
            ShellStrings.Dialog_UnsavedMessage,
            ShellStrings.Dialog_Save,
            ShellStrings.Dialog_Discard);
        switch (choice)
        {
            case 1:
                await editor.SaveCommand.ExecuteAsync(null);
                return !editor.IsDirty;
            case 2:
                editor.DiscardCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private void OnPaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void OnAlertClosed(InfoBar sender, object args)
    {
        if (sender.Tag is AlertItem item)
        {
            ViewModel.Alerts.Dismiss(item);
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingConfirmed)
        {
            return;
        }

        args.Cancel = true;
        if (!await ConfirmLeaveEditorAsync())
        {
            _exitRequested = false;
            return;
        }

        var active = _manager.Controllers.Where(c => c.Status.IsActive).ToArray();
        if (active.Length > 0 && !_exitRequested && _manager.Settings.MinimizeToTrayOnClose && _tray.IsVisible)
        {
            AppWindow.Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloon(ShellStrings.Tray_HiddenTitle, ShellStrings.Tray_HiddenMessage);
            }

            return;
        }

        _exitRequested = false;
        if (active.Length > 0)
        {
            var names = string.Join(", ", active.Select(c => c.Profile.DisplayName));
            var choice = await _dialogs.ChooseAsync(
                ShellStrings.Exit_RunningTitle,
                string.Format(CultureInfo.CurrentCulture,
                    active.Length == 1 ? ShellStrings.Exit_RunningMessageOne : ShellStrings.Exit_RunningMessageMany, names),
                ShellStrings.Exit_StopAndExit,
                ShellStrings.Exit_KeepRunning);
            if (choice == 0)
            {
                return;
            }

            if (choice == 1)
            {
                Content.IsHitTestVisible = false;
                AppTitleBar.Subtitle = ShellStrings.Exit_Stopping;
                foreach (var controller in active)
                {
                    try
                    {
                        var result = await controller.StopAsync();
                        if (!result.Success && controller.Status.IsActive)
                        {
                            Content.IsHitTestVisible = true;
                            AppTitleBar.Subtitle = string.Empty;
                            await _dialogs.AlertAsync(ShellStrings.Exit_StopFailedTitle,
                                string.Format(CultureInfo.CurrentCulture, ShellStrings.Exit_StopFailedMessage, controller.Profile.DisplayName, result.Message));
                            return;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException)
                    {
                        _logger.LogError(ex, "Failed to stop {Profile} on exit", controller.Profile.DisplayName);
                    }
                }
            }
        }

        _closingConfirmed = true;
        ViewModel.Dispose();
        if (App.Current is { } app)
        {
            await app.ShutdownAsync();
        }

        Close();
        Application.Current.Exit();
    }
}
