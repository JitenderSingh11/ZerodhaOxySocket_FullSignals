using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    /// <summary>
    /// Dedicated pipeline for order-related processing.
    /// Partitioned by token to preserve per-instrument ordering while allowing parallelism across instruments.
    /// </summary>
    public static class OrderPipeline
    {
        private static readonly int Partitions;
        private static readonly int ConsumersPerPartition;
        private static readonly int ChannelCapacity;
        private static readonly Channel<TickData>[] _channels;
        private static readonly Task[] _consumerTasks;

        // Counters
        private static long _totalEnqueued = 0;
        private static long _totalDropped = 0;
        public static long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);
        public static long TotalDropped => Interlocked.Read(ref _totalDropped);

        private static bool _started = false;

        static OrderPipeline()
        {
            var cfg = Config.Current.OrderPipeline;
            Partitions = cfg.Partitions > 0 ? cfg.Partitions : Math.Max(1, Environment.ProcessorCount / 4);
            ChannelCapacity = cfg.ChannelCapacity > 0 ? cfg.ChannelCapacity : 50000;
            ConsumersPerPartition = cfg.ConsumersPerPartition > 0 ? cfg.ConsumersPerPartition : 2;

            _channels = new Channel<TickData>[Partitions];
            _consumerTasks = new Task[Partitions * ConsumersPerPartition];

            for (int i = 0; i < Partitions; i++)
            {
                var opts = new BoundedChannelOptions(ChannelCapacity)
                {
                    SingleReader = false,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest // Prioritize newest ticks
                };
                _channels[i] = Channel.CreateBounded<TickData>(opts);
                for (int j = 0; j < ConsumersPerPartition; j++)
                {
                    _consumerTasks[i * ConsumersPerPartition + j] = StartConsumer(_channels[i].Reader);
                }
            }
        }

        public static void Start()
        {
            if (_started) return;
            _started = true;
            _ = Partitions; // touch to ensure static ctor executed
        }

        public static bool EnqueueOrder(TickData t)
        {
            if (t == null) return false;
            var idx = (int)(t.InstrumentToken % (uint)Partitions);
            bool ok = _channels[idx].Writer.TryWrite(t);
            if (ok) Interlocked.Increment(ref _totalEnqueued);
            else Interlocked.Increment(ref _totalDropped);
            return ok;
        }

        public static async Task<bool> EnqueueOrderAsync(TickData t)
        {
            if (t == null) return false;
            var idx = (int)(t.InstrumentToken % (uint)Partitions);
            bool ok = await _channels[idx].Writer.WaitToWriteAsync();
            if (ok)
            {
                await _channels[idx].Writer.WriteAsync(t);
                Interlocked.Increment(ref _totalEnqueued);
            }
            else
            {
                Interlocked.Increment(ref _totalDropped);
            }
            return ok;
        }

        public static Task<bool> EnqueueOrderBatchAsync(List<TickData> batch)
        {
            if (batch == null || batch.Count == 0) return Task.FromResult(true);

            foreach (var t in batch)
            {
                var idx = (int)(t.InstrumentToken % (uint)Partitions);
                if (_channels[idx].Writer.TryWrite(t))
                {
                    Interlocked.Increment(ref _totalEnqueued);
                }
                else
                {
                    Interlocked.Increment(ref _totalDropped);
                    // We return false on the first dropped tick.
                    // The caller might want to know that not all ticks were enqueued.
                    return Task.FromResult(false);
                }
            }

            return Task.FromResult(true);
        }

        private static async Task StartConsumer(ChannelReader<TickData> reader)
        {
            await Task.Run(async () =>
            {
                await foreach (var tick in reader.ReadAllAsync())
                {
                    try
                    {
                        await TickHub.Instance.ProcessSignalOrderAsync(tick, Guid.Empty);
                    }
                    catch (Exception ex)
                    {
                        await SignalDiagnostics.RejectAsync(tick.InstrumentToken, InstrumentCatalog.ResolveName(tick.InstrumentToken) ?? "", DateTime.UtcNow, "OrderPipeline consumer error: " + ex.Message);
                    }
                }
            });
        }

        public static async Task StopAsync()
        {
            try
            {
                foreach (var ch in _channels)
                    ch.Writer.Complete();

                await Task.WhenAll(_consumerTasks).ConfigureAwait(false);
            }
            catch { }
        }
    }
}
