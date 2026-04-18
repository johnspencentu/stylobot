# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**StyloBot** is an enterprise-grade bot detection framework for ASP.NET Core. It uses a blackboard architecture (via StyloFlow) with 31 detectors in 4 waves, real-time inference with <1ms fast path, intent classification with threat scoring, Leiden clustering for bot network discovery, and zero-PII design. The system combines fast-path detection with optional LLM enrichment (not decision-making) for edge cases. Sessions are the primary behavioral unit - compressed into 129-dimensional Markov chain vectors with unified fingerprint dimensions and per-transition timing anomaly detection, enabling inter-session velocity analysis and behavioral anomaly detection. Persistence uses SQLite everywhere (zero-dependency) for the FOSS product, with PostgreSQL as the commercial upgrade path (in the `stylobot-commercial` repo). The website/portal has been moved to `stylobot-commercial` as it depends on commercial packages. The real-time dashboard features session timeline visualization with Markov chain drill-in, behavioral shape radar charts (8-axis projection from 129-dim vectors), world threat map, traffic charts, country analytics, cluster visualization, threat scoring, deterministic bot naming, and live signature feed. All dashboard data persists to SQLite (no in-memory stores).

## Critical Rules

- **NEVER add hard-coded site-specific exceptions, bypass keys, or allowlists.** StyloBot is a detection product - the fix is always to make detection *correct*, not to add workarounds. The live site (www.stylobot.net) runs the product as-is to test it.
- **The `X-SB-Api-Key` header** is part of the product's detection policy system (for customers to exempt their own monitoring/health-check traffic). It is NOT for operational use to bypass detection on the StyloBot site itself.
- **All detection improvements must be generic** - based on protocol specs (W3C Fetch Metadata, RFC 6455, etc.), not site-specific paths or domains.
- **NEVER use in-memory stores for persistence.** All state must persist to SQLite (FOSS) or PostgreSQL (commercial). `ConcurrentDictionary` is fine for per-request transient state and performance caches only. No `InMemory*Store` classes for anything that matters.
- **NEVER skip detection.** No skip paths, no logonly workarounds. Use `BotPolicyAttribute(BlockThreshold = 0.95)` for internal endpoints that need to be reachable by edge-case visitors.
- **Dashboard logins are unlimited.** The "users" limit in commercial tiers refers to protected identity policy overrides (`ConfigResolutionContext.UserId`), not dashboard seats.

## Build Commands

```bash
# Build entire solution
dotnet build mostlylucid.stylobot.sln

# Build specific project
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj

# Run the full demo application (all 31 detectors + dashboard)
dotnet run --project Mostlylucid.BotDetection.Demo
# Visit: https://localhost:5001/SignatureDemo
# Dashboard: http://localhost:5080/_stylobot

# Run all tests
dotnet test

# Run specific test project
dotnet test Mostlylucid.BotDetection.Test/
dotnet test Mostlylucid.BotDetection.Orchestration.Tests/

# Run single test file
dotnet test --filter "FullyQualifiedName~UserAgentDetectorTests"

# Run tests with coverage
dotnet test /p:CollectCoverage=true

# Run benchmarks
dotnet run --project Mostlylucid.BotDetection.Benchmarks -c Release

# Pack NuGet package
dotnet pack Mostlylucid.BotDetection -c Release
```

## Solution Structure

**Main Solution**: `mostlylucid.stylobot.sln` (20 projects)

| Project | Purpose |
|---------|---------|
| `Mostlylucid.BotDetection` | Core detection library (NuGet package) |
| `Mostlylucid.BotDetection.MinimalDemo` | Minimal demo - zero dependencies, shows all protection approaches |
| `Mostlylucid.BotDetection.UI` | Dashboard, TagHelpers, SignalR hub |
| `Mostlylucid.BotDetection.UI.PostgreSQL` | PostgreSQL persistence layer |
| `Mostlylucid.BotDetection.Demo` | Interactive demo with all 30 detectors |
| `Mostlylucid.BotDetection.Console` | Standalone gateway/proxy console |
| `Stylobot.Gateway` | Docker-first YARP reverse proxy |
| `Mostlylucid.GeoDetection` | Geographic routing (MaxMind, ip-api) |
| `Mostlylucid.GeoDetection.Contributor` | Geo enrichment for bot detection |
| `Mostlylucid.Common` | Shared utilities (caching, telemetry) |

**Test Projects**: `*.Test`, `*.Tests` - xUnit + Moq

**Website Solution**: Moved to `stylobot-commercial` repo (depends on commercial packages for portal/licensing)

## Architecture

### Blackboard Pattern (StyloFlow)

Detection uses an ephemeral blackboard where detectors write signals:
- `SignalSink` - In-memory signal store per request
- Raw PII (IP, UA) stays in `DetectionContext`, never on blackboard
- Signals use hierarchical keys: `request.ip.is_datacenter`, `detection.useragent.confidence`

### Detector Pipeline

**Fast Path (<1ms)**: UserAgent, Header, Ip, SecurityTool, Behavioral, ClientSide, Inconsistency, VersionAge, Heuristic, FastPathReputation, CacheBehavior, ReputationBias

**Slow Path (~100ms)**: ProjectHoneypot (DNS lookup)

**Advanced Fingerprinting**: TlsFingerprint (JA3/JA4), TcpIpFingerprint (p0f), Http2Fingerprint (AKAMAI), MultiLayerCorrelation, BehavioralWaveform, ResponseBehavior

**Session Analysis**: SessionVector (Markov chain → 118-dim vector compression, inter-session velocity)

### Session Vector Architecture

Sessions are the primary behavioral unit. Per-request Markov chain transitions are compressed into a fixed-dimension vector per session, enabling similarity search and inter-session anomaly detection.

**Vector dimensions (118 total):**
- `[0..99]` Markov transition probabilities (10 states × 10 states)
- `[100..109]` Stationary distribution (time spent in each state)
- `[110..117]` Temporal features (timing entropy, burst ratio, error rate, etc.)
- `[118..125]` Fingerprint features (TLS, HTTP protocol, TCP OS, headless, datacenter)

**Markov states:** PageView, ApiCall, StaticAsset, WebSocket, SignalR, ServerSentEvent, FormSubmit, AuthAttempt, NotFound, Search

**Key concepts:**
- **Retrogressive session boundary:** Sessions are defined by inter-request gaps (default 30min), detected when the NEXT request reveals the gap - not by fixed time windows
- **Unified fingerprint dimensions:** TLS/TCP/H2 fingerprints are vector dimensions, so fingerprint mutation across sessions appears as velocity in those dimensions
- **Snapshot compaction:** Old session snapshots merge into a maturity-weighted root vector preserving the behavioral baseline while discarding per-session detail
- **Inter-session velocity:** L2 magnitude of the delta vector between consecutive sessions; high velocity = sudden behavioral shift (bot rotation, account takeover)

**Key files:**
- `Analysis/SessionVector.cs` - SessionStore, SessionVectorizer, FingerprintContext, snapshot compaction
- `Orchestration/ContributingDetectors/SessionVectorContributor.cs` - Detection contributor
- `Orchestration/Manifests/detectors/sessionvector.detector.yaml` - YAML config

### Persistence

**Core product (SQLite, zero-dependency):**
- `Data/SqliteSessionStore.cs` - ISessionStore implementation
- `Data/SessionPersistenceService.cs` - Background service bridging in-memory SessionStore events to SQLite
- Tables: `sessions` (vector + Markov chains), `signatures` (cumulative reputation), `buckets` (1-minute counters)
- ~100x compression vs per-request storage (200 sessions/day vs 10,000 requests/day)

**Commercial (PostgreSQL + pgvector):**
- `Mostlylucid.BotDetection.UI.PostgreSQL` - PostgreSQL/TimescaleDB persistence (enterprise feature)
- Native HNSW indexing for sub-millisecond vector similarity queries at scale

### Key Files

- `Extensions/ServiceCollectionExtensions.cs` - DI registration entry points
- `Orchestration/BlackboardOrchestrator.cs` - Main detection orchestration
- `Orchestration/ContributingDetectors/` - All 31 detector implementations
- `Orchestration/Manifests/detectors/*.yaml` - Detector configurations
- `Models/BotDetectionOptions.cs` - Configuration model
- `Actions/*.cs` - Response policies (block, throttle, challenge, redirect)

### Transport-Aware Detection

Detectors are aware of transport protocol context (API, SignalR, WebSocket, gRPC) to avoid false positives on non-document traffic. The `TransportProtocolContributor` (Priority 5) writes signals that downstream detectors consume:
- `transport.protocol_class` - document, api, signalr, grpc, static
- `transport.is_streaming` - WebSocket, SSE, SignalR
- `transport.is_upgrade` - WebSocket upgrade

Detectors that consume transport context: HeuristicFeatureExtractor (8 features), InconsistencyDetector, MultiLayerCorrelation, ResponseBehavior, AdvancedBehavioral, Header, CacheBehavior.

### Configuration Pattern

Detectors are configured via YAML manifests with appsettings.json overrides:

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "NonAiMaxProbability": 0.90,
    "DefaultActionPolicyName": "throttle-stealth",
    "EnableLlmDetection": true,
    "Detectors": {
      "UserAgentContributor": {
        "Weights": { "BotSignal": 2.0 }
      }
    }
  }
}
```

**Oscillation prevention:** `NonAiMaxProbability` (default 0.90) controls the probability ceiling when AI hasn't run. ConfirmedBad reputation patterns use longer decay tau (12h vs 3h) and wider demotion hysteresis (0.5 vs 0.9) to prevent block/allow flapping. Browser attestation downgrade is configurable via YAML (`browser_attestation_max_confidence`, `browser_attestation_weight`).

## Service Registration

```csharp
// Default: all detectors + Heuristic AI
builder.Services.AddBotDetection();

// User-agent only (minimal)
builder.Services.AddSimpleBotDetection();

// With LLM escalation (requires Ollama)
builder.Services.AddAdvancedBotDetection("http://localhost:11434", "qwen3:0.6b");
```

## Key Patterns

### Zero-PII Architecture
- Raw IP/UA only in-memory, never persisted
- Signatures use HMAC-SHA256 hashing
- Blackboard contains only privacy-safe signals

### Action Policies
Separation of detection (WHAT) from response (HOW):
- `block` - HTTP 403
- `throttle-stealth` - Silent delay
- `challenge` - CAPTCHA/proof-of-work
- `redirect-honeypot` - Trap redirect
- `logonly` - Shadow mode

### HttpContext Extensions
```csharp
context.IsBot()
context.GetBotConfidence()
context.GetBotType()
```

## Adding a New Detector

Every detector touches exactly 5 files. Use `Http3FingerprintContributor` as a reference implementation.

### 5-File Checklist

1. **C# class** - `Orchestration/ContributingDetectors/{Name}Contributor.cs`
   - Inherit `ConfiguredContributorBase` (for YAML config) or `ContributingDetectorBase` (for no-config detectors)
   - Constructor takes `ILogger<T>` + `IDetectorConfigProvider` and calls `base(configProvider)`
   - Override `Name` (string), `Priority` (int), `TriggerConditions` (empty array for Wave 0, or signal triggers for later waves)
   - Implement `ContributeAsync(BlackboardState state, CancellationToken)` returning `IReadOnlyList<DetectionContribution>`
   - Use `GetParam<T>(name, default)` for all tunable values - no magic numbers in code

2. **YAML manifest** - `Orchestration/Manifests/detectors/{name}.detector.yaml`
   - Follows the schema: `name`, `priority`, `enabled`, `scope`, `taxonomy`, `input`, `output`, `triggers`, `emits`, `defaults` (weights, confidence, timing, features, parameters)
   - The `*.yaml` glob in `.csproj` auto-includes it as an embedded resource

3. **SignalKeys** - `Models/DetectionContext.cs`
   - Add constants in the `SignalKeys` class grouped with a section header comment
   - Use hierarchical naming: `h3.protocol`, `h3.client_type`, etc.

4. **DI registration** - `Extensions/ServiceCollectionExtensions.cs`
   - Add `services.AddSingleton<IContributingDetector, {Name}Contributor>();` in the appropriate wave section

5. **Narrative builder** - `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`
   - Add entries to both `DetectorFriendlyNames` and `DetectorCategories` dictionaries

### Key Rules

- **No magic numbers** - all confidence, weight, and threshold values come from YAML `defaults.parameters` via `GetParam<T>()`
- **Always add signals to the last contribution** - the orchestrator reads signals from contributions; ensure the final contribution carries the full `signals.ToImmutable()` dictionary
- **Cross-detector communication** - use `TriggerConditions` (e.g., `SignalExistsTrigger`, `AnyOfTrigger`, `AllOfTrigger`) to declare dependencies, and `GetSignal<T>(state, key)` to read signals from earlier detectors
- **Use helper methods** - `BotContribution()`, `HumanContribution()`, `NeutralContribution()`, `StrongBotContribution()` from `ConfiguredContributorBase`

## Versioning

Uses MinVer with tag prefix `botdetection-v{version}`. NuGet packages auto-version from git tags.

## Target Frameworks

.NET 10.0

## External Dependencies (Local Project References)

The solution uses local project references for development. Related repos that must be cloned as siblings:

```
D:\Source\
├── mostlylucid.stylobot\     # This repo
├── styloflow\                # StyloFlow.Core, StyloFlow.Retrieval.Core
└── mostlylucid.atoms\        # mostlylucid.ephemeral and atoms
```

**From StyloFlow** (`D:\Source\styloflow\`):
- `StyloFlow.Core` - Manifest-driven component configuration
- `StyloFlow.Retrieval.Core` - Signal/analysis wave framework

**From Ephemeral** (`D:\Source\mostlylucid.atoms\mostlylucid.ephemeral\`):
- `mostlylucid.ephemeral` - Core signal sink and coordination
- `mostlylucid.ephemeral.atoms.taxonomy` - DetectionLedger, DetectionContribution, IDetectorAtom
- `mostlylucid.ephemeral.atoms.keyedsequential` - Keyed sequential processing
- `mostlylucid.ephemeral.atoms.slidingcache` - Sliding window cache

**NuGet Packages**:
- **OllamaSharp** - LLM integration (optional)
- **YamlDotNet** - Manifest parsing
- **MathNet.Numerics** - Statistical analysis

## Dashboard

Session-centric dashboard at `/_stylobot` with:
- **Sessions tab** - Timeline of behavioral sessions with Markov chain previews, HTMX drill-in
- **Session detail** - Behavioral radar chart (ApexCharts), transition bar visualization, paths visited
- **Visitors/Top Bots** - Signature-level views with sparklines
- **Countries/Endpoints** - Geographic and path-level aggregations
- **Clusters** - Leiden community detection with diagnostics
- **User Agents** - UA family breakdown with version distribution

**API endpoints:** `/api/sessions`, `/api/sessions/recent`, `/api/sessions/signature/{id}`, `/api/detections`, `/api/summary`, `/api/timeseries`, `/api/clusters`, `/api/countries`, `/api/endpoints`, `/api/topbots`, `/api/me`, `/api/diagnostics`, `/api/export`

## Production Architecture

```
Internet → Cloudflare Tunnel → Caddy (TLS) → YARP Gateway (bot detection) → Website
                                            → Website (direct for /_stylobot* / SignalR)
```

- **Gateway** (`Stylobot.Gateway`) - YARP reverse proxy with all 31 detectors, no dashboard
- **Website** (`mostlylucid.stylobot.website`) - ASP.NET Core MVC + dashboard UI + SignalR hub
- **Caddy** routes `/_stylobot*` directly to website (bypasses gateway for SignalR WebSocket)
- **TimescaleDB** - Dashboard event persistence (commercial); SQLite for core product
- **Ollama** - Local LLM for AI bot classification escalation

Config: `mostlylucid.stylobot.website/docker-compose.local.yml`

## Documentation

Detailed docs in `Mostlylucid.BotDetection/docs/`:
- `quickstart.md` - Getting started with zero dependencies
- `integration-levels.md` - Five integration levels from minimal to YARP gateway
- `blocking-and-filters.md` - All bot type allow flags, geo/network blocking
- `signals-and-custom-filters.md` - Signal access API, custom filters, GeoDetection integration
- `action-policies.md` - Block, Throttle, Challenge, Redirect, LogOnly responses
- `configuration.md` - Full options reference
- `ai-detection.md` - Heuristic model and LLM escalation
- `learning-and-reputation.md` - Adaptive learning system
- `yarp-integration.md` - Reverse proxy setup