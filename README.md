# StyloBot

**Self-hosted bot defense and anonymous entity resolution.** 46 detectors, sub-millisecond inference, progressive identity that learns who keeps coming back — even when they rotate everything. One binary. No cloud dependency. Your traffic never leaves your perimeter.

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.botdetection)](https://www.nuget.org/packages/mostlylucid.botdetection)
[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](https://unlicense.org/)

## Install

```bash
# CLI (macOS + Linux)
brew install scottgal/stylobot/stylobot

# Docker
docker run --rm -p 8080:8080 -e DEFAULT_UPSTREAM=http://host.docker.internal:3000 scottgal/stylobot-gateway:latest

# NuGet (ASP.NET Core)
dotnet add package mostlylucid.botdetection
dotnet add package mostlylucid.botdetection.ui  # dashboard
```

Then:

```bash
# Reverse proxy with live detection table
stylobot 5080 http://localhost:3000

# Production with blocking
stylobot 5080 http://localhost:3000 --mode production --policy block

# Background daemon
stylobot start 5080 http://localhost:3000 --policy block
stylobot status && stylobot logs
```

Or embed as middleware:

```csharp
// Two lines — detection + dashboard, correct middleware ordering guaranteed
builder.Services.AddStyloBot(dashboard => {
    dashboard.AllowUnauthenticatedAccess = true; // dev only
});

app.UseRouting();
app.UseStyloBot();
app.MapControllers();
```

## How it works

StyloBot uses a **blackboard architecture** where 46 detectors run in parallel waves, writing signals that downstream detectors consume. The system progressively builds identity from multiple layers:

```
Request → Wave 0 (< 1ms)      → Wave 1 (behavioral)    → Wave 2 (AI)        → Verdict
          Identity, UA, IP,      Session vectors,          Heuristic model,     Bot probability
          Headers, TLS/TCP,      Periodicity, Cookies,     Intent scoring,      Risk band
          HTTP/2, HTTP/3,        Resource waterfall,       Cluster detection,   Action policy
          Transport, Haxxor      CVE probes, Waveform      LLM escalation       Entity resolution
```

### Vector search projection

The 129-dimensional session vector is projected into interpretable axes for similarity search and visualization:

![Vector Search Projection](docs/images/vector-search-projection.png)

*Left: 129 raw dimensions aggregated into 16 consolidated axes. Right: bot archetype profiles in radar space — scrapers show sharp spiky profiles, headless browsers show broad spread, humans show low uniform values. Bottom: temporal evolution as the fingerprint crystallizes over time.*

## Detection surface — 46 detectors

| Layer | Detectors | What it catches |
|-------|-----------|-----------------|
| **Identity** | Signature, HeaderCorrelation, Periodicity | UA rotation, identity factors, temporal patterns |
| **Protocol** | TLS (JA3/JA4), TCP/IP (p0f), HTTP/2, HTTP/3, Transport, StreamAbuse | Spoofed browser fingerprints, protocol inconsistencies |
| **Behavioral** | Waveform, SessionVector, AdvancedBehavioral, CacheBehavior, CookieBehavior, ResourceWaterfall | Timing patterns, Markov chains, missing assets, cookie ignoring |
| **Content** | UserAgent, Header, AiScraper, Haxxor, SecurityTool, VersionAge | Known bots, attack payloads, impossible browser versions |
| **Network** | IP, GeoChange, ResponseBehavior, MultiLayerCorrelation, CveProbe | Datacenter IPs, impossible travel, CVE scanning, cross-layer mismatches |
| **Intelligence** | FastPathReputation, ReputationBias, TimescaleReputation, Cluster, Similarity, Intent | Historical reputation, Leiden clustering, HNSW similarity, threat scoring |
| **AI** | Heuristic, HeuristicLate, LLM | 50-feature ML model (<1ms), optional LLM for ambiguous cases |
| **Client** | ClientSide, FingerprintApproval, ChallengeVerification, PiiQueryString | JS timing probes, headless detection (Puppeteer/Playwright/Selenium named), PoW challenges |

### Key capabilities

- **Sub-millisecond fast path** — 46 detectors, ~150µs per request, ~175KB allocation
- **Anonymous entity resolution** — progressive identity (L0→L5) with merge/split/rewind. Rotation creates a trail of near-miss fingerprints that get linked back to the same actor
- **129-dim session vectors** — Markov chain transitions + timing + fingerprints. Partial chain archetypes detect bots at 3-5 requests before full session maturity
- **Simulation packs** — honeypots that look like real products. WordPress 5.9 pack included with 8 CVE modules
- **Zero PII** — HMAC-SHA256 hashed signatures. Raw UAs stored PII-stripped (emails/phones redacted). No raw IPs persisted
- **Headless framework naming** — identifies Puppeteer, Playwright, Selenium, PhantomJS by name, not "Unknown Bot"

## Pricing

**FOSS is the full product.** Commercial adds persistence, fleet management, and live config editing.

| | **FOSS** | **Commercial** |
|---|---|---|
| **Price** | Free forever | $100/mo per domain |
| **Detectors** | All 46 | All 46 |
| **Entity resolution** | Full | Full |
| **Dashboard** | Full (read-only config) | Live config editor |
| **Persistence** | SQLite (zero-dependency) | PostgreSQL + pgvector |
| **UA search** | Full-text (SQLite LIKE) | Full-text (PostgreSQL) |
| **Simulation packs** | WordPress (FOSS) | All frameworks |
| **Identity inspector** | — | Entity graph explorer |
| **Fleet management** | — | Redis backplane, multi-node |
| **SSO** | — | OIDC/SAML |

After a commercial trial ends, the system reverts to FOSS mode. Detection never stops — only commercial persistence and UI features deactivate.

## LLM providers

Detection works fully without any LLM. LLM enriches bot names and handles ambiguous cases.

```bash
stylobot 5080 http://localhost:3000 --llm ollama          # local, free (default: gemma4)
stylobot 5080 http://localhost:3000 --llm openai --llm-key sk-...
stylobot 5080 http://localhost:3000 --llm anthropic --llm-key sk-ant-...
```

| Provider | Default model | Cost |
|----------|---------------|------|
| `ollama` | gemma4 | Free (local) |
| `openai` | gpt-4o-mini | ~$0.15/1M tokens |
| `anthropic` | claude-haiku-4-5 | ~$0.25/1M tokens |
| `gemini` | gemini-2.0-flash | Free tier |
| `groq` | llama-3.3-70b | Free tier |

## Dashboard

Real-time monitoring at `/stylobot`. All data persists to SQLite.

- **Overview** — top threats, traffic chart, world threat map
- **Visitors** — signature-level cards with probability badges (Bot/Suspicious/Uncertain/Human)
- **Sessions** — Markov chain timeline with behavioral radar and session playback
- **Threats** — CVE probe feed, honeypot engagements, severity badges
- **Clusters** — Leiden community detection visualization
- **User Agents** — family breakdown, version distribution, full-text search
- **Configuration** — Monaco YAML editor (read-only in FOSS)

## Repo layout

```
Mostlylucid.BotDetection/          Core detection library (NuGet)
Mostlylucid.BotDetection.UI/       Dashboard + SignalR hub (NuGet)
Mostlylucid.BotDetection.Api/      Public REST API
Mostlylucid.BotDetection.Demo/     Dev harness / reference app
Mostlylucid.BotDetection.Console/  Standalone CLI (6 platforms)
Stylobot.Gateway/                   Docker YARP reverse proxy
test-bdf-scenarios/                 BDF replay test scenarios
docs/                               Architecture + specs
```

## Requirements

- No external dependencies for FOSS (SQLite is embedded)
- .NET 10.0 (for building from source)
- Commercial: PostgreSQL, optional Redis

## Documentation

- [Quick start](Mostlylucid.BotDetection/docs/quickstart.md)
- [Configuration reference](Mostlylucid.BotDetection/docs/configuration.md)
- [Integration levels](Mostlylucid.BotDetection/docs/integration-levels.md)
- [Action policies](Mostlylucid.BotDetection/docs/action-policies.md)
- [CHANGELOG](Mostlylucid.BotDetection/CHANGELOG.md)

## License

[The Unlicense](https://unlicense.org/) — FOSS core is public domain. Commercial features licensed separately.
