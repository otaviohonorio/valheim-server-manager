using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using ValheimServerManager.Core.Localization;
using ValheimServerManager.Core.Processes;

namespace ValheimServerManager.App;

public static class Program
{
    /// <summary>Held while the app runs; the installer checks it before replacing files (AppMutex in build/installer.iss).</summary>
    public const string RunningMutexName = "ValheimServerManager.Running";

    private static Mutex? _runningMutex;

    private static string InstanceKey
    {
        get
        {
            var dataDir = new ValheimServerManager.Core.Settings.JsonSettingsStore().DataDirectory.ToUpperInvariant();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir)))[..16];
            return "ValheimServerManager." + hash;
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // The executable is also its own Ctrl+C helper; handle that before any UI initialisation.
        if (SignalHelperCommand.TryRunHelper(args) is { } helperExitCode)
        {
            return helperExitCode;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        ApplyLanguage();

        if (RedirectToExistingInstance())
        {
            return 0;
        }

        _runningMutex = new Mutex(false, RunningMutexName);

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>Text comes from Localization\*.resx; WinUI's own controls follow the same language.</summary>
    private static void ApplyLanguage()
    {
        var culture = AppLanguage.Apply(new ValheimServerManager.Core.Settings.JsonSettingsStore().Load().Language);

        // WinUI's own labels (a ToggleSwitch's On/Off, for example) come from its MUI files, which follow
        // the process' preferred UI languages rather than .NET's culture.
        var mui = culture.Name switch
        {
            AppLanguage.PortugueseBrazil => "pt-BR",
            AppLanguage.Spanish => "es-ES\0es-MX",
            _ => "en-US",
        };
        SetProcessPreferredUILanguages(MuiLanguageName, mui + "\0\0", out _);

        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = culture.Name;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Built-in control labels stay in the Windows language; ours are already set.
        }
    }

    private const uint MuiLanguageName = 0x8;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetProcessPreferredUILanguages(uint flags, string languages, out uint count);

    /// <summary>Only one manager may run: two would fight over the same servers.</summary>
    private static bool RedirectToExistingInstance()
    {
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += (_, _) => App.Current?.BringToFront();
            return false;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new ManualResetEventSlim();
        _ = Task.Run(async () =>
        {
            try
            {
                await keyInstance.RedirectActivationToAsync(activation);
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(TimeSpan.FromSeconds(5));
        return true;
    }
}
