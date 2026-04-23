# YAML-Driven Benchmark Harness Design

**Date:** 2026-04-22
**Status:** Approved
**Scope:** Pluggable BenchmarkDotNet harness for detector tuning and regression guarding

---

## Problem

The existing `IndividualDetectorBenchmarks.cs` has 30+ hand-coded methods with hard-coded scenarios. Adding a new detector or scenario means editing C# code. There's no way to define thresholds for regression checking, no tag-based filtering, and no way to pre-populate blackboard signals to simulate wave dependencies.

## Solution

YAML-driven benchmark scenarios in `*.benchmark.yaml` files. A generic BenchmarkDotNet runner class loads all scenarios at runtime via `[ParamsSource]`, constructs `BlackboardState` from the YAML, resolves the named detector from DI, and runs it. One C# class benchmarks any detector with any scenario. Thresholds in YAML enable automated regression checks in CI.

---

## YAML Scenario Schema

Files live in `Mostlylucid.BotDetection.Benchmarks/Scenarios/` with `*.benchmark.yaml` glob.

```yaml
name: HeaderContributor_HumanChrome          # unique, used as BenchmarkDotNet display name
description: Standard Chrome browser         # human-readable
detector: HeaderContributor                  # matches IContributingDetector.Name
                                             # special value "_pipeline" runs full orchestrator

request:
  method: GET                                # HTTP method
  path: /products/123                        # request path (may include query string)
  protocol: https                            # http or https
  ip: 203.0.113.42                           # client IP
  headers:                                   # flat key-value header dict
    user-agent: "Mozilla/5.0 Chrome/120.0"
    accept: "text/html"
    accept-language: "en-US,en;q=0.9"

signals:                                     # pre-populated blackboard signals
  request.ip.is_datacenter: false            # simulates earlier detector output
  detection.useragent.family: Chrome
  transport.protocol_class: document

thresholds:                                  # optional, for regression mode
  max_mean_ns: 5000                          # fail if mean exceeds this
  max_allocated_bytes: 1024                  # fail if Gen0+ allocation exceeds this
  max_p95_ns: 10000                          # fail if P95 exceeds this

tags: [fast-path, header, human]             # for filtering
```

### Signal value types

YAML scalars are auto-typed: `true`/`false` → bool, numbers → double, strings → string. Lists → `List<object>`.

### Pipeline scenarios

Use `detector: _pipeline` to benchmark the full `BlackboardOrchestrator.DetectAsync()`:

```yaml
name: FullPipeline_HumanBrowsing
detector: _pipeline
request:
  method: GET
  path: /
  ip: 203.0.113.42
  headers:
    user-agent: "Mozilla/5.0 Chrome/120.0"
thresholds:
  max_mean_ns: 500000
tags: [pipeline, human]
```

---

## C# Architecture

### `BenchmarkScenario.cs`- YAML model and state builder

Deserializes `*.benchmark.yaml` into a typed model. Core method:

```csharp
public BlackboardState ToBlackboardState()
```

Constructs `DefaultHttpContext` from `request` fields (reuses the same pattern as `SyntheticHttpContext` from the Api project), populates a `ConcurrentDictionary<string, object>` from `signals`, and returns a complete `BlackboardState`.

`ToString()` returns `Name` for BenchmarkDotNet display.

### `BenchmarkScenarioLoader.cs`- File discovery

```csharp
public static class BenchmarkScenarioLoader
{
    public static IReadOnlyList<BenchmarkScenario> LoadAll(string scenarioDir);
    public static IReadOnlyList<BenchmarkScenario> LoadByTag(string scenarioDir, string tag);
    public static IReadOnlyList<BenchmarkScenario> LoadByDetector(string scenarioDir, string detectorName);
}
```

Globs `**/*.benchmark.yaml`, deserializes with YamlDotNet, returns sorted by detector name then scenario name.

### `DetectorBenchmarkRunner.cs`- Individual detector benchmarks

```csharp
[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 10)]
public class DetectorBenchmarkRunner
{
    [ParamsSource(nameof(Scenarios))]
    public BenchmarkScenario Scenario { get; set; }

    private IContributingDetector _detector;
    private BlackboardState _state;

    public IEnumerable<BenchmarkScenario> Scenarios =>
        BenchmarkScenarioLoader.LoadAll("Scenarios")
            .Where(s => s.DetectorName != "_pipeline");

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddBotDetection();
        var provider = services.BuildServiceProvider();

        _detector = provider.GetServices<IContributingDetector>()
            .Single(d => d.Name == Scenario.DetectorName);
        _state = Scenario.ToBlackboardState();
    }

    [Benchmark]
    public Task<IReadOnlyList<DetectionContribution>> Detect()
        => _detector.ContributeAsync(_state);
}
```

### `PipelineBenchmarkRunner.cs`- Full orchestrator benchmarks

Same pattern but filters to `detector: _pipeline` scenarios and calls `BlackboardOrchestrator.DetectAsync(httpContext)`.

### `RegressionChecker.cs`- Post-run threshold validation

After BenchmarkDotNet finishes, parses `BenchmarkDotNet.Artifacts/results/*.json`. For each scenario that has `thresholds`, compares actual results against limits. Prints violations and returns exit code 1 if any exceeded.

```csharp
public static class RegressionChecker
{
    public static int Check(string resultsDir, IReadOnlyList<BenchmarkScenario> scenarios);
}
```

### `Program.cs`- Entry point with CLI

```csharp
if (args.Contains("--regression"))
{
    // Run BenchmarkDotNet, then check thresholds
    BenchmarkRunner.Run<DetectorBenchmarkRunner>();
    BenchmarkRunner.Run<PipelineBenchmarkRunner>();
    var scenarios = BenchmarkScenarioLoader.LoadAll("Scenarios");
    var exitCode = RegressionChecker.Check("BenchmarkDotNet.Artifacts/results", scenarios);
    return exitCode;
}
else
{
    // Normal BenchmarkDotNet interactive mode
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    return 0;
}
```

---

## CLI Usage

```bash
# Tuning: run all detector benchmarks
dotnet run -c Release -- --filter "DetectorBenchmarkRunner*"

# Tuning: specific detector
dotnet run -c Release -- --filter "*HeaderContributor*"

# Tuning: full pipeline
dotnet run -c Release -- --filter "PipelineBenchmarkRunner*"

# Regression: run all + check thresholds (CI)
dotnet run -c Release -- --regression

# List available scenarios
dotnet run -c Release -- --list
```

---

## Starter Scenarios

Ship with scenarios covering the most performance-critical detectors:

### Fast-path detectors (should be <5µs each)
- `header-human-chrome.benchmark.yaml`- HeaderContributor with standard browser
- `header-bot-curl.benchmark.yaml`- HeaderContributor with curl UA
- `useragent-human.benchmark.yaml`- UserAgentContributor with Chrome
- `useragent-known-bot.benchmark.yaml`- UserAgentContributor with Googlebot
- `ip-residential.benchmark.yaml`- IpContributor with residential IP
- `ip-datacenter.benchmark.yaml`- IpContributor with datacenter signals
- `fastpath-reputation-hit.benchmark.yaml`- FastPathReputationContributor with cached signature
- `behavioral-normal.benchmark.yaml`- BehavioralContributor with typical human patterns

### Fingerprint detectors
- `tls-chrome.benchmark.yaml`- TlsFingerprintContributor with Chrome JA3
- `tls-bot.benchmark.yaml`- TlsFingerprintContributor with Python requests JA3
- `http2-normal.benchmark.yaml`- Http2FingerprintContributor with standard settings
- `multilayer-correlation.benchmark.yaml`- MultiLayerCorrelation with full signal set

### Session/advanced detectors
- `session-vector-short.benchmark.yaml`- SessionVectorContributor with 5-request session
- `inconsistency-mismatch.benchmark.yaml`- InconsistencyDetector with TLS/UA mismatch
- `heuristic-features.benchmark.yaml`- HeuristicFeatureExtractor with full feature set

### Pipeline benchmarks
- `pipeline-human-browsing.benchmark.yaml`- Full pipeline, human Chrome
- `pipeline-obvious-bot.benchmark.yaml`- Full pipeline, curl bot
- `pipeline-ai-scraper.benchmark.yaml`- Full pipeline, GPTBot

---

## What Changes in Existing Code

- **`Mostlylucid.BotDetection.Benchmarks/`**- New files added alongside existing benchmark classes. Existing `IndividualDetectorBenchmarks.cs` and `DetectionPipelineBenchmarks.cs` are left as-is (they still work, just aren't YAML-driven). They can be removed later once the YAML harness proves out.
- **No changes to the core detection library.**

---

## Tuning Workflow

After running benchmarks, the output tells you which detectors are hot. The tuning cycle:

1. Run `dotnet run -c Release -- --filter "*HeaderContributor*"` 
2. Read mean time + allocation from BenchmarkDotNet table
3. Profile with `--profiler ETW` or `--profiler diagnoser` for allocation details
4. Make code changes to the detector
5. Re-run the same benchmark
6. Compare- BenchmarkDotNet's `--join` flag overlays runs for comparison
7. If happy, update `thresholds` in the YAML to lock in the new baseline