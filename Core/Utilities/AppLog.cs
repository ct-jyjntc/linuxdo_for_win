using System.Diagnostics;

namespace LinuxDo.Core.Utilities;

public static class AppLog
{
    public static void Info(string category, string message)
        => Debug.WriteLine($"[LinuxDo/{category}] {message}");

    public static void Warning(string category, string message)
        => Debug.WriteLine($"[LinuxDo/{category} WARN] {message}");

    public static void Error(string category, string message)
        => Debug.WriteLine($"[LinuxDo/{category} ERROR] {message}");

    public static void Network(string message) => Info("network", message);
    public static void Auth(string message) => Info("auth", message);
}
