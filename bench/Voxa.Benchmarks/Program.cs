using BenchmarkDotNet.Running;

// Run with:  dotnet run -c Release --project bench/Voxa.Benchmarks -- --filter *
// List with: dotnet run -c Release --project bench/Voxa.Benchmarks -- --list flat
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal sealed partial class Program;
