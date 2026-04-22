# Changelog

All notable changes to StyloBot are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased] - v5.6

### Added

#### Commercial Plugin Architecture
- **IConfigurationOverrideSource** - FOSS extension interface for commercial per-target config overrides (per-endpoint, per-user, per-API-key detector tuning)
- **IFleetReporter** - FOSS extension interface for commercial fleet telemetry reporting across multi-gateway deployments
- **IDetectionEventPublisher** - extension point for out-of-process dashboard UIs
- **FileSystemConfigurationOverrideSource** - FOSS hot-reload implementation for YAML-file-based config changes without restart
- **Signature labeling infrastructure** - groundwork for the upcoming detector weighting pass

#### Customer Portal (stylobot.net)
- **Keycloak OIDC integration** - portal auth scaffold with organization management
- **LicenseIssuer** - Ed25519-signed JWT license issuance with trial request, download, rotate, and revoke
- **Domain-based license entitlement** - DomainEntitlementValidator + cloud-pool host list; signed JWTs include `domains[]` claim
- **Team invites + audit log** - org member management with full audit trail UI
- **Personal API tokens** - `/api/v1/orgs/{slug}/licenses/current` for programmatic license access
- **BurstWorkUnitsPerMinute** mapping to StyloFlow licensing payload

#### Pipeline Coordination (spec)
- **Distributed-blackboard model** - chained YARP instances (edge - regional - app-side) avoid redundant detector execution via input-hash-per-detector deduplication
- **Layered action policies** - monotone-escalating policy cascade: `block` at an inner hop cannot be softened by an outer hop's `allow`

#### Dashboard Enhancements
- **Monaco YAML config editor** - in-dashboard configuration viewer (read-only in FOSS, live-edit in commercial)
- **FOSS licensing v1 wiring** - license status display in dashboard
- **World threat map** - jsVectorMap with countries colored by bot rate (green-amber-red gradient), 30s auto-refresh
- **Traffic-over-time chart** - ApexCharts area chart with Human/Bot series, 15s auto-refresh
- **Sessions in signature detail** - HTMX-loaded session timeline with Markov chain transition previews, path sequences, timing entropy
- **Behavioral shape radar chart** - 129-dim session vector projected into 8 interpretable radar axes with session stepping (prev/next)
- **Dashboard overview redesign** - Top Threats above fold, actionable intelligence first

#### Hardened Proof-of-Work Challenge
- **SHA-256 micro-puzzles** - Web Worker pool (up to `navigator.hardwareConcurrency`) solves puzzles in parallel
- **Blackboard-driven difficulty** - puzzle count and zeros scale with session velocity, cluster membership, reputation bias, threat score
- **Transport-aware** - API/SignalR/gRPC clients get 429 + JSON challenge, not HTML
- **Challenge-as-signal feedback loop** - ChallengeVerificationContributor reads solve metadata, emits human/bot signals based on timing characteristics
- **SqliteChallengeStore** - persistent challenge store (was in-memory)

#### Fingerprint Approval System
- **IFingerprintApprovalStore** - SQLite-backed approval with locked dimensions and audit trail
- **Locked dimensions** - behavioral contract: country, UA, IP CIDR constraints checked against live signals on every request
- **FingerprintApprovalContributor** - strong human signal (-0.4 delta) when approved with matching dimensions, strong bot signal (+0.3) on dimension mismatch (catches stolen credentials)
- **X-SB-Approval-Id header** - one-time approval token for borderline requests (opt-in)
- **Dashboard approval API** - full CRUD + token-based approval flow

#### Per-Transition Timing
- **3 new session vector dimensions** (126-128): impossible timing ratio, timing consistency score, fastest transition z-score
- Session vector now 129 dimensions (was 126/118)
- `CosineSimilarity` handles dimension mismatch via zero-padding for migration

#### Bot Naming
- **DeterministicBotNameSynthesizer** - generates names from signals without LLM: "Rapid Scraper", "Headless Python Bot", "Targeted Scanner"
- Replaces NoOpBotNameSynthesizer as default; LLM packages override via TryAddSingleton

#### Response Headers (opt-in)
- **X-SB-Reason** - top contributing detector reason (PII-free, 200 char max)
- **X-SB-Approval-Id** - one-time fingerprint approval token for borderline requests

### Changed

- Site repositioned as security product (not a tech demo)
- Detector-weights audit and benchmark artifact cleanup
- Bumped all dependencies to latest, cleared Dependabot alerts
- Pricing: $100/mo per domain (unlimited requests, no per-request metering)
- **BoundedCache** replaces raw ConcurrentDictionary across 6 lookup services (ASN, Honeypot, RDNS, CIDR, VerifiedBot DNS)
- Read-through caches on FingerprintApprovalStore and ChallengeStore (eliminates 50-500us/req SQLite hits)
- AccountTakeoverContributor eviction: O(N^2) -> O(N log N)
- GeoChangeContributor: two-phase pruning (expire + LRU)
- MarkovTracker: MaxTrackedSignatures with eviction
- SignatureCoordinator: shadow index pruning
- DriftDetectionHandler: bounded at 10K patterns/50 samples per pattern

### Fixed

- Five bugs found running the Phase 1 portal end-to-end
- Normalized em-dash characters to hyphens for consistent documentation style
- Synced reputation/decay tests to post-oscillation-fix behavior
- **IPv4-mapped IPv6 subnet classification** - `::ffff:x.x.x.x` addresses were grouped into `::ffff::/48` subnet, causing ALL IPv4-mapped addresses to share reputation. Fixed to extract IPv4 and use /24

### Security

- **CRITICAL-1**: HMAC token secret auto-generates cryptographically random secret (no guessable fallback)
- **CRITICAL-2**: returnUrl open redirect fixed (rejects absolute URLs, protocol-relative, scheme injection)
- **CRITICAL-3**: Token secret propagation fixed (EffectiveTokenSecret across requests)
- **HIGH-1**: TrainingEndpoints RequireApiKey defaults to true
- **HIGH-2**: Policy mutation endpoints require authorization
- **HIGH-3**: Dashboard defaults to deny when no auth configured (AllowUnauthenticatedAccess flag)
- **HIGH-4**: X-SB-Labeler header only honored when authenticated
- **MEDIUM-1**: PoW solution SeedIndex validated server-side
- **MEDIUM-2**: BDF replay header injection blocked (X-SB-*, X-Bot-*, Host, X-Forwarded-For)
- **MEDIUM-3**: Rate limiter dictionaries bounded at 10K entries
- **MEDIUM-4**: Raw HMAC token removed from verify JSON response

---

## [5.5.0] - 2026-03-15

### Added

#### Session Vector Architecture
- **SessionVectorizer** - per-request Markov chain transitions compressed into 118-dimensional normalized vectors (100 transition probabilities + 10 stationary distribution + 8 temporal features + 8 fingerprint features)
- **Retrogressive session boundary detection** - sessions defined by inter-request gaps (default 30min), detected when the NEXT request reveals the gap
- **Inter-session velocity analysis** - L2 magnitude of delta vectors between consecutive sessions detects sudden behavioral shifts (bot rotation, account takeover)
- **Snapshot compaction** - old session snapshots merge into maturity-weighted root vector, preserving behavioral baseline while discarding per-session detail
- **Unified fingerprint dimensions** - TLS/TCP/H2 fingerprints are vector dimensions in the same space as behavioral features; fingerprint mutation across sessions appears as velocity

#### SQLite Persistence
- **SqliteSessionStore** - zero-dependency session persistence (sessions, signatures, 1-minute counter buckets)
- **SessionPersistenceService** - background service bridging in-memory SessionStore events to SQLite
- ~100x compression vs per-request storage (200 sessions/day vs 10,000 requests/day)

#### Transport-Aware Detection
- **TransportProtocolContributor** (Priority 5) - classifies request transport context: document, API, SignalR, gRPC, static, WebSocket, SSE
- Seven existing detectors now consume transport context to suppress false positives on non-document traffic: HeuristicFeatureExtractor, InconsistencyDetector, MultiLayerCorrelation, ResponseBehavior, AdvancedBehavioral, Header, CacheBehavior

#### Oscillation Prevention
- **NonAiMaxProbability** (default 0.90) - configurable probability ceiling when AI hasn't run
- **State-aware reputation decay** - ConfirmedBad uses longer decay tau (12h vs 3h) and wider demotion hysteresis (0.5 vs 0.9) to prevent block/allow flapping
- **Browser attestation downgrade** - configurable via YAML (`browser_attestation_max_confidence`, `browser_attestation_weight`)

#### Dashboard
- **Sessions tab** - timeline with Markov chain previews, HTMX drill-in to session detail
- **Session detail view** - behavioral radar chart (ApexCharts), transition bar visualization, paths visited
- **Fail2ban-style escalating action policies** for persistent 404 abuse patterns

### Changed

- Dashboard detector count increased to 31 (SessionVector added to Wave 1)
- Session vector benchmarks added to benchmark suite

### Fixed

- ProcessingTimeMs nullable handling in PostgreSQL event store
- Nullable double coalescing in PostgreSQL event store

---

## [5.0.0] - 2026-02-22

### Added

#### Intent Classification and Threat Scoring
- **IntentContributor** - new Wave 3 detector that classifies request intent (reconnaissance, exploitation, scraping, benign, etc.) using HNSW-backed similarity search and cosine vectorization
- **Threat scoring orthogonal to bot probability** - a human probing `.env` files has low bot probability but high threat score; both dimensions are now independently surfaced
- **ThreatBand enum** - `None`, `Low`, `Elevated`, `High`, `Critical` with configurable score thresholds (0.15 / 0.35 / 0.55 / 0.80)
- **IntentClassificationCoordinator** - orchestrates intent vectorization, similarity search, and threat band assignment
- **HnswIntentSearch** - HNSW approximate nearest-neighbor index for real-time intent matching with configurable M/efConstruction/efSearch parameters
- **IntentVectorizer** - converts request features (path patterns, method, headers) into dense vectors for similarity search
- **IntentLearningHandler** - feeds confirmed intent classifications back into the HNSW index for adaptive improvement
- Intent signals: `intent.category`, `intent.threat_score`, `intent.threat_band`, `intent.confidence`, `intent.similarity_score`, `intent.nearest_label`

#### Dashboard Threat Visualization
- Threat badges on detection detail, "your detection" panel, visitor list rows, and cluster cards
- Cluster enrichment: `DominantIntent` (most common intent) and `AverageThreatScore` per cluster
- Narrative enhancement: threat qualifier prefix on bot narratives (`CRITICAL THREAT:`, `High-threat`, `Elevated-threat`)
- `intent.*` signals in dedicated "Intent / Threat" signal category with target icon
- `threatBandClass()` helper for DaisyUI badge coloring by threat band
- Threat data in all API endpoints: `/api/detections`, `/api/signatures`, `/api/topbots`, `/api/clusters`, `/api/me`, `/api/diagnostics`
- Threat data in CSV export
- Threat data in SignalR real-time broadcasts

#### Stream and Transport Detection
- **StreamAbuseContributor** - new Wave 1 detector that catches attackers hiding behind streaming traffic using per-signature sliding window tracking
- Stream abuse patterns: connection churn, payload flooding, protocol switching, rapid reconnection
- `stream-abuse.detector.yaml` manifest with configurable thresholds for all abuse patterns
- Enhanced **TransportProtocolContributor** - improved WebSocket, SSE, SignalR, gRPC, and GraphQL classification with `transport.is_streaming` signal for downstream consumption
- Five existing detectors now consume `transport.is_streaming` to suppress false positives on legitimate streaming traffic (CacheBehavior, BehavioralWaveform, AdvancedBehavioral, ResponseBehavior, MultiFactorSignature)
- Documentation: [`stream-transport-detection.md`](Mostlylucid.BotDetection/docs/stream-transport-detection.md)

#### Detection Accuracy Improvements
- Enhanced **BehavioralWaveformContributor** - stream-aware burst thresholds, excludes streaming requests from page rate calculations
- Enhanced **CacheBehaviorContributor** - skips cache validation for streaming requests entirely
- Enhanced **AdvancedBehavioralContributor** - skips path entropy, navigation pattern, and burst analysis for streaming
- Enhanced **ResponseBehaviorContributor** - new signals for response analysis
- Updated response behavior, transport protocol, and stream abuse detector YAML manifests
- **PolicyEvaluator** improvements - threat-aware policy evaluation
- **DetectionPolicy** updates - new policy fields for threat-based responses

#### Infrastructure
- New `HttpContext` extension methods for intent/threat access
- `BotCluster` enrichment with `DominantIntent` and `AverageThreatScore`
- `BotClusterService` computes cluster-level intent and threat aggregates
- `ILearningEventBus` extensions for intent learning feedback
- `DetectionLedgerExtensions` - threat band computation from aggregated evidence
- `DetectionContribution` - `ThreatBand` enum and threat fields on `AggregatedEvidence`
- Updated `BotDetectionOptions` with intent detection configuration
- Updated `ServiceCollectionExtensions` with intent detector registration

### Changed

- Dashboard now shows 30 detectors (was 29) - IntentContributor added to Wave 3
- Default `EnabledDetectorCount` increased to 30
- Cluster visualization includes threat percentage and dominant intent
- Bot narratives include threat qualifier prefix for elevated+ threats
- Diagnostics endpoint now includes `ThreatScore`/`ThreatBand` on detections, signatures, and top bots

### Fixed

- Missing `threatBandClass` function in inline Razor dashboard script (NuGet package users would have gotten a JS ReferenceError)
- Missing `Critical` threshold (>= 0.80) in cluster threat badge ternary
- Visitor row threat badge missing DaisyUI `badge` class (visual rendering was inconsistent)
- Removed dead `threatBandColor` function from dashboard.ts

### Documentation

- [`dashboard-threat-scoring.md`](Mostlylucid.BotDetection/docs/dashboard-threat-scoring.md) - full architecture, data flow, API endpoints, UI elements, security considerations
- [`stream-transport-detection.md`](Mostlylucid.BotDetection/docs/stream-transport-detection.md) - stream-aware detection architecture, transport classification, abuse patterns
- [`transport-protocol-detection.md`](Mostlylucid.BotDetection/docs/transport-protocol-detection.md) - updated with streaming classification
- Updated `SESSION_SUMMARY.md` with v5 section

---

## [4.0.0] - 2026-01-25

### Added

- Programmatic request attestation via `Sec-Fetch-Site` headers
- YARP API key passthrough for upstream services
- BDF (Bot Detection Format) export/replay system
- Standardized signal key usage across all contributors

## [3.0.0] - 2025-12-15

### Added

- Real-time dashboard with interactive world map
- Country analytics and reputation tracking
- Cluster visualization (Leiden algorithm)
- User agent breakdown with category badges
- Live signature feed with risk bands and sparklines
- SignalR-based live updates
- Server-side rendering for initial dashboard load

## [2.0.0] - 2025-10-01

### Added

- Wave-based detection pipeline (4 waves)
- Protocol-level fingerprinting (JA3/JA4, p0f, AKAMAI, QUIC)
- Heuristic AI model with ~50 features per request
- Action policies (block, throttle, challenge, redirect, logonly)
- Training data API for ML export
- PostgreSQL/TimescaleDB persistence layer

## [1.0.0] - 2025-07-01

### Added

- Initial release with 20 detectors
- Blackboard architecture via StyloFlow
- Zero-PII design with HMAC-SHA256 signatures
- YARP reverse proxy integration
- Basic dashboard