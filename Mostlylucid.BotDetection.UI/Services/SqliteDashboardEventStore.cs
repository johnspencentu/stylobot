using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     SQLite-backed dashboard event store for the FOSS product.
///     Zero external dependencies - just a file on disk.
///     Commercial product overrides with PostgreSQL via TryAddSingleton.
/// </summary>
public sealed class SqliteDashboardEventStore : IDashboardEventStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _connectionString;
    private readonly ILogger<SqliteDashboardEventStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SqliteDashboardEventStore(
        ILogger<SqliteDashboardEventStore> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        var dbPath = Path.Combine(basePath, "dashboard.db");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
    }

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-4000;

            CREATE TABLE IF NOT EXISTS detections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                signature TEXT NOT NULL,
                method TEXT,
                path TEXT,
                is_bot INTEGER NOT NULL,
                bot_probability REAL NOT NULL,
                confidence REAL NOT NULL,
                risk_band TEXT,
                bot_name TEXT,
                bot_type TEXT,
                action TEXT,
                country_code TEXT,
                processing_time_ms REAL,
                threat_score REAL DEFAULT 0,
                threat_band TEXT,
                status_code INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS signatures (
                signature TEXT PRIMARY KEY,
                bot_name TEXT,
                bot_type TEXT,
                is_bot INTEGER NOT NULL DEFAULT 0,
                bot_probability REAL NOT NULL DEFAULT 0,
                confidence REAL NOT NULL DEFAULT 0,
                risk_band TEXT,
                action TEXT,
                country_code TEXT,
                hit_count INTEGER NOT NULL DEFAULT 1,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                processing_time_ms REAL DEFAULT 0,
                threat_score REAL DEFAULT 0,
                threat_band TEXT,
                narrative TEXT,
                metadata_json TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_det_timestamp ON detections(timestamp DESC);
            CREATE INDEX IF NOT EXISTS idx_det_signature ON detections(signature);
            CREATE INDEX IF NOT EXISTS idx_det_is_bot ON detections(is_bot);
            CREATE INDEX IF NOT EXISTS idx_det_country ON detections(country_code);
            CREATE INDEX IF NOT EXISTS idx_det_path ON detections(path);
            CREATE INDEX IF NOT EXISTS idx_sig_last_seen ON signatures(last_seen DESC);
            CREATE INDEX IF NOT EXISTS idx_sig_is_bot ON signatures(is_bot);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Prune old detections (keep last 7 days)
        await using var pruneCmd = conn.CreateCommand();
        pruneCmd.CommandText = "DELETE FROM detections WHERE timestamp < @cutoff";
        pruneCmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-7).ToString("O"));
        var pruned = await pruneCmd.ExecuteNonQueryAsync(ct);
        if (pruned > 0) _logger.LogDebug("Pruned {Count} old dashboard detections", pruned);

        _initialized = true;
        _logger.LogInformation("SQLite dashboard event store initialized at {Path}", _connectionString);
    }

    public async Task AddDetectionAsync(DashboardDetectionEvent detection)
    {
        await EnsureInitializedAsync();
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO detections (timestamp, signature, method, path, is_bot, bot_probability, confidence,
                    risk_band, bot_name, bot_type, action, country_code, processing_time_ms, threat_score, threat_band, status_code)
                VALUES (@ts, @sig, @method, @path, @isBot, @prob, @conf, @risk, @name, @type, @action, @country, @ms, @threat, @band, @status)
                """;
            cmd.Parameters.AddWithValue("@ts", detection.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@sig", detection.PrimarySignature ?? "");
            cmd.Parameters.AddWithValue("@method", detection.Method ?? "GET");
            cmd.Parameters.AddWithValue("@path", detection.Path ?? "/");
            cmd.Parameters.AddWithValue("@isBot", detection.IsBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@prob", detection.BotProbability);
            cmd.Parameters.AddWithValue("@conf", detection.Confidence);
            cmd.Parameters.AddWithValue("@risk", detection.RiskBand ?? "Unknown");
            cmd.Parameters.AddWithValue("@name", (object?)detection.BotName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", (object?)detection.BotType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@action", (object?)detection.Action ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@country", (object?)detection.CountryCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ms", detection.ProcessingTimeMs);
            cmd.Parameters.AddWithValue("@threat", detection.ThreatScore);
            cmd.Parameters.AddWithValue("@band", (object?)detection.ThreatBand ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@status", detection.StatusCode);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature)
    {
        await EnsureInitializedAsync();
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO signatures (signature, bot_name, bot_type, is_bot, bot_probability, confidence,
                    risk_band, action, country_code, hit_count, first_seen, last_seen, processing_time_ms, threat_score, threat_band, narrative)
                VALUES (@sig, @name, @type, @isBot, @prob, @conf, @risk, @action, @country, 1, @now, @now, @ms, @threat, @band, @narrative)
                ON CONFLICT(signature) DO UPDATE SET
                    bot_name = COALESCE(@name, bot_name),
                    bot_type = COALESCE(@type, bot_type),
                    is_bot = @isBot,
                    bot_probability = @prob,
                    confidence = @conf,
                    risk_band = @risk,
                    action = @action,
                    country_code = COALESCE(@country, country_code),
                    hit_count = hit_count + 1,
                    last_seen = @now,
                    processing_time_ms = @ms,
                    threat_score = @threat,
                    threat_band = @band,
                    narrative = COALESCE(@narrative, narrative)
                RETURNING hit_count
                """;
            cmd.Parameters.AddWithValue("@sig", signature.PrimarySignature ?? "");
            cmd.Parameters.AddWithValue("@name", (object?)signature.BotName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", (object?)signature.BotType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@isBot", signature.IsKnownBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@prob", signature.BotProbability ?? 0);
            cmd.Parameters.AddWithValue("@conf", signature.Confidence ?? 0);
            cmd.Parameters.AddWithValue("@risk", signature.RiskBand ?? "Unknown");
            cmd.Parameters.AddWithValue("@action", (object?)signature.Action ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@country", DBNull.Value); // Signatures don't carry country directly
            cmd.Parameters.AddWithValue("@ms", signature.ProcessingTimeMs ?? 0);
            cmd.Parameters.AddWithValue("@threat", signature.ThreatScore);
            cmd.Parameters.AddWithValue("@band", (object?)signature.ThreatBand ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@narrative", (object?)signature.Narrative ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

            var hitCount = await cmd.ExecuteScalarAsync();
            return signature with { HitCount = Convert.ToInt32(hitCount ?? 1) };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "SELECT * FROM detections";
        var conditions = new List<string>();
        var cmd = conn.CreateCommand();

        if (filter?.StartTime.HasValue == true)
        {
            conditions.Add("timestamp >= @start");
            cmd.Parameters.AddWithValue("@start", filter.StartTime.Value.ToString("O"));
        }
        if (filter?.EndTime.HasValue == true)
        {
            conditions.Add("timestamp <= @end");
            cmd.Parameters.AddWithValue("@end", filter.EndTime.Value.ToString("O"));
        }
        if (filter?.IsBot.HasValue == true)
        {
            conditions.Add("is_bot = @isBot");
            cmd.Parameters.AddWithValue("@isBot", filter.IsBot.Value ? 1 : 0);
        }

        if (conditions.Count > 0)
            sql += " WHERE " + string.Join(" AND ", conditions);

        sql += " ORDER BY timestamp DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", filter?.Limit ?? 100);
        cmd.CommandText = sql;

        var results = new List<DashboardDetectionEvent>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DashboardDetectionEvent
            {
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                PrimarySignature = reader.GetString(reader.GetOrdinal("signature")),
                RequestId = reader.GetString(reader.GetOrdinal("signature")),
                Method = reader.IsDBNull(reader.GetOrdinal("method")) ? null : reader.GetString(reader.GetOrdinal("method")),
                Path = reader.IsDBNull(reader.GetOrdinal("path")) ? null : reader.GetString(reader.GetOrdinal("path")),
                IsBot = reader.GetInt32(reader.GetOrdinal("is_bot")) == 1,
                BotProbability = reader.GetDouble(reader.GetOrdinal("bot_probability")),
                Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
                RiskBand = reader.IsDBNull(reader.GetOrdinal("risk_band")) ? null : reader.GetString(reader.GetOrdinal("risk_band")),
                BotName = reader.IsDBNull(reader.GetOrdinal("bot_name")) ? null : reader.GetString(reader.GetOrdinal("bot_name")),
                BotType = reader.IsDBNull(reader.GetOrdinal("bot_type")) ? null : reader.GetString(reader.GetOrdinal("bot_type")),
                Action = reader.IsDBNull(reader.GetOrdinal("action")) ? null : reader.GetString(reader.GetOrdinal("action")),
                CountryCode = reader.IsDBNull(reader.GetOrdinal("country_code")) ? null : reader.GetString(reader.GetOrdinal("country_code")),
                ProcessingTimeMs = reader.GetDouble(reader.GetOrdinal("processing_time_ms")),
                ThreatScore = reader.GetDouble(reader.GetOrdinal("threat_score")),
                ThreatBand = reader.IsDBNull(reader.GetOrdinal("threat_band")) ? null : reader.GetString(reader.GetOrdinal("threat_band")),
                StatusCode = reader.GetInt32(reader.GetOrdinal("status_code"))
            });
        }
        return results;
    }

    public async Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "SELECT * FROM signatures";
        if (isBot.HasValue) sql += " WHERE is_bot = " + (isBot.Value ? "1" : "0");
        sql += " ORDER BY last_seen DESC LIMIT @limit OFFSET @offset";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var results = new List<DashboardSignatureEvent>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadSignature(reader));
        }
        return results;
    }

    public async Task<DashboardSummary> GetSummaryAsync()
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) as bots,
                COUNT(DISTINCT signature) as signatures
            FROM detections
            WHERE timestamp >= @since
            """;
        cmd.Parameters.AddWithValue("@since", DateTime.UtcNow.AddHours(-6).ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var total = reader.GetInt32(0);
            var bots = reader.GetInt32(1);
            return new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = total,
                BotRequests = bots,
                HumanRequests = total - bots,
                UncertainRequests = 0,
                UniqueSignatures = reader.GetInt32(2),
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>()
            };
        }

        return new DashboardSummary
        {
            Timestamp = DateTime.UtcNow,
            TotalRequests = 0, BotRequests = 0, HumanRequests = 0, UncertainRequests = 0,
            UniqueSignatures = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new()
        };
    }

    public async Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize)
    {
        await EnsureInitializedAsync();
        var points = new List<DashboardTimeSeriesPoint>();
        var current = startTime;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        while (current < endTime)
        {
            var bucketEnd = current.Add(bucketSize);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) as bots,
                    SUM(CASE WHEN is_bot = 0 THEN 1 ELSE 0 END) as humans,
                    COUNT(*) as total
                FROM detections
                WHERE timestamp >= @start AND timestamp < @end
                """;
            cmd.Parameters.AddWithValue("@start", current.ToString("O"));
            cmd.Parameters.AddWithValue("@end", bucketEnd.ToString("O"));

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                points.Add(new DashboardTimeSeriesPoint
                {
                    Timestamp = current,
                    BotCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    HumanCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    TotalCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2)
                });
            }

            current = bucketEnd;
        }

        return points;
    }

    public async Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT signature, bot_name, bot_type, bot_probability, hit_count, last_seen,
                   threat_score, threat_band, action, narrative
            FROM signatures
            WHERE is_bot = 1
            ORDER BY hit_count DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);

        var results = new List<DashboardTopBotEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DashboardTopBotEntry
            {
                PrimarySignature = reader.GetString(0),
                BotName = reader.IsDBNull(1) ? null : reader.GetString(1),
                BotType = reader.IsDBNull(2) ? null : reader.GetString(2),
                BotProbability = reader.GetDouble(3),
                HitCount = reader.GetInt32(4),
                LastSeen = DateTime.Parse(reader.GetString(5)),
                ThreatScore = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                ThreatBand = reader.IsDBNull(7) ? null : reader.GetString(7),
                Action = reader.IsDBNull(8) ? null : reader.GetString(8),
                Narrative = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }
        return results;
    }

    public async Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT country_code,
                   COUNT(*) as total,
                   SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) as bots
            FROM detections
            WHERE country_code IS NOT NULL AND country_code != ''
            GROUP BY country_code
            ORDER BY total DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);

        var results = new List<DashboardCountryStats>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var total = reader.GetInt32(1);
            var bots = reader.GetInt32(2);
            results.Add(new DashboardCountryStats
            {
                CountryCode = reader.GetString(0),
                TotalCount = total,
                BotCount = bots,
                BotRate = total > 0 ? (double)bots / total : 0
            });
        }
        return results;
    }

    public async Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null)
    {
        await EnsureInitializedAsync();
        var stats = await GetCountryStatsAsync(1000);
        var country = stats.FirstOrDefault(c => c.CountryCode == countryCode);
        if (country == null) return null;

        return new DashboardCountryDetail
        {
            CountryCode = country.CountryCode,
            TotalCount = country.TotalCount,
            BotCount = country.BotCount,
            BotRate = country.BotRate,
            TopBotTypes = new Dictionary<string, int>(),
            TopBots = new List<DashboardTopBotEntry>()
        };
    }

    public async Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT method, path,
                   COUNT(*) as total,
                   SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) as bots,
                   COUNT(DISTINCT signature) as sigs,
                   AVG(processing_time_ms) as avg_ms,
                   AVG(threat_score) as avg_threat,
                   MAX(timestamp) as last_seen
            FROM detections
            GROUP BY method, path
            ORDER BY total DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);

        var results = new List<DashboardEndpointStats>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var total = reader.GetInt32(2);
            var bots = reader.GetInt32(3);
            results.Add(new DashboardEndpointStats
            {
                Method = reader.GetString(0),
                Path = reader.GetString(1),
                TotalCount = total,
                BotCount = bots,
                BotRate = total > 0 ? (double)bots / total : 0,
                UniqueSignatures = reader.GetInt32(4),
                AvgProcessingTimeMs = reader.GetDouble(5),
                AvgThreatScore = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                LastSeen = DateTime.Parse(reader.GetString(7))
            });
        }
        return results;
    }

    public async Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null)
    {
        var stats = await GetEndpointStatsAsync(1000);
        var endpoint = stats.FirstOrDefault(e => e.Method == method && e.Path == path);
        if (endpoint == null) return null;

        return new DashboardEndpointDetail
        {
            Method = method,
            Path = path,
            TotalCount = endpoint.TotalCount,
            BotCount = endpoint.BotCount,
            BotRate = endpoint.BotRate,
            UniqueSignatures = endpoint.UniqueSignatures,
            AvgProcessingTimeMs = endpoint.AvgProcessingTimeMs,
            AvgThreatScore = endpoint.AvgThreatScore,
            TopActions = new Dictionary<string, int>(),
            TopCountries = new Dictionary<string, int>(),
            RiskBands = new Dictionary<string, int>(),
            TopBots = new List<DashboardTopBotEntry>(),
            RecentDetections = new List<SignatureDetectionRow>()
        };
    }

    private static DashboardSignatureEvent ReadSignature(SqliteDataReader reader)
    {
        var sig = reader.GetString(reader.GetOrdinal("signature"));
        return new DashboardSignatureEvent
        {
            SignatureId = sig,
            PrimarySignature = sig,
            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_seen"))),
            RiskBand = reader.IsDBNull(reader.GetOrdinal("risk_band")) ? "Unknown" : reader.GetString(reader.GetOrdinal("risk_band")),
            BotName = reader.IsDBNull(reader.GetOrdinal("bot_name")) ? null : reader.GetString(reader.GetOrdinal("bot_name")),
            BotType = reader.IsDBNull(reader.GetOrdinal("bot_type")) ? null : reader.GetString(reader.GetOrdinal("bot_type")),
            IsKnownBot = reader.GetInt32(reader.GetOrdinal("is_bot")) == 1,
            BotProbability = reader.GetDouble(reader.GetOrdinal("bot_probability")),
            Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
            Action = reader.IsDBNull(reader.GetOrdinal("action")) ? null : reader.GetString(reader.GetOrdinal("action")),
            HitCount = reader.GetInt32(reader.GetOrdinal("hit_count")),
            ProcessingTimeMs = reader.GetDouble(reader.GetOrdinal("processing_time_ms")),
            ThreatScore = reader.IsDBNull(reader.GetOrdinal("threat_score")) ? 0 : reader.GetDouble(reader.GetOrdinal("threat_score")),
            ThreatBand = reader.IsDBNull(reader.GetOrdinal("threat_band")) ? null : reader.GetString(reader.GetOrdinal("threat_band")),
            Narrative = reader.IsDBNull(reader.GetOrdinal("narrative")) ? null : reader.GetString(reader.GetOrdinal("narrative"))
        };
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
