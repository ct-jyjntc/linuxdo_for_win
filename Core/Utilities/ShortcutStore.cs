using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxDo.Core.Services;
using Windows.System;

namespace LinuxDo.Core.Utilities;

/// <summary>User-configurable app shortcuts (persisted as key + modifier flags).</summary>
public sealed class ShortcutKey : IEquatable<ShortcutKey>
{
    public string Key { get; set; } = "n";
    public uint ModifiersRaw { get; set; }

    public const uint ModControl = 1 << 0;
    public const uint ModShift = 1 << 1;
    public const uint ModAlt = 1 << 2;
    public const uint ModWin = 1 << 3;

    public ShortcutKey() { }

    public ShortcutKey(string key, uint modifiers)
    {
        Key = key;
        ModifiersRaw = modifiers;
    }

    public static ShortcutKey Ctrl(string key) => new(key, ModControl);
    public static ShortcutKey CtrlShift(string key) => new(key, ModControl | ModShift);

    public bool HasControl => (ModifiersRaw & ModControl) != 0;
    public bool HasShift => (ModifiersRaw & ModShift) != 0;
    public bool HasAlt => (ModifiersRaw & ModAlt) != 0;
    public bool HasWin => (ModifiersRaw & ModWin) != 0;

    public string Display
    {
        get
        {
            var parts = new List<string>();
            if (HasControl) parts.Add("Ctrl");
            if (HasAlt) parts.Add("Alt");
            if (HasShift) parts.Add("Shift");
            if (HasWin) parts.Add("Win");
            parts.Add(KeyLabel);
            return string.Join("+", parts);
        }
    }

    public string KeyLabel => Key switch
    {
        "\r" or "\n" => "Enter",
        " " => "Space",
        "\u001b" => "Esc",
        _ when Key.Length == 1 => Key.ToUpperInvariant(),
        _ => Key
    };

    public bool Matches(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        if (HasControl != ctrl || HasShift != shift || HasAlt != alt) return false;
        return KeyToVirtualKey(Key) == key;
    }

    public static VirtualKey KeyToVirtualKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return VirtualKey.None;
        return key switch
        {
            "\r" or "\n" => VirtualKey.Enter,
            " " => VirtualKey.Space,
            "\u001b" => VirtualKey.Escape,
            "1" => VirtualKey.Number1,
            "2" => VirtualKey.Number2,
            "3" => VirtualKey.Number3,
            "4" => VirtualKey.Number4,
            "5" => VirtualKey.Number5,
            "6" => VirtualKey.Number6,
            "7" => VirtualKey.Number7,
            "8" => VirtualKey.Number8,
            "9" => VirtualKey.Number9,
            "0" => VirtualKey.Number0,
            _ when key.Length == 1 && char.IsLetter(key[0]) =>
                (VirtualKey)(VirtualKey.A + (char.ToUpperInvariant(key[0]) - 'A')),
            _ when Enum.TryParse<VirtualKey>(key, true, out var vk) => vk,
            _ => VirtualKey.None
        };
    }

    public static string VirtualKeyToKey(VirtualKey key) => key switch
    {
        VirtualKey.Enter => "\r",
        VirtualKey.Space => " ",
        VirtualKey.Escape => "\u001b",
        VirtualKey.Number0 => "0",
        VirtualKey.Number1 => "1",
        VirtualKey.Number2 => "2",
        VirtualKey.Number3 => "3",
        VirtualKey.Number4 => "4",
        VirtualKey.Number5 => "5",
        VirtualKey.Number6 => "6",
        VirtualKey.Number7 => "7",
        VirtualKey.Number8 => "8",
        VirtualKey.Number9 => "9",
        >= VirtualKey.A and <= VirtualKey.Z => ((char)('a' + (key - VirtualKey.A))).ToString(),
        _ => key.ToString().ToLowerInvariant()
    };

    public bool Equals(ShortcutKey? other)
        => other is not null && Key == other.Key && ModifiersRaw == other.ModifiersRaw;

    public override bool Equals(object? obj) => obj is ShortcutKey k && Equals(k);
    public override int GetHashCode() => HashCode.Combine(Key, ModifiersRaw);
}

public sealed class ShortcutBindingItem
{
    public AppShortcutAction Action { get; init; }
    public string Title => Action.Title();
    public string Display { get; set; } = "";
    public AppShortcutAction ActionId => Action;
}

public sealed class ShortcutStore
{
    public static ShortcutStore Current { get; } = new();

    private const string StorageKey = "shortcuts.v1";
    private readonly Dictionary<AppShortcutAction, ShortcutKey> _bindings = new();

    private ShortcutStore()
    {
        Load();
    }

    public IReadOnlyDictionary<AppShortcutAction, ShortcutKey> Bindings => _bindings;

    public ShortcutKey Key(AppShortcutAction action)
        => _bindings.TryGetValue(action, out var k) ? k : DefaultKey(action);

    public void SetKey(AppShortcutAction action, ShortcutKey key)
    {
        _bindings[action] = key;
        Persist();
    }

    public void Reset(AppShortcutAction action)
    {
        _bindings[action] = DefaultKey(action);
        Persist();
    }

    public void ResetAll()
    {
        foreach (var a in Enum.GetValues<AppShortcutAction>())
            _bindings[a] = DefaultKey(a);
        Persist();
    }

    public AppShortcutAction? Match(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        foreach (var (action, binding) in _bindings)
        {
            if (binding.Matches(key, ctrl, shift, alt))
                return action;
        }
        return null;
    }

    public List<ShortcutBindingItem> AllItems()
        => Enum.GetValues<AppShortcutAction>()
            .Select(a => new ShortcutBindingItem
            {
                Action = a,
                Display = Key(a).Display
            })
            .ToList();

    public static ShortcutKey DefaultKey(AppShortcutAction a) => a switch
    {
        AppShortcutAction.NewTopic => ShortcutKey.Ctrl("n"),
        AppShortcutAction.NewMessage => ShortcutKey.CtrlShift("n"),
        AppShortcutAction.Search => ShortcutKey.Ctrl("f"),
        AppShortcutAction.Refresh => ShortcutKey.Ctrl("r"),
        AppShortcutAction.GoLatest => ShortcutKey.Ctrl("1"),
        AppShortcutAction.GoTop => ShortcutKey.Ctrl("2"),
        AppShortcutAction.GoUnread => ShortcutKey.Ctrl("3"),
        AppShortcutAction.GoNotifications => ShortcutKey.Ctrl("4"),
        AppShortcutAction.GoMessages => ShortcutKey.Ctrl("5"),
        AppShortcutAction.GoReadLater => ShortcutKey.Ctrl("6"),
        AppShortcutAction.GoHistory => ShortcutKey.Ctrl("7"),
        AppShortcutAction.ListNext => ShortcutKey.Ctrl("j"),
        AppShortcutAction.ListPrev => ShortcutKey.Ctrl("k"),
        AppShortcutAction.ListOpen => ShortcutKey.Ctrl("\r"),
        _ => ShortcutKey.Ctrl("?")
    };

    private void Load()
    {
        foreach (var a in Enum.GetValues<AppShortcutAction>())
            _bindings[a] = DefaultKey(a);

        try
        {
            var path = StoragePath();
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, ShortcutKeyDto>>(json);
            if (dict is null) return;
            foreach (var a in Enum.GetValues<AppShortcutAction>())
            {
                if (dict.TryGetValue(a.ToString(), out var dto) && !string.IsNullOrEmpty(dto.Key))
                    _bindings[a] = new ShortcutKey(dto.Key, dto.ModifiersRaw);
            }
        }
        catch
        {
            // keep defaults
        }
    }

    private void Persist()
    {
        try
        {
            var dict = _bindings.ToDictionary(
                kv => kv.Key.ToString(),
                kv => new ShortcutKeyDto { Key = kv.Value.Key, ModifiersRaw = kv.Value.ModifiersRaw });
            var dir = Path.GetDirectoryName(StoragePath())!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StoragePath(), JsonSerializer.Serialize(dict));
        }
        catch
        {
            // ignore
        }
    }

    private static string StoragePath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinuxDo");
        return Path.Combine(root, "shortcuts.json");
    }

    private sealed class ShortcutKeyDto
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("modifiersRaw")] public uint ModifiersRaw { get; set; }
    }
}
