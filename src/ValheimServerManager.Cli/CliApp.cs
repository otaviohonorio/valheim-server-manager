using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Settings;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Cli;

internal static class CliApp
{
    private static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Nome do perfil (ou início do id). Padrão: o perfil selecionado no app.",
    };

    private static readonly Option<string?> DataDirOption = new("--data-dir")
    {
        Description = "Pasta de configurações (padrão: %LOCALAPPDATA%\\ValheimServerManager ou VSM_DATA_DIR).",
        Recursive = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var root = new RootCommand("Valheim Server Manager — linha de comando");
        root.Options.Add(DataDirOption);
        root.Subcommands.Add(ProfilesCommand());
        root.Subcommands.Add(ImportCommand());
        root.Subcommands.Add(StatusCommand());
        root.Subcommands.Add(StartCommand());
        root.Subcommands.Add(StopCommand());
        root.Subcommands.Add(BackupCommand());
        root.Subcommands.Add(BackupsCommand());
        root.Subcommands.Add(RestoreCommand());
        root.Subcommands.Add(InspectCommand());
        root.Subcommands.Add(RebuildIndexCommand());
        root.Subcommands.Add(EndToEndTest.Command());
        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    internal static ServerManager CreateManager(string? dataDir) =>
        new(
            new JsonSettingsStore(dataDir),
            new BackupService(TimeProvider.System),
            new HiddenConsoleLauncher(),
            new HelperProcessSignalSender(SignalHelperCommand.ForCurrentExecutable()),
            new WmiServerProcessLocator(),
            TimeProvider.System,
            NullLoggerFactory.Instance);

    private static ServerController? Resolve(ServerManager manager, string? name)
    {
        var profiles = manager.Profiles;
        ServerProfile? match = name is null
            ? profiles.FirstOrDefault(p => p.Id == manager.Settings.SelectedProfileId) ?? (profiles.Count == 1 ? profiles[0] : null)
            : profiles.FirstOrDefault(p => p.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
              ?? profiles.FirstOrDefault(p => p.Id.ToString().StartsWith(name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Console.Error.WriteLine(name is null
                ? "Informe --profile (há mais de um perfil ou nenhum selecionado)."
                : $"Perfil \"{name}\" não encontrado. Use 'vsm profiles'.");
            return null;
        }

        return manager.GetController(match.Id);
    }

    private static void Follow(ServerController controller)
    {
        controller.ActivityAdded += (_, a) => Console.WriteLine($"  {a.At:HH:mm:ss}  {a.Message}");
        controller.AlertRaised += (_, a) =>
        {
            if (a.Level >= AlertLevel.Warning)
            {
                Console.WriteLine($"  [{a.Level}] {a.Title}: {a.Message}");
            }
        };
    }

    private static void PrintChecks(OperationResult result)
    {
        Console.WriteLine(result.Message);
        foreach (var check in result.Checks.Where(c => c.Level != CheckLevel.Info))
        {
            Console.WriteLine($"  [{(check.Level == CheckLevel.Blocker ? "BLOQUEIO" : "CONFIRMAR")}] {check.Message}");
        }
    }

    // ------------------------------------------------------------------ commands

    private static Command ProfilesCommand()
    {
        var command = new Command("profiles", "Lista os perfis configurados.");
        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            await manager.RefreshRunningServersAsync(ct).ConfigureAwait(false);
            if (manager.Profiles.Count == 0)
            {
                Console.WriteLine("Nenhum perfil. Use 'vsm import <arquivo.bat>' ou o app.");
                return 0;
            }

            foreach (var controller in manager.Controllers)
            {
                var p = controller.Profile;
                var selected = p.Id == manager.Settings.SelectedProfileId ? "*" : " ";
                Console.WriteLine($"{selected} {p.DisplayName,-24} {controller.Status.State,-9} mundo={p.WorldName,-16} porta={p.Port} {(p.IsCreativeEffective ? "criativo" : "normal")}  id={p.Id.ToString()[..8]}");
            }

            foreach (var s in manager.UnmanagedServers)
            {
                Console.WriteLine($"! fora do gerenciador: \"{s.ServerName}\" mundo={s.WorldName} PID={s.ProcessId} savedir={s.SaveDirectory}");
            }

            return 0;
        });
        return command;
    }

    private static Command ImportCommand()
    {
        var file = new Argument<FileInfo>("arquivo") { Description = "Script .bat que inicia o valheim_server." };
        var saveDir = new Option<string?>("--save-dir") { Description = "Pasta de saves a usar se o .bat não tiver -savedir." };
        var name = new Option<string?>("--name") { Description = "Nome do perfil." };
        var command = new Command("import", "Cria um perfil a partir de um .bat.");
        command.Arguments.Add(file);
        command.Options.Add(saveDir);
        command.Options.Add(name);
        command.SetAction(async (parse, _) =>
        {
            var result = BatchFileImporter.ImportFile(parse.GetValue(file)!.FullName);
            var profile = result.Profile;
            profile.DisplayName = parse.GetValue(name) ?? profile.DisplayName;
            if (parse.GetValue(saveDir) is { } dir)
            {
                profile.SaveDirectory = dir;
            }

            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            manager.AddProfile(profile);
            Console.WriteLine($"Perfil \"{profile.DisplayName}\" criado (id {profile.Id}).");
            foreach (var note in result.Notes)
            {
                Console.WriteLine($"  obs.: {note}");
            }

            foreach (var issue in ProfileValidator.Validate(profile))
            {
                Console.WriteLine($"  [{issue.Severity}] {issue.Message}");
            }

            return 0;
        });
        return command;
    }

    private static Command StatusCommand()
    {
        var command = new Command("status", "Estado do servidor e do mundo.");
        command.Options.Add(ProfileOption);
        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            await manager.RefreshRunningServersAsync(ct).ConfigureAwait(false);
            if (Resolve(manager, parse.GetValue(ProfileOption)) is not { } controller)
            {
                return 2;
            }

            var p = controller.Profile;
            var s = controller.Status;
            Console.WriteLine($"Perfil:     {p.DisplayName}");
            Console.WriteLine($"Servidor:   {p.ServerName} (porta {p.Port}, {(p.Public ? "público" : "privado")}{(p.Crossplay ? ", crossplay" : string.Empty)})");
            Console.WriteLine($"Estado:     {s.State}{(s.ProcessId is { } pid ? $" (PID {pid})" : string.Empty)}{(s.IsActive ? $", {s.PlayerCount} jogador(es)" : string.Empty)}");
            Console.WriteLine($"Modo:       {((s.IsActive ? s.CreativeActive : p.IsCreativeEffective) ? "criativo" : "normal")}");
            if (s.JoinCode is not null)
            {
                Console.WriteLine($"Convite:    {s.JoinCode}");
            }

            var report = WorldInspector.Inspect(p.SaveDirectory, p.WorldName);
            Console.WriteLine($"Mundo:      {p.WorldName} — {(report.IsHealthy ? "íntegro" : report.IsNewWorld ? "não existe" : "COM PROBLEMA")}");
            if (report.LatestSave is { } save)
            {
                Console.WriteLine($"            save {save.Number}, {report.ChunkCount} chunks, {report.TotalZdos:N0} objetos, seed {report.Metadata?.SeedName}");
            }

            foreach (var issue in report.Issues.Where(i => i.Severity != IssueSeverity.Info))
            {
                Console.WriteLine($"  [{issue.Severity}] {issue.Message}");
            }

            return report.HasErrors ? 1 : 0;
        });
        return command;
    }

    private static Command StartCommand()
    {
        var yes = new Option<bool>("--yes", "-y") { Description = "Aceita avisos e criação de mundo novo." };
        var wait = new Option<bool>("--wait") { Description = "Aguarda o servidor ficar online." };
        var command = new Command("start", "Inicia o servidor (sem janela) com todas as verificações.");
        command.Options.Add(ProfileOption);
        command.Options.Add(yes);
        command.Options.Add(wait);
        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            await manager.RefreshRunningServersAsync(ct).ConfigureAwait(false);
            if (Resolve(manager, parse.GetValue(ProfileOption)) is not { } controller)
            {
                return 2;
            }

            Follow(controller);
            var options = parse.GetValue(yes) ? StartOptions.Confirmed : StartOptions.Default;
            var result = await controller.StartAsync(options, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                PrintChecks(result);
                return result.NeedsConfirmation ? 3 : 1;
            }

            if (!parse.GetValue(wait))
            {
                Console.WriteLine($"Iniciado (PID {controller.Status.ProcessId}). Ele continua rodando depois que este comando termina.");
                return 0;
            }

            while (controller.Status.State == ServerRunState.Starting)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            Console.WriteLine($"Estado: {controller.Status.State}");
            return controller.Status.State == ServerRunState.Running ? 0 : 1;
        });
        return command;
    }

    private static Command StopCommand()
    {
        var command = new Command("stop", "Desliga com segurança (Ctrl+C) e espera o save.");
        command.Options.Add(ProfileOption);
        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            await manager.RefreshRunningServersAsync(ct).ConfigureAwait(false);
            if (Resolve(manager, parse.GetValue(ProfileOption)) is not { } controller)
            {
                return 2;
            }

            if (!controller.Status.IsActive)
            {
                Console.WriteLine("O servidor não está rodando.");
                return 0;
            }

            Follow(controller);
            var result = await controller.StopAsync(ct).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            return result.Success ? 0 : 1;
        });
        return command;
    }

    private static Command BackupCommand()
    {
        var note = new Option<string?>("--note") { Description = "Anotação do backup." };
        var command = new Command("backup", "Faz um backup verificado do mundo (pode ser com o servidor rodando).");
        command.Options.Add(ProfileOption);
        command.Options.Add(note);
        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            if (Resolve(manager, parse.GetValue(ProfileOption)) is not { } controller)
            {
                return 2;
            }

            var entry = await manager.Backups.CreateAsync(controller.Profile, BackupKind.Manual, parse.GetValue(note), ct).ConfigureAwait(false);
            Console.WriteLine($"Backup verificado: {entry.Directory}");
            return 0;
        });
        return command;
    }

    private static Command BackupsCommand()
    {
        var command = new Command("backups", "Lista os backups do mundo.");
        command.Options.Add(ProfileOption);
        command.SetAction(async (parse, _) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            if (Resolve(manager, parse.GetValue(ProfileOption)) is not { } controller)
            {
                return 2;
            }

            foreach (var b in manager.Backups.List(controller.Profile))
            {
                Console.WriteLine($"{b.CreatedAt.LocalDateTime:dd/MM/yyyy HH:mm:ss}  save {b.SaveNumber?.ToString(CultureInfo.InvariantCulture) ?? "-",-5} {b.Kind,-11} {b.TotalBytes / 1024,8:N0} KB  {b.Name}");
            }

            return 0;
        });
        return command;
    }

    private static Command RestoreCommand()
    {
        var name = new Option<string>("--backup") { Description = "Nome da pasta do backup (veja 'vsm backups').", Required = true };
        var command = new Command("restore", "Restaura um backup (o servidor precisa estar parado).");
        command.Options.Add(ProfileOption);
        command.Options.Add(name);
        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            await manager.RefreshRunningServersAsync(ct).ConfigureAwait(false);
            if (Resolve(manager, parse.GetValue(ProfileOption)) is not { } controller)
            {
                return 2;
            }

            if (controller.Status.IsActive)
            {
                Console.Error.WriteLine("Pare o servidor antes ('vsm stop').");
                return 1;
            }

            var entry = manager.Backups.List(controller.Profile)
                .FirstOrDefault(b => b.Name.Equals(parse.GetValue(name), StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                Console.Error.WriteLine("Backup não encontrado.");
                return 2;
            }

            var result = await manager.Backups.RestoreAsync(controller.Profile, entry, ct).ConfigureAwait(false);
            Console.WriteLine($"Restaurado: save {entry.SaveNumber} de {entry.CreatedAt.LocalDateTime:g}.");
            if (result.SafetyCopy is not null)
            {
                Console.WriteLine($"Mundo anterior guardado em: {result.SafetyCopy.Directory}");
            }

            return 0;
        });
        return command;
    }

    private static Command InspectCommand()
    {
        var dir = new Argument<DirectoryInfo>("pasta-do-mundo") { Description = "Pasta do mundo (a que contém _main.N.*)." };
        var pieces = new Option<bool>("--pieces") { Description = "Procura peças construídas por jogadores." };
        var command = new Command("inspect", "Confere a integridade de uma pasta de mundo.");
        command.Arguments.Add(dir);
        command.Options.Add(pieces);
        command.SetAction(parse =>
        {
            var report = WorldInspector.InspectDirectory(parse.GetValue(dir)!.FullName);
            Console.WriteLine($"{report.WorldName}: {(report.IsHealthy ? "ÍNTEGRO" : report.IsNewWorld ? "sem save" : "COM PROBLEMA")}");
            if (report.LatestSave is { } save)
            {
                Console.WriteLine($"  save {save.Number}{(save.IsComplete ? string.Empty : " (INCOMPLETO)")}, {report.ChunkCount} chunks, {report.TotalZdos:N0} objetos, {report.SaveSetBytes / 1024:N0} KB");
            }

            if (report.Metadata is { } m)
            {
                Console.WriteLine($"  nome interno {m.Name}, seed {m.SeedName}, chaves [{string.Join(", ", m.KeyNames)}], {m.Players.Count} jogador(es)");
            }

            foreach (var issue in report.Issues)
            {
                Console.WriteLine($"  [{issue.Severity}] {issue.Message}");
            }

            if (parse.GetValue(pieces) && report.IsHealthy)
            {
                foreach (var (prefab, count) in PlayerBuildScanner.ScanWorld(report).OrderByDescending(kv => kv.Value))
                {
                    Console.WriteLine($"  {prefab,-24} {count}");
                }
            }

            return report.HasErrors ? 1 : 0;
        });
        return command;
    }

    private static Command RebuildIndexCommand()
    {
        var dir = new Argument<DirectoryInfo>("pasta-do-mundo");
        var number = new Option<int>("--save-number") { Description = "Número do save a gravar (_main.N.chunks).", Required = true };
        var chunks = new Option<string[]>("--chunks") { Description = "Arquivos .chunk a incluir (padrão: todos da pasta).", AllowMultipleArgumentsPerToken = true };
        var command = new Command("rebuild-index", "Recuperação: reconstrói _main.N.chunks a partir dos cabeçalhos dos chunks.");
        command.Arguments.Add(dir);
        command.Options.Add(number);
        command.Options.Add(chunks);
        command.SetAction(parse =>
        {
            var folder = parse.GetValue(dir)!.FullName;
            var selected = parse.GetValue(chunks) is { Length: > 0 } list
                ? list.Select(c => Path.Combine(folder, Path.GetFileName(c))).ToArray()
                : Directory.GetFiles(folder, "*.chunk");
            var index = ChunkIndexRebuilder.Build(selected);
            var target = Path.Combine(folder, $"_main.{parse.GetValue(number)}.chunks");
            if (File.Exists(target))
            {
                Console.Error.WriteLine($"{target} já existe; não vou sobrescrever.");
                return 1;
            }

            File.WriteAllBytes(target, index.ToBytes());
            Console.WriteLine($"Gravado {target}: {index.Entries.Count} chunks, {index.TotalZdos:N0} objetos.");
            return 0;
        });
        return command;
    }
}
