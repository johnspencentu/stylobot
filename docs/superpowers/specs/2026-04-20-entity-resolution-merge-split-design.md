# Anonymous Entity Resolution: Merge, Split, Rewind

**Date**: 2026-04-20
**Status**: Planning

## Core Architecture

### The Entity Graph

An **Entity** is a resolved identity - our best guess at "this is one actor." Entities are NOT fingerprints. Multiple fingerprints (PrimarySignatures) can belong to one entity (merged). One fingerprint can fork into multiple entities (split).

```
Entity (resolved actor)
├── PrimarySignature A (sessions 1-5)
├── PrimarySignature B (sessions 6-10, merged: cosine neighbor of A)
└── PrimarySignature C (sessions 11-15, merged: rotation trail from B)

Session snapshots are immutable. Entity membership is mutable.
```

### Session Snapshots Are Immutable Truth

Every session snapshot stores the full 129-dim vector, timestamps, request count, dominant state, and (new) header hashes. These NEVER change. They are the ground truth we can always rewind to.

Entity assignment is a LAYER on top of snapshots. When we merge or split, we reassign session ownership - we never modify the sessions themselves.

### Three Operations

#### 1. MERGE - Link two fingerprints to one entity

**Trigger:** New PrimarySignature V_new appears. Cosine similarity to recent entity V_existing is > 0.85 AND:
- Timing matches rotation cadence (if entity has established cadence), OR
- Behavioral dimensions [0-99] match closely (same Markov pattern), OR
- Stable header hashes match (same Accept-Language, same Sec-CH-UA)

**Action:**
- Create an edge: `EntityEdge(from=existing_entity, to=new_signature, type=Merge, confidence=0.91, timestamp, reason="cosine=0.91, markov_sim=0.95")`
- New signature inherits entity's reputation
- Entity's factor count increases (more resolution)

**Reversible:** Yes. The edge can be deleted, reverting to two separate entities.

#### 2. SPLIT - Fork one entity into two

**Trigger:** Oscillation detected. The entity's velocity history shows alternating high-low pattern:

```
Velocity history: [0.4, 0.05, 0.38, 0.06, 0.41, 0.04]
                   ^^^        ^^^^        ^^^^
                   high       high        high
                        ^^^^       ^^^^        ^^^^
                        low        low         low
```

This means sessions alternate between two behavioral clusters. One "entity" is actually two actors sharing a fingerprint (e.g., shared device, NAT, or corporate proxy).

**Detection algorithm:**
```
velocities = last N inter-session velocities
if autocorrelation(velocities, lag=2) > 0.6:
    # Strong period-2 oscillation → two actors
    # Cluster the session vectors into 2 groups (k-means, k=2)
    # Check if the two clusters are well-separated (silhouette > 0.5)
    → SPLIT
```

**Action:**
- Find the divergence point: the first session where the two clusters separate
- Create two new entities from the split point forward
- Sessions before the split stay with the original entity
- Create edge: `EntityEdge(type=Split, timestamp, reason="oscillation detected, silhouette=0.73")`

**Rewind:** Since session snapshots are immutable, the split replays all sessions from the divergence point, assigning each to the correct cluster.

#### 3. REWIND - Undo a bad merge

**Trigger:** After a merge, subsequent sessions show the merged entity diverging from the expected pattern. The new sessions don't match the behavioral baseline established by the original entity.

```
Entity A (sessions 1-10): stable, Markov pattern X
Merge with Signature B at session 11
Sessions 11-15: vector similarity to sessions 1-10 drops to 0.3
→ Bad merge. Signature B is NOT the same actor.
```

**Detection:**
```
post_merge_sessions = sessions after merge edge
pre_merge_baseline = entity baseline before merge
similarity = mean(cosine(post_merge, pre_merge_baseline))

if similarity < 0.5 AND sessions_since_merge >= 3:
    → REWIND: delete merge edge
    → Signature B becomes its own entity with sessions 11-15
    → Entity A reverts to sessions 1-10
```

**Action:**
- Delete the merge edge
- Create new entity for the split-off signature
- Reassign sessions to the correct entity
- Adjust reputation for both entities

## Data Model

### EntityNode (new table)

```sql
CREATE TABLE entity_nodes (
    entity_id TEXT PRIMARY KEY,         -- UUID
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    confidence_level INTEGER DEFAULT 0, -- L0-L5
    factor_count INTEGER DEFAULT 1,
    reputation_score REAL DEFAULT 0,
    is_bot INTEGER DEFAULT 0,
    metadata_json TEXT                  -- stable header hashes, rotation cadence, etc.
);
```

### EntityEdge (new table)

```sql
CREATE TABLE entity_edges (
    edge_id TEXT PRIMARY KEY,
    entity_id TEXT NOT NULL,            -- parent entity
    signature TEXT NOT NULL,            -- PrimarySignature being linked
    edge_type TEXT NOT NULL,            -- 'Merge', 'Split', 'Rewind', 'Initial'
    confidence REAL NOT NULL,
    created_at TEXT NOT NULL,
    reason TEXT,                        -- human-readable: "cosine=0.91, timing_match=true"
    reverted_at TEXT,                   -- set when edge is undone
    FOREIGN KEY (entity_id) REFERENCES entity_nodes(entity_id)
);
CREATE INDEX idx_edges_signature ON entity_edges(signature, created_at DESC);
CREATE INDEX idx_edges_entity ON entity_edges(entity_id, created_at DESC);
```

### EntitySessionAssignment (linking sessions to entities)

```sql
CREATE TABLE entity_session_assignments (
    session_id INTEGER NOT NULL,        -- FK to sessions table
    entity_id TEXT NOT NULL,            -- FK to entity_nodes
    assigned_at TEXT NOT NULL,
    assignment_type TEXT DEFAULT 'Auto', -- 'Auto', 'Merge', 'Split', 'Manual'
    PRIMARY KEY (session_id, entity_id)
);
```

## Processing Pipeline

### On Every Detection (in SignatureContributor or post-detection)

```
1. Compute PrimarySignature (already done)
2. Look up entity for this signature:
   a. Exact match in entity_edges → found entity
   b. No match → candidate for merge or new entity
3. If no entity:
   a. Compute cosine similarity to recent entities (last 1 hour)
   b. If best match > 0.85 → MERGE candidate
   c. Check timing/cadence match → confirm merge
   d. If no match → create new entity (Initial edge)
4. Record session assignment
5. Update entity confidence level and factor count
```

### Background Service (every 30s, like BotClusterService)

```
1. For each entity with 5+ sessions:
   a. Compute velocity history
   b. Check for oscillation (autocorrelation lag-2)
   c. If oscillation → SPLIT
2. For each recent merge (last 1 hour):
   a. Check post-merge similarity to pre-merge baseline
   b. If diverging → REWIND
3. Update entity confidence levels (L0→L5 progression)
4. Compute stable header hashes per entity
5. Compute anchor strengths (PersonalStability × GlobalRarity)
```

## Confidence Level Progression

```
L0: Infrastructure (1 factor: IP only) - first request
L1: Browser Guess (2 factors: IP+UA) - PrimarySignature computed
L2: Transport (4+ factors: +TLS, +HTTP/2) - after TLS/H2 detectors run
L3: Runtime (6+ factors: +client JS, +canvas) - after client-side JS
L4: Behavioral (8+ factors: +Markov, +timing) - after session vector
L5: Persistent Actor (10+ factors, 3+ sessions, stable anchors) - entity resolved
```

## FOSS vs Commercial

### FOSS
- Entity resolution runs automatically (merge/split/rewind)
- Reputation anchored to entities, not raw signatures
- Dashboard shows entity count and confidence levels
- No manual intervention, no graph exploration

### Commercial
- **Identity Inspector**: search by any factor, explore entity graph
- Manual merge/split with audit trail
- Entity timeline visualization
- Export entity graph for compliance
- Rotation trail visualization (connected dots in vector space)
- "Known actor returned with 87% confidence despite new IP" alerts

## Implementation Phases

### Phase 1: Entity Table + Initial Assignment
- Create entity_nodes, entity_edges, entity_session_assignments tables
- On first detection of a PrimarySignature, create entity + Initial edge
- Session assignments link sessions to entities
- Dashboard shows entity IDs alongside signatures

### Phase 2: Merge
- Cosine neighbor detection on new signatures
- Merge edges with confidence and reason
- Reputation inheritance
- Rotation cadence detection (velocity variance)

### Phase 3: Split + Rewind
- Oscillation detection (autocorrelation)
- Automatic split with k-means clustering of session vectors
- Post-merge divergence detection → rewind
- Session reassignment from immutable snapshots

### Phase 4: Progressive Identity + Stability Scoring
- Per-session header hash storage
- Stability × rarity anchor scoring
- Confidence level progression (L0→L5)
- Temporal decay (rolling windows)

### Phase 5: Commercial Identity Inspector
- Entity graph API
- Search by any factor
- Manual merge/split UI
- Rotation trail visualization
- Entity timeline