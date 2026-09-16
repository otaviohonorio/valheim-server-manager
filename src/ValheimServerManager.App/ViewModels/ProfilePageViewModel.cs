using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Servers;

namespace ValheimServerManager.App.ViewModels;

/// <summary>
/// Base for pages that show the selected profile. Follows profile switches and the selected
/// controller's events, always delivering them on the UI thread.
/// </summary>
public abstract partial class ProfilePageViewModel : ObservableObject, IDisposable
{
    private ServerController? _controller;
    private bool _disposed;

    protected ProfilePageViewModel(ProfileContext context, ServerManager manager, UiDispatcher ui)
    {
        Context = context;
        Manager = manager;
        Ui = ui;
        Context.PropertyChanged += OnContextChanged;
        Manager.ProfilesChanged += OnProfilesChanged;
    }

    protected ProfileContext Context { get; }

    protected ServerManager Manager { get; }

    protected UiDispatcher Ui { get; }

    protected ServerController? Controller => _controller;

    public ServerProfile? Profile => _controller?.Profile;

    public bool HasProfile => _controller is not null;

    [ObservableProperty]
    public partial ServerStatus Status { get; set; } = new();

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    /// <summary>Call once the page is loaded.</summary>
    public void Activate()
    {
        Attach(Context.Controller);
    }

    protected virtual void OnControllerChanged()
    {
    }

    protected virtual void OnServerStatusChanged(ServerStatus status)
    {
    }

    protected virtual void OnActivity(ServerActivity activity)
    {
    }

    protected virtual void OnBusyChanged(bool value)
    {
    }

    partial void OnIsBusyChanged(bool value) => OnBusyChanged(value);

    protected virtual void OnLog(IReadOnlyList<ServerLogLine> lines)
    {
    }

    protected virtual void OnProfileSaved()
    {
    }

    protected async Task RunBusyAsync(string text, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        IsBusy = true;
        BusyText = text;
        try
        {
            await work();
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private void Attach(ServerController? controller)
    {
        if (_controller is not null)
        {
            _controller.StatusChanged -= OnControllerStatus;
            _controller.ActivityAdded -= OnControllerActivity;
            _controller.LogReceived -= OnControllerLog;
        }

        _controller = controller;
        if (_controller is not null)
        {
            _controller.StatusChanged += OnControllerStatus;
            _controller.ActivityAdded += OnControllerActivity;
            _controller.LogReceived += OnControllerLog;
            Status = _controller.Status;
        }
        else
        {
            Status = new ServerStatus();
        }

        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(HasProfile));
        OnControllerChanged();
        OnServerStatusChanged(Status);
    }

    private void OnContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileContext.SelectedProfileId))
        {
            Ui.Run(() => Attach(Context.Controller));
        }
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => Ui.Run(() =>
    {
        OnPropertyChanged(nameof(Profile));
        OnProfileSaved();
    });

    private void OnControllerStatus(object? sender, ServerStatus status) => Ui.Run(() =>
    {
        if (!ReferenceEquals(sender, _controller))
        {
            return;
        }

        Status = status;
        OnServerStatusChanged(status);
    });

    private void OnControllerActivity(object? sender, ServerActivity activity) => Ui.Run(() =>
    {
        if (ReferenceEquals(sender, _controller))
        {
            OnActivity(activity);
        }
    });

    private void OnControllerLog(object? sender, IReadOnlyList<ServerLogLine> lines) => Ui.Run(() =>
    {
        if (ReferenceEquals(sender, _controller))
        {
            OnLog(lines);
        }
    });

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _disposed = true;
        Context.PropertyChanged -= OnContextChanged;
        Manager.ProfilesChanged -= OnProfilesChanged;
        Attach(null);
    }
}
