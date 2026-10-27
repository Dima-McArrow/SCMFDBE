namespace Csmfd.Server.Tray;

/// <summary>
/// The application icon (Assets/csmfd.ico, rasterized from Assets/csmfd.svg in 16-256 px frames).
/// The same file is the exe icon (ApplicationIcon) and an embedded resource for the tray and windows.
/// </summary>
internal static class AppIcon
{
    public const string ResourceName = "Csmfd.Server.Assets.csmfd.ico";

    /// <summary>
    /// Loads the frame closest to the requested size, e.g. SystemInformation.SmallIconSize for the tray:
    /// 16 px at 100% scaling, 20 px at 125%, 24 px at 150%, so the icon stays sharp on high-DPI screens.
    /// </summary>
    public static Icon Load(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded application icon is missing.");
        return new Icon(stream, size);
    }
}
