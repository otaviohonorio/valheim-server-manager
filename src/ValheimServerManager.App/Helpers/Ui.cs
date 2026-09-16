using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Servers;

namespace ValheimServerManager.App.Helpers;

/// <summary>Small pure functions used from x:Bind.</summary>
public static class Ui
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfNone(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    public static Brush StateBrush(ServerRunState state) => Resource(state switch
    {
        ServerRunState.Running => "VsmRunningBrush",
        ServerRunState.Starting or ServerRunState.Stopping => "VsmBusyBrush",
        ServerRunState.Crashed => "VsmCrashedBrush",
        _ => "VsmStoppedBrush",
    });

    public static string StateText(ServerRunState state) => state switch
    {
        ServerRunState.Running => "Online",
        ServerRunState.Starting => "Iniciando",
        ServerRunState.Stopping => "Desligando",
        ServerRunState.Crashed => "Parou inesperadamente",
        _ => "Parado",
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
        BackupKind.Manual => "Manual",
        BackupKind.PreStart => "Antes de iniciar",
        BackupKind.PostStop => "Depois de parar",
        BackupKind.PreRestore => "Antes de restaurar",
        BackupKind.Quarantine => "Quarentena (mundo com defeito)",
        BackupKind.GameAuto => "Automático do Valheim",
        _ => "Importado",
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
        < 1024 * 1024 => string.Format(PtBr, "{0:N0} KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(PtBr, "{0:N1} MB", bytes / (1024.0 * 1024)),
        _ => string.Format(PtBr, "{0:N2} GB", bytes / (1024.0 * 1024 * 1024)),
    };

    public static string Number(long value) => value.ToString("N0", PtBr);

    public static string When(DateTimeOffset? at)
    {
        if (at is not { } value)
        {
            return "—";
        }

        var local = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        return local.Date == today
            ? $"hoje, {local:HH:mm:ss}"
            : local.Date == today.AddDays(-1)
                ? $"ontem, {local:HH:mm}"
                : local.ToString("dd/MM/yyyy HH:mm", PtBr);
    }

    public static string Ago(DateTimeOffset? at)
    {
        if (at is not { } value)
        {
            return "nunca";
        }

        var span = DateTimeOffset.Now - value;
        return span.TotalSeconds switch
        {
            < 60 => "agora há pouco",
            < 3600 => $"há {(int)span.TotalMinutes} min",
            < 86400 => $"há {(int)span.TotalHours} h {span.Minutes} min",
            _ => $"há {(int)span.TotalDays} dia(s)",
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
