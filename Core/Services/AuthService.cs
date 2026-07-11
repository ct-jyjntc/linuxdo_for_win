using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxDo.Core.Models;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

public sealed class AuthService
{
    public static AuthService Shared { get; } = new();

    private const string ClientIdAccount = "auth.clientId";
    private const string SessionAccount = "auth.session";
    private const string PrivateKeyAccount = "auth.privateKey";
    private const string PendingNonceAccount = "auth.pendingNonce";

    private const string ApplicationName = "LinuxDo";
    private const string Scopes = "read,write,message,notifications,session_info";
    private const string AuthRedirect = "linuxdo://auth";

    public string ClientId()
    {
        var existing = SecureStore.LoadString(ClientIdAccount);
        if (!string.IsNullOrEmpty(existing)) return existing;
        var id = Guid.NewGuid().ToString("N").ToLowerInvariant();
        SecureStore.SaveString(ClientIdAccount, id);
        return id;
    }

    public AuthSession? LoadSession()
    {
        var raw = SecureStore.LoadString(SessionAccount);
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<AuthSession>(raw, JsonFlexible.Options);
        }
        catch
        {
            return null;
        }
    }

    public void SaveSession(AuthSession session)
    {
        var json = JsonSerializer.Serialize(session, JsonFlexible.Options);
        SecureStore.SaveString(SessionAccount, json);
    }

    public void ClearSession()
    {
        SecureStore.Delete(SessionAccount);
        SecureStore.Delete(PendingNonceAccount);
    }

    public AuthSession SaveCookieSession(string username, int? userId, string? avatarTemplate = null)
    {
        var session = AuthSession.Cookie(username, userId, avatarTemplate);
        SaveSession(session);
        return session;
    }

    public AuthSession SaveManualKey(string apiKey, string username)
    {
        var session = AuthSession.UserApi(
            apiKey.Trim(),
            username.Trim(),
            ClientId(),
            null);
        SaveSession(session);
        return session;
    }

    // MARK: - User API Key (may be disabled by site admin)

    public Uri BeginAuthorization(Uri baseUrl)
    {
        var pair = RSAHelper.GenerateKeyPair();
        SecureStore.SaveBytes(PrivateKeyAccount, pair.PrivateKeyPkcs1);
        pair.PrivateKey.Dispose();

        var nonce = Guid.NewGuid().ToString("N").ToLowerInvariant();
        SecureStore.SaveString(PendingNonceAccount, nonce);

        var clientId = ClientId();
        var baseStr = baseUrl.AbsoluteUri.TrimEnd('/');
        var qs = new StringBuilder();
        qs.Append("application_name=").Append(Uri.EscapeDataString(ApplicationName));
        qs.Append("&client_id=").Append(Uri.EscapeDataString(clientId));
        qs.Append("&scopes=").Append(Uri.EscapeDataString(Scopes));
        qs.Append("&public_key=").Append(Uri.EscapeDataString(pair.PublicKeyPem));
        qs.Append("&nonce=").Append(Uri.EscapeDataString(nonce));
        qs.Append("&auth_redirect=").Append(Uri.EscapeDataString(AuthRedirect));

        var url = new Uri($"{baseStr}/user-api-key/new?{qs}");
        AppLog.Auth("Begin User API authorization");
        return url;
    }

    public async Task<AuthSession> CompleteAuthorizationAsync(Uri url)
    {
        var payload = ExtractPayload(url);
        if (string.IsNullOrEmpty(payload))
            throw new InvalidOperationException("授权回调缺少 payload");

        return await DecryptAndStoreAsync(payload);
    }

    private static string? ExtractPayload(Uri url)
    {
        var fromQuery = GetQueryParam(url.Query, "payload");
        if (!string.IsNullOrEmpty(fromQuery)) return fromQuery;

        var frag = url.Fragment?.TrimStart('#') ?? "";
        if (string.IsNullOrEmpty(frag)) return null;

        if (frag.StartsWith('?')) frag = frag[1..];
        var fromFrag = GetQueryParam("?" + frag, "payload");
        if (!string.IsNullOrEmpty(fromFrag)) return fromFrag;

        if (frag.StartsWith("payload=", StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(frag["payload=".Length..]);

        return null;
    }

    private static string? GetQueryParam(string query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            var key = Uri.UnescapeDataString(part[..idx]);
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(part[(idx + 1)..]);
        }
        return null;
    }

    private async Task<AuthSession> DecryptAndStoreAsync(string payload)
    {
        var privateData = SecureStore.LoadBytes(PrivateKeyAccount);
        if (privateData is null || privateData.Length == 0)
            throw new InvalidOperationException("找不到本地私钥，请重新发起授权");

        using var privateKey = RSAHelper.ImportPrivateKey(privateData);
        var plain = RSAHelper.DecryptPkcs1(payload, privateKey);
        var decoded = JsonSerializer.Deserialize<UserApiKeyPayload>(plain, JsonFlexible.Options)
                      ?? throw new InvalidOperationException("无法解析授权数据");

        var expectedNonce = SecureStore.LoadString(PendingNonceAccount);
        if (!string.IsNullOrEmpty(expectedNonce) &&
            !string.IsNullOrEmpty(decoded.Nonce) &&
            !string.Equals(expectedNonce, decoded.Nonce, StringComparison.Ordinal))
        {
            AppLog.Auth("Nonce mismatch (continuing)");
        }

        if (string.IsNullOrEmpty(decoded.Key))
            throw new InvalidOperationException("授权数据中没有 API Key");

        var clientId = ClientId();
        DiscourseAPI.Shared.SetCredentials(decoded.Key, clientId);
        var user = await DiscourseAPI.Shared.FetchCurrentUserAsync();
        var session = AuthSession.UserApi(
            decoded.Key,
            user?.Username ?? "user",
            clientId,
            user?.Id,
            user?.AvatarTemplate);
        SaveSession(session);
        SecureStore.Delete(PendingNonceAccount);
        AppLog.Auth($"Authorization completed for {session.Username}");
        return session;
    }
}

public sealed class UserApiKeyPayload
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("push")] public bool? Push { get; set; }
    [JsonPropertyName("api_version")] public int? ApiVersion { get; set; }
}
