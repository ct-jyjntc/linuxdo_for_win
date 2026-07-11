using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LinuxDo.Core.Utilities;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace LinuxDo.Core.Services;

/// <summary>
/// Cookie-aware image loader with memory + disk cache.
/// BitmapImage alone often fails under Cloudflare or when avatars recycle.
/// </summary>
public sealed class ImageLoader
{
    public static ImageLoader Shared { get; } = new();

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, BitmapImage> _memory = new();
    private readonly ConcurrentDictionary<string, Task<BitmapImage?>> _inflight = new();
    private readonly SemaphoreSlim _netGate = new(1, 1); // serial image downloads under CF
    private readonly string _diskDir;
    private const int MaxMemoryEntries = 300;
    private const long MaxDiskBytes = 200L * 1024 * 1024;
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private int _recent429;

    private ImageLoader()
    {
        _http = new HttpClient(CookieSessionBridge.CreateHandler())
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        CookieSessionBridge.ApplyDefaultHttpClientHeaders(_http);
        try { _http.DefaultRequestHeaders.Remove("Accept"); } catch { /* ignore */ }
        // Prefer formats WinUI BitmapImage can decode
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", CookieSessionBridge.AcceptImage);

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinuxDo", "image-cache");
        Directory.CreateDirectory(root);
        _diskDir = root;
    }

    public void Pause(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        if (until > _pausedUntilUtc) _pausedUntilUtc = until;
    }

    public void Resume() => _pausedUntilUtc = DateTime.MinValue;

    public BitmapImage? Cached(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var key = CacheKey(url);
        return _memory.TryGetValue(key, out var img) ? img : null;
    }

    public async Task<BitmapImage?> LoadAsync(string? url, int retries = 2)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var key = CacheKey(url);
        if (_memory.TryGetValue(key, out var mem)) return mem;

        // Coalesce concurrent loads
        if (_inflight.TryGetValue(key, out var existing))
            return await existing;

        var task = LoadInternalAsync(uri, key, retries);
        _inflight[key] = task;
        try
        {
            return await task;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public void ClearMemory() => _memory.Clear();

    public void ClearDisk()
    {
        try
        {
            if (Directory.Exists(_diskDir))
            {
                foreach (var f in Directory.EnumerateFiles(_diskDir))
                    try { File.Delete(f); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    public void ClearAll()
    {
        ClearMemory();
        ClearDisk();
    }

    public (int MemoryEntries, long DiskBytes, int DiskFiles) Usage()
    {
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_diskDir))
            {
                try
                {
                    bytes += new FileInfo(f).Length;
                    files++;
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
        return (_memory.Count, bytes, files);
    }

    private async Task<BitmapImage?> LoadInternalAsync(Uri uri, string key, int retries)
    {
        // Disk hit
        var diskPath = Path.Combine(_diskDir, key + ".img");
        if (File.Exists(diskPath))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(diskPath);
                var fromDisk = await BitmapFromBytesAsync(bytes);
                if (fromDisk is not null)
                {
                    StoreMemory(key, fromDisk);
                    return fromDisk;
                }
            }
            catch
            {
                try { File.Delete(diskPath); } catch { /* ignore */ }
            }
        }

        // Don't pile on image downloads while CF is blocking API traffic.
        if (DateTime.UtcNow < _pausedUntilUtc || ApiResponseCache.IsPaused || SiteAccessStore.Current.NeedsChallenge)
            return null;

        var attempts = Math.Max(1, retries + 1);
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (DateTime.UtcNow < _pausedUntilUtc || ApiResponseCache.IsPaused || SiteAccessStore.Current.NeedsChallenge)
                    return null;

                await _netGate.WaitAsync();
                try
                {
                    // Re-check after waiting for the gate — many tasks pile up then all 429.
                    if (DateTime.UtcNow < _pausedUntilUtc || SiteAccessStore.Current.NeedsChallenge)
                        return null;

                    using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                    var page = !string.IsNullOrEmpty(uri.Host)
                        ? new Uri($"https://{uri.Host}/")
                        : null;
                    CookieSessionBridge.ApplyBrowserHeaders(
                        req,
                        pageUrl: page,
                        acceptJson: false,
                        fetchSite: "same-origin",
                        fetchMode: "no-cors",
                        fetchDest: "image");
                    try { req.Headers.Remove("Accept"); } catch { /* ignore */ }
                    req.Headers.TryAddWithoutValidation("Accept", CookieSessionBridge.AcceptImage);

                    using var resp = await _http.SendAsync(req);
                    if ((int)resp.StatusCode == 429)
                    {
                        var n = Interlocked.Increment(ref _recent429);
                        var pauseSec = n >= 3 ? 90 : 45;
                        Pause(TimeSpan.FromSeconds(pauseSec));
                        // Log once per pause window
                        if (n == 1 || n == 3)
                            AppLog.Warning("image", $"429 Too Many Requests — pause image loads {pauseSec}s");
                        return null;
                    }
                    if ((int)resp.StatusCode is 403 or 503)
                    {
                        Pause(TimeSpan.FromSeconds(30));
                        return null;
                    }

                    Interlocked.Exchange(ref _recent429, 0);
                    resp.EnsureSuccessStatusCode();
                    var data = await resp.Content.ReadAsByteArrayAsync();
                    if (data.Length == 0) return null;

                    // Persist raw bytes
                    try
                    {
                        await File.WriteAllBytesAsync(diskPath, data);
                        _ = Task.Run(TrimDiskIfNeeded);
                    }
                    catch { /* ignore disk errors */ }

                    var bmp = await BitmapFromBytesAsync(data);
                    if (bmp is not null) StoreMemory(key, bmp);
                    return bmp;
                }
                finally
                {
                    _netGate.Release();
                }
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(120 * (i + 1));
            }
        }

        if (last is not null)
            AppLog.Warning("image", $"Load failed {uri.Host}: {last.Message}");
        return null;
    }

    private void StoreMemory(string key, BitmapImage img)
    {
        if (_memory.Count >= MaxMemoryEntries)
        {
            // Simple eviction: drop first ~20%
            foreach (var k in _memory.Keys.Take(MaxMemoryEntries / 5).ToList())
                _memory.TryRemove(k, out _);
        }
        _memory[key] = img;
    }

    private void TrimDiskIfNeeded()
    {
        try
        {
            var files = Directory.GetFiles(_diskDir)
                .Select(p => new FileInfo(p))
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();
            long total = files.Sum(f => f.Length);
            if (total <= MaxDiskBytes) return;
            foreach (var f in files)
            {
                if (total <= MaxDiskBytes * 0.8) break;
                try
                {
                    total -= f.Length;
                    f.Delete();
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    private static Task<BitmapImage?> BitmapFromBytesAsync(byte[] data)
    {
        try
        {
            // BitmapImage must be created on the UI dispatcher thread.
            var dq = App.DispatcherQueue
                     ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dq is null) return Task.FromResult<BitmapImage?>(null);

            if (dq.HasThreadAccess)
                return DecodeOnCurrentThreadAsync(data);

            var tcs = new TaskCompletionSource<BitmapImage?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dq.TryEnqueue(() =>
                {
                    _ = CompleteDecodeAsync(data, tcs);
                }))
            {
                return Task.FromResult<BitmapImage?>(null);
            }
            return tcs.Task;
        }
        catch
        {
            return Task.FromResult<BitmapImage?>(null);
        }
    }

    private static async Task CompleteDecodeAsync(byte[] data, TaskCompletionSource<BitmapImage?> tcs)
    {
        try
        {
            tcs.TrySetResult(await DecodeOnCurrentThreadAsync(data));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private static async Task<BitmapImage?> DecodeOnCurrentThreadAsync(byte[] data)
    {
        var ms = new InMemoryRandomAccessStream();
        var writer = new DataWriter(ms.GetOutputStreamAt(0));
        writer.WriteBytes(data);
        await writer.StoreAsync();
        await writer.FlushAsync();
        // Detach so disposing the writer does not close the stream BitmapImage still needs.
        writer.DetachStream();
        writer.Dispose();
        ms.Seek(0);

        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(ms);
        return bmp;
    }

    private static string CacheKey(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }
}
