using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Unified signature contributor. Computes the canonical PrimarySignature via
///     MultiFactorSignatureService and writes it to the blackboard so ALL downstream
///     contributors (BehavioralWaveform, SessionVector, etc.) use one identity key.
///
///     Priority 1 — runs before everything else in Wave 0.
///     No YAML config needed (no tunable parameters).
///     No trigger conditions (runs on every request).
///     Returns empty contributions (informational only — no bot/human signal).
/// </summary>
public class SignatureContributor : ContributingDetectorBase
{
    private readonly ILogger<SignatureContributor> _logger;
    private readonly MultiFactorSignatureService _signatureService;

    public SignatureContributor(
        ILogger<SignatureContributor> logger,
        MultiFactorSignatureService signatureService)
    {
        _logger = logger;
        _signatureService = signatureService;
    }

    public override string Name => "Signature";
    public override int Priority => 1;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var signatures = _signatureService.GenerateSignatures(state.HttpContext);

            // Write the canonical signature to the blackboard
            if (!string.IsNullOrEmpty(signatures.PrimarySignature))
            {
                state.WriteSignal(SignalKeys.PrimarySignature, signatures.PrimarySignature);
            }

            // Store the full MultiFactorSignatures object in HttpContext.Items
            // for backward compat with the post-detection middleware (response headers, etc.)
            state.HttpContext.Items["BotDetection.Signatures"] = signatures;
            state.HttpContext.Items["BotDetection:Signature"] = signatures.PrimarySignature;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error computing unified signature");
        }

        // No detection contributions — this is an identity contributor, not a detector
        return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());
    }
}
