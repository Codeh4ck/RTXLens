using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RTXLens.Service.Mobile;

internal sealed class MobileAccessCertificate : IDisposable
{
    private readonly Lock _gate = new();
    private readonly ECDsa _key;
    private readonly TimeProvider _timeProvider;
    private readonly List<X509Certificate2> _certificates = [];
    private DateTimeOffset _renewAfter;

    public MobileAccessCertificate(byte[] privateKey, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _key = ECDsa.Create();

        try
        {
            _key.ImportPkcs8PrivateKey(privateKey, out _);
            Fingerprint = Convert.ToHexString(SHA256.HashData(_key.ExportSubjectPublicKeyInfo()));
        }
        catch
        {
            _key.Dispose();
            throw;
        }
    }

    public string Fingerprint { get; }

    public X509Certificate2 GetCertificate()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_certificates.Count != 0 && now < _renewAfter)
            {
                return _certificates[^1];
            }

            var request = new CertificateRequest("CN=RTX Lens Mobile Access", _key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));

            var expiration = now.AddYears(1);
            var certificate = CreateTlsCertificate(request, now.AddMinutes(-5), expiration);

            _certificates.Add(certificate);
            _renewAfter = expiration.AddDays(-30);

            return certificate;
        }
    }

    private static X509Certificate2 CreateTlsCertificate(CertificateRequest request, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var certificate = request.CreateSelfSigned(notBefore, notAfter);
        if (!OperatingSystem.IsWindows())
        {
            return certificate;
        }

        using (certificate)
        {
            // Schannel needs a key it can open from the user key store during the TLS handshake.
            var pfx = certificate.Export(X509ContentType.Pkcs12);
            try
            {
                return X509CertificateLoader.LoadPkcs12(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var certificate in _certificates)
            {
                certificate.Dispose();
            }

            _key.Dispose();
        }
    }
}