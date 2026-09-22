using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ValheimServerManager.App.Localization;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Platform;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.App.ViewModels;

public sealed record WorldChoice(string Name, string Detail, string Directory)
{
    public override string ToString() => Name;
}

/// <summary>Creates a server profile together with its world (new with a chosen seed, or copied).</summary>
public sealed partial class NewServerViewModel : ObservableObject
{
    private const int MaxWorldNameLength = 20;
    private readonly ServerManager _manager;
    private readonly ProfileContext _context;
    private readonly IPickerService _pickers;
    private readonly IDialogService _dialogs;
    private readonly IServerProcessLocator _locator;
    private readonly ILogger<NewServerViewModel> _logger;
    private bool _saveDirectoryTouched;
    private bool _worldNameTouched;
    private bool _updatingDefaults;

    public NewServerViewModel(
        ServerManager manager, ProfileContext context, IPickerService pickers, IDialogService dialogs, IServerProcessLocator locator,
        ILogger<NewServerViewModel> logger)
    {
        _manager = manager;
        _context = context;
        _pickers = pickers;
        _dialogs = dialogs;
        _locator = locator;
        _logger = logger;

        _updatingDefaults = true;
        Seed = WorldSeed.Random();
        Port = NextFreePort();
        ServerDirectory = manager.Profiles.Select(p => p.ServerDirectory).FirstOrDefault(d => File.Exists(Path.Combine(d, ValheimPaths.ServerExecutableName)))
                          ?? ServerManager.DetectServerInstallations().FirstOrDefault()
                          ?? string.Empty;
        _updatingDefaults = false;
        LoadGameWorlds();
        Validate();
    }

    public event EventHandler<string>? NavigateRequested;

    public IReadOnlyList<OptionItem> PresetItems { get; } =
        ModifierCatalog.Presets.Select(o => new OptionItem(o.Label, o.Description)).ToArray();

    public ObservableCollection<WorldChoice> GameWorlds { get; } = [];

    public ObservableCollection<IssueItem> Issues { get; } = [];

    [ObservableProperty] public partial string ServerName { get; set; } = string.Empty;
    [ObservableProperty] public partial string Password { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowPassword { get; set; }
    [ObservableProperty] public partial int WorldModeIndex { get; set; }
    [ObservableProperty] public partial string WorldName { get; set; } = string.Empty;
    [ObservableProperty] public partial string Seed { get; set; } = string.Empty;
    [ObservableProperty] public partial WorldChoice? SelectedWorld { get; set; }
    [ObservableProperty] public partial int PresetIndex { get; set; }
    [ObservableProperty] public partial bool CreativeMode { get; set; }
    [ObservableProperty] public partial double Port { get; set; }
    [ObservableProperty] public partial bool IsPublic { get; set; } = true;
    [ObservableProperty] public partial bool Crossplay { get; set; } = true;
    [ObservableProperty] public partial string ServerDirectory { get; set; } = string.Empty;
    [ObservableProperty] public partial string SaveDirectory { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CanCreate { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string BusyText { get; set; } = string.Empty;

    public bool IsNewWorld => WorldModeIndex == 0;

    public bool IsExistingWorld => WorldModeIndex == 1;

    public bool HasGameWorlds => GameWorlds.Count > 0;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_updatingDefaults || e.PropertyName is nameof(Issues) or nameof(CanCreate) or nameof(IsBusy) or nameof(BusyText)
                or nameof(ShowPassword) or nameof(IsNewWorld) or nameof(IsExistingWorld) or nameof(HasGameWorlds))
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ServerName):
                ApplyNameDefaults();
                break;
            case nameof(WorldName):
                _worldNameTouched = true;
                break;
            case nameof(SaveDirectory):
                _saveDirectoryTouched = true;
                break;
            case nameof(WorldModeIndex):
                OnPropertyChanged(nameof(IsNewWorld));
                OnPropertyChanged(nameof(IsExistingWorld));
                break;
        }

        Validate();
    }

    private void ApplyNameDefaults()
    {
        _updatingDefaults = true;
        try
        {
            if (!_worldNameTouched)
            {
                WorldName = SuggestWorldName(ServerName);
            }

            if (!_saveDirectoryTouched)
            {
                var folder = string.Concat(ServerName.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                SaveDirectory = string.IsNullOrWhiteSpace(folder)
                    ? string.Empty
                    : Path.Combine(ValheimPaths.DefaultServersRoot, folder, "ServerSave");
            }
        }
        finally
        {
            _updatingDefaults = false;
        }
    }

    private static string SuggestWorldName(string serverName)
    {
        var clean = new string(serverName.Where(c => char.IsAsciiLetterOrDigit(c)).ToArray());
        return clean.Length > MaxWorldNameLength ? clean[..MaxWorldNameLength] : clean;
    }

    private void LoadGameWorlds()
    {
        GameWorlds.Clear();
        try
        {
            foreach (var world in WorldCreator.ListGameWorlds())
            {
                GameWorlds.Add(new WorldChoice(
                    world.Name,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        SettingsStrings.NewServer_WorldDetail,
                        world.SeedName,
                        world.SaveNumber,
                        Helpers.Ui.Size(world.Bytes),
                        Helpers.Ui.When(new DateTimeOffset(world.LastSavedUtc))),
                    world.Directory));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list the game's worlds");
        }

        OnPropertyChanged(nameof(HasGameWorlds));
        SelectedWorld ??= GameWorlds.FirstOrDefault();
    }

    private int NextFreePort()
    {
        var used = _manager.Profiles.Select(p => p.Port).ToHashSet();
        foreach (var running in _manager.UnmanagedServers)
        {
            if (running.Port is { } p)
            {
                used.Add(p);
            }
        }

        var port = ServerProfile.DefaultPort;
        while (used.Contains(port) || used.Contains(port - 1) || used.Contains(port + 1))
        {
            port += 10;
        }

        return port;
    }

    private ServerProfile BuildProfile() => new()
    {
        DisplayName = ServerName.Trim(),
        ServerName = ServerName.Trim(),
        Password = Password,
        WorldName = IsNewWorld ? WorldName.Trim() : SelectedWorld?.Name ?? string.Empty,
        Port = double.IsNaN(Port) ? ServerProfile.DefaultPort : (int)Port,
        Public = IsPublic,
        Crossplay = Crossplay,
        ServerDirectory = ServerDirectory.Trim(),
        SaveDirectory = SaveDirectory.Trim(),
        Preset = ModifierCatalog.Presets[Math.Clamp(PresetIndex, 0, ModifierCatalog.Presets.Count - 1)].Value,
        CreativeMode = CreativeMode,
    };

    private void Validate()
    {
        Issues.Clear();
        var draft = BuildProfile();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(draft.ServerName))
        {
            errors.Add(SettingsStrings.NewServer_ErrorNoName);
        }

        foreach (var issue in ProfileValidator.Validate(draft).Where(i => i.Field != nameof(ServerProfile.ServerName) &&
                                                                         (IsNewWorld || i.Field != nameof(ServerProfile.WorldName))))
        {
            if (issue.Severity == ValidationSeverity.Error)
            {
                errors.Add(issue.Message);
            }
        }

        if (IsNewWorld)
        {
            if (string.IsNullOrWhiteSpace(WorldName) || WorldName.Length > MaxWorldNameLength ||
                !WorldName.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_'))
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, SettingsStrings.NewServer_ErrorWorldName, MaxWorldNameLength));
            }

            if (WorldSeed.Validate(Seed) is { } seedProblem)
            {
                errors.Add(seedProblem);
            }
        }
        else if (SelectedWorld is null)
        {
            errors.Add(SettingsStrings.NewServer_ErrorChooseWorld);
        }

        if (!string.IsNullOrWhiteSpace(draft.SaveDirectory) && !string.IsNullOrWhiteSpace(draft.WorldName) &&
            Path.IsPathFullyQualified(draft.SaveDirectory))
        {
            var target = WorldFolder.WorldDirectory(draft.SaveDirectory, draft.WorldName);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                errors.Add(string.Format(CultureInfo.CurrentCulture, SettingsStrings.NewServer_ErrorWorldExists, draft.WorldName));
            }

            if (_manager.Profiles.Any(p => p.WorldName.Equals(draft.WorldName, StringComparison.OrdinalIgnoreCase) &&
                                           !string.IsNullOrWhiteSpace(p.SaveDirectory) &&
                                           ValheimPaths.SameDirectory(p.SaveDirectory, draft.SaveDirectory)))
            {
                errors.Add(SettingsStrings.NewServer_ErrorWorldInUse);
            }
        }

        if (_manager.Profiles.Any(p => p.Port == draft.Port || p.Port == draft.Port + 1 || p.Port + 1 == draft.Port))
        {
            errors.Add(string.Format(CultureInfo.CurrentCulture, SettingsStrings.NewServer_ErrorPortInUse, draft.Port, NextFreePort()));
        }

        foreach (var error in errors.Distinct())
        {
            Issues.Add(new IssueItem(error, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error));
        }

        CanCreate = errors.Count == 0 && !IsBusy;
    }

    [RelayCommand]
    private void NewSeed() => Seed = WorldSeed.Random();

    [RelayCommand]
    private async Task BrowseWorldAsync()
    {
        if (await _pickers.PickFolderAsync() is not { } folder)
        {
            return;
        }

        var report = WorldInspector.InspectDirectory(folder);
        if (!report.IsHealthy || report.Metadata is null)
        {
            await _dialogs.AlertAsync(SettingsStrings.NewServer_InvalidWorldFolder_Title,
                SettingsStrings.NewServer_InvalidWorldFolder_Message);
            return;
        }

        var choice = new WorldChoice(report.Metadata.Name,
            string.Format(
                CultureInfo.CurrentCulture,
                SettingsStrings.NewServer_BrowsedWorldDetail,
                report.Metadata.SeedName,
                report.LatestSave!.Number,
                Helpers.Ui.Size(report.SaveSetBytes),
                folder),
            folder);
        GameWorlds.Insert(0, choice);
        OnPropertyChanged(nameof(HasGameWorlds));
        SelectedWorld = choice;
    }

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
    private void Cancel() => NavigateRequested?.Invoke(this, "dashboard");

    [RelayCommand]
    private Task CreateAsync() => CreateCoreAsync(start: false);

    [RelayCommand]
    private Task CreateAndStartAsync() => CreateCoreAsync(start: true);

    private async Task CreateCoreAsync(bool start)
    {
        Validate();
        if (!CanCreate)
        {
            return;
        }

        if (!IsNewWorld && _locator.IsGameRunning() && !await _dialogs.ConfirmAsync(
                SettingsStrings.NewServer_GameRunning_Title,
                SettingsStrings.NewServer_GameRunning_Message,
                SettingsStrings.NewServer_CopyAnyway, SettingsStrings.NewServer_GoBack))
        {
            return;
        }

        var profile = BuildProfile();
        IsBusy = true;
        CanCreate = false;
        try
        {
            BusyText = IsNewWorld ? SettingsStrings.NewServer_BusyNewWorld : SettingsStrings.NewServer_BusyCopying;
            Directory.CreateDirectory(profile.SaveDirectory);
            if (IsNewWorld)
            {
                var seed = Seed;
                await Task.Run(() => WorldCreator.CreateSeeded(profile.SaveDirectory, profile.WorldName, seed));
            }
            else
            {
                var source = SelectedWorld!.Directory;
                var copied = await Task.Run(() => WorldCreator.CopyExisting(source, profile.SaveDirectory));
                profile.WorldName = copied.WorldName;
            }

            _manager.AddProfile(profile);
            _context.SelectedProfileId = profile.Id;
            _logger.LogInformation("Server {Name} created (world {World})", profile.DisplayName, profile.WorldName);
            NavigateRequested?.Invoke(this, "dashboard");

            if (!IsNewWorld)
            {
                await _dialogs.AlertAsync(
                    SettingsStrings.NewServer_TwoWorlds_Title,
                    string.Format(CultureInfo.CurrentCulture, SettingsStrings.NewServer_TwoWorlds_Message1, profile.WorldName) +
                    "\n\n" + SettingsStrings.NewServer_TwoWorlds_Message2,
                    SettingsStrings.NewServer_GotIt);
            }

            if (start)
            {
                var result = await _manager.GetController(profile.Id).StartAsync(StartOptions.Default);
                if (!result.Success)
                {
                    await _dialogs.ShowChecksAsync(SettingsStrings.NewServer_CreatedNotStarted_Title, result.Message, result.Checks, false, string.Empty);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogError(ex, "Failed to create server");
            await _dialogs.AlertAsync(SettingsStrings.NewServer_CreateFailed_Title, ex.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            Validate();
        }
    }
}
