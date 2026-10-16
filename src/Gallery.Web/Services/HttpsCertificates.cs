using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Services;

/// <summary>
/// The certificate for the https:// addresses. If Gallery:Https:CertificatePath is set (PEM, e.g. Let's Encrypt's
/// fullchain.pem with KeyPath, or a .pfx), we use it. Otherwise we create a self-signed certificate in DataDir/https on
/// first use and keep it, so a browser exception or trust setting made for it stays valid. We replace it only when it
/// is within <see cref="RenewBefore"/> of expiring, cannot be read, or does not cover this machine's name or a name in
/// Gallery:Https:Hosts. Nothing is loaded or created unless an https:// address is configured.
/// </summary>
public sealed class HttpsCertificates
{
    public const string CertFile = "gallery.crt", KeyFile = "gallery.key";
    /// <summary>Apple platforms reject server certificates valid for more than 825 days, even ones the user trusts.</summary>
    internal static readonly TimeSpan Validity = TimeSpan.FromDays(800);
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    public sealed record Loaded(X509Certificate2 Certificate, X509Certificate2Collection Chain, string Source, bool Generated);

    private readonly HttpsOptions _o;
    private readonly ILogger _log;
    private readonly Lazy<Loaded> _current;

    public HttpsCertificates(GalleryOptions options, ILogger<HttpsCertificates> log)
    {
        _o = options.Https;
        _log = log;
        Directory = Path.Combine(options.ResolvedDataDir, "https");
        _current = new Lazy<Loaded>(Load);
    }

    /// <summary>Where the generated certificate is kept.</summary>
    public string Directory { get; }

    public Loaded Current => _current.Value;

    /// <summary>Gives every https:// endpoint the gallery's certificate, loading or creating it when the first endpoint starts.</summary>
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<HttpsCertificates>();
        builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h =>
        {
            var c = k.ApplicationServices.GetRequiredService<HttpsCertificates>().Current;
            h.ServerCertificate = c.Certificate;
            h.ServerCertificateChain = c.Chain;
        }));
    }

    private Loaded Load() => string.IsNullOrWhiteSpace(_o.CertificatePath) ? LoadOrCreateGenerated() : LoadConfigured();

    private Loaded LoadConfigured()
    {
        var path = GalleryOptions.ExpandHome(_o.CertificatePath);
        var password = string.IsNullOrEmpty(_o.Password) ? null : _o.Password;
        X509Certificate2 leaf;
        var all = new X509Certificate2Collection();
        if (Path.GetExtension(path).ToLowerInvariant() is ".pfx" or ".p12")
        {
            all = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password);
            leaf = all.FirstOrDefault(c => c.HasPrivateKey) ?? throw new InvalidOperationException($"{path} does not contain a private key");
        }
        else
        {
            var keyPath = string.IsNullOrWhiteSpace(_o.KeyPath) ? null : GalleryOptions.ExpandHome(_o.KeyPath);
            leaf = password is null ? X509Certificate2.CreateFromPemFile(path, keyPath) : X509Certificate2.CreateFromEncryptedPemFile(path, password, keyPath);
            all.ImportFromPemFile(path); // the middle certificates that follow the main one in a full-chain file
        }
        var chain = new X509Certificate2Collection(all.Where(c => c.Thumbprint != leaf.Thumbprint).ToArray());
        var left = leaf.NotAfter.ToUniversalTime() - DateTime.UtcNow;
        _log.LogInformation("HTTPS: using {Path} for {Names}, valid until {Expiry:yyyy-MM-dd}", path, Describe(leaf), leaf.NotAfter);
        if (left <= TimeSpan.Zero)
            _log.LogWarning("HTTPS: the certificate in {Path} expired on {Expiry:yyyy-MM-dd}; browsers will refuse it", path, leaf.NotAfter);
        else if (left < TimeSpan.FromDays(14))
            _log.LogWarning("HTTPS: the certificate in {Path} expires on {Expiry:yyyy-MM-dd}; restart the gallery after renewing it", path, leaf.NotAfter);
        return new Loaded(Usable(leaf), chain, path, false);
    }

    private Loaded LoadOrCreateGenerated()
    {
        var crt = Path.Combine(Directory, CertFile);
        var key = Path.Combine(Directory, KeyFile);
        var names = RequiredNames(_o.Hosts);
        string? reason = null;
        if (File.Exists(crt) && File.Exists(key))
        {
            try
            {
                var existing = X509Certificate2.CreateFromPemFile(crt, key);
                reason = WhyReplace(existing, names, DateTimeOffset.UtcNow);
                if (reason is null)
                {
                    _log.LogInformation("HTTPS: using the self-signed certificate in {Dir} for {Names}, valid until {Expiry:yyyy-MM-dd}, SHA-256 {Fingerprint}. "
                        + "Browsers warn about it until it is trusted; set Gallery:Https:CertificatePath to use your own",
                        Directory, Describe(existing), existing.NotAfter, Fingerprint(existing));
                    return new Loaded(Usable(existing), new X509Certificate2Collection(), crt, true);
                }
                existing.Dispose();
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or ArgumentException)
            {
                reason = $"it could not be read ({ex.Message})";
            }
        }

        CreateFiles(Directory, names, MachineAddresses(), Validity);
        var cert = X509Certificate2.CreateFromPemFile(crt, key);
        _log.LogInformation("HTTPS: {Action} self-signed certificate in {Dir} for {Names}, valid until {Expiry:yyyy-MM-dd}, SHA-256 {Fingerprint}. "
            + "Browsers warn about it until it is trusted; set Gallery:Https:CertificatePath to use your own",
            reason is null ? "created a" : $"replaced the old certificate ({reason}) with a new", Directory, Describe(cert), cert.NotAfter, Fingerprint(cert));
        return new Loaded(Usable(cert), new X509Certificate2Collection(), crt, true);
    }

    /// <summary>Names the certificate must cover to be kept: localhost, this machine's name and the configured hosts.</summary>
    internal static List<string> RequiredNames(IEnumerable<string> hosts)
    {
        var names = new List<string> { "localhost" };
        var host = MachineName();
        if (host is not null) names.Add(host);
        names.AddRange(hosts.Select(h => h.Trim()).Where(h => h.Length > 0));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Why a saved certificate should be replaced, or null to keep it.</summary>
    internal static string? WhyReplace(X509Certificate2 cert, IEnumerable<string> names, DateTimeOffset now)
    {
        if (cert.NotAfter.ToUniversalTime() - now.UtcDateTime < RenewBefore) return $"it expires on {cert.NotAfter:yyyy-MM-dd}";
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        var dns = san?.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var ips = san?.EnumerateIPAddresses().ToHashSet() ?? [];
        var missing = names.Where(n => IPAddress.TryParse(n, out var ip) ? !ips.Contains(ip) : !dns.Contains(n)).ToList();
        return missing.Count > 0 ? $"it does not cover {string.Join(", ", missing)}" : null;
    }

    /// <summary>
    /// Writes a self-signed server certificate (PEM) and its private key (PEM, readable only by this user) into dir.
    /// Names that are IP addresses become IP entries. The loopback addresses are always included.
    /// </summary>
    internal static void CreateFiles(string dir, IEnumerable<string> names, IEnumerable<IPAddress> addresses, TimeSpan validity)
    {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(dir);
        else System.IO.Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var san = new SubjectAlternativeNameBuilder();
        var dns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ips = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        foreach (var n in names)
        {
            if (IPAddress.TryParse(n, out var ip)) ips.Add(ip);
            else if (dns.Add(n) && !n.Contains('.') && n != "localhost") dns.Add(n + ".local"); // mDNS name on a home network
        }
        ips.UnionWith(addresses);
        foreach (var n in dns) san.AddDnsName(n);
        foreach (var ip in ips) san.AddIpAddress(ip);

        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(names.FirstOrDefault(n => n != "localhost" && !IPAddress.TryParse(n, out _)) ?? "localhost");
        subject.AddOrganizationName("Gallery (self-signed)");

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now + validity); // starts a day early, for clocks that are behind

        Write(Path.Combine(dir, KeyFile), rsa.ExportPkcs8PrivateKeyPem(), secret: true);
        Write(Path.Combine(dir, CertFile), cert.ExportCertificatePem(), secret: false);
    }

    /// <summary>Writes through a temporary file so a crash can't leave half a key. The key is readable only by its owner.</summary>
    private static void Write(string path, string text, bool secret)
    {
        var tmp = path + ".tmp";
        File.Delete(tmp);
        var fso = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            fso.UnixCreateMode = secret ? UnixFileMode.UserRead | UnixFileMode.UserWrite : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        using (var w = new StreamWriter(tmp, fso)) w.Write(text + "\n");
        File.Move(tmp, path, overwrite: true);
    }

    private static string? MachineName()
    {
        try { var h = Dns.GetHostName(); return string.IsNullOrWhiteSpace(h) ? null : h.ToLowerInvariant(); }
        catch (SocketException) { return null; }
    }

    /// <summary>This machine's current addresses, so the gallery can also be opened by IP. They change, so they play no part in deciding whether to keep the certificate.</summary>
    internal static List<IPAddress> MachineAddresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6Multicast))
                            list.Add(a);
                    }
                }
                catch (NetworkInformationException) { }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    /// <summary>Windows' TLS cannot use a key loaded from PEM until it goes through PKCS#12 (dotnet/runtime#23749).</summary>
    private static X509Certificate2 Usable(X509Certificate2 cert)
    {
        if (!OperatingSystem.IsWindows()) return cert;
        var copy = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
        cert.Dispose();
        return copy;
    }

    private static string Describe(X509Certificate2 cert)
    {
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null) return cert.GetNameInfo(X509NameType.SimpleName, false);
        return string.Join(", ", san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(ip => ip.ToString())));
    }

    private static string Fingerprint(X509Certificate2 cert) => Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
}
