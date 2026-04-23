# Centroid Freshness: Divergence Staleness + Asset Hash Tracking

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent false-positive bot signals after a content change by detecting when divergence spikes on an endpoint (page was edited) or when a static asset's ETag changes (deploy happened), and suppressing divergence scoring while the centroid adapts.

**Architecture:** Two detection paths feed a shared "centroid staleness" flag. (1) `EndpointDivergenceTracker` counts per-path divergences in a rolling 1-hour window; when the rate exceeds 40% across ≥10 sessions, it marks that endpoint stale in `CentroidSequenceStore`. (2) `AssetHashMiddleware` intercepts response headers after each response for static asset paths, compares the ETag/Last-Modified fingerprint against what `AssetHashStore` recorded last time, and marks the path stale on change. `ContentSequenceContributor` reads both staleness flags and writes `sequence.centroid_stale` / `asset.content_changed` signals; while stale, divergence scoring is suppressed and deferred detectors receive a neutral contribution instead of a bot signal.

**Tech Stack:** C# / .NET 10, `Microsoft.Data.Sqlite`, xUnit, `DefaultHttpContext`, existing `ContentSequenceContributor` / `CentroidSequenceStore` / `SequenceContextStore` infrastructure.

**Spec:** `docs/superpowers/specs/2026-04-23-content-sequence-detection-design.md` (extends content sequence detection)

---

## File Map

| Action | File |
|--------|------|
| Modify | `Mostlylucid.BotDetection/Models/DetectionContext.cs` |
| Create | `Mostlylucid.BotDetection/Services/EndpointDivergenceTracker.cs` |
| Modify | `Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs` |
| Create | `Mostlylucid.BotDetection/Services/AssetHashStore.cs` |
| Create | `Mostlylucid.BotDetection/Middleware/AssetHashMiddleware.cs` |
| Modify | `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs` |
| Modify | `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` |
| Modify | `Mostlylucid.BotDetection/Extensions/ApplicationBuilderExtensions.cs` |
| Create | `Mostlylucid.BotDetection.Test/Services/EndpointDivergenceTrackerTests.cs` |
| Create | `Mostlylucid.BotDetection.Test/Services/AssetHashStoreTests.cs` |
| Create | `Mostlylucid.BotDetection.Test/Middleware/AssetHashMiddlewareTests.cs` |

---

## Task 1: Add Signal Keys

**Files:**
- Modify: `Mostlylucid.BotDetection/Models/DetectionContext.cs`

- [ ] **Step 1: Find the sequence signal key block**

Open `Mostlylucid.BotDetection/Models/DetectionContext.cs`. Find the section that ends with:
```csharp
    /// <summary>Bool: true when no static assets appeared in the critical window- cache warm hit.</summary>
    public const string SequenceCacheWarm = "sequence.cache_warm";
}
```

- [ ] **Step 2: Add two new constants**

Insert before the closing `}`:
```csharp
    /// <summary>Bool: true when divergence rate for this endpoint is high enough to indicate content changed.</summary>
    public const string SequenceCentroidStale = "sequence.centroid_stale";

    /// <summary>Bool: true when a static asset's content fingerprint (ETag/Last-Modified) changed since last recorded.</summary>
    public const string AssetContentChanged = "asset.content_changed";
```

- [ ] **Step 3: Build to verify**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | grep -E "error|Build succeeded"
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection/Models/DetectionContext.cs
git commit -m "feat: add SequenceCentroidStale and AssetContentChanged signal keys"
```

---

## Task 2: EndpointDivergenceTracker

**Files:**
- Create: `Mostlylucid.BotDetection/Services/EndpointDivergenceTracker.cs`
- Test: `Mostlylucid.BotDetection.Test/Services/EndpointDivergenceTrackerTests.cs`

Tracks per-endpoint divergence rate in a rolling 1-hour window. Thread-safe, in-memory only (loss on restart is acceptable- false positives last at most one restart cycle).

- [ ] **Step 1: Write the failing tests**

Create `Mostlylucid.BotDetection.Test/Services/EndpointDivergenceTrackerTests.cs`:

```csharp
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class EndpointDivergenceTrackerTests
{
    [Fact]
    public void RecordSession_increments_total_count()
    {
        var tracker = new EndpointDivergenceTracker();
        tracker.RecordSession("/blog/post");
        tracker.RecordSession("/blog/post");
        Assert.Equal(2, tracker.GetStats("/blog/post").TotalSessions);
    }

    [Fact]
    public void RecordDivergence_increments_divergence_count()
    {
        var tracker = new EndpointDivergenceTracker();
        tracker.RecordSession("/blog/post");
        tracker.RecordDivergence("/blog/post");
        Assert.Equal(1, tracker.GetStats("/blog/post").DivergenceCount);
    }

    [Fact]
    public void IsStale_returns_false_below_min_sessions()
    {
        var tracker = new EndpointDivergenceTracker();
        // Only 5 sessions, threshold requires 10
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordSession("/page");
            tracker.RecordDivergence("/page");
        }
        Assert.False(tracker.IsStale("/page"));
    }

    [Fact]
    public void IsStale_returns_false_below_rate_threshold()
    {
        var tracker = new EndpointDivergenceTracker();
        // 10 sessions, 2 divergences = 20% rate- below 40% threshold
        for (var i = 0; i < 10; i++)
            tracker.RecordSession("/page");
        for (var i = 0; i < 2; i++)
            tracker.RecordDivergence("/page");
        Assert.False(tracker.IsStale("/page"));
    }

    [Fact]
    public void IsStale_returns_true_above_rate_threshold_with_enough_sessions()
    {
        var tracker = new EndpointDivergenceTracker();
        // 10 sessions, 5 divergences = 50% rate- above 40% threshold
        for (var i = 0; i < 10; i++)
            tracker.RecordSession("/page");
        for (var i = 0; i < 5; i++)
            tracker.RecordDivergence("/page");
        Assert.True(tracker.IsStale("/page"));
    }

    [Fact]
    public void GetStats_unknown_path_returns_zero_stats()
    {
        var tracker = new EndpointDivergenceTracker();
        var stats = tracker.GetStats("/unknown");
        Assert.Equal(0, stats.TotalSessions);
        Assert.Equal(0, stats.DivergenceCount);
    }

    [Fact]
    public void Reset_clears_stats_for_path()
    {
        var tracker = new EndpointDivergenceTracker();
        tracker.RecordSession("/page");
        tracker.RecordDivergence("/page");
        tracker.Reset("/page");
        var stats = tracker.GetStats("/page");
        Assert.Equal(0, stats.TotalSessions);
        Assert.Equal(0, stats.DivergenceCount);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "EndpointDivergenceTrackerTests" -v q 2>&1 | tail -5
```

Expected: FAIL with compilation error (type not found).

- [ ] **Step 3: Implement EndpointDivergenceTracker**

Create `Mostlylucid.BotDetection/Services/EndpointDivergenceTracker.cs`:

```csharp
using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Tracks per-endpoint divergence rate in a rolling 1-hour window.
///     When the divergence rate exceeds the threshold (default 40%) across at least
///     <see cref="MinSessions"/> sessions, <see cref="IsStale"/> returns true-
///     indicating the page content likely changed rather than bots arriving.
///     In-memory only. Loss on restart is acceptable (staleness lasts at most one restart cycle).
/// </summary>
public sealed class EndpointDivergenceTracker
{
    public readonly record struct EndpointStats(int TotalSessions, int DivergenceCount);

    private sealed class DivergenceWindow
    {
        private int _totalSessions;
        private int _divergenceCount;
        public DateTimeOffset WindowStart { get; } = DateTimeOffset.UtcNow;
        public int TotalSessions => _totalSessions;
        public int DivergenceCount => _divergenceCount;
        public void IncrementSession() => Interlocked.Increment(ref _totalSessions);
        public void IncrementDivergence() => Interlocked.Increment(ref _divergenceCount);
    }

    private readonly ConcurrentDictionary<string, DivergenceWindow> _windows = new();
    private readonly TimeSpan _windowDuration;
    private readonly double _stalenessRateThreshold;
    private readonly int _minSessions;

    public int MinSessions => _minSessions;

    public EndpointDivergenceTracker(
        TimeSpan? windowDuration = null,
        double stalenessRateThreshold = 0.40,
        int minSessions = 10)
    {
        _windowDuration = windowDuration ?? TimeSpan.FromHours(1);
        _stalenessRateThreshold = stalenessRateThreshold;
        _minSessions = minSessions;
    }

    /// <summary>Record a new session starting at this path (document hit).</summary>
    public void RecordSession(string path)
        => GetOrRefreshWindow(path).IncrementSession();

    /// <summary>Record a divergence event at this path.</summary>
    public void RecordDivergence(string path)
        => GetOrRefreshWindow(path).IncrementDivergence();

    /// <summary>
    ///     Returns true when the divergence rate exceeds the threshold AND at least
    ///     <see cref="MinSessions"/> sessions have been observed in the current window.
    /// </summary>
    public bool IsStale(string path)
    {
        if (!_windows.TryGetValue(path, out var window))
            return false;
        if (window.TotalSessions < _minSessions)
            return false;
        var rate = (double)window.DivergenceCount / window.TotalSessions;
        return rate >= _stalenessRateThreshold;
    }

    /// <summary>Get current stats for a path (for diagnostics / tests).</summary>
    public EndpointStats GetStats(string path)
    {
        if (!_windows.TryGetValue(path, out var window))
            return new EndpointStats(0, 0);
        return new EndpointStats(window.TotalSessions, window.DivergenceCount);
    }

    /// <summary>Reset divergence tracking for a path (called after centroid rebuild).</summary>
    public void Reset(string path) => _windows.TryRemove(path, out _);

    private DivergenceWindow GetOrRefreshWindow(string path)
    {
        if (_windows.TryGetValue(path, out var existing))
        {
            // Window has expired- replace with fresh one
            if (DateTimeOffset.UtcNow - existing.WindowStart > _windowDuration)
            {
                var fresh = new DivergenceWindow();
                _windows[path] = fresh;
                return fresh;
            }
            return existing;
        }

        var created = new DivergenceWindow();
        _windows.TryAdd(path, created);
        // Another thread may have won the race- return whatever is stored
        return _windows[path];
    }
}
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "EndpointDivergenceTrackerTests" -v q 2>&1 | tail -5
```

Expected: 7 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection/Services/EndpointDivergenceTracker.cs \
        Mostlylucid.BotDetection.Test/Services/EndpointDivergenceTrackerTests.cs
git commit -m "feat: add EndpointDivergenceTracker for rolling per-path divergence rate"
```

---

## Task 3: Staleness State in CentroidSequenceStore

**Files:**
- Modify: `Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs`

Adds `MarkEndpointStale`, `IsEndpointStale`, and `ClearEndpointStale` methods. Staleness is in-memory (a `ConcurrentDictionary<string, DateTimeOffset>`)- loss on restart is fine, stale periods last at most a restart cycle.

- [ ] **Step 1: Add stale endpoint state**

Open `Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs`. After the `_centroidChains` field declaration (around line 46), add:

```csharp
// Paths where divergence spiked- suppress scoring until centroid rebuilds.
// Value is the time staleness was declared; expires after _stalenessWindowHours.
private readonly ConcurrentDictionary<string, DateTimeOffset> _staleEndpoints = new();
private const int StalenessWindowHours = 1;
```

Add `using System.Collections.Concurrent;` to the using block if not already present.

- [ ] **Step 2: Add staleness methods**

After the `SetGlobalChain` method (around line 74), add:

```csharp
/// <summary>
///     Mark an endpoint path as stale. Callers should then trigger a centroid rebuild.
///     Staleness expires after <see cref="StalenessWindowHours"/> hours.
/// </summary>
public void MarkEndpointStale(string path)
    => _staleEndpoints[path] = DateTimeOffset.UtcNow;

/// <summary>Returns true when the path was marked stale within the staleness window.</summary>
public bool IsEndpointStale(string path)
{
    if (!_staleEndpoints.TryGetValue(path, out var markedAt))
        return false;
    return DateTimeOffset.UtcNow - markedAt < TimeSpan.FromHours(StalenessWindowHours);
}

/// <summary>Clear staleness for a path- called after a successful rebuild for that path.</summary>
public void ClearEndpointStale(string path) => _staleEndpoints.TryRemove(path, out _);
```

- [ ] **Step 3: Build**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | grep -E "error|Build succeeded"
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs
git commit -m "feat: add endpoint staleness state to CentroidSequenceStore"
```

---

## Task 4: AssetHashStore

**Files:**
- Create: `Mostlylucid.BotDetection/Services/AssetHashStore.cs`
- Test: `Mostlylucid.BotDetection.Test/Services/AssetHashStoreTests.cs`

SQLite-backed store. Records the ETag/Last-Modified fingerprint of each static asset path. Detects when it changes and calls `MarkEndpointStale` on `CentroidSequenceStore`.

- [ ] **Step 1: Write the failing tests**

Create `Mostlylucid.BotDetection.Test/Services/AssetHashStoreTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class AssetHashStoreTests : IDisposable
{
    private readonly string _connectionString;
    private readonly AssetHashStore _store;
    private readonly CentroidSequenceStore _centroidStore;

    public AssetHashStoreTests()
    {
        // In-memory SQLite for tests
        _connectionString = "Data Source=:memory:;Cache=Shared;Mode=Memory";
        _centroidStore = new CentroidSequenceStore(_connectionString, NullLogger<CentroidSequenceStore>.Instance);
        _store = new AssetHashStore(_connectionString, _centroidStore, NullLogger<AssetHashStore>.Instance);
        _centroidStore.InitializeAsync().GetAwaiter().GetResult();
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task RecordHashAsync_first_time_does_not_flag_change()
    {
        var changed = await _store.RecordHashAsync("/vendor/tailwind.css", "\"abc123\"");
        Assert.False(changed);
    }

    [Fact]
    public async Task RecordHashAsync_same_hash_does_not_flag_change()
    {
        await _store.RecordHashAsync("/vendor/tailwind.css", "\"abc123\"");
        var changed = await _store.RecordHashAsync("/vendor/tailwind.css", "\"abc123\"");
        Assert.False(changed);
    }

    [Fact]
    public async Task RecordHashAsync_different_hash_flags_change()
    {
        await _store.RecordHashAsync("/vendor/tailwind.css", "\"abc123\"");
        var changed = await _store.RecordHashAsync("/vendor/tailwind.css", "\"def456\"");
        Assert.True(changed);
    }

    [Fact]
    public async Task RecordHashAsync_change_marks_endpoint_stale()
    {
        await _store.RecordHashAsync("/vendor/tailwind.css", "\"abc123\"");
        await _store.RecordHashAsync("/vendor/tailwind.css", "\"def456\"");
        Assert.True(_centroidStore.IsEndpointStale("/vendor/tailwind.css"));
    }

    [Fact]
    public async Task IsRecentlyChanged_returns_false_when_no_change()
    {
        await _store.RecordHashAsync("/vendor/app.js", "\"hash1\"");
        Assert.False(_store.IsRecentlyChanged("/vendor/app.js"));
    }

    [Fact]
    public async Task IsRecentlyChanged_returns_true_after_change()
    {
        await _store.RecordHashAsync("/vendor/app.js", "\"hash1\"");
        await _store.RecordHashAsync("/vendor/app.js", "\"hash2\"");
        Assert.True(_store.IsRecentlyChanged("/vendor/app.js"));
    }

    [Fact]
    public async Task IsRecentlyChanged_returns_false_for_unknown_path()
    {
        Assert.False(_store.IsRecentlyChanged("/unknown.css"));
    }

    public void Dispose() { }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "AssetHashStoreTests" -v q 2>&1 | tail -5
```

Expected: FAIL (compilation error).

- [ ] **Step 3: Implement AssetHashStore**

Create `Mostlylucid.BotDetection/Services/AssetHashStore.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     SQLite-backed store of static asset content fingerprints (ETag or Last-Modified+Length).
///     Detects when a fingerprint changes between requests, which indicates a deploy happened.
///     On change, marks the asset path stale in <see cref="CentroidSequenceStore"/> so the
///     centroid can be rebuilt and false-positive divergence signals are suppressed.
/// </summary>
public sealed class AssetHashStore
{
    private readonly string _connectionString;
    private readonly CentroidSequenceStore _centroidStore;
    private readonly ILogger<AssetHashStore> _logger;

    // In-memory index for fast IsRecentlyChanged lookups (path → last_changed_at).
    // Populated from DB on startup and updated in-memory on change detection.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentChanges = new();
    private readonly TimeSpan _recentChangeWindow = TimeSpan.FromHours(24);

    public AssetHashStore(
        string connectionString,
        CentroidSequenceStore centroidStore,
        ILogger<AssetHashStore> logger)
    {
        _connectionString = connectionString;
        _centroidStore = centroidStore;
        _logger = logger;
    }

    /// <summary>Create the asset_hashes table and load recent change timestamps into memory.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS asset_hashes (
                path         TEXT PRIMARY KEY,
                hash         TEXT NOT NULL,
                changed_at   TEXT,
                last_seen    TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        await LoadRecentChangesAsync(conn, ct);
    }

    /// <summary>
    ///     Record the content fingerprint for a static asset path.
    ///     If the fingerprint changed since last recorded, marks the path stale and returns true.
    ///     Returns false on first record or unchanged fingerprint.
    /// </summary>
    public async Task<bool> RecordHashAsync(string path, string hash, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Read existing hash
        string? existingHash = null;
        await using (var readCmd = conn.CreateCommand())
        {
            readCmd.CommandText = "SELECT hash FROM asset_hashes WHERE path = @path;";
            readCmd.Parameters.AddWithValue("@path", path);
            var result = await readCmd.ExecuteScalarAsync(ct);
            if (result is string s) existingHash = s;
        }

        var now = DateTimeOffset.UtcNow;
        var changed = existingHash != null && existingHash != hash;
        var changedAt = changed ? now.ToString("O") : (string?)null;

        // Upsert
        await using var upsertCmd = conn.CreateCommand();
        upsertCmd.CommandText = """
            INSERT INTO asset_hashes (path, hash, changed_at, last_seen)
            VALUES (@path, @hash, @changedAt, @lastSeen)
            ON CONFLICT(path) DO UPDATE SET
                hash       = excluded.hash,
                changed_at = COALESCE(excluded.changed_at, asset_hashes.changed_at),
                last_seen  = excluded.last_seen;
            """;
        upsertCmd.Parameters.AddWithValue("@path", path);
        upsertCmd.Parameters.AddWithValue("@hash", hash);
        upsertCmd.Parameters.AddWithValue("@changedAt", changedAt ?? (object)DBNull.Value);
        upsertCmd.Parameters.AddWithValue("@lastSeen", now.ToString("O"));
        await upsertCmd.ExecuteNonQueryAsync(ct);

        if (changed)
        {
            _recentChanges[path] = now;
            _centroidStore.MarkEndpointStale(path);
            _logger.LogInformation("Asset content changed: {Path} ({OldHash} → {NewHash})", path, existingHash, hash);
        }

        return changed;
    }

    /// <summary>
    ///     Returns true when a fingerprint change for this path was detected within
    ///     the last 24 hours. Used by ContentSequenceContributor to suppress divergence scoring.
    /// </summary>
    public bool IsRecentlyChanged(string path)
    {
        if (!_recentChanges.TryGetValue(path, out var changedAt))
            return false;
        return DateTimeOffset.UtcNow - changedAt < _recentChangeWindow;
    }

    private async Task LoadRecentChangesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - _recentChangeWindow;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, changed_at FROM asset_hashes WHERE changed_at IS NOT NULL;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var path = reader.GetString(0);
            if (DateTimeOffset.TryParse(reader.GetString(1), out var changedAt) && changedAt > cutoff)
                _recentChanges[path] = changedAt;
        }
        _logger.LogDebug("AssetHashStore loaded {Count} recent changes from DB", _recentChanges.Count);
    }
}
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "AssetHashStoreTests" -v q 2>&1 | tail -5
```

Expected: 7 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection/Services/AssetHashStore.cs \
        Mostlylucid.BotDetection.Test/Services/AssetHashStoreTests.cs
git commit -m "feat: add AssetHashStore for ETag-based content change detection"
```

---

## Task 5: AssetHashMiddleware

**Files:**
- Create: `Mostlylucid.BotDetection/Middleware/AssetHashMiddleware.cs`
- Test: `Mostlylucid.BotDetection.Test/Middleware/AssetHashMiddlewareTests.cs`

Response-side middleware. Sits BEFORE `BotDetectionMiddleware` in the pipeline so it wraps the full request/response cycle. After `_next` returns, reads the ETag and Last-Modified response headers for static asset paths and records the fingerprint in `AssetHashStore`.

- [ ] **Step 1: Write the failing tests**

Create `Mostlylucid.BotDetection.Test/Middleware/AssetHashMiddlewareTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Middleware;

public class AssetHashMiddlewareTests
{
    private static (AssetHashStore store, CentroidSequenceStore centroidStore) CreateStores()
    {
        var cs = "Data Source=:memory:;Cache=Shared;Mode=Memory";
        var centroid = new CentroidSequenceStore(cs, NullLogger<CentroidSequenceStore>.Instance);
        var assetStore = new AssetHashStore(cs, centroid, NullLogger<AssetHashStore>.Instance);
        centroid.InitializeAsync().GetAwaiter().GetResult();
        assetStore.InitializeAsync().GetAwaiter().GetResult();
        return (assetStore, centroid);
    }

    [Fact]
    public async Task NonStaticPath_does_not_record_hash()
    {
        var (store, _) = CreateStores();
        var middleware = new AssetHashMiddleware(_ => Task.CompletedTask, store, NullLogger<AssetHashMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/blog/post";
        context.Response.Headers.ETag = "\"abc\"";
        await middleware.InvokeAsync(context);
        // No hash recorded- IsRecentlyChanged should be false
        Assert.False(store.IsRecentlyChanged("/blog/post"));
    }

    [Fact]
    public async Task StaticPath_with_etag_records_hash()
    {
        var (store, _) = CreateStores();
        // First request: record "abc"
        var middleware = new AssetHashMiddleware(_ => Task.CompletedTask, store, NullLogger<AssetHashMiddleware>.Instance);
        var context1 = new DefaultHttpContext();
        context1.Request.Path = "/vendor/tailwind.css";
        context1.Response.Headers.ETag = "\"abc\"";
        await middleware.InvokeAsync(context1);

        // Second request: different ETag → change detected
        var context2 = new DefaultHttpContext();
        context2.Request.Path = "/vendor/tailwind.css";
        context2.Response.Headers.ETag = "\"def\"";
        await middleware.InvokeAsync(context2);

        Assert.True(store.IsRecentlyChanged("/vendor/tailwind.css"));
    }

    [Fact]
    public async Task StaticPath_no_etag_uses_last_modified_plus_length()
    {
        var (store, _) = CreateStores();
        var middleware = new AssetHashMiddleware(_ => Task.CompletedTask, store, NullLogger<AssetHashMiddleware>.Instance);

        var context1 = new DefaultHttpContext();
        context1.Request.Path = "/vendor/app.js";
        context1.Response.Headers.LastModified = "Wed, 23 Apr 2026 00:00:00 GMT";
        context1.Response.ContentLength = 1024;
        await middleware.InvokeAsync(context1);

        var context2 = new DefaultHttpContext();
        context2.Request.Path = "/vendor/app.js";
        context2.Response.Headers.LastModified = "Thu, 24 Apr 2026 00:00:00 GMT";
        context2.Response.ContentLength = 2048;
        await middleware.InvokeAsync(context2);

        Assert.True(store.IsRecentlyChanged("/vendor/app.js"));
    }

    [Fact]
    public async Task StaticPath_no_fingerprint_headers_does_not_record()
    {
        var (store, _) = CreateStores();
        var middleware = new AssetHashMiddleware(_ => Task.CompletedTask, store, NullLogger<AssetHashMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/vendor/font.woff2";
        // No ETag, no Last-Modified
        await middleware.InvokeAsync(context);
        Assert.False(store.IsRecentlyChanged("/vendor/font.woff2"));
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "AssetHashMiddlewareTests" -v q 2>&1 | tail -5
```

Expected: FAIL (compilation error).

- [ ] **Step 3: Implement AssetHashMiddleware**

Create `Mostlylucid.BotDetection/Middleware/AssetHashMiddleware.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Response-side middleware that records ETag / Last-Modified fingerprints for static assets.
///     Must be registered BEFORE BotDetectionMiddleware in the pipeline so it wraps the full
///     request/response cycle. After the inner pipeline completes, reads response headers and
///     calls <see cref="AssetHashStore.RecordHashAsync"/> to detect content changes.
///     Only processes static asset paths (CSS, JS, images, fonts).
///     When a fingerprint change is detected, <see cref="AssetHashStore"/> marks the path stale
///     in <see cref="CentroidSequenceStore"/>- ContentSequenceContributor reads this on the
///     NEXT request for the same path and suppresses false-positive divergence scoring.
/// </summary>
public sealed class AssetHashMiddleware(
    RequestDelegate next,
    AssetHashStore assetHashStore,
    ILogger<AssetHashMiddleware> logger)
{
    private static readonly HashSet<string> StaticExtensions =
    [
        ".css", ".js", ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif", ".ico"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        // Only process static asset paths
        var path = context.Request.Path.Value ?? string.Empty;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!StaticExtensions.Contains(ext))
            return;

        // Build a fingerprint from available response headers
        var fingerprint = BuildFingerprint(context.Response);
        if (string.IsNullOrEmpty(fingerprint))
            return;

        try
        {
            var changed = await assetHashStore.RecordHashAsync(path, fingerprint, context.RequestAborted);
            if (changed)
                logger.LogDebug("AssetHashMiddleware: fingerprint changed for {Path}", path);
        }
        catch (Exception ex)
        {
            // Never let hash recording fail a request
            logger.LogWarning(ex, "AssetHashMiddleware: failed to record hash for {Path}", path);
        }
    }

    private static string BuildFingerprint(HttpResponse response)
    {
        // Prefer strong ETag (most reliable content hash)
        var etag = response.Headers.ETag.ToString();
        if (!string.IsNullOrEmpty(etag))
            return etag;

        // Fallback: Last-Modified + Content-Length composite
        var lastModified = response.Headers.LastModified.ToString();
        var length = response.ContentLength?.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(lastModified))
            return $"{lastModified}|{length}";

        return string.Empty;
    }
}
```

- [ ] **Step 4: Run tests to confirm they pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "AssetHashMiddlewareTests" -v q 2>&1 | tail -5
```

Expected: 4 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection/Middleware/AssetHashMiddleware.cs \
        Mostlylucid.BotDetection.Test/Middleware/AssetHashMiddlewareTests.cs
git commit -m "feat: add AssetHashMiddleware for response-side content fingerprint tracking"
```

---

## Task 6: Wire ContentSequenceContributor

**Files:**
- Modify: `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs`

Inject `EndpointDivergenceTracker` and `AssetHashStore`. Use them in document hits and divergence detection.

- [ ] **Step 1: Read the current file**

Open `Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs`. The class currently has:
```csharp
public ContentSequenceContributor(
    ILogger<ContentSequenceContributor> logger,
    IDetectorConfigProvider configProvider,
    SequenceContextStore contextStore,
    CentroidSequenceStore centroidStore,
    BotClusterService? clusterService = null)
    : base(configProvider)
```

- [ ] **Step 2: Add injected services**

Replace the constructor and add two new fields:

```csharp
private readonly EndpointDivergenceTracker _divergenceTracker;
private readonly AssetHashStore? _assetHashStore;

public ContentSequenceContributor(
    ILogger<ContentSequenceContributor> logger,
    IDetectorConfigProvider configProvider,
    SequenceContextStore contextStore,
    CentroidSequenceStore centroidStore,
    EndpointDivergenceTracker divergenceTracker,
    AssetHashStore? assetHashStore = null,
    BotClusterService? clusterService = null)
    : base(configProvider)
{
    _logger = logger;
    _contextStore = contextStore;
    _centroidStore = centroidStore;
    _divergenceTracker = divergenceTracker;
    _assetHashStore = assetHashStore;
    _clusterService = clusterService;
}
```

- [ ] **Step 3: Update HandleDocumentRequest**

Find `HandleDocumentRequest`. After the `_contextStore.Update(signature, newCtx);` line, add:

```csharp
// Record session for divergence rate tracking
_divergenceTracker.RecordSession(contentPath);

// Check if this path's asset hash changed recently
var assetChanged = _assetHashStore?.IsRecentlyChanged(contentPath) ?? false;
```

Then in the `state.WriteSignals([...])` call, add:
```csharp
new(SignalKeys.SequenceCentroidStale, _centroidStore.IsEndpointStale(contentPath)),
new(SignalKeys.AssetContentChanged, assetChanged)
```

So the full WriteSignals block becomes:
```csharp
state.WriteSignals([
    new(SignalKeys.SequencePosition, 0),
    new(SignalKeys.SequenceOnTrack, true),
    new(SignalKeys.SequenceDiverged, false),
    new(SignalKeys.SequenceDivergenceScore, 0.0),
    new(SignalKeys.SequenceChainId, newCtx.ChainId),
    new(SignalKeys.SequenceCentroidType, chain.Type.ToString()),
    new(SignalKeys.SequenceContentPath, contentPath),
    new(SignalKeys.SequenceCentroidStale, _centroidStore.IsEndpointStale(contentPath)),
    new(SignalKeys.AssetContentChanged, assetChanged)
]);
```

Return:
```csharp
return new[] { NeutralContribution("Sequence", $"Document hit- sequence reset at {contentPath}") };
```

- [ ] **Step 4: Update HandleContinuationRequest**

Find `HandleContinuationRequest`. After `var hasDiverged = divergenceScore >= DivergenceThreshold;`, add:

```csharp
// On divergence: record for staleness tracking; check if rate now exceeds staleness threshold
if (hasDiverged)
{
    var contentPath = ctx.ExpectedChain.Length > 0
        ? state.GetSignal<string>(SignalKeys.SequenceContentPath) ?? string.Empty
        : string.Empty;
    if (!string.IsNullOrEmpty(contentPath))
    {
        _divergenceTracker.RecordDivergence(contentPath);
        if (_divergenceTracker.IsStale(contentPath))
        {
            _centroidStore.MarkEndpointStale(contentPath);
            _divergenceTracker.Reset(contentPath); // Reset window after marking stale
            _logger.LogInformation(
                "ContentSequence: divergence rate exceeded threshold for {Path}- marking centroid stale",
                contentPath);
        }
    }
}
```

After the `state.WriteSignals([...])` call, also write the new signals:
```csharp
state.WriteSignal(SignalKeys.SequenceCentroidStale, _centroidStore.IsEndpointStale(
    state.GetSignal<string>(SignalKeys.SequenceContentPath) ?? string.Empty));
```

- [ ] **Step 5: Build**

```bash
dotnet build Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | grep -E "error|Build succeeded"
```

Expected: 0 errors.

- [ ] **Step 6: Run all tests**

```bash
dotnet test Mostlylucid.BotDetection.Test/ -q 2>&1 | tail -10
```

Expected: All passing (some skipped is fine).

- [ ] **Step 7: Commit**

```bash
git add Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs
git commit -m "feat: ContentSequenceContributor tracks divergence rate, reads asset change and centroid staleness"
```

---

## Task 7: DI Registration

**Files:**
- Modify: `Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`
- Modify: `Mostlylucid.BotDetection/Extensions/ApplicationBuilderExtensions.cs`

- [ ] **Step 1: Find the existing ContentSequence registrations**

Open `ServiceCollectionExtensions.cs`. Find the block added in the previous feature:
```csharp
// Content sequence detection- Priority 4, runs before all other detectors
services.TryAddSingleton<SequenceContextStore>();
services.AddSingleton(sp =>
{
    var sessionStore = (Data.SqliteSessionStore)sp.GetRequiredService<Data.ISessionStore>();
    var logger = sp.GetRequiredService<ILogger<CentroidSequenceStore>>();
    return new CentroidSequenceStore(sessionStore.ConnectionString, logger);
});
services.AddSingleton<IContributingDetector, ContentSequenceContributor>();
services.AddHostedService<CentroidSequenceRebuildHostedService>();
```

- [ ] **Step 2: Add EndpointDivergenceTracker and AssetHashStore**

After `services.TryAddSingleton<SequenceContextStore>();`, add:

```csharp
services.TryAddSingleton<EndpointDivergenceTracker>();
services.AddSingleton(sp =>
{
    var sessionStore = (Data.SqliteSessionStore)sp.GetRequiredService<Data.ISessionStore>();
    var centroidStore = sp.GetRequiredService<CentroidSequenceStore>();
    var logger = sp.GetRequiredService<ILogger<AssetHashStore>>();
    return new AssetHashStore(sessionStore.ConnectionString, centroidStore, logger);
});
services.AddHostedService(sp =>
{
    var store = sp.GetRequiredService<AssetHashStore>();
    return new AssetHashInitHostedService(store);
});
```

Then add a tiny hosted service class at the bottom of `ServiceCollectionExtensions.cs` (or in a separate file in `Services/`):

```csharp
/// <summary>Calls AssetHashStore.InitializeAsync on startup to create the SQLite table.</summary>
internal sealed class AssetHashInitHostedService(AssetHashStore store) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => store.InitializeAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 3: Register AssetHashMiddleware in ApplicationBuilderExtensions**

Find `Mostlylucid.BotDetection/Extensions/ApplicationBuilderExtensions.cs`. Read the file to find where `UseBotDetection()` registers middleware.

In the `UseBotDetection()` method, `AssetHashMiddleware` must be registered BEFORE `BotDetectionMiddleware` so it wraps the full cycle. Find the line that adds `BotDetectionMiddleware` and insert before it:

```csharp
app.UseMiddleware<AssetHashMiddleware>();
```

- [ ] **Step 4: Build the full solution**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | grep -E "^.*error|Build succeeded|failed" | head -20
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Run all tests**

```bash
dotnet test mostlylucid.stylobot.sln -q --no-build 2>&1 | tail -15
```

Expected: All previously passing tests still pass.

- [ ] **Step 6: Commit**

```bash
git add Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        Mostlylucid.BotDetection/Extensions/ApplicationBuilderExtensions.cs
git commit -m "feat: register EndpointDivergenceTracker, AssetHashStore, AssetHashMiddleware in DI pipeline"
```

---

## Task 8: Smoke Test

**Files:** None (verification only)

- [ ] **Step 1: Full build**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | grep -E "error|Build succeeded"
```

Expected: 0 errors.

- [ ] **Step 2: All tests**

```bash
dotnet test mostlylucid.stylobot.sln -q 2>&1 | tail -10
```

Report pass/fail/skip counts.

- [ ] **Step 3: Verify signal keys exist**

```bash
grep -n "SequenceCentroidStale\|AssetContentChanged" \
  Mostlylucid.BotDetection/Models/DetectionContext.cs
```

Expected: 2 lines found.

- [ ] **Step 4: Verify new files exist**

```bash
ls Mostlylucid.BotDetection/Services/EndpointDivergenceTracker.cs \
   Mostlylucid.BotDetection/Services/AssetHashStore.cs \
   Mostlylucid.BotDetection/Middleware/AssetHashMiddleware.cs
```

Expected: All 3 files present.

---

## Spec Coverage

| Requirement | Task |
|-------------|------|
| Divergence rate tracking per endpoint | Task 2 |
| Rolling 1-hour window (not cumulative) | Task 2 |
| Min 10 sessions before staleness trigger | Task 2 |
| 40% rate threshold | Task 2 |
| `CentroidSequenceStore.IsEndpointStale` | Task 3 |
| `CentroidSequenceStore.MarkEndpointStale` | Task 3 |
| Window reset after staleness declared | Task 6 |
| `sequence.centroid_stale` signal | Task 1, 6 |
| Static asset ETag fingerprinting | Task 4, 5 |
| Last-Modified fallback fingerprint | Task 5 |
| `AssetHashStore` SQLite table | Task 4 |
| Response-side middleware (not request-side) | Task 5 |
| `asset.content_changed` signal | Task 1, 6 |
| `AssetHashStore` marks endpoint stale on change | Task 4 |
| `ContentSequenceContributor` reads staleness | Task 6 |
| No new contributor (folded into existing) | All tasks |