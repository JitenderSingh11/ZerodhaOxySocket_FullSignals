using System;

namespace BenchmarkSuite1
{
    internal class Program
    {
        static void Main(string[] args)
        {
#if BENCHMARK
            var _ = BenchmarkRunner.Run(typeof(Program).Assembly);
#endif
        }
    }
}
