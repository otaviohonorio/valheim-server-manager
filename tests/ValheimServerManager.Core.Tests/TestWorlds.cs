using System.Buffers.Binary;
using System.Text;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Tests;

/// <summary>Builds synthetic Valheim 1.0 worlds on disk. No real save data is committed to the repo.</summary>
internal static class TestWorlds
{
    public const ushort Version = 41;

    public static byte[] ChunkBytes(int zdoCount, int payloadBytes = 256)
    {
        var data = new byte[ChunkFileHeader.Size + payloadBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(data, Version);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), zdoCount);
        Random.Shared.NextBytes(data.AsSpan(ChunkFileHeader.Size));
        return data;
    }

    public static byte[] Fwl2Bytes(
        string name,
        IReadOnlyList<string>? keys = null,
        IReadOnlyList<WorldPlayer>? players = null,
        string seedName = "TestSeed")
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((int)Version);
            w.Write(name);
            w.Write(seedName);
            w.Write(123456789);
            w.Write(-817225763L);
            w.Write(2);
            w.Write(true);
            keys ??= [];
            w.Write(keys.Count);
            foreach (var k in keys)
            {
                w.Write(k);
            }

            players ??= [];
            w.Write(players.Count);
            foreach (var p in players)
            {
                w.Write(p.PlatformId);
                w.Write(p.Name);
                w.Write(p.CharacterName);
                w.Write(p.PlayerId);
            }
        }

        var payload = body.ToArray();
        var file = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(file, payload.Length);
        payload.CopyTo(file, 4);
        return file;
    }

    /// <summary>Writes a complete, healthy save <paramref name="saveNumber"/> with the given chunks.</summary>
    public static void WriteSave(
        string worldDirectory,
        string worldName,
        int saveNumber,
        IReadOnlyList<(sbyte X, sbyte Z, int Revision, int Zdos)> chunks,
        IReadOnlyList<string>? keys = null,
        bool writeDb2 = true,
        bool writeOk = true)
    {
        Directory.CreateDirectory(worldDirectory);
        var entries = new List<ChunkIndexEntry>();
        foreach (var (x, z, rev, zdos) in chunks)
        {
            var entry = new ChunkIndexEntry(x, z, 1, rev, zdos);
            entries.Add(entry);
            File.WriteAllBytes(Path.Combine(worldDirectory, entry.FileName), ChunkBytes(zdos));
        }

        var index = new ChunkIndex(Version, entries);
        File.WriteAllBytes(Path.Combine(worldDirectory, $"_main.{saveNumber}.chunks"), index.ToBytes());
        File.WriteAllBytes(Path.Combine(worldDirectory, $"_main.{saveNumber}.fwl2"), Fwl2Bytes(worldName, keys));
        if (writeDb2)
        {
            File.WriteAllBytes(Path.Combine(worldDirectory, $"_main.{saveNumber}.db2"), new byte[512]);
        }

        if (writeOk)
        {
            File.WriteAllBytes(Path.Combine(worldDirectory, $"_main.{saveNumber}.ok"), [0x29, 0, 0, 0]);
        }
    }

    public static IReadOnlyList<(sbyte, sbyte, int, int)> DefaultChunks { get; } =
        [(0x1e, 0x1e, 5, 4334), (0x1e, 0x20, 11, 20505), (0x20, 0x20, 4, 13881)];
}

/// <summary>A disposable temporary directory.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vsm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public ServerProfile Profile(string world = "MeuMundo")
    {
        var serverDir = Combine("server");
        Directory.CreateDirectory(serverDir);
        File.WriteAllBytes(System.IO.Path.Combine(serverDir, "valheim_server.exe"), []);
        return new ServerProfile
        {
            DisplayName = "Teste",
            ServerDirectory = serverDir,
            SaveDirectory = Combine("ServerSave"),
            ServerName = "Servidor de Teste",
            WorldName = world,
            Port = 38456,
            Password = "segredo123",
            Public = false,
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
