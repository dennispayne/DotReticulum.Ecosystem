using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Sideband;

internal static class SidebandRepositoryCertificate
{
    internal static X509Certificate2 Open(string directory)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SidebandRepositoryServer.RequirePlainPath(directory);
        var path = Path.Combine(directory, "repository-certificate.pfx");
        SidebandRepositoryServer.RequirePlainPath(path);
        if (!File.Exists(path))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Sideband Repository,O=Sideband,L=Earth,ST=None,C=NA",
                key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(System.Net.IPAddress.Loopback);
            names.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            var now = DateTimeOffset.UtcNow;
            using var created = request.CreateSelfSigned(now.AddDays(-14), now.AddDays(3652));
            var encoded = created.Export(X509ContentType.Pfx, "");
            var staging = Path.Combine(directory, $".repository-certificate-{Guid.NewGuid():N}.part");
            try
            {
                var fileOptions = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(staging, fileOptions))
                {
                    file.Write(encoded);
                    file.Flush(flushToDisk: true);
                }

                try { File.Move(staging, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
                if (File.Exists(staging)) File.Delete(staging);
            }
        }

        SidebandRepositoryServer.RequirePlainPath(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Repository certificate file exceeds its limit.");
        var bytes = File.ReadAllBytes(path);
        try
        {
            // Windows and macOS use temporary key containers; macOS does not support EphemeralKeySet.
            var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet :
                OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            var certificate = X509CertificateLoader.LoadPkcs12(bytes, "", flags);
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow ||
                certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
            {
                certificate.Dispose();
                throw new InvalidDataException("Repository certificate must have a private key and be currently valid.");
            }

            return certificate;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
