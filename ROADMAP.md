# StyloBot Roadmap

Features planned for future releases. Vote on what matters to you by adding a thumbs up reaction to the [GitHub Discussion](https://github.com/scottgal/stylobot/discussions).

## Detection

- [ ] **ThreatFox JA3 integration** - Match TLS fingerprints against known malicious JA3 hashes from ThreatFox feed
- [ ] **Content inspection detector** - Analyze response bodies for data exfiltration patterns (scraping detection)
- [ ] **QUIC/HTTP3 deep fingerprinting** - Extract QUIC transport parameters for client fingerprinting
- [ ] **Prompt injection detector** - Detect LLM prompt injection attempts in request bodies
- [ ] **GraphQL abuse detection** - Detect query depth attacks, batching abuse, introspection probing
- [ ] **WebSocket behavioral analysis** - Track message patterns, timing, payload shapes for WS connections

## LLM

- [ ] **Fallback chain orchestration** - Primary → fallback → local. Auto-degrade when budget exceeded
- [ ] **Per-use-case routing** - Different models for classification vs naming vs intent vs cluster description
- [ ] **Budget controls** - Max requests/hour, max cost/day with automatic degradation
- [ ] **LLM-generated policies** - Natural language policy creation ("block scrapers from China after 10pm")
- [ ] **Escalation replay** - Full slow-path detector rerun on LFU-cached request cohorts at session boundary

## Dashboard

- [ ] **Embedded CLI dashboard** - Non-AOT `stylobot-full` binary with /_stylobot web dashboard
- [ ] **Radar chart session animation** - Step through session behavioral vectors over time
- [ ] **Endpoint policy dropdown** - Per-endpoint policy selection (FOSS view-only, commercial live edit)
- [ ] **Approval form** - Fingerprint approval with locked dimensions directly from dashboard
- [ ] **Config editor** - Monaco YAML editor for live policy editing (commercial)

## CLI

- [ ] **Spectre.Console TUI improvements** - Throughput gauges, input/output rates, session counters
- [ ] **Certificate management** - `stylobot certs renew` for automatic Let's Encrypt
- [ ] **Config validation** - `stylobot check` to validate appsettings.json before deploying

## Distribution

- [ ] **winget** - Windows Package Manager manifest (`winget install stylobot`)
- [ ] **apt/snap** - Debian/Ubuntu package repository
- [ ] **Windows Authenticode signing** - Code signing certificate for Windows binaries
- [ ] **macOS notarization** - Apple Developer Program notarization for Gatekeeper
- [ ] **Homebrew core** - Submit to homebrew-core for `brew install stylobot` without tap

## Integration

- [ ] **Signature Exchange** - Federated zero-PII bot signature sharing between StyloBot nodes
- [ ] **AbuseIPDB reporting** - Automatic reporting of confirmed malicious IPs
- [ ] **Cloudflare Workers** - Edge detection via Cloudflare Workers (client-side signals)
- [ ] **Kubernetes operator** - CRD-based fleet management for multi-gateway deployments
- [ ] **SAML 2.0 + SCIM** - Enterprise SSO with user provisioning

## Commercial

- [ ] **Fleet dashboard** - Multi-gateway monitoring with aggregated metrics
- [ ] **Redis backplane** - Multi-node coordination for distributed deployments
- [ ] **Staged rollout orchestrator** - Gradual policy rollout across gateway fleet
- [ ] **Compliance exports** - Scheduled reports for audit and compliance
- [ ] **Live LLM budget dashboard** - Real-time cost tracking per provider with alerts

## Architecture

- [ ] **Response PII masking** - Strip sensitive data from proxied responses for detected bots
- [ ] **Multi-YARP pipeline** - Three-header model for detection-once, policy-at-every-hop
- [ ] **pgvector HNSW similarity** - Sub-millisecond session vector similarity search at scale
- [ ] **Async SQLite operations** - Non-blocking database operations for all stores
