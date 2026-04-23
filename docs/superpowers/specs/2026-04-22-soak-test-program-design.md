# Soak Test Program & External Call Compliance Audit

**Date:** 2026-04-22
**Status:** Approved
**Scope:** Multi-machine soak testing with BDF replay, graduated stage gates, perf telemetry, and external call audit

---

## Problem

StyloBot has 1,569 passing unit tests and benchmark data for individual detectors, but no long-running stability validation. Memory leaks, detection drift, SQLite growth, GC pressure, and thread exhaustion only manifest under sustained load over hours. Additionally, compliance requires a documented inventory of all external network calls.

## Solution

A graduated soak test program (smoke → soak → endurance) using k6 with BDF signature replay, running across multiple machines. Includes automated metric collection, gate checking, and external call auditing.

---

## Test Infrastructure

### Machines

| Machine | IP/Name | Role | Specs |
|---------|---------|------|-------|
| Linux | 192.168.0.89 | SUT (Gateway/Demo) | Primary deployment target |
| Windows | maxo | Ollama LLM + k6 load gen | 9950X, GPU |
| Mac | localhost | k6 load gen + orchestrator + monitoring | Dev machine |

### Topology

```
Mac (k6: human + attack traffic, 35 req/s)
    ↓
Linux 192.168.0.89 (Demo app, SQLite, all detectors)
    ↑                          ↓
Windows maxo (k6: BDF bot replay, 15 req/s)   (Ollama LLM on maxo:11434)
```

Linux SUT points LLM config at `http://maxo:11434`.

---

## Test Stages

### Stage 1: Smoke (30 minutes)

Proves system starts, detects correctly, doesn't crash. Run before every soak.

| Parameter | Value |
|-----------|-------|
| Duration | 30 minutes |
| Rate | 10 req/s mixed |
| Traffic | All 81 BDF signatures replayed once + human browsing |

**Gate thresholds:**
- 0 crashes/OOM
- Detection accuracy > 85%
- p95 latency < 500ms
- Memory RSS < 500MB
- Error rate < 5%

### Stage 2: Soak (8 hours)

Sustained mixed traffic. Reputation builds, sessions accumulate.

| Parameter | Value |
|-----------|-------|
| Duration | 8 hours |
| Rate | 50 req/s sustained |
| Traffic | Human 60% (30 req/s), Bot BDF 30% (15 req/s), Attack 10% (5 req/s) |

**Gate thresholds:**
- p95 < 1000ms at hour 8 (no degradation vs hour 1)
- Memory RSS < 1.5GB
- Memory delta (hour 8 vs hour 1) < 500MB
- Accuracy stable (hour 8 vs hour 1 within 5%)
- False positive rate < 3%
- Zero OOM/restarts
- Error rate < 5%

### Stage 3: Endurance (72 hours)

Long-term stability proof. Only after Stage 2 passes.

| Parameter | Value |
|-----------|-------|
| Duration | 72 hours |
| Rate | 50 req/s sustained |
| Traffic | Same as Stage 2 |

**Gate thresholds:**
- Same as Stage 2 at 72h mark
- Memory delta (hour 72 vs hour 1) < 200MB
- SQLite DB size stable (not growing unbounded)
- GC Gen2 count growth rate stable (not accelerating)

---

## Traffic Composition

### Human browsing (60%, from Mac)

- 40 browser UA profiles (Chrome, Firefox, Safari, Edge- desktop + mobile)
- Realistic navigation: page → assets → API calls → next page
- Sec-Fetch-* headers, Accept-Language, cookies
- Mixed referers (Google, direct, internal)
- Source: existing `k6-soak.js` human scenarios

### BDF bot replay (30%, from Windows)

- All 81 `bot-signatures/*.json` files cycling continuously
- Each k6 VU = one persistent bot identity (consistent fingerprint per BDF file)
- Replays with `timingProfile` delays (burst + pause patterns)
- After last request in BDF, pause, restart cycle
- Exercises: reputation learning, session accumulation, signature growth
- Source: `bot-signatures/` directory

### Attack traffic (10%, from Mac)

- 13 `test-bdf-scenarios/` (including 6 adversarial)
- SQL injection, XSS, path traversal, credential stuffing, PII exfil
- Honeypot path probing (wp-login, .env, phpmyadmin)
- Exercises: Haxxor detector, intent scoring, holodeck engagement, beacon tracking
- Source: `test-bdf-scenarios/` directory

---

## Metrics Collection

### Per-SUT metrics (every 30 seconds)

| Metric | Source |
|--------|--------|
| `process_rss_mb` | `/admin/metrics` |
| `process_cpu_pct` | `/admin/metrics` |
| `gc_gen0_count` | `/admin/metrics` |
| `gc_gen2_count` | `/admin/metrics` |
| `threadpool_threads` | `/admin/metrics` |
| `requests_total` | `/admin/metrics` |
| `active_connections` | `/admin/metrics` |
| `sqlite_db_size_kb` | `/admin/metrics` |
| `active_signatures` | `/api/v1/summary` |
| `active_sessions` | `/api/v1/summary` |
| `detection_p50_ms` | k6 custom metric |
| `detection_p95_ms` | k6 custom metric |
| `detection_p99_ms` | k6 custom metric |
| `detection_accuracy` | k6 (BDF expected vs actual) |
| `false_positive_rate` | k6 (human flagged as bot) |

### Node config file

```yaml
# soak-nodes.yaml
nodes:
  - host: 192.168.0.89
    role: sut
    name: linux-primary
    metrics_port: 5080
  - host: maxo
    role: k6+llm
    name: windows-maxo
  - host: localhost
    role: k6+orchestrator
    name: mac-dev
```

Scales to 8+ nodes by adding lines.

### Live terminal output

```
[14:32:30] linux-primary  | RSS: 892MB (+47MB) | CPU: 23% | p95: 423ms | GC2: 12 | sigs: 1,847 | sess: 342
[14:32:30] windows-maxo   | Ollama active | k6: 15 req/s
[14:32:30] mac-dev        | k6: 35 req/s
─────────────────────────────────────────────────────────────────────────────────────
Stage: SOAK | Elapsed: 4h 12m | Total: 756,230 req | Accuracy: 91.2% | Errors: 0.3%
```

---

## Gate Checker

`soak-gate-check.mjs` reads metrics CSV + k6 JSON, evaluates thresholds:

```
Stage 2 SOAK- Gate Check
  ✓ p95_latency: 423ms (< 1000ms)
  ✓ memory_rss: 892MB (< 1500MB)
  ✓ memory_delta: +47MB (< 500MB)
  ✓ accuracy_drift: 0.4% (< 5%)
  ✓ false_positive_rate: 1.2% (< 3%)
  ✓ error_rate: 0.3% (< 5%)
  ✓ crashes: 0
  RESULT: PASS
```

Exit code 0 = pass, 1 = fail.

---

## External Call Compliance Audit

### Documented external calls

| Call | Target | When | Frequency | Data sent | PII risk |
|------|--------|------|-----------|-----------|----------|
| Verified bot IP ranges | `developers.google.com`, `bing.com/toolbox`, `openai.com` | Startup + refresh | 24h | GET only | None |
| GeoIP database | `datahub.io` | Startup if no local DB | Once | GET only | None |
| GeoLite2 update | `download.maxmind.com` | If configured | Daily (configurable) | License key | None |
| Project Honeypot DNS | `dnsbl.httpbl.org` | Slow path, per new IP | Per new IP | Client IP (DNS encoded) | IP address |
| Bot list update | Configured URL | Cron schedule | Daily 2AM UTC | GET only | None |
| Ollama LLM | Configured endpoint | Per LLM escalation | Bounded async queue | Request metadata (UA, headers, path) | Pseudonymized |
| Cloud LLM | Anthropic/OpenAI/Gemini API | Per LLM escalation (if configured) | Bounded | Same as Ollama | Pseudonymized |
| Project Honeypot reporting | `projecthoneypot.org` | If enabled | Per high-risk detection | Client IP | IP address |

### What does NOT make external calls

- Detection pipeline (fully local)
- SQLite persistence (local file)
- Dashboard (no CDN, no analytics)
- Session/signature/reputation stores (local)
- Beacon tracking (local SQLite)
- Holodeck responses (local LLM or static)

### Soak test verification

During soak, outbound connections are captured every 5 minutes via `ss -tnp` on the SUT. Gate checker verifies:
- No unexpected external calls
- Project Honeypot DNS respects rate limits
- LLM calls stay within configured concurrency
- No PII in unexpected outbound traffic

### Compliance artifact

Each soak run produces `external-call-audit.md`:

```markdown
# External Call Audit- Soak Run 2026-04-23

| Target | Count | Data | Expected |
|--------|-------|------|----------|
| developers.google.com:443 | 1 | IP ranges (GET) | Yes |
| maxo:11434 | 2,847 | LLM prompts | Yes |
| dnsbl.httpbl.org:53 | 342 | Client IPs | Yes |

Unexpected connections: NONE
```

---

## Run Artifacts

```
soak-results/
├── smoke-2026-04-23T10:00:00/
│   ├── k6-mac-summary.json
│   ├── k6-maxo-summary.json
│   ├── monitor.csv
│   ├── external-calls.csv
│   ├── gate-report.txt
│   └── sut-logs.txt
├── soak-2026-04-23T10:30:00/
│   └── ...
└── endurance-2026-04-24T00:00:00/
    └── ...
```

---

## CLI

```bash
# Run from Mac (orchestrator)
./scripts/soak/run-soak.sh smoke      # 30 min → gate check
./scripts/soak/run-soak.sh soak       # 8 hours → gate check  
./scripts/soak/run-soak.sh endurance   # 72 hours → gate check
./scripts/soak/run-soak.sh plateau     # Breaking point- ramp to failure
```

---

## File Map

```
scripts/soak/
├── run-soak.sh                  # Orchestrator: starts k6 on both machines, monitor, gate check
├── soak-nodes.yaml              # Machine topology config
├── k6-soak-bdf.js               # Main k6 script (all stages via --env STAGE=)
├── soak-collector.mjs            # Metrics scraper (polls /admin/metrics every 30s)
├── soak-gate-check.mjs           # Threshold evaluator
├── soak-external-audit.sh        # Outbound connection capture (ss/netstat)
└── soak-report.mjs               # Post-run HTML report generator
```