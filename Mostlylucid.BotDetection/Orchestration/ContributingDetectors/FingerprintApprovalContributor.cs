using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Checks if the current fingerprint has been manually approved, and if so,
///     validates locked dimension constraints against live blackboard signals.
///
///     This turns API key access from "key = access" into a behavioral contract:
///     the fingerprint must match approved dimensions (country, UA, IP range, etc.)
///     or the approval is void. A stolen key from a different environment is useless.
///
///     Approval is a strong human signal (-0.4 delta) when all locked dimensions match.
///     Dimension mismatch is a strong bot signal (+0.3 delta) - something changed.
/// </summary>
public class FingerprintApprovalContributor : ContributingDetectorBase
{
    private readonly IFingerprintApprovalStore _approvalStore;
    private readonly ILogger<FingerprintApprovalContributor> _logger;

    public FingerprintApprovalContributor(
        IFingerprintApprovalStore approvalStore,
        ILogger<FingerprintApprovalContributor> logger)
    {
        _approvalStore = approvalStore;
        _logger = logger;
    }

    public override string Name => "FingerprintApproval";
    public override int Priority => 24; // Before ChallengeVerification (25), after Behavioral (20)

    public override IReadOnlyList<TriggerCondition> TriggerConditions => [];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // Synchronous wrapper around the async store - the store is fast (SQLite local file)
        return ContributeInternalAsync(state, cancellationToken);
    }

    private async Task<IReadOnlyList<DetectionContribution>> ContributeInternalAsync(
        BlackboardState state,
        CancellationToken cancellationToken)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var signature = state.HttpContext.Items.TryGetValue("BotDetection:Signature", out var sig) && sig is string s
                ? s
                : null;

            if (signature is null)
                return contributions;

            var approval = await _approvalStore.GetAsync(signature, cancellationToken);
            if (approval is null)
                return contributions;

            // Check if approval is still active (not expired, not revoked)
            if (!approval.IsActive)
            {
                state.WriteSignal(SignalKeys.ApprovalVerified, false);
                state.WriteSignal(SignalKeys.ApprovalStatus, approval.RevokedAt.HasValue ? "revoked" : "expired");
                return contributions;
            }

            // Check locked dimensions against live signals
            if (approval.LockedDimensions.Count > 0)
            {
                var (allMatch, mismatches) = LockedDimensionChecker.Check(
                    approval.LockedDimensions,
                    key => GetSignalValue(state, key));

                state.WriteSignal(SignalKeys.ApprovalVerified, true);
                state.WriteSignal(SignalKeys.ApprovalLockedDimensionsOk, allMatch);

                if (!allMatch)
                {
                    // Locked dimensions don't match - approval is void
                    // This catches stolen credentials used from a different environment
                    var mismatchList = string.Join(", ", mismatches);
                    state.WriteSignal(SignalKeys.ApprovalDimensionMismatch, mismatchList);

                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "FingerprintApproval",
                        ConfidenceDelta = 0.3, // Strong bot signal
                        Weight = 2.0,
                        Reason = $"Approved fingerprint violated locked dimensions: {mismatchList}"
                    });

                    _logger.LogWarning(
                        "Fingerprint approval dimension mismatch for {Signature}: {Mismatches}",
                        signature, mismatchList);

                    return contributions;
                }
            }
            else
            {
                state.WriteSignal(SignalKeys.ApprovalVerified, true);
                state.WriteSignal(SignalKeys.ApprovalLockedDimensionsOk, true);
            }

            // All checks pass - emit strong human signal
            state.WriteSignal(SignalKeys.ApprovalJustification, approval.Justification);
            if (approval.ExpiresAt.HasValue)
                state.WriteSignal(SignalKeys.ApprovalExpiresAt, approval.ExpiresAt.Value.ToString("O"));
            state.WriteSignal(SignalKeys.ApprovalStatus, "active");

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "FingerprintApproval",
                ConfidenceDelta = -0.4, // Strong human signal
                Weight = 2.0,
                Reason = $"Fingerprint manually approved: {approval.Justification}"
            });

            _logger.LogDebug(
                "Fingerprint approval verified for {Signature}: {Justification}, {DimCount} locked dimensions OK",
                signature, approval.Justification, approval.LockedDimensions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FingerprintApproval check failed");
        }

        return contributions;
    }

    private static object? GetSignalValue(BlackboardState state, string key)
    {
        return state.Signals.TryGetValue(key, out var value) ? value : null;
    }
}
