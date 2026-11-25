using System.Reflection;

namespace ScanBridge;

/// <summary>Version of the running ScanBridge, shown in the UI, the tray and the API.</summary>
public static class AppInfo
{
    /// <summary>Major.minor.build, e.g. "1.2.0" (a pre-release suffix such as "-test" is not included).</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
