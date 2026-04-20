using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Privacy;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Privacy contributor that detects PII in query strings and emits informational signals.
///     This is NOT a bot detection contributor - it produces neutral contributions with privacy
///     metadata that downstream systems use for sanitization and audit.
///
///     Runs in Wave 0 (no trigger conditions) at priority 8 (very early, before most detectors).
/// </summary>
public class PiiQueryStringContributor : ContributingDetectorBase
{
    private readonly ILogger<PiiQueryStringContributor> _logger;

    public PiiQueryStringContributor(ILogger<PiiQueryStringContributor> logger)
    {
        _logger = logger;
    }

    public override string Name => "PiiQueryString";
    public override int Priority => 8;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var queryString = state.HttpContext.Request.QueryString.Value;

        // Fast path: no query string means no PII to detect
        if (string.IsNullOrEmpty(queryString))
            return Task.FromResult(None());

        var result = QueryStringSanitizer.DetectPii(queryString);

        if (!result.HasPii)
            return Task.FromResult(None());

        // Emit privacy signals (types only, never actual PII values)
        var piiTypes = string.Join(",", result.DetectedTypes);

        state.WriteSignal(SignalKeys.PrivacyQueryPiiDetected, true);
        state.WriteSignal(SignalKeys.PrivacyQueryPiiTypes, piiTypes);

        // Check if request is unencrypted HTTP (PII in plaintext over the wire)
        var isHttps = state.HttpContext.Request.IsHttps;
        if (!isHttps)
        {
            state.WriteSignal(SignalKeys.PrivacyUnencryptedPii, true);
            _logger.LogWarning(
                "PII detected in unencrypted query string: types={PiiTypes}",
                piiTypes);
        }
        else
        {
            _logger.LogDebug(
                "PII detected in query string: types={PiiTypes}",
                piiTypes);
        }

        // Neutral contribution - this is a privacy finding, not a bot/human signal
        var contribution = DetectionContribution.Info(
            Name,
            "Privacy",
            $"Query string contains PII parameters: {piiTypes}");

        return Task.FromResult(Single(contribution));
    }
}
