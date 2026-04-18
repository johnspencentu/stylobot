# The Signature Exchange

Status: Draft  
Owner: Core Platform  
Target Release: Future

## 1. Summary

The Signature Exchange is a hosted and federated sharing system for zero-PII bot behavioral signatures. It lets StyloBot nodes publish, ingest, and consume signed, privacy-safe intelligence about abusive traffic patterns through a Stylobot-hosted exchange service and direct trusted peers.

Goal: improve detection speed and confidence across independent deployments without sharing raw user data or personal identifiers, while turning observed signatures into reusable exchange artifacts and auto-generated BDF scenarios.

## 2. Why This Exists

StyloBot already identifies requests using multi-vector, zero-PII signatures.  
Each deployment learns valuable behavior over time, but that knowledge is local.

The Signature Exchange extends this model:

1. A known abusive behavioral signature seen by one node can be recognized earlier by others.
2. Detection quality improves against evasive traffic that rotates IPs, user agents, and locations.
3. Teams benefit from network intelligence while keeping control of policy decisions.

## 3. Non-Goals

1. No central storage of raw request payloads.
2. No exchange of IP addresses, cookies, emails, or other PII.
3. No automatic hard blocking solely from third-party data.
4. No vendor lock-in transport. Protocol is open and documented.
5. No manual upload path that permits arbitrary JSON or operator-supplied free text outside the allowlisted schema.

## 4. Core Concepts

### 4.1 Signature

A signature represents behavioral identity over time, not personal identity.  
It is derived from multiple vectors and resilient to low-effort evasion.

### 4.2 Signature Record

A signed message describing observed behavior for a signature window:

1. Risk estimate
2. Confidence
3. Detector contributions
4. Behavioral patterns across time
5. Optional cluster context
6. Auto-generated BDF artifact derived from the privacy-safe behavioral signature

### 4.3 Exchange Node

A StyloBot deployment that can:

1. Produce exchange records from local observations
2. Validate incoming records
3. Merge incoming intelligence into local reputation models

### 4.4 Hosted Exchange

The hosted exchange is the canonical place where operators can publish privacy-safe signatures for downstream consumers.

It provides:

1. Signature ingestion with strict schema validation
2. Auto-generation of BDF artifacts from accepted signatures
3. Distribution of approved signatures to connected users
4. Auditable lineage from uploaded signature to shared exchange record

## 5. Product Requirements

1. FOSS deployments ship with exchange support enabled by default, but network participation requires an issued exchange key.
2. Separate controls for publish, consume, and hosted-upload ingestion.
3. Every accepted uploaded signature must produce a normalized exchange record and an auto-generated BDF artifact.
4. Per-peer and per-host trust settings, quotas, and rate limits.
5. Admin visibility into imported, exported, uploaded, promoted, and rejected intelligence.
6. Replay protection, signature validation, and revocation support.
7. Fully auditable data lineage for every imported decision influence.
8. Unkeyed installs remain local-only and never join the shared exchange.

## 6. Privacy and Security Requirements

1. Shared payload must be zero-PII.
2. Records must be signed with peer keys (Ed25519 recommended).
3. Transport must use TLS 1.2+.
4. mTLS support for private exchanges.
5. Record TTL and retention limits must be configurable.
6. Imported records must never bypass local policy engine.
7. Abuse protections:
   1. Peer throttling
   2. Reputation weighting
   3. Quarantine mode for suspicious peers
8. Uploaded signatures must be rejected if they contain non-allowlisted fields or potential PII-bearing text blobs.
9. Auto-generated BDF output must be derived only from privacy-safe exchange fields and never from raw request payloads.

## 7. Data Model (v1)

```json
{
  "version": "1.0",
  "recordId": "uuid",
  "source": {
    "nodeId": "stylo-node-123",
    "pubKeyId": "key-2026-01"
  },
  "signature": {
    "primary": "sig_abc123",
    "algorithm": "hmac-sha256",
    "vectorSchema": "v3"
  },
  "observationWindow": {
    "startUtc": "2026-02-14T10:00:00Z",
    "endUtc": "2026-02-14T10:05:00Z"
  },
  "classification": {
    "isBot": true,
    "riskBand": "High",
    "botProbability": 0.92,
    "confidence": 0.84
  },
  "behavior": {
    "velocityScore": 0.91,
    "sequenceAnomalyScore": 0.77,
    "requestPatternHash": "rp_789",
    "bdfScenarioId": "bdf_123",
    "detectorSummary": [
      { "detector": "Behavioral", "delta": 0.45 },
      { "detector": "Header", "delta": 0.20 },
      { "detector": "ClientSide", "delta": 0.18 }
    ]
  },
  "policyHint": {
    "recommendedAction": "Challenge",
    "reasonCodes": ["BEHAVIORAL_SPIKE", "HEADER_MISMATCH"]
  },
  "ttlSeconds": 86400,
  "issuedAtUtc": "2026-02-14T10:05:05Z",
  "signatureEnvelope": {
    "alg": "Ed25519",
    "kid": "key-2026-01",
    "sig": "base64..."
  }
}
```

### 7.1 Upload Contract Rules

Hosted ingestion accepts only normalized signature payloads that can be losslessly converted into the exchange record.

Rules:

1. No raw IP, raw User-Agent, cookies, email addresses, account identifiers, session identifiers, or free-form request bodies.
2. Reason codes and detector names must come from controlled vocabularies.
3. Any path-like fields must be normalized before upload.
4. BDF is generated server-side after validation; clients do not submit arbitrary BDF content in v1.

### 7.2 Submission Envelope (`sbx-submit.v1`)

The upload unit is a submitter-signed submission envelope. This proves provenance from a keyed install, but it is not yet trusted exchange data.

Design goals:

1. Prove which install submitted the candidate
2. Make replay and tampering detectable
3. Keep the candidate payload zero-PII by construction
4. Separate submitter authenticity from exchange admission

Envelope shape:

```json
{
  "format": "sbx-submit.v1",
  "submissionId": "sub_01jst6d6s4v8x1m3n5p7q9r2t4",
  "canonicalVersion": 1,
  "submittedAtUtc": "2026-04-18T10:15:00Z",
  "expiresAtUtc": "2026-04-18T10:30:00Z",
  "submitter": {
    "installId": "inst_01jss2b1f2qv4d7y9h2m8x6a3p",
    "tenantId": "tenant_01jst0w8q9m3n5f7z1k2c4d6r8",
    "kid": "install-submit-key-2026-04"
  },
  "scope": {
    "topic": "scanner"
  },
  "payload": {
    "signatureRef": {
      "primary": "sig_8b6f2d3f1e2a",
      "algorithm": "hmac-sha256",
      "vectorSchema": "sig.v3"
    },
    "classification": {
      "verdict": "bot",
      "botProbability": 0.96,
      "confidence": 0.88,
      "riskBand": "High",
      "recommendedActionHint": "Challenge"
    },
    "evidence": {
      "reasonCodes": ["HEADER_INCONSISTENT", "BEHAVIORAL_BURST", "TLS_MISMATCH"],
      "detectors": [
        { "name": "Header", "weight": 0.24 },
        { "name": "Behavioral", "weight": 0.39 },
        { "name": "Tls", "weight": 0.17 }
      ],
      "requestCount": 43,
      "windowSeconds": 300,
      "requestPatternHash": "rph_7d1a09c4",
      "pathPatternHash": "pph_92ac0f10",
      "clusterHint": "scanner-burst"
    },
    "privacy": {
      "piiLevel": "none",
      "pathNormalization": "templated",
      "freeTextPresent": false
    }
  },
  "integrity": {
    "canonicalAlg": "JCS-8785",
    "signingAlg": "Ed25519",
    "payloadSha256": "base64url...",
    "sig": "base64url..."
  }
}
```

Submission validation rules:

1. Canonicalize the envelope excluding `integrity.sig`.
2. Verify `payloadSha256` matches the canonical payload bytes.
3. Verify the submitter Ed25519 signature using `submitter.kid`.
4. Reject expired envelopes and timestamps outside skew tolerance.
5. Reject replayed `submissionId` values.
6. Reject if `privacy.piiLevel != none` or if `freeTextPresent = true`.
7. Reject if any field exists outside the allowlisted schema.

### 7.3 Exchange Envelope (`sbx-share.v1`)

The downloadable unit is a Stylobot-signed exchange envelope. Only this object is trusted by consumers for import into local exchange state.

Design goals:

1. Deterministic canonical bytes for signing
2. Zero-PII by construction
3. Central publisher authority for trust bootstrap
4. Preserve lineage back to the original submission
5. Tamper detection without trusting transport alone

Envelope shape:

```json
{
  "format": "sbx-share.v1",
  "shareId": "shr_01jsrj7n0w6j4c6g5m9k2w8r9y",
  "canonicalVersion": 1,
  "issuedAtUtc": "2026-04-18T10:15:00Z",
  "expiresAtUtc": "2026-05-02T10:15:00Z",
  "publisher": {
    "authority": "stylobot-exchange",
    "kid": "exchange-signing-key-2026-04"
  },
  "lineage": {
    "submissionId": "sub_01jst6d6s4v8x1m3n5p7q9r2t4",
    "installId": "inst_01jss2b1f2qv4d7y9h2m8x6a3p",
    "tenantId": "tenant_01jst0w8q9m3n5f7z1k2c4d6r8",
    "admissionState": "accepted"
  },
  "admission": {
    "reviewMode": "automatic",
    "admittedAtUtc": "2026-04-18T10:16:03Z",
    "exchangeRecordId": "exr_01jst7ak2q8v3n6m4p1r9w5y7c"
  },
  "scope": {
    "feed": "global",
    "topic": "scanner",
    "audience": "trusted-exchange"
  },
  "signatureRef": {
    "primary": "sig_8b6f2d3f1e2a",
    "algorithm": "hmac-sha256",
    "vectorSchema": "sig.v3"
  },
  "classification": {
    "verdict": "bot",
    "botProbability": 0.96,
    "confidence": 0.88,
    "riskBand": "High",
    "recommendedActionHint": "Challenge"
  },
  "evidence": {
    "reasonCodes": ["HEADER_INCONSISTENT", "BEHAVIORAL_BURST", "TLS_MISMATCH"],
    "detectors": [
      { "name": "Header", "weight": 0.24 },
      { "name": "Behavioral", "weight": 0.39 },
      { "name": "Tls", "weight": 0.17 }
    ],
    "requestCount": 43,
    "windowSeconds": 300,
    "requestPatternHash": "rph_7d1a09c4",
    "pathPatternHash": "pph_92ac0f10",
    "clusterHint": "scanner-burst"
  },
  "privacy": {
    "piiLevel": "none",
    "pathNormalization": "templated",
    "freeTextPresent": false
  },
  "bdf": {
    "scenarioId": "bdf_01jss4x6f6s1z8e3m5n7p9q2r4",
    "generatorVersion": "bdf-map.v1",
    "digestSha256": "base64url..."
  },
  "integrity": {
    "canonicalAlg": "JCS-8785",
    "signingAlg": "Ed25519",
    "payloadSha256": "base64url...",
    "sig": "base64url..."
  }
}
```

Validation rules:

1. Canonicalize the envelope excluding `integrity.sig`.
2. Verify `payloadSha256` matches the canonical payload bytes.
3. Verify the Ed25519 signature using the publisher `kid`.
4. Verify `lineage.admissionState = accepted`.
5. Reject if `expiresAtUtc` is in the past or `issuedAtUtc` is outside skew tolerance.
6. Reject if `privacy.piiLevel != none` or if `freeTextPresent = true`.
7. Reject if any field exists outside the allowlisted schema.

Recommended key model:

1. Each install that wants to submit candidates receives a bootstrap credential by email.
2. That credential authenticates submission rights to the hosted exchange.
3. The hosted exchange validates, normalizes, and signs the final share object using the Stylobot authority key.
4. Consumers trust only the Stylobot authority signing keys in v1.
5. `kid` rotation is mandatory and old authority keys remain valid only for a bounded overlap window.

Reason for this model:

1. Email is acceptable for bootstrap, not for distributing a global signing secret.
2. Central signing keeps trust simple while the exchange is young.
3. Consumers need to trust one authority keyset, not every participant.
4. A compromised submitter can be revoked without rotating consumer verification material.

### 7.4 Admission State Machine

Every submission moves through an explicit admission lifecycle:

1. `received`
2. `verified`
3. `rejected`
4. `quarantined`
5. `accepted`
6. `revoked`

Rules:

1. `received` means the submission was stored but not yet validated.
2. `verified` means signature, entitlement, replay, and schema checks passed.
3. `rejected` means the submission failed validation and can never appear in the feed.
4. `quarantined` means the submission passed mechanical validation but was held back by policy or anomaly checks.
5. `accepted` means Stylobot minted an exchange envelope and the record is eligible for download.
6. `revoked` means a previously accepted record was withdrawn and should no longer influence imports.

## 8. Protocol (v1)

### 8.1 Discovery

Peers are statically configured in v1:

1. URL
2. Node ID
3. Public key
4. Optional mTLS cert requirements

### 8.2 Publish API

`POST /exchange/v1/submit`

1. Accepts a submitter-signed `sbx-submit.v1` envelope.
2. Idempotency via `submissionId`.
3. Response returns admission state and reasons.
4. `accepted` responses include the minted `shareId` and `exchangeRecordId`.

### 8.3 Subscribe API

`GET /exchange/v1/signatures?since={cursor}`

1. Pull model in v1.
2. Cursor-based pagination.
3. Returns only Stylobot-signed `accepted` exchange envelopes.
4. Backfill window capped by peer policy.

### 8.4 Hosted BDF Retrieval

`GET /exchange/v1/bdf/{bdfScenarioId}`

1. Returns the auto-generated BDF artifact for an accepted exchange signature.
2. Payload remains zero-PII and is suitable for replay/testing workflows.
3. Availability may be governed by plan, trust level, or retention settings.

### 8.5 Health and Metadata

1. `GET /exchange/v1/health`
2. `GET /exchange/v1/capabilities`
3. `GET /exchange/v1/keys`
4. `GET /exchange/v1/revocations`

## 9. Decision Integration

Imported intelligence flows into local orchestration as a weighted contributor.

Rules:

1. Imported record creates an `ExchangeReputation` signal.
2. Weight is bounded and decays over time.
3. Local evidence always has higher priority than external hints.
4. Final action still comes from local policy matrix.

## 10. Trust Model

V1 is centralized trust, not general peer federation.

Rules:

1. Consumers trust only Stylobot authority keys for downloadable exchange objects.
2. Submitter keys prove provenance for uploads, not import trust.
3. Submitters can be rate-limited, suspended, or revoked independently.
4. Imported exchange hints remain capped contributors inside local policy.

Submitter policy:

1. Per-install submit entitlement
2. Optional separate consume entitlement
3. Per-install quotas and rate limits
4. Strike policy for invalid, noisy, or abusive submissions

## 11. Admin UX Requirements

1. New Exchange section in dashboard:
   1. Peer status
   2. Records in/out
   3. Reject rates
   4. Trust score
   5. Uploaded signatures and generated BDF counts
2. Per-peer toggle: enabled, paused, quarantine.
3. Global FOSS toggle: exchange enabled by default, can be disabled locally.
4. Evidence drill-down: show which imported record influenced a decision.
5. Exportable audit logs.
6. Upload review queue showing rejection reasons for non-compliant payloads.
7. Submission states visible by install: received, verified, rejected, quarantined, accepted, revoked.

## 12. Operational Requirements

1. Bounded queue sizes and backpressure behavior.
2. Dead-letter queue for malformed records.
3. Retry policy with exponential backoff.
4. Metrics:
   1. `exchange_records_received_total`
   2. `exchange_records_accepted_total`
   3. `exchange_records_rejected_total`
   4. `exchange_peer_trust_score`
   5. `exchange_influence_applied_total`

## 13. Rollout Plan

### Phase 0: Local-only Simulation

1. Emit candidate submissions to local sink.
2. Validate submission schema and signing.
3. No downloadable feed yet.
4. No decision influence.

### Phase 1: Hosted Ingestion Pilot

1. Stylobot-hosted submit endpoint accepts zero-PII signed submissions from controlled nodes.
2. Stylobot validates and mints accepted exchange envelopes.
3. Read-only import and observability.
4. Compare imported hints vs local outcomes.

### Phase 2: Weighted Influence

1. Enable capped influence for low-risk actions.
2. Measure false positive/negative drift.
3. Automatic rollback on quality regression.

### Phase 3: General Availability

1. FOSS ships with exchange capability on by default, but participation is key-gated and opt-out remains available.
2. Multi-peer federation and hosted exchange feed.
3. Full dashboard management.
4. Public protocol docs and reference configs.

## 14. Risk Register

1. Poisoning attempts from compromised peers
2. Signature collision concerns across schema versions
3. Over-reliance on external hints in sparse local traffic
4. Operational complexity in key rotation

Mitigations are mandatory before GA:

1. Strict trust caps
2. Key rotation and revocation endpoint
3. Data quality scoring
4. Automatic quarantine on anomaly thresholds

## 15. Open Questions

1. Should consume entitlement be separate from submit entitlement in the first release?
2. Should vector schema compatibility be hard-fail or negotiated?
3. What is the minimum accepted confidence for automatic admission?
4. Should cluster-level intelligence be first-class in v1 or v2?

## 16. MVP v1

The MVP is a centralized, moderated exchange.

In scope:

1. Key issuance for participating installs
2. `sbx-submit.v1` submission envelope
3. `POST /exchange/v1/submit`
4. Zero-PII validation, replay protection, entitlement checks, and signature verification
5. Admission state machine persisted server-side
6. `sbx-share.v1` Stylobot-signed downloadable envelope
7. `GET /exchange/v1/signatures?cursor=...`
8. `GET /exchange/v1/keys`
9. Local import as capped, read-only hinting
10. Basic admin visibility for counts, states, rejects, and accepted records

Out of scope:

1. Direct peer-to-peer federation
2. Non-Stylobot signer trust
3. Automatic hard blocking from exchange-only evidence
4. Blocking BDF generation in the submit path
5. Advanced trust scoring between independent peers

Implementation order:

1. Finalize allowlisted payload schema
2. Implement submitter key issuance and verification
3. Build submit endpoint and validation pipeline
4. Persist admission states and audit trail
5. Mint Stylobot-signed accepted envelopes
6. Build pull feed and consumer verification
7. Add capped local influence
8. Add async BDF generation after acceptance

## 17. USP Alignment

The Signature Exchange amplifies StyloBot’s core strengths:

1. Speed with intelligence: import high-signal behavior context before local history is deep.
2. Cross-temporal detection: signatures represent repeated behavior over time, not session snapshots.
3. Zero-PII resilience: matching survives IP/user-agent/location churn because identity is behavioral and multi-vector.
4. Open and operator-controlled: teams keep policy control, transparency, and auditability.
