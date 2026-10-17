using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using BlazorBarcodeScanner.Benchmarks;

// A quick correctness pass before measuring: a benchmark that decodes nothing measures nothing.
Verification.Run();

BenchmarkSwitcher.FromAssembly(typeof(DecoderBenchmarks).Assembly)
    .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator));
