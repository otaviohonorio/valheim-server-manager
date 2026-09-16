using System.Text;

namespace ValheimServerManager.Core.Players;

public enum PlayerListKind
{
    Admins,
    Banned,
    Permitted,
}

/// <summary>
/// <c>adminlist.txt</c>, <c>bannedlist.txt</c> and <c>permittedlist.txt</c> inside the save directory.
/// One id per line; lines starting with <c>//</c> are comments. The server reads them on start.
/// </summary>
public static class PlayerListFile
{
    public static string FileName(PlayerListKind kind) => kind switch
    {
        PlayerListKind.Admins => "adminlist.txt",
        PlayerListKind.Banned => "bannedlist.txt",
        _ => "permittedlist.txt",
    };

    private static string DefaultHeader(PlayerListKind kind) => kind switch
    {
        PlayerListKind.Admins => "// List admin players ID  ONE per line",
        PlayerListKind.Banned => "// List banned players ID  ONE per line",
        _ => "// List permitted players ID ONE per line",
    };

    public static string PathFor(string saveDirectory, PlayerListKind kind) =>
        Path.Combine(saveDirectory, FileName(kind));

    public static IReadOnlyList<string> Read(string saveDirectory, PlayerListKind kind)
    {
        var path = PathFor(saveDirectory, kind);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static void Write(string saveDirectory, PlayerListKind kind, IEnumerable<string> ids)
    {
        Directory.CreateDirectory(saveDirectory);
        var path = PathFor(saveDirectory, kind);
        var comments = File.Exists(path)
            ? File.ReadAllLines(path).Where(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToList()
            : [];
        if (comments.Count == 0)
        {
            comments.Add(DefaultHeader(kind));
        }

        var clean = ids.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct(StringComparer.Ordinal);
        var temp = path + ".tmp";
        File.WriteAllLines(temp, comments.Concat(clean), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public static bool Contains(string saveDirectory, PlayerListKind kind, string id) =>
        Read(saveDirectory, kind).Contains(id, StringComparer.Ordinal);

    public static void Set(string saveDirectory, PlayerListKind kind, string id, bool present)
    {
        var current = Read(saveDirectory, kind).ToList();
        var has = current.Contains(id, StringComparer.Ordinal);
        if (present && !has)
        {
            current.Add(id);
        }
        else if (!present && has)
        {
            current.RemoveAll(i => i.Equals(id, StringComparison.Ordinal));
        }
        else
        {
            return;
        }

        Write(saveDirectory, kind, current);
    }

    /// <summary>Accepts a raw Steam64 id or a <c>Steam_</c> platform id and returns the form the lists use.</summary>
    public static string NormalizeId(string id)
    {
        var trimmed = id.Trim();
        return trimmed.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase) ? trimmed["Steam_".Length..] : trimmed;
    }
}
