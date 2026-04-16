```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.4 (25E246) [Darwin 25.4.0]
Apple M5, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  Job-YFEFPZ : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a
  ShortRun   : .NET 10.0.5 (10.0.5, 10.0.526.15411), Arm64 RyuJIT armv8.0-a

WarmupCount=3  

```
| Method                                          | Job        | IterationCount | LaunchCount | Mean        | Error        | StdDev     | Gen0   | Allocated |
|------------------------------------------------ |----------- |--------------- |------------ |------------:|-------------:|-----------:|-------:|----------:|
| &#39;Encode 10 requests (small session)&#39;            | Job-YFEFPZ | 10             | Default     |   615.34 ns |    26.201 ns |  13.703 ns | 0.0277 |    2840 B |
| &#39;Encode 50 requests (medium session)&#39;           | Job-YFEFPZ | 10             | Default     | 1,775.18 ns |    40.956 ns |  24.372 ns | 0.0629 |    6576 B |
| &#39;Encode 200 requests (large session)&#39;           | Job-YFEFPZ | 10             | Default     | 5,454.59 ns |    56.592 ns |  29.599 ns | 0.1144 |   11768 B |
| &#39;Encode 50 requests + fingerprint&#39;              | Job-YFEFPZ | 10             | Default     | 1,768.37 ns |    37.507 ns |  22.320 ns | 0.0629 |    6576 B |
| &#39;Cosine similarity (118-dim)&#39;                   | Job-YFEFPZ | 10             | Default     |    57.53 ns |     0.221 ns |   0.116 ns |      - |         - |
| &#39;Velocity computation (118-dim)&#39;                | Job-YFEFPZ | 10             | Default     |    48.21 ns |     4.023 ns |   2.661 ns | 0.0052 |     528 B |
| &#39;Velocity magnitude (118-dim)&#39;                  | Job-YFEFPZ | 10             | Default     |   116.76 ns |     3.057 ns |   2.022 ns | 0.0051 |     528 B |
| &#39;Maturity computation (50 requests)&#39;            | Job-YFEFPZ | 10             | Default     |   235.50 ns |    10.233 ns |   6.768 ns | 0.0079 |     824 B |
| &#39;Full pipeline: encode + similarity + velocity&#39; | Job-YFEFPZ | 10             | Default     | 2,645.94 ns |   107.073 ns |  70.822 ns | 0.0954 |    9944 B |
| &#39;Encode 10 requests (small session)&#39;            | ShortRun   | 3              | 1           |   631.31 ns |   539.702 ns |  29.583 ns | 0.0277 |    2840 B |
| &#39;Encode 50 requests (medium session)&#39;           | ShortRun   | 3              | 1           | 1,949.79 ns | 2,036.893 ns | 111.649 ns | 0.0629 |    6576 B |
| &#39;Encode 200 requests (large session)&#39;           | ShortRun   | 3              | 1           | 5,765.24 ns | 6,663.167 ns | 365.231 ns | 0.1144 |   11768 B |
| &#39;Encode 50 requests + fingerprint&#39;              | ShortRun   | 3              | 1           | 1,786.40 ns |    10.464 ns |   0.574 ns | 0.0629 |    6576 B |
| &#39;Cosine similarity (118-dim)&#39;                   | ShortRun   | 3              | 1           |    57.68 ns |     3.426 ns |   0.188 ns |      - |         - |
| &#39;Velocity computation (118-dim)&#39;                | ShortRun   | 3              | 1           |    47.16 ns |    20.886 ns |   1.145 ns | 0.0052 |     528 B |
| &#39;Velocity magnitude (118-dim)&#39;                  | ShortRun   | 3              | 1           |   114.68 ns |     6.540 ns |   0.358 ns | 0.0051 |     528 B |
| &#39;Maturity computation (50 requests)&#39;            | ShortRun   | 3              | 1           |   229.79 ns |   106.666 ns |   5.847 ns | 0.0079 |     824 B |
| &#39;Full pipeline: encode + similarity + velocity&#39; | ShortRun   | 3              | 1           | 2,546.50 ns | 1,211.115 ns |  66.385 ns | 0.0954 |    9944 B |
