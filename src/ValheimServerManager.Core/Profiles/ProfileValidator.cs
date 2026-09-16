using System.Runtime.Versioning;
using ValheimServerManager.Core.Platform;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Profiles;

public enum ValidationSeverity
{
    Warning,
    Error,
}

public sealed record ValidationIssue(string Field, ValidationSeverity Severity, string Message);

/// <summary>Rules a profile must satisfy before the server may start.</summary>
[SupportedOSPlatform("windows")]
public static class ProfileValidator
{
    public const int MinPasswordLength = 5;

    public static IReadOnlyList<ValidationIssue> Validate(ServerProfile profile, string? gameDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        gameDataDirectory ??= ValheimPaths.GameDataDirectory;
        var issues = new List<ValidationIssue>();

        void Error(string field, string message) => issues.Add(new(field, ValidationSeverity.Error, message));
        void Warn(string field, string message) => issues.Add(new(field, ValidationSeverity.Warning, message));

        // Server install
        if (string.IsNullOrWhiteSpace(profile.ServerDirectory))
        {
            Error(nameof(profile.ServerDirectory), "Informe a pasta do Valheim Dedicated Server.");
        }
        else if (!File.Exists(profile.ServerExecutable))
        {
            Error(nameof(profile.ServerDirectory), $"Não encontrei {ValheimPaths.ServerExecutableName} em \"{profile.ServerDirectory}\".");
        }

        // Save directory — the rule that would have prevented the incident
        if (string.IsNullOrWhiteSpace(profile.SaveDirectory))
        {
            Error(nameof(profile.SaveDirectory), "Informe a pasta de saves do servidor.");
        }
        else if (!Path.IsPathFullyQualified(profile.SaveDirectory))
        {
            Error(nameof(profile.SaveDirectory), "A pasta de saves precisa ser um caminho completo (ex.: D:\\Valheim\\ServerSave).");
        }
        else if (ValheimPaths.SameDirectory(profile.SaveDirectory, gameDataDirectory))
        {
            Error(nameof(profile.SaveDirectory),
                "Esta é a pasta de saves do próprio jogo. Servidor e jogo abrindo os mesmos arquivos foi o que " +
                "apagou o mundo em 16/09/2026. Use uma pasta só do servidor.");
        }
        else if (ValheimPaths.IsInside(profile.SaveDirectory, gameDataDirectory))
        {
            Warn(nameof(profile.SaveDirectory),
                "A pasta de saves fica dentro da pasta do jogo. Funciona, mas é fácil confundir os dois. Prefira outro lugar.");
        }

        // Backups
        if (!string.IsNullOrWhiteSpace(profile.SaveDirectory) && Path.IsPathFullyQualified(profile.SaveDirectory))
        {
            var backups = profile.EffectiveBackupDirectory;
            var worlds = WorldFolder.WorldsDirectory(profile.SaveDirectory);
            if (ValheimPaths.SameDirectory(backups, worlds) || ValheimPaths.IsInside(backups, worlds))
            {
                Error(nameof(profile.BackupDirectory),
                    "Os backups não podem ficar dentro de worlds_local: o Valheim trataria cada backup como um mundo.");
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.BackupDirectory) && !Path.IsPathFullyQualified(profile.BackupDirectory))
        {
            Error(nameof(profile.BackupDirectory), "A pasta de backups precisa ser um caminho completo.");
        }

        // Identity
        if (string.IsNullOrWhiteSpace(profile.ServerName))
        {
            Error(nameof(profile.ServerName), "Dê um nome ao servidor.");
        }
        else if (profile.ServerName.Contains('"'))
        {
            Error(nameof(profile.ServerName), "O nome do servidor não pode ter aspas.");
        }

        if (string.IsNullOrWhiteSpace(profile.WorldName))
        {
            Error(nameof(profile.WorldName), "Informe o nome do mundo.");
        }
        else if (profile.WorldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.WorldName.Contains('"'))
        {
            Error(nameof(profile.WorldName), "O nome do mundo tem caracteres que não podem ser usados em nome de pasta.");
        }
        else if (profile.WorldName.Contains(WorldFolder.GameAutoBackupMarker, StringComparison.OrdinalIgnoreCase))
        {
            Error(nameof(profile.WorldName), "Esse nome é de um backup automático do Valheim, não de um mundo.");
        }

        if (profile.Port is < 1024 or > 65534)
        {
            Error(nameof(profile.Port), "A porta deve estar entre 1024 e 65534 (o servidor usa ela e a seguinte).");
        }

        // Password rules enforced by valheim_server itself
        if (string.IsNullOrEmpty(profile.Password))
        {
            Error(nameof(profile.Password), "O servidor dedicado exige senha.");
        }
        else
        {
            if (profile.Password.Length < MinPasswordLength)
            {
                Error(nameof(profile.Password), $"A senha precisa ter pelo menos {MinPasswordLength} caracteres.");
            }

            if (profile.Password.Contains('"'))
            {
                Error(nameof(profile.Password), "A senha não pode ter aspas.");
            }

            if (!string.IsNullOrEmpty(profile.ServerName) &&
                profile.ServerName.Contains(profile.Password, StringComparison.OrdinalIgnoreCase))
            {
                Error(nameof(profile.Password), "A senha não pode aparecer dentro do nome do servidor.");
            }
        }

        // Save cadence
        if (profile.SaveIntervalSeconds < 60)
        {
            Error(nameof(profile.SaveIntervalSeconds), "O intervalo de save precisa ser de pelo menos 60 segundos.");
        }
        else if (profile.SaveIntervalSeconds > 3600)
        {
            Warn(nameof(profile.SaveIntervalSeconds), "Mais de 1 hora entre saves: uma queda de energia perde muito progresso.");
        }

        if (profile.GameBackupCount is < 1 or > 100)
        {
            Error(nameof(profile.GameBackupCount), "Quantidade de backups do jogo deve ficar entre 1 e 100.");
        }

        if (profile.StopTimeoutSeconds is < 15 or > 900)
        {
            Error(nameof(profile.StopTimeoutSeconds), "O tempo de espera para desligar deve ficar entre 15 e 900 segundos.");
        }

        if (profile.AutoBackupRetention is < 1 or > 500)
        {
            Error(nameof(profile.AutoBackupRetention), "A retenção de backups automáticos deve ficar entre 1 e 500.");
        }

        if (!profile.BackupBeforeStart)
        {
            Warn(nameof(profile.BackupBeforeStart), "Sem backup antes de iniciar, um mundo corrompido não tem volta.");
        }

        // Extra arguments cannot override managed ones
        foreach (var arg in CommandLine.Split(profile.ExtraArguments))
        {
            if (LaunchArguments.ManagedArguments.Contains(arg))
            {
                Error(nameof(profile.ExtraArguments),
                    $"\"{arg}\" é controlado pelo gerenciador; configure pela tela em vez de argumentos extras.");
            }
        }

        if (profile.Preset == WorldPreset.Hammer && !profile.CreativeMode)
        {
            Warn(nameof(profile.Preset), "O preset Martelo já liga construção sem custo: o servidor fica sempre em modo criativo.");
        }

        return issues;
    }

    public static bool HasErrors(this IEnumerable<ValidationIssue> issues) =>
        issues.Any(i => i.Severity == ValidationSeverity.Error);
}
