# Progressive Fingerprint Identity System

**Date**: 2026-04-20
**Status**: Spec — needs planning session

## Problem

The reputation system uses IP subnet as a standalone identity signal, causing cross-contamination: curl traffic from `::1` poisons Chrome traffic from `::1`. The multi-factor signature system already computes rich identity (IP+UA, client hints, plugins, geo) but the reputation feedback loop ignores it and groups by raw IP.

More fundamentally: the identity should be progressive. The first request has IP+UA. By the third request, we know TLS fingerprint, HTTP/2 settings, TCP stack. By the fifth, client-side JS has run and we have canvas/WebGL hashes. The identity should GROW with each request, not be fixed at first contact.

## Design Principle

**Identity is a projection, not a snapshot.** Each session stores the HMAC hashes of every observable characteristic at that point. Over multiple sessions, the system retroactively discovers which characteristics are stable for this visitor — those become strong identity anchors. Volatile characteristics (timestamps, request IDs) get low weight automatically.

## Architecture

### Per-Session Header Hash Storage

On each request, HMAC-hash the VALUE of every incoming header. Store as a dictionary:

```
session.header_hashes = {
    "accept-language": HMAC("en-US,en;q=0.9"),
    "accept-encoding": HMAC("gzip, deflate, br"),
    "sec-ch-ua": HMAC("\"Chrome\";v=\"120\"..."),
    "sec-ch-ua-platform": HMAC("\"macOS\""),
    "sec-fetch-mode": HMAC("navigate"),
    ...
}
```

Do NOT store raw values (zero-PII). Only HMAC hashes. The hash is sufficient to determine stability across sessions.

### Stability Analysis (Retroactive)

For a given PrimarySignature, look across the last N sessions:

```
For each header hash key:
    values_across_sessions = [session1.hash, session2.hash, ...]
    stability = count_of_most_common / total_sessions
    
    if stability >= 0.9: strong_anchor (weight: high)
    if stability >= 0.7: moderate_anchor (weight: medium)  
    if stability < 0.5: volatile (weight: zero, exclude from identity)
```

Headers that are stable for THIS visitor become identity anchors. Different visitors may have different stable headers — the system discovers this automatically.

### Progressive Signature Resolution

```
Request 1 (instant):
  - IP + UA → PrimarySignature (2 factors)
  - Accept-Language, Accept-Encoding → header hashes
  - Sec-Fetch-*, Sec-CH-UA-* → client hint hashes

Request 2 (within same session):  
  - TLS fingerprint (JA3/JA4) → factor added
  - HTTP/2 SETTINGS → factor added
  - TCP window/TTL → factor added
  - Resolution: 5+ factors

Request 3+ (after JS execution):
  - Client-side fingerprint → factor added
  - Canvas/WebGL hash → factor added
  - Resolution: 7+ factors

Session 2+ (retroactive):
  - Stability analysis runs
  - Stable headers identified
  - Identity anchored to most stable factors
  - Volatile factors dropped from matching
```

### Reputation Feeds On Richest Signature

The reputation system should:
1. Never use IP-alone or UA-alone as reputation keys
2. Use PrimarySignature (IP+UA) as the minimum
3. Prefer the RICHEST available signature (most factors) for reputation lookup
4. When factors are added, migrate reputation from the simpler signature to the richer one
5. Stability-weighted: stable factors contribute more to identity matching

### What We DON'T Store

- Raw header values (zero-PII)
- Cookie values or names
- Request/response bodies
- Any value that could identify a person

### What We DO Store (per session)

- HMAC hashes of header values (keyed by header name)
- Fingerprint dimension values [118-125] (already in vector)
- Factor count at session time
- Stability scores (computed retroactively)

## Key Files

- `MultiFactorSignatureService.cs` — extend to compute per-header hashes
- `SessionVector.cs` — extend `SessionSnapshot` to carry header hashes
- `SignatureFeedbackHandler.cs` — use PrimarySignature instead of IP range (DONE)
- `SessionPersistence.cs` — add header_hashes_json column
- New: `FingerprintStabilityAnalyzer` — retroactive stability analysis across sessions

## Immediate Fixes Done (this session)

1. Killed IP-range reputation in `SignatureFeedbackHandler` — no more `IpRange` pattern type
2. Added `PrimarySignature` to learning event metadata
3. Added `IsLocalOrLoopback` guard in `NormalizeIpToRange`
4. Reputation now anchors on PrimarySignature, not IP subnet
