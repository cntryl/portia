```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.6.2 (25G83) [Darwin 25.6.0]
Apple M5, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.11, 10.0.1126.37416), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.11 (10.0.11, 10.0.1126.37416), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                | Mean       | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------- |-----------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| NoBehaviors           |   123.5 ns | 525.13 ns | 28.78 ns |  1.03 |    0.28 |      - |         - |          NA |
| OneBehavior           |   152.2 ns | 344.95 ns | 18.91 ns |  1.27 |    0.27 | 0.0381 |     320 B |          NA |
| FiveBehaviors         |   279.8 ns |  29.92 ns |  1.64 ns |  2.34 |    0.42 | 0.0877 |     736 B |          NA |
| AsyncYieldingBehavior | 2,208.9 ns | 294.34 ns | 16.13 ns | 18.47 |    3.29 | 0.1297 |    1088 B |          NA |
