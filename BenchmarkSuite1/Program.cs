using BenchmarkDotNet.Running;
using System;

namespace BenchmarkSuite1
{
    internal class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<TickPipelineBenchmark>();
        }
    }
}
