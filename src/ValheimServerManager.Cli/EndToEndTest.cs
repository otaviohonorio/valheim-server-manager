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
        var serverDir = new Option<string>("--server-dir") { Required = true, Description = "Valheim Dedicated Server installation." };
        var source = new Option<string>("--world-source") { Required = true, Description = "A world folder (it will be copied)." };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 2466 };
        var work = new Option<string?>("--work-dir") { Description = "Working folder (default: temporary)." };
        var keep = new Option<bool>("--keep") { Description = "Don't delete the working folder at the end." };
        var command = new Command("e2e", "End-to-end test with the real server, on a copy of the world.");
        command.Options.Add(serverDir);
        command.Options.Add(source);
        command.Options.Add(port);
        command.Options.Add(work);
        command.Options.Add(keep);
        command.SetAction(async (parse, ct) => await RunAsync(
            Path.GetFullPath(parse.GetValue(serverDir)!), Path.GetFullPath(parse.GetValue(source)!), parse.GetValue(port),
            parse.GetValue(work) is { } w ? Path.GetFullPath(w) : null, parse.GetValue(keep), ct).ConfigureAwait(false));
        return command;
    }

    private static async Task<int> RunAsync(string serverDir, string source, int port, string? workDir, bool keep, CancellationToken ct)
    {
        var root = workDir ?? Path.Combine(Path.GetTempPath(), "vsm-e2e-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var sourceReport = WorldInspector.InspectDirectory(source);
        if (!sourceReport.IsHealthy)
        {
            Console.Error.WriteLine("The source world is not healthy.");
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
        Console.WriteLine($"Working folder: {root}");
        Console.WriteLine($"World {worldName}, save {sourceReport.LatestSave!.Number}, {sourceReport.TotalZdos:N0} objects\n");

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
                Console.WriteLine($"      exception: {ex.Message}");
                ok = false;
            }

            if (!ok && controller.Status.IsActive)
            {
                Console.WriteLine("      (stopping the server that was left running)");
                await controller.StopAsync(ct).ConfigureAwait(false);
            }

            Console.WriteLine($"  {(ok ? "PASSED" : "FAILED")} ({sw.Elapsed.TotalSeconds:N1} s)\n");
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
            Console.WriteLine($"      {result.Message} (save {status.LastSaveNumber}, exit {status.LastExit?.ExitCode})");
            return result.Success && status.LastExit is { Clean: true } && status.LastSaveNumber is not null && status.LastSaveNumber != before;
        }

        bool WorldHasCreativeKey()
        {
            var report = WorldInspector.Inspect(profile.SaveDirectory, worldName);
            Console.WriteLine($"      save {report.LatestSave?.Number}: keys [{string.Join(", ", report.Metadata?.KeyNames ?? [])}]");
            return report.Metadata?.IsCreative ?? false;
        }

        await Step("Repair duplicate objects (the server loads the result in the next steps)", async () =>
        {
            var before = WorldRepair.Scan(profile.SaveDirectory, worldName);
            var result = await controller.RepairWorldAsync(ct).ConfigureAwait(false);
            var after = WorldRepair.Scan(profile.SaveDirectory, worldName);
            Console.WriteLine($"      before: {before.ExtraCopies:N0} copies, {before.ZonesToMark} zones; {result.Message}");
            return result.Success && !after.NeedsRepair && after.Objects == before.Objects - before.ExtraCopies;
        }).ConfigureAwait(false);

        await Step("Start in normal mode (with a backup first)", async () =>
            await StartAndWait().ConfigureAwait(false) &&
            manager.Backups.List(profile).Any(b => b.Kind == BackupKind.PreStart)).ConfigureAwait(false);

        await Step("Manual backup while the server is running", async () =>
        {
            var entry = await manager.Backups.CreateAsync(profile, BackupKind.Manual, "e2e while running", ct).ConfigureAwait(false);
            return (await manager.Backups.VerifyAsync(entry, ct).ConfigureAwait(false)).Ok;
        }).ConfigureAwait(false);

        await Step("Stop safely (Ctrl+C) and confirm the save", StopClean).ConfigureAwait(false);

        await Step("Healthy world and a backup after stopping", () => Task.FromResult(
            WorldInspector.Inspect(profile.SaveDirectory, worldName).IsHealthy &&
            manager.Backups.List(profile).Any(b => b.Kind == BackupKind.PostStop))).ConfigureAwait(false);

        await Step("Creative mode: nobuildcost written to the world", async () =>
        {
            var p = controller.Profile;
            p.CreativeMode = true;
            manager.SaveProfile(p);
            return await StartAndWait().ConfigureAwait(false) && await StopClean().ConfigureAwait(false) && WorldHasCreativeKey();
        }).ConfigureAwait(false);

        await Step("Back to normal: nobuildcost removed from the world", async () =>
        {
            var p = controller.Profile;
            p.CreativeMode = false;
            manager.SaveProfile(p);
            return await StartAndWait().ConfigureAwait(false) && await StopClean().ConfigureAwait(false) && !WorldHasCreativeKey();
        }).ConfigureAwait(false);

        await Step("Every non-default option reaches the server", async () =>
        {
            var p = controller.Profile;
            p.ServerName = "VSM E2E Opcoes " + Environment.ProcessId;
            p.Port = port + 2;
            p.Password = "opcoesTeste77";
            p.Public = false;
            p.Crossplay = true;
            p.SaveIntervalSeconds = 600;
            p.GameBackupCount = 6;
            p.GameBackupShortSeconds = 3600;
            p.GameBackupLongSeconds = 21600;
            p.Preset = WorldPreset.Casual;
            p.Combat = CombatLevel.Hard;
            p.DeathPenalty = DeathPenaltyLevel.Easy;
            p.Resources = ResourceRate.MuchMore;
            p.Raids = RaidFrequency.None;
            p.Portals = PortalRule.Hard;
            p.PlayerEvents = true;
            p.PassiveMobs = true;
            p.NoMap = true;
            p.CreativeMode = true;
            p.ExtraArguments = "-instanceid 7";
            manager.SaveProfile(p);
            Console.WriteLine("      " + LaunchArguments.BuildDisplay(p));

            if (!await StartAndWait().ConfigureAwait(false))
            {
                return false;
            }

            var checkedOk = await WaitFor(() => controller.Status.ConfigDifferences is not null, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var diffs = controller.Status.ConfigDifferences ?? [];
            foreach (var d in diffs)
            {
                Console.WriteLine($"      DIFFERENCE {d.Setting}: in use {d.Actual}; saved {d.Expected}");
            }

            string log;
            using (var stream = new FileStream(profile.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                log = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            foreach (var line in log.Split('\n').Where(l => l.Contains("Setting world modifier", StringComparison.Ordinal)))
            {
                Console.WriteLine("      log: " + line.Trim());
            }

            Console.WriteLine($"      {controller.Status.ConfigCheckedCount} options checked; join code {controller.Status.JoinCode ?? "(none yet)"}");
            var stopped = await StopClean().ConfigureAwait(false);
            var meta = WorldInspector.Inspect(profile.SaveDirectory, worldName).Metadata;
            Console.WriteLine($"      keys in the world: [{string.Join(", ", meta?.KeyNames ?? [])}]");
            var keysOk = meta is not null && new[] { "nobuildcost", "playerevents", "passivemobs", "nomap" }.All(meta.HasKey);
            return checkedOk && diffs.Count == 0 && stopped && keysOk;
        }).ConfigureAwait(false);

        await Step("Editing while the server runs is detected and restarting applies it", async () =>
        {
            if (!await StartAndWait().ConfigureAwait(false) ||
                !await WaitFor(() => controller.Status.ConfigDifferences is { Count: 0 }, TimeSpan.FromSeconds(30)).ConfigureAwait(false))
            {
                return false;
            }

            var p = controller.Profile;
            p.Password = "senhaEditada55";
            p.Resources = ResourceRate.Less;
            p.NoMap = false;
            manager.SaveProfile(p);
            var detected = await WaitFor(() => controller.Status.ConfigDifferences is { Count: 3 }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            foreach (var d in controller.Status.ConfigDifferences ?? [])
            {
                Console.WriteLine($"      pending: {d.Setting}: in use {d.Actual}; saved {d.Expected}");
            }

            var restart = await controller.RestartAsync(StartOptions.Default, ct).ConfigureAwait(false);
            var online = restart.Success && await WaitFor(() => controller.Status.State == ServerRunState.Running, TimeSpan.FromMinutes(4)).ConfigureAwait(false);
            var applied = online && await WaitFor(() => controller.Status.ConfigDifferences is { Count: 0 }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            Console.WriteLine($"      after restarting: {controller.Status.ConfigDifferences?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} difference(s)");
            var stopped = await StopClean().ConfigureAwait(false);

            // Back to the simple profile for the remaining steps.
            var reset = controller.Profile;
            reset.ServerName = "VSM-E2E-" + Environment.ProcessId;
            reset.Port = port;
            reset.Crossplay = false;
            reset.Preset = WorldPreset.Normal;
            reset.Combat = CombatLevel.Default;
            reset.DeathPenalty = DeathPenaltyLevel.Default;
            reset.Resources = ResourceRate.Default;
            reset.Raids = RaidFrequency.Default;
            reset.Portals = PortalRule.Default;
            reset.PlayerEvents = false;
            reset.PassiveMobs = false;
            reset.CreativeMode = false;
            reset.ExtraArguments = string.Empty;
            reset.SaveIntervalSeconds = 1800;
            manager.SaveProfile(reset);
            return detected && applied && stopped;
        }).ConfigureAwait(false);

        BackupEntry? good = null;
        await Step("Refuses to start a world with the .db2 missing (the 09/16 scenario)", async () =>
        {
            good = manager.Backups.List(controller.Profile).First(b => b.Kind == BackupKind.PostStop);
            var latest = WorldInspector.Inspect(profile.SaveDirectory, worldName).LatestSave!;
            File.Delete(latest.Db2!);
            var result = await controller.StartAsync(StartOptions.Confirmed, ct).ConfigureAwait(false);
            return !result.Success &&
                   result.Checks.Any(c => c.Code == "WORLD_INCOMPLETE_LATEST_SAVE") &&
                   controller.Status.State == ServerRunState.Stopped;
        }).ConfigureAwait(false);

        await Step("Restore the backup and start again", async () =>
        {
            var result = await manager.Backups.RestoreAsync(profile, good!, ct).ConfigureAwait(false);
            Console.WriteLine($"      safety copy: {result.SafetyCopy?.Kind}; old folder: {Path.GetFileName(result.ReplacedFolder)}");
            return result.SafetyCopy?.Kind == BackupKind.Quarantine &&
                   await StartAndWait().ConfigureAwait(false) &&
                   await StopClean().ConfigureAwait(false);
        }).ConfigureAwait(false);

        ServerController? seeded = null;
        await Step("New world with the chosen seed", async () =>
        {
            var p = new ServerProfile
            {
                DisplayName = "E2E Seed",
                ServerDirectory = serverDir,
                SaveDirectory = Path.Combine(root, "ServerSaveSeed"),
                BackupDirectory = Path.Combine(root, "backupsSeed"),
                ServerName = "VSM-E2E-Seed-" + Environment.ProcessId,
                WorldName = "SeedTeste",
                Port = port + 20,
                Password = "seedSenha42",
                Public = false,
            };
            WorldCreator.CreateSeeded(p.SaveDirectory, p.WorldName, "VSMe2e2026");
            var before = WorldInspector.Inspect(p.SaveDirectory, p.WorldName);
            Console.WriteLine($"      before: {string.Join(" ", before.Issues.Select(i => i.Code))}");
            manager.AddProfile(p);
            seeded = manager.GetController(p.Id);
            var start = await seeded.StartAsync(StartOptions.Default, ct).ConfigureAwait(false);
            if (!start.Success)
            {
                Console.WriteLine($"      {start.Message} {string.Join(" | ", start.Checks.Select(c => c.Message))}");
                return false;
            }

            var online = await WaitFor(() => seeded.Status.State == ServerRunState.Running, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            var stop = online && (await seeded.StopAsync(ct).ConfigureAwait(false)).Success;
            var after = WorldInspector.Inspect(p.SaveDirectory, p.WorldName);
            var meta = after.Metadata;
            Console.WriteLine($"      after: save {after.LatestSave?.Number}, seed {meta?.SeedName}, hash matches {meta?.Seed == StableHash.Compute("VSMe2e2026")}, {after.TotalZdos:N0} objects, emergency: {seeded.Status.Emergency ?? "none"}");
            return online && stop && after.IsHealthy && meta?.SeedName == "VSMe2e2026" &&
                   meta.Seed == StableHash.Compute("VSMe2e2026") && seeded.Status.Emergency is null;
        }).ConfigureAwait(false);

        await Step("Two servers at the same time, with the locks", async () =>
        {
            if (seeded is null)
            {
                return false;
            }

            var bothOnline = await StartAndWait().ConfigureAwait(false) &&
                             (await seeded.StartAsync(StartOptions.Default, ct).ConfigureAwait(false)).Success &&
                             await WaitFor(() => seeded.Status.State == ServerRunState.Running, TimeSpan.FromMinutes(4)).ConfigureAwait(false);
            Console.WriteLine($"      running together: {bothOnline} (ports {controller.Profile.Port} and {seeded.Profile.Port})");

            // Same world and save folder as the first server, different port: must be refused.
            var sameWorld = controller.Profile.Duplicate("E2E same world");
            sameWorld.Port = port + 40;
            manager.AddProfile(sameWorld);
            var r1 = await manager.GetController(sameWorld.Id).StartAsync(StartOptions.Confirmed, ct).ConfigureAwait(false);
            var worldLocked = !r1.Success && r1.Checks.Any(c => c.Code == "WORLD_IN_USE");
            Console.WriteLine($"      same world refused: {worldLocked} ({r1.Checks.FirstOrDefault(c => c.Level == CheckLevel.Blocker)?.Message})");

            // Another world on the port of the second server: must be refused.
            var samePort = new ServerProfile
            {
                DisplayName = "E2E same port",
                ServerDirectory = serverDir,
                SaveDirectory = Path.Combine(root, "ServerSavePort"),
                ServerName = "VSM-E2E-Porta",
                WorldName = "OutroMundo",
                Port = seeded.Profile.Port,
                Password = "portaSenha42",
                Public = false,
            };
            WorldCreator.CreateSeeded(samePort.SaveDirectory, samePort.WorldName, "Porta123");
            manager.AddProfile(samePort);
            var r2 = await manager.GetController(samePort.Id).StartAsync(StartOptions.Confirmed, ct).ConfigureAwait(false);
            var portLocked = !r2.Success && r2.Checks.Any(c => c.Code is "PORT_IN_USE" or "PORT_BUSY");
            Console.WriteLine($"      same port refused: {portLocked} ({r2.Checks.FirstOrDefault(c => c.Level == CheckLevel.Blocker)?.Message})");

            var stopped = (await controller.StopAsync(ct).ConfigureAwait(false)).Success &
                          (await seeded.StopAsync(ct).ConfigureAwait(false)).Success;
            return bothOnline && worldLocked && portLocked && stopped;
        }).ConfigureAwait(false);

        foreach (var c in manager.Controllers.Where(c => c.Status.IsActive))
        {
            await c.ForceKillAsync().ConfigureAwait(false);
        }

        Console.WriteLine(failures == 0 ? "ALL STEPS PASSED" : $"{failures} STEP(S) FAILED");
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
