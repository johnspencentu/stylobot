# Click Fraud Detection (FOSS) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect click fraud / invalid ad traffic by extracting UTM/click-ID signals from query strings, aggregating in-session behavioral signals into a `ClickFraudContributor`, and wiring those signals into `IntentContributor`, `ReputationBiasContributor`, and `HeuristicFeatureExtractor`.

**Architecture:** `PiiQueryStringContributor` (Priority 8) emits hashed UTM/referrer signals before PII stripping. `ClickFraudContributor` (Priority 38) reads those signals plus existing session/IP/behavioral signals and emits `clickfraud.*`. `IntentContributor` (Priority 40) and `ReputationBiasContributor` (Priority 45) read `clickfraud.*` for threat scoring and reputation amplification respectively. `HeuristicFeatureExtractor` picks up `clickfraud.confidence` as a structured signal value.

**Tech Stack:** C# / .NET 10, xUnit, `HMACSHA256` (System.Security.Cryptography), `BlackboardState`, `ConfiguredContributorBase`, YAML manifests embedded as resources.

---

## File Map

| File | Action | Responsibility |
|---|---|---|
| `Mostlylucid.BotDetection/Models/BotDetectionResult.cs` | Modify | Add `ClickFraud` to `BotType` enum |
| `Mostlylucid.BotDetection/Models/DetectionContext.cs` | Modify | Add `utm.*` and `clickfraud.*` `SignalKeys` constants |
| `Mostlylucid.BotDetection/Privacy/QueryStringSanitizer.cs` | Modify | Add `DetectAdTrafficParams()` + `AdTrafficDetectionResult` record |
| `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/PiiQueryStringContributor.cs` | Modify | Inject `PiiHasher`, call `DetectAdTrafficParams`, emit `utm.*` signals |
| `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ClickFraudContributor.cs` | **Create** | New contributor - Priority 38, reads signals, emits `clickfraud.*` |
| `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/clickfraud.detector.yaml` | **Create** | YAML manifest with all weights/thresholds |
| `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` | Modify | Register `ClickFraudContributor` |
| `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs` | Modify | Add friendly name + category |
| `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/IntentContributor.cs` | Modify | Add `clickfraud.*` features + ad_fraud case in heuristic fallback |
| `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/intent.detector.yaml` | Modify | Add `clickfraud.*` to optional_signals + triggers |
| `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ReputationBiasContributor.cs` | Modify | Apply paid-traffic bias multiplier |
| `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/reputation.detector.yaml` | Modify | Add `paid_traffic_bias_multiplier` parameter |
| `Mostlylucid.BotDetection/Detectors/HeuristicFeatureExtractor.cs` | Modify | Add `clickfraud.*` to `ExtractStructuredSignalValues` |
| `Mostlylucid.BotDetection/Detectors/HeuristicDetector.cs` | Modify | Add `cf:click_fraud_score` to `DefaultWeights` |
| `Mostlylucid.BotDetection.Test/Privacy/QueryStringSanitizerAdTrafficTests.cs` | **Create** | Unit tests for `DetectAdTrafficParams` |
| `Mostlylucid.BotDetection.Test/Orchestration/ClickFraudContributorTests.cs` | **Create** | Unit tests for `ClickFraudContributor` scoring |

---

### Task 1: Add `ClickFraud` BotType + Signal Key Constants

**Files:**
- Modify: `Mostlylucid.BotDetection/Models/BotDetectionResult.cs`
- Modify: `Mostlylucid.BotDetection/Models/DetectionContext.cs`

- [ ] **Step 1: Add `ClickFraud` to BotType enum**

  In `BotDetectionResult.cs`, the `BotType` enum currently ends at `ExploitScanner`. Add:

  ```csharp
  public enum BotType
  {
      Unknown,
      SearchEngine,
      SocialMediaBot,
      MonitoringBot,
      Scraper,
      MaliciousBot,
      GoodBot,
      VerifiedBot,
      AiBot,
      Tool,
      ExploitScanner,
      ClickFraud          // <-- add this
  }
  ```

- [ ] **Step 2: Add utm.* constants to SignalKeys in DetectionContext.cs**

  Find the `SignalKeys` class in `DetectionContext.cs`. Add a new section after the existing `AiScraper` section:

  ```csharp
  // ============================================================
  // UTM / Ad Traffic signals - set by PiiQueryStringContributor
  // ============================================================

  /// <summary>True if any UTM parameter or click ID is present in the query string.</summary>
  public const string UtmPresent = "utm.present";

  /// <summary>HMAC-SHA256 hash of utm_source value (truncated, URL-safe base64).</summary>
  public const string UtmSourceHash = "utm.source_hash";

  /// <summary>HMAC-SHA256 hash of utm_medium value.</summary>
  public const string UtmMediumHash = "utm.medium_hash";

  /// <summary>HMAC-SHA256 hash of utm_campaign value.</summary>
  public const string UtmCampaignHash = "utm.campaign_hash";

  /// <summary>True if gclid (Google Ads click ID) is present.</summary>
  public const string UtmHasGclid = "utm.has_gclid";

  /// <summary>True if fbclid (Meta Ads click ID) is present.</summary>
  public const string UtmHasFbclid = "utm.has_fbclid";

  /// <summary>True if msclkid (Microsoft Ads click ID) is present.</summary>
  public const string UtmHasMsclkid = "utm.has_msclkid";

  /// <summary>True if ttclid (TikTok Ads click ID) is present.</summary>
  public const string UtmHasTtclid = "utm.has_ttclid";

  /// <summary>HMAC-SHA256 hash of whichever click ID is present.</summary>
  public const string UtmClickIdHash = "utm.click_id_hash";

  /// <summary>Inferred ad platform: "google", "meta", "microsoft", "tiktok", "paid_other", "organic".</summary>
  public const string UtmSourcePlatform = "utm.source_platform";

  /// <summary>True if Referer header is present and non-empty.</summary>
  public const string UtmReferrerPresent = "utm.referrer_present";

  /// <summary>True when click ID present but Referer is absent or domain doesn't match source platform.</summary>
  public const string UtmReferrerMismatch = "utm.referrer_mismatch";

  // ============================================================
  // Click Fraud signals - set by ClickFraudContributor
  // ============================================================

  /// <summary>Weighted confidence score 0.0-1.0 that this is click fraud traffic.</summary>
  public const string ClickFraudConfidence = "clickfraud.confidence";

  /// <summary>Comma-separated pattern names: datacenter_paid, referrer_spoof, immediate_bounce, engagement_void, headless_paid.</summary>
  public const string ClickFraudPattern = "clickfraud.pattern";

  /// <summary>True if the request arrived via a paid ad (UTM or click ID present).</summary>
  public const string ClickFraudIsPaidTraffic = "clickfraud.is_paid_traffic";

  /// <summary>True once ClickFraudContributor has run (gate for downstream triggers).</summary>
  public const string ClickFraudChecked = "clickfraud.checked";
  ```

- [ ] **Step 3: Build and verify no compile errors**

  ```bash
  dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
  ```

  Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

  ```bash
  git add Mostlylucid.BotDetection/Models/BotDetectionResult.cs \
          Mostlylucid.BotDetection/Models/DetectionContext.cs
  git commit -m "feat(click-fraud): add ClickFraud BotType + utm/clickfraud signal key constants"
  ```

---

### Task 2: QueryStringSanitizer - UTM/Click-ID Detection

**Files:**
- Modify: `Mostlylucid.BotDetection/Privacy/QueryStringSanitizer.cs`
- Create: `Mostlylucid.BotDetection.Test/Privacy/QueryStringSanitizerAdTrafficTests.cs`

- [ ] **Step 1: Write failing tests first**

  Create `Mostlylucid.BotDetection.Test/Privacy/QueryStringSanitizerAdTrafficTests.cs`:

  ```csharp
  using System.Security.Cryptography;
  using System.Text;
  using Mostlylucid.BotDetection.Privacy;
  using Xunit;

  namespace Mostlylucid.BotDetection.Test.Privacy;

  public class QueryStringSanitizerAdTrafficTests
  {
      private static readonly byte[] TestKey = RandomNumberGenerator.GetBytes(32);

      [Fact]
      public void DetectAdTrafficParams_NoQueryString_ReturnsEmpty()
      {
          var result = QueryStringSanitizer.DetectAdTrafficParams(null, null, TestKey);
          Assert.False(result.UtmPresent);
          Assert.Equal("organic", result.SourcePlatform);
          Assert.False(result.HasGclid);
      }

      [Fact]
      public void DetectAdTrafficParams_Gclid_DetectsGooglePlatform()
      {
          var result = QueryStringSanitizer.DetectAdTrafficParams(
              "?gclid=Cj0KCQiAqsitBhDlARIsAGMR1Rjabc123", null, TestKey);
          Assert.True(result.UtmPresent);
          Assert.True(result.HasGclid);
          Assert.Equal("google", result.SourcePlatform);
          Assert.NotNull(result.ClickIdHash);
          Assert.DoesNotContain("Cj0KCQiAqsitBhDlARIsAGMR1Rjabc123", result.ClickIdHash!);
      }

      [Fact]
      public void DetectAdTrafficParams_Fbclid_DetectsMetaPlatform()
      {
          var result = QueryStringSanitizer.DetectAdTrafficParams(
              "?fbclid=AbC123xyz", null, TestKey);
          Assert.True(result.UtmPresent);
          Assert.True(result.HasFbclid);
          Assert.Equal("meta", result.SourcePlatform);
      }

      [Fact]
      public void DetectAdTrafficParams_UtmSource_HashesValue()
      {
          var result = QueryStringSanitizer.DetectAdTrafficParams(
              "?utm_source=google&utm_campaign=spring_sale&utm_medium=cpc", null, TestKey);
          Assert.True(result.UtmPresent);
          Assert.NotNull(result.SourceHash);
          Assert.NotNull(result.CampaignHash);
          Assert.NotNull(result.MediumHash);
          // Different values produce different hashes
          Assert.NotEqual(result.SourceHash, result.CampaignHash);
          // Raw value not present in hash output
          Assert.DoesNotContain("google", result.SourceHash!);
      }

      [Fact]
      public void DetectAdTrafficParams_HashesAreDeterministic()
      {
          const string qs = "?utm_campaign=test_campaign";
          var r1 = QueryStringSanitizer.DetectAdTrafficParams(qs, null, TestKey);
          var r2 = QueryStringSanitizer.DetectAdTrafficParams(qs, null, TestKey);
          Assert.Equal(r1.CampaignHash, r2.CampaignHash);
      }

      [Fact]
      public void DetectAdTrafficParams_ReferrerMismatch_GclidNoReferer()
      {
          var result = QueryStringSanitizer.DetectAdTrafficParams(
              "?gclid=abc123", referer: null, TestKey);
          Assert.True(result.ReferrerMismatch);
          Assert.False(result.ReferrerPresent);
      }

      [Fact]
      public void DetectAdTrafficParams_ReferrerMismatch_GclidWithGoogleReferer()
      {
          var result = QueryStringSanitizer.DetectAdTrafficParams(
              "?gclid=abc123",
              referer: "https://www.google.com/aclk?sa=l",
              TestKey);
          Assert.False(result.ReferrerMismatch);
          Assert.True(result.ReferrerPresent);
      }

      [Fact]
      public void DetectAdTrafficParams_ReferrerMismatch_GclidWithWrongReferer()
      {
          var result = QueryStringSanitizer.DetectAdTrafficParams(
              "?gclid=abc123",
              referer: "https://www.someblogsite.com",
              TestKey);
          Assert.True(result.ReferrerMismatch);
      }

      [Fact]
      public void DetectAdTrafficParams_NoParams_ReturnsOrganic()
      {
          var result = QueryStringSanitizer.DetectAdTrafficParams(
              "?page=2&sort=desc", null, TestKey);
          Assert.False(result.UtmPresent);
          Assert.Equal("organic", result.SourcePlatform);
      }

      [Fact]
      public void DetectAdTrafficParams_NullKey_StillHashesWithSha256()
      {
          var result = QueryStringSanitizer.DetectAdTrafficParams(
              "?utm_source=google", null, null);
          Assert.True(result.UtmPresent);
          Assert.NotNull(result.SourceHash);
      }
  }
  ```

- [ ] **Step 2: Run tests to verify they fail**

  ```bash
  dotnet test Mostlylucid.BotDetection.Test/ \
    --filter "FullyQualifiedName~QueryStringSanitizerAdTrafficTests" \
    --no-build 2>&1 | tail -20
  ```

  Expected: Build error - `DetectAdTrafficParams` does not exist.

- [ ] **Step 3: Add `AdTrafficDetectionResult` record and `DetectAdTrafficParams` to QueryStringSanitizer.cs**

  Add the record at the bottom of the file (after `PiiDetectionResult`):

  ```csharp
  /// <summary>
  ///     Result of ad traffic parameter detection in a query string.
  ///     All values are HMAC-hashed - no raw PII stored.
  /// </summary>
  public sealed record AdTrafficDetectionResult
  {
      public static readonly AdTrafficDetectionResult Empty = new();

      public bool UtmPresent { get; init; }
      public string SourcePlatform { get; init; } = "organic";
      public string? SourceHash { get; init; }
      public string? MediumHash { get; init; }
      public string? CampaignHash { get; init; }
      public bool HasGclid { get; init; }
      public bool HasFbclid { get; init; }
      public bool HasMsclkid { get; init; }
      public bool HasTtclid { get; init; }
      public string? ClickIdHash { get; init; }
      public bool ReferrerPresent { get; init; }
      public bool ReferrerMismatch { get; init; }
  }
  ```

  Add the `DetectAdTrafficParams` static method to the `QueryStringSanitizer` class:

  ```csharp
  private static readonly HashSet<string> UtmKeys = new(StringComparer.OrdinalIgnoreCase)
  {
      "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content"
  };

  private static readonly HashSet<string> ClickIdKeys = new(StringComparer.OrdinalIgnoreCase)
  {
      "gclid", "fbclid", "msclkid", "ttclid"
  };

  /// <summary>
  ///     Detects UTM parameters and click IDs in a query string and returns hashed signals.
  ///     Raw values are never stored - only HMAC-SHA256 hashes (or SHA256 when key is null).
  ///     Also checks for referrer mismatch (click ID present but referer absent or wrong domain).
  /// </summary>
  public static AdTrafficDetectionResult DetectAdTrafficParams(
      string? queryString,
      string? referer,
      byte[]? hmacKey = null)
  {
      if (string.IsNullOrEmpty(queryString))
          return AdTrafficDetectionResult.Empty;

      var qs = queryString.StartsWith('?') ? queryString[1..] : queryString;
      if (string.IsNullOrEmpty(qs))
          return AdTrafficDetectionResult.Empty;

      string? utmSource = null, utmMedium = null, utmCampaign = null;
      string? clickIdValue = null, clickIdKey = null;
      bool hasGclid = false, hasFbclid = false, hasMsclkid = false, hasTtclid = false;

      foreach (var part in qs.Split('&'))
      {
          var eq = part.IndexOf('=');
          if (eq < 0) continue;

          var key = part[..eq];
          var value = Uri.UnescapeDataString(part[(eq + 1)..]);

          if (string.Equals(key, "utm_source", StringComparison.OrdinalIgnoreCase))
              utmSource = value;
          else if (string.Equals(key, "utm_medium", StringComparison.OrdinalIgnoreCase))
              utmMedium = value;
          else if (string.Equals(key, "utm_campaign", StringComparison.OrdinalIgnoreCase))
              utmCampaign = value;
          else if (string.Equals(key, "gclid", StringComparison.OrdinalIgnoreCase))
          { hasGclid = true; clickIdValue = value; clickIdKey = "gclid"; }
          else if (string.Equals(key, "fbclid", StringComparison.OrdinalIgnoreCase))
          { hasFbclid = true; clickIdValue ??= value; clickIdKey ??= "fbclid"; }
          else if (string.Equals(key, "msclkid", StringComparison.OrdinalIgnoreCase))
          { hasMsclkid = true; clickIdValue ??= value; clickIdKey ??= "msclkid"; }
          else if (string.Equals(key, "ttclid", StringComparison.OrdinalIgnoreCase))
          { hasTtclid = true; clickIdValue ??= value; clickIdKey ??= "ttclid"; }
      }

      var utmPresent = utmSource != null || utmCampaign != null
                       || hasGclid || hasFbclid || hasMsclkid || hasTtclid;

      if (!utmPresent)
          return AdTrafficDetectionResult.Empty;

      // Infer source platform
      var platform = InferSourcePlatform(utmSource, hasGclid, hasFbclid, hasMsclkid, hasTtclid);

      // Hash values (HMAC-SHA256 when key provided, SHA256 fallback)
      var sourceHash = utmSource != null ? HashValue(utmSource, hmacKey) : null;
      var mediumHash = utmMedium != null ? HashValue(utmMedium, hmacKey) : null;
      var campaignHash = utmCampaign != null ? HashValue(utmCampaign, hmacKey) : null;
      var clickIdHash = clickIdValue != null ? HashValue(clickIdValue, hmacKey) : null;

      // Referrer mismatch detection
      var referrerPresent = !string.IsNullOrEmpty(referer);
      var referrerMismatch = DetectReferrerMismatch(platform, hasGclid || hasFbclid || hasMsclkid || hasTtclid,
          referrerPresent, referer);

      return new AdTrafficDetectionResult
      {
          UtmPresent = true,
          SourcePlatform = platform,
          SourceHash = sourceHash,
          MediumHash = mediumHash,
          CampaignHash = campaignHash,
          HasGclid = hasGclid,
          HasFbclid = hasFbclid,
          HasMsclkid = hasMsclkid,
          HasTtclid = hasTtclid,
          ClickIdHash = clickIdHash,
          ReferrerPresent = referrerPresent,
          ReferrerMismatch = referrerMismatch
      };
  }

  private static string InferSourcePlatform(
      string? utmSource, bool hasGclid, bool hasFbclid, bool hasMsclkid, bool hasTtclid)
  {
      if (hasGclid) return "google";
      if (hasFbclid) return "meta";
      if (hasMsclkid) return "microsoft";
      if (hasTtclid) return "tiktok";
      if (utmSource == null) return "paid_other";

      return utmSource.ToLowerInvariant() switch
      {
          "google" or "google_ads" => "google",
          "facebook" or "fb" or "instagram" => "meta",
          "bing" or "microsoft" => "microsoft",
          "tiktok" => "tiktok",
          _ => "paid_other"
      };
  }

  private static bool DetectReferrerMismatch(
      string platform, bool hasClickId, bool referrerPresent, string? referer)
  {
      // Only check mismatch when a hard click ID is present (gclid/fbclid/etc.)
      // UTM-only traffic without click ID may legitimately have no referer
      if (!hasClickId) return false;

      // Click ID present but no Referer = bot followed URL directly
      if (!referrerPresent) return true;

      // Referer domain must match expected platform domain
      var refererLower = referer!.ToLowerInvariant();
      return platform switch
      {
          "google" => !refererLower.Contains("google.") && !refererLower.Contains("googleadservices"),
          "meta" => !refererLower.Contains("facebook.com") && !refererLower.Contains("instagram.com"),
          "microsoft" => !refererLower.Contains("bing.com") && !refererLower.Contains("microsoft.com"),
          "tiktok" => !refererLower.Contains("tiktok.com"),
          _ => false // paid_other: no expected domain, don't flag as mismatch
      };
  }

  private static string HashValue(string value, byte[]? hmacKey)
  {
      byte[] hash;
      if (hmacKey != null && hmacKey.Length >= 16)
      {
          using var hmac = new System.Security.Cryptography.HMACSHA256(hmacKey);
          hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
      }
      else
      {
          hash = System.Security.Cryptography.SHA256.HashData(
              System.Text.Encoding.UTF8.GetBytes(value));
      }
      // Truncate to 128 bits (16 bytes), URL-safe base64 - same format as PiiHasher
      return Convert.ToBase64String(hash[..16])
          .TrimEnd('=')
          .Replace('+', '-')
          .Replace('/', '_');
  }
  ```

- [ ] **Step 4: Run tests to verify they pass**

  ```bash
  dotnet test Mostlylucid.BotDetection.Test/ \
    --filter "FullyQualifiedName~QueryStringSanitizerAdTrafficTests" -v normal
  ```

  Expected: All 9 tests pass.

- [ ] **Step 5: Commit**

  ```bash
  git add Mostlylucid.BotDetection/Privacy/QueryStringSanitizer.cs \
          Mostlylucid.BotDetection.Test/Privacy/QueryStringSanitizerAdTrafficTests.cs
  git commit -m "feat(click-fraud): add DetectAdTrafficParams to QueryStringSanitizer with HMAC hashing + referrer mismatch"
  ```

---

### Task 3: Update PiiQueryStringContributor to Emit UTM Signals

**Files:**
- Modify: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/PiiQueryStringContributor.cs`

The contributor currently takes only `ILogger`. It needs `PiiHasher` to produce keyed HMAC hashes.

- [ ] **Step 1: Update constructor and ContributeAsync**

  Replace the entire file content:

  ```csharp
  using Microsoft.Extensions.Logging;
  using Mostlylucid.BotDetection.Dashboard;
  using Mostlylucid.BotDetection.Models;
  using Mostlylucid.BotDetection.Privacy;
  using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

  namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

  /// <summary>
  ///     Privacy contributor that detects PII in query strings and emits informational signals.
  ///     Also extracts UTM / click-ID parameters and emits hashed ad-traffic signals BEFORE
  ///     the sanitizer strips them - enabling downstream click-fraud detection with zero PII stored.
  ///     Runs in Wave 0 at Priority 8 (very early, before most detectors).
  /// </summary>
  public class PiiQueryStringContributor : ContributingDetectorBase
  {
      private readonly ILogger<PiiQueryStringContributor> _logger;
      private readonly PiiHasher _hasher;

      public PiiQueryStringContributor(
          ILogger<PiiQueryStringContributor> logger,
          PiiHasher hasher)
      {
          _logger = logger;
          _hasher = hasher;
      }

      public override string Name => "PiiQueryString";
      public override int Priority => 8;
      public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

      public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
          BlackboardState state,
          CancellationToken cancellationToken = default)
      {
          var queryString = state.HttpContext.Request.QueryString.Value;

          if (string.IsNullOrEmpty(queryString))
              return Task.FromResult(None());

          var request = state.HttpContext.Request;
          var referer = request.Headers.Referer.ToString();

          // --- UTM / click-ID detection (before PII stripping) ---
          var adTraffic = QueryStringSanitizer.DetectAdTrafficParams(
              queryString,
              string.IsNullOrEmpty(referer) ? null : referer,
              _hasher.GetKey());

          if (adTraffic.UtmPresent)
          {
              state.WriteSignal(SignalKeys.UtmPresent, true);
              state.WriteSignal(SignalKeys.UtmSourcePlatform, adTraffic.SourcePlatform);
              state.WriteSignal(SignalKeys.UtmHasGclid, adTraffic.HasGclid);
              state.WriteSignal(SignalKeys.UtmHasFbclid, adTraffic.HasFbclid);
              state.WriteSignal(SignalKeys.UtmHasMsclkid, adTraffic.HasMsclkid);
              state.WriteSignal(SignalKeys.UtmHasTtclid, adTraffic.HasTtclid);
              state.WriteSignal(SignalKeys.UtmReferrerPresent, adTraffic.ReferrerPresent);
              state.WriteSignal(SignalKeys.UtmReferrerMismatch, adTraffic.ReferrerMismatch);

              if (adTraffic.SourceHash != null)
                  state.WriteSignal(SignalKeys.UtmSourceHash, adTraffic.SourceHash);
              if (adTraffic.MediumHash != null)
                  state.WriteSignal(SignalKeys.UtmMediumHash, adTraffic.MediumHash);
              if (adTraffic.CampaignHash != null)
                  state.WriteSignal(SignalKeys.UtmCampaignHash, adTraffic.CampaignHash);
              if (adTraffic.ClickIdHash != null)
                  state.WriteSignal(SignalKeys.UtmClickIdHash, adTraffic.ClickIdHash);
          }

          // --- PII detection (unchanged) ---
          var result = QueryStringSanitizer.DetectPii(queryString);

          if (!result.HasPii)
              return Task.FromResult(None());

          var piiTypes = string.Join(",", result.DetectedTypes);
          state.WriteSignal(SignalKeys.PrivacyQueryPiiDetected, true);
          state.WriteSignal(SignalKeys.PrivacyQueryPiiTypes, piiTypes);

          var isHttps = request.IsHttps;
          if (!isHttps)
          {
              state.WriteSignal(SignalKeys.PrivacyUnencryptedPii, true);
              _logger.LogWarning("PII detected in unencrypted query string: types={PiiTypes}", piiTypes);
          }
          else
          {
              _logger.LogDebug("PII detected in query string: types={PiiTypes}", piiTypes);
          }

          var contribution = DetectionContribution.Info(
              Name,
              "Privacy",
              $"Query string contains PII parameters: {piiTypes}");

          return Task.FromResult(Single(contribution));
      }
  }
  ```

- [ ] **Step 2: Add `GetKey()` method to PiiHasher**

  `QueryStringSanitizer.DetectAdTrafficParams` takes a `byte[]` HMAC key. `PiiHasher` keeps its key private. Add a `GetKey()` method to `PiiHasher.cs`:

  ```csharp
  /// <summary>Returns the raw key bytes for use with external HMAC operations.</summary>
  public byte[] GetKey() => _key;
  ```

  Add this method to the `PiiHasher` class in `Mostlylucid.BotDetection/Dashboard/PiiHasher.cs`.

- [ ] **Step 3: Build to verify**

  ```bash
  dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
  ```

  Expected: 0 errors.

- [ ] **Step 4: Commit**

  ```bash
  git add Mostlylucid.BotDetection/Orchestration/ContributingDetectors/PiiQueryStringContributor.cs \
          Mostlylucid.BotDetection/Dashboard/PiiHasher.cs
  git commit -m "feat(click-fraud): PiiQueryStringContributor emits hashed utm.* signals; add PiiHasher.GetKey()"
  ```

---

### Task 4: Create ClickFraudContributor + YAML Manifest

**Files:**
- Create: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ClickFraudContributor.cs`
- Create: `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/clickfraud.detector.yaml`

- [ ] **Step 1: Create the YAML manifest**

  Create `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/clickfraud.detector.yaml`:

  ```yaml
  name: ClickFraudContributor
  priority: 38
  enabled: true
  description: Detects click fraud and invalid ad traffic. Reads UTM/click-ID signals from PiiQueryStringContributor plus session/IP/behavioral signals. Priority 38 places it before IntentContributor (40) so downstream detectors can read clickfraud.* signals.

  scope:
    sink: botdetection
    coordinator: detection
    atom: clickfraud

  taxonomy:
    kind: sensor
    determinism: probabilistic
    persistence: ephemeral
    iab_ivt_class: SIVT

  input:
    accepts:
      - type: botdetection.request
        required: true
        description: Request with UTM signals from PiiQueryStringContributor
        signal_pattern: utm.*,ip.*,session.*,ua.*,behavioral.*,resource_waterfall.*,transport.*

    optional_signals:
      - utm.present
      - utm.has_gclid
      - utm.has_fbclid
      - utm.has_msclkid
      - utm.has_ttclid
      - utm.referrer_mismatch
      - utm.referrer_present
      - utm.source_platform
      - ip.is_datacenter
      - ip.is_vpn
      - ip.is_proxy
      - session.request_count
      - ua.is_headless
      - behavioral.interaction_score
      - resource_waterfall.asset_count
      - transport.protocol_class

  output:
    signals:
      - key: clickfraud.confidence
        entity_type: double
        salience: 0.9
      - key: clickfraud.pattern
        entity_type: string
        salience: 0.8
      - key: clickfraud.is_paid_traffic
        entity_type: boolean
        salience: 0.7
      - key: clickfraud.checked
        entity_type: boolean
        salience: 0.3

  triggers:
    requires_any:
      - utm.present
    requires_all_of:
      - session.request_count
      - ip.is_datacenter

  defaults:
    weights:
      BotSignal: 1.5
    confidence:
      min_to_report: 0.30
      bot_threshold: 0.55
    parameters:
      # Network-level weights
      datacenter_paid_weight: 0.50
      datacenter_unpaid_weight: 0.15
      vpn_paid_weight: 0.25
      proxy_paid_weight: 0.20
      # Referrer integrity weights
      referrer_mismatch_clickid_weight: 0.40
      referrer_mismatch_paid_weight: 0.25
      # Session behavior weights
      single_page_weight: 0.20
      immediate_bounce_weight: 0.25
      immediate_bounce_duration_ms: 5000
      no_assets_weight: 0.15
      # Client/UA weights
      headless_paid_weight: 0.40
      headless_unpaid_weight: 0.20
      no_interaction_weight: 0.15
      no_interaction_threshold: 0.10

  tags:
    - wave-1
    - ad-fraud
    - click-fraud
    - sivt
  ```

- [ ] **Step 2: Create ClickFraudContributor.cs**

  Create `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ClickFraudContributor.cs`:

  ```csharp
  using Microsoft.Extensions.Logging;
  using Mostlylucid.BotDetection.Models;
  using Mostlylucid.BotDetection.Orchestration.Manifests;
  using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

  namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

  /// <summary>
  ///     Click fraud / invalid ad traffic detection contributor.
  ///     Aggregates in-session signals into a weighted confidence score for the ClickFraud bot type.
  ///     Priority 38: runs after PiiQueryStringContributor (8) and IP/UA/behavioral detectors,
  ///     but BEFORE IntentContributor (40) so intent can read clickfraud.* signals.
  ///
  ///     All weights and thresholds from clickfraud.detector.yaml. No magic numbers in code.
  ///     Configuration loaded from: clickfraud.detector.yaml
  /// </summary>
  public class ClickFraudContributor : ConfiguredContributorBase
  {
      private readonly ILogger<ClickFraudContributor> _logger;

      public ClickFraudContributor(
          ILogger<ClickFraudContributor> logger,
          IDetectorConfigProvider configProvider)
          : base(configProvider)
      {
          _logger = logger;
      }

      public override string Name => "ClickFraud";
      public override int Priority => Manifest?.Priority ?? 38;

      public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
      {
          Triggers.AnyOf(
              Triggers.WhenSignalExists(SignalKeys.UtmPresent),
              Triggers.AllOf(
                  Triggers.WhenSignalExists(SignalKeys.SessionRequestCount),
                  Triggers.WhenSignalExists(SignalKeys.IpIsDatacenter)))
      };

      // YAML-driven weights - no magic numbers
      private double DatacenterPaidWeight => GetParam("datacenter_paid_weight", 0.50);
      private double DatacenterUnpaidWeight => GetParam("datacenter_unpaid_weight", 0.15);
      private double VpnPaidWeight => GetParam("vpn_paid_weight", 0.25);
      private double ProxyPaidWeight => GetParam("proxy_paid_weight", 0.20);
      private double ReferrerMismatchClickIdWeight => GetParam("referrer_mismatch_clickid_weight", 0.40);
      private double ReferrerMismatchPaidWeight => GetParam("referrer_mismatch_paid_weight", 0.25);
      private double SinglePageWeight => GetParam("single_page_weight", 0.20);
      private double ImmediateBounceWeight => GetParam("immediate_bounce_weight", 0.25);
      private int ImmediateBounceDurationMs => GetParam("immediate_bounce_duration_ms", 5000);
      private double NoAssetsWeight => GetParam("no_assets_weight", 0.15);
      private double HeadlessPaidWeight => GetParam("headless_paid_weight", 0.40);
      private double HeadlessUnpaidWeight => GetParam("headless_unpaid_weight", 0.20);
      private double NoInteractionWeight => GetParam("no_interaction_weight", 0.15);
      private double NoInteractionThreshold => GetParam("no_interaction_threshold", 0.10);
      private double BotThreshold => GetParam("bot_threshold", 0.55);

      public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
          BlackboardState state,
          CancellationToken cancellationToken = default)
      {
          state.WriteSignal(SignalKeys.ClickFraudChecked, true);

          var isPaid = state.GetSignal<bool?>(SignalKeys.UtmPresent) ?? false;
          var hasClickId = (state.GetSignal<bool?>(SignalKeys.UtmHasGclid) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.UtmHasFbclid) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.UtmHasMsclkid) ?? false)
                           || (state.GetSignal<bool?>(SignalKeys.UtmHasTtclid) ?? false);

          state.WriteSignal(SignalKeys.ClickFraudIsPaidTraffic, isPaid);

          var isDatacenter = state.GetSignal<bool?>(SignalKeys.IpIsDatacenter) ?? false;
          var isVpn = state.GetSignal<bool?>(SignalKeys.IpIsVpn) ?? false;
          var isProxy = state.GetSignal<bool?>(SignalKeys.IpIsProxy) ?? false;
          var referrerMismatch = state.GetSignal<bool?>(SignalKeys.UtmReferrerMismatch) ?? false;
          var sessionCount = state.GetSignal<int?>(SignalKeys.SessionRequestCount) ?? 0;
          var sessionDurationMs = state.GetSignal<double?>(SignalKeys.SessionDurationMs) ?? double.MaxValue;
          var assetCount = state.GetSignal<int?>(SignalKeys.ResourceWaterfallAssetCount) ?? -1;
          var isHeadless = state.GetSignal<bool?>(SignalKeys.UaIsHeadless) ?? false;
          var interactionScore = state.GetSignal<double?>(SignalKeys.BehavioralInteractionScore) ?? 1.0;
          var protocolClass = state.GetSignal<string?>(SignalKeys.TransportProtocolClass) ?? "";

          var score = 0.0;
          var patterns = new List<string>();

          // Network-level signals (GIVT-class - strong)
          if (isDatacenter && isPaid)
          {
              score += DatacenterPaidWeight;
              patterns.Add("datacenter_paid");
          }
          else if (isDatacenter)
          {
              score += DatacenterUnpaidWeight;
              patterns.Add("organic_datacenter");
          }

          if (isVpn && isPaid) score += VpnPaidWeight;
          if (isProxy && isPaid) score += ProxyPaidWeight;

          // Referrer integrity (SIVT-class)
          if (referrerMismatch && hasClickId)
          {
              score += ReferrerMismatchClickIdWeight;
              patterns.Add("referrer_spoof");
          }
          else if (referrerMismatch && isPaid)
          {
              score += ReferrerMismatchPaidWeight;
          }

          // Session behavior
          if (sessionCount == 1)
          {
              score += SinglePageWeight;
              if (!patterns.Contains("datacenter_paid")) patterns.Add("immediate_bounce");
          }

          if (sessionCount <= 2 && sessionDurationMs < ImmediateBounceDurationMs)
          {
              score += ImmediateBounceWeight;
              if (!patterns.Contains("immediate_bounce")) patterns.Add("immediate_bounce");
          }

          if (assetCount == 0 && string.Equals(protocolClass, "document", StringComparison.OrdinalIgnoreCase))
          {
              score += NoAssetsWeight;
              if (!patterns.Contains("engagement_void")) patterns.Add("engagement_void");
          }

          // Client/UA signals
          if (isHeadless && isPaid)
          {
              score += HeadlessPaidWeight;
              patterns.Add("headless_paid");
          }
          else if (isHeadless)
          {
              score += HeadlessUnpaidWeight;
          }

          if (interactionScore < NoInteractionThreshold)
          {
              score += NoInteractionWeight;
              if (assetCount == 0) patterns.Add("engagement_void");
          }

          score = Math.Min(score, 1.0);

          state.WriteSignal(SignalKeys.ClickFraudConfidence, score);
          state.WriteSignal(SignalKeys.ClickFraudPattern,
              patterns.Count > 0 ? string.Join(",", patterns) : "none");

          _logger.LogDebug(
              "ClickFraud: score={Score:F3}, paid={IsPaid}, patterns={Patterns}",
              score, isPaid, string.Join(",", patterns));

          if (score < BotThreshold)
              return Task.FromResult(Single(NeutralContribution("ClickFraud",
                  $"Click fraud score below threshold ({score:F3})")));

          var contribution = BotContribution(
              "ClickFraud",
              $"Click fraud detected: {string.Join(", ", patterns)} (score={score:F3})",
              confidenceOverride: score,
              weightMultiplier: GetParam("BotSignal", 1.5),
              botType: BotType.ClickFraud.ToString());

          return Task.FromResult(Single(contribution));
      }
  }
  ```

  **Correct signal key mapping** (verified against `DetectionContext.cs`):
  - `IpIsDatacenter` = `"ip.is_datacenter"` - exists ✅
  - VPN/proxy: use `"geo.is_vpn"` (`SignalKeys.GeoIsVpn`) and `"geo.is_proxy"` (`SignalKeys.GeoIsProxy`)
  - Headless: use `"fingerprint.headless_score"` (`SignalKeys.FingerprintHeadlessScore`) as `double`, check `> 0.5`
  - Asset count: use `SignalKeys.ResourceAssetCount` = `"resource.asset_count"` ✅
  - `TransportProtocolClass` = `"transport.protocol_class"` - exists ✅
  - `SessionDurationMs` does NOT exist - drop the `sessionDurationMs` check, keep only `sessionCount == 1`
  - `BehavioralInteractionScore` does NOT exist - drop the `no_interaction` scoring for now

  Update the variable declarations and scoring in `ClickFraudContributor` to use:

  ```csharp
  var isVpn = state.GetSignal<bool?>(SignalKeys.GeoIsVpn) ?? false;
  var isProxy = state.GetSignal<bool?>(SignalKeys.GeoIsProxy) ?? false;
  var headlessScore = state.GetSignal<double?>(SignalKeys.FingerprintHeadlessScore) ?? 0.0;
  var isHeadless = headlessScore > 0.5;
  var assetCount = state.GetSignal<int?>(SignalKeys.ResourceAssetCount) ?? -1;
  // Drop: sessionDurationMs (signal doesn't exist), interactionScore (signal doesn't exist)
  ```

  Remove the `ImmediateBounceWeight` scoring block (no session duration signal). Remove the `NoInteractionWeight` scoring block. Update `ImmediateBounceDurationMs` parameter to be unused and remove it from the YAML.

- [ ] **Step 3: Build to verify**

  ```bash
  dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
  ```

  Fix any signal key references that don't match actual constants.

- [ ] **Step 4: Commit**

  ```bash
  git add Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ClickFraudContributor.cs \
          Mostlylucid.BotDetection/Orchestration/Manifests/detectors/clickfraud.detector.yaml
  git commit -m "feat(click-fraud): add ClickFraudContributor at priority 38 with YAML-driven scoring"
  ```

---

### Task 5: Register ClickFraudContributor + Update NarrativeBuilder

**Files:**
- Modify: `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`
- Modify: `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`

- [ ] **Step 1: Register in DI**

  In `ServiceCollectionExtensions.cs`, find where `IntentContributor` is registered (line ~548):

  ```csharp
  services.AddSingleton<IContributingDetector, IntentContributor>();
  ```

  Add `ClickFraudContributor` immediately before it (priority 38 runs before 40):

  ```csharp
  services.AddSingleton<IContributingDetector, ClickFraudContributor>();
  services.AddSingleton<IContributingDetector, IntentContributor>();
  ```

- [ ] **Step 2: Update DetectionNarrativeBuilder**

  In `DetectorFriendlyNames` (line ~11 in `DetectionNarrativeBuilder.cs`), add:

  ```csharp
  ["ClickFraud"] = "click fraud detection",
  ```

  In `DetectorCategories` (line ~65), add:

  ```csharp
  ["ClickFraud"] = "Ad Fraud",
  ```

- [ ] **Step 3: Build entire solution**

  ```bash
  dotnet build mostlylucid.stylobot.sln
  ```

  Expected: 0 errors.

- [ ] **Step 4: Commit**

  ```bash
  git add Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
          Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs
  git commit -m "feat(click-fraud): register ClickFraudContributor in DI; add narrative builder entries"
  ```

---

### Task 6: Write ClickFraudContributor Tests

**Files:**
- Create: `Mostlylucid.BotDetection.Test/Orchestration/ClickFraudContributorTests.cs`

- [ ] **Step 1: Create the test file**

  ```csharp
  using System.Collections.Concurrent;
  using System.Collections.Immutable;
  using Microsoft.AspNetCore.Http;
  using Microsoft.Extensions.Logging.Abstractions;
  using Mostlylucid.BotDetection.Models;
  using Mostlylucid.BotDetection.Orchestration;
  using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
  using Mostlylucid.BotDetection.Orchestration.Manifests;
  using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
  using Xunit;

  namespace Mostlylucid.BotDetection.Test.Orchestration;

  public class ClickFraudContributorTests
  {
      private sealed class StubConfigProvider : IDetectorConfigProvider
      {
          private readonly Dictionary<string, object> _params;
          public StubConfigProvider(Dictionary<string, object>? p = null) => _params = p ?? [];
          public DetectorManifest? GetManifest(string n) => null;
          public DetectorDefaults GetDefaults(string n) => new()
          {
              Weights = new WeightDefaults { Base = 1.0, BotSignal = 1.5 },
              Confidence = new ConfidenceDefaults { BotDetected = 0.3, Neutral = 0.0 },
              Parameters = new Dictionary<string, object>(_params)
          };
          public T GetParameter<T>(string det, string param, T def)
          {
              if (_params.TryGetValue(param, out var v))
                  try { return (T)Convert.ChangeType(v, typeof(T)); } catch { }
              return def;
          }
          public Task<T> GetParameterAsync<T>(string d, string p, ConfigResolutionContext c, T def, CancellationToken ct = default)
              => Task.FromResult(GetParameter(d, p, def));
          public void InvalidateCache(string? n = null) { }
          public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests() => new Dictionary<string, DetectorManifest>();
      }

      private static BlackboardState CreateState(Dictionary<string, object> signals)
      {
          var ctx = new DefaultHttpContext();
          var dict = new ConcurrentDictionary<string, object>(signals);
          return new BlackboardState
          {
              HttpContext = ctx,
              Signals = dict,
              SignalWriter = dict,
              CurrentRiskScore = 0,
              CompletedDetectors = ImmutableHashSet<string>.Empty,
              FailedDetectors = ImmutableHashSet<string>.Empty,
              Contributions = ImmutableList<DetectionContribution>.Empty,
              RequestId = Guid.NewGuid().ToString("N"),
              Elapsed = TimeSpan.Zero
          };
      }

      private static ClickFraudContributor CreateContributor(Dictionary<string, object>? p = null)
          => new(NullLogger<ClickFraudContributor>.Instance, new StubConfigProvider(p));

      [Fact]
      public async Task Contribute_DatacenterAndPaid_HighScore()
      {
          var state = CreateState(new Dictionary<string, object>
          {
              [SignalKeys.UtmPresent] = true,
              [SignalKeys.UtmHasGclid] = true,
              [SignalKeys.IpIsDatacenter] = true,
              [SignalKeys.UtmReferrerMismatch] = true,
              [SignalKeys.SessionRequestCount] = 1
          });

          var result = await CreateContributor().ContributeAsync(state);

          var confidence = state.GetSignal<double?>(SignalKeys.ClickFraudConfidence) ?? 0;
          Assert.True(confidence >= 0.55, $"Expected >= 0.55, got {confidence}");
          Assert.True(result.Any(c => c.ConfidenceDelta > 0), "Expected bot contribution");
      }

      [Fact]
      public async Task Contribute_OrganicResidential_NearZeroScore()
      {
          var state = CreateState(new Dictionary<string, object>
          {
              [SignalKeys.IpIsDatacenter] = false,
              [SignalKeys.UtmPresent] = false,
              [SignalKeys.SessionRequestCount] = 10
          });

          await CreateContributor().ContributeAsync(state);

          var confidence = state.GetSignal<double?>(SignalKeys.ClickFraudConfidence) ?? 0;
          Assert.True(confidence < 0.20, $"Expected < 0.20 for organic residential, got {confidence}");
      }

      [Fact]
      public async Task Contribute_HeadlessPaid_HighScore()
      {
          var state = CreateState(new Dictionary<string, object>
          {
              [SignalKeys.UtmPresent] = true,
              [SignalKeys.FingerprintHeadlessScore] = 0.9,   // > 0.5 = headless
              [SignalKeys.UtmHasGclid] = true,
              [SignalKeys.UtmReferrerMismatch] = false
          });

          await CreateContributor().ContributeAsync(state);

          var confidence = state.GetSignal<double?>(SignalKeys.ClickFraudConfidence) ?? 0;
          Assert.True(confidence >= 0.40, $"Expected >= 0.40 for headless+paid, got {confidence}");
      }

      [Fact]
      public async Task Contribute_ReferrerSpoof_PatternFires()
      {
          var state = CreateState(new Dictionary<string, object>
          {
              [SignalKeys.UtmPresent] = true,
              [SignalKeys.UtmHasGclid] = true,
              [SignalKeys.UtmReferrerMismatch] = true
          });

          await CreateContributor().ContributeAsync(state);

          var pattern = state.GetSignal<string?>(SignalKeys.ClickFraudPattern) ?? "";
          Assert.Contains("referrer_spoof", pattern);
      }

      [Fact]
      public async Task Contribute_TriggersNotMet_NeutralContribution()
      {
          // No utm.present, no ip.is_datacenter
          var state = CreateState(new Dictionary<string, object>
          {
              [SignalKeys.SessionRequestCount] = 5
          });

          // Trigger conditions not met - orchestrator would skip, but test directly
          await CreateContributor().ContributeAsync(state);

          var confidence = state.GetSignal<double?>(SignalKeys.ClickFraudConfidence) ?? 0;
          Assert.True(confidence < 0.55);
      }

      [Fact]
      public async Task Contribute_SetsCheckedSignal()
      {
          var state = CreateState([]);
          await CreateContributor().ContributeAsync(state);
          var checked_ = state.GetSignal<bool?>(SignalKeys.ClickFraudChecked);
          Assert.True(checked_);
      }

      [Fact]
      public async Task Contribute_ScoreCappedAt1()
      {
          // Pile on every signal
          var state = CreateState(new Dictionary<string, object>
          {
              [SignalKeys.UtmPresent] = true,
              [SignalKeys.UtmHasGclid] = true,
              [SignalKeys.UtmHasFbclid] = true,
              [SignalKeys.IpIsDatacenter] = true,
              [SignalKeys.GeoIsVpn] = true,
              [SignalKeys.GeoIsProxy] = true,
              [SignalKeys.UtmReferrerMismatch] = true,
              [SignalKeys.FingerprintHeadlessScore] = 0.9,   // > 0.5 = headless
              [SignalKeys.SessionRequestCount] = 1,
              [SignalKeys.ResourceAssetCount] = 0,
              [SignalKeys.TransportProtocolClass] = "document"
          });

          await CreateContributor().ContributeAsync(state);

          var confidence = state.GetSignal<double?>(SignalKeys.ClickFraudConfidence) ?? 0;
          Assert.True(confidence <= 1.0, "Score must not exceed 1.0");
      }
  }
  ```

- [ ] **Step 2: Run tests**

  ```bash
  dotnet test Mostlylucid.BotDetection.Test/ \
    --filter "FullyQualifiedName~ClickFraudContributorTests" -v normal
  ```

  Expected: All 7 tests pass. Fix any signal key name mismatches.

- [ ] **Step 3: Commit**

  ```bash
  git add Mostlylucid.BotDetection.Test/Orchestration/ClickFraudContributorTests.cs
  git commit -m "test(click-fraud): unit tests for ClickFraudContributor scoring and signal emission"
  ```

---

### Task 7: Wire ClickFraud into IntentContributor

**Files:**
- Modify: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/IntentContributor.cs`
- Modify: `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/intent.detector.yaml`

- [ ] **Step 1: Add click fraud features to `BuildIntentFeatures`**

  In `IntentContributor.cs`, at the end of `BuildIntentFeatures` before `return features;` (around line 333):

  ```csharp
  // Click fraud signals (from ClickFraudContributor at priority 38)
  var clickFraudConfidence = state.GetSignal<double?>(SignalKeys.ClickFraudConfidence) ?? 0.0;
  var isPaidTraffic = state.GetSignal<bool?>(SignalKeys.ClickFraudIsPaidTraffic) ?? false;
  var clickFraudPattern = state.GetSignal<string?>(SignalKeys.ClickFraudPattern) ?? "";
  var hasReferrerSpoof = clickFraudPattern.Contains("referrer_spoof", StringComparison.OrdinalIgnoreCase);

  features["ad_fraud:confidence"] = (float)clickFraudConfidence;
  features["ad_fraud:is_paid"] = isPaidTraffic ? 1.0f : 0.0f;
  features["ad_fraud:referrer_spoof"] = hasReferrerSpoof ? 1.0f : 0.0f;
  ```

- [ ] **Step 2: Add ad_fraud case to `ComputeHeuristicThreat`**

  In `ComputeHeuristicThreat`, add the ad_fraud check before the final `return (FallbackClean, "browsing")` line:

  ```csharp
  // Ad fraud: paid traffic with fraud signals
  var clickFraudConf = state.GetSignal<double?>(SignalKeys.ClickFraudConfidence) ?? 0.0;
  if (clickFraudConf >= GetParam("ad_fraud_confidence_threshold", 0.55))
      return (GetParam("fallback_ad_fraud", 0.45), "ad_fraud");
  ```

- [ ] **Step 3: Emit `intent.ad_fraud_threat` signal**

  In `ContributeInternalAsync`, after the existing `state.WriteSignal(SignalKeys.IntentCategory, intentCategory)` line, add:

  ```csharp
  // Emit ad fraud threat component
  var adFraudConf = state.GetSignal<double?>(SignalKeys.ClickFraudConfidence) ?? 0.0;
  if (adFraudConf > 0)
      state.WriteSignal("intent.ad_fraud_threat", adFraudConf);
  ```

- [ ] **Step 4: Update intent.detector.yaml**

  In `intent.detector.yaml`, add to the `optional_signals:` list:

  ```yaml
      - clickfraud.confidence
      - clickfraud.is_paid_traffic
      - clickfraud.pattern
      - clickfraud.checked
  ```

  Add to the `triggers.requires_any` list:

  ```yaml
      - clickfraud.checked
  ```

  Add to `defaults.parameters`:

  ```yaml
      ad_fraud_confidence_threshold: 0.55
      fallback_ad_fraud: 0.45
  ```

- [ ] **Step 5: Build and run existing intent tests**

  ```bash
  dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
  dotnet test Mostlylucid.BotDetection.Test/ \
    --filter "FullyQualifiedName~IntentContributor" -v normal
  ```

  Expected: Existing tests still pass.

- [ ] **Step 6: Commit**

  ```bash
  git add Mostlylucid.BotDetection/Orchestration/ContributingDetectors/IntentContributor.cs \
          Mostlylucid.BotDetection/Orchestration/Manifests/detectors/intent.detector.yaml
  git commit -m "feat(click-fraud): wire clickfraud.* signals into IntentContributor threat scoring"
  ```

---

### Task 8: Wire ClickFraud into ReputationBiasContributor

**Files:**
- Modify: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ReputationBiasContributor.cs`
- Modify: `Mostlylucid.BotDetection/Orchestration/Manifests/detectors/reputation.detector.yaml`

- [ ] **Step 1: Add paid traffic multiplier to ReputationBiasContributor**

  In `ReputationBiasContributor.cs`, add a config property:

  ```csharp
  private double PaidTrafficBiasMultiplier => GetParam("paid_traffic_bias_multiplier", 1.3);
  private double PaidTrafficConfidenceThreshold => GetParam("paid_traffic_confidence_threshold", 0.40);
  ```

  In `ContributeAsync`, at the very end, after all contributions have been added and before `return Task.FromResult`:

  ```csharp
  // Amplify reputation hits when click fraud signals indicate paid traffic bot
  if (contributions.Count > 0)
  {
      var isPaidTraffic = state.GetSignal<bool?>(SignalKeys.ClickFraudIsPaidTraffic) ?? false;
      var clickFraudConf = state.GetSignal<double?>(SignalKeys.ClickFraudConfidence) ?? 0.0;

      if (isPaidTraffic && clickFraudConf >= PaidTrafficConfidenceThreshold)
      {
          var multiplier = PaidTrafficBiasMultiplier;
          for (var i = 0; i < contributions.Count; i++)
              contributions[i] = contributions[i] with { Weight = contributions[i].Weight * multiplier };

          _logger.LogDebug(
              "Paid traffic reputation amplification applied: multiplier={Multiplier}, clickFraudConf={Conf:F3}",
              multiplier, clickFraudConf);
      }
  }
  ```

  **Note:** `List<DetectionContribution>` uses index assignment. Check that `DetectionContribution` is a record (supports `with` expressions). It is, based on other usages in the codebase.

- [ ] **Step 2: Update reputation.detector.yaml**

  Add to `optional_signals:`:

  ```yaml
      - clickfraud.is_paid_traffic
      - clickfraud.confidence
  ```

  Add to `defaults.parameters` (create the section if it doesn't exist):

  ```yaml
  defaults:
    parameters:
      paid_traffic_bias_multiplier: 1.3
      paid_traffic_confidence_threshold: 0.40
  ```

- [ ] **Step 3: Build and run reputation tests**

  ```bash
  dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
  dotnet test Mostlylucid.BotDetection.Test/ \
    --filter "FullyQualifiedName~Reputation" -v normal
  ```

  Expected: Existing tests still pass.

- [ ] **Step 4: Commit**

  ```bash
  git add Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ReputationBiasContributor.cs \
          Mostlylucid.BotDetection/Orchestration/Manifests/detectors/reputation.detector.yaml
  git commit -m "feat(click-fraud): amplify reputation hits for paid traffic bots in ReputationBiasContributor"
  ```

---

### Task 9: Wire ClickFraud into HeuristicFeatureExtractor

**Files:**
- Modify: `Mostlylucid.BotDetection/Detectors/HeuristicFeatureExtractor.cs`
- Modify: `Mostlylucid.BotDetection/Detectors/HeuristicDetector.cs`

- [ ] **Step 1: Add click fraud signals to ExtractStructuredSignalValues**

  In `HeuristicFeatureExtractor.cs`, at the end of `ExtractStructuredSignalValues` (line ~376, after the `AddStringEnumFeature` calls):

  ```csharp
  // Click fraud structured values
  AddNumericSignalFeature(signals, features, SignalKeys.ClickFraudConfidence, "cf:click_fraud_score");
  AddBooleanSignalFeature(signals, features, SignalKeys.ClickFraudIsPaidTraffic, "cf:is_paid_traffic");
  AddBooleanSignalFeature(signals, features, SignalKeys.UtmReferrerMismatch, "cf:referrer_mismatch");
  ```

- [ ] **Step 2: Add default weight to HeuristicDetector**

  In `HeuristicDetector.cs`, in the `DefaultWeights` dictionary (around line 68), add after the stats section:

  ```csharp
  // Click fraud signal - contributes to heuristic score
  // Low weight (0.5) to avoid double-counting with ClickFraudContributor's direct contribution
  ["cf:click_fraud_score"] = 0.5f,
  ["cf:is_paid_traffic"] = 0.1f,   // Context, not a strong bot indicator alone
  ["cf:referrer_mismatch"] = 0.4f,
  ```

- [ ] **Step 3: Build and run heuristic tests**

  ```bash
  dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
  dotnet test Mostlylucid.BotDetection.Test/ \
    --filter "FullyQualifiedName~HeuristicDetector" -v normal
  ```

  Expected: All existing tests pass.

- [ ] **Step 4: Commit**

  ```bash
  git add Mostlylucid.BotDetection/Detectors/HeuristicFeatureExtractor.cs \
          Mostlylucid.BotDetection/Detectors/HeuristicDetector.cs
  git commit -m "feat(click-fraud): add clickfraud.* to HeuristicFeatureExtractor structured signals"
  ```

---

### Task 10: Full Test Run + Build Verification

- [ ] **Step 1: Run all tests**

  ```bash
  dotnet test mostlylucid.stylobot.sln -v normal 2>&1 | tail -30
  ```

  Expected: All tests pass. No regressions.

- [ ] **Step 2: Verify YAML is embedded as resource**

  Confirm `clickfraud.detector.yaml` is picked up (the `*.yaml` glob in `.csproj` auto-includes it):

  ```bash
  dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj -v detailed 2>&1 \
    | grep -i clickfraud
  ```

  Expected: Line showing `clickfraud.detector.yaml` as embedded resource.

- [ ] **Step 3: Smoke test with demo app**

  ```bash
  dotnet run --project Mostlylucid.BotDetection.Demo &
  # Wait for startup then hit a URL with UTM params
  curl -s "http://localhost:5080/?utm_source=google&gclid=test123" \
    -H "User-Agent: Mozilla/5.0" | head -20
  # Check dashboard for click fraud signal
  curl -s "http://localhost:5080/_stylobot/api/detections" | python3 -m json.tool | grep -i clickfraud
  ```

  Expected: Detection response includes `clickfraud.confidence` signal. Kill demo when done.

- [ ] **Step 4: Final commit if any last-minute fixes**

  ```bash
  git status
  # Commit any remaining changes
  ```
