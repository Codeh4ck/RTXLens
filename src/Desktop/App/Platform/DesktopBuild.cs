namespace RTXLens.App.Platform;

internal static class DesktopBuild
{
#if DEBUG
    public static bool IsDebug => true;
    public const string DataDirectoryName = "RTXLens.Debug";
    public const string WindowTitle = "RTX Lens (Debug)";
    public const string InstanceName = "RTXLens.App.Debug";
    public static readonly Guid TrayIconId = new("837E90FD-7C75-46AF-8076-E2EB775CE2FC");
#else
    public static bool IsDebug => false;
    public const string DataDirectoryName = "RTXLens";
    public const string WindowTitle = "RTX Lens";
    public const string InstanceName = "RTXLens.App";
    public static readonly Guid TrayIconId = new("2C899FB2-3A10-4548-A37E-28F976C6B3AF");
#endif

    public const string ShowMessageName = InstanceName + ".Show";
}
