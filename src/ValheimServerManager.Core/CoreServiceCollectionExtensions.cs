using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Settings;

namespace ValheimServerManager.Core;

[SupportedOSPlatform("windows")]
public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers the manager core. <paramref name="signalHelper"/> is the executable that sends Ctrl+C.</summary>
    public static IServiceCollection AddValheimServerManagerCore(this IServiceCollection services, SignalHelperCommand signalHelper)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISettingsStore>(_ => new JsonSettingsStore());
        services.TryAddSingleton<BackupService>();
        services.TryAddSingleton<IServerProcessLauncher, HiddenConsoleLauncher>();
        services.TryAddSingleton<IServerProcessLocator, WmiServerProcessLocator>();
        services.TryAddSingleton<ISignalSender>(_ => new HelperProcessSignalSender(signalHelper));
        services.TryAddSingleton<ServerManager>();
        return services;
    }
}
