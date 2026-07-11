using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LinuxDo.Core.Utilities;

namespace LinuxDo.Core.Services;

/// <summary>Discourse MessageBus long-poll client for notification + topic channels.</summary>
public sealed class MessageBusService
{
    public static MessageBusService Shared { get; } = new();

    public sealed class Message
    {
        public required string Channel { get; init; }
        public int MessageId { get; init; }
        public Dictionary<string, object?> Data { get; init; } = new();
    }

    private readonly string _clientId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, int> _subscriptions = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Uri _baseUrl = new("https://linux.do");
    private Action<IReadOnlyList<Message>>? _handler;
    private int _failureCount;
    private DateTime _pausedUntilUtc = DateTime.MinValue;

    public void Configure(Uri baseUrl) => _baseUrl = baseUrl;

    public void SetHandler(Action<IReadOnlyList<Message>>? handler) => _handler = handler;

    /// <summary>Pause long-poll while CF challenge is open (avoids tight-loop 403s).</summary>
    public void Pause(TimeSpan duration)
    {
        lock (_gate)
        {
            var until = DateTime.UtcNow + duration;
            if (until > _pausedUntilUtc) _pausedUntilUtc = until;
        }
        AppLog.Network($"MessageBus paused {duration.TotalSeconds:0}s");
    }

    public void Resume()
    {
        lock (_gate) _pausedUntilUtc = DateTime.MinValue;
        AppLog.Network("MessageBus resumed");
    }

    public void Subscribe(string channel, int lastMessageId = -1)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(channel, out var existing))
                _subscriptions[channel] = Math.Max(existing, lastMessageId);
            else
                _subscriptions[channel] = lastMessageId;
        }
        StartPollingIfNeeded();
    }

    public void Unsubscribe(string channel)
    {
        lock (_gate)
        {
            _subscriptions.Remove(channel);
            if (_subscriptions.Count == 0) StopPolling();
        }
    }

    public void UnsubscribeAll()
    {
        lock (_gate)
        {
            _subscriptions.Clear();
            StopPolling();
        }
    }

    private void StartPollingIfNeeded()
    {
        lock (_gate)
        {
            if (_cts is not null || _subscriptions.Count == 0) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => PollLoopAsync(token), token);
        }
    }

    private void StopPolling()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Dictionary<string, int> snap;
            DateTime pausedUntil;
            lock (_gate)
            {
                if (_subscriptions.Count == 0) break;
                snap = new Dictionary<string, int>(_subscriptions);
                pausedUntil = _pausedUntilUtc;
            }

            // While CF challenge is open, do not hammer /message-bus.
            if (DateTime.UtcNow < pausedUntil || ApiResponseCache.IsPaused)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
                catch { break; }
                continue;
            }

            try
            {
                var messages = await PerformPollAsync(snap, token);
                if (messages.Count > 0)
                {
                    lock (_gate)
                    {
                        foreach (var msg in messages)
                        {
                            if (msg.Channel == "/__status")
                            {
                                foreach (var (ch, value) in msg.Data)
                                {
                                    var id = ToInt(value);
                                    if (id is not null)
                                        _subscriptions[ch] = Math.Max(_subscriptions.GetValueOrDefault(ch, -1), id.Value);
                                }
                            }
                            else
                            {
                                _subscriptions[msg.Channel] = Math.Max(
                                    _subscriptions.GetValueOrDefault(msg.Channel, -1), msg.MessageId);
                            }
                        }
                    }

                    var batch = messages.Where(m => m.Channel != "/__status").ToList();
                    if (batch.Count > 0)
                    {
                        try { _handler?.Invoke(batch); } catch { /* ignore */ }
                        foreach (var msg in batch.Where(m => m.Channel.StartsWith("/topic/", StringComparison.Ordinal)))
                            AppEvents.RaiseTopicBus(msg.Channel, msg.Data);
                    }
                    _failureCount = 0;
                }
                else
                {
                    // Idle long-poll returned empty quickly — small breathing room.
                    try { await Task.Delay(TimeSpan.FromMilliseconds(400), token); }
                    catch { break; }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _failureCount++;
                AppLog.Warning("messagebus", ex.Message);
                // On CF / 403 / 404 / rate limit, back off hard and pause a bit.
                // Logs showed poll HTTP 404 every few seconds after login — was burning requests.
                var msg = ex.Message ?? "";
                if (msg.Contains("403", StringComparison.Ordinal) ||
                    msg.Contains("404", StringComparison.Ordinal) ||
                    msg.Contains("503", StringComparison.Ordinal) ||
                    msg.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("challenge", StringComparison.OrdinalIgnoreCase))
                {
                    // 404 often means session/channel not ready yet — long pause, don't spam.
                    var pauseMin = msg.Contains("404", StringComparison.Ordinal) ? 5 : 2;
                    Pause(TimeSpan.FromMinutes(pauseMin));
                    if (!msg.Contains("404", StringComparison.Ordinal))
                        ApiResponseCache.Pause(TimeSpan.FromMinutes(2));
                }
                var delay = msg.Contains("404", StringComparison.Ordinal)
                    ? Math.Min(120, 15 * _failureCount)
                    : Math.Min(45, 3 * _failureCount);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), token); }
                catch { break; }
            }
        }
    }

    private async Task<List<Message>> PerformPollAsync(Dictionary<string, int> subs, CancellationToken token)
    {
        // Build form body: channel => lastId
        var pairs = subs.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={kv.Value}");
        // Discourse also wants __seq / dlp optional; minimal is fine
        // Discourse message-bus expects channels as form fields; include __seq for some versions.
        var body = string.Join("&", pairs);
        if (!string.IsNullOrEmpty(body)) body += "&";
        body += "__seq=0";

        // Prefer relative combine without double-slash issues.
        var baseStr = _baseUrl.AbsoluteUri.TrimEnd('/');
        var url = new Uri($"{baseStr}/message-bus/{_clientId}/poll?dlp=t");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        CookieSessionBridge.ApplyBrowserHeaders(request, pageUrl: _baseUrl, acceptJson: true);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Discourse-Present", "true");

        // Use shared cookie jar via a short-lived client
        using var handler = CookieSessionBridge.CreateHandler();
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        CookieSessionBridge.ApplyDefaultHttpClientHeaders(http);
        using var response = await http.SendAsync(request, token);
        var data = await response.Content.ReadAsByteArrayAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            if (code == 429) throw new Exception("rate limited");
            // CF sometimes returns 403 HTML; treat as challenge-ish for backoff.
            if (code is 403 or 503 && ResponseInspector.LooksLikeCloudflare(data, code))
                throw new Exception("challenge poll blocked");
            throw new Exception($"poll HTTP {code}");
        }

        if (!ResponseInspector.IsProbablyJson(data))
            return [];

        return ParseMessages(data);
    }

    private static List<Message> ParseMessages(byte[] data)
    {
        var list = new List<Message>();
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var channel = item.TryGetProperty("channel", out var ch) ? ch.GetString() ?? "" : "";
                var messageId = item.TryGetProperty("message_id", out var mid)
                    ? JsonFlexible.GetInt(mid) ?? -1 : -1;
                var dict = new Dictionary<string, object?>();
                if (item.TryGetProperty("data", out var dataEl))
                {
                    if (dataEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in dataEl.EnumerateObject())
                            dict[p.Name] = JsonElementToObject(p.Value);
                    }
                    else
                    {
                        dict["value"] = JsonElementToObject(dataEl);
                    }
                }
                if (!string.IsNullOrEmpty(channel))
                    list.Add(new Message { Channel = channel, MessageId = messageId, Data = dict });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("messagebus", "parse: " + ex.Message);
        }
        return list;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number when el.TryGetInt32(out var i) => i,
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        _ => el.ToString()
    };

    private static int? ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var n) => n,
        _ => null
    };
}
