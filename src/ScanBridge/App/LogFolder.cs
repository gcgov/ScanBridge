using System.Diagnostics;

namespace ScanBridge.App;

/// <summary>Where ScanBridge writes its log files, and a helper to show them in Explorer.</summary>
public static class LogFolder
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanBridge", "logs");

    public static void Open()
    {
        Directory.CreateDirectory(Path);
        Process.Start(new ProcessStartInfo(Path) { UseShellExecute = true });
    }
}
