using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using ValheimServerManager.App.Services;
using ValheimServerManager.App.ViewModels;
using ValheimServerManager.App.Views;
using ValheimServerManager.Core;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Settings;

namespace ValheimServerManager.App;

public partial class App : Application
{
    private readonly IHost _host;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });

        var store = new JsonSettingsStore();
        var logDirectory = Path.Combine(store.DataDirectory, "logs");
        builder.Services.AddSerilog(config => config
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

        builder.Services.AddSingleton<ISettingsStore>(store);
        builder.Services.AddValheimServerManagerCore(SignalHelperCommand.ForCurrentExecutable());

        // App services
        builder.Services.AddSingleton<UiDispatcher>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<IPickerService, PickerService>();
        builder.Services.AddSingleton<IShellService, ShellService>();
        builder.Services.AddSingleton<TrayIcon>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        builder.Services.AddSingleton<ProfileContext>();
        builder.Services.AddSingleton<AlertCenter>();

        // View models
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<ServerSettingsViewModel>();
        builder.Services.AddTransient<WorldViewModel>();
        builder.Services.AddTransient<BackupsViewModel>();
        builder.Services.AddTransient<PlayersViewModel>();
        builder.Services.AddTransient<LogViewModel>();
        builder.Services.AddTransient<AboutViewModel>();
        builder.Services.AddTransient<NewServerViewModel>();

        // Views
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        Services = _host.Services;
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services.GetService<ILogger<App>>()?.LogError(e.Exception, "Tarefa com exceção não observada");
            e.SetObserved();
        };
    }

    public static new App? Current => Application.Current as App;

    public IServiceProvider Services { get; }

    public static T GetService<T>()
        where T : class =>
        Current?.Services.GetRequiredService<T>() ?? throw new InvalidOperationException("App não inicializado.");

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await _host.StartAsync();
        Services.GetRequiredService<ILogger<App>>().LogInformation("Valheim Server Manager iniciado");
        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    public void BringToFront() =>
        _window?.DispatcherQueue.TryEnqueue(() => _window.BringToFront());

    internal async Task ShutdownAsync()
    {
        try
        {
            Services.GetRequiredService<TrayIcon>().Dispose();
            await Services.GetRequiredService<ServerManager>().DisposeAsync();
            await _host.StopAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            _host.Dispose();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Services.GetService<ILogger<App>>()?.LogCritical(e.Exception, "Erro não tratado na interface");
        e.Handled = true;
        Services.GetService<AlertCenter>()?.Publish(new ServerAlert(
            AlertLevel.Error, "Erro inesperado", e.Message, DateTimeOffset.Now));
    }
}
