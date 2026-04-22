using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;
using Npgsql;

namespace Mostlylucid.BotDetection.UI.PostgreSQL.Storage;

/// <summary>
///     Provides cached 90-day historical reputation data from TimescaleDB.
///     Single efficient query per signature, cached for 5 minutes.
/// </summary>
public class TimescaleReputationProvider : ITimescaleReputationProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (TimescaleReputationData Data, DateTimeOffset ExpiresAt)> _cache = new();
    private readonly ILogger<TimescaleReputationProvider> _logger;
    private readonly PostgreSQLStorageOptions _options;

    public TimescaleReputationProvider(
        ILogger<TimescaleReputationProvider> logger,
        IOptions<PostgreSQLStorageOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<TimescaleReputationData?> GetReputationAsync(string primarySignature, CancellationToken ct = default)
    {
        // Check cache first
        if (_cache.TryGetValue(primarySignature, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Data;

        try
        {
            await using var conn = new NpgsqlConnection(_options.ConnectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                SELECT
                    COUNT(*)::bigint as "TotalCount",
                    COUNT(*) FILTER (WHERE is_bot)::bigint as "BotCount",
                    COALESCE(AVG(bot_probability), 0)::double precision as "AverageBotProbability",
                    MIN(timestamp) as "FirstSeen",
                    MAX(timestamp) as "LastSeen",
                    COUNT(DISTINCT DATE(timestamp))::bigint as "DaysActive",
                    COUNT(*) FILTER (WHERE timestamp > NOW() - INTERVAL '1 hour')::bigint as "RecentHourHitCount"
                FROM dashboard_detections
                WHERE primary_signature = @Signature
                    AND timestamp > NOW() - INTERVAL '90 days'
                    AND confidence > 0
                """;

            var row = await conn.QuerySingleOrDefaultAsync<TimescaleReputationRow>(
                sql,
                new { Signature = primarySignature },
                commandTimeout: 5);

            if (row == null || row.TotalCount == 0 || row.FirstSeen is null || row.LastSeen is null)
                return null;

            var data = new TimescaleReputationData
            {
                BotRatio = (double)row.BotCount / row.TotalCount,
                TotalHitCount = (int)row.TotalCount,
                DaysActive = (int)row.DaysActive,
                RecentHourHitCount = (int)row.RecentHourHitCount,
                AverageBotProbability = row.AverageBotProbability,
                FirstSeen = new DateTimeOffset(row.FirstSeen.Value, TimeSpan.Zero),
                LastSeen = new DateTimeOffset(row.LastSeen.Value, TimeSpan.Zero)
            };

            // Cache the result
            _cache[primarySignature] = (data, DateTimeOffset.UtcNow.Add(CacheTtl));

            return data;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch TimescaleDB reputation for {Signature}", primarySignature);
            return null;
        }
    }

    public void InvalidateCache(string primarySignature)
    {
        _cache.TryRemove(primarySignature, out _);
    }

    private sealed class TimescaleReputationRow
    {
        public long TotalCount { get; init; }
        public long BotCount { get; init; }
        public double AverageBotProbability { get; init; }
        public DateTime? FirstSeen { get; init; }
        public DateTime? LastSeen { get; init; }
        public long DaysActive { get; init; }
        public long RecentHourHitCount { get; init; }
    }
}
