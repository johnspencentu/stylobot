namespace Mostlylucid.BotDetection.Analysis;

/// <summary>
///     Projects a 129-dimensional session vector into 8 interpretable radar axes.
///     Each axis is a normalized [0,1] score representing a behavioral dimension.
///     Used for the behavioral shape radar chart in the dashboard.
/// </summary>
public static class VectorRadarProjection
{
    private static readonly int StateCount = 10;

    /// <summary>
    ///     Radar axis labels for the behavioral shape chart.
    /// </summary>
    public static readonly string[] AxisLabels =
    [
        "Navigation",      // How much page-to-page browsing
        "API Usage",       // How much API calling
        "Asset Loading",   // How much static asset loading (CSS/JS/images)
        "Timing Regularity", // How regular/robotic the timing is
        "Request Rate",    // How fast requests come in
        "Path Diversity",  // How many unique paths visited
        "Fingerprint",     // TLS/TCP/H2 fingerprint quality
        "Timing Anomaly"   // Per-transition timing anomalies (impossible speed, consistency)
    ];

    /// <summary>
    ///     Projects a session vector into 8 radar axes. Returns null if vector is too short.
    /// </summary>
    public static double[]? Project(float[] vector)
    {
        if (vector.Length < 118) return null; // Need at least temporal features

        var axes = new double[8];

        // Axis 0: Navigation - sum of PageView row transitions (dims 0-9)
        var navSum = 0f;
        for (var i = 0; i < StateCount; i++)
            navSum += vector[i]; // Row 0 = PageView transitions
        axes[0] = Math.Min(1.0, navSum * 2); // Scale up (transitions are normalized)

        // Axis 1: API Usage - sum of ApiCall row transitions (dims 10-19)
        var apiSum = 0f;
        for (var i = StateCount; i < StateCount * 2; i++)
            apiSum += vector[i]; // Row 1 = ApiCall transitions
        axes[1] = Math.Min(1.0, apiSum * 2);

        // Axis 2: Asset Loading - sum of StaticAsset row transitions (dims 20-29)
        var assetSum = 0f;
        for (var i = StateCount * 2; i < StateCount * 3; i++)
            assetSum += vector[i]; // Row 2 = StaticAsset transitions
        axes[2] = Math.Min(1.0, assetSum * 2);

        // Axis 3: Timing Regularity - dim 110 (CV of inter-request intervals, inverted)
        // Low CV = regular = bot-like, so we invert: high value = more regular
        var stationaryOffset = StateCount * StateCount; // 100
        var temporalOffset = stationaryOffset + StateCount; // 110
        axes[3] = Math.Min(1.0, 1.0 - Math.Abs(vector[temporalOffset + 0]));

        // Axis 4: Request Rate - dim 114 (request rate normalized)
        axes[4] = Math.Min(1.0, Math.Abs(vector[temporalOffset + 4]) * 3);

        // Axis 5: Path Diversity - dim 116 (unique path ratio)
        axes[5] = Math.Min(1.0, Math.Abs(vector[temporalOffset + 6]) * 2);

        // Axis 6: Fingerprint - average of fingerprint dims (118-125)
        var fpOffset = temporalOffset + 8; // 118
        if (vector.Length > fpOffset + 7)
        {
            var fpSum = 0f;
            for (var i = 0; i < 8; i++)
                fpSum += Math.Abs(vector[fpOffset + i]);
            axes[6] = Math.Min(1.0, fpSum / 4.0); // Avg scaled up
        }

        // Axis 7: Timing Anomaly - average of transition timing dims (126-128)
        if (vector.Length > 128)
        {
            axes[7] = Math.Min(1.0,
                (Math.Abs(vector[126]) + Math.Abs(vector[127]) + Math.Abs(vector[128])) / 1.5);
        }

        // Ensure minimum polygon shape (0.05 minimum per axis so radar always has area)
        for (var i = 0; i < axes.Length; i++)
            axes[i] = Math.Max(0.05, axes[i]);

        return axes;
    }
}
