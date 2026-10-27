using System.Diagnostics.CodeAnalysis;

namespace Csmfd.Server.Hosting;

/// <summary>
/// Game paths derived from the StarCitizen.exe the user picks in the tray (spec 41.14: set locally on the PC,
/// never over the network). Every channel has the same layout: &lt;channel&gt;\Bin64\StarCitizen.exe writes
/// &lt;channel&gt;\Game.log, where the channel folder is LIVE, PTU, EPTU, ...
/// </summary>
internal sealed record GamePaths(string ExePath, string LogPath, string Channel)
{
    public const string ExeName = "StarCitizen.exe";

    private const string BinFolder = "Bin64";
    private const string LogName = "Game.log";

    public static bool TryFromExe(
        string exePath,
        [NotNullWhen(true)] out GamePaths? paths,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        paths = null;

        if (exePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "Network paths are not supported. Choose StarCitizen.exe on a local drive.";
            return false;
        }

        if (!Path.IsPathFullyQualified(exePath))
        {
            error = "Choose the full path to StarCitizen.exe.";
            return false;
        }

        var full = Path.GetFullPath(exePath);
        if (!string.Equals(Path.GetFileName(full), ExeName, StringComparison.OrdinalIgnoreCase))
        {
            error = "Choose the file named " + ExeName + ".";
            return false;
        }

        var bin = Path.GetDirectoryName(full);
        var channelRoot = bin is null ? null : Path.GetDirectoryName(bin);
        // The channel folder (LIVE, PTU...) must be a real folder, not a drive root such as C:\.
        if (bin is null || channelRoot is null || Path.GetDirectoryName(channelRoot) is null
            || !string.Equals(Path.GetFileName(bin), BinFolder, StringComparison.OrdinalIgnoreCase))
        {
            error = ExeName + " must be inside a " + BinFolder + " folder (for example LIVE\\" + BinFolder + ").";
            return false;
        }

        paths = new GamePaths(full, Path.Combine(channelRoot, LogName), Path.GetFileName(channelRoot));
        error = null;
        return true;
    }
}
