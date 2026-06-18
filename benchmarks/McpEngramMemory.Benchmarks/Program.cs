using BenchmarkDotNet.Running;

// Entry point: BenchmarkSwitcher discovers every [*Benchmarks] class in this
// assembly so individual benchmarks can be selected with --filter, e.g.:
//   dotnet run -c Release --project benchmarks/McpEngramMemory.Benchmarks -- --filter '*Vector*'
//   dotnet run -c Release --project benchmarks/McpEngramMemory.Benchmarks -- --filter '*' --job short
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

// Needed so typeof(Program) resolves under top-level statements.
public partial class Program { }
