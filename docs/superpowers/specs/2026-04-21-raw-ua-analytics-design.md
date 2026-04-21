# Raw User-Agent Storage + Analytics

**Date**: 2026-04-21
**Status**: Planning

## Problem

We hash UAs for identity (PrimarySignature = HMAC(IP+UA)) but never store the raw string. This loses:
- Structural analysis (detect fake/impossible UAs)
- Version plausibility (Chrome/999 doesn't exist)
- Family tracking across rotation (Chrome→Firefox→Safari)
- Full-text search ("find all python-requests visitors")
- Version distribution analytics (how many Chrome 119 vs 120?)
- Version change timeline (when did this visitor's browser update?)

## Design

### Storage

Store PII-stripped raw UA alongside every detection event and session.

**PII stripping** (before storage):
- Run Microsoft.Recognizers.Text on the raw UA string
- Replace detected emails → `[email]`
- Replace detected phone numbers → `[phone]`
- Replace detected URLs with credentials → strip creds, keep domain
- Keep browser names, versions, OS, device identifiers (not PII)
- Store the stripped UA, never the original if PII was found

**Schema additions:**

```sql
-- detections table
ALTER TABLE detections ADD COLUMN user_agent_raw TEXT;

-- sessions table  
ALTER TABLE sessions ADD COLUMN user_agent_raw TEXT;

-- New: UA analytics table (aggregated, updated per detection)
CREATE TABLE user_agent_stats (
    ua_family TEXT NOT NULL,         -- Chrome, Firefox, Safari, python-requests, curl
    ua_version TEXT,                 -- 120.0.0.0, 2.31.0
    ua_os TEXT,                      -- Windows NT 10.0, macOS, Linux
    ua_device TEXT,                  -- Desktop, Mobile, Tablet
    is_bot INTEGER NOT NULL DEFAULT 0,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    hit_count INTEGER NOT NULL DEFAULT 1,
    unique_signatures INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (ua_family, ua_version, ua_os)
);
CREATE INDEX idx_ua_family ON user_agent_stats(ua_family, hit_count DESC);
CREATE INDEX idx_ua_last_seen ON user_agent_stats(last_seen DESC);
```

### Search API

**Full-text search** across raw UAs:

```
GET /api/ua/search?q=python-requests
GET /api/ua/search?q=Chrome/120
GET /api/ua/search?q=Googlebot
```

Returns matching signatures with hit counts, last seen, bot classification.

**Implementation**: SQLite `LIKE` for FOSS (simple, no dependencies). PostgreSQL `tsvector` full-text search for commercial.

### Analytics Dashboard Tab: "User Agents"

The existing User Agents tab gets enriched:

**Current**: Shows UA family breakdown with basic counts.

**New**:
1. **Version distribution chart** — bar/pie chart of Chrome versions in use. Shows version adoption curve.
2. **Version change timeline** — per-entity, when did the browser version change? Helps detect:
   - Legitimate browser updates (Chrome 119→120, one jump, months apart)
   - Rotation (Chrome→Firefox→Safari→Edge in one session = bot)
3. **UA search box** — full-text search with autocomplete
4. **Anomaly highlighting** — impossible UAs highlighted:
   - Chrome version higher than latest released
   - Chrome UA with Firefox rendering engine
   - Mobile device with desktop screen resolution
   - Bot UA claiming to be a browser
5. **Raw UA display** — hover/click to see full raw UA string (PII-stripped)

### Version Plausibility Checker

New utility that checks if a UA version is plausible:

```csharp
public static class UaVersionPlausibility
{
    // Updated periodically from browser release schedules
    // Or fetched from the CommonUserAgentService (already exists)
    public static bool IsPlausible(string family, string version)
    {
        // Chrome: current stable ± 3 major versions
        // Firefox: current stable ± 3 major versions  
        // Safari: current ± 2 major versions
        // Edge: follows Chrome versioning
        // python-requests: any version is plausible (it's honest)
    }
}
```

This feeds into the `UserAgentContributor` — implausible versions get a bot signal.

### UA Family Tracking Per Entity

The entity resolution system tracks UA families across sessions:

```
Entity A:
  Session 1: Chrome/120 (Windows)
  Session 2: Chrome/120 (Windows)     → stable, human
  Session 3: Chrome/121 (Windows)     → browser update, human
  
Entity B:  
  Session 1: Chrome/120 (Windows)
  Session 2: Firefox/120 (macOS)      → family change! rotation signal
  Session 3: Safari/17 (macOS)        → another family change! definitive rotation
```

The HeaderCorrelation detector already catches this per-request. The entity resolution system catches it across sessions.

### Signal Keys

```csharp
public const string UserAgentRaw = "ua.raw_stripped";       // PII-stripped raw UA
public const string UserAgentVersionPlausible = "ua.version_plausible";  // bool
public const string UserAgentFamilyChanged = "ua.family_changed";        // bool (entity-level)
```

## Implementation Phases

### Phase 1: PII-stripped raw UA storage
- Add `UaPiiStripper` utility using Microsoft.Recognizers.Text
- Store stripped UA in detections + sessions tables
- Write `ua.raw_stripped` signal to blackboard
- Display raw UA in signature detail and visitor cards

### Phase 2: UA analytics table + search
- Create `user_agent_stats` table
- Aggregate on every detection (upsert family/version/os)
- Add search API endpoint
- Add search box to User Agents dashboard tab

### Phase 3: Version plausibility + anomaly detection
- `UaVersionPlausibility` checker using CommonUserAgentService data
- Feed implausible versions as bot signal in UserAgentContributor
- Highlight anomalies in dashboard

### Phase 4: Version change timeline + entity tracking
- Track UA family changes per entity in EntityResolutionService
- Version change timeline visualization in signature detail
- Family rotation detection signal

## Non-Goals
- Raw UA storage for the purpose of user tracking/profiling
- Correlating UAs with personal identity
- Storing UAs with PII (always stripped first)
