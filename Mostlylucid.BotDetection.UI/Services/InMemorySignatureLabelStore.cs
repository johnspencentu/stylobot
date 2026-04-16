using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Default in-memory label store. Lost on restart - intended for small manual labeling
///     sessions, dev loops, and smoke tests. Production installs should register a SQLite
///     or PostgreSQL implementation so labels accumulate across deploys.
///
///     Upsert semantics: a given signature can carry at most one label per labeler (email);
///     re-labeling replaces the prior value. Cross-labeler disagreement is preserved - a
///     signature with {alice: Bot, bob: Human} returns alice's as "latest" if newer.
/// </summary>
public sealed class InMemorySignatureLabelStore : ISignatureLabelStore
{
    // (signature, labeler) → label
    private readonly ConcurrentDictionary<(string Signature, string Labeler), SignatureLabel> _labels = new();

    public Task<SignatureLabel> UpsertAsync(SignatureLabel label, CancellationToken ct = default)
    {
        var key = (label.Signature, label.LabeledBy);
        _labels[key] = label;
        return Task.FromResult(label);
    }

    public Task<SignatureLabel?> GetLatestAsync(string signature, CancellationToken ct = default)
    {
        SignatureLabel? best = null;
        foreach (var kv in _labels)
        {
            if (!kv.Key.Signature.Equals(signature, StringComparison.Ordinal)) continue;
            if (best is null || kv.Value.LabeledAt > best.LabeledAt) best = kv.Value;
        }
        return Task.FromResult(best);
    }

    public Task<IReadOnlyList<SignatureLabel>> ListSinceAsync(
        DateTime? since, int limit, CancellationToken ct = default)
    {
        var query = _labels.Values.AsEnumerable();
        if (since.HasValue) query = query.Where(l => l.LabeledAt >= since.Value);
        var list = query
            .OrderByDescending(l => l.LabeledAt)
            .Take(limit <= 0 ? 1000 : limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<SignatureLabel>>(list);
    }

    public Task RemoveAsync(string signature, string labeledBy, CancellationToken ct = default)
    {
        _labels.TryRemove((signature, labeledBy), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<SignatureLabelKind, int>> GetCountsAsync(CancellationToken ct = default)
    {
        var counts = new Dictionary<SignatureLabelKind, int>
        {
            [SignatureLabelKind.Bot] = 0,
            [SignatureLabelKind.Human] = 0,
            [SignatureLabelKind.BenignBot] = 0,
            [SignatureLabelKind.Uncertain] = 0
        };
        foreach (var label in _labels.Values)
            counts[label.Kind] = counts.GetValueOrDefault(label.Kind) + 1;
        return Task.FromResult<IReadOnlyDictionary<SignatureLabelKind, int>>(counts);
    }
}