using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimServerManager.Cli.Localization;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Players;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Settings;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Cli;

internal static class CliApp
{
    // Descriptions are set in RunAsync, after Program.cs has applied the UI language.
    private static readonly Option<string?> ProfileOption = new("--profile", "-p");

    private static readonly Option<string?> DataDirOption = new("--data-dir")
    {
        Recursive = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        ProfileOption.Description = CliStrings.Option_Profile_Description;
        DataDirOption.Description = CliStrings.Option_DataDir_Description;

        var root = new RootCommand(CliStrings.Root_Description);
        root.Options.Add(DataDirOption);
        root.Subcommands.Add(ProfilesCommand());
        root.Subcommands.Add(CreateCommand());
        root.Subcommands.Add(StatusCommand());
        root.Subcommands.Add(SetCommand());
        root.Subcommands.Add(StartCommand());
        root.Subcommands.Add(StopCommand());
        root.Subcommands.Add(RestartCommand());
        root.Subcommands.Add(BackupCommand());
        root.Subcommands.Add(BackupsCommand());
        root.Subcommands.Add(RestoreCommand());
        root.Subcommands.Add(InspectCommand());
        root.Subcommands.Add(RebuildIndexCommand());
        root.Subcommands.Add(RepairWorldCommand());
        root.Subcommands.Add(CleanCheatMarksCommand());
        root.Subcommands.Add(CleanCharacterCommand());
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

    private static string F(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>A "Label:    value" row of the status block; labels are padded to a 12-column gutter.</summary>
    private static string Row(string label, string value) => (label + " ").PadRight(12) + value;

    private const string RowIndent = "            ";

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
                ? CliStrings.Resolve_ProfileRequired
                : F(CliStrings.Resolve_ProfileNotFound, name));
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
            Console.WriteLine($"  [{(check.Level == CheckLevel.Blocker ? CliStrings.Check_Blocker : CliStrings.Check_Confirm)}] {check.Message}");
        }
    }

    private static string ModeName(bool creative) => creative ? CliStrings.Mode_Creative : CliStrings.Mode_Normal;

    private static string YesNo(bool value) => value ? CliStrings.Common_Yes : CliStrings.Common_No;

    // ------------------------------------------------------------------ commands

    private static Command ProfilesCommand()
    {
        var command = new Command("profiles", CliStrings.Profiles_Description);
        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            await manager.RefreshRunningServersAsync(ct).ConfigureAwait(false);
            if (manager.Profiles.Count == 0)
            {
                Console.WriteLine(CliStrings.Profiles_None);
                return 0;
            }

            foreach (var controller in manager.Controllers)
            {
                var p = controller.Profile;
                var selected = p.Id == manager.Settings.SelectedProfileId ? "*" : " ";
                Console.WriteLine(F(CliStrings.Profiles_Line, selected, p.DisplayName, controller.Status.State, p.WorldName, p.Port,
                    ModeName(p.IsCreativeEffective), p.Id.ToString()[..8]));
            }

            foreach (var s in manager.UnmanagedServers)
            {
                Console.WriteLine(F(CliStrings.Profiles_Unmanaged, s.ServerName, s.WorldName, s.ProcessId, s.SaveDirectory));
            }

            return 0;
        });
        return command;
    }

    private static Command CreateCommand()
    {
        var name = new Option<string>("--name") { Required = true, Description = CliStrings.Create_Name_Description };
        var password = new Option<string>("--password") { Required = true, Description = CliStrings.Create_Password_Description };
        var world = new Option<string?>("--world") { Description = CliStrings.Create_World_Description };
        var seed = new Option<string?>("--seed") { Description = CliStrings.Create_Seed_Description };
        var copy = new Option<DirectoryInfo?>("--copy-world") { Description = CliStrings.Create_CopyWorld_Description };
        var port = new Option<int?>("--port") { Description = CliStrings.Create_Port_Description };
        var priv = new Option<bool>("--private") { Description = CliStrings.Create_Private_Description };
        var noCross = new Option<bool>("--no-crossplay") { Description = CliStrings.Create_NoCrossplay_Description };
        var saveDir = new Option<string?>("--save-dir") { Description = CliStrings.Create_SaveDir_Description };
        var serverDir = new Option<string?>("--server-dir") { Description = CliStrings.Create_ServerDir_Description };
        var preset = new Option<string?>("--preset") { Description = CliStrings.Create_Preset_Description };
        var creative = new Option<bool>("--creative") { Description = CliStrings.Create_Creative_Description };

        var command = new Command("create", CliStrings.Create_Description);
        foreach (var option in new Option[] { name, password, world, seed, copy, port, priv, noCross, saveDir, serverDir, preset, creative })
        {
            command.Options.Add(option);
        }

        command.SetAction(async (parse, ct) =>
        {
            var serverName = parse.GetValue(name)!;
            var profile = new ServerProfile
            {
                DisplayName = serverName,
                ServerName = serverName,
                Password = parse.GetValue(password)!,
                Port = parse.GetValue(port) ?? ServerProfile.DefaultPort,
                Public = !parse.GetValue(priv),
                Crossplay = !parse.GetValue(noCross),
                CreativeMode = parse.GetValue(creative),
                ServerDirectory = parse.GetValue(serverDir) is { } svd ? Path.GetFullPath(svd) : ServerManager.DetectServerInstallations().FirstOrDefault() ?? string.Empty,
                SaveDirectory = parse.GetValue(saveDir) is { } sd ? Path.GetFullPath(sd) : Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "Valheim Servers",
                    string.Concat(serverName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)), "ServerSave"),
            };

            if (parse.GetValue(preset) is { } presetName)
            {
                if (!ModifierCatalog.TryParse(ModifierCatalog.Presets, presetName, out WorldPreset p))
                {
                    Console.Error.WriteLine(F(CliStrings.Create_UnknownPreset, presetName));
                    return 2;
                }

                profile.Preset = p;
            }

            var source = parse.GetValue(copy);
            profile.WorldName = source is not null
                ? WorldInspector.InspectDirectory(source.FullName).Metadata?.Name ?? string.Empty
                : parse.GetValue(world) ?? new string(serverName.Where(char.IsAsciiLetterOrDigit).Take(20).ToArray());

            var seedName = parse.GetValue(seed) ?? WorldSeed.Random();
            var errors = ProfileValidator.Validate(profile).Where(i => i.Severity == ValidationSeverity.Error)
                .Select(i => i.Message).ToList();
            if (source is null && WorldSeed.Validate(seedName) is { } seedError)
            {
                errors.Add(seedError);
            }

            if (errors.Count > 0)
            {
                errors.ForEach(e => Console.Error.WriteLine("  " + F(CliStrings.Common_ErrorLine, e)));
                return 1;
            }

            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            if (manager.Profiles.Any(o => o.WorldName.Equals(profile.WorldName, StringComparison.OrdinalIgnoreCase) &&
                                          Core.Platform.ValheimPaths.SameDirectory(o.SaveDirectory, profile.SaveDirectory)))
            {
                Console.Error.WriteLine(CliStrings.Create_WorldInUse);
                return 1;
            }

            Directory.CreateDirectory(profile.SaveDirectory);
            if (source is not null)
            {
                var copied = WorldCreator.CopyExisting(source.FullName, profile.SaveDirectory);
                Console.WriteLine(F(CliStrings.Create_WorldCopied, copied.WorldName, copied.LatestSave!.Number, copied.Metadata!.SeedName));
            }
            else
            {
                WorldCreator.CreateSeeded(profile.SaveDirectory, profile.WorldName, seedName);
                Console.WriteLine(F(CliStrings.Create_WorldNew, profile.WorldName, seedName));
            }

            manager.AddProfile(profile);
            Console.WriteLine(F(CliStrings.Create_Created, profile.DisplayName, profile.Id.ToString()[..8], profile.Port));
            await Task.CompletedTask.ConfigureAwait(false);
            return 0;
        });
        return command;
    }

    private static Command StatusCommand()
    {
        var command = new Command("status", CliStrings.Status_Description);
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
            Console.WriteLine(Row(CliStrings.Status_ProfileLabel, p.DisplayName));
            Console.WriteLine(Row(CliStrings.Status_ServerLabel, F(CliStrings.Status_ServerValue, p.ServerName, p.Port,
                p.Public ? CliStrings.Status_Public : CliStrings.Status_Private, p.Crossplay ? ", crossplay" : string.Empty)));
            Console.WriteLine(Row(CliStrings.Status_StateLabel,
                $"{s.State}{(s.ProcessId is { } pid ? $" (PID {pid})" : string.Empty)}{(s.IsActive ? ", " + F(CliStrings.Common_Players, s.PlayerCount) : string.Empty)}"));
            Console.WriteLine(Row(CliStrings.Status_ModeLabel, ModeName(s.IsActive ? s.CreativeActive : p.IsCreativeEffective)));
            foreach (var line in ProfileSummary.Describe(p))
            {
                Console.WriteLine($"{line.Label + ":",-18}{line.Value}");
            }
            if (s.JoinCode is not null)
            {
                Console.WriteLine(Row(CliStrings.Status_InviteLabel, s.JoinCode));
            }

            if (s.IsActive)
            {
                await controller.VerifyConfigurationAsync().ConfigureAwait(false);
                var diffs = controller.Status.ConfigDifferences;
                if (diffs is { Count: 0 })
                {
                    Console.WriteLine(Row(CliStrings.Status_ConfigLabel, F(CliStrings.Status_ConfigMatches, controller.Status.ConfigCheckedCount)));
                }
                else if (diffs is not null)
                {
                    Console.WriteLine(Row(CliStrings.Status_ConfigLabel, CliStrings.Status_ConfigMismatch));
                    foreach (var d in diffs)
                    {
                        Console.WriteLine(RowIndent + F(CliStrings.Status_ConfigDifference, d.Setting, d.Actual, d.Expected));
                    }
                }
            }

            var report = WorldInspector.Inspect(p.SaveDirectory, p.WorldName);
            var health = report.IsHealthy ? CliStrings.Status_WorldHealthy
                : report.IsNewWorld ? CliStrings.Status_WorldMissing
                : CliStrings.World_HasProblem;
            Console.WriteLine(Row(CliStrings.Status_WorldLabel, $"{p.WorldName} — {health}"));
            if (report.LatestSave is { } save)
            {
                Console.WriteLine(RowIndent + F(CliStrings.Status_WorldSave, save.Number, report.ChunkCount, report.TotalZdos, report.Metadata?.SeedName));
            }

            foreach (var issue in report.Issues.Where(i => i.Severity != IssueSeverity.Info))
            {
                Console.WriteLine($"  [{issue.Severity}] {issue.Message}");
            }

            return report.HasErrors ? 1 : 0;
        });
        return command;
    }

    private static Command SetCommand()
    {
        var crossplay = new Option<bool?>("--crossplay") { Description = CliStrings.Set_Crossplay_Description };
        var isPublic = new Option<bool?>("--public") { Description = CliStrings.Set_Public_Description };
        var port = new Option<int?>("--port") { Description = CliStrings.Set_Port_Description };
        var password = new Option<string?>("--password") { Description = CliStrings.Set_Password_Description };
        var fixWorld = new Option<bool?>("--fix-world-after-stop") { Description = CliStrings.Set_FixWorld_Description };
        var command = new Command("set", CliStrings.Set_Description);
        command.Options.Add(ProfileOption);
        foreach (var option in new Option[] { crossplay, isPublic, port, password, fixWorld })
        {
            command.Options.Add(option);
        }

        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            if (Resolve(manager, parse.GetValue(ProfileOption)) is not { } controller)
            {
                return 2;
            }

            await Task.CompletedTask.ConfigureAwait(false);
            var profile = controller.Profile;
            var changes = new List<string>();
            if (parse.GetValue(crossplay) is { } cross && cross != profile.Crossplay)
            {
                profile.Crossplay = cross;
                changes.Add(F(CliStrings.Set_ChangeCrossplay, cross ? CliStrings.Common_On : CliStrings.Common_Off));
            }

            if (parse.GetValue(isPublic) is { } pub && pub != profile.Public)
            {
                profile.Public = pub;
                changes.Add(F(CliStrings.Set_ChangePublic, YesNo(pub)));
            }

            if (parse.GetValue(port) is { } p && p != profile.Port)
            {
                profile.Port = p;
                changes.Add(F(CliStrings.Set_ChangePort, p));
            }

            if (parse.GetValue(password) is { } pw && pw != profile.Password)
            {
                profile.Password = pw;
                changes.Add(CliStrings.Set_ChangePassword);
            }

            if (parse.GetValue(fixWorld) is { } fix && fix != profile.FixWorldAfterStop)
            {
                profile.FixWorldAfterStop = fix;
                changes.Add(F(CliStrings.Set_ChangeFixWorld, YesNo(fix)));
            }

            if (changes.Count == 0)
            {
                Console.WriteLine(CliStrings.Set_NothingToChange);
                return 0;
            }

            var errors = ProfileValidator.Validate(profile).Where(i => i.Severity == ValidationSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                errors.ForEach(e => Console.Error.WriteLine("  " + F(CliStrings.Common_ErrorLine, e.Message)));
                return 1;
            }

            manager.SaveProfile(profile);
            Console.WriteLine(F(CliStrings.Set_Updated, profile.DisplayName, string.Join(", ", changes)));
            if (controller.Status.IsActive)
            {
                Console.WriteLine(CliStrings.Set_RestartToApply);
            }

            return 0;
        });
        return command;
    }

    private static Command StartCommand()
    {
        var yes = new Option<bool>("--yes", "-y") { Description = CliStrings.Start_Yes_Description };
        var wait = new Option<bool>("--wait") { Description = CliStrings.Start_Wait_Description };
        var command = new Command("start", CliStrings.Start_Description);
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
                Console.WriteLine(F(CliStrings.Start_Started, controller.Status.ProcessId));
                return 0;
            }

            while (controller.Status.State == ServerRunState.Starting)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            Console.WriteLine(F(CliStrings.Common_State, controller.Status.State));
            return controller.Status.State == ServerRunState.Running ? 0 : 1;
        });
        return command;
    }

    private static Command StopCommand()
    {
        var command = new Command("stop", CliStrings.Stop_Description);
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
                Console.WriteLine(CliStrings.Stop_NotRunning);
                return 0;
            }

            Follow(controller);
            var result = await controller.StopAsync(ct).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            return result.Success ? 0 : 1;
        });
        return command;
    }

    private static Command RestartCommand()
    {
        var yes = new Option<bool>("--yes", "-y") { Description = CliStrings.Restart_Yes_Description };
        var command = new Command("restart", CliStrings.Restart_Description);
        command.Options.Add(ProfileOption);
        command.Options.Add(yes);
        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            if (Resolve(manager, parse.GetValue(ProfileOption)) is not { } controller)
            {
                return 2;
            }

            await manager.RefreshRunningServersAsync(ct).ConfigureAwait(false);
            Follow(controller);
            var result = await controller.RestartAsync(
                parse.GetValue(yes) ? StartOptions.Confirmed : StartOptions.Default, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                PrintChecks(result);
                return result.NeedsConfirmation ? 3 : 1;
            }

            while (controller.Status.State == ServerRunState.Starting)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            Console.WriteLine(F(CliStrings.Common_State, controller.Status.State));
            return controller.Status.State == ServerRunState.Running ? 0 : 1;
        });
        return command;
    }

    private static Command BackupCommand()
    {
        var note = new Option<string?>("--note") { Description = CliStrings.Backup_Note_Description };
        var command = new Command("backup", CliStrings.Backup_Description);
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
            Console.WriteLine(F(CliStrings.Backup_Done, entry.Directory));
            return 0;
        });
        return command;
    }

    private static Command BackupsCommand()
    {
        var command = new Command("backups", CliStrings.Backups_Description);
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
                Console.WriteLine(F(CliStrings.Backups_Line, b.CreatedAt.LocalDateTime,
                    b.SaveNumber?.ToString(CultureInfo.InvariantCulture) ?? "-", b.Kind, b.TotalBytes / 1024, b.Name));
            }

            return 0;
        });
        return command;
    }

    private static Command RestoreCommand()
    {
        var name = new Option<string>("--backup") { Description = CliStrings.Restore_Backup_Description, Required = true };
        var command = new Command("restore", CliStrings.Restore_Description);
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
                Console.Error.WriteLine(CliStrings.Restore_StopFirst);
                return 1;
            }

            var entry = manager.Backups.List(controller.Profile)
                .FirstOrDefault(b => b.Name.Equals(parse.GetValue(name), StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                Console.Error.WriteLine(CliStrings.Restore_NotFound);
                return 2;
            }

            var result = await manager.Backups.RestoreAsync(controller.Profile, entry, ct).ConfigureAwait(false);
            Console.WriteLine(F(CliStrings.Restore_Done, entry.SaveNumber, entry.CreatedAt.LocalDateTime));
            if (result.SafetyCopy is not null)
            {
                Console.WriteLine(F(CliStrings.Restore_SafetyCopy, result.SafetyCopy.Directory));
            }

            return 0;
        });
        return command;
    }

    private static Command InspectCommand()
    {
        var dir = new Argument<DirectoryInfo>(CliStrings.Argument_WorldFolder) { Description = CliStrings.Inspect_WorldFolder_Description };
        var pieces = new Option<bool>("--pieces") { Description = CliStrings.Inspect_Pieces_Description };
        var duplicates = new Option<bool>("--duplicates") { Description = CliStrings.Inspect_Duplicates_Description };
        var cheats = new Option<bool>("--cheats") { Description = CliStrings.Inspect_Cheats_Description };
        var command = new Command("inspect", CliStrings.Inspect_Description);
        command.Arguments.Add(dir);
        command.Options.Add(pieces);
        command.Options.Add(duplicates);
        command.Options.Add(cheats);
        command.SetAction(parse =>
        {
            var report = WorldInspector.InspectDirectory(parse.GetValue(dir)!.FullName);
            var health = report.IsHealthy ? CliStrings.Inspect_Healthy
                : report.IsNewWorld ? CliStrings.Inspect_NoSave
                : CliStrings.World_HasProblem;
            Console.WriteLine($"{report.WorldName}: {health}");
            if (report.LatestSave is { } save)
            {
                var incomplete = save.IsComplete ? string.Empty : $" ({CliStrings.Inspect_Incomplete})";
                Console.WriteLine("  " + F(CliStrings.Inspect_Save, save.Number, incomplete, report.ChunkCount, report.TotalZdos, report.SaveSetBytes / 1024));
            }

            if (report.Metadata is { } m)
            {
                Console.WriteLine("  " + F(CliStrings.Inspect_Metadata, m.Name, m.SeedName, string.Join(", ", m.KeyNames), m.Players.Count));
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

            if (parse.GetValue(duplicates) && report.IsHealthy)
            {
                PrintDuplicates(WorldRepair.ScanDirectory(report.Directory, report.WorldName));
            }

            if (parse.GetValue(cheats) && report.IsHealthy)
            {
                var marks = WorldCheatMarks.ScanDirectory(report.Directory, report.WorldName);
                Console.WriteLine("  " + F(CliStrings.Inspect_CheatMarks, marks.MarkedPieces, marks.MarkedOthers, marks.MarkedItems));
            }

            return report.HasErrors ? 1 : 0;
        });
        return command;
    }

    private static void PrintDuplicates(WorldDuplicateReport scan)
    {
        Console.WriteLine("  " + F(CliStrings.Duplicates_Summary, scan.ExtraCopies, scan.Objects, scan.ZonesWithDoubleSpawn, scan.ZonesToMark));
        foreach (var (category, count) in scan.ByCategory)
        {
            Console.WriteLine($"    {count,8:N0}  {category}");
        }
    }

    private static Command RepairWorldCommand()
    {
        var command = new Command("repair-world", CliStrings.RepairWorld_Description);
        command.Options.Add(ProfileOption);
        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            if (Resolve(manager, parse.GetValue(ProfileOption)) is not { } controller)
            {
                return 2;
            }

            await manager.RefreshRunningServersAsync(ct).ConfigureAwait(false);
            var profile = controller.Profile;
            PrintDuplicates(WorldRepair.Scan(profile.SaveDirectory, profile.WorldName));
            var result = await controller.RepairWorldAsync(ct).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            foreach (var check in result.Checks)
            {
                Console.WriteLine($"  [{check.Level}] {check.Message}");
            }

            return result.Success ? 0 : 1;
        });
        return command;
    }

    private static Command CleanCheatMarksCommand()
    {
        var command = new Command("clean-cheat-marks", CliStrings.CleanCheatMarks_Description);
        command.Options.Add(ProfileOption);
        command.SetAction(async (parse, ct) =>
        {
            await using var manager = CreateManager(parse.GetValue(DataDirOption));
            if (Resolve(manager, parse.GetValue(ProfileOption)) is not { } controller)
            {
                return 2;
            }

            await manager.RefreshRunningServersAsync(ct).ConfigureAwait(false);
            var result = await controller.CleanCheatMarksAsync(ct).ConfigureAwait(false);
            Console.WriteLine(result.Message);
            foreach (var check in result.Checks)
            {
                Console.WriteLine($"  [{check.Level}] {check.Message}");
            }

            return result.Success ? 0 : 1;
        });
        return command;
    }

    private static Command CleanCharacterCommand()
    {
        var file = new Argument<FileInfo>(CliStrings.Argument_CharacterFile) { Description = CliStrings.CleanCharacter_File_Description };
        var apply = new Option<bool>("--apply") { Description = CliStrings.CleanCharacter_Apply_Description };
        var command = new Command("clean-character", CliStrings.CleanCharacter_Description);
        command.Arguments.Add(file);
        command.Options.Add(apply);
        command.SetAction(parse =>
        {
            var path = parse.GetValue(file)!.FullName;
            var scan = CharacterFile.Scan(path);
            Console.WriteLine(F(CliStrings.CleanCharacter_Scan, scan.Name, scan.Items, scan.MarkedItems));
            if (!parse.GetValue(apply) || !scan.NeedsCleaning)
            {
                return 0;
            }

            var backup = $"{path}.{CliStrings.CleanCharacter_BackupSuffix}-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup);
            var result = CharacterFile.Clean(path);
            Console.WriteLine(F(CliStrings.CleanCharacter_Done, result.MarkedItems, Path.GetFileName(backup)));
            return 0;
        });
        return command;
    }

    private static Command RebuildIndexCommand()
    {
        var dir = new Argument<DirectoryInfo>(CliStrings.Argument_WorldFolder);
        var number = new Option<int>("--save-number") { Description = CliStrings.RebuildIndex_SaveNumber_Description, Required = true };
        var chunks = new Option<string[]>("--chunks") { Description = CliStrings.RebuildIndex_Chunks_Description, AllowMultipleArgumentsPerToken = true };
        var command = new Command("rebuild-index", CliStrings.RebuildIndex_Description);
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
                Console.Error.WriteLine(F(CliStrings.RebuildIndex_TargetExists, target));
                return 1;
            }

            File.WriteAllBytes(target, index.ToBytes());
            Console.WriteLine(F(CliStrings.RebuildIndex_Written, target, index.Entries.Count, index.TotalZdos));
            return 0;
        });
        return command;
    }
}
