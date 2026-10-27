using System.Text.Json;
using System.Text.Json.Serialization;
using Csmfd.Protocol;

namespace Csmfd.Server.Hosting;

/// <summary>Local settings. Only ever changed on the PC (tray or file), never over the network.</summary>
internal sealed record ServerConfig
{
    public const string DefaultGameRoot = @"C:\Program Files\Roberts Space Industries\StarCitizen\LIVE";

    /// <summary>Private IPv4 chosen by the user. Null means the listener stays off (spec 41.4).</summary>
    public string? BindAddress { get; init; }

    public string GameExePath { get; init; } = Path.Combine(DefaultGameRoot, "Bin64", "StarCitizen.exe");

    public string GameLogPath { get; init; } = Path.Combine(DefaultGameRoot, "Game.log");

    public ChatKey ChatKey { get; init; } = ChatKey.Enter;

    public string DisplayName { get; init; } = Environment.MachineName;

    /// <summary>Stable server identity (spec 41.5 item 1), created on first start.</summary>
    public Guid ServerId { get; init; } = Guid.NewGuid();
}

/// <summary>Data directory and JSON persistence in %LocalAppData%\CSMFD (current user only).</summary>
internal sealed class ServerStorage(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, // hand-edited files may use any casing
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>%LocalAppData%\CSMFD, or CSMFD_DATA_DIR for development runs that must not touch real data.</summary>
    public static ServerStorage Default { get; } = new(
        Environment.GetEnvironmentVariable("CSMFD_DATA_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSMFD"));

    public string Directory { get; } = directory;

    public string LogDirectory => Path.Combine(Directory, "logs");

    public string CertificatePath => Path.Combine(Directory, "server-certificate.bin");

    public string TrustPath => Path.Combine(Directory, "trusted-devices.json");

    public string EmotesPath => Path.Combine(Directory, "emotes.txt");

    private string ConfigPath => Path.Combine(Directory, "config.json");

    public ServerConfig LoadConfig()
    {
        EnsureDirectory();
        if (File.Exists(ConfigPath))
        {
            var loaded = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(ConfigPath), JsonOptions);
            if (loaded is not null)
            {
                return loaded;
            }
        }

        var created = new ServerConfig();
        SaveConfig(created);
        return created;
    }

    public void SaveConfig(ServerConfig config) => WriteAtomically(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));

    public static T? ReadJson<T>(string path)
        where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;

    public void WriteJson<T>(string path, T value) => WriteAtomically(path, JsonSerializer.Serialize(value, JsonOptions));

    public void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);

    /// <summary>Write to a temp file and move over the target so a crash never leaves half a file.</summary>
    public void WriteAtomically(string path, string content)
    {
        EnsureDirectory();
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
