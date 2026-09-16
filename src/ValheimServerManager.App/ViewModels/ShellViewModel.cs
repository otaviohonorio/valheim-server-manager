using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Platform;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Servers;

namespace ValheimServerManager.App.ViewModels;

/// <summary>One entry of the profile selector.</summary>
public sealed partial class ProfileItemViewModel : ObservableObject
{
    public ProfileItemViewModel(ServerController controller)
    {
        Controller = controller;
        Refresh();
    }

    public ServerController Controller { get; }

    public Guid Id => Controller.ProfileId;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ServerRunState State { get; set; }

    [ObservableProperty]
    public partial bool IsCreative { get; set; }

    public void Refresh()
    {
        var profile = Controller.Profile;
        var status = Controller.Status;
        DisplayName = profile.DisplayName;
        State = status.State;
        IsCreative = status.IsActive ? status.CreativeActive : profile.IsCreativeEffective;
        Detail = $"{profile.WorldName} · porta {profile.Port}";
    }
}

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ServerManager _manager;
    private readonly ProfileContext _context;
    private readonly UiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IPickerService _pickers;
    private readonly ILogger<ShellViewModel> _logger;
    private DispatcherQueueTimer? _scanTimer;

    public ShellViewModel(
        ServerManager manager,
        ProfileContext context,
        UiDispatcher ui,
        IDialogService dialogs,
        IPickerService pickers,
        AlertCenter alerts,
        ILogger<ShellViewModel> logger)
    {
        _manager = manager;
        _context = context;
        _ui = ui;
        _dialogs = dialogs;
        _pickers = pickers;
        _logger = logger;
        Alerts = alerts;
        _manager.ProfilesChanged += (_, _) => _ui.Run(RebuildProfiles);
        _manager.UnmanagedServersChanged += (_, list) => _ui.Run(() => UpdateUnmanaged(list));
        RebuildProfiles();
    }

    public AlertCenter Alerts { get; }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    [ObservableProperty]
    public partial ProfileItemViewModel? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial bool HasProfiles { get; set; }

    [ObservableProperty]
    public partial string UnmanagedMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasUnmanaged { get; set; }

    public bool AnyActive => _manager.AnyActive;

    public int ActiveCount => _manager.Controllers.Count(c => c.Status.IsActive);

    public void Start()
    {
        _scanTimer = _ui.CreateTimer(TimeSpan.FromSeconds(10), () => _ = ScanAsync());
        _scanTimer.Start();
        _ = ScanAsync();
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        if (value is not null && _context.SelectedProfileId != value.Id)
        {
            _context.SelectedProfileId = value.Id;
        }
    }

    private void RebuildProfiles()
    {
        foreach (var item in Profiles)
        {
            item.Controller.StatusChanged -= OnAnyStatus;
        }

        Profiles.Clear();
        foreach (var controller in _manager.Controllers.OrderBy(c => c.Profile.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            controller.StatusChanged += OnAnyStatus;
            Profiles.Add(new ProfileItemViewModel(controller));
        }

        HasProfiles = Profiles.Count > 0;
        if (_context.SelectedProfileId is null || Profiles.All(p => p.Id != _context.SelectedProfileId))
        {
            _context.SelectedProfileId = Profiles.FirstOrDefault()?.Id;
        }

        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == _context.SelectedProfileId);
    }

    private void OnAnyStatus(object? sender, ServerStatus status) => _ui.Run(() =>
    {
        Profiles.FirstOrDefault(p => ReferenceEquals(p.Controller, sender))?.Refresh();
        OnPropertyChanged(nameof(AnyActive));
        OnPropertyChanged(nameof(ActiveCount));
    });

    private async Task ScanAsync()
    {
        try
        {
            await _manager.RefreshRunningServersAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Falha ao procurar servidores em execução");
        }
    }

    private void UpdateUnmanaged(IReadOnlyList<RunningServer> servers)
    {
        HasUnmanaged = servers.Count > 0;
        UnmanagedMessage = servers.Count switch
        {
            0 => string.Empty,
            1 => DescribeUnmanaged(servers[0]),
            _ => $"{servers.Count} servidores Valheim estão rodando fora do gerenciador.",
        };
    }

    private static string DescribeUnmanaged(RunningServer s)
    {
        var name = s.ServerName ?? "sem nome";
        var shared = s.SaveDirectoryIsDefault
            ? " Ele grava na MESMA pasta do jogo — não abra esse mundo no jogo enquanto ele roda."
            : string.Empty;
        return $"O servidor \"{name}\" (mundo {s.WorldName}, PID {s.ProcessId}) está rodando fora do gerenciador.{shared}";
    }

    [RelayCommand]
    private async Task AdoptUnmanagedAsync()
    {
        var server = _manager.UnmanagedServers.FirstOrDefault();
        if (server is null)
        {
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Gerenciar este servidor",
            $"Criar um perfil a partir do servidor \"{server.ServerName}\" que já está rodando? " +
            "O gerenciador passa a acompanhar e pode desligá-lo com segurança.",
            "Criar perfil");
        if (!ok)
        {
            return;
        }

        var profile = _manager.AdoptRunningServer(server);
        _context.SelectedProfileId = profile.Id;
        await _manager.RefreshRunningServersAsync();
        if (server.SaveDirectoryIsDefault)
        {
            await _dialogs.AlertAsync(
                "Atenção à pasta de saves",
                "Este servidor usa a pasta de saves do próprio jogo. O perfil foi criado para acompanhá-lo, mas o gerenciador " +
                "não vai deixar iniciá-lo assim: pare com segurança, escolha uma pasta só do servidor em \"Servidor\" e mova o mundo para lá.");
        }
    }

    [RelayCommand]
    private async Task NewProfileAsync()
    {
        var name = await _dialogs.PromptAsync("Novo servidor", "Como você quer chamar este perfil?", "Ex.: Mundo principal");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var profile = new ServerProfile
        {
            DisplayName = name,
            ServerName = name,
            WorldName = "Mundo",
            ServerDirectory = ServerManager.DetectServerInstallations().FirstOrDefault() ?? string.Empty,
        };

        var documents = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        profile.SaveDirectory = Path.Combine(documents, "Valheim Server Manager", SafeFolder(name), "ServerSave");
        _manager.AddProfile(profile);
        _context.SelectedProfileId = profile.Id;
    }

    [RelayCommand]
    private async Task ImportBatchAsync()
    {
        var path = await _pickers.PickFileAsync(".bat", ".cmd");
        if (path is null)
        {
            return;
        }

        try
        {
            var result = BatchFileImporter.ImportFile(path);
            var profile = result.Profile;
            if (string.IsNullOrWhiteSpace(profile.ServerDirectory))
            {
                profile.ServerDirectory = ServerManager.DetectServerInstallations().FirstOrDefault() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(profile.SaveDirectory) || ValheimPaths.SameDirectory(profile.SaveDirectory, ValheimPaths.GameDataDirectory))
            {
                profile.SaveDirectory = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "ServerSave");
            }

            _manager.AddProfile(profile);
            _context.SelectedProfileId = profile.Id;

            if (result.Notes.Count > 0)
            {
                await _dialogs.AlertAsync("Importado com observações", string.Join("\n\n", result.Notes));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await _dialogs.AlertAsync("Não foi possível importar", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DuplicateProfileAsync()
    {
        var current = _context.Profile;
        if (current is null)
        {
            return;
        }

        var name = await _dialogs.PromptAsync("Duplicar perfil", "Nome do novo perfil:", "Nome", current.DisplayName + " (cópia)");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var copy = current.Duplicate(name);
        copy.Port = NextFreePort(current.Port);
        _manager.AddProfile(copy);
        _context.SelectedProfileId = copy.Id;
        await _dialogs.AlertAsync("Perfil duplicado",
            $"A porta foi trocada para {copy.Port} para os dois poderem rodar juntos. " +
            "Se for usar outro mundo, troque o nome do mundo em \"Mundo\".");
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        var controller = _context.Controller;
        if (controller is null)
        {
            return;
        }

        if (controller.Status.IsActive)
        {
            await _dialogs.AlertAsync("Servidor em execução", "Pare o servidor antes de excluir o perfil.");
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            "Excluir perfil",
            $"Excluir o perfil \"{controller.Profile.DisplayName}\"? Mundos e backups no disco NÃO são apagados.",
            "Excluir", destructive: true);
        if (ok)
        {
            await _manager.RemoveProfileAsync(controller.ProfileId);
        }
    }

    private int NextFreePort(int start)
    {
        var used = _manager.Profiles.Select(p => p.Port).ToHashSet();
        var port = start + 10;
        while (used.Contains(port) || used.Contains(port - 1) || used.Contains(port + 1))
        {
            port += 10;
        }

        return port;
    }

    private static string SafeFolder(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    public void Dispose()
    {
        _scanTimer?.Stop();
        foreach (var item in Profiles)
        {
            item.Controller.StatusChanged -= OnAnyStatus;
        }
    }
}
