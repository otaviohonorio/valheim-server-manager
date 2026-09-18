using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Platform;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Servers;

namespace ValheimServerManager.App.ViewModels;

public sealed record IssueItem(string Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity Severity);

/// <summary>Shared editing logic for pages that change the selected profile.</summary>
public abstract partial class ProfileEditorViewModel : ProfilePageViewModel
{
    private bool _loading;

    protected ProfileEditorViewModel(ProfileContext context, ServerManager manager, UiDispatcher ui, IDialogService dialogs)
        : base(context, manager, ui)
    {
        Dialogs = dialogs;
    }

    protected IDialogService Dialogs { get; }

    public ObservableCollection<IssueItem> Issues { get; } = [];

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial string SavedMessage { get; set; } = string.Empty;

    protected abstract void Load(ServerProfile profile);

    protected abstract void Apply(ServerProfile profile);

    protected virtual IEnumerable<string> RelevantFields => [];

    protected override void OnControllerChanged() => Reload();

    protected override void OnProfileSaved()
    {
        if (!IsDirty)
        {
            Reload();
        }
    }

    protected void Reload()
    {
        if (Profile is not { } profile)
        {
            return;
        }

        _loading = true;
        try
        {
            Load(profile);
        }
        finally
        {
            _loading = false;
        }

        IsDirty = false;
        Validate();
    }

    protected ServerProfile? Draft()
    {
        if (Profile is not { } profile)
        {
            return null;
        }

        Apply(profile);
        return profile;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(IsDirty) or nameof(SavedMessage) or nameof(Status) or nameof(IsBusy)
                or nameof(BusyText) or nameof(Profile) or nameof(HasProfile) or null)
        {
            return;
        }

        if (IsEditableProperty(e.PropertyName))
        {
            IsDirty = true;
            SavedMessage = string.Empty;
            Validate();
            OnDraftChanged();
        }
    }

    protected virtual bool IsEditableProperty(string propertyName) => true;

    protected virtual void OnDraftChanged()
    {
    }

    protected void Validate()
    {
        Issues.Clear();
        if (Draft() is not { } draft)
        {
            return;
        }

        var fields = RelevantFields.ToHashSet(StringComparer.Ordinal);
        foreach (var issue in ProfileValidator.Validate(draft).Where(i => fields.Count == 0 || fields.Contains(i.Field)))
        {
            Issues.Add(new IssueItem(issue.Message, issue.Severity == ValidationSeverity.Error
                ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error
                : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning));
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Draft() is not { } draft)
        {
            return;
        }

        Manager.SaveProfile(draft);
        IsDirty = false;
        SavedMessage = Status.IsActive
            ? "Salvo. As mudanças valem no próximo início do servidor."
            : "Salvo.";

        if (Status.IsActive && Controller is { HasPendingChanges: true } controller &&
            await Dialogs.ConfirmAsync("Aplicar agora?",
                "O servidor está rodando com a configuração anterior. Reiniciar agora para aplicar? O mundo é salvo antes." +
                (Status.PlayerCount > 0 ? $"\n\n{Status.PlayerCount} jogador(es) serão desconectados por alguns segundos." : string.Empty),
                "Reiniciar agora", "Depois"))
        {
            await RunBusyAsync("Reiniciando…", async () =>
            {
                var result = await controller.RestartAsync(StartOptions.Default);
                if (!result.Success)
                {
                    await Dialogs.ShowChecksAsync("Reinício não concluído", result.Message, result.Checks, false, string.Empty);
                }
                else
                {
                    SavedMessage = "Salvo e aplicado.";
                }
            });
        }
    }

    [RelayCommand]
    private void Discard()
    {
        Reload();
        SavedMessage = string.Empty;
    }
}

public sealed partial class ServerSettingsViewModel : ProfileEditorViewModel
{
    private readonly IPickerService _pickers;
    private readonly IShellService _shell;

    public ServerSettingsViewModel(
        ProfileContext context, ServerManager manager, UiDispatcher ui, IDialogService dialogs, IPickerService pickers, IShellService shell)
        : base(context, manager, ui, dialogs)
    {
        _pickers = pickers;
        _shell = shell;
    }

    [ObservableProperty] public partial string DisplayName { get; set; } = string.Empty;
    [ObservableProperty] public partial string ServerName { get; set; } = string.Empty;
    [ObservableProperty] public partial double Port { get; set; } = ServerProfile.DefaultPort;
    [ObservableProperty] public partial string Password { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsPublic { get; set; }
    [ObservableProperty] public partial bool Crossplay { get; set; }
    [ObservableProperty] public partial string ServerDirectory { get; set; } = string.Empty;
    [ObservableProperty] public partial string SaveDirectory { get; set; } = string.Empty;
    [ObservableProperty] public partial string BackupDirectory { get; set; } = string.Empty;
    [ObservableProperty] public partial double SaveIntervalMinutes { get; set; } = 30;
    [ObservableProperty] public partial double GameBackupCount { get; set; } = 4;
    [ObservableProperty] public partial double GameBackupShortHours { get; set; } = 2;
    [ObservableProperty] public partial double GameBackupLongHours { get; set; } = 12;
    [ObservableProperty] public partial bool BackupBeforeStart { get; set; }
    [ObservableProperty] public partial bool BackupAfterStop { get; set; }

    [ObservableProperty] public partial bool FixWorldAfterStop { get; set; }
    [ObservableProperty] public partial double AutoBackupRetention { get; set; } = 20;
    [ObservableProperty] public partial double StopTimeoutSeconds { get; set; } = 120;
    [ObservableProperty] public partial string ExtraArguments { get; set; } = string.Empty;
    [ObservableProperty] public partial string CommandPreview { get; set; } = string.Empty;
    [ObservableProperty] public partial string EffectiveBackupDirectory { get; set; } = string.Empty;
    [ObservableProperty] public partial string DetectedInstallations { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowPassword { get; set; }

    public string GameDataDirectory => ValheimPaths.GameDataDirectory;

    protected override bool IsEditableProperty(string propertyName) =>
        propertyName is not (nameof(CommandPreview) or nameof(EffectiveBackupDirectory) or nameof(DetectedInstallations) or nameof(ShowPassword));

    protected override IEnumerable<string> RelevantFields =>
    [
        nameof(ServerProfile.ServerDirectory), nameof(ServerProfile.SaveDirectory), nameof(ServerProfile.BackupDirectory),
        nameof(ServerProfile.ServerName), nameof(ServerProfile.Port), nameof(ServerProfile.Password),
        nameof(ServerProfile.SaveIntervalSeconds), nameof(ServerProfile.GameBackupCount), nameof(ServerProfile.StopTimeoutSeconds),
        nameof(ServerProfile.AutoBackupRetention), nameof(ServerProfile.BackupBeforeStart), nameof(ServerProfile.ExtraArguments),
    ];

    protected override void Load(ServerProfile p)
    {
        DisplayName = p.DisplayName;
        ServerName = p.ServerName;
        Port = p.Port;
        Password = p.Password;
        IsPublic = p.Public;
        Crossplay = p.Crossplay;
        ServerDirectory = p.ServerDirectory;
        SaveDirectory = p.SaveDirectory;
        BackupDirectory = p.BackupDirectory;
        SaveIntervalMinutes = p.SaveIntervalSeconds / 60.0;
        GameBackupCount = p.GameBackupCount;
        GameBackupShortHours = p.GameBackupShortSeconds / 3600.0;
        GameBackupLongHours = p.GameBackupLongSeconds / 3600.0;
        BackupBeforeStart = p.BackupBeforeStart;
        BackupAfterStop = p.BackupAfterStop;
        FixWorldAfterStop = p.FixWorldAfterStop;
        AutoBackupRetention = p.AutoBackupRetention;
        StopTimeoutSeconds = p.StopTimeoutSeconds;
        ExtraArguments = p.ExtraArguments;
        UpdatePreview(p);
    }

    protected override void Apply(ServerProfile p)
    {
        p.DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? p.DisplayName : DisplayName.Trim();
        p.ServerName = ServerName.Trim();
        p.Port = SafeInt(Port, p.Port);
        p.Password = Password;
        p.Public = IsPublic;
        p.Crossplay = Crossplay;
        p.ServerDirectory = ServerDirectory.Trim();
        p.SaveDirectory = SaveDirectory.Trim();
        p.BackupDirectory = BackupDirectory.Trim();
        p.SaveIntervalSeconds = SafeInt(SaveIntervalMinutes * 60, p.SaveIntervalSeconds);
        p.GameBackupCount = SafeInt(GameBackupCount, p.GameBackupCount);
        p.GameBackupShortSeconds = SafeInt(GameBackupShortHours * 3600, p.GameBackupShortSeconds);
        p.GameBackupLongSeconds = SafeInt(GameBackupLongHours * 3600, p.GameBackupLongSeconds);
        p.BackupBeforeStart = BackupBeforeStart;
        p.BackupAfterStop = BackupAfterStop;
        p.FixWorldAfterStop = FixWorldAfterStop;
        p.AutoBackupRetention = SafeInt(AutoBackupRetention, p.AutoBackupRetention);
        p.StopTimeoutSeconds = SafeInt(StopTimeoutSeconds, p.StopTimeoutSeconds);
        p.ExtraArguments = ExtraArguments.Trim();
    }

    protected override void OnDraftChanged()
    {
        if (Draft() is { } draft)
        {
            UpdatePreview(draft);
        }
    }

    private void UpdatePreview(ServerProfile p)
    {
        try
        {
            CommandPreview = LaunchArguments.BuildDisplay(p);
            EffectiveBackupDirectory = string.IsNullOrWhiteSpace(p.SaveDirectory) ? "—" : p.EffectiveBackupDirectory;
        }
        catch (ArgumentException)
        {
            CommandPreview = "—";
        }
    }

    private static int SafeInt(double value, int fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) ? fallback : (int)Math.Round(value);

    [RelayCommand]
    private async Task BrowseServerDirectoryAsync()
    {
        if (await _pickers.PickFolderAsync() is { } path)
        {
            ServerDirectory = path;
        }
    }

    [RelayCommand]
    private async Task BrowseSaveDirectoryAsync()
    {
        if (await _pickers.PickFolderAsync() is { } path)
        {
            SaveDirectory = path;
        }
    }

    [RelayCommand]
    private async Task BrowseBackupDirectoryAsync()
    {
        if (await _pickers.PickFolderAsync() is { } path)
        {
            BackupDirectory = path;
        }
    }

    [RelayCommand]
    private void DetectInstallation()
    {
        var found = ServerManager.DetectServerInstallations();
        if (found.Count > 0)
        {
            ServerDirectory = found[0];
            DetectedInstallations = found.Count == 1
                ? "Instalação encontrada pela Steam."
                : $"{found.Count} instalações encontradas; usando a primeira.";
        }
        else
        {
            DetectedInstallations = "Nenhuma instalação encontrada. Na Steam, ative \"Ferramentas\" na biblioteca e instale \"Valheim Dedicated Server\".";
        }
    }

    [RelayCommand]
    private void OpenSaveDirectory()
    {
        if (!string.IsNullOrWhiteSpace(SaveDirectory))
        {
            _shell.OpenFolder(SaveDirectory);
        }
    }

    [RelayCommand]
    private void CopyCommandLine() => _shell.CopyText(CommandPreview);
}
