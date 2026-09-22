using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ValheimServerManager.App.Localization;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Servers;

namespace ValheimServerManager.App.Helpers;

/// <summary>Small pure functions used from x:Bind.</summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNone(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    public static Windows.UI.Text.FontWeight Weight(bool strong) =>
        strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public static Microsoft.UI.Xaml.Controls.PasswordRevealMode RevealMode(bool show) =>
        show ? Microsoft.UI.Xaml.Controls.PasswordRevealMode.Visible : Microsoft.UI.Xaml.Controls.PasswordRevealMode.Hidden;

    /// <summary>Open eye when hidden (click to show), crossed eye when visible (click to hide).</summary>
    public static string EyeGlyph(bool show) => show ? "\uED1A" : "\uE7B3";

    public static Brush StateBrush(ServerRunState state) => Resource(state switch
    {
        ServerRunState.Running => "VsmRunningBrush",
        ServerRunState.Starting or ServerRunState.Stopping => "VsmBusyBrush",
        ServerRunState.Crashed => "VsmCrashedBrush",
        _ => "VsmStoppedBrush",
    });

    public static string StateText(ServerRunState state) => state switch
    {
        ServerRunState.Running => ShellStrings.State_Online,
        ServerRunState.Starting => ShellStrings.State_Starting,
        ServerRunState.Stopping => ShellStrings.State_Stopping,
        ServerRunState.Crashed => ShellStrings.State_Crashed,
        _ => ShellStrings.State_Stopped,
    };

    public static string StateGlyph(ServerRunState state) => state switch
    {
        ServerRunState.Running => "",
        ServerRunState.Starting or ServerRunState.Stopping => "",
        ServerRunState.Crashed => "",
        _ => "",
    };

    public static string KindText(BackupKind kind) => kind switch
    {
        BackupKind.Manual => ShellStrings.BackupKind_Manual,
        BackupKind.PreStart => ShellStrings.BackupKind_PreStart,
        BackupKind.PostStop => ShellStrings.BackupKind_PostStop,
        BackupKind.PreRestore => ShellStrings.BackupKind_PreRestore,
        BackupKind.Quarantine => ShellStrings.BackupKind_Quarantine,
        BackupKind.GameAuto => ShellStrings.BackupKind_GameAuto,
        _ => ShellStrings.BackupKind_Imported,
    };

    public static string KindGlyph(BackupKind kind) => kind switch
    {
        BackupKind.Manual => "",
        BackupKind.PreStart => "",
        BackupKind.PostStop => "",
        BackupKind.PreRestore => "",
        BackupKind.Quarantine => "",
        BackupKind.GameAuto => "",
        _ => "",
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:N0} KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", bytes / (1024.0 * 1024)),
        _ => string.Format(CultureInfo.CurrentCulture, "{0:N2} GB", bytes / (1024.0 * 1024 * 1024)),
    };

    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Text comes in the UI language; numbers and dates follow the user's regional settings.</summary>
    public static string When(DateTimeOffset? at)
    {
        if (at is not { } value)
        {
            return "—";
        }

        var local = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        return local.Date == today
            ? string.Format(CultureInfo.CurrentCulture, ShellStrings.Time_Today, local)
            : local.Date == today.AddDays(-1)
                ? string.Format(CultureInfo.CurrentCulture, ShellStrings.Time_Yesterday, local)
                : local.ToString("g", CultureInfo.CurrentCulture);
    }

    public static string Ago(DateTimeOffset? at)
    {
        if (at is not { } value)
        {
            return ShellStrings.Time_Never;
        }

        var span = DateTimeOffset.Now - value;
        return span.TotalSeconds switch
        {
            < 60 => ShellStrings.Time_JustNow,
            < 3600 => string.Format(CultureInfo.CurrentCulture, ShellStrings.Time_MinutesAgo, (int)span.TotalMinutes),
            < 86400 => string.Format(CultureInfo.CurrentCulture, ShellStrings.Time_HoursAgo, (int)span.TotalHours, span.Minutes),
            _ => string.Format(CultureInfo.CurrentCulture,
                (int)span.TotalDays == 1 ? ShellStrings.Time_DayAgo : ShellStrings.Time_DaysAgo, (int)span.TotalDays),
        };
    }

    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours} h {span.Minutes:00} min"
            : span.TotalMinutes >= 1
                ? $"{span.Minutes} min {span.Seconds:00} s"
                : $"{span.Seconds} s";

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
