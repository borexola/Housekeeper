using System.Text.Json;
using System.Text.Json.Nodes;

namespace Housekeeper.Api;

/// <summary>Where a secret came from, so the settings screen can say so without revealing the value.</summary>
public enum SecretSource
{
    /// <summary>Not set anywhere.</summary>
    None = 0,
    /// <summary>Typed into the settings UI and stored in the data directory.</summary>
    Stored = 1,
    /// <summary>Supplied by an environment variable, or the Docker <c>{NAME}_FILE</c> convention.</summary>
    Environment = 2,
}

/// <summary>Where a secret is read from. The store is the real one; testing unsaved values lays typed ones over it.</summary>
public interface ISecretSource
{
    string? Resolve(string name, string? environmentVariableName);
}

/// <summary>
/// The three secrets Housekeeper holds. A value typed into the settings UI is written to a file of its own
/// in the data directory, restricted to the owner where the platform supports it, and takes precedence over
/// the environment so a correction made in the UI actually applies. Values are never read back out over HTTP.
/// </summary>
public sealed class SecretStore(string path, ILogger<SecretStore> logger) : ISecretSource
{
    public const string HomeAssistantToken = "HomeAssistantToken";
    public const string ApiToken = "ApiToken";
    public const string LlmApiKey = "LlmApiKey";

    private readonly Lock _gate = new();
    private JsonObject? _cache;

    /// <summary>The value in force: what the UI stored, else the environment variable (or its <c>_FILE</c> form).</summary>
    public string? Resolve(string name, string? environmentVariableName)
    {
        var stored = Stored(name);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;

        return FromEnvironment(environmentVariableName);
    }

    /// <summary>
    /// What this secret would fall back to if the stored value were cleared. Asked before allowing a clear,
    /// so the answer has to honour the <c>{NAME}_FILE</c> convention the same way <see cref="Resolve"/> does
    /// -- reading the plain environment variable alone refused a clear that was perfectly safe.
    /// </summary>
    public string? WithoutStored(string? environmentVariableName) => FromEnvironment(environmentVariableName);

    public SecretSource SourceOf(string name, string? environmentVariableName)
    {
        if (!string.IsNullOrWhiteSpace(Stored(name))) return SecretSource.Stored;
        return string.IsNullOrWhiteSpace(FromEnvironment(environmentVariableName)) ? SecretSource.None : SecretSource.Environment;
    }

    /// <summary>Stores or, when given nothing, clears a secret. Returns true when the stored value changed.</summary>
    public bool Set(string name, string? value)
    {
        lock (_gate)
        {
            var trimmed = value?.Trim();
            var current = Load();

            // Changed on a copy, and the copy only becomes the cache once it is safely on disk. Editing the
            // cached document in place meant a write that failed -- a full disk, a read-only volume -- still
            // took effect for this process, so the UI reported an error while the secret was already gone.
            var document = (JsonObject)current.DeepClone();

            if (string.IsNullOrEmpty(trimmed))
            {
                if (!document.Remove(name)) return false;
            }
            else
            {
                if (current[name]?.GetValue<string>() == trimmed) return false;
                document[name] = trimmed;
            }

            Write(document);
            _cache = document;
            return true;
        }
    }

    private string? Stored(string name)
    {
        lock (_gate)
        {
            var value = Load()[name];
            return value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        }
    }

    private static string? FromEnvironment(string? environmentVariableName)
    {
        if (string.IsNullOrWhiteSpace(environmentVariableName)) return null;

        // Docker's convention: HOUSEKEEPER_HA_TOKEN_FILE points at a mounted secret.
        var filePath = Environment.GetEnvironmentVariable(environmentVariableName + "_FILE");
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try
            {
                var fromFile = File.ReadAllText(filePath).Trim();
                if (fromFile.Length > 0) return fromFile;
            }
            catch (IOException)
            {
                // Unreadable secret file: fall through to the plain variable. Never log the path or contents.
            }
        }

        var value = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
            // Never log the contents. A damaged file behaves as "nothing stored" so the environment still works.
            logger.LogWarning("The stored secrets file could not be read; falling back to environment variables.");
        }

        return _cache = [];
    }

    private void Write(JsonObject document)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Restrict(temporary);
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // The half-written file is not the state of anything and must not be left lying about next to the
            // real one -- particularly here, where it holds the same secrets.
            try { File.Delete(temporary); } catch (IOException) { /* best effort */ }
            throw;
        }
        Restrict(path);
    }

    /// <summary>Owner-only where the platform has such a concept; on Windows the directory ACL is the control.</summary>
    private void Restrict(string file)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogWarning("Could not restrict permissions on the secrets file; check the data directory is not world-readable.");
        }
    }
}
