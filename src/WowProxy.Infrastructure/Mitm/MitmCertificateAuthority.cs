using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WowProxy.Infrastructure.Mitm;

/// <summary>
/// Manages a self-signed root CA and generates per-domain TLS certificates for MITM interception.
/// The root CA is persisted to disk so the user only needs to trust it once.
/// </summary>
public sealed class MitmCertificateAuthority : IDisposable
{
    private readonly string _caDir;
    private X509Certificate2? _rootCa;
    private readonly ConcurrentDictionary<string, X509Certificate2> _certCache = new(StringComparer.OrdinalIgnoreCase);

    public MitmCertificateAuthority()
    {
        _caDir = Path.Combine(AppDataPaths.GetAppRoot(), "mitm-ca");
        Directory.CreateDirectory(_caDir);
    }

    public string CaCertPath => Path.Combine(_caDir, "wowproxy-ca.crt");
    private string CaPfxPath => Path.Combine(_caDir, "wowproxy-ca.pfx");

    /// <summary>Get or create the root CA certificate.</summary>
    public X509Certificate2 GetOrCreateRootCa()
    {
        if (_rootCa != null) return _rootCa;

        if (File.Exists(CaPfxPath))
        {
            try
            {
                _rootCa = new X509Certificate2(CaPfxPath, "wowproxy", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                return _rootCa;
            }
            catch { /* regenerate */ }
        }

        _rootCa = GenerateRootCa();
        File.WriteAllBytes(CaPfxPath, _rootCa.Export(X509ContentType.Pfx, "wowproxy"));
        File.WriteAllText(CaCertPath, _rootCa.ExportCertificatePem());
        return _rootCa;
    }

    /// <summary>Get or create a per-domain certificate signed by our root CA.</summary>
    public X509Certificate2 GetOrCreateDomainCert(string hostname)
    {
        if (_certCache.TryGetValue(hostname, out var cached)) return cached;

        var rootCa = GetOrCreateRootCa();
        var cert = GenerateDomainCert(hostname, rootCa);
        _certCache[hostname] = cert;
        return cert;
    }

    /// <summary>Install the root CA into the current user's Trusted Root store (requires user consent).</summary>
    public bool TryInstallRootCa()
    {
        try
        {
            var ca = GetOrCreateRootCa();
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            // Check if already installed
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false);
            if (existing.Count > 0) return true;

            store.Add(ca);
            store.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsRootCaInstalled()
    {
        try
        {
            var ca = GetOrCreateRootCa();
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false);
            return found.Count > 0;
        }
        catch { return false; }
    }

    private static X509Certificate2 GenerateRootCa()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=WowProxy MITM CA, O=WowProxy, OU=Dev",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(10);
        var cert = req.CreateSelfSigned(notBefore, notAfter);

        // On Windows we need to persist the key
        return new X509Certificate2(cert.Export(X509ContentType.Pfx, "wowproxy"), "wowproxy",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private static X509Certificate2 GenerateDomainCert(string hostname, X509Certificate2 caCert)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth

        // SAN
        var sanBuilder = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(hostname, out var ip))
            sanBuilder.AddIpAddress(ip);
        else
            sanBuilder.AddDnsName(hostname);
        req.CertificateExtensions.Add(sanBuilder.Build());

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F; // positive

        using var caKey = caCert.GetRSAPrivateKey()!;
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddDays(365);

        var cert = req.Create(caCert, notBefore, notAfter, serial);
        var certWithKey = cert.CopyWithPrivateKey(rsa);

        return new X509Certificate2(certWithKey.Export(X509ContentType.Pfx, "tmp"), "tmp",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    public void Dispose()
    {
        _rootCa?.Dispose();
        foreach (var cert in _certCache.Values)
            cert.Dispose();
        _certCache.Clear();
    }
}
