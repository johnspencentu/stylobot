using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Session-level behavioral vector contributor.
///     Compresses per-request Markov chain transitions into a fixed-dimension vector per session,
///     enabling inter-session anomaly detection via cosine similarity and velocity analysis.
///
///     Key capabilities:
///     - Retrogressive session boundary detection (gap-based, not time-windowed)
///     - Markov transition matrix → normalized vector compression
///     - Inter-session velocity: detects sudden behavioral shifts
///     - Human baseline comparison: sessions that don't look like normal browsing
///
///     Runs in Wave 1+ after transport classification and behavioral signals are available.
///     Configuration loaded from: sessionvector.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:SessionVectorContributor:*
/// </summary>
public class SessionVectorContributor : ConfiguredContributorBase
{
    private readonly ILogger<SessionVectorContributor> _logger;
    private readonly SessionStore _sessionStore;

    public SessionVectorContributor(
        ILogger<SessionVectorContributor> logger,
        IDetectorConfigProvider configProvider,
        SessionStore sessionStore)
        : base(configProvider)
    {
        _logger = logger;
        _sessionStore = sessionStore;
    }

    public override string Name => "SessionVector";
    public override int Priority => Manifest?.Priority ?? 30;

    // Requires a signature (unified PrimarySignature or legacy WaveformSignature)
    public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
    {
        new AnyOfTrigger(new TriggerCondition[]
        {
            new SignalExistsTrigger(SignalKeys.PrimarySignature),
            new SignalExistsTrigger(SignalKeys.WaveformSignature)
        })
    };

    // Config-driven thresholds
    private int MinSessionRequests => GetParam("min_session_requests", 5);
    private float MinMaturityForScoring => (float)GetParam("min_maturity_for_scoring", 0.3);
    private float VelocityAnomalyThreshold => (float)GetParam("velocity_anomaly_threshold", 0.6);
    private float DissimilarityBotThreshold => (float)GetParam("dissimilarity_bot_threshold", 0.3);
    private double HighVelocityConfidence => GetParam("high_velocity_confidence", 0.5);
    private double DissimilarSessionConfidence => GetParam("dissimilar_session_confidence", 0.4);
    private double ConsistentSessionHumanConfidence => GetParam("consistent_session_human_confidence", -0.2);
    private double StableVelocityHumanConfidence => GetParam("stable_velocity_human_confidence", -0.15);

    // Partial chain early detection thresholds
    private int PartialChainMinRequests => GetParam("partial_chain_min_requests", 3);
    private float PartialChainSimilarityThreshold => (float)GetParam("partial_chain_similarity_threshold", 0.6);
    private float PartialChainMaxConfidence => (float)GetParam("partial_chain_max_confidence", 0.35);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var signature = state.GetSignal<string>(SignalKeys.PrimarySignature)
                ?? state.GetSignal<string>(SignalKeys.WaveformSignature); // fallback
            if (string.IsNullOrEmpty(signature))
            {
                contributions.Add(NeutralContribution("No waveform signature available"));
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
            }

            // Classify the current request into a Markov state
            var requestState = ClassifyRequestState(state);
            var statusCode = state.HttpContext.Response.StatusCode;
            var path = TemplatizePath(state.HttpContext.Request.Path.Value ?? "/");

            var sessionRequest = new SessionRequest(
                requestState,
                DateTimeOffset.UtcNow,
                path,
                statusCode > 0 ? statusCode : 200);

            // Build fingerprint context from blackboard signals - these are per-session
            // constants that become dimensions in the unified vector. Fingerprint mutation
            // across sessions appears as velocity in these dimensions.
            var fpContext = BuildFingerprintContext(state);

            // Record request - may return a completed session snapshot (retrogressive boundary)
            var completedSession = _sessionStore.RecordRequest(signature, sessionRequest, fpContext);

            // Write session signals
            var currentSession = _sessionStore.GetCurrentSession(signature);
            var sessionHistory = _sessionStore.GetHistory(signature);

            state.WriteSignals([
                new(SignalKeys.SessionRequestCount, currentSession?.Count ?? 1),
                new(SignalKeys.SessionHistoryCount, sessionHistory.Count),
                new(SignalKeys.SessionCurrentState, requestState.ToString())
            ]);

            if (completedSession != null)
            {
                state.WriteSignals([
                    new(SignalKeys.SessionBoundaryDetected, true),
                    new(SignalKeys.SessionCompletedMaturity, completedSession.Maturity),
                    new(SignalKeys.SessionCompletedRequestCount, completedSession.RequestCount),
                    new(SignalKeys.SessionDominantState, completedSession.DominantState.ToString())
                ]);

                _logger.LogDebug(
                    "Session boundary detected for {Signature}: {Count} requests, maturity={Maturity:F2}",
                    signature, completedSession.RequestCount, completedSession.Maturity);
            }

            // === Partial chain early detection (before full maturity) ===
            if (currentSession != null &&
                currentSession.Count >= PartialChainMinRequests &&
                currentSession.Count < MinSessionRequests)
            {
                AnalyzePartialChain(state, currentSession, fpContext, contributions);
            }

            // === Analyze current session (in-progress) ===
            if (currentSession != null && currentSession.Count >= MinSessionRequests)
            {
                var currentVector = SessionVectorizer.Encode(currentSession, fpContext);
                var currentMaturity = SessionVectorizer.ComputeMaturity(currentSession);

                state.WriteSignal(SignalKeys.SessionVectorMaturity, currentMaturity);

                if (currentMaturity >= MinMaturityForScoring)
                    AnalyzeCurrentSession(state, currentVector, contributions);
            }

            // === Analyze inter-session velocity (if we have history) ===
            if (sessionHistory.Count >= 2)
                AnalyzeInterSessionVelocity(state, sessionHistory, contributions);

            // Default: neutral if no analysis triggered
            if (contributions.Count == 0)
                contributions.Add(NeutralContribution(
                    $"Session tracking active ({currentSession?.Count ?? 0} requests, {sessionHistory.Count} prior sessions)"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in session vector analysis");
            contributions.Add(NeutralContribution("Session vector analysis error"));
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    /// <summary>
    ///     Analyzes the current in-progress session against historical baselines.
    /// </summary>
    private void AnalyzeCurrentSession(
        BlackboardState state,
        float[] currentVector,
        List<DetectionContribution> contributions)
    {
        var history = _sessionStore.GetHistory(
            state.GetSignal<string>(SignalKeys.PrimarySignature)
            ?? state.GetSignal<string>(SignalKeys.WaveformSignature) ?? "");

        if (history.Count == 0) return;

        // Compare current session against prior sessions from this same client
        var similarities = history
            .Where(h => h.Maturity >= MinMaturityForScoring)
            .Select(h => SessionVectorizer.CosineSimilarity(currentVector, h.Vector))
            .ToList();

        if (similarities.Count == 0) return;

        var avgSimilarity = similarities.Average();
        state.WriteSignal(SignalKeys.SessionSelfSimilarity, avgSimilarity);

        // Very dissimilar from own history = possible session hijack or bot rotation
        if (avgSimilarity < DissimilarityBotThreshold)
        {
            contributions.Add(BotContribution(
                DissimilarSessionConfidence,
                $"Current session behavior diverges from client history (similarity={avgSimilarity:F2})",
                BotType.Scraper));
        }
        // Consistent with own history = human-like continuity
        else if (avgSimilarity > 0.7f)
        {
            contributions.Add(HumanContribution(
                Math.Abs(ConsistentSessionHumanConfidence),
                $"Session behavior consistent with client history (similarity={avgSimilarity:F2})"));
        }
    }

    /// <summary>
    ///     Detects anomalous velocity between recent sessions.
    ///     Human sessions drift gradually; bots show binary state switches.
    /// </summary>
    private void AnalyzeInterSessionVelocity(
        BlackboardState state,
        IReadOnlyList<SessionSnapshot> history,
        List<DetectionContribution> contributions)
    {
        // Compare the two most recent completed sessions
        var recent = history
            .Where(h => h.Maturity >= MinMaturityForScoring)
            .OrderByDescending(h => h.EndedAt)
            .Take(2)
            .ToList();

        if (recent.Count < 2) return;

        var velocity = SessionVectorizer.ComputeVelocity(recent[0].Vector, recent[1].Vector);
        var magnitude = SessionVectorizer.VelocityMagnitude(velocity);

        state.WriteSignals([
            new(SignalKeys.SessionVelocityMagnitude, magnitude),
            new(SignalKeys.SessionVelocityVector, velocity)
        ]);

        // High velocity = sudden behavioral shift
        if (magnitude > VelocityAnomalyThreshold)
        {
            contributions.Add(BotContribution(
                HighVelocityConfidence,
                $"Sudden behavioral shift between sessions (velocity={magnitude:F2})",
                BotType.Scraper));
        }
        // Low velocity across multiple sessions = stable human behavior
        else if (magnitude < 0.15f && history.Count >= 3)
        {
            contributions.Add(HumanContribution(
                Math.Abs(StableVelocityHumanConfidence),
                $"Stable behavior across {history.Count} sessions (velocity={magnitude:F2})"));
        }
    }

    /// <summary>
    ///     Analyzes a partial Markov chain (3-5 requests) against known behavioral archetypes
    ///     for early bot/human signal scoring before the full session vector matures.
    /// </summary>
    private void AnalyzePartialChain(
        BlackboardState state,
        IReadOnlyList<SessionRequest> currentSession,
        FingerprintContext fpContext,
        List<DetectionContribution> contributions)
    {
        // Encode only the transition matrix (first 100 dims) from the partial session
        var fullVector = SessionVectorizer.Encode(currentSession, fpContext);
        var stateCount = Enum.GetValues<RequestState>().Length;
        var transitionDims = stateCount * stateCount;
        var partialVector = new float[transitionDims];
        Array.Copy(fullVector, partialVector, Math.Min(fullVector.Length, transitionDims));

        // L2-normalize the partial vector
        float norm = 0;
        for (var i = 0; i < partialVector.Length; i++)
            norm += partialVector[i] * partialVector[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (var i = 0; i < partialVector.Length; i++)
                partialVector[i] /= norm;
        }
        else
        {
            // Zero vector — no transitions to analyze
            return;
        }

        // Compare against each archetype
        var bestSimilarity = 0f;
        MarkovArchetype? bestMatch = null;

        foreach (var archetype in MarkovArchetypes.All)
        {
            var similarity = CosineSimilarity100(partialVector, archetype.PartialVector);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestMatch = archetype;
            }
        }

        var threshold = PartialChainSimilarityThreshold;
        if (bestMatch == null || bestSimilarity < threshold)
            return;

        // Scale confidence by similarity, clamped to max
        var scaledConfidence = Math.Min(
            PartialChainMaxConfidence,
            Math.Abs(bestMatch.DefaultConfidence) * bestSimilarity);

        // Write signals
        state.WriteSignals([
            new(SignalKeys.SessionPartialChainMatch, bestMatch.Name),
            new(SignalKeys.SessionPartialChainSimilarity, bestSimilarity),
            new(SignalKeys.SessionPartialChainConfidence, scaledConfidence)
        ]);

        if (bestMatch.IsHuman)
        {
            contributions.Add(HumanContribution(
                scaledConfidence,
                $"Partial chain ({currentSession.Count} requests) matches '{bestMatch.Name}' archetype (similarity={bestSimilarity:F2})"));
        }
        else
        {
            contributions.Add(BotContribution(
                scaledConfidence,
                $"Partial chain ({currentSession.Count} requests) matches '{bestMatch.Name}' archetype (similarity={bestSimilarity:F2})",
                bestMatch.DefaultBotType ?? BotType.Scraper));
        }

        _logger.LogDebug(
            "Partial chain match: {Archetype} (similarity={Similarity:F2}, confidence={Confidence:F2}, requests={Count})",
            bestMatch.Name, bestSimilarity, scaledConfidence, currentSession.Count);
    }

    /// <summary>
    ///     Cosine similarity for two float[100] vectors (already L2-normalized).
    /// </summary>
    private static float CosineSimilarity100(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 0 ? dot / denom : 0f;
    }

    /// <summary>
    ///     Builds a FingerprintContext from blackboard signals.
    ///     These are per-session constants that become vector dimensions,
    ///     unifying network fingerprints with behavioral vectors.
    /// </summary>
    private static FingerprintContext BuildFingerprintContext(BlackboardState state)
    {
        // TLS version
        var tlsProtocol = state.GetSignal<string>(SignalKeys.TlsProtocol) ?? "";
        var tlsVersion = tlsProtocol switch
        {
            _ when tlsProtocol.Contains("1.3") => 1.0f,
            _ when tlsProtocol.Contains("1.2") => 0.5f,
            _ when tlsProtocol.Length > 0 => 0.2f, // Old TLS
            _ => 0f // Unknown
        };

        // HTTP protocol version
        var h2 = state.GetSignal<string>(SignalKeys.H2Protocol);
        var h3 = state.GetSignal<string>(SignalKeys.H3Protocol);
        var httpProtocol = h3 != null ? 1.0f : h2 != null ? 0.5f : 0f;

        // H2/H3 client type (browser vs tool)
        var clientType = state.GetSignal<string>(SignalKeys.H2ClientType)
                         ?? state.GetSignal<string>(SignalKeys.H3ClientType) ?? "";
        var protocolClientType = clientType.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                                 clientType.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ||
                                 clientType.Contains("Safari", StringComparison.OrdinalIgnoreCase)
            ? 1.0f
            : clientType.Contains("Bot", StringComparison.OrdinalIgnoreCase) ? 0.2f
            : clientType.Length > 0 ? 0.5f : 0f;

        // TCP OS vs UA OS consistency
        var tcpOs = state.GetSignal<string>(SignalKeys.TcpOsHint)
                    ?? state.GetSignal<string>(SignalKeys.TcpOsHintTtl) ?? "";
        var uaOs = state.GetSignal<string>(SignalKeys.UserAgentOs) ?? "";
        float tcpOsConsistency;
        if (tcpOs.Length == 0 || uaOs.Length == 0)
            tcpOsConsistency = 0.5f; // Unknown
        else
            tcpOsConsistency = tcpOs.Contains(uaOs, StringComparison.OrdinalIgnoreCase) ||
                               uaOs.Contains(tcpOs, StringComparison.OrdinalIgnoreCase)
                ? 1.0f
                : 0f;

        // QUIC 0-RTT (returning visitor)
        var zeroRtt = state.GetSignal<bool?>(SignalKeys.H3ZeroRtt) ?? false;

        // Client-side fingerprint
        var fpIntegrity = state.GetSignal<double?>(SignalKeys.FingerprintIntegrityScore);
        var clientFpIntegrity = fpIntegrity.HasValue ? Math.Min(1f, (float)fpIntegrity.Value) : 0f;

        var headless = state.GetSignal<double?>(SignalKeys.FingerprintHeadlessScore);
        var headlessScore = headless.HasValue ? Math.Min(1f, (float)headless.Value) : 0f;

        // Datacenter IP
        var isDatacenter = state.GetSignal<bool?>(SignalKeys.IpIsDatacenter) ?? false;

        return new FingerprintContext
        {
            TlsVersion = tlsVersion,
            HttpProtocol = httpProtocol,
            ProtocolClientType = protocolClientType,
            TcpOsConsistency = tcpOsConsistency,
            QuicZeroRtt = zeroRtt ? 1f : 0f,
            ClientFingerprintIntegrity = clientFpIntegrity,
            HeadlessScore = headlessScore,
            IsDatacenter = isDatacenter ? 1f : 0f
        };
    }

    /// <summary>
    ///     Maps the current request into a Markov state based on transport, path, and response signals.
    /// </summary>
    private static RequestState ClassifyRequestState(BlackboardState state)
    {
        var context = state.HttpContext;
        var request = context.Request;

        // Transport-level classification (highest priority)
        var isStreaming = state.GetSignal<bool?>(SignalKeys.TransportIsStreaming) ?? false;
        var isSignalR = state.GetSignal<bool?>(SignalKeys.TransportIsSignalR) ?? false;
        var isUpgrade = state.GetSignal<bool?>(SignalKeys.TransportIsUpgrade) ?? false;

        if (isSignalR) return RequestState.SignalR;
        if (isUpgrade) return RequestState.WebSocket;

        var acceptHeader = request.Headers.Accept.ToString();
        if (acceptHeader.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return RequestState.ServerSentEvent;

        // Response-based classification
        var statusCode = context.Response.StatusCode;
        if (statusCode == 401 || statusCode == 403)
            return RequestState.AuthAttempt;
        if (statusCode == 404)
            return RequestState.NotFound;

        // Content-type classification
        var protocolClass = state.GetSignal<string>(SignalKeys.TransportProtocolClass);
        if (protocolClass == "api") return RequestState.ApiCall;
        if (protocolClass == "static") return RequestState.StaticAsset;

        // Method + content heuristics
        if (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method))
        {
            var contentType = request.ContentType ?? "";
            if (contentType.Contains("form", StringComparison.OrdinalIgnoreCase))
                return RequestState.FormSubmit;
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return RequestState.ApiCall;
        }

        // Path heuristics
        var path = request.Path.Value ?? "";
        if (path.Contains("/search", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/find", StringComparison.OrdinalIgnoreCase) ||
            request.QueryString.Value?.Contains("q=", StringComparison.OrdinalIgnoreCase) == true)
            return RequestState.Search;

        // Sec-Fetch-Dest for page vs asset
        var secFetchDest = request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
        if (secFetchDest is "script" or "style" or "image" or "font")
            return RequestState.StaticAsset;

        return RequestState.PageView;
    }

    /// <summary>
    ///     Simplifies paths for Markov state comparison: /users/123/posts → /users/{id}/posts
    /// </summary>
    private static string TemplatizePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            // Replace numeric IDs
            if (long.TryParse(segments[i], out _))
                segments[i] = "{id}";
            // Replace GUIDs
            else if (Guid.TryParse(segments[i], out _))
                segments[i] = "{guid}";
            // Replace base64-like tokens (>20 chars, alphanumeric)
            else if (segments[i].Length > 20 && segments[i].All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '='))
                segments[i] = "{token}";
        }

        return "/" + string.Join("/", segments);
    }

    private DetectionContribution NeutralContribution(string reason) => new()
    {
        DetectorName = Name,
        Category = "SessionVector",
        ConfidenceDelta = 0.0,
        Weight = 1.0,
        Reason = reason
    };

    private DetectionContribution BotContribution(double confidence, string reason, BotType botType) => new()
    {
        DetectorName = Name,
        Category = "SessionVector",
        ConfidenceDelta = confidence,
        Weight = WeightBotSignal,
        Reason = reason,
        BotType = botType.ToString()
    };

    private DetectionContribution HumanContribution(double confidence, string reason) => new()
    {
        DetectorName = Name,
        Category = "SessionVector",
        ConfidenceDelta = -confidence,
        Weight = WeightHumanSignal,
        Reason = reason
    };
}