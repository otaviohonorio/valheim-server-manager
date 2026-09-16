using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using ValheimServerManager.App.Helpers;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.App.ViewModels;

public sealed record ActivityItem(string Time, string Message, string Glyph);

public sealed record SummaryItem(string Label, string Value, string Section, bool IsCustomized);

public sealed partial class DashboardViewModel : ProfilePageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private DispatcherQueueTimer? _clock;

    public DashboardViewModel(
        ProfileContext context, ServerManager manager, UiDispatcher ui, IDialogService dialogs, IShellService shell)
        : base(context, manager, ui)
    {
        _dialogs = dialogs;
        _shell = shell;
    }

    public event EventHandler<string>? NavigateRequested;

    public ObservableCollection<ActivityItem> Activity { get; } = [];

    /// <summary>What the server runs with, shown next to the start button.</summary>
    public ObservableCollection<SummaryItem> Summary { get; } = [];

    public void RequestNavigation(string section) => NavigateRequested?.Invoke(this, section);

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Uptime { get; set; } = "—";

    [ObservableProperty]
    public partial string LastSaveText { get; set; } = "—";

    [ObservableProperty]
    public partial string LastBackupText { get; set; } = "—";

    [ObservableProperty]
    public partial string ModeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModeDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCreative { get; set; }

    [ObservableProperty]
    public partial string ToggleModeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorldSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorldHealthTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorldHealthMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Controls.InfoBarSeverity WorldHealthSeverity { get; set; }

    [ObservableProperty]
    public partial bool CanStart { get; set; }

    [ObservableProperty]
    public partial bool CanStop { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChanges { get; set; }

    [ObservableProperty]
    public partial string ExitText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ConfigConfirmed { get; set; }

    [ObservableProperty]
    public partial string ConfigText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LastExitWasBad { get; set; }

    public string JoinCodeText => Status.JoinCode ?? "—";

    public string AddressText => Status.PublicAddress ?? (Profile is { } p ? $"porta {p.Port}" : "—");

    public string PlayersText => Status.IsActive ? Status.PlayerCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";

    public void StartClock()
    {
        _clock ??= Ui.CreateTimer(TimeSpan.FromSeconds(1), UpdateClock);
        _clock.Start();
    }

    protected override void OnControllerChanged()
    {
        Activity.Clear();
        if (Controller is not null)
        {
            foreach (var item in Controller.RecentActivity.Take(60))
            {
                Activity.Add(ToItem(item));
            }
        }

        RefreshWorld();
    }

    protected override void OnProfileSaved() => OnServerStatusChanged(Status);

    protected override void OnServerStatusChanged(ServerStatus status)
    {
        var profile = Profile;
        RefreshSummary(profile);
        Title = profile?.DisplayName ?? string.Empty;
        Subtitle = profile is null ? string.Empty : $"{profile.ServerName} · mundo {profile.WorldName}";
        IsRunning = status.State == ServerRunState.Running;
        CanStart = profile is not null && !status.IsActive && !IsBusy;
        CanStop = status.State is ServerRunState.Running or ServerRunState.Starting && !IsBusy;
        var diffs = status.IsActive ? status.ConfigDifferences : null;
        HasPendingChanges = diffs is { Count: > 0 };
        ConfigConfirmed = diffs is { Count: 0 };
        ConfigText = diffs switch
        {
            null => string.Empty,
            { Count: 0 } => $"O servidor está usando exatamente a configuração salva ({status.ConfigCheckedCount} opções conferidas na linha de comando e no log).",
            _ => string.Join("\n", diffs.Select(d => $"• {d.Setting}: em uso {d.Actual}; salvo {d.Expected}")),
        };

        IsCreative = status.IsActive ? status.CreativeActive : profile?.IsCreativeEffective ?? false;
        ModeText = IsCreative ? "Modo criativo" : "Modo normal";
        ModeDetail = IsCreative
            ? "Construção e craft sem custo para todos. Conquistas bloqueadas enquanto estiver ligado."
            : "Custos normais. Conquistas liberadas (se personagem e mundo não tiverem marca de trapaça).";
        if (status.IsActive && status.ModeVerified is { } verified)
        {
            ModeDetail += verified ? " ✓ Confirmado no arquivo do mundo." : " ✗ O arquivo do mundo NÃO confere!";
        }

        ToggleModeText = IsCreative ? "Voltar ao modo normal" : "Ativar modo criativo";

        LastSaveText = status.LastSaveAt is null
            ? "Nenhum save nesta sessão"
            : $"Save {status.LastSaveNumber} · {Helpers.Ui.Ago(status.LastSaveAt)}";
        LastBackupText = status.LastBackupAt is null ? LastBackupFromDisk() : $"{Helpers.Ui.Ago(status.LastBackupAt)}";

        LastExitWasBad = status is { IsActive: false, LastExit.Clean: false };
        ExitText = status.LastExit is { } exit ? $"{exit.Reason} ({Helpers.Ui.When(exit.At)})" : string.Empty;

        OnPropertyChanged(nameof(JoinCodeText));
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(PlayersText));
        UpdateClock();

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        if (status.State == ServerRunState.Stopped && !IsBusy)
        {
            RefreshWorld();
        }
    }

    protected override void OnActivity(ServerActivity activity)
    {
        Activity.Insert(0, ToItem(activity));
        while (Activity.Count > 60)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }

        if (activity.Kind is ActivityKind.Save or ActivityKind.Backup)
        {
            RefreshWorld();
        }
    }

    protected override void OnBusyChanged(bool value) => OnServerStatusChanged(Status);

    private void RefreshSummary(Core.Profiles.ServerProfile? profile)
    {
        var lines = profile is null ? [] : Core.Profiles.ProfileSummary.Describe(profile);
        var items = lines.Select(l => new SummaryItem(l.Label, l.Value, l.Section, l.IsCustomized)).ToArray();
        if (items.SequenceEqual(Summary))
        {
            return;
        }

        Summary.Clear();
        foreach (var item in items)
        {
            Summary.Add(item);
        }
    }

    private void UpdateClock()
    {
        Uptime = Status is { IsActive: true, StartedAt: { } started } ? Helpers.Ui.Duration(DateTimeOffset.Now - started) : "—";
        if (Status.LastSaveAt is not null)
        {
            LastSaveText = $"Save {Status.LastSaveNumber} · {Helpers.Ui.Ago(Status.LastSaveAt)}";
        }
    }

    private string LastBackupFromDisk()
    {
        if (Profile is not { } profile)
        {
            return "—";
        }

        try
        {
            var last = Manager.Backups.List(profile, includeGameAutoBackups: false)
                .FirstOrDefault(b => b.WorldName.Equals(profile.WorldName, StringComparison.OrdinalIgnoreCase) && b.Kind != BackupKind.Quarantine);
            return last is null ? "Nenhum backup ainda" : $"{Helpers.Ui.Ago(last.CreatedAt)} ({Helpers.Ui.KindText(last.Kind).ToLowerInvariant()})";
        }
        catch (IOException)
        {
            return "—";
        }
    }

    private void RefreshWorld()
    {
        if (Profile is not { } profile || string.IsNullOrWhiteSpace(profile.SaveDirectory) || string.IsNullOrWhiteSpace(profile.WorldName))
        {
            WorldHealthTitle = "Configure o servidor";
            WorldHealthMessage = "Defina a pasta de saves e o nome do mundo em \"Servidor\" e \"Mundo\".";
            WorldHealthSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
            WorldSummary = string.Empty;
            return;
        }

        WorldHealthReport report;
        try
        {
            report = WorldInspector.Inspect(profile.SaveDirectory, profile.WorldName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WorldHealthTitle = "Não foi possível ler o mundo";
            WorldHealthMessage = ex.Message;
            WorldHealthSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
            return;
        }

        if (report.HasErrors)
        {
            WorldHealthTitle = "Mundo com problema — não inicie";
            WorldHealthMessage = string.Join(" ", report.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));
            WorldHealthSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        }
        else if (report.Issues.Any(i => i.Code == "NEW_WORLD_SEEDED"))
        {
            WorldHealthTitle = "Mundo novo pronto para gerar";
            WorldHealthMessage = report.Issues.First(i => i.Code == "NEW_WORLD_SEEDED").Message;
            WorldHealthSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
        }
        else if (report.IsNewWorld)
        {
            WorldHealthTitle = "Mundo ainda não existe";
            WorldHealthMessage = report.Issues.FirstOrDefault()?.Message ?? "O servidor vai criar um mundo novo.";
            WorldHealthSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
        }
        else
        {
            WorldHealthTitle = "Mundo íntegro";
            WorldHealthMessage = $"Save {report.LatestSave!.Number} completo, {report.ChunkCount} chunks conferidos. " +
                                 $"Último save em disco: {Helpers.Ui.When(report.LastSavedUtc is { } u ? new DateTimeOffset(u) : null)}.";
            WorldHealthSeverity = report.HasWarnings
                ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning
                : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
        }

        WorldSummary = report.Metadata is { } meta
            ? $"Seed {meta.SeedName} · {Helpers.Ui.Number(report.TotalZdos)} objetos · {Helpers.Ui.Size(report.SaveSetBytes)}"
            : string.Empty;
        LastBackupText = Status.LastBackupAt is null ? LastBackupFromDisk() : Helpers.Ui.Ago(Status.LastBackupAt);
    }

    private static ActivityItem ToItem(ServerActivity a) => new(
        a.At.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        a.Message,
        a.Kind switch
        {
            ActivityKind.Player => "",
            ActivityKind.Save => "",
            ActivityKind.Backup => "",
            ActivityKind.Warning => "",
            ActivityKind.Error => "",
            _ => "",
        });

    // ----------------------------------------------------------------- commands

    private bool CanStartServer() => CanStart;

    private bool CanStopServer() => CanStop;

    [RelayCommand(CanExecute = nameof(CanStartServer))]
    private async Task StartAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        await RunBusyAsync("Verificando e fazendo backup…", async () =>
        {
            var result = await controller.StartAsync(StartOptions.Default);
            if (result.Success)
            {
                return;
            }

            if (result.NeedsConfirmation)
            {
                var isNewWorld = result.Checks.Any(c => c.Code == "WORLD_NEW_WORLD");
                var proceed = await _dialogs.ShowChecksAsync(
                    isNewWorld ? "Criar um mundo novo?" : "Confirme antes de iniciar",
                    isNewWorld
                        ? $"Não existe o mundo \"{controller.Profile.WorldName}\" nesta pasta de saves. Se você esperava encontrar um mundo aqui, cancele e confira o nome e a pasta."
                        : "Leia os avisos abaixo:",
                    result.Checks,
                    canProceed: true,
                    proceedText: isNewWorld ? "Criar mundo novo e iniciar" : "Iniciar mesmo assim");
                if (proceed)
                {
                    result = await controller.StartAsync(StartOptions.Confirmed);
                    if (result.Success)
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            var worldBroken = result.Checks.Any(c => c.Code.StartsWith("WORLD_", StringComparison.Ordinal) && c.Level == CheckLevel.Blocker);
            await _dialogs.ShowChecksAsync("O servidor não foi iniciado", result.Message, result.Checks, canProceed: false, proceedText: string.Empty);
            if (worldBroken)
            {
                NavigateRequested?.Invoke(this, "backups");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private async Task StopAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        if (Status.PlayerCount > 0 && !await _dialogs.ConfirmAsync(
                "Há jogadores online",
                $"{Status.PlayerCount} jogador(es) conectado(s) serão desconectados. O mundo será salvo antes de desligar.",
                "Desligar"))
        {
            return;
        }

        await RunBusyAsync("Salvando o mundo e desligando…", async () =>
        {
            var result = await controller.StopAsync();
            if (!result.Success && controller.Status.StopTimedOut)
            {
                await HandleStopTimeoutAsync(controller);
            }
            else if (!result.Success)
            {
                await _dialogs.AlertAsync("Desligamento", result.Message);
            }
        });
    }

    private async Task HandleStopTimeoutAsync(ServerController controller)
    {
        while (controller.Status.IsActive)
        {
            var choice = await _dialogs.ChooseAsync(
                "O servidor ainda não desligou",
                "Normalmente o save leva segundos, mas mundos grandes podem demorar. Forçar o encerramento perde tudo desde o último save.",
                "Esperar mais 2 minutos",
                "Forçar encerramento",
                "Deixar rodando");
            if (choice == 1)
            {
                BusyText = "Aguardando o servidor salvar…";
                var result = await controller.WaitForStopAsync(TimeSpan.FromMinutes(2));
                if (result.Success || !controller.Status.IsActive)
                {
                    return;
                }
            }
            else if (choice == 2)
            {
                if (await _dialogs.ConfirmAsync("Forçar encerramento?", "O progresso desde o último save será perdido. Continuar?", "Forçar", destructive: true))
                {
                    await controller.ForceKillAsync();
                    await controller.WaitForStopAsync(TimeSpan.FromSeconds(30));
                    return;
                }
            }
            else
            {
                return;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private async Task RestartAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        if (Status.PlayerCount > 0 && !await _dialogs.ConfirmAsync(
                "Reiniciar com jogadores online?",
                $"{Status.PlayerCount} jogador(es) serão desconectados por alguns segundos.",
                "Reiniciar"))
        {
            return;
        }

        await RunBusyAsync("Reiniciando com segurança…", async () =>
        {
            var result = await controller.RestartAsync(StartOptions.Default);
            if (!result.Success)
            {
                await _dialogs.ShowChecksAsync("Reinício não concluído", result.Message, result.Checks, false, string.Empty);
            }
        });
    }

    [RelayCommand]
    private async Task ToggleModeAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        var profile = controller.Profile;
        var goingCreative = !IsCreative;
        if (profile.Preset == Core.Profiles.WorldPreset.Hammer)
        {
            await _dialogs.AlertAsync("Preset Martelo", "O preset Martelo é sempre criativo. Troque o preset em \"Mundo\" para usar o modo normal.");
            return;
        }

        var message = goingCreative
            ? "Construção e craft ficam SEM CUSTO para todos que estiverem no servidor, e ninguém ganha conquistas enquanto o modo estiver ligado."
            : "Construção e craft voltam a custar recursos. A chave nobuildcost é removida do mundo no próximo início.";
        if (Status.IsActive)
        {
            message += Status.PlayerCount > 0
                ? $"\n\nO servidor será reiniciado agora e {Status.PlayerCount} jogador(es) serão desconectados por alguns segundos."
                : "\n\nO servidor será reiniciado agora (o mundo é salvo antes).";
        }

        if (!await _dialogs.ConfirmAsync(goingCreative ? "Ativar modo criativo?" : "Voltar ao modo normal?", message,
                Status.IsActive ? "Salvar e reiniciar" : "Confirmar"))
        {
            return;
        }

        profile.CreativeMode = goingCreative;
        Manager.SaveProfile(profile);

        if (Status.IsActive)
        {
            await RunBusyAsync(goingCreative ? "Reiniciando em modo criativo…" : "Reiniciando em modo normal…", async () =>
            {
                var result = await controller.RestartAsync(StartOptions.Default);
                if (!result.Success)
                {
                    await _dialogs.ShowChecksAsync("Reinício não concluído", result.Message, result.Checks, false, string.Empty);
                }
            });
        }

        OnServerStatusChanged(Status);
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        await RunBusyAsync("Copiando e verificando o mundo…", async () =>
        {
            try
            {
                var entry = await Manager.Backups.CreateAsync(controller.Profile, BackupKind.Manual, "Backup manual pelo painel");
                controller.RecordBackup(entry);
                RefreshWorld();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                await _dialogs.AlertAsync("Backup não realizado", ex.Message);
            }
        });
    }

    [RelayCommand]
    private void OpenWorldFolder()
    {
        if (Profile is { } p)
        {
            _shell.OpenFolder(WorldFolder.WorldDirectory(p.SaveDirectory, p.WorldName));
        }
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        if (Profile is { } p)
        {
            _shell.OpenFile(p.LogFilePath);
        }
    }

    [RelayCommand]
    private void CopyJoinCode()
    {
        if (Status.JoinCode is { } code)
        {
            _shell.CopyText(code);
        }
    }

    [RelayCommand]
    private void GoToBackups() => NavigateRequested?.Invoke(this, "backups");

    protected override void Dispose(bool disposing)
    {
        _clock?.Stop();
        base.Dispose(disposing);
    }
}
