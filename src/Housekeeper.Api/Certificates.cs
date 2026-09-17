using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Housekeeper.Core;

namespace Housekeeper.Api;

/// <summary>
/// Decides whether to accept the TLS certificate Home Assistant presented.
///
/// This matters more here than it would in most applications, because every request carries an admin token.
/// Whatever answers at the configured address receives it, so "is this really my Home Assistant?" and "may
/// this thing have the keys to my house?" are the same question.
///
/// A self-signed certificate is completely ordinary for a home install, so refusing outright would be
/// unhelpful. The answer is to let the user name the certificate they mean, by fingerprint, rather than to
/// stop checking: a machine-in-the-middle on the same network presenting its own self-signed certificate is
/// then still refused, which is the whole point of checking at all.
/// </summary>
public static class Certificates
{
    /// <summary>
    /// Whether the certificate is acceptable, given what the user configured.
    ///
    /// A certificate that validates normally is always accepted; the settings below only ever widen this,
    /// never narrow it, so turning on a pin cannot break a properly issued certificate.
    /// </summary>
    public static bool Accept(
        X509Certificate? certificate,
        SslPolicyErrors errors,
        string? pinnedFingerprint,
        bool acceptAny)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (acceptAny) return true;

        // Nothing was presented, so there is nothing to have pinned.
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) return false;

        return Matches(certificate, pinnedFingerprint);
    }

    /// <summary>
    /// Whether this is the exact certificate the user nominated.
    ///
    /// Only the fingerprint is compared, so an expired or wrongly-named certificate still matches. That is
    /// deliberate: naming a certificate by its SHA-256 hash identifies it precisely, and the things a chain
    /// check would otherwise complain about — no recognised issuer, a hostname that does not match, an
    /// expiry nobody renews — are all normal for the self-signed certificate someone generated once for
    /// their own house. What it cannot be is a different certificate.
    /// </summary>
    public static bool Matches(X509Certificate certificate, string? fingerprint)
    {
        var wanted = Fingerprints.Normalise(fingerprint);
        if (wanted.Length == 0) return false;

        return string.Equals(
            Fingerprints.Normalise(certificate.GetCertHashString(HashAlgorithmName.SHA256)),
            wanted,
            StringComparison.Ordinal);
    }

}
