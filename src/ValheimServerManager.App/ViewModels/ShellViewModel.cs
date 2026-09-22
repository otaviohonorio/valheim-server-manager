using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ValheimServerManager.App.Localization;
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
        Detail = string.Format(CultureInfo.CurrentCulture, ShellStrings.Shell_ProfileDetail, profile.WorldName, profile.Port);
    }
}

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ServerManager _manager;
    private readonly ProfileContext _context;
    private readonly UiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ShellViewModel> _logger;
    private DispatcherQueueTimer? _scanTimer;
    private bool _revertingSelection;

    public ShellViewModel(
        ServerManager manager,
        ProfileContext context,
        UiDispatcher ui,
        IDialogService dialogs,
        AlertCenter alerts,
        ILogger<ShellViewModel> logger)
    {
        _manager = manager;
        _context = context;
        _ui = ui;
        _dialogs = dialogs;
        _logger = logger;
        Alerts = alerts;
        _manager.ProfilesChanged += (_, _) => _ui.Run(RebuildProfiles);
        _manager.UnmanagedServersChanged += (_, list) => _ui.Run(() => UpdateUnmanaged(list));

        // Profiles created, duplicated or adopted elsewhere are selected through the context; keep the selector in sync.
        _context.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileContext.SelectedProfileId))
            {
                _ui.Run(() => SelectedProfile = Profiles.FirstOrDefault(p => p.Id == _context.SelectedProfileId));
            }
        };
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

    /// <summary>Set by the window: returns false when the user cancels leaving unsaved edits.</summary>
    public Func<Task<bool>>? ConfirmLeaveAsync { get; set; }

    public bool AnyActive => _manager.AnyActive;

    public int ActiveCount => _manager.Controllers.Count(c => c.Status.IsActive);

    public void Start()
    {
        _scanTimer = _ui.CreateTimer(TimeSpan.FromSeconds(10), () => _ = ScanAsync());
        _scanTimer.Start();
        _ = ScanAsync();
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? oldValue, ProfileItemViewModel? newValue) =>
        _ = SwitchProfileAsync(oldValue, newValue);

    private async Task SwitchProfileAsync(ProfileItemViewModel? oldValue, ProfileItemViewModel? newValue)
    {
        if (_revertingSelection || newValue is null || _context.SelectedProfileId == newValue.Id)
        {
            return;
        }

        if (oldValue is not null && ConfirmLeaveAsync is { } confirm && !await confirm())
        {
            _revertingSelection = true;
            SelectedProfile = oldValue;
            _revertingSelection = false;
            return;
        }

        _context.SelectedProfileId = newValue.Id;
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
            _logger.LogWarning(ex, "Failed to scan for running servers");
        }
    }

    private void UpdateUnmanaged(IReadOnlyList<RunningServer> servers)
    {
        HasUnmanaged = servers.Count > 0;
        UnmanagedMessage = servers.Count switch
        {
            0 => string.Empty,
            1 => DescribeUnmanaged(servers[0]),
            _ => string.Format(CultureInfo.CurrentCulture, ShellStrings.Unmanaged_Many, servers.Count),
        };
    }

    private static string DescribeUnmanaged(RunningServer s)
    {
        var name = s.ServerName ?? ShellStrings.Unmanaged_NoName;
        var text = string.Format(CultureInfo.CurrentCulture, ShellStrings.Unmanaged_One, name, s.WorldName, s.ProcessId);
        return s.SaveDirectoryIsDefault ? text + " " + ShellStrings.Unmanaged_SharedFolder : text;
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
            ShellStrings.Unmanaged_Adopt,
            string.Format(CultureInfo.CurrentCulture, ShellStrings.Unmanaged_AdoptMessage, server.ServerName),
            ShellStrings.Unmanaged_AdoptConfirm);
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
                ShellStrings.Unmanaged_SaveFolderTitle,
                ShellStrings.Unmanaged_SaveFolderMessage);
        }
    }

    /// <summary>The window shows the "new server" page.</summary>
    public event EventHandler? NewServerRequested;

    [ObservableProperty]
    public partial bool IsCreatingServer { get; set; }

    public bool ShowContent => HasProfiles || IsCreatingServer;

    partial void OnIsCreatingServerChanged(bool value) => OnPropertyChanged(nameof(ShowContent));

    partial void OnHasProfilesChanged(bool value) => OnPropertyChanged(nameof(ShowContent));

    [RelayCommand]
    private void NewServer() => NewServerRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task DuplicateProfileAsync()
    {
        var current = _context.Profile;
        if (current is null)
        {
            return;
        }

        var name = await _dialogs.PromptAsync(
            ShellStrings.Profile_DuplicateTitle,
            ShellStrings.Profile_DuplicatePrompt,
            ShellStrings.Profile_DuplicatePlaceholder,
            string.Format(CultureInfo.CurrentCulture, ShellStrings.Profile_CopyName, current.DisplayName));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var copy = current.Duplicate(name);
        copy.Port = NextFreePort(current.Port);
        _manager.AddProfile(copy);
        _context.SelectedProfileId = copy.Id;
        await _dialogs.AlertAsync(ShellStrings.Profile_DuplicatedTitle,
            string.Format(CultureInfo.CurrentCulture, ShellStrings.Profile_DuplicatedMessage, copy.Port));
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
            await _dialogs.AlertAsync(ShellStrings.Profile_DeleteRunningTitle, ShellStrings.Profile_DeleteRunningMessage);
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            ShellStrings.Profile_DeleteTitle,
            string.Format(CultureInfo.CurrentCulture, ShellStrings.Profile_DeleteMessage, controller.Profile.DisplayName),
            ShellStrings.Profile_DeleteConfirm, destructive: true);
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

    public void Dispose()
    {
        _scanTimer?.Stop();
        foreach (var item in Profiles)
        {
            item.Controller.StatusChanged -= OnAnyStatus;
        }
    }
}
