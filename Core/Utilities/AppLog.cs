using System.Diagnostics;
using System.Text;

namespace LinuxDo.Core.Utilities;

/// <summary>
/// Debug.WriteLine + rolling file log under %LocalAppData%\LinuxDo\logs\
/// so packaged / standalone runs still leave a trail (VS Output alone is not enough).
/// </summary>
public static class AppLog
{
    private static readonly object FileGate = new();
    private static string? _logDir;
    private static string? _logFile;
    private static int _dayStamp;

    public static string LogDirectory
    {
        get
        {
            EnsurePath();
            return _logDir!;
        }
    }

    public static string CurrentLogFile
    {
        get
        {
            EnsurePath();
            return _logFile!;
        }
    }

    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Warning(string category, string message) => Write("WARN", category, message);
    public static void Error(string category, string message) => Write("ERROR", category, message);

    public static void Network(string message) => Info("network", message);
    public static void Auth(string message) => Info("auth", message);

    private static void Write(string level, string category, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{category}] {message}";
        Debug.WriteLine($"[LinuxDo/{category}{(level == "INFO" ? "" : " " + level)}] {message}");
        try
        {
            EnsurePath();
            lock (FileGate)
            {
                File.AppendAllText(_logFile!, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // never throw from logging
        }
    }

    private static void EnsurePath()
    {
        var today = DateTime.Now.Year * 10000 + DateTime.Now.Month * 100 + DateTime.Now.Day;
        if (_logDir is not null && _logFile is not null && _dayStamp == today)
            return;

        lock (FileGate)
        {
            if (_logDir is not null && _logFile is not null && _dayStamp == today)
                return;

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LinuxDo", "logs");
            Directory.CreateDirectory(root);
            _logDir = root;
            _dayStamp = today;
            _logFile = Path.Combine(root, $"linuxdo-{DateTime.Now:yyyyMMdd}.log");

            // Trim old logs (keep ~14 days)
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "linuxdo-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14))
                            File.Delete(f);
                    }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }

            try
            {
                File.AppendAllText(_logFile,
                    Environment.NewLine +
                    $"======== session {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ========" +
                    Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { /* ignore */ }
        }
    }
}
