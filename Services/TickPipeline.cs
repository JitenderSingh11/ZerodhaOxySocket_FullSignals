// Services/TickPipeline.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    /// <summary>
    /// Channel-based pipeline:
    ///   socket -> input channel -> dispatcher -> per-instrument channels -> instrument consumer.
    /// Usage:
    ///   TickPipeline.Start();
    ///   TickPipeline.EnqueueTick(tick);
    ///   await TickPipeline.StopAsync();
    /// </summary>
    public static class TickPipeline
    {
        private static readonly Channel<TickData> _inputChannel =
            Channel.CreateUnbounded<TickData>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        private static readonly ConcurrentDictionary<uint, Channel<TickData>> _instrumentChannels =
            new ConcurrentDictionary<uint, Channel<TickData>>();

        private static CancellationTokenSource _cts;
        private static Task _dispatcherTask;

        // Metrics
        private static long _totalEnqueued = 0;
        private static long _totalDropped = 0;
        public static long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);
        public static long TotalDropped => Interlocked.Read(ref _totalDropped);

        // Tunables (move to config.json and wire in startup if desired)
        public static int InstrumentChannelCapacity { get; set; } = 10000;
        public static int DbBatchSize { get; set; } = 1000;
        public static TimeSpan DbFlushInterval { get; set; } = TimeSpan.FromSeconds(2);
        public static TimeSpan MaxAllowedTickDelay { get; set; } = TimeSpan.FromSeconds(8);

        public static void Start(CancellationToken? externalToken = null)
        {
            if (_cts != null) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken ?? CancellationToken.None);
            _dispatcherTask = Task.Run(() => DispatcherLoop(_cts.Token));
            SignalDiagnostics.Info(0, "TickPipeline", DateTime.Now, "Start", "Tick pipeline started");
        }

        public static async Task StopAsync()
        {
            try
            {
                if (_cts == null) return;
                _cts.Cancel();
                _inputChannel.Writer.Complete();
                if (_dispatcherTask != null)
                    await _dispatcherTask.ConfigureAwait(false);
            }
            catch { }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
            SignalDiagnostics.Info(0, "TickPipeline", DateTime.Now, "Stop", "Tick pipeline stopped");
        }

        /// <summary>
        /// Fast non-blocking enqueue for socket thread.
        /// </summary>
        public static bool EnqueueTick(TickData tick)
        {
            if (tick == null) return false;
            Interlocked.Increment(ref _totalEnqueued);
            if (!_inputChannel.Writer.TryWrite(tick))
            {
                Interlocked.Increment(ref _totalDropped);
                return false;
            }
            return true;
        }

        private static async Task DispatcherLoop(CancellationToken ct)
        {
            var reader = _inputChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var tick))
                    {
                        RouteToInstrumentChannel(tick, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(0, "TickPipeline", DateTime.Now, $"Dispatcher failure: {ex.Message}");
            }
        }

        private static void RouteToInstrumentChannel(TickData tick, CancellationToken ct)
        {
            var ch = _instrumentChannels.GetOrAdd(tick.InstrumentToken, tok =>
            {
                var options = new BoundedChannelOptions(InstrumentChannelCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest
                };
                var newCh = Channel.CreateBounded<TickData>(options);
                _ = Task.Run(() => InstrumentConsumerLoop(tok, newCh.Reader, ct), ct);
                return newCh;
            });

            if (!ch.Writer.TryWrite(tick))
            {
                Interlocked.Increment(ref _totalDropped);
                SignalDiagnostics.Warn(tick.InstrumentToken, ResolveNameSafe(tick.InstrumentToken), DateTime.Now,
                    $"Instrument channel full - dropped tick for {tick.InstrumentToken}.");
            }
        }

        private static async Task InstrumentConsumerLoop(uint token, ChannelReader<TickData> reader, CancellationToken ct)
        {
            var batch = new List<TickData>(DbBatchSize);
            DateTime lastFlush = DateTime.UtcNow;
            var name = ResolveNameSafe(token);

            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var tick))
                    {
                        var delay = tick.ReceivedAt.HasValue ? tick.ReceivedAt.Value - tick.TickTime : TimeSpan.Zero;
                        if (delay > MaxAllowedTickDelay)
                        {
                            Interlocked.Increment(ref _totalDropped);
                            SignalDiagnostics.Warn(token, name, tick.TickTime, $"Dropping stale tick delayMs={delay.TotalMilliseconds:F0}");
                            continue;
                        }

                        try
                        {
                            tick.InstrumentName = ResolveNameSafe(tick.InstrumentToken);
                            // 1) Fast in-memory candle update - MUST be non-blocking and cheap.
                            ProcessTickForInstrument(token, tick);

                            // 2) add to DB batch
                            batch.Add(tick);
                            if (batch.Count >= DbBatchSize || (DateTime.UtcNow - lastFlush) >= DbFlushInterval)
                            {
                                var flush = batch.ToArray();
                                batch.Clear();
                                lastFlush = DateTime.UtcNow;

                                // Fire-and-forget DB write
                                _ = Task.Run(() =>
                                {
                                    try
                                    {
                                        DataAccess.InsertTicksBatch(flush); // adapt signature if needed
                                        SignalDiagnostics.Info(token, name, DateTime.Now, "DB", $"Inserted {flush.Length} ticks (batch)");
                                    }
                                    catch (Exception ex)
                                    {
                                        SignalDiagnostics.Reject(token, name, DateTime.Now, $"DB insert failed: {ex.Message}");
                                    }
                                }, ct);
                            }
                        }
                        catch (Exception eProc)
                        {
                            SignalDiagnostics.Reject(token, name, DateTime.Now, $"Tick processing error: {eProc.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* graceful */ }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(token, name, DateTime.Now, $"Instrument consumer failed: {ex.Message}");
            }
            finally
            {
                if (batch.Count > 0)
                {
                    try
                    {
                        DataAccess.InsertTicksBatch(batch);
                        SignalDiagnostics.Info(token, name, DateTime.Now, "DB", $"Inserted {batch.Count} ticks (final)");
                    }
                    catch (Exception ex)
                    {
                        SignalDiagnostics.Reject(token, name, DateTime.Now, $"DB final insert failed: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Replace contents of this with a call to your in-memory candle builder.
        /// Must be fast, synchronous, and non-blocking (no DB or heavy IO).
        /// When a candle closes, raise your existing OnCandleClosed / SignalGate flow.
        /// </summary>
        private static void ProcessTickForInstrument(uint token, TickData tick)
        {
            // Forward to TickHub to run the strategy/candle updates.
            // Note: TickPipeline persists ticks in batches to DB; TickHub will run candle update and signal logic.
            try
            {
                TickHub.ProcessTickFromPipeline(tick);
            }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(token, ResolveNameSafe(token), DateTime.Now, $"Forward to TickHub failed: {ex.Message}");
            }
        }


        private static string ResolveNameSafe(uint token)
        {
            try { return InstrumentCatalog.ResolveName(token) ?? token.ToString(); }
            catch { return token.ToString(); }
        }
    }
}
