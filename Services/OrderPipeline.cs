using System;
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
        private static readonly int Partitions = Math.Max(1, Environment.ProcessorCount / 2);
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
            _channels = new Channel<TickData>[Partitions];
            _consumerTasks = new Task[Partitions];

            for (int i = 0; i < Partitions; i++)
            {
                var opts = new BoundedChannelOptions(10000)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropWrite
                };
                _channels[i] = Channel.CreateBounded<TickData>(opts);
                _consumerTasks[i] = StartConsumer(_channels[i].Reader);
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

        private static Task StartConsumer(ChannelReader<TickData> reader)
        {
            return Task.Run(async () =>
            {
                await foreach (var tick in reader.ReadAllAsync())
                {
                    try
                    {
                        // Process signal/order in dedicated pipeline
                        TickHub.Instance.ProcessSignalOrder(tick, Guid.Empty);
                    }
                    catch (Exception ex)
                    {
                        SignalDiagnostics.Reject(tick.InstrumentToken, InstrumentCatalog.ResolveName(tick.InstrumentToken) ?? "", DateTime.UtcNow, "OrderPipeline consumer error: " + ex.Message);
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
