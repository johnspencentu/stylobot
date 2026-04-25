namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     ANN index for session behavioral vectors (129-dim Markov chain + fingerprint).
///     Used by entity resolution for merge-candidate detection and dashboard similarity queries.
///     FOSS: file-backed HNSW (HnswSessionVectorSearch).
///     Commercial: can be replaced with Qdrant or pgvector.
/// </summary>
public interface ISessionVectorSearch
{
    /// <summary>Find the topK most similar sessions by cosine similarity.</summary>
    Task<IReadOnlyList<SessionVectorMatch>> FindSimilarAsync(
        float[] vector, int topK = 10, float minSimilarity = 0.70f);

    /// <summary>
    ///     Add a session vector to the index. Non-blocking; rebuilds graph asynchronously.
    ///     All supplementary vectors are stored in metadata and preserved through compaction.
    ///     - velocityVector: current - previous session (2-point delta)
    ///     - frequencyFingerprint: 8D autocorrelation rhythm vector
    ///     - driftVector: linear regression slope over N recent sessions
    /// </summary>
    Task AddAsync(float[] vector, string signature, bool isBot, double botProbability,
        float[]? velocityVector = null,
        float[]? frequencyFingerprint = null,
        float[]? driftVector = null);

    /// <summary>
    ///     Finds similar sessions using Mahalanobis distance for centroid entries that have
    ///     a variance envelope. Falls back to cosine similarity for L0 entries.
    ///     This correctly penalizes deviations in low-variance dimensions (discriminative dims)
    ///     and tolerates deviations in high-variance dimensions (noise dims).
    ///     topK results returned, ordered by ascending Mahalanobis distance.
    /// </summary>
    Task<IReadOnlyList<SessionVectorMatch>> FindSimilarMahalanobisAsync(
        float[] vector, int topK = 10, float maxDistance = 5.0f);

    /// <summary>Flush the index to disk.</summary>
    Task SaveAsync();

    /// <summary>Load the index from disk (called on startup).</summary>
    Task LoadAsync();

    /// <summary>Total vectors in the index (graph + pending).</summary>
    int Count { get; }

    /// <summary>
    ///     Returns a point-in-time snapshot of all (vector, metadata) pairs.
    ///     Used by VectorCompactionService to compute behavioral centroids and velocity centroids.
    ///     The returned collection is a copy; modifications do not affect the index.
    /// </summary>
    IReadOnlyList<(float[] Vector, SessionVectorMetadata Metadata)> GetAllVectorsSnapshot();

    /// <summary>
    ///     Atomically replaces the entire index with a compacted set of (vector, metadata) pairs.
    ///     Used after compaction to rebuild the HNSW graph from the compressed representation.
    ///     Saves the new index to disk immediately.
    /// </summary>
    Task ReplaceAllAsync(IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)> items);
}

/// <summary>A single ANN search result from the session vector index.</summary>
public record SessionVectorMatch(string Signature, float Similarity);
