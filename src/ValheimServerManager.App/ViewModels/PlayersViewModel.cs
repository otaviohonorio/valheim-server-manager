using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerManager.App.Localization;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Players;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.App.ViewModels;

public sealed partial class PlayerRowViewModel : ObservableObject
{
    private readonly Action<PlayerRowViewModel, PlayerListKind, bool> _changed;
    private bool _loading = true;

    public PlayerRowViewModel(string id, string name, string detail, bool admin, bool banned, bool permitted,
        Action<PlayerRowViewModel, PlayerListKind, bool> changed)
    {
        Id = id;
        Name = name;
        Detail = detail;
        _changed = changed;
        IsAdmin = admin;
        IsBanned = banned;
        IsPermitted = permitted;
        _loading = false;
    }

    public string Id { get; }

    [ObservableProperty]
    public partial bool IsOnline { get; set; }

    public string Name { get; }

    public string Detail { get; }

    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    [ObservableProperty]
    public partial bool IsAdmin { get; set; }

    [ObservableProperty]
    public partial bool IsBanned { get; set; }

    [ObservableProperty]
    public partial bool IsPermitted { get; set; }

    partial void OnIsAdminChanged(bool value)
    {
        if (!_loading)
        {
            _changed(this, PlayerListKind.Admins, value);
        }
    }

    partial void OnIsBannedChanged(bool value)
    {
        if (!_loading)
        {
            _changed(this, PlayerListKind.Banned, value);
        }
    }

    partial void OnIsPermittedChanged(bool value)
    {
        if (!_loading)
        {
            _changed(this, PlayerListKind.Permitted, value);
        }
    }
}

/// <summary>A character in the world right now.</summary>
public sealed record OnlinePlayerRow(string Name, string Detail)
{
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
}

public sealed partial class PlayersViewModel : ProfilePageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;

    public PlayersViewModel(ProfileContext context, ServerManager manager, UiDispatcher ui, IDialogService dialogs, IShellService shell)
        : base(context, manager, ui)
    {
        _dialogs = dialogs;
        _shell = shell;
    }

    public ObservableCollection<PlayerRowViewModel> Players { get; } = [];

    public ObservableCollection<OnlinePlayerRow> Online { get; } = [];

    [ObservableProperty]
    public partial bool HasOnline { get; set; }

    [ObservableProperty]
    public partial string NewId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AllowListActive { get; set; }

    [ObservableProperty]
    public partial bool NeedsRestart { get; set; }

    [ObservableProperty]
    public partial string OnlineText { get; set; } = string.Empty;

    protected override void OnControllerChanged() => Refresh();

    protected override void OnServerStatusChanged(ServerStatus status)
    {
        OnlineText = status.IsActive
            ? string.Format(CultureInfo.CurrentCulture, WorldStrings.Players_OnlineCount, status.PlayerCount)
            : WorldStrings.Players_ServerStopped;
        UpdateOnline(status);
    }

    private void UpdateOnline(ServerStatus status)
    {
        var admins = Profile is { } profile && !string.IsNullOrWhiteSpace(profile.SaveDirectory)
            ? PlayerListFile.Read(profile.SaveDirectory, PlayerListKind.Admins).ToHashSet(StringComparer.Ordinal)
            : [];
        var players = status.IsActive ? status.OnlinePlayers : [];

        Online.Clear();
        foreach (var player in players)
        {
            var parts = new List<string>();
            if (player.SteamId is { } steam)
            {
                parts.Add($"Steam · {steam}");
            }
            else if (player.PlatformId is { } platform)
            {
                parts.Add(platform);
            }

            parts.Add(string.Format(CultureInfo.CurrentCulture, WorldStrings.Players_InWorldSince, player.Since.ToLocalTime()));
            if (player.SteamId is { } id && admins.Contains(id))
            {
                parts.Add(WorldStrings.Players_AdminTag);
            }

            Online.Add(new OnlinePlayerRow(player.Name, string.Join(" · ", parts)));
        }

        HasOnline = Online.Count > 0;
        var onlineIds = players.Select(p => p.SteamId ?? p.PlatformId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var onlineNames = players.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Players)
        {
            row.IsOnline = onlineIds.Contains(row.Id) || (onlineIds.Count == 0 && onlineNames.Contains(row.Name));
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        Players.Clear();
        NeedsRestart = false;
        if (Profile is not { } profile || string.IsNullOrWhiteSpace(profile.SaveDirectory))
        {
            return;
        }

        var admins = PlayerListFile.Read(profile.SaveDirectory, PlayerListKind.Admins).ToHashSet(StringComparer.Ordinal);
        var banned = PlayerListFile.Read(profile.SaveDirectory, PlayerListKind.Banned).ToHashSet(StringComparer.Ordinal);
        var permitted = PlayerListFile.Read(profile.SaveDirectory, PlayerListKind.Permitted).ToHashSet(StringComparer.Ordinal);
        AllowListActive = permitted.Count > 0;

        var known = new Dictionary<string, (string Name, string Detail)>(StringComparer.Ordinal);
        try
        {
            var meta = WorldInspector.Inspect(profile.SaveDirectory, profile.WorldName).Metadata;
            foreach (var player in meta?.Players ?? [])
            {
                var id = player.SteamId ?? player.PlatformId;
                known[id] = (player.Name, player.SteamId is null ? player.PlatformId : $"Steam · {id}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Lists still work without the world file.
        }

        foreach (var id in admins.Concat(banned).Concat(permitted))
        {
            known.TryAdd(id, (WorldStrings.Players_UnnamedId, id));
        }

        foreach (var (id, info) in known.OrderBy(k => k.Value.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Players.Add(new PlayerRowViewModel(id, info.Name, info.Detail,
                admins.Contains(id), banned.Contains(id), permitted.Contains(id), OnPlayerChanged));
        }

        UpdateOnline(Status);
    }

    private void OnPlayerChanged(PlayerRowViewModel row, PlayerListKind kind, bool value)
    {
        if (Profile is not { } profile)
        {
            return;
        }

        _ = ApplyAsync(profile.SaveDirectory, row, kind, value);
    }

    private async Task ApplyAsync(string saveDirectory, PlayerRowViewModel row, PlayerListKind kind, bool value)
    {
        if (kind == PlayerListKind.Permitted && value && !AllowListActive &&
            !await _dialogs.ConfirmAsync(
                WorldStrings.Players_EnableAllowListTitle,
                WorldStrings.Players_EnableAllowListMessage,
                WorldStrings.Players_EnableAllowListConfirm))
        {
            row.IsPermitted = false;
            return;
        }

        try
        {
            PlayerListFile.Set(saveDirectory, kind, row.Id, value);
            AllowListActive = PlayerListFile.Read(saveDirectory, PlayerListKind.Permitted).Count > 0;
            NeedsRestart = Status.IsActive;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.AlertAsync(WorldStrings.Players_SaveFailedTitle, ex.Message);
        }
    }

    [RelayCommand]
    private void AddAdmin() => AddManual(PlayerListKind.Admins);

    [RelayCommand]
    private void AddBanned() => AddManual(PlayerListKind.Banned);

    private void AddManual(PlayerListKind kind)
    {
        if (Profile is not { } profile || string.IsNullOrWhiteSpace(NewId))
        {
            return;
        }

        PlayerListFile.Set(profile.SaveDirectory, kind, PlayerListFile.NormalizeId(NewId), true);
        NewId = string.Empty;
        Refresh();
        NeedsRestart = Status.IsActive;
    }

    [RelayCommand]
    private void OpenListsFolder()
    {
        if (Profile is { } profile)
        {
            _shell.OpenFolder(profile.SaveDirectory);
        }
    }
}
