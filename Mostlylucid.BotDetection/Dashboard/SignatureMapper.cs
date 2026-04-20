using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Mostlylucid.BotDetection.Dashboard;

/// <summary>
///     Bidirectional mapping between MultiFactorSignature IDs (used by dashboard)
///     and WaveformSignature keys (used by session store and behavioral analysis).
///     This bridges the two signature spaces so sessions can be looked up from the dashboard.
///     Uses IMemoryCache with sliding expiration — not persisted, rebuilt as traffic flows.
/// </summary>
public sealed class SignatureMapper
{
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(6);

    public SignatureMapper(IMemoryCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    ///     Record a mapping between a multi-factor signature ID and a waveform signature.
    ///     Called from the middleware after both signatures are computed.
    /// </summary>
    public void Map(string multiFactorId, string waveformSignature)
    {
        if (string.IsNullOrEmpty(multiFactorId) || string.IsNullOrEmpty(waveformSignature))
            return;

        _cache.Set($"sigmap:mf2wf:{multiFactorId}", waveformSignature,
            new MemoryCacheEntryOptions { SlidingExpiration = CacheExpiration });
        _cache.Set($"sigmap:wf2mf:{waveformSignature}", multiFactorId,
            new MemoryCacheEntryOptions { SlidingExpiration = CacheExpiration });
    }

    /// <summary>
    ///     Look up the waveform signature for a multi-factor signature ID.
    ///     Returns null if not yet mapped (visitor hasn't been seen recently).
    /// </summary>
    public string? GetWaveformSignature(string multiFactorId)
    {
        return _cache.TryGetValue($"sigmap:mf2wf:{multiFactorId}", out string? waveform)
            ? waveform
            : null;
    }

    /// <summary>
    ///     Look up the multi-factor signature ID for a waveform signature.
    /// </summary>
    public string? GetMultiFactorId(string waveformSignature)
    {
        return _cache.TryGetValue($"sigmap:wf2mf:{waveformSignature}", out string? mfId)
            ? mfId
            : null;
    }
}
