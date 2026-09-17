using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Housekeeper.Api;
using Housekeeper.Core;

namespace Housekeeper.Tests;

/// <summary>
/// Which TLS certificate Housekeeper will talk to. Every request to Home Assistant carries an admin token,
/// so "is this really my Home Assistant?" and "may this thing have the keys to my house?" are one question.
/// </summary>
public class CertificateTests
{
    /// <summary>A throwaway self-signed certificate, standing in for the one a home install generated.</summary>
    private static X509Certificate2 SelfSigned(string name = "CN=homeassistant.local")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Fingerprint(X509Certificate certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);

    [Fact]
    public void A_certificate_that_validates_normally_is_always_accepted()
    {
        using var certificate = SelfSigned();

        // No pin, no escape hatch, no complaint from the chain: nothing here narrows ordinary validation.
        Assert.True(Certificates.Accept(certificate, SslPolicyErrors.None, "", false));
        Assert.True(Certificates.Accept(certificate, SslPolicyErrors.None, "not a fingerprint", false));
    }

    [Fact]
    public void An_untrusted_certificate_is_refused_when_nothing_says_otherwise()
    {
        using var certificate = SelfSigned();

        Assert.False(Certificates.Accept(certificate, SslPolicyErrors.RemoteCertificateChainErrors, "", false));
    }

    [Fact]
    public void The_pinned_certificate_is_accepted_and_anything_else_is_not()
    {
        using var mine = SelfSigned();
        using var someone_elses = SelfSigned();

        var pin = Fingerprint(mine);

        Assert.True(Certificates.Accept(mine, SslPolicyErrors.RemoteCertificateChainErrors, pin, false));

        // The whole point: something on the same network with its own self-signed certificate still fails,
        // so the admin token cannot be collected by whatever answered first.
        Assert.False(Certificates.Accept(someone_elses, SslPolicyErrors.RemoteCertificateChainErrors, pin, false));
    }

    [Theory]
    // openssl prints colons, browsers print spaces, some tools lowercase it. None of that should matter.
    [InlineData("{0}")]
    [InlineData("{0}  ")]
    [InlineData("sha256 Fingerprint={0}")]
    public void The_fingerprint_is_matched_however_it_was_punctuated(string format)
    {
        using var certificate = SelfSigned();
        var raw = Fingerprint(certificate);

        var colons = string.Join(':', Enumerable.Range(0, raw.Length / 2).Select(i => raw.Substring(i * 2, 2)));
        var pin = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, colons.ToLowerInvariant());

        Assert.True(Certificates.Accept(certificate, SslPolicyErrors.RemoteCertificateNameMismatch, pin, false));
    }

    /// <summary>
    /// A fingerprint names one exact certificate, so the things a chain check complains about for a
    /// home-made one — unknown issuer, wrong hostname — are all beside the point once it matches.
    /// </summary>
    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)]
    public void A_pin_covers_the_complaints_a_home_made_certificate_attracts(SslPolicyErrors errors)
    {
        using var certificate = SelfSigned();

        Assert.True(Certificates.Accept(certificate, errors, Fingerprint(certificate), false));
    }

    [Fact]
    public void Nothing_presented_is_never_acceptable_on_a_pin()
    {
        using var certificate = SelfSigned();

        // There is no certificate to have pinned, so a pin cannot rescue it.
        Assert.False(Certificates.Accept(null, SslPolicyErrors.RemoteCertificateNotAvailable, Fingerprint(certificate), false));
        Assert.False(Certificates.Accept(certificate, SslPolicyErrors.RemoteCertificateNotAvailable, Fingerprint(certificate), false));

        // Turning checking off entirely still does, which is exactly why it is the discouraged option.
        Assert.True(Certificates.Accept(null, SslPolicyErrors.RemoteCertificateNotAvailable, "", true));
    }

    [Fact]
    public void Accepting_anything_accepts_anything()
    {
        using var someone_elses = SelfSigned();

        Assert.True(Certificates.Accept(someone_elses, SslPolicyErrors.RemoteCertificateChainErrors, "", true));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("nonsense", false)]
    [InlineData("AB:CD", false)]
    [InlineData("AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899", true)]
    [InlineData("aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99", true)]
    public void A_fingerprint_has_to_look_like_one(string fingerprint, bool wellFormed) =>
        Assert.Equal(wellFormed, Fingerprints.IsWellFormed(fingerprint));

    [Fact]
    public void A_malformed_fingerprint_is_a_configuration_error_and_accepting_anything_is_a_warning()
    {
        var options = new HousekeeperOptions();
        options.HomeAssistant.CertificateFingerprint = "AB:CD";

        Assert.Contains(options.Validate().Errors, error => error.Contains("CertificateFingerprint", StringComparison.Ordinal));

        options.HomeAssistant.CertificateFingerprint = "";
        options.HomeAssistant.AcceptAnyCertificate = true;

        var validation = options.Validate();

        // Discouraged, but the user's call: it must not stop the application from starting.
        Assert.True(validation.IsValid);
        Assert.Contains(validation.Warnings, warning => warning.Contains("admin token", StringComparison.Ordinal));
    }
}
