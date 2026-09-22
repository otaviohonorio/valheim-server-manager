using System.Runtime.InteropServices;
using ValheimServerManager.App.Localization;

namespace ValheimServerManager.App.Services;

public enum BalloonKind
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Minimal notification-area icon on top of Shell_NotifyIcon. It needs nothing from the Windows App
/// SDK runtime, so it also carries the notifications (toast APIs require the installed runtime,
/// which self-contained apps do not have).
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint CallbackMessage = WmApp + 17;
    private const uint WmCommand = 0x0111;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint NinBalloonUserClick = 0x0405;
    private const uint WmLButtonDblClk = 0x0203;

    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifInfo = 0x10;
    private const uint NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const uint NiifLargeIcon = 0x20;
    private const uint NiifRespectQuietTime = 0x80;

    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;

    private const int MenuOpen = 1;
    private const int MenuExit = 2;

    private readonly SubclassProc _proc;
    private readonly uint _taskbarCreated;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private string _tooltip = string.Empty;
    private bool _added;

    public TrayIcon()
    {
        _proc = WindowProc;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ExitRequested;

    /// <summary>
    /// Windows is logging off or shutting down. Handlers run synchronously on the UI thread and may
    /// block (the shell shows <see cref="SetShutdownBlockReason"/> while they do).
    /// </summary>
    public event EventHandler? SessionEnding;

    public bool IsVisible => _added;

    public void Initialize(IntPtr hwnd, string iconPath, string tooltip)
    {
        _hwnd = hwnd;
        _tooltip = tooltip;
        _icon = LoadImageW(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, 32, 32, 0x10 /* LR_LOADFROMFILE */);
        SetWindowSubclass(hwnd, _proc, 1, IntPtr.Zero);
        Add();
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        if (!_added)
        {
            return;
        }

        var data = NewData(NifTip | NifShowTip);
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void ShowBalloon(string title, string message, BalloonKind kind = BalloonKind.Info)
    {
        if (!_added)
        {
            return;
        }

        var data = NewData(NifInfo);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = (uint)kind | NiifLargeIcon | NiifRespectQuietTime;
        Shell_NotifyIconW(NimModify, ref data);
    }

    private void Add()
    {
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        _added = Shell_NotifyIconW(NimAdd, ref data);
        if (_added)
        {
            data.uTimeoutOrVersion = NotifyIconVersion4;
            Shell_NotifyIconW(NimSetVersion, ref data);
        }
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = Truncate(_tooltip, 127),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        if (msg == CallbackMessage)
        {
            var evt = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (evt)
            {
                case NinSelect:
                case NinKeySelect:
                case NinBalloonUserClick:
                case WmLButtonDblClk:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WmContextMenu:
                    ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == WmQueryEndSession)
        {
            // Allow the shutdown, but ask Windows to wait while servers save (see WM_ENDSESSION).
            return (IntPtr)1;
        }

        if (msg == WmEndSession && wParam != IntPtr.Zero)
        {
            SessionEnding?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted: the icon is gone and must be added again.
            _added = false;
            Add();
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, 0, (UIntPtr)MenuOpen, ShellStrings.Tray_Open);
            AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
            AppendMenuW(menu, 0, (UIntPtr)MenuExit, ShellStrings.Tray_Exit);
            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0080 /* TPM_NONOTIFY */ | 0x0002 /* TPM_RIGHTBUTTON */, x, y, _hwnd, IntPtr.Zero);
            if (chosen == MenuOpen)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (chosen == MenuExit)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>Shows a reason on the Windows shutdown screen, or clears it with <c>null</c>.</summary>
    public void SetShutdownBlockReason(string? reason)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (reason is null)
        {
            ShutdownBlockReasonDestroy(_hwnd);
        }
        else
        {
            ShutdownBlockReasonCreate(_hwnd, reason);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIconW(NimDelete, ref data);
            _added = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            RemoveWindowSubclass(_hwnd, _proc, 1);
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr refData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInstance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);
}
