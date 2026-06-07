using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    public static class TickPipeline
    {
        private static readonly int Partitions;
        private static readonly int ConsumersPerPartition;
        private static readonly int ChannelCapacity;
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
            var cfg = Config.Current.TickPipeline;
            Partitions = cfg.Partitions > 0 ? cfg.Partitions : Math.Max(1, Environment.ProcessorCount / 2);
            ChannelCapacity = cfg.ChannelCapacity > 0 ? cfg.ChannelCapacity : 300000;
            ConsumersPerPartition = cfg.ConsumersPerPartition > 0 ? cfg.ConsumersPerPartition : 4;

            _channels = new Channel<TickData>[Partitions];
            _consumerTasks = new Task[Partitions * ConsumersPerPartition];

            for (int i = 0; i < Partitions; i++)
            {
                var opts = new BoundedChannelOptions(ChannelCapacity)
                {
                    SingleReader = false, // Allow multiple readers
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest
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

        private static Task StartConsumer(ChannelReader<TickData> reader)
        {
            return Task.Run(async () =>
            {
                // OPTIMIZED: Larger pre-allocated batch buffer to reduce allocations
                var batch = new List<TickData>(2000);
                
                // Pre-allocate dictionary for grouping to avoid repeated allocations
                var groupedByToken = new Dictionary<uint, List<TickData>>(16);
                
                while (await reader.WaitToReadAsync())
                {
                    // Read all available ticks in one shot (non-blocking burst read)
                    batch.Clear();
                    while (reader.TryRead(out var tick) && batch.Count < 2000)
                    {
                        batch.Add(tick);
                    }

                    if (batch.Count == 0) continue;

                    try
                    {
                        // OPTIMIZED: Group by token without LINQ (zero-allocation hot path)
                        groupedByToken.Clear();
                        for (int i = 0; i < batch.Count; i++)
                        {
                            var tick = batch[i];
                            if (!groupedByToken.TryGetValue(tick.InstrumentToken, out var tokenBatch))
                            {
                                tokenBatch = new List<TickData>(100);
                                groupedByToken[tick.InstrumentToken] = tokenBatch;
                            }
                            tokenBatch.Add(tick);
                        }

                        // Process each instrument's ticks in parallel (key optimization)
                        var tasks = new List<Task>(groupedByToken.Count);
                        foreach (var kvp in groupedByToken)
                        {
                            var instrumentToken = kvp.Key;
                            var ticksForInstrument = kvp.Value;
                            
                            // Spawn parallel processing per instrument
                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    // Process all ticks for this instrument
                                    await TickHub.Instance.ProcessTickBatchForInstrumentAsync(instrumentToken, ticksForInstrument);
                                    
                                    // Check if any tick needs order pipeline routing
                                    if (OrderManager.Instance.HasPlacedPositionForInstrument(instrumentToken))
                                    {
                                        if (!await OrderPipeline.EnqueueOrderBatchAsync(ticksForInstrument))
                                        {
                                            await SignalDiagnostics.WarnAsync(instrumentToken, 
                                                InstrumentCatalog.ResolveName(instrumentToken) ?? instrumentToken.ToString(), 
                                                DateTime.UtcNow, 
                                                $"[OrderPipeline] Dropped {ticksForInstrument.Count} ticks");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    await SignalDiagnostics.RejectAsync(instrumentToken, 
                                        InstrumentCatalog.ResolveName(instrumentToken) ?? instrumentToken.ToString(), 
                                        DateTime.UtcNow, 
                                        $"Instrument batch error: {ex.Message}");
                                }
                            }));
                        }

                        // Wait for all instruments to finish processing
                        await Task.WhenAll(tasks);
                    }
                    catch (Exception ex)
                    {
                        await SignalDiagnostics.RejectAsync(0, "TickPipeline", DateTime.UtcNow, 
                            $"Pipeline consumer batch error: {ex.Message}");
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
