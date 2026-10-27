using System.Security.Cryptography;
using Csmfd.Server.Hosting;
using Csmfd.Server.Tray;

namespace Csmfd.Server;

internal static class Program
{
    /// <summary>One instance per user session, ordinary user rights, tray only (spec 41.3).</summary>
    [STAThread]
    private static int Main()
    {
        using var instance = new Mutex(initiallyOwned: true, @"Local\CSMFD.Server", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("CSMFD Server is already running (see the tray).", "CSMFD Server", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        ApplicationConfiguration.Initialize();

        ServerApp app;
        try
        {
            app = new ServerApp(ServerStorage.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException)
        {
            MessageBox.Show("CSMFD Server could not start: " + ex.Message, "CSMFD Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        using var tray = new TrayContext(app);
        Application.Run(tray);
        return 0;
    }
}
