using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Analyzes PoW challenge solve characteristics as detection signals.
///     When a visitor has previously solved a proof-of-work challenge, this contributor
///     reads the solve metadata (duration, worker count, timing jitter) and emits
///     human or bot signals based on the solve profile.
///
///     This is the feedback loop: challenge is an ACTION (post-detection),
///     verification result is a SIGNAL (consumed on next request). No circular dependency.
///
///     Key insight: a visitor scoring 0.5-0.7 (edge case) who solves a PoW with
///     realistic browser timing gets a strong human signal, reducing false positives.
/// </summary>
public class ChallengeVerificationContributor : ContributingDetectorBase
{
    private readonly IChallengeStore _challengeStore;
    private readonly ILogger<ChallengeVerificationContributor> _logger;

    public ChallengeVerificationContributor(
        IChallengeStore challengeStore,
        ILogger<ChallengeVerificationContributor> logger)
    {
        _challengeStore = challengeStore;
        _logger = logger;
    }

    public override string Name => "ChallengeVerification";
    public override int Priority => 25; // Wave 1, after behavioral, before session vector

    public override IReadOnlyList<TriggerCondition> TriggerConditions => [];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            // Get signature from context
            var signature = state.HttpContext.Items.TryGetValue("BotDetection:Signature", out var sig) && sig is string s
                ? s
                : null;

            if (signature is null)
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

            var verification = _challengeStore.GetVerification(signature);
            if (verification is null)
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);

            // Write signals for downstream consumers
            state.WriteSignal(SignalKeys.ChallengeVerified, true);
            state.WriteSignal(SignalKeys.ChallengeSolveDurationMs, verification.TotalSolveDurationMs);
            state.WriteSignal(SignalKeys.ChallengeTimingJitter, verification.TimingJitter);
            state.WriteSignal(SignalKeys.ChallengeWorkerCount, verification.ReportedWorkerCount);
            state.WriteSignal(SignalKeys.ChallengePuzzleCount, verification.PuzzleCount);

            // Analyze solve characteristics
            var perPuzzleMs = verification.PuzzleCount > 0
                ? verification.TotalSolveDurationMs / verification.PuzzleCount
                : verification.TotalSolveDurationMs;

            // Too fast: powerful hardware or pre-computed (< 50ms per puzzle is suspicious)
            if (perPuzzleMs < 50)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ChallengeVerification",
                    ConfidenceDelta = 0.15,
                    Weight = 1.2,
                    Reason = $"PoW solved suspiciously fast ({perPuzzleMs:F0}ms/puzzle) - possible dedicated solver"
                });
            }
            // Expected human range: 200ms-5000ms per puzzle
            else if (perPuzzleMs is >= 200 and <= 5000)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ChallengeVerification",
                    ConfidenceDelta = -0.35, // Strong human signal
                    Weight = 1.5,
                    Reason = $"PoW solved with realistic timing ({perPuzzleMs:F0}ms/puzzle)"
                });
            }

            // Timing jitter analysis (performance.now() has 5ms quantization in workers)
            // Near-zero jitter with multiple puzzles = suspicious (dedicated solver, no OS scheduling noise)
            if (verification.PuzzleCount >= 4 && verification.TimingJitter < 0.05)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ChallengeVerification",
                    ConfidenceDelta = 0.10,
                    Weight = 1.0,
                    Reason = $"PoW timing jitter unusually low ({verification.TimingJitter:F3}) - possible automation"
                });
            }
            else if (verification.TimingJitter >= 0.15)
            {
                // High jitter = normal browser with OS scheduling noise = human-like
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ChallengeVerification",
                    ConfidenceDelta = -0.05,
                    Weight = 0.8,
                    Reason = "PoW timing jitter consistent with real browser"
                });
            }

            // Worker count analysis
            // 0-1 workers = possible headless browser (many don't expose navigator.hardwareConcurrency)
            if (verification.ReportedWorkerCount <= 1 && verification.PuzzleCount >= 4)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "ChallengeVerification",
                    ConfidenceDelta = 0.08,
                    Weight = 0.9,
                    Reason = $"PoW solved with {verification.ReportedWorkerCount} worker(s) - possible headless browser"
                });
            }

            _logger.LogDebug(
                "ChallengeVerification for {Signature}: {Duration:F0}ms total, {PerPuzzle:F0}ms/puzzle, jitter={Jitter:F3}, workers={Workers}",
                signature, verification.TotalSolveDurationMs, perPuzzleMs, verification.TimingJitter, verification.ReportedWorkerCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChallengeVerification analysis failed");
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }
}
