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
    ///     Issue a 30-day SME trial license for the org. One-per-org — returns
    ///     <see cref="IssueResult.AlreadyHasTrial"/> if the org ever had a trial before.
    /// </summary>
    public async Task<IssueResult> IssueTrialAsync(
        Organization org,
        string actorSub,
        string? actorEmail,
        string? hardwareFingerprint,
        CancellationToken ct)
    {
        // One trial per org, ever — including revoked/expired ones.
        var everHadTrial = await _db.Licenses
            .AnyAsync(l => l.OrganizationId == org.Id && l.IsTrial, ct);

        if (everHadTrial)
            return IssueResult.AlreadyHasTrial();

        var now = DateTime.UtcNow;
        var expiry = now + _options.TrialDuration;

        var license = await IssueAsync(
            org: org,
            tier: StyloBotTiers.Sme,
            isTrial: true,
            expiresAt: expiry,
            hardwareFingerprint: hardwareFingerprint,
            actorSub: actorSub,
            actorEmail: actorEmail,
            reason: "30-day SME trial",
            ct: ct);

        return IssueResult.Success(license);
    }

    /// <summary>Common issuance path — signs, persists, audits.</summary>
    public async Task<License> IssueAsync(
        Organization org,
        string tier,
        bool isTrial,
        DateTime expiresAt,
        string? hardwareFingerprint,
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
            HardwareFingerprint: hardwareFingerprint);

        var unsigned = JsonSerializer.Serialize(payload, SerializerOptions);
        var signed = _keys.Signer.SignLicense(unsigned);

        var license = new License
        {
            OrganizationId = org.Id,
            Tier = tier,
            IsTrial = isTrial,
            Features = features,
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
        var limits = StyloBotLimits.ForTier(tier);
        // StyloFlow's LicenseToken uses MaxMoleculeSlots / MaxWorkUnitsPerMinute / MaxNodes.
        // Map our gateway-instance limit onto both MaxNodes (cluster size) and MaxMoleculeSlots.
        return new PayloadLimits(
            MaxNodes: limits.MaxGatewayInstances == int.MaxValue ? null : limits.MaxGatewayInstances,
            MaxMoleculeSlots: limits.MaxGatewayInstances == int.MaxValue ? 10_000 : limits.MaxGatewayInstances,
            MaxWorkUnitsPerMinute: limits.WorkUnitsPerMinute == int.MaxValue ? 1_000_000 : limits.WorkUnitsPerMinute);
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
        string? HardwareFingerprint);

    private sealed record PayloadLimits(int? MaxNodes, int MaxMoleculeSlots, int MaxWorkUnitsPerMinute);
}

public sealed record IssueResult(bool Succeeded, string? Reason, License? License)
{
    public static IssueResult Success(License license) => new(true, null, license);
    public static IssueResult AlreadyHasTrial() => new(false, "trial_already_consumed", null);
}
