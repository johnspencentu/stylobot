using Microsoft.Extensions.Options;
using StyloFlow.Licensing.Cryptography;

namespace Stylobot.Website.Portal.Licensing;

/// <summary>
///     Supplies the vendor Ed25519 keypair to <see cref="LicenseIssuer"/>. Priority order:
///     <list type="number">
///       <item><description><c>Portal:License:PrivateKeyBase64</c> in config (production should pull this from Vault/KMS via env var).</description></item>
///       <item><description><c>Portal:License:PrivateKeyFilePath</c> - path to a file containing the base64 key.</description></item>
///       <item><description>Development fallback: generate a fresh ephemeral keypair, log a BIG warning, and cache in memory.</description></item>
///     </list>
///     NEVER commit a keypair to source control. In production the key should live in a
///     secrets vault (HashiCorp Vault, AWS KMS, Azure Key Vault) and be supplied via the
///     environment variable <c>Portal__License__PrivateKeyBase64</c>.
/// </summary>
public sealed class VendorKeyProvider
{
    private readonly PortalLicenseOptions _options;
    private readonly ILogger<VendorKeyProvider> _logger;
    private readonly Lazy<LicenseSigningService> _signer;

    public VendorKeyProvider(IOptions<PortalLicenseOptions> options, ILogger<VendorKeyProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _signer = new Lazy<LicenseSigningService>(LoadOrGenerate);
    }

    public LicenseSigningService Signer => _signer.Value;
    public string PublicKey => Signer.PublicKey;

    private LicenseSigningService LoadOrGenerate()
    {
        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyBase64))
        {
            _logger.LogInformation("Vendor signing key loaded from configuration");
            return new LicenseSigningService(_options.PrivateKeyBase64);
        }

        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyFilePath) && File.Exists(_options.PrivateKeyFilePath))
        {
            var key = File.ReadAllText(_options.PrivateKeyFilePath).Trim();
            _logger.LogInformation("Vendor signing key loaded from file {Path}", _options.PrivateKeyFilePath);
            return new LicenseSigningService(key);
        }

        // Dev-only fallback: generate ephemeral. Restarting the portal rotates the key and
        // invalidates every previously-issued license - surface this loudly.
        var (priv, pub) = Ed25519Signer.GenerateKeyPair();
        _logger.LogWarning(
            "═════════════════════════════════════════════════════════════════════\n" +
            " NO VENDOR SIGNING KEY CONFIGURED - generated a temporary one in memory.\n" +
            " Licenses issued with this key will stop validating after a portal restart.\n" +
            "\n" +
            " For persistent dev usage, set one of:\n" +
            "   Portal__License__PrivateKeyBase64=<base64>\n" +
            "   Portal__License__PrivateKeyFilePath=/path/to/key.txt\n" +
            "\n" +
            " Public key for this session (bake into control planes for validation):\n" +
            "   {PublicKey}\n" +
            "═════════════════════════════════════════════════════════════════════",
            pub);
        return new LicenseSigningService(priv);
    }
}

/// <summary>Licensing-specific options within the Portal section.</summary>
public sealed class PortalLicenseOptions
{
    /// <summary>Base64-encoded Ed25519 private key. Env: <c>Portal__License__PrivateKeyBase64</c>.</summary>
    public string? PrivateKeyBase64 { get; set; }

    /// <summary>Path to a file containing the base64 key. Used when mounting from Vault/KMS.</summary>
    public string? PrivateKeyFilePath { get; set; }

    /// <summary>Trial license duration. Default: 30 days.</summary>
    public TimeSpan TrialDuration { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Grace window after expiry during which premium features still work. Default: 7 days.</summary>
    public TimeSpan TrialGracePeriod { get; set; } = TimeSpan.FromDays(7);
}