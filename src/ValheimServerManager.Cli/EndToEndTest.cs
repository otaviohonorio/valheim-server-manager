using System.CommandLine;
using System.Diagnostics;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Cli;

/// <summary>
/// Exercises the real valheim_server.exe against a throwaway copy of a world: start, online,
/// backup while running, graceful stop, creative mode on and off (checked in the world file),
/// refusal of a broken world and restore. Never touches the source world.
/// </summary>
internal static class EndToEndTest
{
    public static Command Command()
    {
        var serverDir = new Option<string>("--server-dir") { Required = true, Description = "Instalação do Valheim Dedicated Server." };
        var source = new Option<string>("--world-source") { Required = true, Description = "Pasta de um mundo (será copiada)." };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 2466 };
        var work = new Option<string?>("--work-dir") { Description = "Pasta de trabalho (padrão: temporária)." };
        var keep = new Option<bool>("--keep") { Description = "Não apagar a pasta de trabalho no fim." };
        var command = new Command("e2e", "Teste de ponta a ponta com o servidor real, numa cópia do mundo.");
        command.Options.Add(serverDir);
        command.Options.Add(source);
        command.Options.Add(port);
        command.Options.Add(work);
        command.Options.Add(keep);
        command.SetAction(async (parse, ct) => await RunAsync(
            parse.GetValue(serverDir)!, parse.GetValue(source)!, parse.GetValue(port), parse.GetValue(work), parse.GetValue(keep), ct).ConfigureAwait(false));
        return command;
    }

    private static async Task<int> RunAsync(string serverDir, string source, int port, string? workDir, bool keep, CancellationToken ct)
    {
        var root = workDir ?? Path.Combine(Path.GetTempPath(), "vsm-e2e-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var sourceReport = WorldInspector.InspectDirectory(source);
        if (!sourceReport.IsHealthy)
        {
            Console.Error.WriteLine("O mundo de origem não está íntegro.");
            return 2;
        }

        var worldName = sourceReport.Metadata?.Name ?? Path.GetFileName(source);
        var profile = new ServerProfile
        {
            DisplayName = "E2E",
            ServerDirectory = serverDir,
            SaveDirectory = Path.Combine(root, "ServerSave"),
            BackupDirectory = Path.Combine(root, "backups"),
            ServerName = "VSM-E2E-" + Environment.ProcessId,
            WorldName = worldName,
            Port = port,
            Password = "e2eSenha42",
            Public = false,
            StopTimeoutSeconds = 180,
        };

        var worldDir = WorldFolder.WorldDirectory(profile.SaveDirectory, worldName);
        CopyDirectory(source, worldDir);
        Console.WriteLine($"Pasta de trabalho: {root}");
        Console.WriteLine($"Mundo {worldName}, save {sourceReport.LatestSave!.Number}, {sourceReport.TotalZdos:N0} objetos\n");

        var failures = 0;
        await using var manager = CliApp.CreateManager(Path.Combine(root, "appdata"));
        manager.AddProfile(profile);
        var controller = manager.GetController(profile.Id);
        controller.ActivityAdded += (_, a) => Console.WriteLine($"      · {a.Message}");

        async Task Step(string name, Func<Task<bool>> body)
        {
            var sw = Stopwatch.StartNew();
            Console.WriteLine($"▶ {name}");
            bool ok;
            try
            {
                ok = await body().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      exceção: {ex.Message}");
                ok = false;
            }

            Console.WriteLine($"  {(ok ? "PASSOU" : "FALHOU")} ({sw.Elapsed.TotalSeconds:N1} s)\n");
            if (!ok)
            {
                failures++;
            }
        }

        async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.Elapsed > timeout)
                {
                    return false;
                }

                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            return true;
        }

        async Task<bool> StartAndWait()
        {
            var result = await controller.StartAsync(StartOptions.Default, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                Console.WriteLine($"      {result.Message} {string.Join(" | ", result.Checks.Select(c => c.Message))}");
                return false;
            }

            return await WaitFor(() => controller.Status.State == ServerRunState.Running, TimeSpan.FromMinutes(4)).ConfigureAwait(false);
        }

        async Task<bool> StopClean()
        {
            var before = controller.Status.LastSaveNumber;
            var result = await controller.StopAsync(ct).ConfigureAwait(false);
            var status = controller.Status;
            Console.WriteLine($"      {result.Message} (save {status.LastSaveNumber}, saída {status.LastExit?.ExitCode})");
            return result.Success && status.LastExit is { Clean: true } && status.LastSaveNumber is not null && status.LastSaveNumber != before;
        }

        bool WorldHasCreativeKey()
        {
            var report = WorldInspector.Inspect(profile.SaveDirectory, worldName);
            Console.WriteLine($"      save {report.LatestSave?.Number}: chaves [{string.Join(", ", report.Metadata?.KeyNames ?? [])}]");
            return report.Metadata?.IsCreative ?? false;
        }

        await Step("Iniciar em modo normal (com backup antes)", async () =>
            await StartAndWait().ConfigureAwait(false) &&
            manager.Backups.List(profile).Any(b => b.Kind == BackupKind.PreStart)).ConfigureAwait(false);

        await Step("Backup manual com o servidor rodando", async () =>
        {
            var entry = await manager.Backups.CreateAsync(profile, BackupKind.Manual, "e2e em execução", ct).ConfigureAwait(false);
            return (await manager.Backups.VerifyAsync(entry, ct).ConfigureAwait(false)).Ok;
        }).ConfigureAwait(false);

        await Step("Parar com segurança (Ctrl+C) e confirmar o save", StopClean).ConfigureAwait(false);

        await Step("Mundo íntegro e backup depois de parar", () => Task.FromResult(
            WorldInspector.Inspect(profile.SaveDirectory, worldName).IsHealthy &&
            manager.Backups.List(profile).Any(b => b.Kind == BackupKind.PostStop))).ConfigureAwait(false);

        await Step("Modo criativo: nobuildcost gravado no mundo", async () =>
        {
            var p = controller.Profile;
            p.CreativeMode = true;
            manager.SaveProfile(p);
            return await StartAndWait().ConfigureAwait(false) && await StopClean().ConfigureAwait(false) && WorldHasCreativeKey();
        }).ConfigureAwait(false);

        await Step("Volta ao normal: nobuildcost removido do mundo", async () =>
        {
            var p = controller.Profile;
            p.CreativeMode = false;
            manager.SaveProfile(p);
            return await StartAndWait().ConfigureAwait(false) && await StopClean().ConfigureAwait(false) && !WorldHasCreativeKey();
        }).ConfigureAwait(false);

        BackupEntry? good = null;
        await Step("Recusa iniciar mundo com o .db2 faltando (cenário de 16/09)", async () =>
        {
            good = manager.Backups.List(profile).First(b => b.Kind == BackupKind.PostStop);
            var latest = WorldInspector.Inspect(profile.SaveDirectory, worldName).LatestSave!;
            File.Delete(latest.Db2!);
            var result = await controller.StartAsync(StartOptions.Confirmed, ct).ConfigureAwait(false);
            return !result.Success &&
                   result.Checks.Any(c => c.Code == "WORLD_INCOMPLETE_LATEST_SAVE") &&
                   controller.Status.State == ServerRunState.Stopped;
        }).ConfigureAwait(false);

        await Step("Restaurar o backup e voltar a iniciar", async () =>
        {
            var result = await manager.Backups.RestoreAsync(profile, good!, ct).ConfigureAwait(false);
            Console.WriteLine($"      cópia de segurança: {result.SafetyCopy?.Kind}; pasta antiga: {Path.GetFileName(result.ReplacedFolder)}");
            return result.SafetyCopy?.Kind == BackupKind.Quarantine &&
                   await StartAndWait().ConfigureAwait(false) &&
                   await StopClean().ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (controller.Status.IsActive)
        {
            await controller.ForceKillAsync().ConfigureAwait(false);
        }

        Console.WriteLine(failures == 0 ? "TODOS OS PASSOS PASSARAM" : $"{failures} PASSO(S) FALHARAM");
        if (!keep && failures == 0)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }
}
