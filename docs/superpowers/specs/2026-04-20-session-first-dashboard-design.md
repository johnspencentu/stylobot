# Session-First Dashboard UX + Live Sessions

**Date**: 2026-04-20
**Status**: Approved

## Problem

The signature detail view currently shows raw requests as the primary view. This isn't useful - sessions are the behavioral unit, not individual HTTP requests. Additionally, behavioral shape radar charts show as empty when no finalized sessions exist in SQLite, even though the write-through cache always has live data.

## Design

### Core Principle: Sessions are the primary view, requests are drill-down

When viewing a signature, you see **sessions** - including the active/live one. Each session shows its radar shape, Markov transitions, duration, request count, risk band. You drill INTO a session to see raw requests with Chrome DevTools-style filtering.

### Changes

#### 1. Signature Detail: Session-First Layout

**Current**: Shows raw detection events in a table
**New**: Shows sessions as cards/rows with:
- Live session at top with pulsing indicator and live-updating radar
- Completed sessions below, sorted by recency
- Each session card shows: radar shape (mini), duration, request count, dominant state, risk band, bot probability
- Click to drill into session detail (existing `_SessionDetail.cshtml`)

#### 2. Live Session Card

The first entry in the session list is the **active session** from the write-through cache:
- Pulsing green dot indicator
- Radar shape updates on each SignalR invalidation
- Request count incrementing live
- "In Progress" badge instead of duration
- Same drill-in to see raw requests

Already implemented in the sessions API: live session is injected from `SessionStore.GetCurrentSession()` with `live: true` flag.

#### 3. Session Drill-In: Chrome DevTools-Style Request View

When you click into a session, the existing `_SessionDetail.cshtml` expands to show:
- Full radar chart (large)
- Markov transition visualization
- Path list
- **Request table with filters**:
  - Filter by Markov state (PageView, ApiCall, StaticAsset, etc.)
  - Filter by status code (2xx, 3xx, 4xx, 5xx)
  - Filter by time range within session
  - Search by path
  - Sort by timestamp, status, state
- This is the raw request view - but contextualised within a session

#### 4. API Changes

**`api/sessions/signature/{id}`** (already modified):
- Returns live session as first entry with `live: true`
- Live radar projection computed from write-through cache
- Persisted sessions follow with their stored vectors

**New: `api/sessions/{sessionId}/requests`**:
- Returns raw requests for a specific session
- Supports query params: `?state=ApiCall&status=4xx&search=/api/users`
- Source: For live sessions, from write-through cache; for completed, from SQLite `session_requests` table (if stored) or reconstructed from detection events

#### 5. SignalR Live Updates

When the signature detail is open and a live session exists:
- `BroadcastInvalidation("sessions")` triggers on new requests for that signature
- Client re-fetches the live session entry only (not all sessions)
- Radar chart re-renders with updated shape

### Files to Modify

| File | Change |
|------|--------|
| `_SignatureDetail.cshtml` | Rewrite primary view from requests to sessions; add session cards with mini radar; add live session indicator |
| `StyloBotDashboardMiddleware.cs` | Already done: live session injection. Add request filter API. |
| `_SessionDetail.cshtml` | Add Chrome DevTools-style request filter bar |
| `SqliteSessionStore` | Add method to get raw requests for a session (if stored) |
| `SessionStore` | Already done: `GetLiveRadarProjection()` and `GetCurrentSession()` |

### Non-Goals

- Full DevTools network panel (no request/response body inspection - zero-PII)
- WebSocket/SSE live streaming of radar updates (SignalR invalidation + re-fetch is sufficient)
- Request body inspection (violates zero-PII)