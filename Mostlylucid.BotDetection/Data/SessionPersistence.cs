using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Persisted session record. Sessions are the unit of storage - not individual requests.
///     Each session captures a compressed behavioral snapshot (Markov vector + fingerprint),
///     summary stats, and the dominant detection outcome.
///     This replaces per-request event storage (TimescaleDB) with session-level compression,
///     reducing storage by ~100x while preserving behavioral intelligence.
/// </summary>
public sealed record PersistedSession
{
    /// <summary>Auto-increment row ID</summary>
    public long Id { get; init; }

    /// <summary>Client signature (hashed IP:UA - zero PII)</summary>
    public required string Signature { get; init; }

    /// <summary>When this session started</summary>
    public required DateTime StartedAt { get; init; }

    /// <summary>When this session ended</summary>
    public required DateTime EndedAt { get; init; }

    /// <summary>Number of requests in this session</summary>
    public required int RequestCount { get; init; }

    /// <summary>Session vector as byte[] (float[] serialized for SQLite BLOB / PostgreSQL vector)</summary>
    public required byte[] Vector { get; init; }

    /// <summary>Vector maturity score (0-1)</summary>
    public required float Maturity { get; init; }

    /// <summary>Dominant Markov state (e.g., "PageView", "ApiCall")</summary>
    public required string DominantState { get; init; }

    /// <summary>Was the session classified as bot?</summary>
    public required bool IsBot { get; init; }

    /// <summary>Average bot probability across the session</summary>
    public required double AvgBotProbability { get; init; }

    /// <summary>Average confidence across the session</summary>
    public required double AvgConfidence { get; init; }

    /// <summary>Risk band at session end</summary>
    public required string RiskBand { get; init; }

    /// <summary>Action taken (block, throttle-stealth, Allow, etc.)</summary>
    public string? Action { get; init; }

    /// <summary>Bot name if identified</summary>
    public string? BotName { get; init; }

    /// <summary>Bot type if identified</summary>
    public string? BotType { get; init; }

    /// <summary>Country code (2-letter ISO)</summary>
    public string? CountryCode { get; init; }

    /// <summary>Top detection reasons (JSON array)</summary>
    public string? TopReasonsJson { get; init; }

    /// <summary>
    ///     Markov transition counts as JSON: {"PageView->ApiCall": 5, "ApiCall->PageView": 3, ...}
    ///     Enables drill-in visualization of request chains without storing individual requests.
    /// </summary>
    public string? TransitionCountsJson { get; init; }

    /// <summary>Distinct paths visited in this session (JSON array, templatized)</summary>
    public string? PathsJson { get; init; }

    /// <summary>Average processing time in ms</summary>
    public double AvgProcessingTimeMs { get; init; }

    /// <summary>Count of 4xx responses in this session</summary>
    public int ErrorCount { get; init; }

    /// <summary>Timing entropy (low = bot-like regularity)</summary>
    public float TimingEntropy { get; init; }

    /// <summary>Narrative summary</summary>
    public string? Narrative { get; init; }

    /// <summary>
    ///     HMAC hashes of discriminatory HTTP headers at session time.
    ///     JSON: {"accept-language": "hash1", "sec-ch-ua": "hash2", "_header_order": "hash3"}.
    ///     Used for retroactive stability analysis per entity.
    /// </summary>
    public string? HeaderHashesJson { get; init; }

    /// <summary>
    ///     PII-stripped raw User-Agent string from the session's first request.
    ///     Emails, credential URLs, and phone numbers are redacted before storage.
    /// </summary>
    public string? UserAgentRaw { get; init; }
}

/// <summary>
///     Persisted signature reputation. Accumulated across all sessions.
///     This is the long-lived identity - sessions come and go, signatures persist.
/// </summary>
public sealed record PersistedSignature
{
    /// <summary>Signature ID (hashed, zero PII)</summary>
    public required string SignatureId { get; init; }

    /// <summary>Total sessions observed</summary>
    public required int SessionCount { get; init; }

    /// <summary>Total requests across all sessions</summary>
    public required int TotalRequestCount { get; init; }

    /// <summary>First seen timestamp</summary>
    public required DateTime FirstSeen { get; init; }

    /// <summary>Last seen timestamp</summary>
    public required DateTime LastSeen { get; init; }

    /// <summary>Is this a known bot?</summary>
    public required bool IsBot { get; init; }

    /// <summary>Latest bot probability</summary>
    public required double BotProbability { get; init; }

    /// <summary>Latest confidence</summary>
    public required double Confidence { get; init; }

    /// <summary>Latest risk band</summary>
    public required string RiskBand { get; init; }

    /// <summary>Bot name if identified</summary>
    public string? BotName { get; init; }

    /// <summary>Bot type</summary>
    public string? BotType { get; init; }

    /// <summary>Latest action</summary>
    public string? Action { get; init; }

    /// <summary>Dominant country code</summary>
    public string? CountryCode { get; init; }

    /// <summary>
    ///     Compacted root vector: maturity-weighted average of all past session vectors.
    ///     Represents the signature's baseline behavioral profile.
    /// </summary>
    public byte[]? RootVector { get; init; }

    /// <summary>Root vector maturity</summary>
    public float RootVectorMaturity { get; init; }

    /// <summary>Narrative description</summary>
    public string? Narrative { get; init; }

    /// <summary>Top reasons (JSON)</summary>
    public string? TopReasonsJson { get; init; }
}

/// <summary>
///     Aggregated counter row for time-series charts.
///     Instead of storing every request, we maintain per-bucket counters.
///     Buckets are 1-minute granularity, rolled up to 1-hour and 1-day on query.
/// </summary>
public sealed record AggregatedBucket
{
    /// <summary>Bucket start time (truncated to minute)</summary>
    public required DateTime BucketTime { get; init; }

    /// <summary>Total requests in this bucket</summary>
    public required int TotalCount { get; init; }

    /// <summary>Bot requests in this bucket</summary>
    public required int BotCount { get; init; }

    /// <summary>Human requests in this bucket</summary>
    public required int HumanCount { get; init; }

    /// <summary>Unique signatures in this bucket</summary>
    public required int UniqueSignatures { get; init; }

    /// <summary>Sessions started in this bucket</summary>
    public required int SessionsStarted { get; init; }

    /// <summary>Average processing time in this bucket</summary>
    public required double AvgProcessingTimeMs { get; init; }
}

/// <summary>
///     Session-based event store interface. Sessions are the unit of storage.
///     Replaces per-request event storage with session-level compression.
///     Implementations:
///     - SQLite (core, zero-dependency, default)
///     - PostgreSQL + pgvector (commercial, for vector similarity at scale)
/// </summary>
public interface ISessionStore
{
    // === Write path ===

    /// <summary>Persist a completed session snapshot.</summary>
    Task AddSessionAsync(PersistedSession session, CancellationToken ct = default);

    /// <summary>Upsert a signature (create or update hit counts/stats).</summary>
    Task UpsertSignatureAsync(PersistedSignature signature, CancellationToken ct = default);

    /// <summary>Increment aggregated counters for a time bucket.</summary>
    Task IncrementBucketAsync(DateTime bucketTime, bool isBot, double processingTimeMs, CancellationToken ct = default);

    // === Read path: Sessions ===

    /// <summary>Get sessions for a signature, ordered by most recent.</summary>
    Task<List<PersistedSession>> GetSessionsAsync(string signature, int limit = 20, CancellationToken ct = default);

    /// <summary>Get recent sessions across all signatures.</summary>
    Task<List<PersistedSession>> GetRecentSessionsAsync(int limit = 50, bool? isBot = null, CancellationToken ct = default);

    // === Read path: Signatures ===

    /// <summary>Get a single signature by ID.</summary>
    Task<PersistedSignature?> GetSignatureAsync(string signatureId, CancellationToken ct = default);

    /// <summary>Get top signatures by session count.</summary>
    Task<List<PersistedSignature>> GetTopSignaturesAsync(int limit = 20, bool? isBot = null, CancellationToken ct = default);

    // === Read path: Aggregations ===

    /// <summary>Get summary stats (total sessions, bots, humans, unique signatures).</summary>
    Task<DashboardSessionSummary> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>Get time-series buckets for charts.</summary>
    Task<List<AggregatedBucket>> GetTimeSeriesAsync(DateTime start, DateTime end, CancellationToken ct = default);

    /// <summary>Get country-level stats from session data.</summary>
    Task<List<CountrySessionStats>> GetCountryStatsAsync(int limit = 20, CancellationToken ct = default);

    // === Read path: Vector search ===

    /// <summary>Find sessions similar to a query vector (cosine similarity).</summary>
    Task<List<(PersistedSession Session, float Similarity)>> FindSimilarSessionsAsync(
        float[] queryVector, int topK = 10, float minSimilarity = 0.7f, CancellationToken ct = default);

    // === Entity Resolution ===

    /// <summary>Resolve or create entity for a PrimarySignature. Returns the entity ID.</summary>
    Task<string> ResolveEntityAsync(string primarySignature, CancellationToken ct = default);

    /// <summary>Get the entity for a PrimarySignature (null if not yet resolved).</summary>
    Task<ResolvedEntity?> GetEntityForSignatureAsync(string primarySignature, CancellationToken ct = default);

    /// <summary>Get an entity by ID.</summary>
    Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default);

    /// <summary>Get all active edges for an entity.</summary>
    Task<List<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default);

    /// <summary>Create a merge edge linking a signature to an existing entity.</summary>
    Task MergeSignatureAsync(string entityId, string signature, double confidence, string reason, CancellationToken ct = default);

    /// <summary>Update entity metadata (confidence level, rotation cadence, stable anchors, etc.).</summary>
    Task UpdateEntityAsync(ResolvedEntity entity, CancellationToken ct = default);

    // === Maintenance ===

    /// <summary>Delete sessions older than retention period.</summary>
    Task PruneAsync(TimeSpan retention, CancellationToken ct = default);

    /// <summary>Initialize schema (create tables if needed).</summary>
    Task InitializeAsync(CancellationToken ct = default);
}

/// <summary>Summary stats for the session-based dashboard.</summary>
public sealed record DashboardSessionSummary
{
    public int TotalSessions { get; init; }
    public int BotSessions { get; init; }
    public int HumanSessions { get; init; }
    public int UniqueSignatures { get; init; }
    public int TotalRequests { get; init; }
    public double AvgProcessingTimeMs { get; init; }
    public DateTime? LastActivityAt { get; init; }
}

/// <summary>Country-level stats aggregated from sessions.</summary>
public sealed record CountrySessionStats
{
    public required string CountryCode { get; init; }
    public int TotalSessions { get; init; }
    public int BotSessions { get; init; }
    public int HumanSessions { get; init; }
    public int TotalRequests { get; init; }
    public double BotRate => TotalSessions > 0 ? (double)BotSessions / TotalSessions : 0;
}