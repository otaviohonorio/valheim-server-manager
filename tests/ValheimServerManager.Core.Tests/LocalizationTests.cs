using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ValheimServerManager.Core.Localization;

namespace ValheimServerManager.Core.Tests;

internal static class TestCulture
{
    /// <summary>Tests assert on English, the key language, whatever the machine's language is.</summary>
    [ModuleInitializer]
    internal static void UseEnglish()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}

public partial class LocalizationTests
{
    [Theory]
    [InlineData(null, "pt-BR", "pt-BR")]
    [InlineData(null, "pt-PT", "pt-BR")]
    [InlineData(null, "es-MX", "es")]
    [InlineData(null, "en-GB", "en")]
    [InlineData(null, "fr-FR", "en")]
    [InlineData("", "es-ES", "es")]
    [InlineData("en", "pt-BR", "en")]
    [InlineData("es", "en-US", "es")]
    [InlineData("klingon", "pt-BR", "pt-BR")]
    public void Resolves_the_language(string? preference, string system, string expected) =>
        Assert.Equal(expected, AppLanguage.Resolve(preference, CultureInfo.GetCultureInfo(system)));

    public static TheoryData<string> ResourceFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.resx", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                 Path.GetFileNameWithoutExtension(f).IndexOf('.', StringComparison.Ordinal) < 0))
        {
            data.Add(Path.GetRelativePath(RepoRoot(), file));
        }

        return data;
    }

    /// <summary>
    /// Every English key has a pt-BR and an es translation, nothing extra, no empty text, and the same
    /// {0}/{1:N0} placeholders — a missing placeholder would throw or drop a number at run time.
    /// </summary>
    [Theory]
    [MemberData(nameof(ResourceFiles))]
    public void Translations_match_the_english_keys(string relativePath)
    {
        var english = Read(Path.Combine(RepoRoot(), relativePath));
        foreach (var culture in new[] { "pt-BR", "es" })
        {
            var path = Path.Combine(RepoRoot(), Path.ChangeExtension(relativePath, null) + $".{culture}.resx");
            Assert.True(File.Exists(path), $"Falta {path}");
            var translated = Read(path);

            Assert.Empty(english.Keys.Except(translated.Keys).Select(k => $"{culture} sem {k}"));
            Assert.Empty(translated.Keys.Except(english.Keys).Select(k => $"{culture} com chave a mais {k}"));
            foreach (var (key, text) in english)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{key} vazio em inglês");
                Assert.False(string.IsNullOrWhiteSpace(translated[key]), $"{key} vazio em {culture}");
                Assert.True(Placeholders(text).SetEquals(Placeholders(translated[key])),
                    $"{culture} {key}: placeholders diferentes de \"{text}\" e \"{translated[key]}\"");
            }
        }
    }

    private static Dictionary<string, string> Read(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    private static HashSet<string> Placeholders(string text) =>
        PlaceholderRegex().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ValheimServerManager.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"(?<!\{)\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderRegex();
}
