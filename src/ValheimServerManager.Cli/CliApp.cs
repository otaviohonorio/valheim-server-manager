using System.CommandLine;
using System.Diagnostics;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Profiles;

namespace ValheimServerManager.Cli;

internal static class CliApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = new RootCommand("Valheim Server Manager — linha de comando");
        root.Subcommands.Add(BuildLabCommand());
        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    private static Command BuildLabCommand()
    {
        var serverDir = new Option<string>("--server-dir") { Required = true };
        var saveDir = new Option<string>("--save-dir") { Required = true };
        var world = new Option<string>("--world") { Required = true };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 2466 };

        var command = new Command("lab-stop", "Teste: inicia um servidor escondido e desliga com Ctrl+C.");
        command.Options.Add(serverDir);
        command.Options.Add(saveDir);
        command.Options.Add(world);
        command.Options.Add(port);
        command.SetAction(async (parse, ct) =>
        {
            var profile = new ServerProfile
            {
                ServerDirectory = parse.GetValue(serverDir)!,
                SaveDirectory = parse.GetValue(saveDir)!,
                WorldName = parse.GetValue(world)!,
                Port = parse.GetValue(port),
                ServerName = "VSM-Teste-E2E",
                Password = "testeVSM123",
                Public = false,
            };

            var launcher = new HiddenConsoleLauncher();
            using var process = launcher.Launch(profile);
            Console.WriteLine($"PID {process.Id} iniciado. Aguardando 'Game server connected'...");

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromMinutes(3))
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                if (File.Exists(profile.LogFilePath) && ReadShared(profile.LogFilePath).Contains("Game server connected", StringComparison.Ordinal))
                {
                    break;
                }

                if (process.HasExited)
                {
                    Console.WriteLine($"Processo saiu cedo, código {process.ExitCode}");
                    return 1;
                }
            }

            Console.WriteLine($"Online em {sw.Elapsed.TotalSeconds:F0}s. Enviando Ctrl+C...");
            var sender = new HelperProcessSignalSender(new SignalHelperCommand(System.Environment.ProcessPath!, [SignalHelperCommand.AppSwitch]));
            var result = await sender.SendCtrlCAsync(process.Id, ct).ConfigureAwait(false);
            Console.WriteLine($"Sinal: {result}");

            var exited = process.WaitForExit(TimeSpan.FromSeconds(120));
            Console.WriteLine(exited ? $"Saiu. Código {process.ExitCode}" : "NÃO saiu em 120s");
            if (!exited)
            {
                process.Kill();
                return 2;
            }

            var log = ReadShared(profile.LogFilePath);
            var saved = log.Contains("World save (5/5) done", StringComparison.Ordinal);
            var destroyed = log.Contains("Net scene destroyed", StringComparison.Ordinal) || log.Contains("ZNet OnDestroy", StringComparison.Ordinal);
            Console.WriteLine($"Save concluído no log: {saved}; encerramento limpo: {destroyed}");
            return saved && destroyed ? 0 : 3;
        });
        return command;
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
