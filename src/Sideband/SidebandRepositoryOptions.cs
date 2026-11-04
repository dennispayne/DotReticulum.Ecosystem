using System.Net;

namespace Sideband;

/// <summary>Explicitly enabled, public HTTPS file sharing. This is not a Git or Reticulum repository.</summary>
public sealed record SidebandRepositoryOptions
{
    public required string RootDirectory { get; init; }
    public required string CertificateDirectory { get; init; }
    public bool AllowPublicUnauthenticatedAccess { get; init; }
    public IPAddress Address { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = 4444;
    public int MaximumConnections { get; init; } = 8;
    public int MaximumHeaderBytes { get; init; } = 8192;
    public long MaximumFileBytes { get; init; } = 256L * 1024 * 1024;
    public int MaximumPackageEntries { get; init; } = 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
