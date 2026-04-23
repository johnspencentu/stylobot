using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Llm.Tunnel;

/// <summary>
/// Probes a local Ollama instance to discover available models
/// and returns sanitized model metadata.
/// </summary>
public sealed class LocalLlmProviderProbe(
    ILogger<LocalLlmProviderProbe> logger,
    HttpClient? httpClient = null)
{
    /// <summary>
    /// Probe the Ollama instance and return filtered model inventory.
    /// </summary>
    /// <param name="ollamaBaseUrl">e.g. http://127.0.0.1:11434</param>
    /// <param name="allowedModels">If non-empty, only include these models.</param>
    /// <param name="maxContextTokens">Cap ContextLength to this value.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<LlmProbeResult> ProbeAsync(
        string ollamaBaseUrl,
        IReadOnlyList<string>? allowedModels = null,
        int maxContextTokens = 8192,
        CancellationToken ct = default)
    {
        // Use an injected client (for tests) or create a short-lived one (production).
        // Do not mutate a pooled IHttpClientFactory client.
        using var ownedClient = httpClient is null
            ? new HttpClient
            {
                BaseAddress = new Uri(ollamaBaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            }
            : null;
        var client = ownedClient ?? httpClient!;
        if (ownedClient is null)
        {
            // For injected clients (tests), configure base address if not already set
            client.BaseAddress ??= new Uri(ollamaBaseUrl.TrimEnd('/') + "/");
        }

        OllamaTagsResponse? tags;
        try
        {
            tags = await client.GetFromJsonAsync<OllamaTagsResponse>("api/tags", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            logger.LogWarning("Ollama probe failed at {Url}: {Message}", ollamaBaseUrl, ex.Message);
            return LlmProbeResult.Unready("Ollama unavailable: " + ex.Message);
        }

        if (tags?.Models is null or { Count: 0 })
        {
            logger.LogWarning("Ollama at {Url} returned no models.", ollamaBaseUrl);
            return LlmProbeResult.Empty();
        }

        var allowSet = allowedModels is { Count: > 0 }
            ? new HashSet<string>(allowedModels, StringComparer.OrdinalIgnoreCase)
            : null;

        var models = tags.Models
            .Where(m => allowSet is null || allowSet.Contains(m.Name))
            .Select(m => new LlmNodeModelInfo
            {
                Id = m.Name,
                Family = m.Details?.Family,
                ParameterSize = m.Details?.ParameterSize,
                Quantization = m.Details?.QuantizationLevel,
                ContextLength = Math.Min(maxContextTokens, GuessContextLength(m.Details?.ParameterSize)),
                Allowed = true,
                SupportsStreaming = true
            })
            .ToList();

        return new LlmProbeResult
        {
            IsReady = true,
            Inventory = new LlmNodeModelInventory
            {
                Provider = "ollama",
                Models = models
            }
        };
    }

    /// Estimate a reasonable context length from the parameter size string.
    private static int GuessContextLength(string? paramSize)
    {
        if (paramSize is null) return 4096;
        return paramSize.ToLowerInvariant() switch
        {
            var s when s.Contains("70b") || s.Contains("72b") => 8192,
            var s when s.Contains("34b") || s.Contains("30b") => 8192,
            var s when s.Contains("14b") || s.Contains("13b") => 8192,
            var s when s.Contains("8b")  || s.Contains("7b")  => 8192,
            _ => 4096
        };
    }

    // ── Ollama JSON models (private, not exposed) ──────────────────────────

    private sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModelEntry>? Models { get; set; }
    }

    private sealed class OllamaModelEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("details")]
        public OllamaModelDetails? Details { get; set; }
    }

    private sealed class OllamaModelDetails
    {
        [JsonPropertyName("family")]
        public string? Family { get; set; }

        [JsonPropertyName("parameter_size")]
        public string? ParameterSize { get; set; }

        [JsonPropertyName("quantization_level")]
        public string? QuantizationLevel { get; set; }
    }
}

/// <summary>Result of probing the local LLM provider.</summary>
public sealed class LlmProbeResult
{
    public bool IsReady { get; set; }
    public string? Error { get; set; }
    public LlmNodeModelInventory? Inventory { get; set; }

    public static LlmProbeResult Unready(string error) => new() { IsReady = false, Error = error };
    public static LlmProbeResult Empty() => new() { IsReady = true, Inventory = new LlmNodeModelInventory { Provider = "ollama" } };
}
