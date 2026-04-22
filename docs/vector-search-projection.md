# Vector Search Projection

StyloBot compresses each visitor's behavioral session into a **129-dimensional vector** that captures Markov chain transitions, temporal features, and fingerprint characteristics. This vector enables similarity search, anomaly detection, and bot archetype matching.

![Vector Search Projection](images/vector-search-projection.png)

## How it works

### Raw vector → 16 consolidated axes

The 129 raw dimensions are too many for human interpretation or efficient similarity search. The **search projection** aggregates them into 16 interpretable axes:

| Axis group | Raw dimensions | Consolidated axes |
|------------|---------------|-------------------|
| **Markov features** | [0-48] | UA anomaly, Header anomaly, IP reputation, Behavioral, Advanced behavioral, Cache behavior |
| **Temporal features** | [49-80] | Security tool, Client fingerprint, Version age, Inconsistency, Reputation match |
| **Fingerprint features** | [81-112] | AI classification, Cluster signal, Country reputation |
| **Context features** | [113-129] | Rate pattern, Payload signature |

Some axes are **enriched from detection context** (not directly from the raw vector) — e.g., country reputation and security tool signals come from other detectors' contributions, not from the session vector itself.

### Bot archetype profiles

Different bot types have distinctive shapes in the 16-axis radar space:

**Python Requests scraper** — Sharp, spiky profile. High on a few axes (UA anomaly, rate pattern, header anomaly), near zero elsewhere. The scraper doesn't try to look human — it just hammers endpoints fast.

**Headless Chrome** — Moderate, rounded profile. Broad spread across many axes. The headless browser passes basic checks (UA, headers) but shows anomalies in behavioral patterns, timing, and client fingerprint. The spread is wider than a real browser but less extreme than a raw scraper.

**Legitimate user** — Low, uniform values. Smooth, organic shape close to the center. Real browsers produce consistent, low-signal responses across all axes because they genuinely ARE what they claim to be.

### Fuzz band (tolerance zone)

The green dashed circle in the radar chart represents the **HNSW similarity threshold** — the cosine distance tolerance zone for matching. Vectors inside this band are considered similar enough to be the same behavioral pattern. Vectors outside it are distinct.

This is how the entity resolution system detects rotation: when a new signature appears with a vector INSIDE the fuzz band of an existing entity, they're merged. Outside the band = new entity.

## Temporal evolution

The bottom of the diagram shows how a session's behavioral fingerprint **crystallizes over time**:

| Time | Data points | Confidence | What's happening |
|------|-------------|------------|-----------------|
| T=0s | ~10 | 0.05 | Insufficient data. Small, undifferentiated shape. |
| T=30s | ~50 | 0.28 | Initial patterns detected. Rate pattern and inconsistency start emerging. |
| T=60s | ~150 | 0.62 | Distinctive shape forming. IP reputation, inconsistency, rate pattern, and header anomaly clearly visible. |
| T=120s | ~300+ | 0.91 | Stable fingerprint established. The shape is fully formed and won't change significantly with more data. |

This is why the **partial Markov chain detection** fires at 3-5 requests — it matches the emerging shape against known archetypes before the fingerprint fully crystallizes. By T=30s, the system already has enough signal to classify most scrapers.

## Implementation

The projection is implemented in `VectorRadarProjection.Project()` which maps the 129-dim vector to 8 axes (the dashboard uses 8; the search projection uses 16 for higher resolution):

- **Dashboard radar**: 8 axes — Navigation, API Usage, Asset Loading, Timing Regularity, Request Rate, Path Diversity, Fingerprint, Timing Anomaly
- **Search projection**: 16 axes — the full set shown in the diagram, used for HNSW similarity search and entity resolution

The HNSW index (`HnswIntentSearch`) stores these projected vectors for sub-millisecond nearest-neighbor queries. When a new session is finalized, its projection is compared against all known archetypes and existing entity vectors to find matches.
