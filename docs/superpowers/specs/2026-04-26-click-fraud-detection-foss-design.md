# Click Fraud Detection (FOSS) - Design Spec

**Date:** 2026-04-26
**Scope:** FOSS (`Mostlylucid.BotDetection`)
**Status:** Ready for implementation

## Summary

Two changes: (1) fix `PiiQueryStringContributor` to emit hashed UTM/click-id signals before stripping
instead of silently discarding them; (2) add `ClickFraudContributor` that aggregates in-session signals
into a `ClickFraud` bot type score.

No new data collection beyond what the request already contains. Zero PII persisted.
Cross-session UTM correlation is commercial (`AdTrafficContributor` - separate spec).

---

## Part 1: PiiQueryStringContributor - UTM Signal Fix

### The Bug

`utm_source`, `utm_medium`, `utm_campaign`, `utm_term`, `utm_content` are in `SafeKeys` so they are
passed through the sanitizer untouched. `gclid`, `fbclid`, `msclkid`, `ttclid` are not in either set
so they fall through to the value-pattern check and may be silently dropped.

None of these parameters are signaled on the blackboard. The detector has no idea a request arrived
via a paid ad click. Downstream click fraud detection is blind to paid traffic context.

### Fix

Before (or alongside) PII detection, scan for UTM and click-ID parameters. Hash values with HMAC-SHA256
using the same key used for signature hashing (already available in `DetectionContext`). Emit signals.
Never persist raw values.

### New Signals Emitted (all Wave 0, Priority 8)

| Signal key | Type | Description |
|---|---|---|
| `utm.present` | bool | Any UTM or click-ID parameter found |
| `utm.source_hash` | string | HMAC-SHA256 of utm_source value (truncated to 16 hex chars) |
| `utm.medium_hash` | string | HMAC-SHA256 of utm_medium value |
| `utm.campaign_hash` | string | HMAC-SHA256 of utm_campaign value |
| `utm.has_gclid` | bool | gclid present (Google Ads click ID) |
| `utm.has_fbclid` | bool | fbclid present (Meta Ads click ID) |
| `utm.has_msclkid` | bool | msclkid present (Microsoft Ads) |
| `utm.has_ttclid` | bool | ttclid present (TikTok Ads) |
| `utm.click_id_hash` | string | HMAC-SHA256 of whichever click ID is present |
| `utm.source_platform` | string | Inferred platform: "google" / "meta" / "microsoft" / "tiktok" / "organic" |

### Signal Derivation

```
utm.source_platform logic:
  gclid present           → "google"
  fbclid present          → "meta"
  msclkid present         → "microsoft"
  ttclid present          → "tiktok"
  utm_source = "google"   → "google"
  utm_source = "facebook" or "fb" → "meta"
  utm_source present      → "paid_other"
  none                    → "organic"
```

### Referrer Mismatch Signal

Also emitted by this contributor (same Wave 0 pass, same Priority 8):

| Signal key | Type | Description |
|---|---|---|
| `utm.referrer_present` | bool | Referer header present and non-empty |
| `utm.referrer_mismatch` | bool | Click ID present but Referer absent OR Referer domain inconsistent with source_platform |

Mismatch logic:
- `gclid` present but no Referer → mismatch (bots follow ad URLs directly, real browsers follow from Google)
- `source_platform = "google"` but Referer is not `*.google.*` or `*.googleadservices.com` → mismatch
- `source_platform = "meta"` but Referer is not `*.facebook.com` or `*.instagram.com` → mismatch

### Implementation Notes

- `QueryStringSanitizer` gets a new `DetectAdTrafficParams(string queryString)` method returning
  `AdTrafficDetectionResult` (which params found, their hashed values).
- HMAC key: use `DetectionContext.SignatureHmacKey` (already passed to other contributors via
  `BlackboardState`). If not available, SHA256 of value without key (still one-way, less ideal).
- The existing `DetectPii` pass is unchanged. UTM detection is a separate pass run first.
- `PiiQueryStringContributor.ContributeAsync` runs the UTM pass regardless of whether PII is found.
  Returns a single `Info` contribution carrying all signals (or `None` if no query string).

---

## Part 2: ClickFraudContributor

### Overview

Aggregates in-session signals into a weighted confidence score for the `ClickFraud` bot type.
Reads exclusively from the blackboard (signals written by earlier contributors) plus the two new
UTM signals from Part 1. No network calls. No persistence. No new data collection.

**Bot type emitted:** `ClickFraud`
**Priority:** 46 (after SessionVector=43, IntentContributor=40, before Heuristic=50)
**Wave:** Triggered (not Wave 0)
**Config file:** `clickfraud.detector.yaml`

### Trigger Conditions

```
AnyOf(
  WhenSignalExists("utm.present"),                          // paid traffic context
  AllOf(
    WhenSignalExists("session.request_count"),
    WhenSignalExists("ip.is_datacenter")                   // datacenter + session
  )
)
```

Rationale: if there are no UTM signals AND no datacenter flag, click fraud is unlikely enough to skip.
This avoids running on every organic request.

### Scoring Model

All weights and thresholds come from YAML `defaults.parameters`. No magic numbers in code.

```
score = 0.0

// --- Paid traffic context (gating, not additive) ---
is_paid = utm.present == true
is_click_id = utm.has_gclid OR utm.has_fbclid OR utm.has_msclkid OR utm.has_ttclid

// --- Network-level signals (strong, GIVT-class) ---
if ip.is_datacenter AND is_paid:       score += datacenter_paid_weight         (default 0.50)
if ip.is_datacenter AND NOT is_paid:   score += datacenter_unpaid_weight        (default 0.15)
if ip.is_vpn AND is_paid:              score += vpn_paid_weight                 (default 0.25)
if ip.is_proxy AND is_paid:            score += proxy_paid_weight               (default 0.20)

// --- Referrer integrity (SIVT-class) ---
if utm.referrer_mismatch AND is_click_id: score += referrer_mismatch_weight     (default 0.40)
if utm.referrer_mismatch AND is_paid:     score += referrer_mismatch_paid_weight (default 0.25)

// --- Session behavior ---
if session.request_count == 1:         score += single_page_weight              (default 0.20)
if session.request_count <= 2
   AND session.duration_ms < 5000:     score += immediate_bounce_weight         (default 0.25)
if resource_waterfall.asset_count == 0
   AND transport.protocol_class == "document": score += no_assets_weight        (default 0.15)

// --- Client/UA signals ---
if ua.is_headless AND is_paid:         score += headless_paid_weight            (default 0.40)
if ua.is_headless AND NOT is_paid:     score += headless_unpaid_weight          (default 0.20)
if behavioral.interaction_score < 0.1: score += no_interaction_weight           (default 0.15)

// --- Cap at 1.0 ---
score = min(score, 1.0)
```

### Pattern Classification

Emitted as `clickfraud.pattern` string for dashboard display and LLM escalation context:

| Pattern | Condition |
|---|---|
| `datacenter_paid` | ip.is_datacenter AND is_paid AND score >= threshold |
| `referrer_spoof` | utm.referrer_mismatch AND is_click_id |
| `immediate_bounce` | session.request_count <= 1 AND session.duration_ms < 5000 |
| `engagement_void` | no_interaction AND no_assets AND session.request_count < 3 |
| `headless_paid` | ua.is_headless AND is_paid |
| `organic_datacenter` | ip.is_datacenter AND NOT is_paid (lower confidence) |

Multiple patterns can fire; emitted as comma-separated string.

### Signals Emitted

| Signal key | Type | Description |
|---|---|---|
| `clickfraud.confidence` | double | Weighted score 0.0-1.0 |
| `clickfraud.pattern` | string | Comma-separated pattern names |
| `clickfraud.is_paid_traffic` | bool | utm.present or click ID found |
| `clickfraud.checked` | bool | Contributor ran (for downstream triggers) |

### Bot Type

Add `ClickFraud = "ClickFraud"` to the bot type enum/class in `Models/BotType.cs`.
Narrative builder entries: friendly name "Click Fraud Bot", category "Ad Fraud".

### YAML Config (`clickfraud.detector.yaml`)

```yaml
name: ClickFraudContributor
priority: 46
enabled: true
scope: PerRequest
taxonomy:
  category: AdFraud
  subcategory: ClickFraud
  iab_ivt_class: SIVT
input:
  - utm.present
  - utm.has_gclid
  - utm.has_fbclid
  - utm.referrer_mismatch
  - ip.is_datacenter
  - ip.is_vpn
  - ip.is_proxy
  - session.request_count
  - ua.is_headless
  - behavioral.interaction_score
  - resource_waterfall.asset_count
  - transport.protocol_class
output:
  - clickfraud.confidence
  - clickfraud.pattern
  - clickfraud.is_paid_traffic
  - clickfraud.checked
triggers:
  - signal_exists: utm.present
  - all_of:
      - signal_exists: session.request_count
      - signal_exists: ip.is_datacenter
defaults:
  weights:
    BotSignal: 1.5
  confidence:
    min_to_report: 0.30
    bot_threshold: 0.55
  parameters:
    datacenter_paid_weight: 0.50
    datacenter_unpaid_weight: 0.15
    vpn_paid_weight: 0.25
    proxy_paid_weight: 0.20
    referrer_mismatch_weight: 0.40
    referrer_mismatch_paid_weight: 0.25
    single_page_weight: 0.20
    immediate_bounce_weight: 0.25
    no_assets_weight: 0.15
    headless_paid_weight: 0.40
    headless_unpaid_weight: 0.20
    no_interaction_weight: 0.15
```

---

## Files to Create/Modify (5-file pattern)

### New files

1. `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ClickFraudContributor.cs`
2. `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/clickfraud.detector.yaml`

### Modified files

3. `Mostlylucid.BotDetection/Privacy/QueryStringSanitizer.cs`
   - Add `DetectAdTrafficParams(string queryString, byte[]? hmacKey = null) → AdTrafficDetectionResult`
   - Add `AdTrafficDetectionResult` record (UtmPresent, SourcePlatform, SourceHash, MediumHash,
     CampaignHash, HasGclid, HasFbclid, HasMsclkid, HasTtclid, ClickIdHash)

4. `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/PiiQueryStringContributor.cs`
   - Call `QueryStringSanitizer.DetectAdTrafficParams` before PII detection
   - Emit all `utm.*` signals listed in Part 1

5. `Mostlylucid.BotDetection/Models/DetectionContext.cs`
   - Add `utm.*` and `clickfraud.*` signal key constants

6. `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`
   - Register `ClickFraudContributor` as singleton `IContributingDetector`

7. `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`
   - Add `ClickFraudContributor` to `DetectorFriendlyNames` ("Click Fraud")
   - Add to `DetectorCategories` ("Ad Fraud")

Note: 7 files not 5 because `PiiQueryStringContributor` and `QueryStringSanitizer` are both modified
as part of the bug fix. The contributor pattern itself is still 5 files.

---

## Part 3: Downstream Signal Wiring

Click fraud signals must be consumed by all applicable downstream detectors. Priority is adjusted
to 38 (not 46) so click fraud runs BEFORE IntentContributor (40) and ReputationBiasContributor (45).

### Priority Ordering

```
PiiQueryStringContributor   Priority  8  → emits utm.*
...
ClickFraudContributor       Priority 38  → emits clickfraud.*   ← MOVED from 46
IntentContributor           Priority 40  → reads clickfraud.*
SessionVectorContributor    Priority 43  → notes clickfraud.confidence in session metadata
ReputationBiasContributor   Priority 45  → reads clickfraud.confidence for paid-traffic bias
HeuristicContributor        Priority 50  → reads clickfraud.confidence as named feature
```

### IntentContributor (Priority 40)

**Change:** Add `clickfraud.confidence` and `clickfraud.is_paid_traffic` to the intent feature vector.
Modify trigger conditions to also fire when `clickfraud.checked = true`.

Intent threat scoring update:
- If `clickfraud.confidence >= 0.55` AND `clickfraud.is_paid_traffic`: add `ad_fraud` threat dimension
  to intent vector with weight from `intent.detector.yaml` parameter `ad_fraud_weight` (default 0.8).
- If `clickfraud.pattern` contains `referrer_spoof`: intent threat score gets `+spoofing_bias`
  (default 0.15) - referrer spoofing overlaps with general deception intent.

New signals IntentContributor emits when click fraud is present:
- `intent.ad_fraud_threat` (double) - click-fraud component of threat score

**Files to modify:**
- `IntentContributor.cs`: add `clickfraud.*` reads + threat dimension
- `intent.detector.yaml`: add `ad_fraud_weight`, `spoofing_bias` parameters; add `clickfraud.*` to
  `input:` section; update `triggers:` to include `WhenSignalValue("clickfraud.checked", true)`

### ReputationBiasContributor (Priority 45)

**Change:** When `clickfraud.is_paid_traffic = true` AND `clickfraud.confidence >= 0.4`, apply a
`paid_traffic_bias_multiplier` (default 1.3) to the combined pattern reputation score. Paid traffic
bots that have been seen before should hit the fast-path block sooner.

This is a YAML-configurable parameter only - no logic change beyond reading one existing signal.

**Files to modify:**
- `ReputationBiasContributor.cs`: read `clickfraud.is_paid_traffic` + `clickfraud.confidence`;
  apply multiplier when both conditions met
- `reputation.detector.yaml`: add `paid_traffic_bias_multiplier` parameter (default 1.3),
  add `clickfraud.is_paid_traffic` and `clickfraud.confidence` to `input:` section

### HeuristicContributor (Priority 50)

**Change:** The `HeuristicDetector` builds a feature vector. Add `clickfraud.confidence` as a named
feature `click_fraud_score` with weight from `heuristic.detector.yaml`.

This is additive: if heuristic already scores a request as 60% bot and click fraud adds 0.5
confidence, the combined heuristic output rises. The feature weight prevents double-counting by
being set low (default 0.5) since `ClickFraudContributor` already emits its own contribution.

**Files to modify:**
- `HeuristicDetector.cs` (or wherever features are built): read `clickfraud.confidence` signal,
  add as `click_fraud_score` feature
- `heuristic.detector.yaml`: add `click_fraud_feature_weight: 0.5`, add `clickfraud.confidence`
  to `input:` section

### SignatureCoordinator / BotClusterService

No code changes required. `ClickFraud` as a registered bot type automatically flows through:
- `SignatureCoordinator` assigns bot type to signature on block/record
- `BotClusterService` clusters signatures by bot type including `ClickFraud`
- Reputation decay uses existing confirmed-bad tau (12h) for `ClickFraud` type

### FastPathReputationContributor

No code changes required. Once a signature has a `ClickFraud` reputation record, the fast path
blocks it on the next request before any detectors run. This is the existing reputation system
working as designed.

### Summary of Additional File Modifications

Beyond the 7 files listed in the original 5-file checklist, add:

8. `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/IntentContributor.cs`
   - Read `clickfraud.confidence`, `clickfraud.is_paid_traffic`, `clickfraud.pattern`
   - Add `ad_fraud` threat dimension; emit `intent.ad_fraud_threat`

9. `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/intent.detector.yaml`
   - Add `clickfraud.*` to `input:`, add trigger, add `ad_fraud_weight` + `spoofing_bias` params

10. `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ReputationBiasContributor.cs`
    - Add paid-traffic bias multiplier when click fraud signals present

11. `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/reputation.detector.yaml`
    - Add `paid_traffic_bias_multiplier` parameter

12. `Mostlylucid.BotDetection/Detectors/HeuristicDetector.cs` (or feature extraction class)
    - Add `click_fraud_score` feature from `clickfraud.confidence` signal

13. `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/heuristic.detector.yaml`
    - Add `click_fraud_feature_weight` parameter

---

## What This Does NOT Do

- No cross-session UTM correlation (commercial: `AdTrafficContributor`)
- No click farm network detection (requires `UtmHashStore`, commercial)
- No dashboard widget (commercial)
- No UTM hash persistence of any kind

---

## Testing

- Unit tests for `QueryStringSanitizer.DetectAdTrafficParams`:
  - gclid-only URL, fbclid-only, utm-only, mixed, no params
  - Referrer mismatch logic: gclid + no Referer, gclid + google Referer, gclid + wrong Referer
- Unit tests for `ClickFraudContributor`:
  - datacenter + gclid = high score
  - organic + residential = near-zero score
  - referrer spoof pattern fires correctly
  - headless + paid = high score
  - no UTM signals + no datacenter = contributor skips (trigger conditions not met)