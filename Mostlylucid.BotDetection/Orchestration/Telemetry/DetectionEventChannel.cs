using System.Threading.Channels;

namespace Mostlylucid.BotDetection.Orchestration.Telemetry;

/// <summary>
///     In-process channel for detection events. Bridges the detection middleware
///     (which may block requests before downstream middleware runs) to the
///     dashboard broadcast middleware (which persists and broadcasts events).
///     This ensures ALL detections — including blocked requests — are visible in the dashboard.
/// </summary>
public sealed class DetectionEventChannel
{
    private readonly Channel<DetectionEvent> _channel;

    public DetectionEventChannel()
    {
        _channel = Channel.CreateBounded<DetectionEvent>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <summary>
    ///     Publish a detection event. Non-blocking — drops oldest if channel is full.
    ///     Called by BotDetectionMiddleware for ALL detections (blocked or not).
    /// </summary>
    public bool TryPublish(DetectionEvent evt) => _channel.Writer.TryWrite(evt);

    /// <summary>
    ///     Read all available events without waiting. Used by DetectionBroadcastMiddleware
    ///     to drain events that arrived via the channel (e.g., from blocked requests).
    /// </summary>
    public IAsyncEnumerable<DetectionEvent> ReadAllAsync(CancellationToken ct = default)
        => _channel.Reader.ReadAllAsync(ct);

    /// <summary>Try to read a single event without waiting.</summary>
    public bool TryRead(out DetectionEvent? evt) => _channel.Reader.TryRead(out evt);
}
