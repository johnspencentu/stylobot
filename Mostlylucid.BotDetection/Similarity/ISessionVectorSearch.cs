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
    ///     velocityVector is the delta from the previous session for the same signature (null for first session).
    ///     Stored in metadata to enable velocity-centroid preservation during compaction.
    /// </summary>
    Task AddAsync(float[] vector, string signature, bool isBot, double botProbability,
        float[]? velocityVector = null);

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
