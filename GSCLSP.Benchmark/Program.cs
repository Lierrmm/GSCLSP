using BenchmarkDotNet.Running;

namespace GSCLSP.Benchmark;

internal class Program
{
    static void Main(string[] args)
    {
        var _ = BenchmarkRunner.Run(typeof(Program).Assembly);
    }
}
