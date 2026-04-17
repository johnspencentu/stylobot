using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     A single proof-of-work micro-puzzle: find a nonce such that SHA256(seed + nonce)
///     has the required number of leading zero hex characters.
/// </summary>
public sealed record PuzzleSeed(byte[] Seed, int RequiredZeros);

/// <summary>
///     Server-side record of a challenge issued to a client.
///     Single-use: consumed on first successful verification.
/// </summary>
public sealed record ChallengeRecord
{
    public required string Id { get; init; }
    public required string Signature { get; init; }
    public required int PuzzleCount { get; init; }
    public required int RequiredZeros { get; init; }
    public required PuzzleSeed[] Puzzles { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
///     Result of a client solving a PoW challenge, capturing timing metadata
///     for use as detection signals on subsequent requests.
/// </summary>
public sealed record ChallengeVerificationResult
{
    public required string Signature { get; init; }
    public required double TotalSolveDurationMs { get; init; }
    public required int ReportedWorkerCount { get; init; }
    public required int PuzzleCount { get; init; }
    public required double[] PuzzleTimingsMs { get; init; }
    public required double TimingJitter { get; init; }
    public required DateTimeOffset VerifiedAt { get; init; }
}

/// <summary>
///     Server-side store for PoW challenges: creation, single-use consumption, and
///     verification result storage for the feedback loop into detection signals.
/// </summary>
public interface IChallengeStore
{
    /// <summary>
    ///     Creates a new PoW challenge for the given signature.
    /// </summary>
    ChallengeRecord CreateChallenge(string signature, int puzzleCount, int requiredZeros, TimeSpan expiry);

    /// <summary>
    ///     Validates and consumes a challenge (single-use). Returns null if not found or expired.
    /// </summary>
    ChallengeRecord? ValidateAndConsume(string challengeId);

    /// <summary>
    ///     Records the verification result (solve timing metadata) for the feedback loop.
    /// </summary>
    void RecordVerification(ChallengeVerificationResult result);

    /// <summary>
    ///     Gets the most recent verification result for a signature, if any.
    /// </summary>
    ChallengeVerificationResult? GetVerification(string signature);
}

/// <summary>
///     In-memory challenge store with automatic expiry sweep.
///     Challenges are ephemeral (2-minute default lifetime), so SQLite persistence is unnecessary.
/// </summary>
public sealed class InMemoryChallengeStore : IChallengeStore, IDisposable
{
    private readonly ConcurrentDictionary<string, ChallengeRecord> _challenges = new();
    private readonly ConcurrentDictionary<string, ChallengeVerificationResult> _verifications = new();
    private readonly Timer _sweepTimer;
    private readonly ILogger<InMemoryChallengeStore>? _logger;
    private readonly TimeSpan _verificationRetention;

    public InMemoryChallengeStore(
        ILogger<InMemoryChallengeStore>? logger = null,
        TimeSpan? verificationRetention = null)
    {
        _logger = logger;
        _verificationRetention = verificationRetention ?? TimeSpan.FromMinutes(30);
        _sweepTimer = new Timer(SweepExpired, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public ChallengeRecord CreateChallenge(string signature, int puzzleCount, int requiredZeros, TimeSpan expiry)
    {
        var puzzles = new PuzzleSeed[puzzleCount];
        for (var i = 0; i < puzzleCount; i++)
        {
            var seed = new byte[16];
            RandomNumberGenerator.Fill(seed);
            puzzles[i] = new PuzzleSeed(seed, requiredZeros);
        }

        var record = new ChallengeRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Signature = signature,
            PuzzleCount = puzzleCount,
            RequiredZeros = requiredZeros,
            Puzzles = puzzles,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(expiry)
        };

        _challenges[record.Id] = record;

        _logger?.LogDebug(
            "Created PoW challenge {Id} for {Signature}: {Count} puzzles, {Zeros} zeros, expires {Expiry}",
            record.Id, signature, puzzleCount, requiredZeros, record.ExpiresAt);

        return record;
    }

    public ChallengeRecord? ValidateAndConsume(string challengeId)
    {
        if (!_challenges.TryRemove(challengeId, out var record))
            return null;

        if (DateTimeOffset.UtcNow > record.ExpiresAt)
        {
            _logger?.LogDebug("Challenge {Id} expired", challengeId);
            return null;
        }

        return record;
    }

    public void RecordVerification(ChallengeVerificationResult result)
    {
        _verifications[result.Signature] = result;

        _logger?.LogDebug(
            "Recorded PoW verification for {Signature}: {Duration:F0}ms, {Workers} workers, jitter={Jitter:F3}",
            result.Signature, result.TotalSolveDurationMs, result.ReportedWorkerCount, result.TimingJitter);
    }

    public ChallengeVerificationResult? GetVerification(string signature)
    {
        if (!_verifications.TryGetValue(signature, out var result))
            return null;

        // Check retention window
        if (DateTimeOffset.UtcNow - result.VerifiedAt > _verificationRetention)
        {
            _verifications.TryRemove(signature, out _);
            return null;
        }

        return result;
    }

    private void SweepExpired(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        var expiredChallenges = 0;
        var expiredVerifications = 0;

        foreach (var kvp in _challenges)
        {
            if (now > kvp.Value.ExpiresAt && _challenges.TryRemove(kvp.Key, out _))
                expiredChallenges++;
        }

        foreach (var kvp in _verifications)
        {
            if (now - kvp.Value.VerifiedAt > _verificationRetention && _verifications.TryRemove(kvp.Key, out _))
                expiredVerifications++;
        }

        if (expiredChallenges > 0 || expiredVerifications > 0)
        {
            _logger?.LogDebug(
                "Swept {Challenges} expired challenges, {Verifications} expired verifications",
                expiredChallenges, expiredVerifications);
        }
    }

    public void Dispose()
    {
        _sweepTimer.Dispose();
    }
}
