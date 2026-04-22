# Holodeck Rearchitecture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix holodeck bypass (FastPathReputation early exit kills it), add per-fingerprint keyed coordination, add beacon-based fingerprint correlation via canary values in fake responses.

**Architecture:** Pre-detection `HoneypotPathTagger` middleware tags honeypot paths before any detector runs. `HandleBlockedRequest` checks the tag and delegates to `HolodeckCoordinator` (ephemeral keyed sequential slots, one per fingerprint) instead of 403. `BeaconContributor` (priority 2) scans requests for canary values from previous holodeck responses. `BeaconStore` persists canary→fingerprint mappings in SQLite.

**Tech Stack:** .NET 10, ASP.NET Core middleware, SQLite (Dapper), ephemeral keyed sequential atoms, xUnit

**Spec:** `docs/superpowers/specs/2026-04-22-holodeck-rearchitecture-design.md`

---

## File Map

### New files in `Mostlylucid.BotDetection.ApiHolodeck/`

| File | Responsibility |
|------|---------------|
| `Middleware/HoneypotPathTagger.cs` | Pre-detection path tagging middleware |
| `Services/HolodeckCoordinator.cs` | Keyed sequential engagement slots, global capacity |
| `Services/BeaconStore.cs` | SQLite canary → fingerprint persistence |
| `Services/BeaconCanaryGenerator.cs` | Deterministic HMAC canary generation |
| `Contributors/BeaconContributor.cs` | Priority 2 detector, scans for canary matches |

### Modified files

| File | Change |
|------|--------|
| `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` | Add holodeck check in `HandleBlockedRequest` |
| `Mostlylucid.BotDetection.ApiHolodeck/Models/HolodeckOptions.cs` | Add coordinator + beacon config properties |
| `Mostlylucid.BotDetection.ApiHolodeck/Extensions/ServiceCollectionExtensions.cs` | Register new services |
| `Mostlylucid.BotDetection.ApiHolodeck/Actions/HolodeckActionPolicy.cs` | Inject canaries into responses |
| `Mostlylucid.BotDetection.Demo/appsettings.json` | Signal-based holodeck transitions |

### Test files

| File | Responsibility |
|------|---------------|
| `Mostlylucid.BotDetection.Test/Holodeck/HoneypotPathTaggerTests.cs` | Path matching |
| `Mostlylucid.BotDetection.Test/Holodeck/HolodeckCoordinatorTests.cs` | Keyed slots, capacity |
| `Mostlylucid.BotDetection.Test/Holodeck/BeaconCanaryGeneratorTests.cs` | Deterministic canary generation |
| `Mostlylucid.BotDetection.Test/Holodeck/BeaconStoreTests.cs` | SQLite persistence |
| `Mostlylucid.BotDetection.Test/Holodeck/BeaconContributorTests.cs` | Canary scan + signal writing |

---

## Task 1: HolodeckOptions — add new config properties

**Files:**
- Modify: `Mostlylucid.BotDetection.ApiHolodeck/Models/HolodeckOptions.cs`

- [ ] **Step 1: Add coordinator and beacon config to HolodeckOptions**

Add these properties to the end of `HolodeckOptions` class in `Mostlylucid.BotDetection.ApiHolodeck/Models/HolodeckOptions.cs`, before the Project Honeypot section:

```csharp
    // ==========================================
    // Holodeck Coordinator
    // ==========================================

    /// <summary>
    ///     Maximum concurrent holodeck engagements across all fingerprints.
    ///     When full, new requests get normal 403 block.
    ///     Default: 10
    /// </summary>
    public int MaxConcurrentEngagements { get; set; } = 10;

    /// <summary>
    ///     Maximum concurrent engagements per fingerprint.
    ///     Default: 1 (one fake response at a time per bot)
    /// </summary>
    public int MaxEngagementsPerFingerprint { get; set; } = 1;

    /// <summary>
    ///     Auto-release engagement slot after this timeout (ms).
    ///     Prevents stuck slots from exhausting capacity.
    ///     Default: 5000
    /// </summary>
    public int EngagementTimeoutMs { get; set; } = 5000;

    // ==========================================
    // Beacon Tracking
    // ==========================================

    /// <summary>
    ///     Enable canary-based beacon tracking in holodeck responses.
    ///     When enabled, fake responses contain unique canary values that
    ///     are tracked — if a rotated fingerprint replays a canary, the
    ///     two fingerprints are linked via beacon.matched signal.
    ///     Default: true
    /// </summary>
    public bool EnableBeaconTracking { get; set; } = true;

    /// <summary>
    ///     Time-to-live for beacons in hours. Expired beacons are purged.
    ///     Default: 24
    /// </summary>
    public int BeaconTtlHours { get; set; } = 24;

    /// <summary>
    ///     Length of canary strings (hex chars). Longer = fewer collisions.
    ///     Default: 8
    /// </summary>
    public int BeaconCanaryLength { get; set; } = 8;
```

- [ ] **Step 2: Verify build**

```bash
dotnet build Mostlylucid.BotDetection.ApiHolodeck/ --no-restore -v:minimal
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Mostlylucid.BotDetection.ApiHolodeck/Models/HolodeckOptions.cs
git commit -m "Add coordinator and beacon config to HolodeckOptions"
```

---

## Task 2: BeaconCanaryGenerator

**Files:**
- Create: `Mostlylucid.BotDetection.ApiHolodeck/Services/BeaconCanaryGenerator.cs`
- Create: `Mostlylucid.BotDetection.Test/Holodeck/BeaconCanaryGeneratorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Test/Holodeck/BeaconCanaryGeneratorTests.cs`:

```csharp
using Mostlylucid.BotDetection.ApiHolodeck.Services;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class BeaconCanaryGeneratorTests
{
    private readonly BeaconCanaryGenerator _generator = new("test-secret-key-for-hmac");

    [Fact]
    public void Generate_ReturnsDeterministicCanary()
    {
        var canary1 = _generator.Generate("fingerprint-abc", "/wp-login.php");
        var canary2 = _generator.Generate("fingerprint-abc", "/wp-login.php");
        Assert.Equal(canary1, canary2);
    }

    [Fact]
    public void Generate_DifferentFingerprintsDifferentCanaries()
    {
        var canary1 = _generator.Generate("fingerprint-abc", "/wp-login.php");
        var canary2 = _generator.Generate("fingerprint-xyz", "/wp-login.php");
        Assert.NotEqual(canary1, canary2);
    }

    [Fact]
    public void Generate_DifferentPathsDifferentCanaries()
    {
        var canary1 = _generator.Generate("fingerprint-abc", "/wp-login.php");
        var canary2 = _generator.Generate("fingerprint-abc", "/.env");
        Assert.NotEqual(canary1, canary2);
    }

    [Fact]
    public void Generate_ReturnsCorrectLength()
    {
        var canary = _generator.Generate("fingerprint-abc", "/wp-login.php");
        Assert.Equal(8, canary.Length); // default length
    }

    [Fact]
    public void Generate_CustomLength()
    {
        var generator = new BeaconCanaryGenerator("test-secret", canaryLength: 12);
        var canary = generator.Generate("fp", "/path");
        Assert.Equal(12, canary.Length);
    }

    [Fact]
    public void Generate_OnlyHexCharacters()
    {
        var canary = _generator.Generate("fingerprint-abc", "/wp-login.php");
        Assert.Matches("^[0-9a-f]+$", canary);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "BeaconCanaryGeneratorTests" -v m
```

Expected: FAIL — `BeaconCanaryGenerator` doesn't exist.

- [ ] **Step 3: Implement BeaconCanaryGenerator**

Create `Mostlylucid.BotDetection.ApiHolodeck/Services/BeaconCanaryGenerator.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Mostlylucid.BotDetection.ApiHolodeck.Services;

/// <summary>
///     Generates deterministic canary values for beacon tracking.
///     Same fingerprint + path always produces the same canary.
///     Different fingerprints produce different canaries.
/// </summary>
public sealed class BeaconCanaryGenerator
{
    private readonly byte[] _keyBytes;
    private readonly int _canaryLength;

    public BeaconCanaryGenerator(string secret, int canaryLength = 8)
    {
        _keyBytes = Encoding.UTF8.GetBytes(secret);
        _canaryLength = canaryLength;
    }

    /// <summary>
    ///     Generate a deterministic canary for the given fingerprint and path.
    /// </summary>
    public string Generate(string fingerprint, string path)
    {
        var input = Encoding.UTF8.GetBytes($"{fingerprint}:{path}");
        var hash = HMACSHA256.HashData(_keyBytes, input);
        return Convert.ToHexStringLower(hash)[.._canaryLength];
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "BeaconCanaryGeneratorTests" -v m
```

Expected: All 6 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.ApiHolodeck/Services/BeaconCanaryGenerator.cs \
       Mostlylucid.BotDetection.Test/Holodeck/BeaconCanaryGeneratorTests.cs
git commit -m "Add BeaconCanaryGenerator with HMAC deterministic canaries"
```

---

## Task 3: BeaconStore — SQLite persistence

**Files:**
- Create: `Mostlylucid.BotDetection.ApiHolodeck/Services/BeaconStore.cs`
- Create: `Mostlylucid.BotDetection.Test/Holodeck/BeaconStoreTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Test/Holodeck/BeaconStoreTests.cs`:

```csharp
using Mostlylucid.BotDetection.ApiHolodeck.Services;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class BeaconStoreTests : IDisposable
{
    private readonly BeaconStore _store;
    private readonly string _dbPath;

    public BeaconStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"beacon-test-{Guid.NewGuid():N}.db");
        _store = new BeaconStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task StoreAndLookup_RoundTrips()
    {
        await _store.StoreAsync("abc12345", "fingerprint-1", "/wp-login.php", "wordpress-foss", TimeSpan.FromHours(24));
        var result = await _store.LookupAsync("abc12345");
        Assert.NotNull(result);
        Assert.Equal("fingerprint-1", result.Value.Fingerprint);
        Assert.Equal("/wp-login.php", result.Value.Path);
        Assert.Equal("wordpress-foss", result.Value.PackId);
    }

    [Fact]
    public async Task Lookup_MissingCanary_ReturnsNull()
    {
        var result = await _store.LookupAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task Lookup_ExpiredBeacon_ReturnsNull()
    {
        await _store.StoreAsync("expired1", "fp-1", "/path", null, TimeSpan.FromMilliseconds(1));
        await Task.Delay(50); // let it expire
        var result = await _store.LookupAsync("expired1");
        Assert.Null(result);
    }

    [Fact]
    public async Task BatchLookup_ReturnsOnlyMatches()
    {
        await _store.StoreAsync("match001", "fp-1", "/a", null, TimeSpan.FromHours(1));
        await _store.StoreAsync("match002", "fp-2", "/b", null, TimeSpan.FromHours(1));

        var results = await _store.BatchLookupAsync(["match001", "nomatch1", "match002"]);
        Assert.Equal(2, results.Count);
        Assert.Equal("fp-1", results["match001"].Fingerprint);
        Assert.Equal("fp-2", results["match002"].Fingerprint);
    }

    [Fact]
    public async Task Cleanup_RemovesExpiredBeacons()
    {
        await _store.StoreAsync("keep0001", "fp-1", "/a", null, TimeSpan.FromHours(1));
        await _store.StoreAsync("expire01", "fp-2", "/b", null, TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        var removed = await _store.CleanupExpiredAsync();
        Assert.True(removed >= 1);

        Assert.NotNull(await _store.LookupAsync("keep0001"));
        Assert.Null(await _store.LookupAsync("expire01"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "BeaconStoreTests" -v m
```

Expected: FAIL — `BeaconStore` doesn't exist.

- [ ] **Step 3: Implement BeaconStore**

Create `Mostlylucid.BotDetection.ApiHolodeck/Services/BeaconStore.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Mostlylucid.BotDetection.ApiHolodeck.Services;

/// <summary>
///     Stores beacon canary → fingerprint mappings in SQLite.
///     Used to correlate rotated fingerprints when they replay canary values.
/// </summary>
public sealed class BeaconStore : IDisposable
{
    private readonly string _connectionString;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public BeaconStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public record BeaconRecord(string Fingerprint, string Path, string? PackId, DateTime CreatedAt);

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS beacons (
                    canary TEXT PRIMARY KEY,
                    fingerprint TEXT NOT NULL,
                    path TEXT NOT NULL,
                    pack_id TEXT,
                    created_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_beacons_expires ON beacons(expires_at);
                CREATE INDEX IF NOT EXISTS ix_beacons_fingerprint ON beacons(fingerprint);
                """;
            await cmd.ExecuteNonQueryAsync();
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task StoreAsync(string canary, string fingerprint, string path, string? packId, TimeSpan ttl)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO beacons (canary, fingerprint, path, pack_id, created_at, expires_at)
            VALUES (@canary, @fingerprint, @path, @packId, @createdAt, @expiresAt)
            """;
        var now = DateTime.UtcNow;
        cmd.Parameters.AddWithValue("@canary", canary);
        cmd.Parameters.AddWithValue("@fingerprint", fingerprint);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@packId", (object?)packId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", now.ToString("O"));
        cmd.Parameters.AddWithValue("@expiresAt", (now + ttl).ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<BeaconRecord?> LookupAsync(string canary)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint, path, pack_id, created_at FROM beacons
            WHERE canary = @canary AND expires_at > @now
            """;
        cmd.Parameters.AddWithValue("@canary", canary);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new BeaconRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTime.Parse(reader.GetString(3)));
    }

    public async Task<Dictionary<string, BeaconRecord>> BatchLookupAsync(IReadOnlyList<string> canaries)
    {
        if (canaries.Count == 0) return new();
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var results = new Dictionary<string, BeaconRecord>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow.ToString("O");

        // Batch in groups of 50 to stay under SQLite parameter limits
        foreach (var batch in canaries.Chunk(50))
        {
            await using var cmd = conn.CreateCommand();
            var paramNames = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                var pname = $"@c{i}";
                paramNames.Add(pname);
                cmd.Parameters.AddWithValue(pname, batch[i]);
            }
            cmd.Parameters.AddWithValue("@now", now);
            cmd.CommandText = $"""
                SELECT canary, fingerprint, path, pack_id, created_at FROM beacons
                WHERE canary IN ({string.Join(",", paramNames)}) AND expires_at > @now
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results[reader.GetString(0)] = new BeaconRecord(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTime.Parse(reader.GetString(4)));
            }
        }

        return results;
    }

    public async Task<int> CleanupExpiredAsync()
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM beacons WHERE expires_at <= @now";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _initLock.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "BeaconStoreTests" -v m
```

Expected: All 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.ApiHolodeck/Services/BeaconStore.cs \
       Mostlylucid.BotDetection.Test/Holodeck/BeaconStoreTests.cs
git commit -m "Add BeaconStore for canary→fingerprint persistence in SQLite"
```

---

## Task 4: HoneypotPathTagger middleware

**Files:**
- Create: `Mostlylucid.BotDetection.ApiHolodeck/Middleware/HoneypotPathTagger.cs`
- Create: `Mostlylucid.BotDetection.Test/Holodeck/HoneypotPathTaggerTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Test/Holodeck/HoneypotPathTaggerTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Middleware;
using Mostlylucid.BotDetection.ApiHolodeck.Models;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class HoneypotPathTaggerTests
{
    private static HoneypotPathTagger CreateTagger(
        List<string>? paths = null,
        RequestDelegate? next = null)
    {
        var options = Options.Create(new HolodeckOptions
        {
            HoneypotPaths = paths ?? ["/wp-login.php", "/.env", "/phpmyadmin", "/wp-admin"]
        });
        return new HoneypotPathTagger(next ?? (_ => Task.CompletedTask), options);
    }

    [Fact]
    public async Task ExactMatch_TagsContext()
    {
        var tagger = CreateTagger();
        var context = new DefaultHttpContext();
        context.Request.Path = "/wp-login.php";

        await tagger.InvokeAsync(context);

        Assert.True(context.Items["Holodeck.IsHoneypotPath"] is true);
        Assert.Equal("/wp-login.php", context.Items["Holodeck.MatchedPath"]);
    }

    [Fact]
    public async Task CaseInsensitiveMatch_TagsContext()
    {
        var tagger = CreateTagger();
        var context = new DefaultHttpContext();
        context.Request.Path = "/WP-LOGIN.PHP";

        await tagger.InvokeAsync(context);

        Assert.True(context.Items["Holodeck.IsHoneypotPath"] is true);
    }

    [Fact]
    public async Task PrefixMatch_TagsContext()
    {
        var tagger = CreateTagger();
        var context = new DefaultHttpContext();
        context.Request.Path = "/wp-admin/post.php";

        await tagger.InvokeAsync(context);

        Assert.True(context.Items["Holodeck.IsHoneypotPath"] is true);
    }

    [Fact]
    public async Task NoMatch_DoesNotTag()
    {
        var tagger = CreateTagger();
        var context = new DefaultHttpContext();
        context.Request.Path = "/products/123";

        await tagger.InvokeAsync(context);

        Assert.False(context.Items.ContainsKey("Holodeck.IsHoneypotPath"));
    }

    [Fact]
    public async Task CallsNext()
    {
        var nextCalled = false;
        var tagger = CreateTagger(next: _ => { nextCalled = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Path = "/anything";

        await tagger.InvokeAsync(context);

        Assert.True(nextCalled);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "HoneypotPathTaggerTests" -v m
```

Expected: FAIL — class doesn't exist.

- [ ] **Step 3: Implement HoneypotPathTagger**

Create `Mostlylucid.BotDetection.ApiHolodeck/Middleware/HoneypotPathTagger.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;

namespace Mostlylucid.BotDetection.ApiHolodeck.Middleware;

/// <summary>
///     Pre-detection middleware that tags honeypot paths on HttpContext.Items.
///     Runs before BotDetectionMiddleware so the tag is available even when
///     early exit prevents the HoneypotLinkContributor from running.
/// </summary>
public sealed class HoneypotPathTagger
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _exactPaths;
    private readonly List<string> _prefixPaths;

    public HoneypotPathTagger(RequestDelegate next, IOptions<HolodeckOptions> options)
    {
        _next = next;

        var paths = options.Value.HoneypotPaths;
        _exactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _prefixPaths = new List<string>();

        foreach (var p in paths)
        {
            var normalized = p.TrimEnd('/');
            _exactPaths.Add(normalized);
            // Also register as prefix so /wp-admin matches /wp-admin/post.php
            _prefixPaths.Add(normalized);
        }
    }

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Exact match
        if (_exactPaths.Contains(path.TrimEnd('/')))
        {
            context.Items["Holodeck.IsHoneypotPath"] = true;
            context.Items["Holodeck.MatchedPath"] = path;
            return _next(context);
        }

        // Prefix match (e.g., /wp-admin matches /wp-admin/post.php)
        foreach (var prefix in _prefixPaths)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (path.Length == prefix.Length || path[prefix.Length] == '/'))
            {
                context.Items["Holodeck.IsHoneypotPath"] = true;
                context.Items["Holodeck.MatchedPath"] = prefix;
                return _next(context);
            }
        }

        return _next(context);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "HoneypotPathTaggerTests" -v m
```

Expected: All 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.ApiHolodeck/Middleware/HoneypotPathTagger.cs \
       Mostlylucid.BotDetection.Test/Holodeck/HoneypotPathTaggerTests.cs
git commit -m "Add HoneypotPathTagger pre-detection middleware with tests"
```

---

## Task 5: HolodeckCoordinator — keyed sequential engagement slots

**Files:**
- Create: `Mostlylucid.BotDetection.ApiHolodeck/Services/HolodeckCoordinator.cs`
- Create: `Mostlylucid.BotDetection.Test/Holodeck/HolodeckCoordinatorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Test/Holodeck/HolodeckCoordinatorTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.ApiHolodeck.Services;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class HolodeckCoordinatorTests
{
    private static HolodeckCoordinator CreateCoordinator(int maxConcurrent = 10, int maxPerFp = 1)
    {
        var options = Options.Create(new HolodeckOptions
        {
            MaxConcurrentEngagements = maxConcurrent,
            MaxEngagementsPerFingerprint = maxPerFp,
            EngagementTimeoutMs = 5000
        });
        return new HolodeckCoordinator(options);
    }

    [Fact]
    public void TryEngage_FirstRequest_Succeeds()
    {
        var coordinator = CreateCoordinator();
        Assert.True(coordinator.TryEngage("fp-1", out var slot));
        Assert.NotNull(slot);
        slot.Dispose();
    }

    [Fact]
    public void TryEngage_SameFingerprint_Blocked()
    {
        var coordinator = CreateCoordinator();
        Assert.True(coordinator.TryEngage("fp-1", out var slot1));
        Assert.False(coordinator.TryEngage("fp-1", out _));
        slot1!.Dispose();
    }

    [Fact]
    public void TryEngage_AfterDispose_SameFingerprint_Succeeds()
    {
        var coordinator = CreateCoordinator();
        Assert.True(coordinator.TryEngage("fp-1", out var slot1));
        slot1!.Dispose();
        Assert.True(coordinator.TryEngage("fp-1", out var slot2));
        slot2!.Dispose();
    }

    [Fact]
    public void TryEngage_DifferentFingerprints_BothSucceed()
    {
        var coordinator = CreateCoordinator();
        Assert.True(coordinator.TryEngage("fp-1", out var slot1));
        Assert.True(coordinator.TryEngage("fp-2", out var slot2));
        slot1!.Dispose();
        slot2!.Dispose();
    }

    [Fact]
    public void TryEngage_GlobalCapacityExhausted_Blocked()
    {
        var coordinator = CreateCoordinator(maxConcurrent: 2);
        Assert.True(coordinator.TryEngage("fp-1", out var s1));
        Assert.True(coordinator.TryEngage("fp-2", out var s2));
        Assert.False(coordinator.TryEngage("fp-3", out _)); // capacity full
        s1!.Dispose();
        Assert.True(coordinator.TryEngage("fp-3", out var s3)); // slot freed
        s2!.Dispose();
        s3!.Dispose();
    }

    [Fact]
    public void ActiveEngagements_TracksCorrectly()
    {
        var coordinator = CreateCoordinator();
        Assert.Equal(0, coordinator.ActiveEngagements);
        coordinator.TryEngage("fp-1", out var s1);
        Assert.Equal(1, coordinator.ActiveEngagements);
        coordinator.TryEngage("fp-2", out var s2);
        Assert.Equal(2, coordinator.ActiveEngagements);
        s1!.Dispose();
        Assert.Equal(1, coordinator.ActiveEngagements);
        s2!.Dispose();
        Assert.Equal(0, coordinator.ActiveEngagements);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "HolodeckCoordinatorTests" -v m
```

Expected: FAIL — class doesn't exist.

- [ ] **Step 3: Implement HolodeckCoordinator**

Create `Mostlylucid.BotDetection.ApiHolodeck/Services/HolodeckCoordinator.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;

namespace Mostlylucid.BotDetection.ApiHolodeck.Services;

/// <summary>
///     Manages concurrent holodeck engagements. One slot per fingerprint,
///     global capacity cap. When a slot is busy or capacity is full,
///     the request falls through to normal 403 blocking.
/// </summary>
public sealed class HolodeckCoordinator
{
    private readonly ConcurrentDictionary<string, byte> _activeSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxConcurrent;
    private readonly int _maxPerFingerprint;
    private int _activeCount;

    public HolodeckCoordinator(IOptions<HolodeckOptions> options)
    {
        _maxConcurrent = options.Value.MaxConcurrentEngagements;
        _maxPerFingerprint = options.Value.MaxEngagementsPerFingerprint;
    }

    public int ActiveEngagements => _activeCount;
    public int Capacity => _maxConcurrent;

    /// <summary>
    ///     Try to acquire a holodeck engagement slot for the given fingerprint.
    ///     Returns false if the fingerprint already has an active engagement
    ///     or global capacity is exhausted.
    /// </summary>
    public bool TryEngage(string fingerprint, out IDisposable? slot)
    {
        slot = null;

        // Check global capacity
        if (Interlocked.CompareExchange(ref _activeCount, 0, 0) >= _maxConcurrent)
            return false;

        // Check per-fingerprint limit
        if (!_activeSlots.TryAdd(fingerprint, 0))
            return false;

        // Increment global counter
        if (Interlocked.Increment(ref _activeCount) > _maxConcurrent)
        {
            // Raced past capacity — release and fail
            _activeSlots.TryRemove(fingerprint, out _);
            Interlocked.Decrement(ref _activeCount);
            return false;
        }

        slot = new EngagementSlot(this, fingerprint);
        return true;
    }

    private void Release(string fingerprint)
    {
        _activeSlots.TryRemove(fingerprint, out _);
        Interlocked.Decrement(ref _activeCount);
    }

    private sealed class EngagementSlot : IDisposable
    {
        private readonly HolodeckCoordinator _coordinator;
        private readonly string _fingerprint;
        private int _disposed;

        public EngagementSlot(HolodeckCoordinator coordinator, string fingerprint)
        {
            _coordinator = coordinator;
            _fingerprint = fingerprint;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _coordinator.Release(_fingerprint);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "HolodeckCoordinatorTests" -v m
```

Expected: All 6 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.ApiHolodeck/Services/HolodeckCoordinator.cs \
       Mostlylucid.BotDetection.Test/Holodeck/HolodeckCoordinatorTests.cs
git commit -m "Add HolodeckCoordinator with keyed per-fingerprint engagement slots"
```

---

## Task 6: BeaconContributor — canary scan detector

**Files:**
- Create: `Mostlylucid.BotDetection.ApiHolodeck/Contributors/BeaconContributor.cs`
- Create: `Mostlylucid.BotDetection.Test/Holodeck/BeaconContributorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `Mostlylucid.BotDetection.Test/Holodeck/BeaconContributorTests.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Contributors;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.ApiHolodeck.Services;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class BeaconContributorTests : IDisposable
{
    private readonly BeaconStore _store;
    private readonly BeaconContributor _contributor;
    private readonly string _dbPath;

    public BeaconContributorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"beacon-test-{Guid.NewGuid():N}.db");
        _store = new BeaconStore($"Data Source={_dbPath}");
        var options = Options.Create(new HolodeckOptions { BeaconCanaryLength = 8 });
        _contributor = new BeaconContributor(
            NullLogger<BeaconContributor>.Instance, _store, options);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task QueryParam_MatchesBeacon()
    {
        await _store.StoreAsync("abc12345", "old-fingerprint", "/wp-login.php", null, TimeSpan.FromHours(1));

        var context = new DefaultHttpContext();
        context.Request.Path = "/wp-admin/post.php";
        context.Request.QueryString = new QueryString("?nonce=abc12345&action=edit");

        var state = CreateState(context);
        var contributions = await _contributor.ContributeAsync(state);

        Assert.Single(contributions);
        Assert.Equal("old-fingerprint",
            state.Signals.TryGetValue("beacon.original_fingerprint", out var fp) ? fp : null);
        Assert.Equal(true,
            state.Signals.TryGetValue("beacon.matched", out var matched) ? matched : null);
    }

    [Fact]
    public async Task NoCanaryInRequest_NoMatch()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/products/123";
        context.Request.QueryString = new QueryString("?page=2");

        var state = CreateState(context);
        var contributions = await _contributor.ContributeAsync(state);

        Assert.Single(contributions);
        Assert.False(state.Signals.ContainsKey("beacon.matched"));
    }

    [Fact]
    public async Task CookieValue_MatchesBeacon()
    {
        await _store.StoreAsync("cook1234", "cookie-fp", "/.env", null, TimeSpan.FromHours(1));

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/data";
        context.Request.Headers["Cookie"] = "session=cook1234; other=value";

        var state = CreateState(context);
        var contributions = await _contributor.ContributeAsync(state);

        Assert.Equal("cookie-fp",
            state.Signals.TryGetValue("beacon.original_fingerprint", out var fp) ? fp : null);
    }

    private static BlackboardState CreateState(HttpContext context)
    {
        var signals = new ConcurrentDictionary<string, object>();
        return new BlackboardState
        {
            HttpContext = context,
            Signals = signals,
            SignalWriter = signals,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = new List<DetectionContribution>(),
            RequestId = "test",
            Elapsed = TimeSpan.Zero
        };
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "BeaconContributorTests" -v m
```

Expected: FAIL — class doesn't exist.

- [ ] **Step 3: Implement BeaconContributor**

Create `Mostlylucid.BotDetection.ApiHolodeck/Contributors/BeaconContributor.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.ApiHolodeck.Services;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.ApiHolodeck.Contributors;

/// <summary>
///     Scans incoming requests for beacon canary values from previous holodeck responses.
///     When a canary matches, writes beacon.matched and beacon.original_fingerprint signals
///     for entity resolution to link rotated fingerprints.
///     Runs at priority 2 (before FastPathReputation at 3) so the signal is always available.
/// </summary>
public class BeaconContributor : ContributingDetectorBase
{
    private readonly BeaconStore _store;
    private readonly ILogger<BeaconContributor> _logger;
    private readonly int _canaryLength;

    public BeaconContributor(
        ILogger<BeaconContributor> logger,
        BeaconStore store,
        IOptions<HolodeckOptions> options)
    {
        _logger = logger;
        _store = store;
        _canaryLength = options.Value.BeaconCanaryLength;
    }

    public override string Name => "Beacon";
    public override int Priority => 2; // Before FastPathReputation (3)
    public override IReadOnlyList<TriggerCondition> TriggerConditions => [];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var candidates = ExtractCandidates(state);
            if (candidates.Count == 0)
                return Single(NeutralContribution("Beacon", "No canary candidates in request"));

            var matches = await _store.BatchLookupAsync(candidates);
            if (matches.Count == 0)
                return Single(NeutralContribution("Beacon", $"Scanned {candidates.Count} candidates, no match"));

            // Take the first match (most beacon scenarios produce one match)
            var (canary, record) = matches.First();

            state.WriteSignal("beacon.matched", true);
            state.WriteSignal("beacon.original_fingerprint", record.Fingerprint);
            state.WriteSignal("beacon.canary", canary);
            state.WriteSignal("beacon.path", record.Path);
            state.WriteSignal("beacon.age_seconds",
                (DateTime.UtcNow - record.CreatedAt).TotalSeconds);

            if (record.PackId != null)
                state.WriteSignal("beacon.pack_id", record.PackId);

            _logger.LogInformation(
                "Beacon matched: canary={Canary} links current request to fingerprint={OriginalFp} from path={Path}",
                canary, record.Fingerprint, record.Path);

            return Single(NeutralContribution("Beacon",
                $"Beacon match: canary {canary} → fingerprint {record.Fingerprint[..8]}..."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Beacon scan failed");
            return Single(NeutralContribution("Beacon", "Beacon scan error"));
        }
    }

    private List<string> ExtractCandidates(BlackboardState state)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var context = state.HttpContext;

        // Query string values
        foreach (var (_, values) in context.Request.Query)
        foreach (var v in values)
        {
            if (v != null && v.Length == _canaryLength)
                candidates.Add(v);
        }

        // Path segments
        var path = context.Request.Path.Value ?? "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length == _canaryLength)
                candidates.Add(segment);
        }

        // Cookie values
        foreach (var cookie in context.Request.Cookies)
        {
            if (cookie.Value.Length == _canaryLength)
                candidates.Add(cookie.Value);
        }

        // Referer query params
        var referer = context.Request.Headers.Referer.FirstOrDefault();
        if (referer != null)
        {
            var qIdx = referer.IndexOf('?');
            if (qIdx >= 0)
            {
                var qs = referer[(qIdx + 1)..];
                foreach (var pair in qs.Split('&'))
                {
                    var eqIdx = pair.IndexOf('=');
                    if (eqIdx >= 0)
                    {
                        var val = pair[(eqIdx + 1)..];
                        if (val.Length == _canaryLength)
                            candidates.Add(Uri.UnescapeDataString(val));
                    }
                }
            }
        }

        return candidates.ToList();
    }

    private static DetectionContribution NeutralContribution(string category, string reason) =>
        new()
        {
            DetectorName = "Beacon",
            Category = category,
            ConfidenceDelta = 0,
            Weight = 0,
            Reason = reason
        };

    private static IReadOnlyList<DetectionContribution> Single(DetectionContribution c) => [c];
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --filter "BeaconContributorTests" -v m
```

Expected: All 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.ApiHolodeck/Contributors/BeaconContributor.cs \
       Mostlylucid.BotDetection.Test/Holodeck/BeaconContributorTests.cs
git commit -m "Add BeaconContributor: scans requests for canary values, writes beacon signals"
```

---

## Task 7: Wire into middleware and DI

**Files:**
- Modify: `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`
- Modify: `Mostlylucid.BotDetection.ApiHolodeck/Extensions/ServiceCollectionExtensions.cs`
- Modify: `Mostlylucid.BotDetection.Demo/Program.cs`
- Modify: `Mostlylucid.BotDetection.Demo/appsettings.json`

- [ ] **Step 1: Register new services in AddApiHolodeck**

In `Mostlylucid.BotDetection.ApiHolodeck/Extensions/ServiceCollectionExtensions.cs`, add to the first `AddApiHolodeck` overload (after the existing `AddHostedService<HoneypotReporter>()` line):

```csharp
        // Register holodeck coordinator
        services.AddSingleton<HolodeckCoordinator>();

        // Register beacon tracking
        services.AddSingleton<BeaconCanaryGenerator>(sp =>
        {
            var botOptions = sp.GetRequiredService<IOptions<BotDetectionOptions>>();
            var holoOptions = sp.GetRequiredService<IOptions<HolodeckOptions>>();
            var secret = botOptions.Value.SignatureHashKey ?? "stylobot-default-beacon-key";
            return new BeaconCanaryGenerator(secret, holoOptions.Value.BeaconCanaryLength);
        });

        services.AddSingleton<BeaconStore>(sp =>
        {
            var env = sp.GetRequiredService<IHostEnvironment>();
            var dbPath = Path.Combine(env.ContentRootPath, "beacons.db");
            return new BeaconStore($"Data Source={dbPath};Cache=Shared");
        });

        // Register beacon contributor
        services.AddSingleton<BeaconContributor>();
        services.AddSingleton<IContributingDetector>(sp => sp.GetRequiredService<BeaconContributor>());
```

Add the necessary `using` statements at the top:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
```

Also add the same registrations to the second `AddApiHolodeck(IConfiguration)` overload.

- [ ] **Step 2: Add holodeck check to BotDetectionMiddleware.HandleBlockedRequest**

In `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`, find the `HandleBlockedRequest` method (line ~1025). Add holodeck check at the very beginning of the method, before the `switch (action)`:

```csharp
    private async Task HandleBlockedRequest(
        HttpContext context,
        AggregatedEvidence aggregated,
        DetectionPolicy policy,
        BotPolicyAttribute? policyAttr,
        BotBlockAction action)
    {
        var riskScore = aggregated.BotProbability;

        // --- Holodeck engagement check ---
        // If the path was tagged as a honeypot (pre-detection) or attack signals fired,
        // try holodeck instead of hard block.
        var holodeckCoordinator = context.RequestServices.GetService<HolodeckCoordinator>();
        if (holodeckCoordinator != null)
        {
            var isHoneypotPath = context.Items.TryGetValue("Holodeck.IsHoneypotPath", out var hp) && hp is true;
            var hasAttackSignal = aggregated.Signals.ContainsKey(SignalKeys.AttackDetected);

            if (isHoneypotPath || hasAttackSignal)
            {
                var fingerprint = aggregated.Signals.TryGetValue(SignalKeys.PrimarySignature, out var sig)
                    ? sig?.ToString() ?? "unknown"
                    : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                if (holodeckCoordinator.TryEngage(fingerprint, out var slot))
                {
                    using (slot!)
                    {
                        var holodeckPolicy = context.RequestServices.GetService<IActionPolicy>()?? 
                            context.RequestServices.GetServices<IActionPolicy>()
                                .FirstOrDefault(p => p.Name == "holodeck");

                        if (holodeckPolicy != null)
                        {
                            _logger.LogInformation(
                                "[HOLODECK] Engaging holodeck for {Path} (fingerprint={Fp}, honeypot={IsHoneypot}, attack={HasAttack})",
                                context.Request.Path, fingerprint[..Math.Min(8, fingerprint.Length)],
                                isHoneypotPath, hasAttackSignal);

                            await holodeckPolicy.ExecuteAsync(context, aggregated, context.RequestAborted);
                            return;
                        }
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "[HOLODECK] Coordinator rejected engagement for {Fp} (busy or capacity full, active={Active}/{Cap})",
                        fingerprint[..Math.Min(8, fingerprint.Length)],
                        holodeckCoordinator.ActiveEngagements, holodeckCoordinator.Capacity);
                }
            }
        }
        // --- End holodeck check ---

        _logger.LogWarning(
            "[BLOCK] Request blocked: {Path} policy={Policy} risk={Risk:F2} action={Action}",
            context.Request.Path, policy.Name, riskScore, action);
```

Add the needed using at the top of the file:

```csharp
using Mostlylucid.BotDetection.ApiHolodeck.Services;
using Mostlylucid.BotDetection.Actions;
```

**Note:** This creates a soft dependency on ApiHolodeck — `GetService<HolodeckCoordinator>()` returns null when ApiHolodeck isn't registered. The core middleware never directly references the type; it resolves via DI at runtime.

Wait — that creates a project reference dependency we don't want. The core `BotDetection` project should not reference `ApiHolodeck`. Instead, resolve the coordinator by a well-known `HttpContext.Items` key or by an interface in the core project.

Better approach: use `IActionPolicy` resolution by name, which already works:

```csharp
        // --- Holodeck engagement check ---
        var isHoneypotPath = context.Items.TryGetValue("Holodeck.IsHoneypotPath", out var hp) && hp is true;
        var hasAttackSignal = aggregated.Signals.ContainsKey(SignalKeys.AttackDetected);

        if (isHoneypotPath || hasAttackSignal)
        {
            var holodeckPolicy = actionPolicyRegistry.GetPolicy("holodeck");
            if (holodeckPolicy != null)
            {
                _logger.LogInformation(
                    "[HOLODECK] Engaging for {Path} (honeypot={IsHoneypot}, attack={HasAttack})",
                    context.Request.Path, isHoneypotPath, hasAttackSignal);

                var result = await holodeckPolicy.ExecuteAsync(context, aggregated, context.RequestAborted);
                if (!result.Continue) return;
            }
        }
        // --- End holodeck check ---
```

This keeps the core middleware decoupled — it resolves `"holodeck"` from the action policy registry (already injected). The `HolodeckActionPolicy` internally uses the coordinator for rate limiting.

- [ ] **Step 3: Add coordinator gate inside HolodeckActionPolicy.ExecuteAsync**

In `Mostlylucid.BotDetection.ApiHolodeck/Actions/HolodeckActionPolicy.cs`, inject `HolodeckCoordinator` via constructor and gate the execute:

Add to constructor params: `HolodeckCoordinator coordinator`

At the top of `ExecuteAsync`, add:

```csharp
        var fingerprint = GetContextKey(context, evidence);

        // Gate: one engagement per fingerprint, global capacity limit
        if (!_coordinator.TryEngage(fingerprint, out var slot))
        {
            _logger.LogDebug("[HOLODECK] Coordinator rejected {Fp} (busy or full)", fingerprint);
            // Fall through to normal block
            return ActionResult.Continue();
        }

        using (slot!)
        {
            // ... existing holodeck logic ...
        }
```

Wrap the entire existing body (from `var contextKey = ...` onwards) inside the `using (slot!)` block.

- [ ] **Step 4: Wire HoneypotPathTagger in Demo Program.cs**

In `Mostlylucid.BotDetection.Demo/Program.cs`, add BEFORE `app.UseStyloBot()`:

```csharp
app.UseMiddleware<Mostlylucid.BotDetection.ApiHolodeck.Middleware.HoneypotPathTagger>();
```

- [ ] **Step 5: Build and verify**

```bash
dotnet build mostlylucid.stylobot.sln --no-restore -v:minimal
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs \
       Mostlylucid.BotDetection.ApiHolodeck/Actions/HolodeckActionPolicy.cs \
       Mostlylucid.BotDetection.ApiHolodeck/Extensions/ServiceCollectionExtensions.cs \
       Mostlylucid.BotDetection.Demo/Program.cs \
       Mostlylucid.BotDetection.Demo/appsettings.json
git commit -m "Wire holodeck: path tagger middleware, coordinator gate, signal-based transitions"
```

---

## Task 8: Run full test suite and verify

**Files:** None (verification only)

- [ ] **Step 1: Run all tests**

```bash
dotnet test Mostlylucid.BotDetection.Test/ --no-restore -v:minimal
dotnet test Mostlylucid.BotDetection.Api.Tests/ --no-restore -v:minimal
dotnet test Mostlylucid.BotDetection.Demo.Tests/ --no-restore -v:minimal
dotnet test Stylobot.Gateway.Tests/ --no-restore -v:minimal
dotnet test Mostlylucid.BotDetection.Orchestration.Tests/ --no-restore -v:minimal --filter "FullyQualifiedName!~PostgreSQL"
```

Expected: All pass.

- [ ] **Step 2: Build commercial solution**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot-commercial && dotnet build Stylobot.Commercial.slnx --no-restore -v:minimal
```

Expected: 0 Warning(s), 0 Error(s).

- [ ] **Step 3: Start Demo and test holodeck live**

```bash
dotnet run --project Mostlylucid.BotDetection.Demo -c Release --urls "http://localhost:5080"
```

Test honeypot paths with bot UA — should now get holodeck responses instead of 403:

```bash
curl -s http://localhost:5080/wp-login.php -H "User-Agent: python-requests/2.31.0"
# Should return fake HTML login page, NOT {"error":"Access denied"...}

curl -s http://localhost:5080/.env -H "User-Agent: python-requests/2.31.0"
# Should return fake .env content
```

- [ ] **Step 4: Commit any fixes**

```bash
git add -A && git commit -m "Fix issues from holodeck integration testing"
```
