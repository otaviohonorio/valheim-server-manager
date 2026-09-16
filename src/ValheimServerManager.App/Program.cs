using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using ValheimServerManager.Core.Processes;

namespace ValheimServerManager.App;

public static class Program
{
    private const string InstanceKey = "ValheimServerManager.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        // The executable is also its own Ctrl+C helper; handle that before any UI initialisation.
        if (SignalHelperCommand.TryRunHelper(args) is { } helperExitCode)
        {
            return helperExitCode;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToExistingInstance())
        {
            return 0;
        }

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

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
