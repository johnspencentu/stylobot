using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stylobot.Commercial.Abstractions;
using Stylobot.Website.Portal.Data;

namespace Stylobot.Website.Portal.Licensing;

/// <summary>
///     Issues, revokes, and retrieves license tokens for organizations.
///     Token payload intentionally matches <c>StyloFlow.Licensing.Models.LicenseToken</c> so
///     the customer's control plane can validate it with the shared vendor public key.
/// </summary>
public sealed class LicenseIssuer
{
    private readonly PortalDbContext _db;
    private readonly VendorKeyProvider _keys;
    private readonly PortalLicenseOptions _options;
    private readonly ILogger<LicenseIssuer> _logger;

    public LicenseIssuer(
        PortalDbContext db,
        VendorKeyProvider keys,
        IOptions<PortalLicenseOptions> options,
        ILogger<LicenseIssuer> logger)
    {
        _db = db;
        _keys = keys;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Issue a 30-day SME trial license for the org. One-per-org - returns
    ///     <see cref="IssueResult.AlreadyHasTrial"/> if the org ever had a trial before.
    /// </summary>
    public async Task<IssueResult> IssueTrialAsync(
        Organization org,
        string actorSub,
        string? actorEmail,
        string? hardwareFingerprint,
        IReadOnlyList<string>? domains,
        CancellationToken ct)
    {
        // One trial per org, ever - including revoked/expired ones.
        var everHadTrial = await _db.Licenses
            .AnyAsync(l => l.OrganizationId == org.Id && l.IsTrial, ct);

        if (everHadTrial)
            return IssueResult.AlreadyHasTrial();

        var validatedDomains = ValidateDomains(domains);
        if (validatedDomains.Error is not null)
            return IssueResult.Failure(validatedDomains.Error);

        var now = DateTime.UtcNow;
        var expiry = now + _options.TrialDuration;

        var license = await IssueAsync(
            org: org,
            tier: StyloBotTiers.Sme,
            isTrial: true,
            expiresAt: expiry,
            hardwareFingerprint: hardwareFingerprint,
            domains: validatedDomains.Normalized,
            actorSub: actorSub,
            actorEmail: actorEmail,
            reason: "30-day SME trial",
            ct: ct);

        return IssueResult.Success(license);
    }

    /// <summary>Common issuance path - signs, persists, audits.</summary>
    public async Task<License> IssueAsync(
        Organization org,
        string tier,
        bool isTrial,
        DateTime expiresAt,
        string? hardwareFingerprint,
        IReadOnlyList<string> domains,
        string actorSub,
        string? actorEmail,
        string reason,
        CancellationToken ct)
    {
        var features = StyloBotFeatures.TierDefaults.TryGetValue(tier, out var f)
            ? f.ToList()
            : new List<string>();

        var jti = Guid.NewGuid().ToString("N");
        var payload = new LicensePayload(
            LicenseId: jti,
            IssuedTo: org.Name,
            IssuedAt: DateTimeOffset.UtcNow,
            Expiry: new DateTimeOffset(expiresAt, TimeSpan.Zero),
            Tier: StyloBotTiers.MapToStyloFlowTier(tier),
            Features: features,
            Limits: BuildLimits(tier),
            OrgId: org.Id.ToString(),
            Domains: domains.ToList(),
            HardwareFingerprint: hardwareFingerprint);

        var unsigned = JsonSerializer.Serialize(payload, SerializerOptions);
        var signed = _keys.Signer.SignLicense(unsigned);

        var license = new License
        {
            OrganizationId = org.Id,
            Tier = tier,
            IsTrial = isTrial,
            Features = features,
            Domains = domains.ToList(),
            HardwareFingerprint = hardwareFingerprint,
            TokenJti = jti,
            SignedToken = signed,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        };
        _db.Licenses.Add(license);

        _db.LicenseAudits.Add(new LicenseAudit
        {
            OrganizationId = org.Id,
            LicenseId = license.Id,
            Action = isTrial ? "issue-trial" : "issue",
            ActorKeycloakSub = actorSub,
            ActorEmail = actorEmail,
            Reason = reason
        });

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Issued {Kind} license {Jti} for org {OrgSlug} (tier={Tier}, expires={Expiry})",
            isTrial ? "trial" : "paid", jti, org.Slug, tier, expiresAt);

        return license;
    }

    public async Task RevokeAsync(
        License license,
        string actorSub,
        string? actorEmail,
        string? reason,
        CancellationToken ct)
    {
        if (license.RevokedAt is not null) return;

        license.RevokedAt = DateTime.UtcNow;
        _db.LicenseAudits.Add(new LicenseAudit
        {
            OrganizationId = license.OrganizationId,
            LicenseId = license.Id,
            Action = "revoke",
            ActorKeycloakSub = actorSub,
            ActorEmail = actorEmail,
            Reason = reason
        });
        await _db.SaveChangesAsync(ct);
    }

    private static PayloadLimits BuildLimits(string tier)
    {
        var quotas = StyloBotLimits.ForTier(tier);
        // StyloFlow.Licensing.LicenseToken uses MaxNodes / MaxMoleculeSlots / MaxWorkUnitsPerMinute
        // as its wire contract - those are StyloFlow's fields, not ours, and we have to fill them.
        // Our tier model doesn't cap nodes or slots (capability-only; see docs/licensing-tiers.md),
        // so we send null/high values there. The one field that genuinely matters for us is
        // MaxWorkUnitsPerMinute → customer's burst-rate-limit ceiling for fair-use throttling.
        return new PayloadLimits(
            MaxNodes: null,                                              // no cap
            MaxMoleculeSlots: 1_000_000,                                 // functionally unlimited
            MaxWorkUnitsPerMinute: quotas.BurstWorkUnitsPerMinute == 0   // 0 in our model means
                ? 1_000_000                                              // "no ceiling" - convert
                : quotas.BurstWorkUnitsPerMinute);                       // for StyloFlow
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    ///     Wire shape = <c>StyloFlow.Licensing.Models.LicenseToken</c> + StyloBot extensions
    ///     (<c>OrgId</c>, <c>HardwareFingerprint</c>). Extension fields are ignored by the
    ///     StyloFlow parser but remain signed-and-verifiable as part of the payload hash.
    /// </summary>
    private sealed record LicensePayload(
        string LicenseId,
        string IssuedTo,
        DateTimeOffset IssuedAt,
        DateTimeOffset Expiry,
        string Tier,
        List<string> Features,
        PayloadLimits Limits,
        string OrgId,
        List<string> Domains,
        string? HardwareFingerprint);

    private sealed record PayloadLimits(int? MaxNodes, int MaxMoleculeSlots, int MaxWorkUnitsPerMinute);

    /// <summary>
    ///     Validate and normalize a domain list. Rejects:
    ///     - empty / whitespace entries
    ///     - cloud-pool hostnames as PRIMARY domains (azurewebsites.net, vercel.app, etc.);
    ///       they'd cover thousands of unrelated customers. Fine as additional hosts - if
    ///       all domains on a license are cloud-pool, reject; if at least one is a real
    ///       domain, allow the cloud-pool ones as staging hosts.
    ///     - obviously malformed hosts (no dot; reserved TLDs like .local)
    ///
    ///     Returns normalized domain list (lowercased, trimmed, port stripped) or an error.
    /// </summary>
    internal static (IReadOnlyList<string> Normalized, string? Error) ValidateDomains(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0)
            return (Array.Empty<string>(), "At least one domain is required for the license.");

        var normalized = new List<string>();
        var hasRealDomain = false;

        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var e = entry.Trim();

            var isExact = e.StartsWith('=');
            var host = isExact ? e[1..] : e;
            host = Mostlylucid.BotDetection.Licensing.CloudPoolHosts.NormalizeHost(host);

            if (!LooksLikeDomain(host))
                return (Array.Empty<string>(), $"\"{entry}\" isn't a valid domain.");

            // Reserved dev-only TLDs shouldn't be licensed - they're covered implicitly.
            if (host.EndsWith(".test", StringComparison.Ordinal) ||
                host.EndsWith(".local", StringComparison.Ordinal) ||
                host.EndsWith(".localhost", StringComparison.Ordinal) ||
                host == "localhost")
            {
                return (Array.Empty<string>(),
                    $"\"{entry}\" is a development host - these are always allowed without licensing.");
            }

            if (!Mostlylucid.BotDetection.Licensing.CloudPoolHosts.IsCloudPoolHost(host))
                hasRealDomain = true;

            normalized.Add(isExact ? "=" + host : host);
        }

        if (normalized.Count == 0)
            return (Array.Empty<string>(), "At least one domain is required.");

        if (!hasRealDomain)
            return (Array.Empty<string>(),
                "Primary domain must be your organization's own domain. Shared cloud platform hostnames " +
                "(azurewebsites.net, vercel.app, herokuapp.com, etc.) can be added AFTER your primary, " +
                "but aren't valid on their own.");

        return (normalized, null);
    }

    private static bool LooksLikeDomain(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.Length is < 3 or > 253) return false;
        if (!host.Contains('.')) return false;
        // Basic character set check - letters, digits, dots, hyphens.
        foreach (var c in host)
            if (!(char.IsLetterOrDigit(c) || c is '.' or '-'))
                return false;
        // No leading/trailing dots or hyphens, no consecutive dots.
        if (host.StartsWith('.') || host.EndsWith('.')) return false;
        if (host.Contains("..")) return false;
        return true;
    }
}

public sealed record IssueResult(bool Succeeded, string? Reason, License? License)
{
    public static IssueResult Success(License license) => new(true, null, license);
    public static IssueResult AlreadyHasTrial() => new(false, "trial_already_consumed", null);
    public static IssueResult Failure(string reason) => new(false, reason, null);
}