using System.Text;
using ValheimServerManager.Cli;
using ValheimServerManager.Core.Localization;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Settings;

// Helper mode must run before anything touches the console.
if (SignalHelperCommand.TryRunHelper(args) is { } helperExitCode)
{
    return helperExitCode;
}

Console.OutputEncoding = Encoding.UTF8;
AppLanguage.Apply(System.Environment.GetEnvironmentVariable("VSM_LANG") ?? SavedLanguage());
return await CliApp.RunAsync(args).ConfigureAwait(false);

static string? SavedLanguage()
{
    try
    {
        return new JsonSettingsStore().Load().Language;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}
