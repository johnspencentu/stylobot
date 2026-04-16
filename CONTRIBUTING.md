# Contributing to StyloBot

Thanks for your interest in contributing to StyloBot! This document covers the basics.

## Getting Started

1. Fork and clone the repo
2. Install [.NET SDK 10.0](https://dotnet.microsoft.com/download)
3. Build: `dotnet build mostlylucid.stylobot.sln`
4. Run tests: `dotnet test`
5. Run the demo: `dotnet run --project Mostlylucid.BotDetection.Demo`

## Development Guidelines

- **No hard-coded site-specific exceptions.** StyloBot is a detection product - the fix is always to make detection *correct*, not to add workarounds or allowlists.
- **All detection improvements must be generic** - based on protocol specs (W3C, RFCs), not site-specific paths or domains.
- **No magic numbers in detectors** - all confidence, weight, and threshold values come from YAML manifest `defaults.parameters` via `GetParam<T>()`.
- **Zero PII** - raw IP addresses and user agents must never be persisted. Use HMAC-SHA256 signatures.

## Adding a Detector

Every new detector touches exactly 5 files. See `CLAUDE.md` for the full checklist, or use `Http3FingerprintContributor` as a reference implementation:

1. C# class in `Orchestration/ContributingDetectors/`
2. YAML manifest in `Orchestration/Manifests/detectors/`
3. Signal keys in `Models/DetectionContext.cs`
4. DI registration in `Extensions/ServiceCollectionExtensions.cs`
5. Narrative builder entries in `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`

## Pull Requests

- Keep PRs focused - one feature or fix per PR
- Include tests for new detection logic
- Run `dotnet test` before submitting
- Update detector YAML manifests if you change default weights or thresholds
- Update `CHANGELOG.md` under the `[Unreleased]` section

## Running Tests

```bash
# All tests
dotnet test

# Specific project
dotnet test Mostlylucid.BotDetection.Test/
dotnet test Mostlylucid.BotDetection.Orchestration.Tests/

# Single test class
dotnet test --filter "FullyQualifiedName~UserAgentDetectorTests"
```

## Reporting Issues

Open an issue on [GitHub](https://github.com/scottgal/stylobot/issues). Include:

- What you expected vs what happened
- Steps to reproduce
- Relevant log output or detection signals
- .NET version and OS

## License

By contributing, you agree that your contributions will be released under [The Unlicense](LICENSE).
