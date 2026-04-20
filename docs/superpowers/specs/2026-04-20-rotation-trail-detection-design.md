# Rotation Trail Detection — Fingerprint Neighbor Merging

**Date**: 2026-04-20
**Status**: Spec

## Core Insight

Identity rotation creates a trail of near-miss fingerprints in vector space. Each rotation step is a small delta from the previous. By detecting sequential near-neighbors, we can:

1. Identify rotation behavior (small consistent deltas = systematic rotation)
2. Merge rotated identities back to the same entity
3. Turn the evasion technique into a detection signal

## How It Works

### Observation

The inter-session velocity (already computed) measures `||V_n - V_{n-1}||` — the L2 magnitude of the behavioral delta between sessions. This IS the rotation signal.

**Human patterns:**
- Long stretches of velocity ≈ 0 (same browser, same behavior)
- Occasional large jump (browser update, device change) — velocity spike
- **Variance of velocity: HIGH** (stable → spike → stable)

**Bot rotation patterns:**
- Consistent moderate velocity (0.1-0.3) every session
- Never zero (always rotating something), never huge (gradual changes)
- **Variance of velocity: LOW** (systematic, predictable rotation)

### Detection: Velocity Variance as Rotation Signal

```
velocity_history = [v1, v2, v3, v4, v5, ...]
mean_velocity = mean(velocity_history)
variance_velocity = var(velocity_history)

if mean_velocity > 0.1 AND variance_velocity < 0.05:
    → systematic rotation detected
    → rotation_period ≈ mean_session_gap
    → rotation_magnitude ≈ mean_velocity
```

### Merging: Cosine Neighbor Walk

When a NEW fingerprint appears (no history for this PrimarySignature):

1. Compute cosine similarity to ALL recent fingerprints from the last hour
2. If `similarity > 0.85` to a known entity AND timing matches rotation cadence:
   → Merge: same actor, new disguise
   → Inherit reputation from the matched entity
3. If `similarity < 0.5` to everything:
   → Genuinely new visitor

### What Dimensions Drive Rotation Detection

The 129-dim vector has different rotation characteristics:

**Typically rotated by bots (dims that change):**
- [118] TLS version — rotated between 1.2/1.3
- [119] HTTP protocol — rotated between h1/h2
- [120] Protocol client type — different HTTP libraries
- [126-128] Timing features — varies with load/proxy

**Typically stable even during rotation (dims that DON'T change):**
- [0-99] Markov transitions — behavior pattern is the goal, hard to fake
- [110] Timing regularity — bot rhythm is persistent
- [114] Request rate — throughput is consistent
- [116] Path diversity — target set doesn't change

**The key dimensions for merging are the STABLE ones** — if Markov transitions are similar but fingerprint dims changed, it's the same actor rotating their transport layer.

### Integration with Entity Resolution

The rotation trail feeds directly into the identity graph:

```
Entity A (PrimarySignature: abc123)
  ├── Session 1: vector V1
  ├── Session 2: vector V2, velocity 0.15 from V1
  ├── Session 3: vector V3, velocity 0.12 from V2
  └── ROTATION DETECTED: consistent velocity 0.13 ± 0.02

New visitor (PrimarySignature: def456)
  └── Session 1: vector V4, cosine_sim(V3, V4) = 0.91
      → MERGE with Entity A: same actor, new IP/UA
      → Inherit reputation: confirmed bot
```

### Periodicity × Rotation = Double Signal

If the rotation happens with a detectable PERIOD (every 5 min, every 100 requests):

```
rotation_cadence = FFT(velocity_history) → dominant frequency
```

Two entities with:
- Same rotation cadence (5 min)
- Same behavioral pattern (Markov similarity > 0.9)
- Sequential near-neighbor fingerprints

→ Almost certainly the same botnet operator

## Implementation

1. **Extend `SessionVectorContributor`** — compute velocity variance across last N sessions
2. **New signal:** `session.rotation_detected`, `session.rotation_cadence`, `session.velocity_variance`
3. **Extend `ClusterContributor`** — use cosine neighbor walking for entity merging
4. **New contributor: `RotationDetectionContributor`** — fires when velocity variance is suspiciously low
5. **Dashboard:** show rotation trail visualization (connected dots in vector space)

## Already Have

- Inter-session velocity (computed, displayed in dashboard "Drift" column)
- Cosine similarity (in SessionVectorizer)
- Session snapshots with full vectors
- Leiden clustering (can incorporate rotation edges)
