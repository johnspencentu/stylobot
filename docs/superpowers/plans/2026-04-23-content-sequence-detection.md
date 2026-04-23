# Content Sequence Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Spread detection budget across the natural request sequence following a document hit, reducing first-request latency while using Markov chain centroid data to detect divergence and escalate on later requests.

**Architecture:** A new `ContentSequenceContributor` (Priority 4) writes `sequence.*` signals to the blackboard on every request. Expensive detectors read these signals via `AnyOfTrigger` conditions and skip when the visitor is on-track early in a sequence. Divergence from the expected centroid chain triggers immediate escalation. Zero orchestrator changes.

**Tech Stack:** C# / .NET 10, `Microsoft.Data.Sqlite`, xUnit, `DefaultHttpContext` (for tests), existing `TriggerCondition` / `ConfiguredContributorBase` / `BlackboardState` infrastructure.

**Spec:** `docs/superpowers/specs/2026-04-23-content-sequence-detection-design.md`

---

## File Map

| Action | File |
|--------|------|
| Modify | `Mostlylucid.BotDetection/Orchestration/IContributingDetector.cs` |
| Create | `Mostlylucid.BotDetection/Markov/RequestMarkovClassifier.cs` |
| Modify | `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs` |
| Modify | `Mostlylucid.BotDetection/Models/DetectionContext.cs` |
| Create | `Mostlylucid.BotDetection/Services/SequenceContextStore.cs` |
| Create | `Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs` |
| Create | `Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs` |
| Create | `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs` |
| Create | `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/contentsequence.detector.yaml` |
| Modify | `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs` |
| Modify | `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/PeriodicityContributor.cs` |
| Modify | `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/BehavioralWaveformContributor.cs` |
| Modify | `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/StreamAbuseContributor.cs` |
| Modify | `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` |
| Modify | `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs` |
| Create | `Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs` |
| Create | `Mostlylucid.BotDetection.Test/Services/SequenceContextStoreTests.cs` |
| Create | `Mostlylucid.BotDetection.Test/Markov/RequestMarkovClassifierTests.cs` |

---

## Task 1: Add `SignalNotExistsTrigger`

**Files:**
- Modify: `Mostlylucid.BotDetection/Orchestration/IContributingDetector.cs` (after line ~92, after `SignalExistsTrigger`)

The existing trigger system has `SignalExistsTrigger` but no inverse. Several detectors need "skip if signal X is present" semantics. Add the inverse trigger.

- [ ] **Step 1: Add the new trigger type**

Open `Mostlylucid.BotDetection/Orchestration/IContributingDetector.cs`. After the closing `}` of `SignalExistsTrigger` (around line 92), add:

```csharp
/// <summary>
///     Trigger when a specific signal key does NOT exist.
///     Use to skip a detector when a controlling signal has been written.
/// </summary>
public sealed record SignalNotExistsTrigger(string SignalKey) : TriggerCondition
{
    public override string Description => $"Signal '{SignalKey}' does not exist";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return !signals.ContainsKey(SignalKey);
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Mostlylucid.BotDetection/Orchestration/IContributingDetector.cs
git commit -m "feat: add SignalNotExistsTrigger for sequence-aware skip logic"
```

---

## Task 2: Extract `RequestMarkovClassifier`

**Files:**
- Create: `Mostlylucid.BotDetection/Markov/RequestMarkovClassifier.cs`
- Modify: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs`
- Create: `Mostlylucid.BotDetection.Test/Markov/RequestMarkovClassifierTests.cs`

`ClassifyRequestState` is currently `private static` in `SessionVectorContributor`. Both `ContentSequenceContributor` and `SessionVectorContributor` need to use the same classification logic. Extract it to a shared static helper.

- [ ] **Step 1: Create `RequestMarkovClassifier.cs`**

Create `Mostlylucid.BotDetection/Markov/RequestMarkovClassifier.cs`:

```csharp
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Markov;

/// <summary>
///     Classifies an HTTP request into a <see cref="RequestState"/> for Markov chain tracking.
///     Shared by <c>SessionVectorContributor</c> and <c>ContentSequenceContributor</c>
///     so both use identical classification logic.
/// </summary>
public static class RequestMarkovClassifier
{
    /// <summary>
    ///     Maps the current request into a Markov state based on transport, path, and response signals.
    ///     Identical logic to the former SessionVectorContributor.ClassifyRequestState().
    /// </summary>
    public static RequestState Classify(BlackboardState state)
    {
        var context = state.HttpContext;
        var request = context.Request;

        // Transport-level classification (highest priority)
        var isSignalR = state.GetSignal<bool?>(SignalKeys.TransportIsSignalR) ?? false;
        var isUpgrade = state.GetSignal<bool?>(SignalKeys.TransportIsUpgrade) ?? false;

        if (isSignalR) return RequestState.SignalR;
        if (isUpgrade) return RequestState.WebSocket;

        var acceptHeader = request.Headers.Accept.ToString();
        if (acceptHeader.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return RequestState.ServerSentEvent;

        // Response-based classification
        var statusCode = context.Response.StatusCode;
        if (statusCode == 401 || statusCode == 403)
            return RequestState.AuthAttempt;
        if (statusCode == 404)
            return RequestState.NotFound;

        // Content-type classification from transport signal
        var protocolClass = state.GetSignal<string>(SignalKeys.TransportProtocolClass);
        if (protocolClass == "api") return RequestState.ApiCall;
        if (protocolClass == "static") return RequestState.StaticAsset;

        // Method + content heuristics
        if (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method))
        {
            var contentType = request.ContentType ?? "";
            if (contentType.Contains("form", StringComparison.OrdinalIgnoreCase))
                return RequestState.FormSubmit;
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return RequestState.ApiCall;
        }

        // Path heuristics
        var path = request.Path.Value ?? "";
        if (path.Contains("/search", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/find", StringComparison.OrdinalIgnoreCase) ||
            request.QueryString.Value?.Contains("q=", StringComparison.OrdinalIgnoreCase) == true)
            return RequestState.Search;

        // Sec-Fetch-Dest for page vs asset
        var secFetchDest = request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
        if (secFetchDest is "script" or "style" or "image" or "font")
            return RequestState.StaticAsset;

        return RequestState.PageView;
    }
}
```

- [ ] **Step 2: Update `SessionVectorContributor` to delegate to the classifier**

In `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs`, find the `ClassifyRequestState` method and replace its body with a delegation call:

```csharp
private static RequestState ClassifyRequestState(BlackboardState state)
    => RequestMarkovClassifier.Classify(state);
```

Add `using Mostlylucid.BotDetection.Markov;` at the top if not present.

- [ ] **Step 3: Write failing tests**

Create `Mostlylucid.BotDetection.Test/Markov/RequestMarkovClassifierTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Markov;

public class RequestMarkovClassifierTests
{
    private static BlackboardState BuildState(
        Action<DefaultHttpContext>? configureHttp = null,
        Dictionary<string, object>? signals = null)
    {
        var ctx = new DefaultHttpContext();
        configureHttp?.Invoke(ctx);
        var signalDict = new ConcurrentDictionary<string, object>(
            signals ?? new Dictionary<string, object>());
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signalDict,
            SignalWriter = signalDict,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = "test",
            Elapsed = TimeSpan.Zero
        };
    }

    [Fact]
    public void SignalR_transport_signal_returns_SignalR()
    {
        var state = BuildState(signals: new()
        {
            [SignalKeys.TransportIsSignalR] = (bool?)true
        });
        Assert.Equal(RequestState.SignalR, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void WebSocket_upgrade_returns_WebSocket()
    {
        var state = BuildState(signals: new()
        {
            [SignalKeys.TransportIsUpgrade] = (bool?)true
        });
        Assert.Equal(RequestState.WebSocket, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Static_protocol_class_returns_StaticAsset()
    {
        var state = BuildState(signals: new()
        {
            [SignalKeys.TransportProtocolClass] = "static"
        });
        Assert.Equal(RequestState.StaticAsset, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Api_protocol_class_returns_ApiCall()
    {
        var state = BuildState(signals: new()
        {
            [SignalKeys.TransportProtocolClass] = "api"
        });
        Assert.Equal(RequestState.ApiCall, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void Default_GET_returns_PageView()
    {
        var state = BuildState(ctx => ctx.Request.Method = "GET");
        Assert.Equal(RequestState.PageView, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void SecFetchDest_script_returns_StaticAsset()
    {
        var state = BuildState(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Headers["Sec-Fetch-Dest"] = "script";
        });
        Assert.Equal(RequestState.StaticAsset, RequestMarkovClassifier.Classify(state));
    }

    [Fact]
    public void NotFound_status_returns_NotFound()
    {
        var state = BuildState(ctx => ctx.Response.StatusCode = 404);
        Assert.Equal(RequestState.NotFound, RequestMarkovClassifier.Classify(state));
    }
}
```

- [ ] **Step 4: Run tests — expect them to pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~RequestMarkovClassifierTests" -v
```

Expected: All 7 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection/Markov/RequestMarkovClassifier.cs \
        Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs \
        Mostlylucid.BotDetection.Test/Markov/RequestMarkovClassifierTests.cs
git commit -m "refactor: extract RequestMarkovClassifier shared helper from SessionVectorContributor"
```

---

## Task 3: Add Signal Keys

**Files:**
- Modify: `Mostlylucid.BotDetection/Models/DetectionContext.cs`

- [ ] **Step 1: Add sequence signal key constants**

Open `Mostlylucid.BotDetection/Models/DetectionContext.cs`. Find the `SignalKeys` static class. After the last existing constant block, add:

```csharp
// ==========================================
// Content Sequence signals
// Written by ContentSequenceContributor (Priority 4).
// Consumed by deferred detectors via TriggerConditions.
// ==========================================

/// <summary>Int: current position in the request sequence (0 = document hit).</summary>
public const string SequencePosition = "sequence.position";

/// <summary>Bool: true while actual requests match the expected Markov chain.</summary>
public const string SequenceOnTrack = "sequence.on_track";

/// <summary>Bool: true once the sequence has diverged from the expected chain.</summary>
public const string SequenceDiverged = "sequence.diverged";

/// <summary>Double: 0.0-1.0 divergence score for the current request.</summary>
public const string SequenceDivergenceScore = "sequence.divergence_score";

/// <summary>Int: sequence position at which the first divergence occurred.</summary>
public const string SequenceDivergenceAtPosition = "sequence.divergence_at_position";

/// <summary>String: UUID identifying the current content sequence context.</summary>
public const string SequenceChainId = "sequence.chain_id";

/// <summary>String: centroid classification — "Unknown", "Human", or "Bot".</summary>
public const string SequenceCentroidType = "sequence.centroid_type";

/// <summary>String: path of the document that started this sequence.</summary>
public const string SequenceContentPath = "sequence.content_path";

/// <summary>Bool: true when SignalR is the expected next Markov state and centroid is not Bot.</summary>
public const string SequenceSignalRExpected = "sequence.signalr_expected";

/// <summary>Bool: true when a prefetch request (Purpose: prefetch) is observed in the sequence.</summary>
public const string SequencePrefetchDetected = "sequence.prefetch_detected";

/// <summary>Bool: true when no static assets appeared in the critical window — cache warm hit.</summary>
public const string SequenceCacheWarm = "sequence.cache_warm";
```

- [ ] **Step 2: Build to verify no typos**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Mostlylucid.BotDetection/Models/DetectionContext.cs
git commit -m "feat: add sequence.* signal keys for content sequence detection"
```

---

## Task 4: `SequenceContextStore`

**Files:**
- Create: `Mostlylucid.BotDetection/Services/SequenceContextStore.cs`
- Create: `Mostlylucid.BotDetection.Test/Services/SequenceContextStoreTests.cs`

- [ ] **Step 1: Write the failing tests first**

Create `Mostlylucid.BotDetection.Test/Services/SequenceContextStoreTests.cs`:

```csharp
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class SequenceContextStoreTests
{
    [Fact]
    public void GetOrCreate_new_signature_creates_context_at_position_zero()
    {
        var store = new SequenceContextStore();
        var ctx = store.GetOrCreate("sig1");
        Assert.Equal(0, ctx.Position);
        Assert.False(ctx.HasDiverged);
        Assert.Equal(CentroidType.Unknown, ctx.CentroidType);
    }

    [Fact]
    public void GetOrCreate_same_signature_returns_same_context()
    {
        var store = new SequenceContextStore();
        var ctx1 = store.GetOrCreate("sig1");
        var ctx2 = store.GetOrCreate("sig1");
        Assert.Equal(ctx1.ChainId, ctx2.ChainId);
    }

    [Fact]
    public void TryGet_unknown_signature_returns_null()
    {
        var store = new SequenceContextStore();
        var result = store.TryGet("unknown");
        Assert.Null(result);
    }

    [Fact]
    public void Update_stores_new_version()
    {
        var store = new SequenceContextStore();
        var ctx = store.GetOrCreate("sig1");
        var updated = ctx with { Position = 2, HasDiverged = true };
        store.Update("sig1", updated);
        var retrieved = store.TryGet("sig1");
        Assert.NotNull(retrieved);
        Assert.Equal(2, retrieved.Position);
        Assert.True(retrieved.HasDiverged);
    }

    [Fact]
    public void Expire_removes_stale_entries()
    {
        var store = new SequenceContextStore();
        // Create with an old LastRequest timestamp (40 minutes ago)
        var ctx = store.GetOrCreate("sig1");
        var stale = ctx with { LastRequest = DateTimeOffset.UtcNow.AddMinutes(-40) };
        store.Update("sig1", stale);

        store.EvictExpired(TimeSpan.FromMinutes(30));

        Assert.Null(store.TryGet("sig1"));
    }

    [Fact]
    public void Expire_keeps_fresh_entries()
    {
        var store = new SequenceContextStore();
        store.GetOrCreate("sig1"); // LastRequest = now
        store.EvictExpired(TimeSpan.FromMinutes(30));
        Assert.NotNull(store.TryGet("sig1"));
    }

    [Fact]
    public void ExpiredContext_returns_new_on_GetOrCreate()
    {
        var store = new SequenceContextStore();
        var ctx = store.GetOrCreate("sig1");
        var originalChainId = ctx.ChainId;
        // Mark as expired
        var stale = ctx with { LastRequest = DateTimeOffset.UtcNow.AddMinutes(-40) };
        store.Update("sig1", stale);

        // GetOrCreate with a gap that exceeds the session timeout should create fresh
        var renewed = store.GetOrCreate("sig1", sessionGapMinutes: 30);
        Assert.NotEqual(originalChainId, renewed.ChainId);
        Assert.Equal(0, renewed.Position);
    }
}
```

- [ ] **Step 2: Run tests — expect compile errors (type not yet defined)**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SequenceContextStoreTests" 2>&1 | head -20
```

Expected: Build error — `SequenceContextStore` and `CentroidType` not found.

- [ ] **Step 3: Create `SequenceContextStore.cs`**

Create `Mostlylucid.BotDetection/Services/SequenceContextStore.cs`:

```csharp
using System.Collections.Concurrent;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Classification of a sequence context's centroid — human, bot, or unknown.
/// </summary>
public enum CentroidType { Unknown = 0, Human = 1, Bot = 2 }

/// <summary>
///     Immutable snapshot of a fingerprint's current position in its content sequence.
///     Updated on each request by ContentSequenceContributor.
/// </summary>
public sealed record SequenceContext
{
    public required string ChainId { get; init; }
    public required string Signature { get; init; }
    public string CentroidId { get; init; } = string.Empty;
    public CentroidType CentroidType { get; init; } = CentroidType.Unknown;
    public int Position { get; init; }
    public RequestState[] ExpectedChain { get; init; } = Array.Empty<RequestState>();
    public double[] TypicalGapsMs { get; init; } = Array.Empty<double>();
    public double[] GapToleranceMs { get; init; } = Array.Empty<double>();
    public DateTimeOffset LastRequest { get; init; } = DateTimeOffset.UtcNow;
    public bool HasDiverged { get; init; }
    public int DivergenceCount { get; init; }
    // Set-based divergence tracking (Task 6.5)
    public HashSet<RequestState> ObservedStateSet { get; init; } = [];
    public DateTimeOffset WindowStartTime { get; init; } = DateTimeOffset.UtcNow;
    public int RequestCountInWindow { get; init; }
    public bool CacheWarm { get; init; }
}

/// <summary>
///     Transient per-fingerprint sequence state. ConcurrentDictionary backed — no SQLite.
///     Loss on restart is acceptable: fingerprints just get a fresh context.
///     TTL sweep runs every 5 minutes, evicts entries older than the session gap.
/// </summary>
public sealed class SequenceContextStore : IDisposable
{
    private readonly ConcurrentDictionary<string, SequenceContext> _contexts = new();
    private readonly Timer _sweepTimer;
    private static readonly TimeSpan DefaultSessionGap = TimeSpan.FromMinutes(30);

    public SequenceContextStore()
    {
        _sweepTimer = new Timer(
            _ => EvictExpired(DefaultSessionGap),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    /// <summary>
    ///     Get the existing context, or create a fresh one at position 0.
    ///     If the existing context is older than <paramref name="sessionGapMinutes"/>, it is
    ///     replaced with a new one (session boundary detection).
    /// </summary>
    public SequenceContext GetOrCreate(string signature, int sessionGapMinutes = 30)
    {
        if (_contexts.TryGetValue(signature, out var existing))
        {
            var gap = DateTimeOffset.UtcNow - existing.LastRequest;
            if (gap.TotalMinutes < sessionGapMinutes)
                return existing;

            // Session boundary: replace with fresh context
            var renewed = CreateFresh(signature);
            _contexts[signature] = renewed;
            return renewed;
        }

        var fresh = CreateFresh(signature);
        _contexts[signature] = fresh;
        return fresh;
    }

    /// <summary>Atomically store an updated context.</summary>
    public void Update(string signature, SequenceContext updated)
        => _contexts[signature] = updated;

    /// <summary>Retrieve without creating. Returns null if not found.</summary>
    public SequenceContext? TryGet(string signature)
        => _contexts.TryGetValue(signature, out var ctx) ? ctx : null;

    /// <summary>
    ///     Remove all entries whose <see cref="SequenceContext.LastRequest"/> is older than
    ///     <paramref name="sessionGap"/>. Called by the internal sweep timer.
    /// </summary>
    public void EvictExpired(TimeSpan sessionGap)
    {
        var cutoff = DateTimeOffset.UtcNow - sessionGap;
        foreach (var key in _contexts.Keys)
        {
            if (_contexts.TryGetValue(key, out var ctx) && ctx.LastRequest < cutoff)
                _contexts.TryRemove(key, out _);
        }
    }

    public int Count => _contexts.Count;

    public void Dispose() => _sweepTimer.Dispose();

    private static SequenceContext CreateFresh(string signature) => new()
    {
        ChainId = Guid.NewGuid().ToString("N"),
        Signature = signature
    };
}
```

- [ ] **Step 4: Run tests — expect them to pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~SequenceContextStoreTests" -v
```

Expected: All 7 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection/Services/SequenceContextStore.cs \
        Mostlylucid.BotDetection.Test/Services/SequenceContextStoreTests.cs
git commit -m "feat: add SequenceContextStore for transient per-fingerprint sequence state"
```

---

## Task 5: `CentroidSequenceStore`

**Files:**
- Create: `Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs`

This store manages two things:
1. The hard-coded **Tier 1 global chain** (used on request 1 before any cluster data is available)
2. The cluster-specific **Tier 2 chains** (persisted in SQLite, rebuilt when clustering runs)

The Tier 2 rebuilding reads `BotCluster` data from `BotClusterService` and assigns expected chains based on cluster type. For the MVP, Bot and Human clusters get different hard-coded sequences (configurable from YAML). The important part is divergence detection, not per-cluster chain accuracy — that improves over time.

- [ ] **Step 1: Create `CentroidSequenceStore.cs`**

Create `Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs`:

```csharp
using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     A cluster's expected request sequence, used by ContentSequenceContributor.
/// </summary>
public sealed record CentroidSequence
{
    public required string CentroidId { get; init; }
    public CentroidType Type { get; init; } = CentroidType.Unknown;
    public RequestState[] ExpectedStates { get; init; } = Array.Empty<RequestState>();
    public double[] TypicalGapsMs { get; init; } = Array.Empty<double>();
    public double[] GapToleranceMs { get; init; } = Array.Empty<double>();
    public int SampleSize { get; init; }
}

/// <summary>
///     Provides centroid-specific expected request chains (Tier 2) keyed by cluster ID,
///     plus a global fallback chain (Tier 1) for new fingerprints.
///     Rebuilt after each clustering run via <see cref="RebuildAsync"/>.
///     Persisted in the same SQLite DB as sessions.
/// </summary>
public sealed class CentroidSequenceStore
{
    private readonly string _connectionString;
    private readonly ILogger<CentroidSequenceStore> _logger;

    // Global fallback chain (Tier 1): typical human sequence from PageView.
    // Hard-coded sensible defaults — configurable by the caller via SetGlobalChain().
    private CentroidSequence _globalChain = new()
    {
        CentroidId = "global",
        Type = CentroidType.Unknown,
        // PageView → 3× StaticAsset → ApiCall → SignalR is a typical SPA pattern
        ExpectedStates = [
            RequestState.StaticAsset, RequestState.StaticAsset, RequestState.StaticAsset,
            RequestState.ApiCall, RequestState.SignalR
        ],
        // Typical gaps: assets load quickly (200ms), API takes longer (500ms), SignalR after that
        TypicalGapsMs = [200, 100, 100, 500, 1000],
        GapToleranceMs = [500, 300, 300, 1500, 3000],
        SampleSize = 0 // synthetic default
    };

    // Cluster-specific chains (Tier 2) swapped atomically after each rebuild
    private volatile FrozenDictionary<string, CentroidSequence> _centroidChains =
        FrozenDictionary<string, CentroidSequence>.Empty;

    // Hard-coded sequences per cluster type (Bot clusters have different expected sequences)
    private static readonly RequestState[] TypicalHumanChain =
    [
        RequestState.StaticAsset, RequestState.StaticAsset, RequestState.StaticAsset,
        RequestState.ApiCall, RequestState.SignalR
    ];
    private static readonly RequestState[] TypicalBotChain =
    [
        RequestState.ApiCall, RequestState.ApiCall, RequestState.ApiCall,
        RequestState.NotFound, RequestState.ApiCall
    ];
    private static readonly double[] DefaultHumanGapsMs = [200, 100, 100, 500, 1000];
    private static readonly double[] DefaultHumanTolerancesMs = [500, 300, 300, 1500, 3000];
    private static readonly double[] DefaultBotGapsMs = [50, 50, 50, 50, 50];
    private static readonly double[] DefaultBotTolerancesMs = [100, 100, 100, 100, 100];

    public CentroidSequenceStore(
        string connectionString,
        ILogger<CentroidSequenceStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>Returns the global fallback chain (Tier 1) for new/unknown fingerprints.</summary>
    public CentroidSequence GlobalChain => _globalChain;

    /// <summary>Override the global chain (called from ContentSequenceContributor config).</summary>
    public void SetGlobalChain(CentroidSequence chain) => _globalChain = chain;

    /// <summary>
    ///     Look up a cluster-specific chain by cluster ID. Returns null if not found or below
    ///     <paramref name="minSampleSize"/>.
    /// </summary>
    public CentroidSequence? TryGetCentroidChain(string clusterId, int minSampleSize = 20)
    {
        if (_centroidChains.TryGetValue(clusterId, out var chain) &&
            chain.SampleSize >= minSampleSize)
            return chain;
        return null;
    }

    /// <summary>
    ///     Initialize the SQLite table and load any persisted chains into memory.
    ///     Call once at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS centroid_sequences (
                centroid_id TEXT PRIMARY KEY,
                centroid_type INTEGER NOT NULL,
                sequence_json TEXT NOT NULL,
                sample_size INTEGER NOT NULL,
                computed_at TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        await LoadFromDatabaseAsync(conn, ct);
    }

    /// <summary>
    ///     Rebuild chains from the current cluster snapshot and persist to SQLite.
    ///     Called by BotClusterService.ClustersUpdated event handler.
    /// </summary>
    public async Task RebuildAsync(
        IReadOnlyList<BotCluster> clusters,
        CancellationToken ct = default)
    {
        var newChains = new Dictionary<string, CentroidSequence>(clusters.Count);

        foreach (var cluster in clusters)
        {
            var type = cluster.Type switch
            {
                BotClusterType.BotProduct => CentroidType.Bot,
                BotClusterType.BotNetwork => CentroidType.Bot,
                _ => CentroidType.Unknown
            };

            // Use cluster member count as sample size proxy
            var sampleSize = cluster.MemberCount;

            var (states, gaps, tolerances) = type == CentroidType.Bot
                ? (TypicalBotChain, DefaultBotGapsMs, DefaultBotTolerancesMs)
                : (TypicalHumanChain, DefaultHumanGapsMs, DefaultHumanTolerancesMs);

            newChains[cluster.ClusterId] = new CentroidSequence
            {
                CentroidId = cluster.ClusterId,
                Type = type,
                ExpectedStates = states,
                TypicalGapsMs = gaps,
                GapToleranceMs = tolerances,
                SampleSize = sampleSize
            };
        }

        // Atomically swap the in-memory snapshot
        _centroidChains = newChains.ToFrozenDictionary();

        // Persist to SQLite
        await PersistAsync(newChains.Values, ct);

        _logger.LogDebug("CentroidSequenceStore rebuilt with {Count} clusters", newChains.Count);
    }

    private async Task PersistAsync(IEnumerable<CentroidSequence> chains, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var chain in chains)
            {
                var json = JsonSerializer.Serialize(chain);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO centroid_sequences
                        (centroid_id, centroid_type, sequence_json, sample_size, computed_at)
                    VALUES
                        (@id, @type, @json, @size, @at)
                    ON CONFLICT(centroid_id) DO UPDATE SET
                        centroid_type = excluded.centroid_type,
                        sequence_json = excluded.sequence_json,
                        sample_size = excluded.sample_size,
                        computed_at = excluded.computed_at;
                    """;
                cmd.Parameters.AddWithValue("@id", chain.CentroidId);
                cmd.Parameters.AddWithValue("@type", (int)chain.Type);
                cmd.Parameters.AddWithValue("@json", json);
                cmd.Parameters.AddWithValue("@size", chain.SampleSize);
                cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task LoadFromDatabaseAsync(SqliteConnection conn, CancellationToken ct)
    {
        var chains = new Dictionary<string, CentroidSequence>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sequence_json FROM centroid_sequences;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            try
            {
                var json = reader.GetString(0);
                var chain = JsonSerializer.Deserialize<CentroidSequence>(json);
                if (chain != null)
                    chains[chain.CentroidId] = chain;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize centroid sequence");
            }
        }

        _centroidChains = chains.ToFrozenDictionary();
        _logger.LogDebug("CentroidSequenceStore loaded {Count} chains from DB", chains.Count);
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs
git commit -m "feat: add CentroidSequenceStore for cluster-specific expected request chains"
```

---

## Task 6: `ContentSequenceContributor` + YAML

**Files:**
- Create: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs`
- Create: `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/contentsequence.detector.yaml`

- [ ] **Step 1: Create the YAML manifest**

Create `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/contentsequence.detector.yaml`:

```yaml
# Content Sequence Detector
# Tracks request sequences following a document hit and writes sequence.* signals
# that allow expensive detectors to defer until they have meaningful data.
name: ContentSequenceContributor
priority: 4
enabled: true
description: Tracks content request sequences and writes signals for deferred detection

scope:
  sink: botdetection
  coordinator: detection
  atom: sequence

taxonomy:
  kind: sequencer
  determinism: deterministic
  persistence: ephemeral

input:
  accepts:
    - type: botdetection.request
      required: true
  required_signals: []
  optional_signals:
    - detection.primary_signature
    - similarity.cluster_id

output:
  signals:
    - key: sequence.position
      entity_type: integer
    - key: sequence.on_track
      entity_type: boolean
    - key: sequence.diverged
      entity_type: boolean
    - key: sequence.divergence_score
      entity_type: number
    - key: sequence.chain_id
      entity_type: string
    - key: sequence.centroid_type
      entity_type: string
    - key: sequence.content_path
      entity_type: string
    - key: sequence.signalr_expected
      entity_type: boolean

triggers:
  requires: []
  skip_when: []

lane:
  name: fast
  max_concurrency: 16
  priority: 96

defaults:
  weights:
    base: 0.0
    bot_signal: 0.5
    human_signal: 0.5
    verified: 0.0
    early_exit: 0.0

  confidence:
    neutral: 0.0
    bot_detected: 0.25
    human_indicated: -0.1
    strong_signal: 0.4
    high_threshold: 0.7
    low_threshold: 0.2
    escalation_threshold: 0.0

  timing:
    timeout_ms: 5
    cache_refresh_sec: 0

  features:
    detailed_logging: false
    enable_cache: false
    can_early_exit: false
    can_escalate: false

  parameters:
    # Position threshold before deferred detectors trigger
    deferred_detector_min_position: 3
    # Divergence score (0.0-1.0) to mark sequence.diverged=true
    divergence_threshold: 0.4
    # Timing tolerance = centroid_gap_ms * this multiplier
    timing_tolerance_multiplier: 3.0
    # Minimum cluster sample size to use Tier 2 chain
    min_centroid_sample_size: 20
    # Session gap (minutes) to reset sequence context
    session_gap_minutes: 30
    # Maximum positions to track (prevents unbounded accumulation)
    max_tracked_positions: 20

tags:
  - fast-path
  - sequence
  - stage-0
```

- [ ] **Step 2: Create `ContentSequenceContributor.cs`**

Create `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Tracks the natural request sequence following a document (HTML page) hit.
///     On request 1: detects the document, creates a SequenceContext, loads the global
///     expected chain (Tier 1), writes sequence.position=0 and sequence.on_track=true.
///     Expensive detectors (SessionVector, Periodicity, etc.) see sequence.on_track=true
///     at position 0 and skip via their AnyOfTrigger conditions.
///     On requests 2-N: advances the position, compares actual Markov state to expected,
///     writes divergence signals if the sequence deviates. Divergence triggers the full
///     pipeline on the next wave.
///     Configuration loaded from: contentsequence.detector.yaml
/// </summary>
public class ContentSequenceContributor : ConfiguredContributorBase
{
    private readonly SequenceContextStore _contextStore;
    private readonly CentroidSequenceStore _centroidStore;
    private readonly BotClusterService? _clusterService;
    private readonly ILogger<ContentSequenceContributor> _logger;

    public ContentSequenceContributor(
        ILogger<ContentSequenceContributor> logger,
        IDetectorConfigProvider configProvider,
        SequenceContextStore contextStore,
        CentroidSequenceStore centroidStore,
        BotClusterService? clusterService = null)
        : base(configProvider)
    {
        _logger = logger;
        _contextStore = contextStore;
        _centroidStore = centroidStore;
        _clusterService = clusterService;
    }

    public override string Name => "ContentSequence";
    public override int Priority => Manifest?.Priority ?? 4;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config params
    private int DeferredDetectorMinPosition => GetParam("deferred_detector_min_position", 3);
    private double DivergenceThreshold => GetParam("divergence_threshold", 0.4);
    private double TimingToleranceMultiplier => GetParam("timing_tolerance_multiplier", 3.0);
    private int MinCentroidSampleSize => GetParam("min_centroid_sample_size", 20);
    private int SessionGapMinutes => GetParam("session_gap_minutes", 30);
    private int MaxTrackedPositions => GetParam("max_tracked_positions", 20);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
            if (string.IsNullOrEmpty(signature))
            {
                // No signature yet — write sequence.position not present, deferred detectors run normally
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
            }

            var isDocument = IsDocumentRequest(state);
            var ctx = _contextStore.GetOrCreate(signature, SessionGapMinutes);

            if (ctx.Position == 0 && !isDocument)
            {
                // Non-document entry point (e.g., direct API call, bot starting mid-sequence)
                // Do not write sequence signals — deferred detectors will run via NotExists fallback
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
            }

            if (isDocument && ctx.Position == 0)
            {
                // Request 1: document hit — start the sequence
                ctx = HandleDocumentHit(state, signature, ctx);
            }
            else if (ctx.Position > 0 && ctx.Position < MaxTrackedPositions)
            {
                // Requests 2-N: advance the sequence
                ctx = HandleContinuation(state, signature, ctx);
            }
            else if (ctx.Position >= MaxTrackedPositions)
            {
                // Sequence too long — stop tracking, let all detectors run freely
                state.WriteSignal(SignalKeys.SequencePosition, ctx.Position);
                state.WriteSignal(SignalKeys.SequenceOnTrack, false);
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
            }

            _contextStore.Update(signature, ctx);
            WriteSignals(state, ctx);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ContentSequenceContributor error — deferred detectors will run normally");
            // Do NOT write sequence signals on error; deferred detectors use NotExists fallback
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    /// <summary>
    ///     Returns true if this request is an HTML document navigation.
    ///     Checked in priority order — Sec-Fetch-Mode is most reliable.
    /// </summary>
    private static bool IsDocumentRequest(BlackboardState state)
    {
        var request = state.HttpContext.Request;

        // 1. Sec-Fetch-Mode: navigate (modern browsers, most reliable)
        var secFetchMode = request.Headers["Sec-Fetch-Mode"].FirstOrDefault();
        if (string.Equals(secFetchMode, "navigate", StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Accept: text/html + GET (fallback for older clients)
        if (HttpMethods.IsGet(request.Method))
        {
            var accept = request.Headers.Accept.ToString();
            if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 3. Opportunistic: transport.protocol_class if already written in this wave
        var protocolClass = state.GetSignal<string>(SignalKeys.TransportProtocolClass);
        if (string.Equals(protocolClass, "document", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private SequenceContext HandleDocumentHit(
        BlackboardState state,
        string signature,
        SequenceContext ctx)
    {
        var chain = _centroidStore.GlobalChain;

        // Check if this signature already belongs to a known cluster (for Tier 2)
        if (_clusterService != null)
        {
            var cluster = _clusterService.FindCluster(signature);
            if (cluster != null)
            {
                var tier2 = _centroidStore.TryGetCentroidChain(cluster.ClusterId, MinCentroidSampleSize);
                if (tier2 != null)
                {
                    chain = tier2;
                    ctx = ctx with
                    {
                        CentroidId = cluster.ClusterId,
                        CentroidType = tier2.Type
                    };
                }
            }
        }

        return ctx with
        {
            Position = 0,
            ExpectedChain = chain.ExpectedStates,
            TypicalGapsMs = chain.TypicalGapsMs,
            GapToleranceMs = chain.GapToleranceMs,
            LastRequest = DateTimeOffset.UtcNow,
            HasDiverged = false,
            DivergenceCount = 0
        };
    }

    private SequenceContext HandleContinuation(
        BlackboardState state,
        string signature,
        SequenceContext ctx)
    {
        // Try to upgrade to Tier 2 if centroid not yet resolved but cluster is now known
        if (ctx.CentroidType == CentroidType.Unknown && _clusterService != null)
        {
            var cluster = _clusterService.FindCluster(signature);
            if (cluster != null)
            {
                var tier2 = _centroidStore.TryGetCentroidChain(cluster.ClusterId, MinCentroidSampleSize);
                if (tier2 != null)
                {
                    ctx = ctx with
                    {
                        CentroidId = cluster.ClusterId,
                        CentroidType = tier2.Type,
                        ExpectedChain = tier2.ExpectedStates,
                        TypicalGapsMs = tier2.TypicalGapsMs,
                        GapToleranceMs = tier2.GapToleranceMs
                    };
                }
            }
        }

        var actualState = RequestMarkovClassifier.Classify(state);
        var position = ctx.Position + 1;
        var gapMs = (DateTimeOffset.UtcNow - ctx.LastRequest).TotalMilliseconds;

        var divergenceScore = ComputeDivergenceScore(ctx, actualState, position, gapMs);
        var diverged = divergenceScore >= DivergenceThreshold;

        return ctx with
        {
            Position = position,
            LastRequest = DateTimeOffset.UtcNow,
            HasDiverged = ctx.HasDiverged || diverged,
            DivergenceCount = ctx.DivergenceCount + (diverged ? 1 : 0)
        };
    }

    private double ComputeDivergenceScore(
        SequenceContext ctx,
        RequestState actualState,
        int position,
        double gapMs)
    {
        double score = 0.0;

        // State mismatch
        if (position < ctx.ExpectedChain.Length && actualState != ctx.ExpectedChain[position])
            score += 0.5;

        // Timing anomaly
        if (position < ctx.TypicalGapsMs.Length)
        {
            var expectedGap = ctx.TypicalGapsMs[position];
            var tolerance = ctx.GapToleranceMs.Length > position
                ? ctx.GapToleranceMs[position]
                : expectedGap * TimingToleranceMultiplier;
            var deviation = Math.Abs(gapMs - expectedGap);
            if (deviation > tolerance)
                score += 0.3 * Math.Min(1.0, deviation / (tolerance * 2));
        }

        // Machine-speed timing (< 20ms is almost certainly automated)
        if (gapMs < 20)
            score += 0.4;

        return Math.Min(1.0, score);
    }

    private void WriteSignals(BlackboardState state, SequenceContext ctx)
    {
        state.WriteSignal(SignalKeys.SequencePosition, ctx.Position);
        state.WriteSignal(SignalKeys.SequenceOnTrack, !ctx.HasDiverged);
        state.WriteSignal(SignalKeys.SequenceChainId, ctx.ChainId);
        state.WriteSignal(SignalKeys.SequenceCentroidType, ctx.CentroidType.ToString());
        state.WriteSignal(SignalKeys.SequenceContentPath,
            state.HttpContext.Request.Path.Value ?? string.Empty);

        if (ctx.HasDiverged)
        {
            state.WriteSignal(SignalKeys.SequenceDiverged, true);
            // Position where divergence first occurred (approximate: DivergenceCount > 0 → at current or earlier)
            state.WriteSignal(SignalKeys.SequenceDivergenceAtPosition, ctx.Position);
        }

        // Signal that SignalR is the expected next state AND centroid is not Bot
        var nextPosition = ctx.Position + 1;
        if (nextPosition < ctx.ExpectedChain.Length &&
            ctx.ExpectedChain[nextPosition] == RequestState.SignalR &&
            ctx.CentroidType != CentroidType.Bot)
        {
            state.WriteSignal(SignalKeys.SequenceSignalRExpected, true);
        }
    }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs \
        Mostlylucid.BotDetection/Orchestration/Manifests/detectors/contentsequence.detector.yaml
git commit -m "feat: add ContentSequenceContributor for sequence-aware detection"
```

---

## Task 6.5: Set-Based Divergence + Prefetch/Cache Tolerance

**Files:**
- Modify: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs`
- Modify: `Mostlylucid.BotDetection/Services/SequenceContextStore.cs`

The naive `ComputeDivergenceScore` from Task 6 compares `actualState == expectedChain[position]` one-to-one. This misclassifies:
- **Cache-warm browsers** that skip the static asset burst (assets served from cache → no request)
- **HTTP/2 parallel bursts** where assets arrive in any order, not sequentially
- **Prefetch requests** (`Purpose: prefetch`) that appear at any point in the sequence

Replace the position-indexed divergence check with a time-window set-based model.

**Phase windows (hardcoded thresholds, configurable from YAML `parameters`):**

| Phase | Time after doc | Expected state set |
|-------|---------------|-------------------|
| Critical | 0–500ms | `{StaticAsset}` (may be absent if cache warm) |
| Mid | 500ms–2s | `{StaticAsset, ApiCall, PageView}` |
| Late | 2s–30s | `{ApiCall, SignalR, WebSocket, ServerSentEvent}` |
| Settled | 30s+ | `{ApiCall, SignalR, ServerSentEvent}` |

**Prefetch detection:** A request is prefetch if `Purpose: prefetch` header is present, OR `Sec-Fetch-Mode: no-cors` + `Sec-Fetch-Dest: document`. Prefetch requests never contribute to divergence.

**Cache warm detection:** If the critical window (0–500ms) closes with zero `StaticAsset` requests AND the subsequent requests look normal (no machine-speed timing, no ghost paths), classify as cache warm. Write `sequence.cache_warm = true`.

- [ ] **Step 1: Write failing tests for set-based divergence**

Add to `Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs`:

```csharp
[Fact]
public async Task Cache_warm_browser_skipping_assets_does_not_diverge()
{
    // A browser that skips the static asset burst (cache warm) should NOT be flagged
    // as diverged just because no StaticAsset appeared in the critical window.
    var store = new SequenceContextStore();
    var contributor = CreateContributor(store);
    var sig = "sig-cache-warm";

    // Request 1: document
    var state1 = BuildDocumentState(sig);
    await contributor.ContributeAsync(state1);

    // Jump forward 600ms (past the 500ms critical window) — assets came from cache
    store.Update(sig, store.TryGet(sig)! with
    {
        Position = 0,
        LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-600),
        WindowStartTime = DateTimeOffset.UtcNow.AddMilliseconds(-600),
        ObservedStateSet = [] // no StaticAsset seen
    });

    // Request 2: API call (reasonable post-cache timing)
    var state2 = BuildApiState(sig);
    await contributor.ContributeAsync(state2);

    var diverged = state2.GetSignal<bool?>(SignalKeys.SequenceDiverged);
    var cacheWarm = state2.GetSignal<bool?>(SignalKeys.SequenceCacheWarm);
    Assert.Null(diverged);        // not diverged
    Assert.True(cacheWarm);       // recognized as cache warm
}

[Fact]
public async Task Prefetch_request_does_not_cause_divergence()
{
    var store = new SequenceContextStore();
    var contributor = CreateContributor(store);
    var sig = "sig-prefetch";

    // Request 1: document
    var state1 = BuildDocumentState(sig);
    await contributor.ContributeAsync(state1);
    store.Update(sig, store.TryGet(sig)! with { Position = 0, LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-300) });

    // Request 2: prefetch (Purpose: prefetch header)
    var ctx2 = new DefaultHttpContext();
    ctx2.Request.Method = "GET";
    ctx2.Request.Path = "/next-page";
    ctx2.Request.Headers["Purpose"] = "prefetch";
    ctx2.Request.Headers["Sec-Fetch-Mode"] = "no-cors";
    var signals2 = new ConcurrentDictionary<string, object> { [SignalKeys.PrimarySignature] = sig };
    var state2 = new BlackboardState
    {
        HttpContext = ctx2, Signals = signals2, SignalWriter = signals2,
        CurrentRiskScore = 0, CompletedDetectors = ImmutableHashSet<string>.Empty,
        FailedDetectors = ImmutableHashSet<string>.Empty,
        Contributions = ImmutableList<DetectionContribution>.Empty,
        RequestId = "req2", Elapsed = TimeSpan.Zero
    };
    await contributor.ContributeAsync(state2);

    var diverged = state2.GetSignal<bool?>(SignalKeys.SequenceDiverged);
    var prefetchDetected = state2.GetSignal<bool?>(SignalKeys.SequencePrefetchDetected);
    Assert.Null(diverged);
    Assert.True(prefetchDetected);
}

[Fact]
public async Task Http2_parallel_assets_in_any_order_do_not_diverge()
{
    // Multiple StaticAsset requests arriving within the critical window in any order
    // should NOT cause divergence even if they don't match ExpectedChain[N] exactly
    var store = new SequenceContextStore();
    var contributor = CreateContributor(store);
    var sig = "sig-parallel";

    var state1 = BuildDocumentState(sig);
    await contributor.ContributeAsync(state1);

    // Simulate first static asset (150ms after doc, within critical window)
    store.Update(sig, store.TryGet(sig)! with { Position = 0, LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-150) });
    var stateAsset1 = BuildStaticAssetState(sig);
    await contributor.ContributeAsync(stateAsset1);

    // Simulate second static asset (50ms after first, still within critical window)
    var ctx = store.TryGet(sig)!;
    store.Update(sig, ctx with { LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-50) });
    var stateAsset2 = BuildStaticAssetState(sig);
    await contributor.ContributeAsync(stateAsset2);

    var diverged = stateAsset2.GetSignal<bool?>(SignalKeys.SequenceDiverged);
    Assert.Null(diverged); // parallel asset burst is healthy
}

// Helper: Build a static asset request state
private static BlackboardState BuildStaticAssetState(string signature = "test-sig")
{
    var ctx = new DefaultHttpContext();
    ctx.Request.Method = "GET";
    ctx.Request.Path = "/styles.css";
    ctx.Request.Headers["Sec-Fetch-Dest"] = "style";
    ctx.Request.Headers["Sec-Fetch-Mode"] = "no-cors";
    var signals = new ConcurrentDictionary<string, object>
    {
        [SignalKeys.PrimarySignature] = signature,
        [SignalKeys.TransportProtocolClass] = "static"
    };
    return new BlackboardState
    {
        HttpContext = ctx, Signals = signals, SignalWriter = signals,
        CurrentRiskScore = 0, CompletedDetectors = ImmutableHashSet<string>.Empty,
        FailedDetectors = ImmutableHashSet<string>.Empty,
        Contributions = ImmutableList<DetectionContribution>.Empty,
        RequestId = "req-asset", Elapsed = TimeSpan.Zero
    };
}
```

- [ ] **Step 2: Run tests — expect them to fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~ContentSequenceContributorTests" 2>&1 | tail -20
```

Expected: 3 new tests FAIL (set-based logic not yet implemented).

- [ ] **Step 3: Add prefetch/preload detection helper to `RequestMarkovClassifier.cs`**

Add a new static method after `Classify`:

```csharp
/// <summary>
///     Returns true if this request is a browser prefetch/preload resource hint.
///     Prefetch requests never count toward divergence regardless of their Markov state.
/// </summary>
public static bool IsPrefetchRequest(HttpRequest request)
{
    // Chromium/Firefox: Purpose: prefetch header
    var purpose = request.Headers["Purpose"].FirstOrDefault();
    if (string.Equals(purpose, "prefetch", StringComparison.OrdinalIgnoreCase))
        return true;

    // Sec-Fetch-Mode: no-cors + Sec-Fetch-Dest: document = browser-initiated prefetch
    var secFetchMode = request.Headers["Sec-Fetch-Mode"].FirstOrDefault();
    var secFetchDest = request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
    if (string.Equals(secFetchMode, "no-cors", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(secFetchDest, "document", StringComparison.OrdinalIgnoreCase))
        return true;

    return false;
}
```

- [ ] **Step 4: Replace `ComputeDivergenceScore` in `ContentSequenceContributor.cs` with set-based logic**

Replace the existing `ComputeDivergenceScore` and `HandleContinuation` methods with the set-based version:

```csharp
// Phase window boundaries (ms since document hit or last window open)
private static readonly double[] PhaseThresholdsMs = [500, 2000, 30_000];

// Expected state sets per phase — index matches PhaseThresholdsMs
private static readonly RequestState[][] PhaseExpectedSets =
[
    // Critical (0-500ms): static assets + page views (preload)
    [RequestState.StaticAsset, RequestState.PageView],
    // Mid (500ms-2s): api calls also expected, assets tail off
    [RequestState.StaticAsset, RequestState.ApiCall, RequestState.PageView],
    // Late (2s-30s): API + streaming transports
    [RequestState.ApiCall, RequestState.SignalR, RequestState.WebSocket, RequestState.ServerSentEvent],
    // Settled (30s+): only long-running connections expected
    [RequestState.ApiCall, RequestState.SignalR, RequestState.ServerSentEvent]
];

private SequenceContext HandleContinuation(
    BlackboardState state,
    string signature,
    SequenceContext ctx)
{
    // Upgrade to Tier 2 if cluster is now known
    if (ctx.CentroidType == CentroidType.Unknown && _clusterService != null)
    {
        var cluster = _clusterService.FindCluster(signature);
        if (cluster != null)
        {
            var tier2 = _centroidStore.TryGetCentroidChain(cluster.ClusterId, MinCentroidSampleSize);
            if (tier2 != null)
            {
                ctx = ctx with
                {
                    CentroidId = cluster.ClusterId,
                    CentroidType = tier2.Type,
                    ExpectedChain = tier2.ExpectedStates,
                    TypicalGapsMs = tier2.TypicalGapsMs,
                    GapToleranceMs = tier2.GapToleranceMs
                };
            }
        }
    }

    var request = state.HttpContext.Request;
    var gapMs = (DateTimeOffset.UtcNow - ctx.LastRequest).TotalMilliseconds;
    var totalMsFromWindow = (DateTimeOffset.UtcNow - ctx.WindowStartTime).TotalMilliseconds;

    // Prefetch: never counts as divergence
    var isPrefetch = RequestMarkovClassifier.IsPrefetchRequest(request);
    if (isPrefetch)
    {
        var updatedCtx = ctx with
        {
            Position = ctx.Position + 1,
            LastRequest = DateTimeOffset.UtcNow,
            RequestCountInWindow = ctx.RequestCountInWindow + 1
        };
        _contextStore.Update(signature, updatedCtx);
        state.WriteSignal(SignalKeys.SequencePrefetchDetected, true);
        WriteSignals(state, updatedCtx);
        return updatedCtx;
    }

    var actualState = RequestMarkovClassifier.Classify(state);
    var phaseIndex = GetPhaseIndex(totalMsFromWindow);
    var expectedSet = PhaseExpectedSets[phaseIndex];

    // Machine-speed timing check (< 20ms is almost certainly automated)
    var machineSpeed = gapMs < 20;

    // Set-based divergence: is the actual state in the expected set for this phase?
    var stateInSet = expectedSet.Contains(actualState);

    // Cache-warm detection: critical window closed with no static assets
    var cacheWarm = ctx.CacheWarm;
    if (!cacheWarm && phaseIndex > 0 && !ctx.ObservedStateSet.Contains(RequestState.StaticAsset))
    {
        // Critical window has passed, no static assets seen — likely cache warm
        cacheWarm = true;
    }

    // Update the observed state set
    var newObservedSet = new HashSet<RequestState>(ctx.ObservedStateSet) { actualState };

    // Compute divergence score
    double divergenceScore = 0.0;
    if (machineSpeed)
        divergenceScore += 0.4;
    if (!stateInSet && !(cacheWarm && actualState == RequestState.ApiCall))
        divergenceScore += 0.5; // unexpected state for this phase
    if (ctx.RequestCountInWindow > 50) // bot-volume burst
        divergenceScore += 0.3;

    divergenceScore = Math.Min(1.0, divergenceScore);
    var diverged = divergenceScore >= DivergenceThreshold;

    return ctx with
    {
        Position = ctx.Position + 1,
        LastRequest = DateTimeOffset.UtcNow,
        ObservedStateSet = newObservedSet,
        RequestCountInWindow = ctx.RequestCountInWindow + 1,
        CacheWarm = cacheWarm,
        HasDiverged = ctx.HasDiverged || diverged,
        DivergenceCount = ctx.DivergenceCount + (diverged ? 1 : 0)
    };
}

private static int GetPhaseIndex(double msSinceWindowStart)
{
    for (var i = 0; i < PhaseThresholdsMs.Length; i++)
        if (msSinceWindowStart <= PhaseThresholdsMs[i])
            return i;
    return PhaseThresholdsMs.Length; // settled phase
}
```

Also update `WriteSignals` to emit the new signals:

```csharp
if (ctx.CacheWarm)
    state.WriteSignal(SignalKeys.SequenceCacheWarm, true);
```

- [ ] **Step 5: Run tests — expect all to pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~ContentSequenceContributorTests" -v
```

Expected: All 12 tests PASS (original 9 + 3 new set-based tests).

- [ ] **Step 6: Build full solution**

```bash
dotnet build mostlylucid.stylobot.sln
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs \
        Mostlylucid.BotDetection/Markov/RequestMarkovClassifier.cs \
        Mostlylucid.BotDetection/Services/SequenceContextStore.cs \
        Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs
git commit -m "feat: set-based divergence scoring with prefetch and cache-warm tolerance"
```

---

## Task 7: Tests for `ContentSequenceContributor`

**Files:**
- Create: `Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs`

- [ ] **Step 1: Write the tests**

Create `Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public class ContentSequenceContributorTests
{
    private sealed class StubConfigProvider : IDetectorConfigProvider
    {
        private readonly Dictionary<string, object> _p;
        public StubConfigProvider(Dictionary<string, object>? p = null) => _p = p ?? [];
        public DetectorManifest? GetManifest(string n) => null;
        public DetectorDefaults GetDefaults(string n) => new()
        {
            Weights = new() { Base = 1.0, BotSignal = 1.0, HumanSignal = 1.0, Verified = 1.0 },
            Confidence = new() { BotDetected = 0.3, HumanIndicated = -0.2, Neutral = 0.0, StrongSignal = 0.5 },
            Parameters = new Dictionary<string, object>(_p)
        };
        public T GetParameter<T>(string d, string p, T def) =>
            _p.TryGetValue(p, out var v) ? (T)Convert.ChangeType(v, typeof(T)) : def;
        public Task<T> GetParameterAsync<T>(string d, string p, ConfigResolutionContext c, T def, CancellationToken ct = default)
            => Task.FromResult(GetParameter(d, p, def));
        public void InvalidateCache(string? n = null) { }
        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests() => new Dictionary<string, DetectorManifest>();
    }

    private static ContentSequenceContributor CreateContributor(
        SequenceContextStore? store = null,
        CentroidSequenceStore? centroidStore = null)
    {
        store ??= new SequenceContextStore();
        centroidStore ??= new CentroidSequenceStore("Data Source=:memory:", NullLogger<CentroidSequenceStore>.Instance);
        return new ContentSequenceContributor(
            NullLogger<ContentSequenceContributor>.Instance,
            new StubConfigProvider(),
            store,
            centroidStore);
    }

    private static BlackboardState BuildDocumentState(string signature = "test-sig")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        ctx.Request.Path = "/page";
        var signals = new ConcurrentDictionary<string, object>
        {
            [SignalKeys.PrimarySignature] = signature
        };
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signals,
            SignalWriter = signals,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = "req1",
            Elapsed = TimeSpan.Zero
        };
    }

    private static BlackboardState BuildApiState(string signature = "test-sig")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/data";
        var signals = new ConcurrentDictionary<string, object>
        {
            [SignalKeys.PrimarySignature] = signature,
            [SignalKeys.TransportProtocolClass] = "api"
        };
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signals,
            SignalWriter = signals,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = "req2",
            Elapsed = TimeSpan.Zero
        };
    }

    [Fact]
    public void Name_is_ContentSequence()
    {
        var contributor = CreateContributor();
        Assert.Equal("ContentSequence", contributor.Name);
    }

    [Fact]
    public void Priority_is_4()
    {
        var contributor = CreateContributor();
        Assert.Equal(4, contributor.Priority);
    }

    [Fact]
    public async Task Document_request_writes_position_zero_and_on_track()
    {
        var store = new SequenceContextStore();
        var contributor = CreateContributor(store);
        var state = BuildDocumentState();

        await contributor.ContributeAsync(state);

        var position = state.GetSignal<int?>(SignalKeys.SequencePosition);
        var onTrack = state.GetSignal<bool?>(SignalKeys.SequenceOnTrack);
        Assert.Equal(0, position);
        Assert.True(onTrack);
    }

    [Fact]
    public async Task Document_request_writes_chain_id()
    {
        var contributor = CreateContributor();
        var state = BuildDocumentState();

        await contributor.ContributeAsync(state);

        var chainId = state.GetSignal<string>(SignalKeys.SequenceChainId);
        Assert.NotNull(chainId);
        Assert.NotEmpty(chainId);
    }

    [Fact]
    public async Task No_signature_writes_no_sequence_signals()
    {
        var contributor = CreateContributor();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        var signals = new ConcurrentDictionary<string, object>(); // no signature
        var state = new BlackboardState
        {
            HttpContext = ctx,
            Signals = signals,
            SignalWriter = signals,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = "req",
            Elapsed = TimeSpan.Zero
        };

        await contributor.ContributeAsync(state);

        Assert.False(signals.ContainsKey(SignalKeys.SequencePosition));
    }

    [Fact]
    public async Task Non_document_first_request_writes_no_sequence_signals()
    {
        var contributor = CreateContributor();
        var state = BuildApiState("sig-direct-api");

        await contributor.ContributeAsync(state);

        Assert.False(state.Signals.ContainsKey(SignalKeys.SequencePosition));
    }

    [Fact]
    public async Task Continuation_increments_position()
    {
        var store = new SequenceContextStore();
        var contributor = CreateContributor(store);
        var sig = "sig-continuation";

        // Request 1: document
        var state1 = BuildDocumentState(sig);
        await contributor.ContributeAsync(state1);
        store.Update(sig, store.TryGet(sig)! with { Position = 0, LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-300) });

        // Request 2: continuation
        var state2 = BuildApiState(sig);
        await contributor.ContributeAsync(state2);

        var position = state2.GetSignal<int?>(SignalKeys.SequencePosition);
        Assert.Equal(1, position);
    }

    [Fact]
    public async Task Machine_speed_request_diverges()
    {
        var store = new SequenceContextStore();
        var contributor = CreateContributor(store);
        var sig = "sig-machine-speed";

        // Request 1
        var state1 = BuildDocumentState(sig);
        await contributor.ContributeAsync(state1);
        // Simulate < 20ms gap (machine speed)
        store.Update(sig, store.TryGet(sig)! with { Position = 0, LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-5) });

        // Request 2 immediately after
        var state2 = BuildApiState(sig);
        await contributor.ContributeAsync(state2);

        var diverged = state2.GetSignal<bool?>(SignalKeys.SequenceDiverged);
        Assert.True(diverged);
    }

    [Fact]
    public async Task No_trigger_conditions()
    {
        var contributor = CreateContributor();
        Assert.Empty(contributor.TriggerConditions);
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~ContentSequenceContributorTests" -v
```

Expected: All 9 tests PASS.

- [ ] **Step 3: Commit**

```bash
git add Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs
git commit -m "test: ContentSequenceContributor — document detection, position, divergence"
```

---

## Task 8: Update Deferred Detector Trigger Conditions

**Files:**
- Modify: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs`
- Modify: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/PeriodicityContributor.cs`
- Modify: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/BehavioralWaveformContributor.cs`

These three detectors are contractually useless at sequence position 0 — they need N>1 requests to produce meaningful signal. Add an `AnyOfTrigger` that skips them when the visitor is on-track early in the sequence, but always lets them run when: diverged, past position 3, or sequence signals are absent (direct API entry).

The existing `TriggerConditions` on each detector return a plain array (All must satisfy). The new pattern wraps the sequence-aware skip logic using `AnyOfTrigger`:

```csharp
// The sequence guard: run if ANY of these is true:
// - sequence.position doesn't exist (non-document entry, no sequence tracking)
// - sequence.on_track is false (diverged — run full pipeline)
// - sequence.diverged is true (explicit divergence signal)
// - sequence.position >= 3 (enough requests to have meaningful data)
private static readonly AnyOfTrigger SequenceGuard = new([
    new SignalNotExistsTrigger(SignalKeys.SequencePosition),
    new SignalValueTrigger<bool>(SignalKeys.SequenceOnTrack, false),
    new SignalValueTrigger<bool>(SignalKeys.SequenceDiverged, true),
    new SignalPredicateTrigger<int>(SignalKeys.SequencePosition, pos => pos >= 3, "position >= 3")
]);
```

**Important:** The existing trigger conditions on each detector must still be satisfied (e.g., `SignalExistsTrigger(PrimarySignature)`) AND the sequence guard must be satisfied. Keep existing conditions, add the sequence guard.

- [ ] **Step 1: Update `SessionVectorContributor.TriggerConditions`**

In `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs`, find the `TriggerConditions` property (around line 44):

Current:
```csharp
public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
{
    new SignalExistsTrigger(SignalKeys.PrimarySignature)
};
```

Replace with:
```csharp
private static readonly AnyOfTrigger SequenceGuard = new([
    new SignalNotExistsTrigger(SignalKeys.SequencePosition),
    new SignalValueTrigger<bool>(SignalKeys.SequenceOnTrack, false),
    new SignalValueTrigger<bool>(SignalKeys.SequenceDiverged, true),
    new SignalPredicateTrigger<int>(SignalKeys.SequencePosition, pos => pos >= 3, "position >= 3")
]);

public override IReadOnlyList<TriggerCondition> TriggerConditions =>
[
    new SignalExistsTrigger(SignalKeys.PrimarySignature),
    SequenceGuard
];
```

- [ ] **Step 2: Update `PeriodicityContributor.TriggerConditions`**

Find `PeriodicityContributor`. Check its existing `TriggerConditions`. Add the same `SequenceGuard` static field and include it in the returned list alongside the existing conditions.

Open `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/PeriodicityContributor.cs`:

```csharp
private static readonly AnyOfTrigger SequenceGuard = new([
    new SignalNotExistsTrigger(SignalKeys.SequencePosition),
    new SignalValueTrigger<bool>(SignalKeys.SequenceOnTrack, false),
    new SignalValueTrigger<bool>(SignalKeys.SequenceDiverged, true),
    new SignalPredicateTrigger<int>(SignalKeys.SequencePosition, pos => pos >= 3, "position >= 3")
]);
```

Add `SequenceGuard` to whatever `TriggerConditions` array currently exists there.

- [ ] **Step 3: Update `BehavioralWaveformContributor.TriggerConditions`**

Same pattern as steps 1-2. Open `BehavioralWaveformContributor.cs`, add `SequenceGuard` and include it in `TriggerConditions`.

- [ ] **Step 4: Build**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Run existing tests to verify no regression**

```bash
dotnet test Mostlylucid.BotDetection.Test/ -v 2>&1 | tail -20
```

Expected: All passing tests still pass. No new failures.

- [ ] **Step 6: Commit**

```bash
git add Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs \
        Mostlylucid.BotDetection/Orchestration/ContributingDetectors/PeriodicityContributor.cs \
        Mostlylucid.BotDetection/Orchestration/ContributingDetectors/BehavioralWaveformContributor.cs
git commit -m "feat: defer SessionVector, Periodicity, BehavioralWaveform via sequence trigger guard"
```

---

## Task 9: SignalR Trust Boost (`StreamAbuseContributor`)

**Files:**
- Modify: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/StreamAbuseContributor.cs`

When `sequence.signalr_expected` is written, the SignalR connection is a healthy continuation of a document sequence. `StreamAbuseContributor` should not penalise it.

`TriggerConditions` specifies when to RUN. Adding `SignalNotExistsTrigger(SequenceSignalRExpected)` means "run StreamAbuse only when `signalr_expected` was NOT written". When `signalr_expected=true`, the condition fails, detector skips.

- [ ] **Step 1: Update `StreamAbuseContributor.TriggerConditions`**

Current (around line 39):
```csharp
public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
{
    new SignalExistsTrigger(SignalKeys.TransportProtocol),
    new SignalExistsTrigger(SignalKeys.PrimarySignature)
};
```

Replace with:
```csharp
public override IReadOnlyList<TriggerCondition> TriggerConditions =>
[
    new SignalExistsTrigger(SignalKeys.TransportProtocol),
    new SignalExistsTrigger(SignalKeys.PrimarySignature),
    // Skip if this is an expected SignalR continuation from a known-good sequence
    new SignalNotExistsTrigger(SignalKeys.SequenceSignalRExpected)
];
```

- [ ] **Step 2: Build and run all tests**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj && \
dotnet test Mostlylucid.BotDetection.Test/ 2>&1 | tail -10
```

Expected: Build succeeded, all existing tests pass.

- [ ] **Step 3: Commit**

```bash
git add Mostlylucid.BotDetection/Orchestration/ContributingDetectors/StreamAbuseContributor.cs
git commit -m "feat: StreamAbuse skips expected SignalR continuations from known-good sequences"
```

---

## Task 10: DI Registration + Cluster Event Hook

**Files:**
- Modify: `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Add registrations**

In `ServiceCollectionExtensions.cs`, find the block where `FastPathReputationContributor` is registered (line ~488). Just before it, add:

```csharp
// Content sequence detection (Priority 4 — must come before all other detectors)
services.AddSingleton<SequenceContextStore>();
services.AddSingleton(sp =>
{
    // CentroidSequenceStore shares the sessions.db connection string from SqliteSessionStore
    var sessionStore = sp.GetRequiredService<SqliteSessionStore>();
    var logger = sp.GetRequiredService<ILogger<CentroidSequenceStore>>();
    return new CentroidSequenceStore(sessionStore.ConnectionString, logger);
});
services.AddSingleton<IContributingDetector, ContentSequenceContributor>();
```

- [ ] **Step 2: Wire ClustersUpdated event**

Find where `BotClusterService` is registered and used (search for `BotClusterService` in the file). The `BotClusterService.ClustersUpdated` event fires after each cluster run. Hook it to rebuild `CentroidSequenceStore`.

After the `BotClusterService` singleton registration, add an `IHostedService` wrapper or use a `PostConfigure` delegate. The simplest approach is to add a startup hook in the app pipeline. Find the section in `ServiceCollectionExtensions.cs` where `BackgroundService` types are registered and add:

```csharp
// Wire CentroidSequenceStore rebuild to cluster updates
services.AddHostedService<CentroidSequenceRebuildHostedService>();
```

Then create `Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Wires BotClusterService.ClustersUpdated → CentroidSequenceStore.RebuildAsync
///     and initializes the SQLite table on startup.
/// </summary>
internal sealed class CentroidSequenceRebuildHostedService : IHostedService
{
    private readonly BotClusterService? _clusterService;
    private readonly CentroidSequenceStore _centroidStore;
    private readonly ILogger<CentroidSequenceRebuildHostedService> _logger;

    public CentroidSequenceRebuildHostedService(
        CentroidSequenceStore centroidStore,
        ILogger<CentroidSequenceRebuildHostedService> logger,
        BotClusterService? clusterService = null)
    {
        _centroidStore = centroidStore;
        _logger = logger;
        _clusterService = clusterService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _centroidStore.InitializeAsync(cancellationToken);

        if (_clusterService != null)
        {
            _clusterService.ClustersUpdated += OnClustersUpdated;
            _logger.LogDebug("CentroidSequenceStore wired to BotClusterService.ClustersUpdated");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_clusterService != null)
            _clusterService.ClustersUpdated -= OnClustersUpdated;
        return Task.CompletedTask;
    }

    private void OnClustersUpdated(
        IReadOnlyList<BotCluster> clusters,
        IReadOnlyList<SignatureBehavior> behaviors)
    {
        _ = _centroidStore.RebuildAsync(clusters, CancellationToken.None);
    }
}
```

- [ ] **Step 3: Build the solution**

```bash
dotnet build mostlylucid.stylobot.sln
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs
git commit -m "feat: register ContentSequence services and wire cluster update event"
```

---

## Task 11: Dashboard Narrative Builder

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`

- [ ] **Step 1: Add friendly name and category entries**

Open `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`.

In `DetectorFriendlyNames` dictionary, add:
```csharp
["ContentSequence"] = "content sequence analysis",
```

In `DetectorCategories` dictionary, add:
```csharp
["ContentSequence"] = "Behavioral",
```

- [ ] **Step 2: Build**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Run full test suite**

```bash
dotnet test mostlylucid.stylobot.sln 2>&1 | tail -15
```

Expected: All tests pass. No regressions.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs
git commit -m "feat: register ContentSequence in dashboard narrative builder"
```

---

## Task 12: Smoke Test — Run Demo and Verify

**Files:** None (verification only)

- [ ] **Step 1: Build the demo**

```bash
dotnet build Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run the demo and hit a page**

```bash
dotnet run --project Mostlylucid.BotDetection.Demo &
sleep 3
# Hit the homepage (document request)
curl -s -H "Sec-Fetch-Mode: navigate" -H "Accept: text/html" https://localhost:5001/ -k -o /dev/null -w "%{http_code}\n"
```

Expected: `200` (page loads successfully — not blocked by sequence detection).

- [ ] **Step 3: Check dashboard for sequence signals**

Visit `http://localhost:5080/_stylobot` and look at a recent session in the Sessions tab. Verify:
- `ContentSequence` appears in the contributing detectors column
- `sequence.position` and `sequence.on_track` appear in the signal viewer for the session

- [ ] **Step 4: Kill the demo process and commit any config changes**

```bash
kill %1 2>/dev/null || true
git status
```

If any config files changed, commit them. If nothing changed, the run was clean.

---

## Self-Review Checklist

**Spec coverage:**

| Spec Requirement | Task |
|-----------------|------|
| `ContentSequenceContributor` P4 | Task 6 |
| `SequenceContextStore` with TTL sweep | Task 4 |
| `CentroidSequenceStore` + SQLite table | Task 5 |
| `RequestMarkovClassifier` shared helper | Task 2 |
| `contentsequence.detector.yaml` | Task 6 |
| `SignalNotExistsTrigger` new trigger type | Task 1 |
| Signal keys in `SignalKeys` | Task 3 |
| Document detection via `Sec-Fetch-Mode` | Task 6 |
| Sequence position + on_track signals | Task 6, 7 |
| Divergence detection + signals | Task 6, 7 |
| Deferred detector trigger updates (SessionVector, Periodicity, Waveform) | Task 8 |
| StreamAbuse SignalR trust boost | Task 9 |
| DI registration | Task 10 |
| ClustersUpdated → RebuildAsync hook | Task 10 |
| Dashboard narrative entries | Task 11 |
| `sequence.signalr_expected` written by contributor | Task 6 |
| Tier 1 global chain (hard-coded from YAML defaults) | Task 5 |
| Tier 2 centroid chain (from cluster data) | Task 5, 6 |
| Zero orchestrator changes | All tasks — confirmed |
| `NotExists` fallback so non-document requests always run full pipeline | Task 8 |

All spec requirements covered. ✓

**Type consistency:**
- `SequenceContext.ExpectedChain` is `RequestState[]` — matches `CentroidSequence.ExpectedStates` (same type) ✓
- `CentroidType` enum defined in `SequenceContextStore.cs`, used in `CentroidSequence` and `SequenceContext` ✓
- `SignalKeys.SequencePosition` used as `int` throughout ✓
- `SignalKeys.SequenceOnTrack` used as `bool` throughout ✓
- `SignalNotExistsTrigger` used in Task 8 and 9, defined in Task 1 ✓
- `SequenceGuard` is `AnyOfTrigger`, compatible with `IReadOnlyList<TriggerCondition>` ✓
