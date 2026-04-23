# Content Sequence Detection- Design Spec

**Date:** 2026-04-23  
**Status:** Approved  
**Scope:** New `ContentSequenceContributor` detector + `CentroidSequenceStore` + YAML trigger updates

---

## Problem

The first HTML document request from a new fingerprint incurs full pipeline latency because all
detectors run synchronously regardless of whether they have useful data. Detectors like
`SessionVector`, `Periodicity`, and `Similarity` contractually need N>1 requests to produce
meaningful signal- on request 1 they waste time and contribute noise. Meanwhile, follow-on
requests (static assets, API calls, SignalR) that are healthy continuations of a page load can
appear robotic in isolation, causing false positives.

---

## Solution

Spread the detection budget across the natural request sequence that follows a document hit.
The Markov chain data already collected tells us what a typical visitor does after hitting a
given page. Use that as an orchestration guide:

- Request 1 (document): run only sub-1ms detectors, load the expected chain, serve the page fast.
- Requests 2-N (continuation): layer in heavier detectors as they gain data to work with.
- Divergence from expected chain: escalate immediately to the full pipeline.

This is implemented entirely via the existing blackboard signal + `TriggerConditions` mechanism.
**Zero orchestrator changes.**

---

## Architecture

### Approach: New detector writing `sequence.*` signals (Approach C)

A new `ContentSequenceContributor` (Priority 4, runs after `FastPathReputation` at P3, before
everything else) writes signals that the rest of the pipeline reacts to via their existing YAML
`TriggerConditions`. No orchestrator changes. No new persistence- sequence context is transient
(30-min TTL matching session boundary).

---

## Components

### 1. `ContentSequenceContributor`

**File:** `Orchestration/ContributingDetectors/ContentSequenceContributor.cs`  
**Priority:** 4  
**Inherits:** `ConfiguredContributorBase`

#### Request 1- Document hit (new or unknown fingerprint)

Detection criteria for "this is a document request"- checked in this order, first match wins:
1. `Sec-Fetch-Mode: navigate` header present (most reliable, covers all modern browsers)
2. `Accept` contains `text/html` AND method is `GET` (fallback for older clients)
3. `transport.protocol_class == "document"` signal already in blackboard (opportunistic- 
   `TransportProtocolContributor` runs at P5 in the same wave, so this is only available
   if a prior wave already ran Transport; do not depend on it for request 1 classification)

Note: `ContentSequenceContributor` (P4) and `TransportProtocolContributor` (P5) run in the
same Wave 0 in parallel. ContentSequenceContributor must not depend on Transport's signals
for its document classification- direct header inspection is the authoritative check.

On detection:
1. Create `SequenceContext` for this signature, write to `SequenceContextStore`
2. Load Tier 1 chain (global fallback- top-5 `PageView→*` transitions from global Markov matrix)
3. Write signals:
   - `sequence.position = 0`
   - `sequence.on_track = true`
   - `sequence.chain_id = <uuid>`
   - `sequence.centroid_type = Unknown`
   - `sequence.content_path = <request path>`
4. Most expensive detectors see `sequence.on_track=true` + `sequence.position=0` → skip

#### Requests 2-N- Continuation

1. Look up `SequenceContext` by signature
2. If the signature now has a known cluster ID, swap Tier 1 → Tier 2 chain via `BotClusterService.FindCluster(signature)`
3. Classify this request into a `MarkovState` using `RequestMarkovClassifier.Classify(HttpContext)`- the shared static helper extracted from `SessionVectorContributor`
4. **Set-based divergence** (see Page→Resource Chain Model below): check whether the actual `MarkovState` falls in the *expected state set* for the current time window; compare timing against `TypicalGapsMs ± GapToleranceMs`
5. Write:
   - `sequence.position = N`
   - `sequence.on_track = true/false`
   - `sequence.divergence_score = 0.0-1.0`
   - `sequence.centroid_type = Human/Bot/Unknown` (refined once centroid known)
   - `sequence.signalr_expected = true` (when SignalR is the expected next state)
   - `sequence.prefetch_detected = true` (when `Purpose: prefetch` or `Sec-Fetch-Mode: no-cors` + idle priority signals prefetch/preload)
6. If diverged, additionally write:
   - `sequence.diverged = true`
   - `sequence.divergence_at_position = N`

#### Session boundary

When a request arrives with a gap > 30 minutes from the last `SequenceContext` timestamp, the
existing context is expired and a new one is created (same logic as `MarkovTracker` session
boundaries).

---

### Page→Resource Chain Model

A real browser doesn't make requests in strict sequential order. The divergence scorer must model
the actual page load structure to avoid false positives on healthy traffic.

#### The complete browser request sequence

```
T+0ms       Document (Sec-Fetch-Mode: navigate, Sec-Fetch-Dest: document)
T+50-200ms  Critical asset burst (HTTP/2 multiplexed, parallel):
              CSS   (Sec-Fetch-Mode: no-cors, Sec-Fetch-Dest: style)
              JS    (Sec-Fetch-Mode: no-cors, Sec-Fetch-Dest: script)
              Fonts (Sec-Fetch-Mode: no-cors, Sec-Fetch-Dest: font)
              [<link rel="preload"> assets arrive in this same window]
T+200-500ms Lazy assets (as viewport renders):
              Images (Sec-Fetch-Dest: image)
              Below-fold scripts
T+500ms+    Prefetched resources (<link rel="prefetch">):
              Purpose: prefetch header (Chromium/Firefox)
              Low priority, idle-time
T+any       API calls (Sec-Fetch-Mode: cors, Sec-Fetch-Site: same-origin)
T+any       SignalR negotiate + WebSocket upgrade
[missing]   Cache hits → NO request emitted (warm cache = expected assets absent)
```

#### Why position-based matching fails

If a browser has a warm cache, the critical asset burst (positions 1-N) won't appear because
the browser serves them from cache without making network requests. A position-indexed scorer
would see `ApiCall` at position 1 instead of `StaticAsset` and flag divergence- but this is
correct human behavior.

Similarly, prefetched resources can arrive in a burst at any point during page render, and
preloaded resources share the critical asset window. Sequential bots, by contrast, make requests
one at a time in strict order.

#### Set-based divergence model

Instead of `actual[N] == expected[N]`, divergence is measured across a **time window**:

```
IsOnTrack(window) = true  when:
  - The set of Markov states observed in the window is a subset of ExpectedStateSet
  - OR the window contains at least one StaticAsset (cache misses expected, cache hits OK)
  - AND timing is not machine-speed (<20ms between any two requests)
  - AND the total request count in the window is plausible (not 100× the expected count)
```

The `SequenceContext` accumulates:
- `ObservedStateSet`- the `HashSet<RequestState>` of all states seen in the current window
- `WindowStartTime`- when the current window opened
- `RequestCountInWindow`- total requests in window

Windows are defined by time breaks (200ms, 500ms, 2s, 5s, 30s) that correspond to the natural
phases of page load. When the time since the last observed request exceeds the phase threshold,
the window closes, divergence is assessed, and a new window opens.

#### Prefetch detection

A request is classified as `RequestCategory.Prefetch` (annotated on the `MarkovState`) when:
1. `Purpose: prefetch` header is present (Chromium ≥ 80, Firefox ≥ 110)
2. OR `Sec-Fetch-Mode: no-cors` + `Sec-Fetch-Dest: document` with idle-priority signals
3. OR path matches a `<link rel="prefetch">` hint observed in the document response headers
   (this requires the document response to have been inspected- not available in Wave 0)

Prefetch requests are **never** counted as divergence regardless of their Markov state, because
they represent browser speculation, not user intent.

`sequence.prefetch_detected` is written to the blackboard when any prefetch request is observed.
Downstream detectors (StreamAbuse, BehavioralWaveform) can use this as a trust signal.

#### Bot vs Human distinguishing factors

| Factor | Human | Bot |
|--------|-------|-----|
| Asset requests after document | Yes (critical burst) | Often absent |
| Request parallelism | HTTP/2 burst (5-20 parallel) | Sequential (1 at a time) |
| Inter-request timing | 50-2000ms with variance | <20ms OR machine-regular |
| Sec-Fetch-* headers | Present, consistent | Often missing or wrong |
| Prefetch requests | Follow page hints | Don't (no JS execution) |
| Cache behavior | Mix of hits/misses | All misses (no cookie jar) |
| Paths after document | Assets then API | API only, or ghost paths |
| SignalR/WebSocket | After assets + API | Never, or immediately |

**Ghost paths**- URLs only discoverable via JavaScript execution (e.g., paths loaded via
dynamic `import()`, paths in inline JS). A request to a ghost path proves JS ran, which is
strong human signal. Bot requests to ghost paths prove JS execution capability (headless) and
are suspicious when not preceded by the expected asset load.

#### Cache tolerance rule

`SequenceContext` tracks whether the expected critical-asset burst was observed. If no
`StaticAsset` requests appear in the 50-500ms window after the document but the sequence
otherwise looks healthy (API calls at reasonable timing, no machine-speed gaps), this is
treated as a **cache-warm hit**, not divergence. `sequence.cache_warm = true` is written to
signal that asset requests are expected to be absent.

---

### 2. `SequenceContextStore`

**File:** `Services/SequenceContextStore.cs`  
**Type:** Singleton `ConcurrentDictionary<string, SequenceContext>` with background TTL sweep

```csharp
record SequenceContext {
    string ChainId;
    string Signature;
    string CentroidId;                    // Empty until cluster resolves
    CentroidType CentroidType;            // Unknown → Human/Bot once resolved
    int Position;
    RequestState[] ExpectedChain;         // Active chain (Tier 1 or Tier 2)
    double[] TypicalGapsMs;
    double[] GapToleranceMs;
    HashSet<RequestState> ObservedStateSet; // States seen in the current time window
    DateTimeOffset WindowStartTime;       // When the current window opened
    int RequestCountInWindow;             // Requests in current window
    DateTimeOffset LastRequest;
    bool HasDiverged;
    int DivergenceCount;
    bool CacheWarm;                       // True when no static assets seen in critical window
}
```

TTL sweep runs every 5 minutes, evicts entries where `LastRequest` is older than 30 minutes.
This is transient state- **not persisted to SQLite**. Loss on restart is acceptable (new
fingerprint just gets a fresh context).

---

### 3. `CentroidSequenceStore`

**File:** `Services/CentroidSequenceStore.cs`  
**Type:** SQLite-backed singleton, loaded into memory on startup, rebuilt when cluster recompute runs

```csharp
record CentroidSequence {
    string CentroidId;
    CentroidType Type;            // Human, Bot, Unknown
    MarkovState[] ExpectedStates; // Ordered sequence of most likely next states
    double[] TypicalGapsMs;       // Expected inter-request timing per step
    double[] GapToleranceMs;      // Allowed deviation (±) per step
    PathPattern[] ExpectedPaths;  // Optional path prefix hints per step
    int SampleSize;               // Number of sessions this was derived from
}
```

**SQLite table:** `centroid_sequences`

```sql
CREATE TABLE centroid_sequences (
    centroid_id TEXT PRIMARY KEY,
    centroid_type INTEGER NOT NULL,  -- 0=Unknown, 1=Human, 2=Bot
    sequence_json TEXT NOT NULL,     -- JSON-serialised CentroidSequence
    sample_size INTEGER NOT NULL,
    computed_at TEXT NOT NULL
);
```

Rebuilt by `BotClusterService` when Leiden clustering completes. Each cluster's centroid vector
is used to derive the representative Markov chain (the most common sequence across all sessions
in that cluster).

---

### 4. Global Fallback Chain (Tier 1)

Computed once at startup (and refreshed hourly) from the aggregate `PageView→*` transition
probabilities in `MarkovTracker`. Stored as a singleton `GlobalExpectedChain`- the top-5
most probable next Markov states from a `PageView` entry point, with median timing gaps
derived from all sessions.

This is the chain used on Request 1 before the `SimilarityContributor` has had a chance to
identify the nearest centroid. It represents "what does a typical visitor do after loading a
page" regardless of bot/human classification.

---

### 5. `contentsequence.detector.yaml`

```yaml
name: ContentSequenceContributor
priority: 4
enabled: true

defaults:
  parameters:
    # Minimum position before SessionVector, Periodicity etc. trigger
    deferred_detector_min_position: 3
    # Divergence score threshold to write sequence.diverged=true
    divergence_threshold: 0.4
    # Timing tolerance multiplier (centroid gap * this = allowed range)
    timing_tolerance_multiplier: 3.0
    # Minimum sample size to trust a centroid chain
    min_centroid_sample_size: 20
    # Session gap threshold (minutes) for context expiry
    session_gap_minutes: 30
    # Max sequence positions to track before stopping (avoid infinite chains)
    max_tracked_positions: 20
```

---

## Detector YAML Changes

Heavy detectors that have no useful data on request 1 get a new trigger guard.

**Pattern- skip when on-track and early in sequence:**

```yaml
# heuristic.detector.yaml, intent.detector.yaml, sessionvector.detector.yaml etc.
triggers:
  requires:
    - anyOf:
      - signal: sequence.on_track
        condition: IsFalse
      - signal: sequence.position
        condition: GreaterThan(3)
      - signal: sequence.diverged
        condition: IsTrue
      # Fallback: if ContentSequenceContributor didn't run (non-document entry)
      - signal: sequence.position
        condition: NotExists
```

The `NotExists` fallback is critical- requests that don't go through the sequence (direct API
calls, non-browser clients) must not be blocked from detection just because `sequence.position`
was never written.

**SignalR trust boost:**

`TransportProtocolContributor` and `StreamAbuse` consume `sequence.signalr_expected`:

```yaml
# In StreamAbuse and TransportProtocol trigger guards
triggers:
  skip_when:
    - sequence.signalr_expected  # SignalR is an expected continuation- don't penalise
```

---

## Signal Keys

New constants in `DetectionContext.SignalKeys`:

```csharp
// Content Sequence signals
public const string SequencePosition = "sequence.position";
public const string SequenceOnTrack = "sequence.on_track";
public const string SequenceDiverged = "sequence.diverged";
public const string SequenceDivergenceScore = "sequence.divergence_score";
public const string SequenceDivergenceAtPosition = "sequence.divergence_at_position";
public const string SequenceChainId = "sequence.chain_id";
public const string SequenceCentroidType = "sequence.centroid_type";
public const string SequenceContentPath = "sequence.content_path";
public const string SequenceSignalRExpected = "sequence.signalr_expected";
public const string SequencePrefetchDetected = "sequence.prefetch_detected";
public const string SequenceCacheWarm = "sequence.cache_warm";
```

---

## New Components Summary

| Component | Type | Purpose |
|-----------|------|---------|
| `ContentSequenceContributor` | `IContributingDetector` (P4) | Writes all `sequence.*` signals, manages SequenceContext |
| `SequenceContextStore` | Singleton `ConcurrentDictionary` + TTL sweep | Transient per-fingerprint sequence state |
| `CentroidSequenceStore` | SQLite-backed singleton | Maps centroid IDs to expected chains; rebuilt on cluster recompute |
| `RequestMarkovClassifier` | Static helper class | Classifies `HttpContext` → `MarkovState`; extracted from `MarkovTracker` and shared with `ContentSequenceContributor` |
| `contentsequence.detector.yaml` | YAML manifest | Config: position thresholds, timing tolerances, divergence sensitivity |

---

## DI Registration

In `ServiceCollectionExtensions.cs`, in the Wave 0 (fast path) section:

```csharp
services.AddSingleton<SequenceContextStore>();
services.AddSingleton<CentroidSequenceStore>();
services.AddSingleton<IContributingDetector, ContentSequenceContributor>();
```

`CentroidSequenceStore` needs a `BotClusterService` notification hook to trigger rebuild after
clustering completes. `BotClusterService.NotifyClusteringComplete()` (new method, or extend
existing `NotifyBotDetected`) calls `CentroidSequenceStore.RebuildAsync()`.

---

## Dashboard

In `DetectionNarrativeBuilder`:
- `ContentSequenceContributor` → friendly name: `"Content Sequence"`
- Category: `"Behavioral"` (same as SessionVector, Periodicity)

No new dashboard tab needed. Sequence divergence events surface in the existing Session
timeline and Threats tab via the `sequence.diverged` signal.

---

## What Does Not Change

- `BlackboardOrchestrator`- zero changes
- Existing detector C# classes- only YAML `triggers` sections updated
- `MarkovTracker`- reads its data, doesn't modify it
- Session boundary logic- reuses the 30-min TTL already in MarkovTracker
- SQLite schema (except adding `centroid_sequences` table)
- All existing detection paths for non-document requests (direct API, bots that never hit a page)

---

## Correctness Constraints

- A request with no `sequence.position` signal must never be silently skipped. The `NotExists`
  fallback trigger on all deferred detectors guarantees this.
- `SequenceContextStore` entries must expire correctly. A 30-min TTL sweep is not optional-
  stale contexts accumulate position counts incorrectly.
- `CentroidSequenceStore` must not serve centroid chains with `SampleSize < min_centroid_sample_size`.
  Poorly sampled centroids produce noisy expected chains. Fall back to Tier 1 (global) if
  sample size is below threshold.
- `sequence.signalr_expected` must only be written when the centroid type is `Human` or the
  global chain includes SignalR. Never write it for Bot centroid chains.

---

## Out of Scope

- Retroactive response modification (a served page is served; sequence affects future requests only)
- Per-path Markov models (page-level granularity). Centroid chains are cluster-level. Per-path
  models are a future optimisation once this baseline is validated.
- Persistent sequence context across restarts (acceptable to lose; new fingerprint just gets
  a fresh context on next hit)