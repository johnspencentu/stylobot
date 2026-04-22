# LLM Holodeck Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Mostlylucid.BotDetection.Llm.Holodeck` plugin that generates dynamic fake honeypot responses using the system's existing `ILlmProvider`, replacing the external MockLLMApi HTTP proxy.

**Architecture:** New package with `IHolodeckResponder` interface, `LlmHolodeckResponder` implementation (prompt builder + `ILlmProvider.CompleteAsync()` + response cache), and `SimulationPackResponder` modified to optionally use it for `Dynamic = true` templates. Capability-aware: nodes without the plugin serve static templates.

**Tech Stack:** .NET 10, `ILlmProvider` (existing), `SimulationPack` models (existing), xUnit

**Spec:** `docs/superpowers/specs/2026-04-22-llm-holodeck-plugin-design.md`

---

## File Map

### New package: `Mostlylucid.BotDetection.Llm.Holodeck/`

| File | Responsibility |
|------|---------------|
| `Mostlylucid.BotDetection.Llm.Holodeck.csproj` | Project file, refs Llm + core |
| `IHolodeckResponder.cs` | Interface + DTOs (`HolodeckResponse`, `HolodeckRequestContext`) |
| `LlmHolodeckResponder.cs` | Implementation: prompt → ILlmProvider → response |
| `HolodeckPromptBuilder.cs` | Builds prompts from ResponseHints + canary |
| `HolodeckResponseCache.cs` | Per-fingerprint+path cache with TTL |
| `HolodeckLlmOptions.cs` | Config: TimeoutMs, MaxTokens, Temperature, CacheSize |
| `Extensions/LlmHolodeckExtensions.cs` | `AddLlmHolodeck()` registration |

### Modified files

| File | Change |
|------|--------|
| `Mostlylucid.BotDetection/SimulationPacks/SimulationPackResponder.cs` | Optional `IHolodeckResponder` injection, dynamic generation path |
| `Mostlylucid.BotDetection/SimulationPacks/SimulationPack.cs` | Add `PromptPersonality` field |
| `mostlylucid.stylobot.sln` | Add new project |

### Test files

| File | Responsibility |
|------|---------------|
| `Mostlylucid.BotDetection.Test/Holodeck/HolodeckPromptBuilderTests.cs` | Prompt building |
| `Mostlylucid.BotDetection.Test/Holodeck/LlmHolodeckResponderTests.cs` | End-to-end with mock ILlmProvider |
| `Mostlylucid.BotDetection.Test/Holodeck/HolodeckResponseCacheTests.cs` | Cache hit/miss/eviction |
| `Mostlylucid.BotDetection.Test/Holodeck/SimulationPackResponderDynamicTests.cs` | Integration: dynamic vs static path |

---

## Task 1: Create project skeleton and options

**Files:**
- Create: `Mostlylucid.BotDetection.Llm.Holodeck/Mostlylucid.BotDetection.Llm.Holodeck.csproj`
- Create: `Mostlylucid.BotDetection.Llm.Holodeck/HolodeckLlmOptions.cs`
- Modify: `mostlylucid.stylobot.sln`

- [ ] **Step 1: Create project directory**

```bash
mkdir -p Mostlylucid.BotDetection.Llm.Holodeck/Extensions
```

- [ ] **Step 2: Create .csproj**

Create `Mostlylucid.BotDetection.Llm.Holodeck/Mostlylucid.BotDetection.Llm.Holodeck.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFrameworks>net10.0</TargetFrameworks>
        <LangVersion>preview</LangVersion>
        <Nullable>enable</Nullable>
        <ImplicitUsings>enable</ImplicitUsings>
        <IsPackable>true</IsPackable>
        <PackageId>Mostlylucid.BotDetection.Llm.Holodeck</PackageId>
        <MinVerTagPrefix>botdetection-v</MinVerTagPrefix>
        <Authors>Mostlylucid</Authors>
        <Description>LLM-powered dynamic response generation for StyloBot holodeck honeypots</Description>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\Mostlylucid.BotDetection\Mostlylucid.BotDetection.csproj"/>
        <ProjectReference Include="..\Mostlylucid.BotDetection.Llm\Mostlylucid.BotDetection.Llm.csproj"/>
    </ItemGroup>

</Project>
```

- [ ] **Step 3: Create options class**

Create `Mostlylucid.BotDetection.Llm.Holodeck/HolodeckLlmOptions.cs`:

```csharp
namespace Mostlylucid.BotDetection.Llm.Holodeck;

/// <summary>
///     Configuration for the LLM-powered holodeck responder.
/// </summary>
public class HolodeckLlmOptions
{
    public const string SectionName = "BotDetection:LlmHolodeck";

    /// <summary>Timeout for LLM generation in ms. Falls back to static on timeout. Default: 3000.</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>LLM temperature for response generation. Default: 0.7.</summary>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>Max tokens for generated response. Default: 2048.</summary>
    public int MaxTokens { get; set; } = 2048;

    /// <summary>Max cached responses (per fingerprint+path). Default: 500.</summary>
    public int CacheSize { get; set; } = 500;

    /// <summary>Cache TTL in hours. Should match beacon TTL. Default: 24.</summary>
    public int CacheTtlHours { get; set; } = 24;
}
```

- [ ] **Step 4: Add to solution and build**

```bash
dotnet sln mostlylucid.stylobot.sln add Mostlylucid.BotDetection.Llm.Holodeck/Mostlylucid.BotDetection.Llm.Holodeck.csproj
dotnet build Mostlylucid.BotDetection.Llm.Holodeck/ --no-restore -v:minimal
```

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.Llm.Holodeck/ mostlylucid.stylobot.sln
git commit -m "Add Mostlylucid.BotDetection.Llm.Holodeck project skeleton"
```

---

## Task 2: IHolodeckResponder interface and DTOs

**Files:**
- Create: `Mostlylucid.BotDetection.Llm.Holodeck/IHolodeckResponder.cs`

- [ ] **Step 1: Create interface and DTOs**

Create `Mostlylucid.BotDetection.Llm.Holodeck/IHolodeckResponder.cs`:

```csharp
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Llm.Holodeck;

/// <summary>
///     Generates dynamic fake responses for holodeck honeypots using an LLM.
///     Registered optionally — SimulationPackResponder resolves via GetService
///     and falls back to static templates when null.
/// </summary>
public interface IHolodeckResponder
{
    /// <summary>
    ///     Generate a dynamic fake response for the given template.
    /// </summary>
    Task<HolodeckResponse> GenerateAsync(
        PackResponseTemplate template,
        HolodeckRequestContext requestContext,
        string? canary,
        CancellationToken ct = default);

    /// <summary>
    ///     Whether the LLM provider is ready. False when no provider is registered
    ///     or it hasn't initialized yet.
    /// </summary>
    bool IsAvailable { get; }
}

/// <summary>
///     Generated holodeck response content.
/// </summary>
public sealed record HolodeckResponse
{
    public required string Content { get; init; }
    public required string ContentType { get; init; }
    public int StatusCode { get; init; } = 200;
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>True if content was LLM-generated, false if static fallback.</summary>
    public bool WasGenerated { get; init; }
}

/// <summary>
///     Request context passed to the holodeck for prompt building.
/// </summary>
public sealed record HolodeckRequestContext
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? QueryString { get; init; }
    public string? ContentType { get; init; }
    public string? Fingerprint { get; init; }
    public string? PackId { get; init; }
    public string? PackFramework { get; init; }
    public string? PackVersion { get; init; }
    public string? PackPersonality { get; init; }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build Mostlylucid.BotDetection.Llm.Holodeck/ --no-restore -v:minimal
```

- [ ] **Step 3: Commit**

```bash
git add Mostlylucid.BotDetection.Llm.Holodeck/IHolodeckResponder.cs
git commit -m "Add IHolodeckResponder interface and DTOs"
```

---

## Task 3: HolodeckPromptBuilder

**Files:**
- Create: `Mostlylucid.BotDetection.Llm.Holodeck/HolodeckPromptBuilder.cs`
- Create: `Mostlylucid.BotDetection.Test/Holodeck/HolodeckPromptBuilderTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Test/Holodeck/HolodeckPromptBuilderTests.cs`:

```csharp
using Mostlylucid.BotDetection.Llm.Holodeck;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class HolodeckPromptBuilderTests
{
    [Fact]
    public void Build_IncludesFrameworkAndVersion()
    {
        var template = CreateTemplate(hints: new PackResponseHints
        {
            EndpointDescription = "WordPress login page",
            ResponseFormat = "html"
        });
        var context = CreateContext(framework: "WordPress", version: "5.9");

        var prompt = HolodeckPromptBuilder.Build(template, context, canary: null);

        Assert.Contains("WordPress", prompt);
        Assert.Contains("5.9", prompt);
        Assert.Contains("login page", prompt);
        Assert.Contains("html", prompt);
    }

    [Fact]
    public void Build_IncludesCanaryInstructions()
    {
        var template = CreateTemplate();
        var context = CreateContext();

        var prompt = HolodeckPromptBuilder.Build(template, context, canary: "abc12345");

        Assert.Contains("abc12345", prompt);
        Assert.Contains("nonce", prompt.ToLowerInvariant());
    }

    [Fact]
    public void Build_OmitsCanaryWhenNull()
    {
        var template = CreateTemplate();
        var context = CreateContext();

        var prompt = HolodeckPromptBuilder.Build(template, context, canary: null);

        Assert.DoesNotContain("Embed this exact value", prompt);
    }

    [Fact]
    public void Build_IncludesBodySchema()
    {
        var template = CreateTemplate(hints: new PackResponseHints
        {
            BodySchema = "{\"users\": [{\"id\": 1}]}",
            ResponseFormat = "json"
        });
        var context = CreateContext();

        var prompt = HolodeckPromptBuilder.Build(template, context, canary: null);

        Assert.Contains("{\"users\"", prompt);
    }

    [Fact]
    public void Build_IncludesPackPersonality()
    {
        var template = CreateTemplate();
        var context = CreateContext();
        context = context with { PackPersonality = "Respond as a misconfigured Apache server with debug mode enabled" };

        var prompt = HolodeckPromptBuilder.Build(template, context, canary: null);

        Assert.Contains("misconfigured Apache", prompt);
    }

    [Fact]
    public void Build_IncludesMethodAndPath()
    {
        var template = CreateTemplate();
        var context = CreateContext(method: "POST", path: "/wp-login.php");

        var prompt = HolodeckPromptBuilder.Build(template, context, canary: null);

        Assert.Contains("POST", prompt);
        Assert.Contains("/wp-login.php", prompt);
    }

    private static PackResponseTemplate CreateTemplate(PackResponseHints? hints = null) =>
        new()
        {
            PathPattern = "*",
            Body = "fallback",
            Dynamic = true,
            ContentType = "text/html",
            ResponseHints = hints ?? new PackResponseHints
            {
                EndpointDescription = "Test endpoint",
                ResponseFormat = "html"
            }
        };

    private static HolodeckRequestContext CreateContext(
        string method = "GET",
        string path = "/test",
        string framework = "WordPress",
        string version = "5.9") =>
        new()
        {
            Method = method,
            Path = path,
            PackFramework = framework,
            PackVersion = version
        };
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "HolodeckPromptBuilderTests" -v m
```

Add `<ProjectReference Include="..\Mostlylucid.BotDetection.Llm.Holodeck\Mostlylucid.BotDetection.Llm.Holodeck.csproj"/>` to test .csproj if needed.

- [ ] **Step 3: Implement HolodeckPromptBuilder**

Create `Mostlylucid.BotDetection.Llm.Holodeck/HolodeckPromptBuilder.cs`:

```csharp
using System.Text;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Llm.Holodeck;

/// <summary>
///     Builds LLM prompts for holodeck fake response generation.
/// </summary>
public static class HolodeckPromptBuilder
{
    public static string Build(
        PackResponseTemplate template,
        HolodeckRequestContext context,
        string? canary)
    {
        var sb = new StringBuilder(1024);
        var hints = template.ResponseHints;
        var format = hints?.ResponseFormat ?? "html";

        // System context
        sb.AppendLine($"You are simulating a {context.PackFramework ?? "web"} {context.PackVersion ?? ""} installation.");

        if (!string.IsNullOrEmpty(context.PackPersonality))
            sb.AppendLine(context.PackPersonality);

        sb.AppendLine($"Generate a realistic {format} response.");
        sb.AppendLine();

        // Rules
        sb.AppendLine("Rules:");
        sb.AppendLine($"- Output ONLY the response body, no explanation or markdown fencing");
        sb.AppendLine($"- Match the content type exactly: {template.ContentType}");
        sb.AppendLine($"- The response must be valid {format} that a real {context.PackFramework ?? "server"} would produce");

        if (!string.IsNullOrEmpty(canary))
        {
            sb.AppendLine($"- Embed this exact value naturally in the response: \"{canary}\"");
            sb.AppendLine("- Place it where a nonce, token, API key, or session value would appear");
            sb.AppendLine("- Do NOT label it or mark it as special");
        }

        sb.AppendLine();

        // Context
        sb.AppendLine("Context:");
        if (!string.IsNullOrEmpty(hints?.EndpointDescription))
            sb.AppendLine($"- Endpoint: {hints.EndpointDescription}");
        if (!string.IsNullOrEmpty(hints?.BodySchema))
            sb.AppendLine($"- Expected structure: {hints.BodySchema}");
        sb.AppendLine($"- HTTP method: {context.Method}");
        sb.AppendLine($"- Request path: {context.Path}");

        if (hints?.ProductContext is { Count: > 0 })
        {
            foreach (var (key, value) in hints.ProductContext)
                sb.AppendLine($"- {key}: {value}");
        }

        sb.AppendLine();
        sb.AppendLine($"Generate the {format} response for {context.Method} {context.Path}");

        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "HolodeckPromptBuilderTests" -v m
```

Expected: All 6 PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.Llm.Holodeck/HolodeckPromptBuilder.cs \
       Mostlylucid.BotDetection.Test/Holodeck/HolodeckPromptBuilderTests.cs \
       Mostlylucid.BotDetection.Test/*.csproj
git commit -m "Add HolodeckPromptBuilder with tests"
```

---

## Task 4: HolodeckResponseCache

**Files:**
- Create: `Mostlylucid.BotDetection.Llm.Holodeck/HolodeckResponseCache.cs`
- Create: `Mostlylucid.BotDetection.Test/Holodeck/HolodeckResponseCacheTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Test/Holodeck/HolodeckResponseCacheTests.cs`:

```csharp
using Mostlylucid.BotDetection.Llm.Holodeck;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class HolodeckResponseCacheTests
{
    [Fact]
    public void TryGet_Miss_ReturnsFalse()
    {
        var cache = new HolodeckResponseCache(maxSize: 10, ttl: TimeSpan.FromHours(1));
        Assert.False(cache.TryGet("fp1", "/path", out _));
    }

    [Fact]
    public void Set_ThenGet_ReturnsValue()
    {
        var cache = new HolodeckResponseCache(maxSize: 10, ttl: TimeSpan.FromHours(1));
        var response = new HolodeckResponse
        {
            Content = "<html>fake</html>",
            ContentType = "text/html",
            WasGenerated = true
        };

        cache.Set("fp1", "/wp-login.php", response);

        Assert.True(cache.TryGet("fp1", "/wp-login.php", out var cached));
        Assert.Equal("<html>fake</html>", cached!.Content);
    }

    [Fact]
    public void DifferentFingerprints_DifferentEntries()
    {
        var cache = new HolodeckResponseCache(maxSize: 10, ttl: TimeSpan.FromHours(1));
        var r1 = new HolodeckResponse { Content = "resp1", ContentType = "text/html", WasGenerated = true };
        var r2 = new HolodeckResponse { Content = "resp2", ContentType = "text/html", WasGenerated = true };

        cache.Set("fp1", "/path", r1);
        cache.Set("fp2", "/path", r2);

        cache.TryGet("fp1", "/path", out var got1);
        cache.TryGet("fp2", "/path", out var got2);
        Assert.Equal("resp1", got1!.Content);
        Assert.Equal("resp2", got2!.Content);
    }

    [Fact]
    public void MaxSize_EvictsOldest()
    {
        var cache = new HolodeckResponseCache(maxSize: 2, ttl: TimeSpan.FromHours(1));
        var r = new HolodeckResponse { Content = "x", ContentType = "text/html", WasGenerated = true };

        cache.Set("fp1", "/a", r);
        cache.Set("fp2", "/b", r);
        cache.Set("fp3", "/c", r); // evicts fp1:/a

        Assert.False(cache.TryGet("fp1", "/a", out _));
        Assert.True(cache.TryGet("fp2", "/b", out _));
        Assert.True(cache.TryGet("fp3", "/c", out _));
    }

    [Fact]
    public void Count_TracksEntries()
    {
        var cache = new HolodeckResponseCache(maxSize: 10, ttl: TimeSpan.FromHours(1));
        var r = new HolodeckResponse { Content = "x", ContentType = "text/html", WasGenerated = true };

        Assert.Equal(0, cache.Count);
        cache.Set("fp1", "/a", r);
        Assert.Equal(1, cache.Count);
        cache.Set("fp2", "/b", r);
        Assert.Equal(2, cache.Count);
    }
}
```

- [ ] **Step 2: Implement HolodeckResponseCache**

Create `Mostlylucid.BotDetection.Llm.Holodeck/HolodeckResponseCache.cs`:

```csharp
using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Llm.Holodeck;

/// <summary>
///     Caches generated holodeck responses per (fingerprint, path).
///     Same fingerprint + same path = same canary = same response.
///     Avoids redundant LLM calls on repeat visits.
/// </summary>
public sealed class HolodeckResponseCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxSize;
    private readonly TimeSpan _ttl;

    public HolodeckResponseCache(int maxSize, TimeSpan ttl)
    {
        _maxSize = maxSize;
        _ttl = ttl;
    }

    public int Count => _cache.Count;

    public bool TryGet(string fingerprint, string path, out HolodeckResponse? response)
    {
        response = null;
        var key = MakeKey(fingerprint, path);

        if (!_cache.TryGetValue(key, out var entry))
            return false;

        if (DateTime.UtcNow - entry.CreatedAt > _ttl)
        {
            _cache.TryRemove(key, out _);
            return false;
        }

        response = entry.Response;
        return true;
    }

    public void Set(string fingerprint, string path, HolodeckResponse response)
    {
        var key = MakeKey(fingerprint, path);

        // Evict oldest if at capacity
        while (_cache.Count >= _maxSize)
        {
            var oldest = _cache.OrderBy(kv => kv.Value.CreatedAt).FirstOrDefault();
            if (oldest.Key != null)
                _cache.TryRemove(oldest.Key, out _);
            else
                break;
        }

        _cache[key] = new CacheEntry(response, DateTime.UtcNow);
    }

    private static string MakeKey(string fingerprint, string path)
        => $"{fingerprint}:{path}";

    private sealed record CacheEntry(HolodeckResponse Response, DateTime CreatedAt);
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "HolodeckResponseCacheTests" -v m
```

Expected: All 5 PASS.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection.Llm.Holodeck/HolodeckResponseCache.cs \
       Mostlylucid.BotDetection.Test/Holodeck/HolodeckResponseCacheTests.cs
git commit -m "Add HolodeckResponseCache with TTL and max-size eviction"
```

---

## Task 5: LlmHolodeckResponder — the core implementation

**Files:**
- Create: `Mostlylucid.BotDetection.Llm.Holodeck/LlmHolodeckResponder.cs`
- Create: `Mostlylucid.BotDetection.Test/Holodeck/LlmHolodeckResponderTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Test/Holodeck/LlmHolodeckResponderTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Llm;
using Mostlylucid.BotDetection.Llm.Holodeck;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class LlmHolodeckResponderTests
{
    private static readonly PackResponseTemplate DynamicTemplate = new()
    {
        PathPattern = "/wp-login.php",
        Body = "static fallback with {{nonce}} placeholder",
        Dynamic = true,
        ContentType = "text/html",
        ResponseHints = new PackResponseHints
        {
            EndpointDescription = "WordPress login page",
            ResponseFormat = "html",
            BodySchema = "HTML form with username and password fields"
        }
    };

    private static readonly HolodeckRequestContext TestContext = new()
    {
        Method = "GET",
        Path = "/wp-login.php",
        PackFramework = "WordPress",
        PackVersion = "5.9",
        Fingerprint = "test-fp"
    };

    [Fact]
    public async Task GenerateAsync_CallsLlmProvider()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(true);
        mockLlm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html><body><h1>WordPress Login</h1></body></html>");

        var responder = CreateResponder(mockLlm.Object);

        var response = await responder.GenerateAsync(DynamicTemplate, TestContext, "abc12345");

        Assert.True(response.WasGenerated);
        Assert.Contains("WordPress Login", response.Content);
        Assert.Equal("text/html", response.ContentType);
        mockLlm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_CachesResponse()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(true);
        mockLlm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html>generated</html>");

        var responder = CreateResponder(mockLlm.Object);

        var r1 = await responder.GenerateAsync(DynamicTemplate, TestContext, "abc12345");
        var r2 = await responder.GenerateAsync(DynamicTemplate, TestContext, "abc12345");

        Assert.Equal(r1.Content, r2.Content);
        // LLM called only once — second hit from cache
        mockLlm.Verify(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateAsync_FallsBackOnLlmFailure()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(true);
        mockLlm.Setup(l => l.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timeout"));

        var responder = CreateResponder(mockLlm.Object);

        var response = await responder.GenerateAsync(DynamicTemplate, TestContext, "abc12345");

        Assert.False(response.WasGenerated);
        Assert.Contains("abc12345", response.Content); // canary embedded in static fallback
    }

    [Fact]
    public async Task GenerateAsync_FallsBackWhenLlmNotReady()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(false);

        var responder = CreateResponder(mockLlm.Object);

        var response = await responder.GenerateAsync(DynamicTemplate, TestContext, null);

        Assert.False(response.WasGenerated);
        Assert.Contains("static fallback", response.Content);
    }

    [Fact]
    public void IsAvailable_TrueWhenLlmReady()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(true);

        var responder = CreateResponder(mockLlm.Object);
        Assert.True(responder.IsAvailable);
    }

    [Fact]
    public void IsAvailable_FalseWhenLlmNotReady()
    {
        var mockLlm = new Mock<ILlmProvider>();
        mockLlm.Setup(l => l.IsReady).Returns(false);

        var responder = CreateResponder(mockLlm.Object);
        Assert.False(responder.IsAvailable);
    }

    private static LlmHolodeckResponder CreateResponder(ILlmProvider llmProvider)
    {
        var options = Options.Create(new HolodeckLlmOptions
        {
            TimeoutMs = 3000,
            Temperature = 0.7f,
            MaxTokens = 2048,
            CacheSize = 100,
            CacheTtlHours = 24
        });
        return new LlmHolodeckResponder(
            llmProvider,
            options,
            NullLogger<LlmHolodeckResponder>.Instance);
    }
}
```

- [ ] **Step 2: Implement LlmHolodeckResponder**

Create `Mostlylucid.BotDetection.Llm.Holodeck/LlmHolodeckResponder.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Llm.Holodeck;

/// <summary>
///     Generates dynamic fake responses using the system's ILlmProvider.
///     Caches responses per fingerprint+path to avoid redundant LLM calls.
///     Falls back to static template body on timeout or LLM failure.
/// </summary>
public sealed class LlmHolodeckResponder : IHolodeckResponder
{
    private readonly ILlmProvider _llmProvider;
    private readonly HolodeckLlmOptions _options;
    private readonly HolodeckResponseCache _cache;
    private readonly ILogger<LlmHolodeckResponder> _logger;

    public LlmHolodeckResponder(
        ILlmProvider llmProvider,
        IOptions<HolodeckLlmOptions> options,
        ILogger<LlmHolodeckResponder> logger)
    {
        _llmProvider = llmProvider;
        _options = options.Value;
        _logger = logger;
        _cache = new HolodeckResponseCache(
            _options.CacheSize,
            TimeSpan.FromHours(_options.CacheTtlHours));
    }

    public bool IsAvailable => _llmProvider.IsReady;

    public async Task<HolodeckResponse> GenerateAsync(
        PackResponseTemplate template,
        HolodeckRequestContext requestContext,
        string? canary,
        CancellationToken ct = default)
    {
        var fingerprint = requestContext.Fingerprint ?? "unknown";

        // Check cache first
        if (_cache.TryGet(fingerprint, requestContext.Path, out var cached))
        {
            _logger.LogDebug("Holodeck cache hit for {Fp}:{Path}", fingerprint[..Math.Min(8, fingerprint.Length)], requestContext.Path);
            return cached!;
        }

        // Try LLM generation
        if (_llmProvider.IsReady)
        {
            try
            {
                var prompt = HolodeckPromptBuilder.Build(template, requestContext, canary);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_options.TimeoutMs);

                var content = await _llmProvider.CompleteAsync(new LlmRequest
                {
                    Prompt = prompt,
                    Temperature = _options.Temperature,
                    MaxTokens = _options.MaxTokens,
                    TimeoutMs = _options.TimeoutMs
                }, cts.Token);

                var response = new HolodeckResponse
                {
                    Content = content,
                    ContentType = template.ContentType,
                    StatusCode = template.StatusCode,
                    Headers = template.Headers,
                    WasGenerated = true
                };

                _cache.Set(fingerprint, requestContext.Path, response);

                _logger.LogInformation(
                    "Holodeck generated {Format} response for {Path} ({Length} chars)",
                    template.ResponseHints?.ResponseFormat ?? "unknown",
                    requestContext.Path,
                    content.Length);

                return response;
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
            {
                _logger.LogWarning("Holodeck LLM timeout for {Path}, falling back to static", requestContext.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Holodeck LLM failed for {Path}, falling back to static", requestContext.Path);
            }
        }

        // Fallback: static template with canary placeholder replacement
        return BuildStaticFallback(template, canary);
    }

    private static HolodeckResponse BuildStaticFallback(PackResponseTemplate template, string? canary)
    {
        var body = template.Body;
        if (canary != null)
        {
            body = body.Replace("{{nonce}}", canary)
                       .Replace("{{api_key}}", canary)
                       .Replace("{{token}}", canary);
        }

        return new HolodeckResponse
        {
            Content = body,
            ContentType = template.ContentType,
            StatusCode = template.StatusCode,
            Headers = template.Headers,
            WasGenerated = false
        };
    }
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "LlmHolodeckResponderTests" -v m
```

Expected: All 6 PASS.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection.Llm.Holodeck/LlmHolodeckResponder.cs \
       Mostlylucid.BotDetection.Test/Holodeck/LlmHolodeckResponderTests.cs
git commit -m "Add LlmHolodeckResponder: LLM generation with cache and static fallback"
```

---

## Task 6: Extension method and SimulationPack model update

**Files:**
- Create: `Mostlylucid.BotDetection.Llm.Holodeck/Extensions/LlmHolodeckExtensions.cs`
- Modify: `Mostlylucid.BotDetection/SimulationPacks/SimulationPack.cs`

- [ ] **Step 1: Add PromptPersonality to SimulationPack**

In `Mostlylucid.BotDetection/SimulationPacks/SimulationPack.cs`, add after the `Description` property:

```csharp
    /// <summary>System prompt additions giving the LLM domain vocabulary and API style for this pack.</summary>
    public string? PromptPersonality { get; init; }
```

- [ ] **Step 2: Create extension method**

Create `Mostlylucid.BotDetection.Llm.Holodeck/Extensions/LlmHolodeckExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.BotDetection.Llm.Holodeck.Extensions;

/// <summary>
///     Registers the LLM-powered holodeck responder.
///     Call after AddBotDetection() and the LLM provider (AddStylobotOllama/AddStylobotLlamaSharp/etc).
/// </summary>
public static class LlmHolodeckExtensions
{
    public static IServiceCollection AddLlmHolodeck(
        this IServiceCollection services,
        Action<HolodeckLlmOptions>? configure = null)
    {
        services.AddOptions<HolodeckLlmOptions>()
            .BindConfiguration(HolodeckLlmOptions.SectionName)
            .Configure(opts => configure?.Invoke(opts));

        services.AddSingleton<IHolodeckResponder, LlmHolodeckResponder>();

        return services;
    }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build mostlylucid.stylobot.sln --no-restore -v:minimal
```

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection.Llm.Holodeck/Extensions/ \
       Mostlylucid.BotDetection/SimulationPacks/SimulationPack.cs
git commit -m "Add AddLlmHolodeck() extension and PromptPersonality to SimulationPack"
```

---

## Task 7: Wire IHolodeckResponder into SimulationPackResponder

**Files:**
- Modify: `Mostlylucid.BotDetection/SimulationPacks/SimulationPackResponder.cs`
- Create: `Mostlylucid.BotDetection.Test/Holodeck/SimulationPackResponderDynamicTests.cs`

- [ ] **Step 1: Write failing test**

Create `Mostlylucid.BotDetection.Test/Holodeck/SimulationPackResponderDynamicTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Mostlylucid.BotDetection.Llm.Holodeck;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class SimulationPackResponderDynamicTests
{
    [Fact]
    public async Task DynamicTemplate_UsesHolodeckResponder()
    {
        var mockRegistry = CreateRegistryWithDynamicTemplate("/wp-login.php");
        var mockResponder = new Mock<IHolodeckResponder>();
        mockResponder.Setup(r => r.IsAvailable).Returns(true);
        mockResponder.Setup(r => r.GenerateAsync(
                It.IsAny<PackResponseTemplate>(),
                It.IsAny<HolodeckRequestContext>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HolodeckResponse
            {
                Content = "<html>LLM generated</html>",
                ContentType = "text/html",
                WasGenerated = true
            });

        var responder = new SimulationPackResponder(
            mockRegistry.Object,
            NullLogger<SimulationPackResponder>.Instance,
            mockResponder.Object);

        var context = new DefaultHttpContext();
        context.Request.Path = "/wp-login.php";
        context.Request.Method = "GET";
        var evidence = CreateMinimalEvidence();

        await responder.ExecuteAsync(context, evidence);

        mockResponder.Verify(r => r.GenerateAsync(
            It.IsAny<PackResponseTemplate>(),
            It.IsAny<HolodeckRequestContext>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StaticTemplate_DoesNotCallResponder()
    {
        var mockRegistry = CreateRegistryWithStaticTemplate("/wp-login.php");
        var mockResponder = new Mock<IHolodeckResponder>();
        mockResponder.Setup(r => r.IsAvailable).Returns(true);

        var responder = new SimulationPackResponder(
            mockRegistry.Object,
            NullLogger<SimulationPackResponder>.Instance,
            mockResponder.Object);

        var context = new DefaultHttpContext();
        context.Request.Path = "/wp-login.php";
        context.Request.Method = "GET";

        await responder.ExecuteAsync(context, CreateMinimalEvidence());

        mockResponder.Verify(r => r.GenerateAsync(
            It.IsAny<PackResponseTemplate>(),
            It.IsAny<HolodeckRequestContext>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NullResponder_ServesStaticTemplate()
    {
        var mockRegistry = CreateRegistryWithDynamicTemplate("/wp-login.php");

        var responder = new SimulationPackResponder(
            mockRegistry.Object,
            NullLogger<SimulationPackResponder>.Instance,
            holodeckResponder: null);

        var context = new DefaultHttpContext();
        context.Request.Path = "/wp-login.php";
        context.Request.Method = "GET";

        var result = await responder.ExecuteAsync(context, CreateMinimalEvidence());

        // Should not throw, should serve static body
        Assert.False(result.Continue);
    }

    private static Mock<ISimulationPackRegistry> CreateRegistryWithDynamicTemplate(string path)
    {
        var pack = new SimulationPack
        {
            Id = "test-pack",
            Name = "Test Pack",
            Framework = "WordPress",
            Version = "5.9",
            ResponseTemplates =
            [
                new PackResponseTemplate
                {
                    PathPattern = path,
                    Body = "static fallback",
                    ContentType = "text/html",
                    Dynamic = true,
                    ResponseHints = new PackResponseHints
                    {
                        EndpointDescription = "WordPress login",
                        ResponseFormat = "html"
                    }
                }
            ]
        };

        var mock = new Mock<ISimulationPackRegistry>();
        mock.Setup(r => r.IsHoneypotPath(path, out pack, out It.Ref<PackCveModule?>.IsAny))
            .Returns(true);
        return mock;
    }

    private static Mock<ISimulationPackRegistry> CreateRegistryWithStaticTemplate(string path)
    {
        var pack = new SimulationPack
        {
            Id = "test-pack",
            Name = "Test Pack",
            Framework = "WordPress",
            Version = "5.9",
            ResponseTemplates =
            [
                new PackResponseTemplate
                {
                    PathPattern = path,
                    Body = "<html>static content</html>",
                    ContentType = "text/html",
                    Dynamic = false
                }
            ]
        };

        var mock = new Mock<ISimulationPackRegistry>();
        mock.Setup(r => r.IsHoneypotPath(path, out pack, out It.Ref<PackCveModule?>.IsAny))
            .Returns(true);
        return mock;
    }

    private static AggregatedEvidence CreateMinimalEvidence() =>
        new()
        {
            BotProbability = 0.9,
            Confidence = 0.8,
            RiskBand = RiskBand.High,
            ThreatScore = 0.5,
            ThreatBand = ThreatBand.Elevated,
            TotalProcessingTimeMs = 10,
            ContributingDetectors = new HashSet<string>(),
            Signals = new Dictionary<string, object>()
        };
}
```

- [ ] **Step 2: Modify SimulationPackResponder**

In `Mostlylucid.BotDetection/SimulationPacks/SimulationPackResponder.cs`:

Add `IHolodeckResponder?` as an optional constructor parameter. Since `IHolodeckResponder` is defined in the `Llm.Holodeck` package which the core project doesn't reference, we need a different approach.

**Better approach:** Define `IHolodeckResponder` in the core project so `SimulationPackResponder` can reference it without depending on the LLM package.

Move the interface to `Mostlylucid.BotDetection/SimulationPacks/IHolodeckResponder.cs`:

```csharp
namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     Generates dynamic fake responses for holodeck honeypots.
///     Registered optionally — SimulationPackResponder resolves via GetService
///     and falls back to static templates when null or unavailable.
/// </summary>
public interface IHolodeckResponder
{
    Task<HolodeckResponse> GenerateAsync(
        PackResponseTemplate template,
        HolodeckRequestContext requestContext,
        string? canary,
        CancellationToken ct = default);

    bool IsAvailable { get; }
}

public sealed record HolodeckResponse
{
    public required string Content { get; init; }
    public required string ContentType { get; init; }
    public int StatusCode { get; init; } = 200;
    public Dictionary<string, string>? Headers { get; init; }
    public bool WasGenerated { get; init; }
}

public sealed record HolodeckRequestContext
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? QueryString { get; init; }
    public string? ContentType { get; init; }
    public string? Fingerprint { get; init; }
    public string? PackId { get; init; }
    public string? PackFramework { get; init; }
    public string? PackVersion { get; init; }
    public string? PackPersonality { get; init; }
}
```

Then update `Mostlylucid.BotDetection.Llm.Holodeck/IHolodeckResponder.cs` to just re-export (or delete it and have `LlmHolodeckResponder` implement the core interface directly).

Update `SimulationPackResponder` constructor to accept optional `IHolodeckResponder?`:

```csharp
public class SimulationPackResponder : IActionPolicy
{
    private readonly ISimulationPackRegistry _registry;
    private readonly ILogger<SimulationPackResponder> _logger;
    private readonly IHolodeckResponder? _holodeckResponder;

    public SimulationPackResponder(
        ISimulationPackRegistry registry,
        ILogger<SimulationPackResponder> logger,
        IHolodeckResponder? holodeckResponder = null)
    {
        _registry = registry;
        _logger = logger;
        _holodeckResponder = holodeckResponder;
    }
```

In `ExecuteAsync`, replace the body-writing section (lines 85-88) with:

```csharp
        // Serve response — dynamic (LLM) or static
        if (template.Dynamic && _holodeckResponder?.IsAvailable == true)
        {
            var requestCtx = new HolodeckRequestContext
            {
                Method = context.Request.Method,
                Path = path,
                QueryString = context.Request.QueryString.Value,
                ContentType = context.Request.ContentType,
                Fingerprint = evidence.Signals.TryGetValue("identity.primary_signature", out var sig) ? sig?.ToString() : null,
                PackId = matchedPack.Id,
                PackFramework = matchedPack.Framework,
                PackVersion = matchedPack.Version,
                PackPersonality = matchedPack.PromptPersonality
            };

            // Get canary from beacon generator if available
            var canaryGenerator = context.RequestServices.GetService<BeaconCanaryGenerator>();
            string? canary = null;
            if (canaryGenerator != null && requestCtx.Fingerprint != null)
                canary = canaryGenerator.Generate(requestCtx.Fingerprint, path);

            var holoResponse = await _holodeckResponder.GenerateAsync(template, requestCtx, canary, cancellationToken);
            await context.Response.WriteAsync(holoResponse.Content, cancellationToken);

            // Store beacon if canary was generated
            if (canary != null)
            {
                var beaconStore = context.RequestServices.GetService<BeaconStore>();
                if (beaconStore != null)
                {
                    var ttl = TimeSpan.FromHours(24); // match beacon TTL
                    await beaconStore.StoreAsync(canary, requestCtx.Fingerprint!, path, matchedPack.Id, ttl);
                }
            }
        }
        else
        {
            // Static fallback — embed canary via placeholder replacement
            var body = template.Body ?? "";
            var canaryGenerator = context.RequestServices.GetService<BeaconCanaryGenerator>();
            if (canaryGenerator != null)
            {
                var fingerprint = evidence.Signals.TryGetValue("identity.primary_signature", out var sig) ? sig?.ToString() : null;
                if (fingerprint != null)
                {
                    var canary = canaryGenerator.Generate(fingerprint, path);
                    body = body.Replace("{{nonce}}", canary)
                               .Replace("{{api_key}}", canary)
                               .Replace("{{token}}", canary);
                }
            }
            await context.Response.WriteAsync(body, cancellationToken);
        }
```

Add needed usings to `SimulationPackResponder.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.ApiHolodeck.Services;
```

Wait — the core project should NOT reference ApiHolodeck. `BeaconCanaryGenerator` and `BeaconStore` live in ApiHolodeck. Use service locator with `GetService` by type name, or move the beacon types to core.

**Simpler approach:** Resolve `BeaconCanaryGenerator` and `BeaconStore` dynamically via `context.RequestServices`. Since `SimulationPackResponder` already has `HttpContext`, it can use `GetService<T>()`. The types resolve at runtime only when registered — no compile-time dependency needed. But we still need `using` for the types.

**Cleanest approach:** Pass canary as a parameter from the middleware that already has access to beacon services. The `HandleBlockedRequest` method in `BotDetectionMiddleware` already knows the fingerprint and has access to all services. It can compute the canary and pass it via `HttpContext.Items["Holodeck.Canary"]`.

Let me simplify: `SimulationPackResponder` reads `HttpContext.Items["Holodeck.Canary"]` (set by the middleware). No new dependencies.

```csharp
        var canary = context.Items.TryGetValue("Holodeck.Canary", out var c) ? c as string : null;

        if (template.Dynamic && _holodeckResponder?.IsAvailable == true)
        {
            var requestCtx = new HolodeckRequestContext
            {
                Method = context.Request.Method,
                Path = path,
                QueryString = context.Request.QueryString.Value,
                ContentType = context.Request.ContentType,
                Fingerprint = context.Items.TryGetValue("Holodeck.Fingerprint", out var fp) ? fp as string : null,
                PackId = matchedPack.Id,
                PackFramework = matchedPack.Framework,
                PackVersion = matchedPack.Version,
                PackPersonality = matchedPack.PromptPersonality
            };

            var holoResponse = await _holodeckResponder.GenerateAsync(template, requestCtx, canary, cancellationToken);
            await context.Response.WriteAsync(holoResponse.Content, cancellationToken);
        }
        else
        {
            var body = template.Body ?? "";
            if (canary != null)
            {
                body = body.Replace("{{nonce}}", canary)
                           .Replace("{{api_key}}", canary)
                           .Replace("{{token}}", canary);
            }
            await context.Response.WriteAsync(body, cancellationToken);
        }
```

The middleware (`BotDetectionMiddleware.HandleBlockedRequest`) sets:
- `context.Items["Holodeck.Canary"]` = canary string
- `context.Items["Holodeck.Fingerprint"]` = fingerprint string

This keeps all dependency boundaries clean.

- [ ] **Step 3: Run tests**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "SimulationPackResponderDynamicTests" -v m
```

- [ ] **Step 4: Build full solution**

```bash
dotnet build mostlylucid.stylobot.sln --no-restore -v:minimal
```

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection/SimulationPacks/SimulationPackResponder.cs \
       Mostlylucid.BotDetection/SimulationPacks/IHolodeckResponder.cs \
       Mostlylucid.BotDetection/SimulationPacks/SimulationPack.cs \
       Mostlylucid.BotDetection.Test/Holodeck/SimulationPackResponderDynamicTests.cs
git commit -m "Wire IHolodeckResponder into SimulationPackResponder for dynamic templates"
```

---

## Task 8: Update middleware to set canary items + full verification

**Files:**
- Modify: `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`

- [ ] **Step 1: Set canary and fingerprint in HandleBlockedRequest**

In the holodeck engagement check block in `HandleBlockedRequest` (added in the earlier holodeck rearchitecture), BEFORE calling the holodeck policy, compute and set canary:

```csharp
        if (isHoneypotPath || hasAttackSignal)
        {
            var holodeckPolicy = actionPolicyRegistry.GetPolicy("holodeck");
            if (holodeckPolicy != null || actionPolicyRegistry.GetPolicy("simulation-pack") != null)
            {
                // Compute fingerprint and canary for beacon tracking
                var fingerprint = aggregated.Signals.TryGetValue(SignalKeys.PrimarySignature, out var sigVal)
                    ? sigVal?.ToString() : null;

                if (fingerprint != null)
                {
                    context.Items["Holodeck.Fingerprint"] = fingerprint;

                    // Try to compute canary (BeaconCanaryGenerator may not be registered)
                    var canaryGen = context.RequestServices.GetService(
                        Type.GetType("Mostlylucid.BotDetection.ApiHolodeck.Services.BeaconCanaryGenerator, Mostlylucid.BotDetection.ApiHolodeck"));
                    if (canaryGen != null)
                    {
                        var generateMethod = canaryGen.GetType().GetMethod("Generate");
                        if (generateMethod != null)
                        {
                            var canary = generateMethod.Invoke(canaryGen, [fingerprint, context.Request.Path.Value ?? "/"]) as string;
                            if (canary != null)
                                context.Items["Holodeck.Canary"] = canary;
                        }
                    }
                }
```

Wait — reflection is ugly. Better: define a simple `IBeaconCanaryGenerator` interface in the core project:

Actually even simpler: the `SimulationPackResponder` or `HolodeckActionPolicy` can set the canary itself since it has access to DI. The middleware just needs to set the fingerprint. Let me simplify.

The middleware sets `context.Items["Holodeck.Fingerprint"]` from `PrimarySignature`. The `SimulationPackResponder` (or whatever action policy fires) computes the canary if `BeaconCanaryGenerator` is available via `context.RequestServices.GetService<BeaconCanaryGenerator>()`.

But that creates the dependency problem again. The CLEANEST solution: define a tiny `ICanaryGenerator` interface in core:

```csharp
// In Mostlylucid.BotDetection/SimulationPacks/ICanaryGenerator.cs
namespace Mostlylucid.BotDetection.SimulationPacks;

public interface ICanaryGenerator
{
    string Generate(string fingerprint, string path);
}
```

Then `BeaconCanaryGenerator` implements it, and `SimulationPackResponder` resolves `ICanaryGenerator?` from DI.

- [ ] **Step 2: Create ICanaryGenerator in core**

Create `Mostlylucid.BotDetection/SimulationPacks/ICanaryGenerator.cs`:

```csharp
namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     Generates deterministic canary values for beacon tracking.
///     Implemented by ApiHolodeck's BeaconCanaryGenerator.
/// </summary>
public interface ICanaryGenerator
{
    string Generate(string fingerprint, string path);
}
```

Make `BeaconCanaryGenerator` implement it — add to its class declaration:

```csharp
public sealed class BeaconCanaryGenerator : ICanaryGenerator
```

Register it in `AddApiHolodeck`:

```csharp
services.AddSingleton<ICanaryGenerator>(sp => sp.GetRequiredService<BeaconCanaryGenerator>());
```

- [ ] **Step 3: Set fingerprint in middleware**

In `BotDetectionMiddleware.HandleBlockedRequest`, in the holodeck check block, add:

```csharp
        if (isHoneypotPath || hasAttackSignal)
        {
            // Set fingerprint for downstream action policies
            var fingerprint = aggregated.Signals.TryGetValue(SignalKeys.PrimarySignature, out var sigVal)
                ? sigVal?.ToString() : null;
            if (fingerprint != null)
                context.Items["Holodeck.Fingerprint"] = fingerprint;

            // ... existing holodeck policy resolution ...
```

- [ ] **Step 4: Update SimulationPackResponder to use ICanaryGenerator**

Update `SimulationPackResponder` constructor to accept `ICanaryGenerator?`:

```csharp
    private readonly ICanaryGenerator? _canaryGenerator;

    public SimulationPackResponder(
        ISimulationPackRegistry registry,
        ILogger<SimulationPackResponder> logger,
        IHolodeckResponder? holodeckResponder = null,
        ICanaryGenerator? canaryGenerator = null)
    {
        _registry = registry;
        _logger = logger;
        _holodeckResponder = holodeckResponder;
        _canaryGenerator = canaryGenerator;
    }
```

In `ExecuteAsync`, compute canary from fingerprint:

```csharp
        var fingerprint = context.Items.TryGetValue("Holodeck.Fingerprint", out var fpVal) ? fpVal as string : null;
        var canary = (fingerprint != null && _canaryGenerator != null)
            ? _canaryGenerator.Generate(fingerprint, path) : null;
```

- [ ] **Step 5: Run full test suite**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --no-restore -v:minimal
dotnet test Mostlylucid.BotDetection.Api.Tests/ --no-restore -v:minimal
dotnet test Stylobot.Gateway.Tests/ --no-restore -v:minimal
```

- [ ] **Step 6: Build commercial**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot-commercial && dotnet build Stylobot.Commercial.slnx --no-restore -v:minimal
```

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Complete LLM holodeck wiring: ICanaryGenerator interface, fingerprint propagation, SimulationPackResponder dynamic+static paths"
```
