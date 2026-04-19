using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Contributing detector that matches the current session's radar shape against
///     CVE-derived fingerprints. Runs in Wave 1 (after heuristic features are available)
///     at Priority 55 -- between Heuristic (50) and Similarity (60).
///
///     When a match is found, emits signals with CVE correlation metadata and contributes
///     a bot detection signal proportional to match confidence and advisory severity.
/// </summary>
public sealed class CveFingerprintContributor : ContributingDetectorBase
{
    private readonly ICveFingerprintMatcher _matcher;
    private readonly ILogger<CveFingerprintContributor> _logger;

    /// <summary>Minimum similarity to consider a CVE fingerprint match actionable.</summary>
    private const double MatchThreshold = 0.80;

    /// <summary>Confidence boost per severity level when a CVE matches.</summary>
    private static readonly IReadOnlyDictionary<string, double> SeverityBoost = new Dictionary<string, double>
    {
        ["critical"] = 0.35,
        ["high"] = 0.25,
        ["medium"] = 0.15,
        ["low"] = 0.08
    };

    public CveFingerprintContributor(
        ICveFingerprintMatcher matcher,
        ILogger<CveFingerprintContributor> logger)
    {
        _matcher = matcher;
        _logger = logger;
    }

    public override string Name => "CveFingerprint";
    public override int Priority => 55; // After Heuristic (50), before Similarity (60)

    // Requires heuristic to have run so we have feature signals to build the radar shape from
    public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
    {
        new SignalExistsTrigger(SignalKeys.HeuristicPrediction)
    };

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // No fingerprints loaded -- nothing to match against
        if (_matcher.FingerprintCount == 0)
        {
            state.WriteSignal(SignalKeys.CveMatchCount, 0);
            return None();
        }

        try
        {
            // Build the current session's radar shape from blackboard signals
            var dimensions = BuildRadarDimensions(state);

            // Find matching CVE fingerprints
            var matches = await _matcher.FindMatchesAsync(
                dimensions, topK: 5, minSimilarity: MatchThreshold, cancellationToken);

            state.WriteSignal(SignalKeys.CveMatchCount, matches.Count);

            if (matches.Count == 0)
                return None();

            var topMatch = matches[0];
            state.WriteSignal(SignalKeys.CveTopAdvisoryId, topMatch.AdvisoryId);
            state.WriteSignal(SignalKeys.CveTopSimilarity, topMatch.Similarity);
            state.WriteSignal(SignalKeys.CveTopSeverity, topMatch.Severity);

            if (topMatch.ClusterLabel is not null)
                state.WriteSignal(SignalKeys.CveClusterLabel, topMatch.ClusterLabel);

            // Write all matched CVE IDs for telemetry
            state.WriteSignal(SignalKeys.CveMatchedIds,
                string.Join(",", matches.Select(m => m.AdvisoryId)));

            // Calculate confidence boost based on severity and similarity
            var severityBoost = SeverityBoost.GetValueOrDefault(topMatch.Severity, 0.10);
            var confidence = severityBoost * topMatch.Similarity;

            _logger.LogInformation(
                "CVE fingerprint match: {AdvisoryId} ({Severity}) similarity={Similarity:F2} boost={Boost:F3}",
                topMatch.AdvisoryId, topMatch.Severity, topMatch.Similarity, confidence);

            var reason = topMatch.ClusterLabel is not null
                ? $"Traffic matches {topMatch.AdvisoryId} ({topMatch.Severity}) exploit family '{topMatch.ClusterLabel}' ({topMatch.Similarity:P0} match, {matches.Count} total CVE matches)"
                : $"Traffic matches {topMatch.AdvisoryId} ({topMatch.Severity}) CVE fingerprint ({topMatch.Similarity:P0} match, {matches.Count} total CVE matches)";

            return Single(DetectionContribution.Bot(
                Name,
                "CveFingerprint",
                confidence,
                reason,
                weight: 1.5, // High weight -- CVE matches are strong evidence
                botType: BotType.ExploitScanner.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CVE fingerprint matching failed");
            state.WriteSignal("cve.error", ex.Message);
            return None();
        }
    }

    /// <summary>
    ///     Build the 16-dimension radar shape from current blackboard signals.
    ///     Maps detector signals to the standardised RadarDimensions.
    /// </summary>
    private static Dictionary<string, double> BuildRadarDimensions(BlackboardState state)
    {
        var s = state.Signals;
        var dims = RadarDimensions.CreateEmpty();

        dims[RadarDimensions.UaAnomaly] = ReadBool(s, SignalKeys.UserAgentIsBot) ? 0.8 : 0.0;
        dims[RadarDimensions.HeaderAnomaly] = s.ContainsKey(SignalKeys.HeadersSuspicious) ? 0.6 : 0.0;
        dims[RadarDimensions.IpReputation] = ReadBool(s, SignalKeys.IpIsDatacenter) ? 0.5 : 0.0;
        dims[RadarDimensions.Behavioral] = ReadBool(s, SignalKeys.BehavioralAnomalyDetected) ? 0.7 : 0.0;
        dims[RadarDimensions.AdvancedBehavioral] = 0.0;
        dims[RadarDimensions.CacheBehavior] = ReadBool(s, SignalKeys.CacheBehaviorAnomaly) ? 0.6 : 0.0;
        dims[RadarDimensions.SecurityTool] = ReadBool(s, SignalKeys.SecurityToolDetected) ? 0.9 : 0.0;
        dims[RadarDimensions.ClientFingerprint] = Math.Min(1.0, ReadDouble(s, SignalKeys.FingerprintHeadlessScore));
        dims[RadarDimensions.VersionAge] = Math.Min(1.0, ReadDouble(s, SignalKeys.BrowserVersionAge) / 365.0);
        dims[RadarDimensions.Inconsistency] = Math.Min(1.0, ReadDouble(s, SignalKeys.InconsistencyScore));
        dims[RadarDimensions.ReputationMatch] = ReadBool(s, SignalKeys.ReputationFastPathHit) ? 0.7 : 0.0;
        dims[RadarDimensions.AiClassification] = Math.Min(1.0, ReadDouble(s, SignalKeys.AiConfidence));
        dims[RadarDimensions.ClusterSignal] = 0.0;
        dims[RadarDimensions.CountryReputation] = 0.0;
        dims[RadarDimensions.RatePattern] = ReadBool(s, SignalKeys.BehavioralRateExceeded) ? 0.8 : 0.0;
        dims[RadarDimensions.PayloadSignature] = 0.0;

        return dims;
    }

    private static bool ReadBool(IReadOnlyDictionary<string, object> signals, string key) =>
        signals.TryGetValue(key, out var val) && val is true;

    private static double ReadDouble(IReadOnlyDictionary<string, object> signals, string key) =>
        signals.TryGetValue(key, out var val) && val is double d ? d : 0.0;
}
