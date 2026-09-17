namespace Housekeeper.Core;

/// <summary>
/// Reading a certificate fingerprint the way a person pasted it.
///
/// Lives in Core because Core validates the setting and the API layer compares against a real certificate,
/// and those two must agree exactly — a value the settings page accepted but the comparison could never
/// match would present as "your certificate is wrong" with nothing to see.
/// </summary>
public static class Fingerprints
{
    /// <summary>
    /// The hex digits, upper-cased, from however it was pasted.
    ///
    /// Every tool prints these differently — <c>openssl</c> gives <c>sha256 Fingerprint=AB:CD:…</c>, a
    /// browser gives space-separated pairs, some use lowercase — so a leading label and the usual separators
    /// come off. Everything else is left in place deliberately: discarding any character that is not a hex
    /// digit would quietly turn a mistyped value into a well-formed one that can never match.
    /// </summary>
    public static string Normalise(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return "";

        var value = fingerprint[(fingerprint.LastIndexOf('=') + 1)..];

        return new string([.. value
            .Where(ch => ch is not (':' or ' ' or '-' or '\t'))
            .Select(char.ToUpperInvariant)]);
    }

    /// <summary>A SHA-256 fingerprint is 64 hex characters, whatever punctuation came with it.</summary>
    public static bool IsWellFormed(string? fingerprint)
    {
        var normalised = Normalise(fingerprint);
        return normalised.Length == 64 && normalised.All(char.IsAsciiHexDigit);
    }
}
