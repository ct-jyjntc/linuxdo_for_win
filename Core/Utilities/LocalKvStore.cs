using System.Text.Json;
using Windows.Storage;

namespace LinuxDo.Core.Utilities;

/// <summary>
/// Key/value persistence that works both packaged (ApplicationData.LocalSettings)
/// and unpackaged (JSON under %LocalAppData%\LinuxDo). Avoids TypeInitializationException
/// when ApplicationData.Current is unavailable.
/// </summary>
public sealed class LocalKvStore
{
    private static readonly Lazy<LocalKvStore> SharedLazy = new(() => new LocalKvStore("app"));
    public static LocalKvStore Shared => SharedLazy.Value;

    private readonly ApplicationDataContainer? _container;
    private readonly Dictionary<string, object?> _fileValues = new(StringComparer.Ordinal);
    private readonly string _filePath;
    private readonly object _gate = new();
    private bool _useFile;

    public LocalKvStore(string bucket)
    {
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinuxDo",
            $"kv-{bucket}.json");

        try
        {
            _container = ApplicationData.Current.LocalSettings;
            _useFile = false;
        }
        catch (Exception ex)
        {
            _container = null;
            _useFile = true;
            AppLog.Warning("kv", $"ApplicationData unavailable ({bucket}): {ex.Message}");
            LoadFile();
        }
    }

    public bool TryGetString(string key, out string? value)
    {
        value = null;
        try
        {
            if (!_useFile && _container is not null)
            {
                if (_container.Values.TryGetValue(key, out var v) && v is string s)
                {
                    value = s;
                    return true;
                }
                return false;
            }
            lock (_gate)
            {
                if (_fileValues.TryGetValue(key, out var v) && v is string s)
                {
                    value = s;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            SwitchToFile(ex);
            lock (_gate)
            {
                if (_fileValues.TryGetValue(key, out var v) && v is string s)
                {
                    value = s;
                    return true;
                }
            }
        }
        return false;
    }

    public void SetString(string key, string value)
    {
        try
        {
            if (!_useFile && _container is not null)
            {
                _container.Values[key] = value;
                return;
            }
            lock (_gate)
            {
                _fileValues[key] = value;
                SaveFile();
            }
        }
        catch (Exception ex)
        {
            SwitchToFile(ex);
            lock (_gate)
            {
                _fileValues[key] = value;
                SaveFile();
            }
        }
    }

    public void Remove(string key)
    {
        try
        {
            if (!_useFile && _container is not null)
            {
                _container.Values.Remove(key);
                return;
            }
            lock (_gate)
            {
                _fileValues.Remove(key);
                SaveFile();
            }
        }
        catch (Exception ex)
        {
            SwitchToFile(ex);
            lock (_gate)
            {
                _fileValues.Remove(key);
                SaveFile();
            }
        }
    }

    private void SwitchToFile(Exception ex)
    {
        if (_useFile) return;
        _useFile = true;
        AppLog.Warning("kv", "Falling back to file store: " + ex.Message);
        LoadFile();
    }

    private void LoadFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (dict is null) return;
            lock (_gate)
            {
                _fileValues.Clear();
                foreach (var (k, el) in dict)
                {
                    _fileValues[k] = el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
                        _ => el.GetRawText()
                    };
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("kv", "Load file: " + ex.Message);
        }
    }

    private void SaveFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            Dictionary<string, object?> snap;
            lock (_gate) snap = new Dictionary<string, object?>(_fileValues);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(snap));
        }
        catch (Exception ex)
        {
            AppLog.Warning("kv", "Save file: " + ex.Message);
        }
    }
}
