# Release Checklist

## Pre-RTM (v5.6)

### Build & Test
- [x] `dotnet build` - 0 errors across 20 projects
- [x] `dotnet test` - 1006 pass, 0 failures (core), 363 pass (orchestration, 9 pre-existing Postgres integration failures)
- [x] k6 load test - 0.6ms detection p95 at 100 VUs
- [x] Cross-platform build - linux/amd64 Docker image builds and runs on staging

### Security Audit
- [x] CRITICAL-1: HMAC token secret auto-generates (no guessable fallback)
- [x] CRITICAL-2: returnUrl open redirect sanitized
- [x] CRITICAL-3: Token secret propagation fixed (EffectiveTokenSecret)
- [x] HIGH-1: Training endpoints require API key by default
- [x] HIGH-2: Policy mutation endpoints require authorization
- [x] HIGH-3: Dashboard defaults to deny when no auth configured
- [x] HIGH-4: X-SB-Labeler only honored when authenticated
- [x] MEDIUM-1: PoW solution index validation
- [x] MEDIUM-2: BDF replay header injection blocked
- [x] MEDIUM-3: Rate limiter dictionaries bounded at 10K
- [x] MEDIUM-4: Token removed from JSON verify response
- [x] LOW-3: Exception message no longer leaked in BDF replay

### In-Memory Audit
- [x] All 16 unbounded ConcurrentDictionary stores bounded or converted to BoundedCache/SQLite
- [x] ChallengeStore: SQLite (was InMemory)
- [x] FingerprintApprovalStore: SQLite
- [x] 6 lookup caches converted to BoundedCache (ASN, Honeypot, RDNS, CIDR, VerifiedBot DNS)
- [x] GeoChange, AccountTakeover, MarkovTracker, DriftDetection, SignatureCoordinator: eviction tightened

### Dashboard
- [x] World threat map rendering (jsVectorMap)
- [x] Traffic-over-time chart (ApexCharts)
- [x] Sessions in signature detail with Markov chain drill-in
- [x] Behavioral shape radar chart with session stepping
- [x] Deterministic bot naming (no more "Unknown")
- [x] Overview restructured: Top Threats above fold
- [ ] Dashboard screenshots for website (need live traffic first)

### Pricing & Website
- [x] Pricing page: $100/mo per domain, competitive comparison
- [x] Homepage: launch banner, clearer copy, no jargon
- [x] Features page: commercial features section added
- [x] README: pricing table, unlimited requests, LLM-enhanced positioning

### Infrastructure
- [x] Staging deployed: 192.168.0.89:8090
- [x] Dependencies released: atoms v2.4.0, styloflow v2.5.0
- [x] Dependabot alerts cleared (basic-ftp)
- [x] IPv4-mapped IPv6 subnet bug fixed (was causing false positives)

## Post-RTM
- [ ] Async SQLite operations (read caches cover 99%)
- [ ] Endpoint policy management UI
- [ ] Dashboard approval form (Razor partial)
- [ ] Radar chart session animation
- [ ] Website Docker image rebuild (commercial deps need resolving)
- [ ] Stripe integration for self-serve billing
