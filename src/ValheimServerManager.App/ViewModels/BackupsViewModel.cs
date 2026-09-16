using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Servers;

namespace ValheimServerManager.App.ViewModels;

public sealed partial class BackupItemViewModel : ObservableObject
{
    public BackupItemViewModel(BackupEntry entry, bool matchesWorld)
    {
        Entry = entry;
        MatchesWorld = matchesWorld;
    }

    public BackupEntry Entry { get; }

    public bool MatchesWorld { get; }

    public string Title => Entry.Kind == BackupKind.Quarantine
        ? $"{Entry.WorldName} — cópia do mundo com defeito"
        : $"{Entry.WorldName} — save {Entry.SaveNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}";

    public string KindText => Helpers.Ui.KindText(Entry.Kind);

    public string Glyph => Helpers.Ui.KindGlyph(Entry.Kind);

    public string When => Helpers.Ui.When(Entry.CreatedAt);

    public string SizeText => Helpers.Ui.Size(Entry.TotalBytes);

    public string Details
    {
        get
        {
            var parts = new List<string> { KindText, SizeText };
            if (Entry.Manifest is { } m)
            {
                parts.Add($"{Helpers.Ui.Number(m.TotalZdos)} objetos");
                if (m.CreativeKey)
                {
                    parts.Add("modo criativo");
                }
            }
            else if (Entry.Kind != BackupKind.GameAuto)
            {
                parts.Add("sem manifesto");
            }

            if (!MatchesWorld)
            {
                parts.Add("outro mundo");
            }

            return string.Join(" · ", parts);
        }
    }

    public string Note => Entry.Manifest?.Note ?? string.Empty;

    public bool CanRestore => MatchesWorld && Entry.Kind != BackupKind.Quarantine;

    public bool CanDelete => Entry.Kind != BackupKind.GameAuto;

    [ObservableProperty]
    public partial string VerifyText { get; set; } = string.Empty;
}

public sealed partial class BackupsViewModel : ProfilePageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;

    public BackupsViewModel(ProfileContext context, ServerManager manager, UiDispatcher ui, IDialogService dialogs, IShellService shell)
        : base(context, manager, ui)
    {
        _dialogs = dialogs;
        _shell = shell;
    }

    public ObservableCollection<BackupItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool ShowAllWorlds { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackupRoot { get; set; } = string.Empty;

    protected override void OnControllerChanged() => Refresh();

    protected override void OnActivity(ServerActivity activity)
    {
        if (activity.Kind == ActivityKind.Backup)
        {
            Refresh();
        }
    }

    partial void OnShowAllWorldsChanged(bool value) => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        Items.Clear();
        if (Profile is not { } profile || string.IsNullOrWhiteSpace(profile.SaveDirectory))
        {
            Summary = "Configure a pasta de saves para ver os backups.";
            return;
        }

        BackupRoot = profile.EffectiveBackupDirectory;
        IReadOnlyList<BackupEntry> entries;
        try
        {
            entries = Manager.Backups.List(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = $"Não foi possível listar os backups: {ex.Message}";
            return;
        }

        foreach (var entry in entries)
        {
            var matches = entry.WorldName.Equals(profile.WorldName, StringComparison.OrdinalIgnoreCase);
            if (matches || ShowAllWorlds)
            {
                Items.Add(new BackupItemViewModel(entry, matches));
            }
        }

        var mine = entries.Where(e => e.WorldName.Equals(profile.WorldName, StringComparison.OrdinalIgnoreCase)).ToArray();
        Summary = mine.Length == 0
            ? $"Nenhum backup do mundo \"{profile.WorldName}\" ainda."
            : $"{mine.Length} backup(s) de \"{profile.WorldName}\", {Helpers.Ui.Size(mine.Sum(e => e.TotalBytes))} no total. Mais recente: {Helpers.Ui.When(mine[0].CreatedAt)}.";
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        var note = await _dialogs.PromptAsync("Backup manual", "Quer anotar algo sobre este backup? (opcional)", "Ex.: antes de enfrentar a Moder");
        if (note is null)
        {
            return;
        }

        await RunBusyAsync("Copiando e verificando…", async () =>
        {
            try
            {
                var entry = await Manager.Backups.CreateAsync(controller.Profile, BackupKind.Manual, string.IsNullOrWhiteSpace(note) ? null : note);
                controller.RecordBackup(entry);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                await _dialogs.AlertAsync("Backup não realizado", ex.Message);
            }
        });
        Refresh();
    }

    [RelayCommand]
    private async Task RestoreAsync(BackupItemViewModel? item)
    {
        if (item is null || Controller is not { } controller)
        {
            return;
        }

        var profile = controller.Profile;
        var message =
            $"Restaurar \"{item.Title}\" ({item.When}) no mundo \"{profile.WorldName}\"?\n\n" +
            "• O mundo atual é copiado antes (ou guardado em quarentena, se estiver com defeito).\n" +
            "• A pasta atual é movida para \"backups\\_substituidos\" — nada é apagado.\n" +
            "• O backup é verificado antes e depois da cópia.";

        if (controller.Status.IsActive)
        {
            message += controller.Status.PlayerCount > 0
                ? $"\n\nO servidor está rodando com {controller.Status.PlayerCount} jogador(es). Ele será desligado com segurança primeiro."
                : "\n\nO servidor está rodando e será desligado com segurança primeiro.";
        }

        if (!await _dialogs.ConfirmAsync("Restaurar backup", message, "Restaurar"))
        {
            return;
        }

        await RunBusyAsync("Restaurando…", async () =>
        {
            if (controller.Status.IsActive)
            {
                BusyText = "Desligando o servidor com segurança…";
                var stop = await controller.StopAsync();
                if (!stop.Success && controller.Status.IsActive)
                {
                    await _dialogs.AlertAsync("Restauração cancelada", "O servidor não desligou. Pare-o pelo Painel e tente de novo.");
                    return;
                }
            }

            BusyText = "Verificando e copiando o backup…";
            try
            {
                var result = await Manager.Backups.RestoreAsync(controller.Profile, item.Entry);
                var safety = result.SafetyCopy is null
                    ? string.Empty
                    : $"\n\nO mundo anterior foi guardado em \"{result.SafetyCopy.Name}\".";
                await _dialogs.AlertAsync("Mundo restaurado",
                    $"O mundo \"{profile.WorldName}\" agora é o save {item.Entry.SaveNumber} de {item.When}.{safety}\n\nInicie o servidor pelo Painel quando quiser.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                await _dialogs.AlertAsync("Não foi possível restaurar", ex.Message);
            }
        });
        Refresh();
    }

    [RelayCommand]
    private async Task VerifyAsync(BackupItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.VerifyText = "Verificando…";
        var result = await Manager.Backups.VerifyAsync(item.Entry);
        item.VerifyText = result.Ok ? "✓ Íntegro" : "✗ " + string.Join(" ", result.Problems);
    }

    [RelayCommand]
    private async Task DeleteAsync(BackupItemViewModel? item)
    {
        if (item is null || Profile is not { } profile)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Excluir backup", $"Apagar definitivamente \"{item.Entry.Name}\"?", "Excluir", destructive: true))
        {
            return;
        }

        try
        {
            Manager.Backups.Delete(profile, item.Entry);
            Items.Remove(item);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            await _dialogs.AlertAsync("Não foi possível excluir", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenItem(BackupItemViewModel? item)
    {
        if (item is not null)
        {
            _shell.OpenFolder(item.Entry.Directory);
        }
    }

    [RelayCommand]
    private void OpenRoot()
    {
        if (!string.IsNullOrWhiteSpace(BackupRoot))
        {
            _shell.OpenFolder(BackupRoot);
        }
    }
}
