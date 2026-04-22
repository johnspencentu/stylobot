# Simulation Packs + Partial Markov Chain Early Detection

**Date**: 2026-04-20
**Status**: Approved

## Overview

Two independent features that strengthen StyloBot's honeypot and early detection capabilities:

1. **Simulation Pack Framework** - make honeypots look like real unpatched products (WordPress FOSS, others commercial)
2. **Partial Markov Chain Early Detection** - score first 3-5 requests against behavioral archetypes

## Feature 1: Simulation Pack Framework

### Concept

A simulation pack makes StyloBot's honeypot behave like an unpatched version of a real product. Bots probing for WordPress vulns get realistic responses - login forms, xmlrpc, REST API, wp-admin - including CVE-specific endpoints that simulate known vulnerabilities. The bot thinks it found a real target, wastes time, and StyloBot records everything.

CVE probe telemetry feeds back into the threat intel detector, enabling StyloBot to learn which CVEs bots are actively targeting and prioritize accordingly as attack patterns evolve.

### Architecture

```
SimulationPack (YAML, embedded resource)
├── identity: name, framework, version
├── honeypot_paths: framework-specific trap paths with scoring
├── response_templates: realistic HTML/JSON/XML per path
│   └── timing profiles (WP is slow - simulate realistic latency)
├── cve_modules: CVEs to simulate
│   ├── cve_id, affected_versions, severity
│   ├── probe_paths: paths that trigger this CVE
│   └── response: vulnerable-looking response template
├── context_memory_hints: per-bot state tracking config
└── signals: what signals to emit when paths are hit
```

### Models (SimulationPack.cs in core project)

```csharp
SimulationPack
  - Id, Name, Framework, Version, Description
  - HoneypotPaths: List<PackHoneypotPath>
  - ResponseTemplates: List<PackResponseTemplate>
  - CveModules: List<PackCveModule>
  - TimingProfile: PackTimingProfile
  - Signals: List<PackSignalDefinition>

PackHoneypotPath
  - Path (glob pattern), Confidence, Weight, Category

PackResponseTemplate
  - PathPattern, StatusCode, ContentType, Body, Headers
  - DelayMs (min/max for realistic timing)

PackCveModule
  - CveId, Severity, AffectedVersions
  - ProbePaths, ProbeResponse (template)
  - Signals emitted when probed

PackTimingProfile
  - MinResponseMs, MaxResponseMs, JitterMs
```

### WordPress Pack (FOSS)

Embedded YAML in `Mostlylucid.BotDetection/SimulationPacks/Packs/wordpress.yaml`.

Paths: wp-login.php (fake login form HTML), xmlrpc.php (pingback response XML), /wp-json/wp/v2/users (fake user list JSON), /wp-admin/ (302 to wp-login), /wp-content/plugins/ (directory listing), /wp-includes/version.php, /readme.html (WP version disclosure), /wp-cron.php, /wp-config.php.bak (fake config with dummy DB creds).

CVE modules (initial set):
- CVE-2024-6386 (WPML RCE) - /wp-admin/admin-ajax.php with specific POST
- CVE-2023-2982 (miniOrange bypass) - /wp-json/mo/v1/
- CVE-2023-32243 (Starter Templates RCE) - /wp-json/starter-templates/
- CVE-2022-21661 (WP Core SQL injection) - /wp-json/wp/v2/posts with crafted query

### Integration Points

1. **SimulationPackLoader** loads YAML at startup, registers paths with HoneypotLinkContributor
2. **HolodeckActionPolicy** uses pack response templates instead of forwarding to MockLLMApi when a pack path is hit
3. **ResponseBehaviorContributor** emits CVE-specific signals (e.g., `cve.probe.CVE-2024-6386`)
4. **Threat intel feedback**: CVE probe signals flow to the existing threat intel/learning system for prioritization over time

### File Locations

- `Mostlylucid.BotDetection/SimulationPacks/SimulationPack.cs` - models
- `Mostlylucid.BotDetection/SimulationPacks/SimulationPackLoader.cs` - YAML loader + registry
- `Mostlylucid.BotDetection/SimulationPacks/SimulationPackResponder.cs` - serves fake responses with timing
- `Mostlylucid.BotDetection/SimulationPacks/Packs/wordpress.yaml` - WordPress pack
- Wire into existing DI in `ServiceCollectionExtensions.cs`

## Feature 2: Partial Markov Chain Early Detection

### Concept

Score the first 3-5 requests against known behavioral archetypes before waiting for full session maturity. An LLM bot going PageView->PageView->PageView (no assets) is already suspicious by request 3.

### Implementation

Add to `SessionVectorContributor.cs` - not a new contributor. Insert before the existing maturity gate.

### Archetypes (hardcoded, configurable via YAML)

| Name | Pattern | Bot Signal |
|------|---------|------------|
| human-browser | PageView->StaticAsset->StaticAsset->PageView | -0.15 (human) |
| scraper | PageView->PageView->PageView | +0.25 (bot) |
| api-bot | ApiCall->ApiCall->ApiCall | +0.30 (bot) |
| scanner | PageView->NotFound->NotFound | +0.35 (bot) |
| auth-bot | PageView->FormSubmit->AuthAttempt->AuthAttempt | +0.30 (bot) |

### Scoring

- Build partial transition vector from first N transitions (only dims [0..99])
- L2-normalize
- Cosine similarity against each archetype's partial vector
- Best match above threshold (0.6 similarity) emits signal
- Confidence scaled: 0.15-0.35 range (early signal, not conviction)
- Configurable via sessionvector.detector.yaml `partial_chain_*` params

### Signals Emitted

- `session.partial_chain_match` - archetype name
- `session.partial_chain_similarity` - cosine similarity score
- `session.partial_chain_confidence` - contribution confidence

## Non-Goals

- Commercial packs (Django, Rails, Laravel, Spring Boot) - separate repo
- Pack marketplace/dynamic loading - future
- Partial chain ML training - hardcoded archetypes first, learn later