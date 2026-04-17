using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Analysis;

/// <summary>
///     Request state for Markov chain transitions within a session.
///     Maps to observable request-level behavior, not content.
/// </summary>
public enum RequestState
{
    PageView = 0,
    ApiCall = 1,
    StaticAsset = 2,
    WebSocket = 3,
    SignalR = 4,
    ServerSentEvent = 5,
    FormSubmit = 6,
    AuthAttempt = 7,
    NotFound = 8,
    Search = 9
}

/// <summary>
///     A single observed request within a session, capturing state and timing.
/// </summary>
public readonly record struct SessionRequest(
    RequestState State,
    DateTimeOffset Timestamp,
    string PathTemplate,
    int StatusCode);

/// <summary>
///     A compressed behavioral snapshot of a session.
///     The session vector is the Markov transition matrix flattened + temporal features,
///     normalized for cosine similarity comparison.
/// </summary>
public sealed record SessionSnapshot
{
    /// <summary>Client signature (hashed IP:UA - zero PII)</summary>
    public required string Signature { get; init; }

    /// <summary>When this session started (first request)</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When this session ended (last request before boundary)</summary>
    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>Number of requests in this session</summary>
    public required int RequestCount { get; init; }

    /// <summary>
    ///     The compressed session vector. Dimensions:
    ///     [0..N²-1]  Flattened Markov transition probabilities (N = RequestState count)
    ///     [N²..N²+N] Stationary distribution (time spent in each state)
    ///     [N²+N..]   Temporal features (timing entropy, burst ratio, session duration, etc.)
    /// </summary>
    public required float[] Vector { get; init; }

    /// <summary>
    ///     Confidence in this snapshot: f(request_count, state_diversity).
    ///     Low request counts produce unreliable vectors - consumers should gate on this.
    /// </summary>
    public required float Maturity { get; init; }

    /// <summary>Dominant request state by frequency</summary>
    public required RequestState DominantState { get; init; }
}

/// <summary>
///     Network/transport fingerprint context for a session.
///     These are per-session constants (TLS, TCP, H2 don't change mid-session)
///     and become dimensions in the unified session vector.
///     This unifies the "fingerprint" and "behavioral vector" concepts:
///     fingerprint mutation across sessions becomes velocity in these dimensions.
/// </summary>
public sealed record FingerprintContext
{
    /// <summary>TLS version bucket: 0=unknown, 0.5=TLS1.2, 1.0=TLS1.3</summary>
    public float TlsVersion { get; init; }

    /// <summary>HTTP protocol bucket: 0=HTTP/1.1, 0.5=HTTP/2, 1.0=HTTP/3</summary>
    public float HttpProtocol { get; init; }

    /// <summary>H2/H3 client type match: 0=unknown, 0.5=bot-like, 1.0=browser-like</summary>
    public float ProtocolClientType { get; init; }

    /// <summary>TCP OS matches UA OS: 0=mismatch, 0.5=unknown, 1.0=match</summary>
    public float TcpOsConsistency { get; init; }

    /// <summary>QUIC 0-RTT used (returning visitor): 0=no, 1=yes</summary>
    public float QuicZeroRtt { get; init; }

    /// <summary>Client-side fingerprint integrity: 0=missing, 0.5=suspicious, 1.0=legitimate</summary>
    public float ClientFingerprintIntegrity { get; init; }

    /// <summary>Client-side headless indicator: 0=not headless, 1.0=headless</summary>
    public float HeadlessScore { get; init; }

    /// <summary>IP is datacenter: 0=residential, 1.0=datacenter</summary>
    public float IsDatacenter { get; init; }

    public static readonly FingerprintContext Empty = new();
}

/// <summary>
///     Builds Markov-chain-based session vectors from request sequences.
///     The vector captures HOW a client navigates (transition probabilities between states),
///     WHEN (temporal features), and WITH WHAT (fingerprint context), compressed into a
///     fixed-size float[] for similarity search.
///     Fingerprints are unified into the same vector space as behavioral features,
///     so fingerprint mutation across sessions appears as velocity in those dimensions.
/// </summary>
public static class SessionVectorizer
{
    private static readonly int StateCount = Enum.GetValues<RequestState>().Length;

    private const int TemporalFeatureCount = 8;
    private const int FingerprintFeatureCount = 8;
    private const int TransitionTimingFeatureCount = 3;

    /// <summary>
    ///     Total vector dimensions:
    ///     N² Markov transitions + N stationary distribution + temporal + fingerprint
    /// </summary>
    public static int Dimensions =>
        StateCount * StateCount + StateCount + TemporalFeatureCount + FingerprintFeatureCount + TransitionTimingFeatureCount;

    /// <summary>
    ///     Encodes a sequence of session requests into a normalized float vector.
    /// </summary>
    public static float[] Encode(
        IReadOnlyList<SessionRequest> requests,
        FingerprintContext? fingerprint = null)
    {
        var vector = new float[Dimensions];

        if (requests.Count < 2)
            return vector; // Not enough data for transitions

        // === 1. Build transition matrix ===
        var transitions = new int[StateCount, StateCount];
        var stateCounts = new int[StateCount];

        for (var i = 0; i < requests.Count; i++)
        {
            var stateIdx = (int)requests[i].State;
            stateCounts[stateIdx]++;

            if (i > 0)
            {
                var fromIdx = (int)requests[i - 1].State;
                transitions[fromIdx, stateIdx]++;
            }
        }

        // === 2. Flatten transition probabilities into vector [0..N²-1] ===
        for (var from = 0; from < StateCount; from++)
        {
            var rowTotal = 0;
            for (var to = 0; to < StateCount; to++)
                rowTotal += transitions[from, to];

            for (var to = 0; to < StateCount; to++)
            {
                var idx = from * StateCount + to;
                vector[idx] = rowTotal > 0 ? (float)transitions[from, to] / rowTotal : 0f;
            }
        }

        // === 3. Stationary distribution [N²..N²+N-1] ===
        var stationaryOffset = StateCount * StateCount;
        var total = (float)requests.Count;
        for (var s = 0; s < StateCount; s++)
            vector[stationaryOffset + s] = stateCounts[s] / total;

        // === 4. Temporal features [N²+N..] ===
        var temporalOffset = stationaryOffset + StateCount;
        EncodeTemporalFeatures(requests, vector, temporalOffset);

        // === 5. Fingerprint features [N²+N+temporal..] ===
        var fpOffset = temporalOffset + TemporalFeatureCount;
        EncodeFingerprintFeatures(fingerprint ?? FingerprintContext.Empty, vector, fpOffset);

        // === 6. Per-transition timing features [N²+N+temporal+fingerprint..] ===
        var ttOffset = fpOffset + FingerprintFeatureCount;
        EncodeTransitionTimingFeatures(requests, vector, ttOffset);

        // === 7. L2-normalize for cosine similarity ===
        Normalize(vector);

        return vector;
    }

    /// <summary>
    ///     Computes maturity score: confidence in this vector based on data density.
    /// </summary>
    public static float ComputeMaturity(IReadOnlyList<SessionRequest> requests)
    {
        if (requests.Count < 2) return 0f;

        // Factor 1: Request count (diminishing returns past 20)
        var countFactor = Math.Min(1f, requests.Count / 20f);

        // Factor 2: State diversity (more states observed = more reliable transitions)
        var uniqueStates = requests.Select(r => r.State).Distinct().Count();
        var diversityFactor = Math.Min(1f, uniqueStates / 5f);

        // Factor 3: Session duration (very short sessions are unreliable)
        var duration = (requests[^1].Timestamp - requests[0].Timestamp).TotalMinutes;
        var durationFactor = Math.Min(1f, (float)(duration / 2.0)); // 2+ minutes = full confidence

        return countFactor * 0.5f + diversityFactor * 0.3f + durationFactor * 0.2f;
    }

    /// <summary>
    ///     Computes cosine similarity between two session vectors.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        // Handle dimension mismatch from vector evolution (e.g., 126-dim vs 129-dim):
        // use the shorter length for dot product, zero-pad implicitly.
        var len = Math.Min(a.Length, b.Length);
        if (len == 0) return 0f;

        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        // Include remaining dimensions in norms (zero-padded other vector contributes nothing to dot)
        for (var i = len; i < a.Length; i++)
            normA += a[i] * a[i];
        for (var i = len; i < b.Length; i++)
            normB += b[i] * b[i];

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0 ? dot / denom : 0f;
    }

    /// <summary>
    ///     Computes the velocity vector (delta) between two session snapshots.
    ///     Positive values = dimension increased; negative = decreased.
    /// </summary>
    public static float[] ComputeVelocity(float[] current, float[] previous)
    {
        var velocity = new float[current.Length];
        for (var i = 0; i < current.Length; i++)
            velocity[i] = current[i] - previous[i];
        return velocity;
    }

    /// <summary>
    ///     Computes the L2 magnitude of a velocity vector.
    ///     High magnitude = large behavioral shift between sessions.
    /// </summary>
    public static float VelocityMagnitude(float[] velocity)
    {
        float sum = 0;
        for (var i = 0; i < velocity.Length; i++)
            sum += velocity[i] * velocity[i];
        return MathF.Sqrt(sum);
    }

    private static void EncodeTemporalFeatures(
        IReadOnlyList<SessionRequest> requests, float[] vector, int offset)
    {
        // Compute inter-request intervals
        var intervals = new List<double>(requests.Count - 1);
        for (var i = 1; i < requests.Count; i++)
            intervals.Add((requests[i].Timestamp - requests[i - 1].Timestamp).TotalMilliseconds);

        if (intervals.Count == 0) return;

        var mean = intervals.Average();
        var stdDev = intervals.Count > 1
            ? Math.Sqrt(intervals.Sum(x => Math.Pow(x - mean, 2)) / intervals.Count)
            : 0;

        // [0] Timing regularity: CV (coefficient of variation) - low = bot-like
        vector[offset + 0] = mean > 0 ? Math.Min(1f, (float)(stdDev / mean)) : 0f;

        // [1] Timing entropy (Shannon entropy of 100ms-bucketed intervals)
        vector[offset + 1] = Math.Min(1f, ComputeTimingEntropy(intervals) / 4f);

        // [2] Burst ratio: fraction of intervals < 100ms
        var burstCount = intervals.Count(i => i < 100);
        vector[offset + 2] = (float)burstCount / intervals.Count;

        // [3] Session duration (normalized: 0-30 minutes → 0-1)
        var durationMinutes = (requests[^1].Timestamp - requests[0].Timestamp).TotalMinutes;
        vector[offset + 3] = Math.Min(1f, (float)(durationMinutes / 30.0));

        // [4] Request rate (requests per minute, normalized)
        var rate = durationMinutes > 0 ? requests.Count / durationMinutes : requests.Count;
        vector[offset + 4] = Math.Min(1f, (float)(rate / 60.0)); // 60 rpm = 1.0

        // [5] 4xx error ratio
        var errorCount = requests.Count(r => r.StatusCode >= 400 && r.StatusCode < 500);
        vector[offset + 5] = (float)errorCount / requests.Count;

        // [6] Unique path ratio (diversity)
        var uniquePaths = requests.Select(r => r.PathTemplate).Distinct().Count();
        vector[offset + 6] = (float)uniquePaths / requests.Count;

        // [7] Mean interval (normalized: 0-10s → 0-1)
        vector[offset + 7] = Math.Min(1f, (float)(mean / 10000.0));
    }

    private static void EncodeFingerprintFeatures(
        FingerprintContext fp, float[] vector, int offset)
    {
        vector[offset + 0] = fp.TlsVersion;
        vector[offset + 1] = fp.HttpProtocol;
        vector[offset + 2] = fp.ProtocolClientType;
        vector[offset + 3] = fp.TcpOsConsistency;
        vector[offset + 4] = fp.QuicZeroRtt;
        vector[offset + 5] = fp.ClientFingerprintIntegrity;
        vector[offset + 6] = fp.HeadlessScore;
        vector[offset + 7] = fp.IsDatacenter;
    }

    private static float ComputeTimingEntropy(List<double> intervals)
    {
        // Bucket into 100ms bins
        var buckets = new Dictionary<int, int>();
        foreach (var interval in intervals)
        {
            var bucket = (int)(interval / 100);
            buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
        }

        var total = (double)intervals.Count;
        double entropy = 0;
        foreach (var count in buckets.Values)
        {
            var p = count / total;
            if (p > 0) entropy -= p * Math.Log2(p);
        }

        return (float)entropy;
    }

    // Per-transition impossible timing thresholds (milliseconds).
    // If a transition happens faster than this, a human couldn't have caused it
    // (page hasn't rendered, API response hasn't arrived, etc.)
    private static readonly Dictionary<(int from, int to), double> ImpossibleTimingThresholds = new()
    {
        // PageView -> anything: page needs to render first
        { ((int)RequestState.PageView, (int)RequestState.PageView), 200 },
        { ((int)RequestState.PageView, (int)RequestState.ApiCall), 50 },
        { ((int)RequestState.PageView, (int)RequestState.FormSubmit), 300 },
        { ((int)RequestState.PageView, (int)RequestState.Search), 200 },
        // ApiCall -> PageView: needs navigation decision
        { ((int)RequestState.ApiCall, (int)RequestState.PageView), 100 },
        // FormSubmit -> anything: form processing visible to user
        { ((int)RequestState.FormSubmit, (int)RequestState.PageView), 150 },
    };

    private const double DefaultImpossibleThresholdMs = 30;
    private const double FastestTransitionBaselineMs = 100;

    private static void EncodeTransitionTimingFeatures(
        IReadOnlyList<SessionRequest> requests, float[] vector, int offset)
    {
        if (requests.Count < 3) return; // Need enough transitions for meaningful stats

        // Build per-transition timing map
        var transitionTimings = new Dictionary<(int from, int to), List<double>>();
        var allIntervals = new List<double>();

        for (var i = 1; i < requests.Count; i++)
        {
            var fromIdx = (int)requests[i - 1].State;
            var toIdx = (int)requests[i].State;
            var intervalMs = (requests[i].Timestamp - requests[i - 1].Timestamp).TotalMilliseconds;

            var key = (fromIdx, toIdx);
            if (!transitionTimings.TryGetValue(key, out var timings))
            {
                timings = new List<double>();
                transitionTimings[key] = timings;
            }
            timings.Add(intervalMs);
            allIntervals.Add(intervalMs);
        }

        if (allIntervals.Count == 0) return;

        // [0] Impossible timing ratio: fraction of transitions faster than physical threshold
        var impossibleCount = 0;
        var totalTransitions = 0;
        foreach (var (key, timings) in transitionTimings)
        {
            var threshold = ImpossibleTimingThresholds.GetValueOrDefault(key, DefaultImpossibleThresholdMs);
            foreach (var t in timings)
            {
                totalTransitions++;
                if (t < threshold) impossibleCount++;
            }
        }

        vector[offset + 0] = totalTransitions > 0
            ? Math.Clamp((float)impossibleCount / totalTransitions, 0f, 1f)
            : 0f;

        // [1] Timing consistency score: weighted avg CV across transition types (inverted: low CV = high score = bot-like)
        var totalWeight = 0.0;
        var weightedCvSum = 0.0;
        foreach (var (_, timings) in transitionTimings)
        {
            if (timings.Count < 2) continue;
            var mean = timings.Average();
            if (mean <= 0) continue;
            var stdDev = Math.Sqrt(timings.Select(t => Math.Pow(t - mean, 2)).Average());
            var cv = stdDev / mean;
            weightedCvSum += cv * timings.Count;
            totalWeight += timings.Count;
        }

        var avgCv = totalWeight > 0 ? weightedCvSum / totalWeight : 1.0;
        vector[offset + 1] = Math.Clamp(1f - (float)Math.Min(1.0, avgCv), 0f, 1f);

        // [2] Fastest transition z-score: how extreme is the minimum interval vs human baseline
        var minInterval = allIntervals.Min();
        vector[offset + 2] = Math.Clamp((float)Math.Max(0, 1.0 - minInterval / FastestTransitionBaselineMs), 0f, 1f);
    }

    private static void Normalize(float[] vector)
    {
        float norm = 0;
        for (var i = 0; i < vector.Length; i++)
            norm += vector[i] * vector[i];

        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        }
    }
}

/// <summary>
///     Manages session state per client: retrogressive boundary detection, history, and snapshots.
///     A session boundary is detected when:
///     - Inter-request gap exceeds threshold (default 30 minutes)
///     - User-Agent changes (new client context)
///     The boundary is retrogressive: we only know the previous session ended
///     when the NEXT request arrives and reveals the gap.
/// </summary>
public sealed class SessionStore
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<SessionStore> _logger;
    private readonly TimeSpan _sessionGapThreshold;
    private readonly int _maxSnapshotsPerSignature;
    private readonly int _maxRequestsPerSession;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>
    ///     Fired when a session is finalized (boundary detected).
    ///     Used by persistence layer to write completed sessions to SQLite.
    /// </summary>
    public event Action<SessionSnapshot, IReadOnlyList<SessionRequest>>? SessionFinalized;

    public SessionStore(
        IMemoryCache cache,
        ILogger<SessionStore> logger,
        TimeSpan? sessionGapThreshold = null,
        int maxSnapshotsPerSignature = 10,
        int maxRequestsPerSession = 200)
    {
        _cache = cache;
        _logger = logger;
        _sessionGapThreshold = sessionGapThreshold ?? TimeSpan.FromMinutes(30);
        _maxSnapshotsPerSignature = maxSnapshotsPerSignature;
        _maxRequestsPerSession = maxRequestsPerSession;
    }

    /// <summary>
    ///     Records a request and returns a session boundary event if the previous session just ended.
    ///     This is the retrogressive detection: the arrival of THIS request tells us the
    ///     PREVIOUS session has ended (because the gap exceeded the threshold).
    /// </summary>
    public SessionSnapshot? RecordRequest(
        string signature,
        SessionRequest request,
        FingerprintContext? fingerprint = null)
    {
        var sessionLock = _locks.GetOrAdd(signature, _ => new SemaphoreSlim(1, 1));
        sessionLock.Wait();

        try
        {
            var sessionKey = $"session:current:{signature}";
            var currentSession = _cache.Get<List<SessionRequest>>(sessionKey);

            SessionSnapshot? completedSnapshot = null;

            if (currentSession != null && currentSession.Count > 0)
            {
                var lastRequest = currentSession[^1];
                var gap = request.Timestamp - lastRequest.Timestamp;

                // Retrogressive boundary: the gap tells us the PREVIOUS session is over
                if (gap >= _sessionGapThreshold)
                {
                    // Finalize the previous session into a snapshot
                    completedSnapshot = FinalizeSession(signature, currentSession, fingerprint);

                    // Start fresh
                    currentSession = new List<SessionRequest> { request };
                }
                else if (currentSession.Count < _maxRequestsPerSession)
                {
                    currentSession.Add(request);
                }
            }
            else
            {
                // First request - start new session
                currentSession = new List<SessionRequest> { request };
            }

            // Store with sliding expiration matching the gap threshold
            _cache.Set(sessionKey, currentSession, new MemoryCacheEntryOptions
            {
                SlidingExpiration = _sessionGapThreshold + TimeSpan.FromMinutes(5)
            });

            return completedSnapshot;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    ///     Gets the current (in-progress) session for a signature, if any.
    /// </summary>
    public IReadOnlyList<SessionRequest>? GetCurrentSession(string signature)
    {
        return _cache.Get<List<SessionRequest>>($"session:current:{signature}");
    }

    /// <summary>
    ///     Gets completed session snapshots for inter-session analysis.
    /// </summary>
    public IReadOnlyList<SessionSnapshot> GetHistory(string signature)
    {
        return _cache.Get<List<SessionSnapshot>>($"session:history:{signature}")
               ?? (IReadOnlyList<SessionSnapshot>)Array.Empty<SessionSnapshot>();
    }

    private SessionSnapshot? FinalizeSession(
        string signature, List<SessionRequest> requests, FingerprintContext? fingerprint = null)
    {
        if (requests.Count < 3) return null; // Too few requests for meaningful vector

        var vector = SessionVectorizer.Encode(requests, fingerprint);
        var maturity = SessionVectorizer.ComputeMaturity(requests);

        // Find dominant state
        var dominantState = requests
            .GroupBy(r => r.State)
            .OrderByDescending(g => g.Count())
            .First().Key;

        var snapshot = new SessionSnapshot
        {
            Signature = signature,
            StartedAt = requests[0].Timestamp,
            EndedAt = requests[^1].Timestamp,
            RequestCount = requests.Count,
            Vector = vector,
            Maturity = maturity,
            DominantState = dominantState
        };

        // Append to history (ring buffer)
        var historyKey = $"session:history:{signature}";
        var history = _cache.Get<List<SessionSnapshot>>(historyKey) ?? new List<SessionSnapshot>();
        history.Add(snapshot);

        // Compact old snapshots into a root when history grows too large.
        // The root is a maturity-weighted average of old vectors - lossy compression
        // that preserves the client's baseline behavioral profile while discarding
        // per-session detail beyond the useful threshold.
        if (history.Count > _maxSnapshotsPerSignature)
            CompactHistory(history);

        _cache.Set(historyKey, history, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromHours(2)
        });

        _logger.LogDebug(
            "Session finalized for {Signature}: {Count} requests, maturity={Maturity:F2}, dominant={State}",
            signature, requests.Count, maturity, dominantState);

        // Notify persistence layer
        try { SessionFinalized?.Invoke(snapshot, requests); }
        catch (Exception ex) { _logger.LogWarning(ex, "SessionFinalized event handler failed"); }

        return snapshot;
    }

    /// <summary>
    ///     Compacts old snapshots into a single root snapshot via maturity-weighted averaging.
    ///     Keeps the most recent (maxSnapshots/2) snapshots intact for velocity analysis,
    ///     and merges all older ones into a root that preserves the baseline behavioral profile.
    ///     This is lossy compression: individual session detail is lost but the aggregate
    ///     behavioral signature is preserved for similarity comparison.
    /// </summary>
    private void CompactHistory(List<SessionSnapshot> history)
    {
        var keepCount = _maxSnapshotsPerSignature / 2;
        var compactCount = history.Count - keepCount;

        if (compactCount < 2) return; // Not enough to compact

        var toCompact = history.Take(compactCount).ToList();
        var toKeep = history.Skip(compactCount).ToList();

        // Weighted average of vectors by maturity
        var dims = SessionVectorizer.Dimensions;
        var rootVector = new float[dims];
        var totalWeight = 0f;
        var totalRequestCount = 0;

        foreach (var s in toCompact)
        {
            var weight = Math.Max(0.01f, s.Maturity);
            totalWeight += weight;
            totalRequestCount += s.RequestCount;

            for (var i = 0; i < dims; i++)
                rootVector[i] += s.Vector[i] * weight;
        }

        if (totalWeight > 0)
        {
            for (var i = 0; i < dims; i++)
                rootVector[i] /= totalWeight;
        }

        // Re-normalize after averaging
        float norm = 0;
        for (var i = 0; i < dims; i++)
            norm += rootVector[i] * rootVector[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (var i = 0; i < dims; i++)
                rootVector[i] /= norm;
        }

        // Find dominant state across compacted sessions
        var dominantState = toCompact
            .GroupBy(s => s.DominantState)
            .OrderByDescending(g => g.Sum(s => s.RequestCount))
            .First().Key;

        var rootSnapshot = new SessionSnapshot
        {
            Signature = toCompact[0].Signature,
            StartedAt = toCompact[0].StartedAt,
            EndedAt = toCompact[^1].EndedAt,
            RequestCount = totalRequestCount,
            Vector = rootVector,
            Maturity = Math.Min(1f, totalWeight / compactCount), // Average maturity
            DominantState = dominantState
        };

        // Replace history: root + recent snapshots
        history.Clear();
        history.Add(rootSnapshot);
        history.AddRange(toKeep);

        _logger.LogDebug(
            "Compacted {CompactCount} snapshots into root ({TotalRequests} total requests), keeping {KeepCount} recent",
            compactCount, totalRequestCount, keepCount);
    }
}