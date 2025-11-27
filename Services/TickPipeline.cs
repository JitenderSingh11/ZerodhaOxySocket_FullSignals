using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    public static class TickPipeline
    {
        private static readonly int Partitions = Math.Max(1, Environment.ProcessorCount / 2);
        private static readonly int ConsumersPerPartition = 2; // Tune as needed
        private static readonly Channel<TickData>[] _channels;
        private static readonly Task[] _consumerTasks;

        // Counters (thread-safe)
        private static long _totalEnqueued = 0;
        private static long _totalDropped = 0;
        public static long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);
        public static long TotalDropped => Interlocked.Read(ref _totalDropped);

        private static bool _started = false;

        static TickPipeline()
        {
            _channels = new Channel<TickData>[Partitions];
            _consumerTasks = new Task[Partitions * ConsumersPerPartition];

            for (int i = 0; i < Partitions; i++)
            {
                var opts = new BoundedChannelOptions(500000)
                {
                    SingleReader = false, // Allow multiple readers
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropWrite
                };
                _channels[i] = Channel.CreateBounded<TickData>(opts);
                for (int j = 0; j < ConsumersPerPartition; j++)
                {
                    _consumerTasks[i * ConsumersPerPartition + j] = StartConsumer(_channels[i].Reader);
                }
            }
        }

        /// <summary>
        /// Explicit start to make pipeline activation deterministic from UI/startup code.
        /// Calling Start will also ensure OrderPipeline is started.
        /// </summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;
            // Ensure underlying order pipeline is initialized
            try { OrderPipeline.Start(); } catch { }
            // Access a field to ensure static ctor has run (it will have run before this call normally)
            _ = Partitions;
        }

        public static bool EnqueueTick(TickData t)
        {
            var idx = (int)(t.InstrumentToken % (uint)Partitions);
            bool ok = _channels[idx].Writer.TryWrite(t);
            if (ok) Interlocked.Increment(ref _totalEnqueued);
            else Interlocked.Increment(ref _totalDropped);
            return ok;
        }

        private static async Task StartConsumer(ChannelReader<TickData> reader)
        {
            await Task.Run(async () =>
            {
                await foreach (var tick in reader.ReadAllAsync())
                {
                    try
                    {
                        await TickHub.Instance.ProcessTickFromPipelineAsync(tick);
                        try
                        {
                            if (OrderManager.Instance.HasOpenPositionForInstrument(tick.InstrumentToken))
                            {
                                if (OrderPipeline.EnqueueOrderAsync != null)
                                {
                                    if (!await OrderPipeline.EnqueueOrderAsync(tick))
                                    {
                                        await SignalDiagnostics.WarnAsync(tick.InstrumentToken, InstrumentCatalog.ResolveName(tick.InstrumentToken) ?? "", DateTime.UtcNow, "OrderPipeline enqueue failed - dropped");
                                    }
                                }
                                else
                                {
                                    if (!OrderPipeline.EnqueueOrder(tick))
                                    {
                                        await SignalDiagnostics.WarnAsync(tick.InstrumentToken, InstrumentCatalog.ResolveName(tick.InstrumentToken) ?? "", DateTime.UtcNow, "OrderPipeline enqueue failed - dropped");
                                    }
                                }
                            }
                        }
                        catch (Exception exOrder)
                        {
                            await SignalDiagnostics.RejectAsync(tick.InstrumentToken, InstrumentCatalog.ResolveName(tick.InstrumentToken) ?? "", DateTime.UtcNow, "Order routing failed: " + exOrder.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        await SignalDiagnostics.RejectAsync(tick.InstrumentToken, "", DateTime.UtcNow, "Pipeline consumer error: " + ex.Message);
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

            // stop order pipeline as well
            try { await OrderPipeline.StopAsync().ConfigureAwait(false); } catch { }
        }
    }
}
