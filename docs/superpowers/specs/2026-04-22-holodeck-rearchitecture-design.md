# Holodeck Rearchitecture: Signal-Driven Engagement with Beacon Tracking

**Date:** 2026-04-22
**Status:** Approved
**Scope:** Fix holodeck bypass caused by early exit, add per-fingerprint coordination, add beacon-based fingerprint correlation

---

## Problem

The holodeck never fires. `FastPathReputation` (priority 3) sees known-bad bots as `ConfirmedBad`, triggers early exit, and blocks the request before `HoneypotLinkContributor` (priority 5) runs. The `HoneypotTriggered` signal is never written, so signal-based transitions to the `holodeck` action policy never match. Every bot gets a generic 403 instead of a fake response.

Additionally:
- No rate limiting per fingerprint — an attacker could overwhelm the holodeck
- Served fake data isn't stored — missed opportunity to correlate rotated fingerprints
- Simulation pack selection isn't signal-driven — no connection between CVE detector output and which pack serves the response

## Solution

Make holodeck a **response mode within blocking**, not an alternative to it. Three new components:

1. **HoneypotPathTagger** — pre-detection middleware that tags honeypot paths via hash set lookup, before any detector runs
2. **HolodeckCoordinator** — ephemeral keyed sequential processor, one engagement per fingerprint at a time, global capacity cap
3. **BeaconStore + BeaconContributor** — canary values in fake responses become fingerprint correlation beacons

---

## Architecture

### Request flow

```
Request arrives
  ↓
HoneypotPathTagger: HttpContext.Items["Holodeck.IsHoneypotPath"] = true/false
  ↓
BeaconContributor (priority 2): scan request for canary values → beacon.matched signal
  ↓
Detection pipeline runs (may early-exit on ConfirmedBad)
  ↓
ShouldBlock?
  ├── no → continue normally
  └── yes → HolodeckEligible?
              ├── path is honeypot AND isBot → try holodeck
              ├── attack.detected signal AND isBot → try holodeck (via transition)
              └── neither → normal 403
                    ↓
              HolodeckCoordinator.TryEngage(fingerprint)
              ├── slot available → serve fake response + store beacon → return
              └── slot busy or capacity full → normal 403
```

### Why pre-detection path tagging?

Honeypot path matching doesn't need the detection pipeline. `/wp-login.php` is a honeypot path from config — that's a hash set lookup, not a detection decision. Detection tells you *who's* hitting it (bot vs human). The holodeck decision combines both:

- Honeypot path + bot → holodeck
- Honeypot path + human → normal 404 (don't reveal the trap)
- Non-honeypot path + attack signal → holodeck (via existing transition system)
- Non-honeypot path + bot → normal 403

---

## HoneypotPathTagger

Lightweight middleware that runs before `BotDetectionMiddleware`. Checks request path against configured honeypot paths.

```csharp
public class HoneypotPathTagger
{
    // Configured from HolodeckOptions.HoneypotPaths
    // Built once at startup as a HashSet<string> for O(1) exact match
    // Plus glob patterns for prefix matching ("/wp-*")
    
    // Sets: HttpContext.Items["Holodeck.IsHoneypotPath"] = true
    // Sets: HttpContext.Items["Holodeck.MatchedPath"] = "/wp-login.php"
}
```

This is effectively free for non-honeypot paths. No allocations, no async, no DI resolution on the hot path.

---

## HolodeckCoordinator

Manages concurrent holodeck engagements using an ephemeral keyed sequential processor from `mostlylucid.ephemeral.atoms.keyedsequential` (already a dependency).

### Interface

```csharp
public class HolodeckCoordinator
{
    bool TryEngage(string fingerprint, out IDisposable slot);
    int ActiveEngagements { get; }
    int Capacity { get; }
}
```

### Constraints

- **One engagement per fingerprint** — while fingerprint "abc123" has an active holodeck response being generated, subsequent requests from "abc123" get normal 403
- **Global capacity** — max concurrent engagements across all fingerprints (configurable, default 10). When full, all new requests get 403 regardless of fingerprint
- **Timeout** — engagement auto-releases after `EngagementTimeoutMs` (default 5000ms) to prevent stuck slots
- **Metrics** — track: total engagements, rejections (fingerprint busy), rejections (capacity full), active count

### Configuration

```json
{
  "BotDetection": {
    "Holodeck": {
      "MaxConcurrentEngagements": 10,
      "MaxEngagementsPerFingerprint": 1,
      "EngagementTimeoutMs": 5000
    }
  }
}
```

### Pack selection

When an engagement starts, the coordinator selects the simulation pack. Priority order:

1. `cve.probe.pack_id` signal → use that exact pack (CVE detector already identifies it)
2. `HoneypotPath` value → match against pack honeypot path lists
3. `attack.categories` signal → match category to pack (e.g., "wordpress" attacks → WordPress pack)
4. No match → default pack (generic fake responses)

---

## Beacon Tracking

### Concept

Every holodeck response contains unique **canary values** — fake usernames, nonces, API keys, form field names — that are deterministic per fingerprint but globally unique. When any future request references a canary value, the beacon fires and links the new fingerprint to the old one.

### Canary generation

```
canary = HMAC-SHA256(fingerprint + path + beaconSecret)[0:8]  // 8-char hex
```

- **Deterministic per fingerprint + path** — same fingerprint hitting same path always gets same canary → consistent fake world
- **Globally unique** — different fingerprints get different canaries → match is a strong correlation signal
- `beaconSecret` is the same `SignatureHashKey` used for PrimarySignature (already configured)

### Where canaries go

Embedded in fake response content in positions a bot will naturally replay:

- Form hidden input values (`<input type="hidden" name="nonce" value="HK7x9QmR">`)
- URL parameters in links (`/wp-admin/post.php?nonce=HK7x9QmR`)
- JSON field values in API responses (`{"api_key": "sk-HK7x9QmR"}`)
- Fake database names, table names, credentials in `.env` responses
- Cookie set-cookie headers (`session_id=HK7x9QmR`)

NOT embedded in:
- Response headers (too obvious, easily stripped)
- Visible page text (bot might parse and filter)

### BeaconStore

SQLite table (FOSS), PostgreSQL (commercial):

```sql
CREATE TABLE beacons (
    canary TEXT PRIMARY KEY,
    fingerprint TEXT NOT NULL,
    path TEXT NOT NULL,
    pack_id TEXT,
    response_hash TEXT,          -- hash of what we served (for correlation)
    created_at DATETIME NOT NULL,
    expires_at DATETIME NOT NULL -- TTL, default 24h
);

CREATE INDEX ix_beacons_fingerprint ON beacons(fingerprint);
CREATE INDEX ix_beacons_expires ON beacons(expires_at);
```

Background cleanup job purges expired beacons (same pattern as session cleanup).

### BeaconContributor

New detector, priority 2 (before FastPathReputation). Scans incoming request for canary matches:

**Scan locations:**
- Query string parameter values
- Request path segments (split on `/`)
- Cookie values
- `Referer` header query parameters
- POST body form values (if Content-Type is `application/x-www-form-urlencoded`)

**Scan method:** Extract all candidate strings of length 8 (the canary length), batch-lookup against beacon store. This is a single SQLite query per request — fast.

**Signals written when matched:**
- `beacon.matched = true`
- `beacon.original_fingerprint = "abc123"`
- `beacon.canary = "HK7x9QmR"`
- `beacon.age_seconds = 3600`
- `beacon.path = "/wp-login.php"` (the original honeypot path)

**Entity resolution integration:** The existing `EntityResolutionService` already watches for signals that link fingerprints. `beacon.original_fingerprint` is a direct merge hint — if fingerprint "xyz789" carries a beacon from "abc123", entity resolution can propose a merge.

---

## Middleware Integration

### Change to BotDetectionMiddleware

In `HandleBlockedRequest`, add holodeck check before the normal 403:

```csharp
private async Task HandleBlockedRequest(HttpContext context, AggregatedEvidence evidence, ...)
{
    // Check 1: honeypot path (pre-tagged, always available)
    var isHoneypotPath = context.Items["Holodeck.IsHoneypotPath"] is true;
    
    // Check 2: attack signal (written by Haxxor, CveProbe — these run before early exit)
    var hasAttackSignal = evidence.Signals.ContainsKey(SignalKeys.AttackDetected)
                       || evidence.Signals.ContainsKey(SignalKeys.CveProbeDetected);
    
    if ((isHoneypotPath || hasAttackSignal) && holodeckCoordinator != null)
    {
        var fingerprint = GetFingerprint(context, evidence);
        if (holodeckCoordinator.TryEngage(fingerprint, out var slot))
        {
            using (slot)
            {
                var pack = SelectPack(context, evidence);
                await holodeckResponder.ServeAsync(context, evidence, pack);
                StoreBeacon(context, evidence, fingerprint);
            }
            return;
        }
    }
    
    // Normal 403
    await WriteBlockResponse(context, evidence);
}
```

### Transition system still works for non-honeypot attack paths

The existing `WhenSignal` transition mechanism continues to work for detectors that run before early exit. Haxxor (priority 10, Wave 0) writes `attack.detected` before FastPathReputation can early-exit. So this transition config works:

```json
{
  "WhenSignal": "attack.detected",
  "ActionPolicyName": "holodeck",
  "Description": "Attack payload on non-honeypot path - engage holodeck"
}
```

When the `holodeck` action policy fires via transitions, it also goes through `HolodeckCoordinator` for rate limiting.

---

## Configuration

Full `HolodeckOptions` with new fields:

```json
{
  "BotDetection": {
    "Holodeck": {
      "MockApiBaseUrl": "http://localhost:5080/api/mock",
      "Mode": "RealisticButUseless",
      "MaxConcurrentEngagements": 10,
      "MaxEngagementsPerFingerprint": 1,
      "EngagementTimeoutMs": 5000,
      "EnableBeaconTracking": true,
      "BeaconTtlHours": 24,
      "BeaconCanaryLength": 8,
      "HoneypotPaths": [
        "/wp-login.php",
        "/wp-admin",
        "/.env",
        "/config.php",
        "/.git/config",
        "/phpmyadmin",
        "/backup.sql",
        "/xmlrpc.php",
        "/wp-content/debug.log"
      ],
      "ReportToProjectHoneypot": false,
      "MaxStudyRequests": 50
    }
  }
}
```

---

## New Files

| File | Responsibility |
|------|---------------|
| `ApiHolodeck/Middleware/HoneypotPathTagger.cs` | Pre-detection path tagging |
| `ApiHolodeck/Services/HolodeckCoordinator.cs` | Keyed sequential engagement slots |
| `ApiHolodeck/Services/BeaconStore.cs` | SQLite canary → fingerprint storage |
| `ApiHolodeck/Services/BeaconCanaryGenerator.cs` | Deterministic canary generation per fingerprint+path |
| `ApiHolodeck/Contributors/BeaconContributor.cs` | Priority 2 detector, scans requests for canaries |
| `ApiHolodeck/Services/HolodeckResponder.cs` | Refactored response generation (extracted from HolodeckActionPolicy) |

## Modified Files

| File | Change |
|------|--------|
| `BotDetectionMiddleware.cs` | Holodeck check in `HandleBlockedRequest` |
| `HolodeckActionPolicy.cs` | Delegate to coordinator, add beacon generation |
| `HolodeckOptions.cs` | New config properties |
| `ApiHolodeck/Extensions/ServiceCollectionExtensions.cs` | Register new services |
| Demo `appsettings.json` | Signal-based transitions |

## What Does NOT Change

- Detection pipeline / orchestrator — untouched
- Early exit logic — still works, path tagging is pre-detection
- Simulation pack format — consumed as-is by coordinator
- Existing action policies (block, throttle, challenge) — untouched
- Entity resolution — already consumes signals, beacon signals feed in naturally
- Dashboard — holodeck engagements appear as blocked requests with `action=holodeck`
