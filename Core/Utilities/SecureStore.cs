using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Security.Credentials;

namespace LinuxDo.Core.Utilities;

/// <summary>Windows Credential Locker / DPAPI-backed secure storage.</summary>
public static class SecureStore
{
    private const string Resource = "LinuxDo.Auth";

    public static void SaveString(string account, string value)
    {
        try
        {
            var vault = new PasswordVault();
            try
            {
                var existing = vault.Retrieve(Resource, account);
                vault.Remove(existing);
            }
            catch
            {
                // none
            }
            vault.Add(new PasswordCredential(Resource, account, value));
        }
        catch
        {
            // Fallback to DPAPI local file
            SaveDpapi(account, Encoding.UTF8.GetBytes(value));
        }
    }

    public static string? LoadString(string account)
    {
        try
        {
            var vault = new PasswordVault();
            var cred = vault.Retrieve(Resource, account);
            cred.RetrievePassword();
            return cred.Password;
        }
        catch
        {
            var data = LoadDpapi(account);
            return data is null ? null : Encoding.UTF8.GetString(data);
        }
    }

    public static void SaveBytes(string account, byte[] data)
    {
        try
        {
            SaveString(account, Convert.ToBase64String(data));
        }
        catch
        {
            SaveDpapi(account, data);
        }
    }

    public static byte[]? LoadBytes(string account)
    {
        var s = LoadString(account);
        if (s is null) return null;
        try { return Convert.FromBase64String(s); }
        catch { return Encoding.UTF8.GetBytes(s); }
    }

    public static void Delete(string account)
    {
        try
        {
            var vault = new PasswordVault();
            var cred = vault.Retrieve(Resource, account);
            vault.Remove(cred);
        }
        catch
        {
            // ignore
        }
        try
        {
            var path = DpapiPath(account);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string DpapiPath(string account)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinuxDo", "secure");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(account.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return Path.Combine(dir, safe + ".bin");
    }

    private static void SaveDpapi(string account, byte[] data)
    {
        var protectedBytes = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(DpapiPath(account), protectedBytes);
    }

    private static byte[]? LoadDpapi(string account)
    {
        var path = DpapiPath(account);
        if (!File.Exists(path)) return null;
        var protectedBytes = File.ReadAllBytes(path);
        return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
    }
}
