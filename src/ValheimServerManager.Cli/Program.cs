using System.Text;
using ValheimServerManager.Cli;
using ValheimServerManager.Core.Processes;

// Helper mode must run before anything touches the console.
if (SignalHelperCommand.TryRunHelper(args) is { } helperExitCode)
{
    return helperExitCode;
}

Console.OutputEncoding = Encoding.UTF8;
return await CliApp.RunAsync(args).ConfigureAwait(false);
