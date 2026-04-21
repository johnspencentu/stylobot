namespace Mostlylucid.BotDetection.Analysis;

/// <summary>
///     Projects a 129-dimensional session vector into the 16-axis RadarDimensions search space.
///     Each axis aggregates relevant raw dimensions into a 0-1 normalized score.
///     Used for HNSW similarity search and radar visualization.
/// </summary>
public static class SearchProjection
{
    private const int StateCount = 10;
    private const int MarkovEnd = StateCount * StateCount;      // 100
    private const int StationaryEnd = MarkovEnd + StateCount;   // 110
    private const int TemporalOffset = StationaryEnd;           // 110
    private const int FingerprintOffset = TemporalOffset + 8;   // 118
    private const int TransitionTimingOffset = FingerprintOffset + 8; // 126

    /// <summary>
    ///     Project a session vector to the 16-axis search space.
    ///     Returns null if the vector is too short (needs at least 118 dims).
    /// </summary>
    public static float[]? Project(float[] vector)
    {
        if (vector.Length < 118) return null;

        var shape = new float[RadarDimensions.Count]; // 16 axes

        // 0: ua_anomaly -- derived from UA-related signals in important_signals
        //    Not directly in session vector; set to 0 (populated from detection context)
        shape[0] = 0f;

        // 1: header_anomaly -- not in session vector; populated from detection context
        shape[1] = 0f;

        // 2: ip_reputation -- datacenter flag from fingerprint (dim 125)
        shape[2] = vector.Length > 125 ? Clamp(vector[125]) : 0f;

        // 3: behavioral -- PageView transitions (Markov row 0, dims 0-9)
        shape[3] = Clamp(SumRange(vector, 0, StateCount) * 2f);

        // 4: advanced_behavioral -- timing regularity (dim 110) + timing entropy (dim 111)
        shape[4] = Clamp((1f - Math.Abs(vector[TemporalOffset])) * 0.5f +
                         Math.Abs(vector[TemporalOffset + 1]) * 0.5f);

        // 5: cache_behavior -- StaticAsset transitions (Markov row 2, dims 20-29)
        shape[5] = Clamp(SumRange(vector, StateCount * 2, StateCount * 3) * 2f);

        // 6: security_tool -- not directly in vector; populated from detection context
        shape[6] = 0f;

        // 7: client_fingerprint -- average of fingerprint dims (118-125)
        if (vector.Length > FingerprintOffset + 7)
        {
            shape[7] = Clamp(SumRange(vector, FingerprintOffset, FingerprintOffset + 8) / 4f);
        }

        // 8: version_age -- not in session vector; populated from detection context
        shape[8] = 0f;

        // 9: inconsistency -- TCP/OS consistency (dim 121, inverted: low = inconsistent)
        shape[9] = vector.Length > 121 ? Clamp(1f - vector[121]) : 0f;

        // 10: reputation_match -- not in session vector; populated from detection context
        shape[10] = 0f;

        // 11: ai_classification -- not in session vector; populated from detection context
        shape[11] = 0f;

        // 12: cluster_signal -- not in session vector; populated from detection context
        shape[12] = 0f;

        // 13: country_reputation -- not in session vector; populated from detection context
        shape[13] = 0f;

        // 14: rate_pattern -- request rate (dim 114) + burst ratio (dim 112)
        shape[14] = Clamp(Math.Abs(vector[TemporalOffset + 4]) * 0.6f +
                          Math.Abs(vector[TemporalOffset + 2]) * 0.4f);

        // 15: payload_signature -- not in session vector; populated from detection context
        shape[15] = 0f;

        return shape;
    }

    /// <summary>
    ///     Enrich a projected shape with detection-context signals (detector contributions,
    ///     important_signals). Call after Project() to fill in context-dependent axes.
    /// </summary>
    public static void EnrichFromDetection(
        float[] shape,
        IReadOnlyDictionary<string, double>? detectorContributions,
        IReadOnlyDictionary<string, object>? importantSignals)
    {
        if (detectorContributions is not null)
        {
            // Map detector names to radar axes
            foreach (var (detector, contribution) in detectorContributions)
            {
                var axisIndex = MapDetectorToAxis(detector);
                if (axisIndex >= 0 && axisIndex < shape.Length)
                    shape[axisIndex] = Clamp(Math.Max(shape[axisIndex], (float)Math.Abs(contribution)));
            }
        }

        if (importantSignals is not null)
        {
            // Extract specific signals to their axes
            if (importantSignals.TryGetValue("ua.anomaly_score", out var uaScore))
                shape[0] = Clamp((float)Convert.ToDouble(uaScore));
            if (importantSignals.TryGetValue("threat.score", out var threatScore))
                shape[13] = Clamp((float)Convert.ToDouble(threatScore));
        }
    }

    private static int MapDetectorToAxis(string detectorName)
    {
        // Map common detector names to RadarDimensions axis indices
        return detectorName.ToLowerInvariant() switch
        {
            "useragent" or "useragentcontributor" => 0,   // ua_anomaly
            "header" or "headercontributor" => 1,          // header_anomaly
            "ipreputation" => 2,                           // ip_reputation
            "heuristic" or "behavioral" => 3,              // behavioral
            "advanced" or "markov" => 4,                   // advanced_behavioral
            "cache" => 5,                                  // cache_behavior
            "securitytool" => 6,                           // security_tool
            "fingerprint" or "clientfingerprint" => 7,     // client_fingerprint
            "versionage" => 8,                             // version_age
            "inconsistency" => 9,                          // inconsistency
            "reputation" => 10,                            // reputation_match
            "ai" or "llm" => 11,                           // ai_classification
            "cluster" => 12,                               // cluster_signal
            "country" or "geo" => 13,                      // country_reputation
            "ratelimit" or "rate" => 14,                   // rate_pattern
            "payload" => 15,                               // payload_signature
            _ => -1
        };
    }

    private static float Clamp(float value) => Math.Max(0f, Math.Min(1f, Math.Abs(value)));

    private static float SumRange(float[] vector, int start, int end)
    {
        var sum = 0f;
        for (var i = start; i < Math.Min(end, vector.Length); i++)
            sum += Math.Abs(vector[i]);
        return sum;
    }
}
