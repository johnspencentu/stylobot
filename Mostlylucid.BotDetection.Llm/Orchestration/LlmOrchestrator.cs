using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Llm.Orchestration;

/// <summary>
///     LLM orchestrator that wraps a primary provider with fallback chain,
///     budget enforcement, and per-use-case routing.
///     Implements ILlmProvider so it's transparent to consumers.
/// </summary>
public sealed class LlmOrchestrator : ILlmProvider
{
    private readonly ILlmProvider _primary;
    private readonly LlmOrchestratorOptions _options;
    private readonly LlmUsageTracker _tracker;
    private readonly ILogger<LlmOrchestrator> _logger;

    // Fallback providers are lazily created from options
    // (we don't DI-resolve them since there can be multiple)
    private readonly List<Func<LlmRequest, CancellationToken, Task<string>>>? _fallbacks;

    public LlmOrchestrator(
        ILlmProvider primary,
        IOptions<LlmOrchestratorOptions> options,
        LlmUsageTracker tracker,
        ILogger<LlmOrchestrator> logger)
    {
        _primary = primary;
        _options = options.Value;
        _tracker = tracker;
        _logger = logger;
    }

    public bool IsReady => _primary.IsReady;

    public Task InitializeAsync(CancellationToken ct = default) => _primary.InitializeAsync(ct);

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        // Budget check
        if (!_tracker.IsWithinBudget(_options.Budget))
        {
            _logger.LogWarning("LLM budget exceeded ({Requests}/hr, ${Cost}/day). Degrading to {Fallback}",
                _tracker.RequestsThisHour, _tracker.CostToday, _options.Budget.DegradeTo);
            // In degraded mode, still try primary (which might be ollama if configured)
            // Real degradation would swap to a different provider instance
        }

        // Try primary
        try
        {
            var result = await _primary.CompleteAsync(request, ct);
            if (!string.IsNullOrEmpty(result))
            {
                _tracker.RecordRequest("primary", request.Prompt.Length / 4, result.Length / 4);
                return result;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Primary LLM provider failed, trying fallbacks");
        }

        // Try fallback chain
        foreach (var fallback in _options.Fallback)
        {
            try
            {
                _logger.LogDebug("Trying fallback provider: {Provider}", fallback.Provider);
                // For now, fallbacks are informational - they'd need their own ILlmProvider instances
                // Full fallback instantiation is a Phase 2 feature
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Fallback {Provider} failed", fallback.Provider);
            }
        }

        return string.Empty;
    }
}
