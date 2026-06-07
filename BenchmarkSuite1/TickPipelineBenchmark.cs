using BenchmarkDotNet.Attributes;
using System;
using System.Threading;
using ZerodhaOxySocket;

namespace BenchmarkSuite1
{
    [MemoryDiagnoser]
    public class TickPipelineBenchmark
    {
        private TickData[] _ticks;
        private int _tickCount = 126_326;
        private int _idx;

        [GlobalSetup]
        public void Setup()
        {
            _ticks = new TickData[_tickCount];
            var rand = new Random(42);
            for (int i = 0; i < _tickCount; i++)
            {
                _ticks[i] = new TickData
                {
                    InstrumentToken = (uint)(rand.Next(2) == 0 ? 99999u : 88888u),
                    LastPrice = rand.NextDouble() * 100,
                    Volume = rand.Next(1000),
                    // Add other fields as needed
                };
            }
            TickPipeline.Start();
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            // If TickPipeline.StopAsync or similar exists, call it here.
            // If not, add a TODO for manual cleanup.
            // TODO: TickPipeline.StopAsync().Wait();
        }

        [Benchmark]
        public void EnqueueSingleTick()
        {
            // Measures the cost of a single enqueue operation.
            // Uses Interlocked to avoid locking or allocation in the hot path.
            int i = Interlocked.Increment(ref _idx) % _tickCount;
            TickPipeline.EnqueueTick(_ticks[i]);
        }

        [Benchmark]
        public void EnqueueBatch()
        {
            // Measures throughput for enqueuing a full batch.
            for (int i = 0; i < _tickCount; i++)
            {
                TickPipeline.EnqueueTick(_ticks[i]);
            }
            // TODO: Optionally wait for consumers to drain the pipeline here.
        }
    }
}