namespace Housekeeper.Api;

/// <summary>
/// The settings the Home Assistant add-on owns rather than the user.
///
/// Almost everything in Housekeeper is configurable from its own settings page, deliberately. These three
/// are not, and only when running as an add-on: the Supervisor opens ingress on the port
/// <c>housekeeper/config.yaml</c> declares and reaches the container on a fixed address, and the database
/// lives on the add-on's own volume. A stored override of any of them leaves the Supervisor knocking at a
/// port nothing is listening on — and the settings page that could put it right is behind that same ingress,
/// so there is no way back in short of deleting the file from a terminal add-on.
///
/// Outside the add-on none of this applies: run it in Docker or from the command line and every one of these
/// is an ordinary setting.
/// </summary>
public static class Managed
{
    /// <summary>Set by the add-on's run.sh. Nothing else sets it, and nothing reads it but this.</summary>
    public const string Marker = "HOUSEKEEPER_MANAGED";

    public static bool IsAddOn =>
        string.Equals(Environment.GetEnvironmentVariable(Marker), "addon", StringComparison.OrdinalIgnoreCase);

    /// <summary>The keys the add-on pins. Read the same way whether they are being applied or refused.</summary>
    public static readonly string[] Keys =
    [
        "Housekeeper:Api:BindAddress",
        "Housekeeper:Api:Port",
        "Housekeeper:Api:IngressAddress",
        "Housekeeper:Storage:Path",
    ];

    public static bool IsPinned(string key) =>
        IsAddOn && Keys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The pinned values as the environment set them, ready to be layered over anything stored. Read from the
    /// configuration built so far rather than from the environment directly, so a value the add-on did not
    /// set simply is not pinned instead of being pinned to nothing.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string?>> PinnedFrom(IConfiguration configuration) =>
        Keys
            .Select(key => new KeyValuePair<string, string?>(key, configuration[key]))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToList();
}
