using System.Globalization;

namespace ValheimServerManager.Core.Localization;

/// <summary>A language the app ships, with its name written in that language.</summary>
public sealed record SupportedLanguage(string Code, string NativeName);

/// <summary>
/// Picks the UI language: the user's choice when there is one, otherwise the Windows display
/// language when it is one we ship, otherwise English. English is the key language: every resource
/// exists in it, and pt-BR / es sit next to it (Strings.pt-BR.resx, Strings.es.resx).
/// </summary>
public static class AppLanguage
{
    public const string English = "en";
    public const string PortugueseBrazil = "pt-BR";
    public const string Spanish = "es";

    public static IReadOnlyList<SupportedLanguage> Supported { get; } =
    [
        new(English, "English"),
        new(PortugueseBrazil, "Português (Brasil)"),
        new(Spanish, "Español"),
    ];

    /// <summary>
    /// The language code to use. <paramref name="preference"/> is the saved choice (null or empty
    /// follows Windows); <paramref name="system"/> defaults to the Windows display language.
    /// </summary>
    public static string Resolve(string? preference, CultureInfo? system = null)
    {
        if (!string.IsNullOrWhiteSpace(preference) && Match(preference) is { } chosen)
        {
            return chosen;
        }

        system ??= CultureInfo.InstalledUICulture;
        return Match(system.Name) ?? English;
    }

    /// <summary>
    /// Applies the language to this process: current and future threads. Number and date formats stay
    /// in the user's regional settings; only the text changes.
    /// </summary>
    public static CultureInfo Apply(string? preference)
    {
        var culture = CultureInfo.GetCultureInfo(Resolve(preference));
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return culture;
    }

    private static string? Match(string name)
    {
        // Any Portuguese goes to pt-BR and any Spanish to es: close enough, and far better than English.
        if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            return PortugueseBrazil;
        }

        if (name.StartsWith("es", StringComparison.OrdinalIgnoreCase))
        {
            return Spanish;
        }

        return name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : null;
    }
}
