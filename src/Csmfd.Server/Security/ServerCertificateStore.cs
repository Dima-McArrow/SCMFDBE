using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Csmfd.Server.Security;

/// <summary>
/// Persistent self-signed TLS identity (spec 41.5 items 1 and 8). Created once, kept across restarts,
/// stored as PKCS#12 encrypted with DPAPI for the current user only. The iPad pins the SHA-256
/// fingerprint of the DER certificate, so the certificate must not change silently.
/// </summary>
internal static class ServerCertificateStore
{
    // Apple's TLS server certificate rules: ECDSA P-256 + SHA-256, serverAuth EKU, DNS SAN, at most 825 days.
    private const int ValidityDays = 820;
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    private static readonly byte[] Entropy = "CSMFD.Server.Certificate.v1"u8.ToArray();

    public static X509Certificate2 LoadOrCreate(string path, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        if (File.Exists(path))
        {
            var existing = TryLoad(path);
            if (existing is not null && existing.NotAfter.ToUniversalTime() - time.GetUtcNow().UtcDateTime > RenewBefore)
            {
                return existing;
            }

            existing?.Dispose();
        }

        var pfx = Create(time.GetUtcNow());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, ProtectedData.Protect(pfx, Entropy, DataProtectionScope.CurrentUser));
        File.Move(temp, path, overwrite: true);
        return Load(pfx);
    }

    /// <summary>SHA-256 over the DER certificate as 64 uppercase hex characters.</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>Fingerprint grouped as "AB12 CD34 ..." for reading aloud / comparing on screen.</summary>
    public static string FormatFingerprint(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return string.Join(' ', fingerprint.Chunk(4).Select(chunk => new string(chunk)));
    }

    private static X509Certificate2? TryLoad(string path)
    {
        try
        {
            var pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var certificate = Load(pfx);
            if (certificate.HasPrivateKey)
            {
                return certificate;
            }

            certificate.Dispose();
            return null;
        }
        catch (CryptographicException)
        {
            // Corrupted or protected for another user: a new identity requires re-pairing.
            return null;
        }
    }

    private static X509Certificate2 Load(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.UserKeySet);

    private static byte[] Create(DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=CSMFD Server", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Environment.MachineName.ToLowerInvariant());
        names.AddDnsName("csmfd.local");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(ValidityDays));
        return certificate.Export(X509ContentType.Pkcs12);
    }
}
