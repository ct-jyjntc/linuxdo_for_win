using System.Reflection;
using System.Text.RegularExpressions;

namespace LinuxDo.Core.Utilities;

/// <summary>
/// Marketing version + package/build id (timestamp like 20260711-094016).
/// BuildInfo.g.cs is written by MSBuild / Package.ps1.
/// </summary>
public static partial class AppVersion
{
    public const string RepoUrl = "https://github.com/ct-jyjntc/linuxdo_for_win";
    public const string ReleasesApiUrl = "https://api.github.com/repos/ct-jyjntc/linuxdo_for_win/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/ct-jyjntc/linuxdo_for_win/releases";

    /// <summary>Build id shown in Settings — package timestamp or "dev". All versioning is build-based.</summary>
    public static string BuildId
    {
        get
        {
            try
            {
                var generated = BuildInfo.BuildId;
                if (!string.IsNullOrWhiteSpace(generated))
                    return generated.Trim();
            }
            catch { /* ignore */ }

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    var n = NormalizeBuildId(info);
                    if (TimestampRegex().IsMatch(n)) return n;
                }
            }
            catch { /* ignore */ }

            return "dev";
        }
    }

    /// <summary>Single display string — build id only (no marketing version).</summary>
    public static string DisplayVersion => BuildId;

    /// <summary>Strip leading v / extract yyyyMMdd-HHmmss if present.</summary>
    public static string NormalizeBuildId(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..].Trim();
        var m = TimestampRegex().Match(s);
        if (m.Success) return m.Value;
        return s;
    }

    /// <summary>
    /// True if <paramref name="latest"/> is newer than <paramref name="current"/>.
    /// Timestamp ids compare lexicographically (yyyyMMdd-HHmmss sorts correctly).
    /// </summary>
    public static bool IsNewer(string latest, string current)
    {
        var a = NormalizeBuildId(latest);
        var b = NormalizeBuildId(current);
        if (string.IsNullOrEmpty(a)) return false;
        if (string.IsNullOrEmpty(b) || b is "dev") return true;
        return string.CompareOrdinal(a, b) > 0;
    }

    [GeneratedRegex(@"\d{8}-\d{6}")]
    private static partial Regex TimestampRegex();
}
