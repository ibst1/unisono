using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Unisono;

/// <summary>A server certificate that failed validation, described well enough for a person to decide whether to trust it.</summary>
public sealed record CertificateInfo(string Address, string Subject, string Issuer, DateTime NotAfter, string Sha256, string Problem)
{
    public static CertificateInfo From(string address, X509Certificate2 cert, X509Chain? chain, SslPolicyErrors errors)
    {
        var problems = new List<string>();
        var now = DateTime.Now;
        if (cert.NotAfter < now) problems.Add($"expired on {cert.NotAfter:yyyy-MM-dd}");
        else if (cert.NotBefore > now) problems.Add($"not valid before {cert.NotBefore:yyyy-MM-dd}");
        if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0 && cert.NotAfter >= now)
        {
            var selfSigned = cert.Subject == cert.Issuer
                             || chain?.ChainStatus.Any(s => s.Status is X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain) == true;
            problems.Add(selfSigned ? "not issued by a trusted authority (DSM's own self-signed certificate)" : "chain not trusted");
        }
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) problems.Add("issued for a different name");
        return new CertificateInfo(address, cert.Subject, cert.Issuer, cert.NotAfter, cert.GetCertHashString(HashAlgorithmName.SHA256),
            problems.Count == 0 ? errors.ToString() : string.Join(", ", problems));
    }

    /// <summary>SHA-256 in groups of four, easier to compare with what DSM shows.</summary>
    public string FingerprintForDisplay =>
        string.Join(" ", Enumerable.Range(0, Sha256.Length / 4).Select(i => Sha256.Substring(i * 4, 4)));

    public string Describe() =>
        $"{Address}\n  {Subject}\n  issued by {Issuer}\n  problem: {Problem}\n  SHA-256: {FingerprintForDisplay}";
}

/// <summary>The NAS answered, but only with certificates that are not trusted (and not pinned for this login).</summary>
public sealed class UntrustedCertificateException : Exception
{
    public IReadOnlyList<CertificateInfo> Certificates { get; }

    public UntrustedCertificateException(IReadOnlyList<CertificateInfo> certs, Exception? inner)
        : base("The NAS answered, but its certificate is not trusted: " +
               string.Join("; ", certs.Select(c => $"{c.Address} ({c.Problem})")) +
               ". Log in to the NAS again from the Unisono window to review and trust it.", inner)
    {
        Certificates = certs;
    }

    /// <summary>One entry per distinct certificate, the first address it was seen at.</summary>
    public IReadOnlyList<CertificateInfo> Distinct =>
        Certificates.GroupBy(c => c.Sha256, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
}
