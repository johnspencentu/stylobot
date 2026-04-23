# Content Sequence Detection — Design Spec

**Date:** 2026-04-23  
**Status:** Approved  
**Scope:** New `ContentSequenceContributor` detector + `CentroidSequenceStore` + YAML trigger updates

---

## Problem

The first HTML document request from a new fingerprint incurs full pipeline latency because all
detectors run synchronously regardless of whether they have useful data. Detectors like
`SessionVector`, `Periodicity`, and `Similarity` contractually need N>1 requests to produce
meaningful signal — on request 1 they waste time and contribute noise. Meanwhile, follow-on
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
`TriggerConditions`. No orchestrator changes. No new persistence — sequence context is transient
(30-min TTL matching session boundary).

---

## Components

### 1. `ContentSequenceContributor`

**File:** `Orchestration/ContributingDetectors/ContentSequenceContributor.cs`  
**Priority:** 4  
**Inherits:** `ConfiguredContributorBase`

#### Request 1 — Document hit (new or unknown fingerprint)

Detection criteria for "this is a document request" — checked in this order, first match wins:
1. `Sec-Fetch-Mode: navigate` header present (most reliable, covers all modern browsers)
2. `Accept` contains `text/html` AND method is `GET` (fallback for older clients)
3. `transport.protocol_class == "document"` signal already in blackboard (opportunistic — 
   `TransportProtocolContributor` runs at P5 in the same wave, so this is only available
   if a prior wave already ran Transport; do not depend on it for request 1 classification)

Note: `ContentSequenceContributor` (P4) and `TransportProtocolContributor` (P5) run in the
same Wave 0 in parallel. ContentSequenceContributor must not depend on Transport's signals
for its document classification — direct header inspection is the authoritative check.

On detection:
1. Create `SequenceContext` for this signature, write to `SequenceContextStore`
2. Load Tier 1 chain (global fallback — top-5 `PageView→*` transitions from global Markov matrix)
3. Write signals:
   - `sequence.position = 0`
   - `sequence.on_track = true`
   - `sequence.chain_id = <uuid>`
   - `sequence.centroid_type = Unknown`
   - `sequence.content_path = <request path>`
4. Most expensive detectors see `sequence.on_track=true` + `sequence.position=0` → skip

#### Requests 2-N — Continuation

1. Look up `SequenceContext` by signature
2. If `similarity.top_cluster_id` signal is now set (written by `SimilarityContributor` on the
   previous request's pipeline run and now available in the blackboard), swap Tier 1 → Tier 2 chain
3. Classify this request into a `MarkovState` using the same path/header heuristics as
   `MarkovTracker.ClassifyRequest()` — extract this logic into a shared static helper so both
   components stay in sync: `RequestMarkovClassifier.Classify(HttpContext)`
4. Compare actual `MarkovState` against `ExpectedStates[position]` and timing against
   `TypicalGapsMs[position] ± GapToleranceMs[position]`
5. Write:
   - `sequence.position = N`
   - `sequence.on_track = true/false`
   - `sequence.divergence_score = 0.0-1.0`
   - `sequence.centroid_type = Human/Bot/Unknown` (refined once centroid known)
   - `sequence.signalr_expected = true` (when SignalR is the expected next state)
6. If diverged, additionally write:
   - `sequence.diverged = true`
   - `sequence.divergence_at_position = N`

#### Session boundary

When a request arrives with a gap > 30 minutes from the last `SequenceContext` timestamp, the
existing context is expired and a new one is created (same logic as `MarkovTracker` session
boundaries).

---

### 2. `SequenceContextStore`

**File:** `Services/SequenceContextStore.cs`  
**Type:** Singleton `ConcurrentDictionary<string, SequenceContext>` with background TTL sweep

```csharp
record SequenceContext {
    string ChainId;
    string Signature;
    string CentroidId;           // Empty until similarity resolves
    CentroidType CentroidType;   // Unknown → Human/Bot once resolved
    int Position;
    CentroidSequence ActiveChain; // Tier 1 initially, Tier 2 once centroid known
    DateTimeOffset LastRequest;
    bool HasDiverged;
    int DivergenceCount;
}
```

TTL sweep runs every 5 minutes, evicts entries where `LastRequest` is older than 30 minutes.
This is transient state — **not persisted to SQLite**. Loss on restart is acceptable (new
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
probabilities in `MarkovTracker`. Stored as a singleton `GlobalExpectedChain` — the top-5
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

**Pattern — skip when on-track and early in sequence:**

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

The `NotExists` fallback is critical — requests that don't go through the sequence (direct API
calls, non-browser clients) must not be blocked from detection just because `sequence.position`
was never written.

**SignalR trust boost:**

`TransportProtocolContributor` and `StreamAbuse` consume `sequence.signalr_expected`:

```yaml
# In StreamAbuse and TransportProtocol trigger guards
triggers:
  skip_when:
    - sequence.signalr_expected  # SignalR is an expected continuation — don't penalise
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

- `BlackboardOrchestrator` — zero changes
- Existing detector C# classes — only YAML `triggers` sections updated
- `MarkovTracker` — reads its data, doesn't modify it
- Session boundary logic — reuses the 30-min TTL already in MarkovTracker
- SQLite schema (except adding `centroid_sequences` table)
- All existing detection paths for non-document requests (direct API, bots that never hit a page)

---

## Correctness Constraints

- A request with no `sequence.position` signal must never be silently skipped. The `NotExists`
  fallback trigger on all deferred detectors guarantees this.
- `SequenceContextStore` entries must expire correctly. A 30-min TTL sweep is not optional —
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
