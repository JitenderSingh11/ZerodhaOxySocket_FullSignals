#if BENCHMARK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

// This benchmark measures enqueue + background batching throughput for
// a single-writer vs a partitioned-writer design. It uses simulated
// "DB writes" (simulated latency) so it is safe to run in CI/dev.
namespace ZerodhaOxySocket.Benchmarks
{
    [SimpleJob(RuntimeMoniker.HostProcess, 1, 1, 3)]
    [MemoryDiagnoser]
    public class TickWriterPartitionBenchmark
    {
        private List<Tick> _ticks = new();
        private const int TickCount = 50000; // reduced to keep runs reasonable

        // Sweep parameters
        [Params(1, 2, 4)]
        public int Partitions { get; set; }

        [Params(250, 1000)]
        public int BatchSize { get; set; }

        // Simulated DB latency parameters (ms)
        [Params(5, 20)]
        public int BaseMs { get; set; } = 5; // base per-batch overhead

        [Params(0.02, 0.05)]
        public double PerRowMs { get; set; } = 0.02; // per-row cost in ms

        [GlobalSetup]
        public void Setup()
        {
            var rnd = new Random(42);
            _ticks = new List<Tick>(TickCount);
            for (int i = 0; i < TickCount; i++)
            {
                _ticks.Add(new Tick { InstrumentToken = (uint)(1 + rnd.Next(0, 200)), LastPrice = rnd.NextDouble() * 1000.0, TickTime = DateTime.UtcNow });
            }
        }

        [Benchmark(Baseline = true, Description = "SingleWriter")]
        public async Task SingleWriter_Run()
        {
            var writer = new SingleWriter(batchSize: BatchSize, perRowMs: PerRowMs, baseMs: BaseMs);
            foreach (var t in _ticks)
                writer.Enqueue(t);
            await writer.StopAndFlushAsync().ConfigureAwait(false);
        }

        [Benchmark(Description = "PartitionedWriter")]
        public async Task PartitionedWriter_Run()
        {
            var writer = new PartitionedWriter(Partitions, batchSize: BatchSize, perChannelCapacity: 50_000, perRowMs: PerRowMs, baseMs: BaseMs);
            foreach (var t in _ticks)
                writer.Enqueue(t);
            await writer.StopAndFlushAsync().ConfigureAwait(false);
        }

        // Simple Tick representation used only by the benchmark
        public class Tick
        {
            public uint InstrumentToken { get; set; }
            public double LastPrice { get; set; }
            public DateTime TickTime { get; set; }
        }

        // Single background writer that batches ticks and "writes" them.
        // The "DB write" is simulated with Task.Delay to reflect bulk insert cost.
        private class SingleWriter
        {
            private readonly Channel<Tick> _ch;
            private readonly Task _consumer;
            private readonly int _batchSize;
            private readonly CancellationTokenSource _cts = new();
            private readonly double _perRowMs;
            private readonly int _baseMs;

            public SingleWriter(int batchSize = 1000, int capacity = 100_000, double perRowMs = 0.02, int baseMs = 5)
            {
                _batchSize = batchSize;
                _perRowMs = perRowMs;
                _baseMs = baseMs;
                var opts = new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                    SingleWriter = false
                };
                _ch = Channel.CreateBounded<Tick>(opts);
                _consumer = Task.Run(ConsumerLoop);
            }

            public bool Enqueue(Tick t) => _ch.Writer.TryWrite(t);
            private async Task ConsumerLoop()
            {
                var reader = _ch.Reader;
                var batch = new List<Tick>(_batchSize);
                try
                {
                    while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                    {
                        while (reader.TryRead(out var tick))
                        {
                            batch.Add(tick);
                            if (batch.Count >= _batchSize)
                            {
                                // simulate DB bulk insert latency
                                await SimulatedWrite(batch).ConfigureAwait(false);
                                batch.Clear();
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    if (batch.Count > 0)
                        await SimulatedWrite(batch).ConfigureAwait(false);
                }
            }

            private async Task SimulatedWrite(IList<Tick> batch)
            {
                double ms = _baseMs + batch.Count * _perRowMs;
                int delay = Math.Max(1, (int)Math.Round(ms));
                await Task.Delay(delay).ConfigureAwait(false);
            }

            public async Task StopAndFlushAsync()
            {
                _ch.Writer.Complete();
                try
                {
                    await _consumer.ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        // Partitioned writer: N channels, one consumer per partition.
        private class PartitionedWriter
        {
            private readonly Channel<Tick>[] _channels;
            private readonly Task[] _consumers;
            private readonly int _partitions;
            private readonly int _batchSize;
            private readonly double _perRowMs;
            private readonly int _baseMs;

            public PartitionedWriter(int partitions, int batchSize = 1000, int perChannelCapacity = 50_000, double perRowMs = 0.02, int baseMs = 5)
            {
                _partitions = Math.Max(1, partitions);
                _batchSize = batchSize;
                _perRowMs = perRowMs;
                _baseMs = baseMs;
                _channels = new Channel<Tick>[_partitions];
                _consumers = new Task[_partitions];
                for (int i = 0; i < _partitions; i++)
                {
                    var opts = new BoundedChannelOptions(perChannelCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropWrite,
                        SingleReader = true,
                        SingleWriter = false
                    };
                    _channels[i] = Channel.CreateBounded<Tick>(opts);
                    int idx = i;
                    _consumers[i] = Task.Run(() => ConsumerLoop(_channels[idx].Reader));
                }
            }

            public bool Enqueue(Tick t)
            {
                var idx = (int)(t.InstrumentToken % (uint)_partitions);
                return _channels[idx].Writer.TryWrite(t);
            }

            private async Task ConsumerLoop(ChannelReader<Tick> reader)
            {
                var batch = new List<Tick>(_batchSize);
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out var tick))
                    {
                        batch.Add(tick);
                        if (batch.Count >= _batchSize)
                        {
                            await SimulatedWrite(batch).ConfigureAwait(false);
                            batch.Clear();
                        }
                    }
                }

                if (batch.Count > 0)
                    await SimulatedWrite(batch).ConfigureAwait(false);
            }

            private async Task SimulatedWrite(IList<Tick> batch)
            {
                double ms = _baseMs + batch.Count * _perRowMs;
                int delay = Math.Max(1, (int)Math.Round(ms));
                await Task.Delay(delay).ConfigureAwait(false);
            }

            public async Task StopAndFlushAsync()
            {
                foreach (var ch in _channels)
                    ch.Writer.Complete();
                await Task.WhenAll(_consumers).ConfigureAwait(false);
            }
        }
    }
}
#endif