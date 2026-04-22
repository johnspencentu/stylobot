# LLM Holodeck Plugin: In-Process Fake Response Generation

**Date:** 2026-04-22
**Status:** Approved
**Scope:** New `Mostlylucid.BotDetection.Llm.Holodeck` plugin that uses the system's existing `ILlmProvider` to generate dynamic fake responses for honeypot/holodeck paths

---

## Problem

The current `HolodeckActionPolicy` proxies requests over HTTP to a separate MockLLMApi service. This means:

- A whole separate service must be deployed and managed
- The holodeck uses a different LLM instance than bot classification
- When MockLLMApi is down, bots get generic JSON fallbacks (`{"data":[],"message":"No results found"}`)
- The HTTP proxy adds latency, failure modes, and operational complexity

## Solution

A new plugin package `Mostlylucid.BotDetection.Llm.Holodeck` that calls `ILlmProvider.CompleteAsync()` in-process. When a `SimulationPackResponder` template has `Dynamic = true`, the plugin generates realistic fake content using the same LLM provider already configured for bot classification (Ollama, LlamaSharp, Cloud). No separate service, no HTTP proxy.

---

## Architecture

### Three layers, one request

```
Bot hits /wp-login.php
  → SimulationPackResponder finds WordPress pack template (Dynamic=true)
  → IHolodeckResponder.GenerateAsync(template, request context, canary)
    → HolodeckPromptBuilder: pack personality + ResponseHints + canary instructions
    → ILlmProvider.CompleteAsync(prompt)
    → Returns generated HTML/JSON with canary embedded
  → SimulationPackResponder serves the response
```

### Capability-aware: optional plugin, graceful fallback

```csharp
// Node WITH LLM capability
builder.Services.AddBotDetection();
builder.Services.AddOllamaLlm();       // registers ILlmProvider
builder.Services.AddLlmHolodeck();     // registers IHolodeckResponder

// Node WITHOUT LLM (detection only, static templates)
builder.Services.AddBotDetection();
// No AddLlmHolodeck() — IHolodeckResponder is null, static templates served
```

`SimulationPackResponder` resolves `IHolodeckResponder` via `GetService<IHolodeckResponder>()`. If null or `IsAvailable == false`, it serves static `template.Body`. No hard dependency. Future multi-node deployments assign LLM capability to specific nodes — nodes without `AddLlmHolodeck()` serve static templates automatically.

---

## IHolodeckResponder Interface

```csharp
public interface IHolodeckResponder
{
    /// <summary>
    ///     Generate a dynamic fake response using the LLM.
    ///     Returns generated content with canary embedded.
    /// </summary>
    Task<HolodeckResponse> GenerateAsync(
        PackResponseTemplate template,
        HolodeckRequestContext requestContext,
        string? canary,
        CancellationToken ct = default);

    /// <summary>
    ///     Whether the LLM provider is ready to accept requests.
    ///     False when no ILlmProvider is registered or it hasn't initialized.
    /// </summary>
    bool IsAvailable { get; }
}

public sealed record HolodeckResponse
{
    public required string Content { get; init; }
    public required string ContentType { get; init; }
    public int StatusCode { get; init; } = 200;
    public Dictionary<string, string>? Headers { get; init; }
    public bool WasGenerated { get; init; }  // true = LLM, false = static fallback
}

public sealed record HolodeckRequestContext
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? QueryString { get; init; }
    public string? ContentType { get; init; }
    public string? Fingerprint { get; init; }
    public string? PackId { get; init; }
}
```

---

## SimulationPackResponder Change

The existing `SimulationPackResponder` gets one small change. After finding a template, before writing the response:

```csharp
if (template.Dynamic && _holodeckResponder?.IsAvailable == true)
{
    var response = await _holodeckResponder.GenerateAsync(
        template, requestCtx, canary, cancellationToken);
    context.Response.StatusCode = response.StatusCode;
    context.Response.ContentType = response.ContentType;
    if (response.Headers != null)
        foreach (var h in response.Headers)
            context.Response.Headers.TryAdd(h.Key, h.Value);
    await context.Response.WriteAsync(response.Content, cancellationToken);
}
else
{
    // Static fallback — embed canary via placeholder replacement
    var body = template.Body;
    if (canary != null)
        body = body.Replace("{{nonce}}", canary)
                   .Replace("{{api_key}}", canary)
                   .Replace("{{token}}", canary);
    await context.Response.WriteAsync(body, cancellationToken);
}
```

`IHolodeckResponder?` is injected as an optional constructor parameter (null when plugin not registered).

---

## HolodeckPromptBuilder

Builds the LLM prompt from the template's `ResponseHints` and pack personality.

### Prompt structure

```
System:
You are simulating a {pack.Framework} {pack.Version} installation.
{pack.PromptPersonality ?? pack.Description}
Generate a realistic {template.ResponseHints.ResponseFormat} response.

Rules:
- Output ONLY the response body, no explanation or markdown fencing
- Match the content type exactly: {template.ContentType}
- The response must be valid {format} that a real {pack.Framework} would produce
{if canary:}
- Embed this exact value naturally in the response: "{canary}"
- Place it where a nonce, token, API key, or session value would appear
- Do NOT label it or mark it as special
{endif}

Context:
- Endpoint: {template.ResponseHints.EndpointDescription}
- Expected structure: {template.ResponseHints.BodySchema}
- HTTP method: {requestContext.Method}
- Request path: {requestContext.Path}
{if template.ResponseHints.ProductContext:}
- Product details: {each key-value pair}
{endif}

User:
Generate the {template.ResponseHints.ResponseFormat} response for {method} {path}
```

### Prompt parameters

- `Temperature`: 0.7 (creative enough for varied responses, not so high it produces garbage)
- `MaxTokens`: 2048 (enough for a full HTML page or JSON response)
- `TimeoutMs`: 3000 (fast fallback to static)

---

## Response Caching

Same fingerprint + same path = same canary (deterministic from `BeaconCanaryGenerator`). Cache generated responses to avoid redundant LLM calls.

```csharp
ConcurrentDictionary<string, CachedResponse> _cache
// Key: "{fingerprint}:{path}"
// Value: generated content + timestamp
// TTL: matches BeaconTtlHours (default 24h)
```

Request flow:
1. First hit from fingerprint "abc" to `/wp-login.php` → cache miss → LLM generates → cached + beacon stored
2. Second hit from "abc" to `/wp-login.php` → cache hit → served instantly (no LLM call)
3. Hit from fingerprint "xyz" to `/wp-login.php` → different cache key → LLM generates fresh

Eviction: background timer purges entries older than TTL. Max cache size configurable (default 500 entries).

---

## Timeout and Fallback

If the LLM doesn't respond within `HolodeckTimeoutMs` (default 3000ms):

1. Cancel the LLM request
2. Fall back to static `template.Body`
3. Embed canary via placeholder replacement (`{{nonce}}`, `{{api_key}}`, `{{token}}`)
4. Log the timeout for monitoring

Pack authors add `{{nonce}}` or `{{api_key}}` placeholders in their static templates as canary insertion points. If no placeholder exists, canary embedding is skipped for that template.

---

## What This Replaces

The entire HTTP proxy mechanism in `HolodeckActionPolicy`:

| Before (proxy to MockLLMApi) | After (in-process ILlmProvider) |
|-----|------|
| Separate MockLLMApi service | Same `ILlmProvider` as bot classification |
| HTTP proxy with custom headers | Direct `CompleteAsync()` call |
| `_httpClient.SendAsync()` | `_llmProvider.CompleteAsync()` |
| MockLLMApi context memory | Response cache keyed by fingerprint+path |
| ShapeBuilder + API type detection | Pack `ResponseHints` drive the prompt |
| Separate LLM instance/config | Shared LLM, shared config, shared capacity |

`HolodeckActionPolicy` is replaced by the enhanced `SimulationPackResponder` + `IHolodeckResponder`. The action policy class, HTTP client, ShapeBuilder, and all proxy infrastructure can be removed.

---

## New Package Structure

```
Mostlylucid.BotDetection.Llm.Holodeck/
├── Mostlylucid.BotDetection.Llm.Holodeck.csproj
├── IHolodeckResponder.cs           # Interface + DTOs
├── LlmHolodeckResponder.cs         # Implementation: prompt → ILlmProvider → response
├── HolodeckPromptBuilder.cs        # Builds prompts from ResponseHints + canary
├── HolodeckResponseCache.cs        # Per-fingerprint+path cache with TTL
├── HolodeckLlmOptions.cs           # TimeoutMs, MaxTokens, Temperature, CacheSize
└── Extensions/
    └── LlmHolodeckExtensions.cs    # AddLlmHolodeck() registration
```

### Dependencies

- `Mostlylucid.BotDetection` — for `SimulationPack`, `PackResponseTemplate`, `IActionPolicy`
- `Mostlylucid.BotDetection.Llm` — for `ILlmProvider`, `LlmRequest`
- No dependency on `Mostlylucid.BotDetection.ApiHolodeck` — the interface `IHolodeckResponder` is defined in the core project or in this plugin

### Modified files (outside the new package)

| File | Change |
|------|--------|
| `Mostlylucid.BotDetection/SimulationPacks/SimulationPackResponder.cs` | Optional `IHolodeckResponder` injection, dynamic generation path |
| `Mostlylucid.BotDetection/SimulationPacks/SimulationPack.cs` | Add `string? PromptPersonality` to pack model (already exists in LLMApi's `HoldeckPack`) |

---

## Configuration

```json
{
  "BotDetection": {
    "LlmHolodeck": {
      "TimeoutMs": 3000,
      "Temperature": 0.7,
      "MaxTokens": 2048,
      "CacheSize": 500,
      "CacheTtlHours": 24
    }
  }
}
```

No LLM endpoint config — it uses whatever `ILlmProvider` is already registered.

---

## What Does NOT Change

- `ILlmProvider` interface — untouched, consumed as-is
- Existing LLM providers (Ollama, LlamaSharp, Cloud) — untouched
- `SimulationPack` YAML format — unchanged (just `PromptPersonality` field added to C# model)
- `BeaconCanaryGenerator` / `BeaconStore` — untouched, canaries still work the same
- `HolodeckCoordinator` — still gates engagements per fingerprint
- `HoneypotPathTagger` — still tags paths pre-detection
- Detection pipeline — untouched
