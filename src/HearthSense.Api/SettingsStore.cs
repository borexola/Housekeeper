using System.Text.Json;
using System.Text.Json.Nodes;
using HearthSense.Core;
using Microsoft.Extensions.Primitives;

namespace HearthSense.Api;

/// <summary>
/// The settings the user changed in the UI, as a JSON document shaped like the <c>HearthSense</c> section.
/// It is layered above every other configuration source, so what you set on the settings screen is what runs.
/// Only the keys actually changed are stored; everything else keeps inheriting from appsettings, the optional
/// config file, or the environment, and can be put back with a reset.
/// </summary>
public sealed class SettingsStore(string path, ILogger<SettingsStore> logger)
{
    private readonly Lock _gate = new();
    private JsonObject? _cache;

    public string Path => path;

    /// <summary>The stored overrides. Callers must not mutate the result; use <see cref="Apply"/>.</summary>
    public JsonObject Overrides
    {
        get { lock (_gate) return Load(); }
    }

    public bool Has(string key)
    {
        lock (_gate) return Find(Load(), key) is not null;
    }

    public JsonNode? Get(string key)
    {
        lock (_gate) return Find(Load(), key)?.DeepClone();
    }

    /// <summary>
    /// Applies a batch of changes and persists them. A null value removes the override so the key inherits
    /// again. Returns the keys whose stored value actually changed.
    /// </summary>
    public IReadOnlyList<string> Apply(IReadOnlyDictionary<string, JsonNode?> changes)
    {
        lock (_gate)
        {
            var document = (JsonObject)Load().DeepClone();
            List<string> changed = [];

            foreach (var (key, value) in changes)
            {
                var before = Find(document, key);
                if (Equal(before, value)) continue;

                if (value is null) Remove(document, key);
                else Set(document, key, value.DeepClone());

                changed.Add(key);
            }

            if (changed.Count > 0)
            {
                Write(document);
                _cache = document;
            }

            return changed;
        }
    }

    /// <summary>Drops every override under a section, or all of them when given nothing.</summary>
    public int Reset(string? section)
    {
        lock (_gate)
        {
            var document = (JsonObject)Load().DeepClone();
            if (document[HearthSenseOptions.SectionName] is not JsonObject root) return 0;

            int removed;
            if (string.IsNullOrWhiteSpace(section))
            {
                removed = Count(root);
                document.Remove(HearthSenseOptions.SectionName);
            }
            else
            {
                if (root[section] is not JsonObject branch) return 0;
                removed = Count(branch);
                root.Remove(section);
                if (root.Count == 0) document.Remove(HearthSenseOptions.SectionName);
            }

            if (removed > 0)
            {
                Write(document);
                _cache = document;
            }

            return removed;
        }
    }

    private static int Count(JsonObject node) =>
        node.Sum(pair => pair.Value is JsonObject child ? Count(child) : 1);

    private static bool Equal(JsonNode? left, JsonNode? right) =>
        (left is null && right is null) ||
        (left is not null && right is not null && JsonNode.DeepEquals(left, right));

    private static JsonNode? Find(JsonObject root, string key)
    {
        JsonNode? node = root;
        foreach (var segment in key.Split(':'))
        {
            if (node is not JsonObject branch || !branch.TryGetPropertyValue(segment, out node)) return null;
        }

        return node;
    }

    private static void Set(JsonObject root, string key, JsonNode value)
    {
        var segments = key.Split(':');
        var branch = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (branch[segments[i]] is not JsonObject next)
            {
                next = [];
                branch[segments[i]] = next;
            }

            branch = next;
        }

        branch[segments[^1]] = value;
    }

    private static void Remove(JsonObject root, string key)
    {
        var segments = key.Split(':');
        List<JsonObject> trail = [root];

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (trail[^1][segments[i]] is not JsonObject next) return;
            trail.Add(next);
        }

        trail[^1].Remove(segments[^1]);

        // An override document should not accumulate empty branches.
        for (var i = trail.Count - 1; i > 0; i--)
        {
            if (trail[i].Count > 0) break;
            trail[i - 1].Remove(segments[i - 1]);
        }
    }

    private JsonObject Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject parsed)
                return _cache = parsed;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogWarning(ex, "The settings override file at {Path} could not be read; using inherited configuration.", path);
        }

        return _cache = [];
    }

    private void Write(JsonObject document)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Binds the options and keeps the bound instance current as configuration reloads.</summary>
public sealed class SettingsProvider : ISettingsProvider, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly SettingsStore _store;
    private readonly IDisposable? _subscription;
    private volatile HearthSenseOptions _current;

    public SettingsProvider(IConfiguration configuration, SettingsStore store)
    {
        _configuration = configuration;
        _store = store;
        _current = SettingsBinder.Bind(configuration, store.Overrides);

        if (configuration is IConfigurationRoot root)
            _subscription = ChangeToken.OnChange(root.GetReloadToken, Reload);
    }

    public HearthSenseOptions Current => _current;

    public void Reload() => _current = SettingsBinder.Bind(_configuration, _store.Overrides);

    public void Dispose() => _subscription?.Dispose();
}

/// <summary>Turns configuration into options, with the one correction the standard binder cannot make.</summary>
public static class SettingsBinder
{
    /// <summary>
    /// Lists are merged by index across configuration sources, so overriding four inherited globs with two
    /// would leave the last two in place. These are replaced wholesale from the override document instead.
    /// </summary>
    private static readonly (string Key, Action<HearthSenseOptions, List<string>> Set)[] Lists =
    [
        ("HearthSense:Scan:Include", (options, values) => options.Scan.Include = values),
        ("HearthSense:Scan:Exclude", (options, values) => options.Scan.Exclude = values),
    ];

    public static HearthSenseOptions Bind(IConfiguration configuration, JsonObject? overrides)
    {
        var options = new HearthSenseOptions();
        configuration.GetSection(HearthSenseOptions.SectionName).Bind(options);

        foreach (var (key, set) in Lists)
        {
            if (overrides is not null && TryArray(overrides, key, out var values)) set(options, values);
        }

        options.Scan.Include = Clean(options.Scan.Include);
        options.Scan.Exclude = Clean(options.Scan.Exclude);

        return options;
    }

    private static List<string> Clean(List<string> values) =>
        [.. values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())];

    private static bool TryArray(JsonObject root, string key, out List<string> values)
    {
        values = [];

        JsonNode? node = root;
        foreach (var segment in key.Split(':'))
        {
            if (node is not JsonObject branch || !branch.TryGetPropertyValue(segment, out node)) return false;
        }

        if (node is not JsonArray array) return false;

        foreach (var item in array)
            if (item?.GetValueKind() == JsonValueKind.String)
                values.Add(item.GetValue<string>());

        return true;
    }
}
