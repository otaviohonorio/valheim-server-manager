using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private readonly ServerManager _manager;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainWindow> _logger;
    private bool _closingConfirmed;

    public MainWindow(ShellViewModel viewModel, UiDispatcher ui, ServerManager manager, IDialogService dialogs, ILogger<MainWindow> logger)
    {
        ViewModel = viewModel;
        _manager = manager;
        _dialogs = dialogs;
        _logger = logger;
        InitializeComponent();
        ui.Initialize(DispatcherQueue);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ConfigureSize();

        AppWindow.Closing += OnClosing;
        Nav.SelectedItem = Nav.MenuItems[0];
        ViewModel.Start();
    }

    public ShellViewModel ViewModel { get; }

    public void Navigate(string key)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string)i.Tag == key);
        if (item is not null)
        {
            Nav.SelectedItem = item;
        }
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

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string key } && Pages.TryGetValue(key, out var page) &&
            ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page, null, args.RecommendedNavigationTransitionInfo);
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
        var active = _manager.Controllers.Where(c => c.Status.IsActive).ToArray();
        if (active.Length > 0)
        {
            var names = string.Join(", ", active.Select(c => c.Profile.DisplayName));
            var choice = await _dialogs.ChooseAsync(
                "Há servidores em execução",
                $"{names} continua(m) rodando em segundo plano se você fechar o gerenciador — sem janela, mas funcionando normalmente. " +
                "Ao abrir o gerenciador de novo, ele reconhece e volta a acompanhar.\n\nO que você prefere?",
                "Desligar com segurança e sair",
                "Sair e deixar rodando");
            if (choice == 0)
            {
                return;
            }

            if (choice == 1)
            {
                Content.IsHitTestVisible = false;
                AppTitleBar.Subtitle = "Salvando e desligando…";
                foreach (var controller in active)
                {
                    try
                    {
                        var result = await controller.StopAsync();
                        if (!result.Success && controller.Status.IsActive)
                        {
                            Content.IsHitTestVisible = true;
                            AppTitleBar.Subtitle = string.Empty;
                            await _dialogs.AlertAsync("Não foi possível desligar",
                                $"\"{controller.Profile.DisplayName}\": {result.Message} O gerenciador continua aberto.");
                            return;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException)
                    {
                        _logger.LogError(ex, "Falha ao desligar {Profile} ao sair", controller.Profile.DisplayName);
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
