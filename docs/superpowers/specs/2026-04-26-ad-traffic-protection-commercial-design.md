# Ad Traffic Protection (Commercial) - Design Spec

**Date:** 2026-04-26
**Scope:** Commercial (`stylobot-commercial`)
**Depends on:** click-fraud-detection-foss-design.md (UTM signals must be present on blackboard)
**Status:** Ready for implementation

## Summary

Cross-session ad traffic analysis: persist hashed UTM tuples per signature, detect click farms
(N distinct fingerprints sharing a campaign hash in a short window), surface results in a new
dashboard "Ad Traffic" tab. The FOSS `ClickFraudContributor` does per-session detection;
this layer adds network-level detection across sessions.

---

## Part 1: UtmHashStore

### Purpose

Persist hashed UTM attribution tuples per signature so we can answer:
- "How many distinct signatures have arrived via this campaign in the last hour?"
- "Has this signature arrived from multiple different campaigns in the last 24h?" (cookie-stuffing)
- "Did this signature's UTM source change between sessions?" (attribution fraud)

### Schema (SQLite FOSS base / TimescaleDB commercial override)

```sql
CREATE TABLE utm_sessions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    signature_hash  TEXT NOT NULL,          -- PrimarySignature (already hashed)
    source_hash     TEXT,                   -- HMAC of utm_source value
    medium_hash     TEXT,                   -- HMAC of utm_medium value
    campaign_hash   TEXT NOT NULL,          -- HMAC of utm_campaign value (required for indexing)
    click_id_hash   TEXT,                   -- HMAC of gclid/fbclid/msclkid value
    source_platform TEXT NOT NULL,          -- "google" / "meta" / "microsoft" / "tiktok" / "paid_other"
    first_seen      INTEGER NOT NULL,       -- Unix timestamp ms
    last_seen       INTEGER NOT NULL,       -- Unix timestamp ms
    request_count   INTEGER NOT NULL DEFAULT 1,
    session_id      TEXT NOT NULL           -- links to sessions table
);

CREATE INDEX idx_utm_campaign ON utm_sessions (campaign_hash, first_seen);
CREATE INDEX idx_utm_signature ON utm_sessions (signature_hash, first_seen);
CREATE INDEX idx_utm_click_id ON utm_sessions (click_id_hash) WHERE click_id_hash IS NOT NULL;
```

### IUtmHashStore Interface

```csharp
public interface IUtmHashStore
{
    // Write: upsert a UTM session record
    Task RecordAsync(UtmSessionRecord record, CancellationToken ct = default);

    // Query: distinct signature count for a campaign hash in a time window
    Task<int> GetCampaignSignatureCountAsync(
        string campaignHash, TimeSpan window, CancellationToken ct = default);

    // Query: how many distinct campaigns has this signature arrived from?
    Task<int> GetSignatureCampaignCountAsync(
        string signatureHash, TimeSpan window, CancellationToken ct = default);

    // Query: full abuse score for a campaign (0.0-1.0)
    Task<double> GetCampaignAbuseScoreAsync(
        string campaignHash, TimeSpan window, CancellationToken ct = default);

    // Maintenance
    Task PruneAsync(TimeSpan maxAge, CancellationToken ct = default);
}

public sealed record UtmSessionRecord(
    string SignatureHash,
    string? SourceHash,
    string? MediumHash,
    string CampaignHash,
    string? ClickIdHash,
    string SourcePlatform,
    string SessionId,
    long TimestampMs
);
```

### Abuse Score Formula

`GetCampaignAbuseScoreAsync` returns a 0.0-1.0 score:
```
distinct_signatures = count of unique signature_hash for this campaign_hash in window
abuse_score = min(1.0, log10(distinct_signatures + 1) / log10(abuse_threshold + 1))
```

Default `abuse_threshold` = 50 (50 distinct signatures in 1h = score 1.0). Configurable via
`BotDetectionOptions.CommercialOptions.UtmStore.AbuseThreshold`.

### Background Pruning

`UtmHashStorePruner` background service runs every 6 hours, deletes records older than
`MaxRetentionDays` (default 30). This keeps the table bounded without TimescaleDB compression.

---

## Part 2: AdTrafficContributor

### Overview

Reads UTM signals from blackboard (written by FOSS `PiiQueryStringContributor`), queries
`IUtmHashStore` for cross-session campaign abuse score, and writes an enriched ad traffic signal.

**Priority:** 47 (immediately after FOSS `ClickFraudContributor` at 46)
**Wave:** Triggered
**Config file:** `adtraffic.detector.yaml`

### Trigger Conditions

```
AllOf(
  WhenSignalExists("utm.present"),
  WhenSignalValue("utm.present", true),
  WhenSignalExists("clickfraud.checked")   // FOSS contributor must have run first
)
```

If the FOSS `ClickFraudContributor` is not registered (FOSS-only deployment), this contributor
does not trigger and is safely skipped.

### What It Adds

1. **Campaign abuse score** - query `UtmHashStore` for this campaign hash in a 1-hour window.
   Append to `clickfraud.confidence` via additive weight.

2. **Cookie stuffing detection** - if this signature has arrived from >3 distinct campaigns in
   24h, emit `adtraffic.cookie_stuffing = true`. Cookie stuffing = inflating attribution by
   dropping many UTM params on the same user.

3. **Click ID reuse detection** - if this click_id_hash has been seen from >1 distinct signature,
   emit `adtraffic.click_id_reuse = true`. One click ID should map to one user.

4. **Attribution churn** - if source_platform changed between sessions for this signature,
   emit `adtraffic.attribution_churn = true`.

### Signals Emitted

| Signal key | Type | Description |
|---|---|---|
| `adtraffic.campaign_abuse_score` | double | Cross-session abuse score 0.0-1.0 |
| `adtraffic.cookie_stuffing` | bool | Signature seen from >N campaigns in 24h |
| `adtraffic.click_id_reuse` | bool | Same click ID from multiple signatures |
| `adtraffic.attribution_churn` | bool | Source platform changed between sessions |
| `adtraffic.checked` | bool | Contributor ran |

### Write Path

After emitting signals, record the current UTM session to `IUtmHashStore` asynchronously
(fire-and-forget with timeout). Never block the request on the write.

### YAML Config (`adtraffic.detector.yaml`)

```yaml
name: AdTrafficContributor
priority: 47
enabled: true
scope: PerRequest
taxonomy:
  category: AdFraud
  subcategory: AdTrafficAbuse
  iab_ivt_class: SIVT
defaults:
  weights:
    BotSignal: 1.2
  parameters:
    campaign_abuse_window_hours: 1
    campaign_abuse_weight: 0.30          # additive to clickfraud.confidence
    cookie_stuffing_campaign_threshold: 3
    cookie_stuffing_window_hours: 24
    click_id_reuse_signature_threshold: 2
    attribution_churn_window_hours: 48
    write_timeout_ms: 50                 # fire-and-forget write cap
```

---

## Part 3: Policy Preset System

### Overview

Named protection profiles that bundle per-bot-category action policies. Replaces manual
`BotTypeActionPolicies` config with a dashboard-selectable preset system.

### Data Model

```csharp
public sealed class ProtectionProfile
{
    public string Name { get; init; }           // "block-ai-scrapers"
    public string DisplayName { get; init; }    // "Block AI Scrapers"
    public string Description { get; init; }
    public bool IsBuiltIn { get; init; }        // built-ins cannot be deleted
    public Dictionary<string, string> BotTypeActionPolicies { get; init; }
    // bot type name → action policy name. e.g. "AiScraper" → "block"
    public bool AllowVerifiedSearchEngines { get; init; } = true;
    public bool AllowVerifiedSocialMedia { get; init; }
    public string? DefaultActionPolicy { get; init; }
    // fallback for bot types not explicitly listed
}
```

### Built-in Presets

| Name | Description | Bot types blocked | Exemptions |
|---|---|---|---|
| `permissive` | Log everything, block nothing | none | all |
| `block-malicious` | Block attack bots only | MaliciousBot, Haxxor, CveProbeBot | SearchEngine |
| `block-ai-scrapers` | Block AI training crawlers | AiScraper, GenericScraper | SearchEngine |
| `block-click-fraud` | Block ad fraud traffic | ClickFraud | SearchEngine |
| `ad-protection` | Block AI + click fraud | AiScraper, ClickFraud | SearchEngine |
| `block-all-bots` | Strict: block all detected bots | all | SearchEngine (if AllowVerified=true) |
| `search-engine-friendly` | Block malicious + AI, allow crawlers | MaliciousBot, AiScraper | SearchEngine, SocialMedia |
| `shadow-mode` | Detect everything, never block (learning) | none (logonly) | all |

### Persistence

`active_protection_profile` table (one row):

```sql
CREATE TABLE active_protection_profile (
    id           INTEGER PRIMARY KEY DEFAULT 1,
    profile_name TEXT NOT NULL DEFAULT 'permissive',
    custom_overrides TEXT,    -- JSON: partial overrides on top of named profile
    updated_at   INTEGER NOT NULL
);
```

`IProtectionProfileStore` interface with `GetActiveAsync`, `SetActiveAsync`, `GetAllAsync`.

### API Endpoints (commercial, under `/api/v1/protection-profiles`)

- `GET /` - list all profiles (built-in + custom)
- `GET /active` - get currently active profile
- `PUT /active` - set active profile by name
- `POST /` - create custom profile (auth required)
- `PUT /{name}` - update custom profile (auth required, cannot update built-ins)
- `DELETE /{name}` - delete custom profile (auth required, cannot delete built-ins)

### Dashboard Tab: "Protection"

New tab in `/_stylobot` dashboard (commercial UI):

**Layout:**
```
Protection
[ Preset selector dropdown: "Block Malicious Bots" ▾ ]   [ Apply ]

Bot Category          Action          Status
─────────────────────────────────────────────
Malicious / Attack    [Block ▾]        Active
AI Scrapers           [Block ▾]        Active
Click Fraud           [Throttle ▾]     Active
Generic Scrapers      [Log Only ▾]     Active
Search Engines        [Allow ▾]        Exempt (verified)
Social Media          [Allow ▾]        Exempt (verified)
Credential Stuffing   [Challenge ▾]    Active
CVE Probes            [Block ▾]        Active
```

Each row: bot category label, action dropdown (Allow / Log Only / Throttle / Challenge / Block),
status badge. Changes are staged (yellow "unsaved") until "Apply" is clicked, which calls
`PUT /api/v1/protection-profiles/active`.

Preset dropdown changes all rows to the preset values. Individual row edits create an automatic
"Custom (modified)" label in the dropdown.

FOSS mode: preset selector and table render read-only with a "Upgrade to commercial to edit" CTA.

---

## Files to Create/Modify

### New files (commercial repo)

1. `Mostlylucid.BotDetection.Commercial/AdTraffic/IUtmHashStore.cs`
2. `Mostlylucid.BotDetection.Commercial/AdTraffic/SqliteUtmHashStore.cs`
3. `Mostlylucid.BotDetection.Commercial/AdTraffic/PostgresUtmHashStore.cs` (TimescaleDB variant)
4. `Mostlylucid.BotDetection.Commercial/AdTraffic/UtmHashStorePruner.cs` (background service)
5. `Mostlylucid.BotDetection.Commercial/AdTraffic/AdTrafficContributor.cs`
6. `Mostlylucid.BotDetection.Commercial/AdTraffic/adtraffic.detector.yaml`
7. `Mostlylucid.BotDetection.Commercial/Protection/ProtectionProfile.cs`
8. `Mostlylucid.BotDetection.Commercial/Protection/IProtectionProfileStore.cs`
9. `Mostlylucid.BotDetection.Commercial/Protection/SqliteProtectionProfileStore.cs`
10. `Mostlylucid.BotDetection.Commercial/Protection/BuiltInProfiles.cs`
11. `Mostlylucid.BotDetection.Commercial/Endpoints/ProtectionProfileEndpoints.cs`
12. `Mostlylucid.BotDetection.UI.Commercial/Views/Protection/_ProtectionTab.cshtml`
13. `Mostlylucid.BotDetection.UI.Commercial/js/protection-tab.js` (HTMX + Alpine for dropdown staging)

### Modified files

14. `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`
    - Add `AdTrafficContributor` friendly name + category
15. Commercial DI registration in `ServiceCollectionExtensions` (commercial project)
    - Register `IUtmHashStore`, `AdTrafficContributor`, `IProtectionProfileStore`
    - Register `UtmHashStorePruner` as hosted service
16. Dashboard hub/nav to add "Protection" tab (commercial UI shell)

---

## What This Does NOT Do

- No raw UTM value storage (all hashed before write)
- No LLM escalation for ad traffic (out of scope for v1)
- No per-keyword or per-ad bidding integration (future: Google Ads API exclusion lists)
- No attribution reporting (not a marketing analytics tool)

---

## Testing

- Unit: `UtmHashStore` upsert + query + prune against SQLite in-memory
- Unit: `AdTrafficContributor` scoring with mocked `IUtmHashStore`
- Unit: `ProtectionProfile` preset composition + custom override merge
- Integration: full pipeline with FOSS `ClickFraudContributor` + commercial `AdTrafficContributor`
  in sequence, verifying `adtraffic.*` signals only appear when UTM signals present
- E2E: `PUT /api/v1/protection-profiles/active` → `BotTypeActionPolicies` effective for next request
