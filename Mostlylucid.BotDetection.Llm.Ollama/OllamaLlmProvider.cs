using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Llm.Ollama;

/// <summary>
///     ILlmProvider backed by an external Ollama HTTP server.
///     Uses raw HTTP (no OllamaSharp) for reliable think:false support.
/// </summary>
public class OllamaLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaLlmProvider> _logger;
    private readonly OllamaProviderOptions _options;

    public OllamaLlmProvider(
        IHttpClientFactory httpFactory,
        ILogger<OllamaLlmProvider> logger,
        IOptions<OllamaProviderOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _http = httpFactory.CreateClient("stylobot-ollama");
        _http.BaseAddress = new Uri(_options.Endpoint.TrimEnd('/'));
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public bool IsReady => !string.IsNullOrEmpty(_options.Endpoint);

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (!IsReady) return string.Empty;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(request.TimeoutMs);

        var body = new OllamaChatRequest
        {
            Model = _options.Model,
            Messages = [new() { Role = "user", Content = request.Prompt }],
            Stream = false,
            Think = false,
            Options = new() { NumThread = _options.NumThreads, Temperature = request.Temperature }
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        try
        {
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(cts.Token);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    _logger.LogError("Ollama model '{Model}' not found. Run: ollama pull {Model}", _options.Model, _options.Model);
                else
                    _logger.LogWarning("[ollama] {Status}: {Error}", resp.StatusCode, err.Length > 300 ? err[..300] : err);
                return string.Empty;
            }

            var respJson = await resp.Content.ReadAsStringAsync(cts.Token);
            var payload = JsonSerializer.Deserialize<OllamaChatResponse>(respJson, JsonOpts);
            var content = payload?.Message?.Content?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Ollama returned empty response for model '{Model}'", _options.Model);
                return string.Empty;
            }

            return content;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Ollama request timed out after {Timeout}ms", request.TimeoutMs);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama completion failed");
            return string.Empty;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    internal sealed record OllamaChatRequest
    {
        public required string Model { get; init; }
        public required List<OllamaMessage> Messages { get; init; }
        public bool Stream { get; init; }
        public bool Think { get; init; }
        public OllamaRequestOptions? Options { get; init; }
    }

    internal sealed record OllamaMessage
    {
        public required string Role { get; init; }
        public required string Content { get; init; }
    }

    internal sealed record OllamaRequestOptions
    {
        [JsonPropertyName("num_thread")] public int NumThread { get; init; }
        public float Temperature { get; init; }
    }

    internal sealed record OllamaChatResponse
    {
        public OllamaMessage? Message { get; init; }
    }
}
