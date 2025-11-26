using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ZerodhaOxySocket.MultiSockets
{
    public class TickWriter
    {
        private readonly Channel<TickData>[] _channels;
        private readonly Task[] _writerTasks;
        private readonly ConcurrentBag<Task> _inflightWrites = new();
        private readonly SemaphoreSlim _writeSemaphore;

        // Metrics
        private long _totalEnqueued = 0;
        private long _totalDropped = 0;
        public long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);
        public long TotalDropped => Interlocked.Read(ref _totalDropped);

        // Bulk write metrics
        private long _totalBulkWrites = 0;
        private long _totalBulkRows = 0;
        private long _totalBulkWriteDurationMs = 0;
        private int _lastBulkBatchSize = 0;

        public long TotalBulkWrites => Interlocked.Read(ref _totalBulkWrites);
        public long TotalBulkRows => Interlocked.Read(ref _totalBulkRows);
        public double AverageBulkBatchSize => TotalBulkWrites > 0 ? (double)TotalBulkRows / TotalBulkWrites : 0.0;
        public double AverageBulkWriteMs => Interlocked.Read(ref _totalBulkWrites) > 0 ? (double)Interlocked.Read(ref _totalBulkWriteDurationMs) / Interlocked.Read(ref _totalBulkWrites) : 0.0;
        public int LastBulkBatchSize => Volatile.Read(ref _lastBulkBatchSize);

        // Tunables
        public const int DefaultInstrumentChannelCapacity = 100000;
        private int _dbBatchSize = 1000;
        public TimeSpan DbFlushInterval { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan MaxAllowedTickDelay { get; set; } = TimeSpan.FromSeconds(8);

        private readonly int _partitions;
        private readonly int _perChannelCapacity;
        private readonly int _maxConcurrentBulkWrites;

        public TickWriter(int capacity = DefaultInstrumentChannelCapacity, int partitions = 0, int maxConcurrentBulkWrites = 2, int dbBatchSize = 1000, int dbFlushIntervalSeconds = 2, int maxAllowedTickDelaySeconds = 8)
        {
            _partitions = partitions > 0 ? partitions : Math.Max(1, Environment.ProcessorCount / 2);
            _perChannelCapacity = Math.Max(1024, capacity / _partitions);
            _maxConcurrentBulkWrites = Math.Max(1, maxConcurrentBulkWrites);
            _dbBatchSize = Math.Max(1, dbBatchSize);
            DbFlushInterval = TimeSpan.FromSeconds(Math.Max(1, dbFlushIntervalSeconds));
            MaxAllowedTickDelay = TimeSpan.FromSeconds(Math.Max(1, maxAllowedTickDelaySeconds));

            _writeSemaphore = new SemaphoreSlim(_maxConcurrentBulkWrites);

            _channels = new Channel<TickData>[_partitions];
            _writerTasks = new Task[_partitions];

            for (int i = 0; i < _partitions; i++)
            {
                var opts = new BoundedChannelOptions(_perChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                    SingleWriter = false
                };
                _channels[i] = Channel.CreateBounded<TickData>(opts);
                int idx = i; // capture
                _writerTasks[i] = Task.Run(() => WriterLoopAsync(idx));
            }
        }

        private async Task WriterLoopAsync(int partitionIndex)
        {
            var reader = _channels[partitionIndex].Reader;
            var batch = new List<TickData>(_dbBatchSize);
            DateTime lastFlush = SessionClock.NowIst();

            try
            {
                while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var tick))
                    {
                        var name = ResolveNameSafe(tick.InstrumentToken);
                        var token = tick.InstrumentToken;

                        var delay = tick.ReceivedAt.HasValue ? tick.ReceivedAt.Value - tick.TickTime : TimeSpan.Zero;
                        if (delay > MaxAllowedTickDelay)
                        {
                            Interlocked.Increment(ref _totalDropped);
                            SignalDiagnostics.Warn(token, name, tick.TickTime, $"Dropping stale tick delayMs={delay.TotalMilliseconds:F0}");
                            //continue;
                        }

                        try
                        {
                            tick.InstrumentName = name;

                            batch.Add(tick);
                            if (batch.Count >= _dbBatchSize || (SessionClock.NowIst() - lastFlush) >= DbFlushInterval)
                            {
                                var flush = batch.ToArray();
                                batch.Clear();
                                lastFlush = SessionClock.NowIst();

                                // Schedule a bulk write limited by semaphore
                                await _writeSemaphore.WaitAsync().ConfigureAwait(false);
                                var writeTask = Task.Run(() =>
                                {
                                    var sw = Stopwatch.StartNew();
                                    try
                                    {
                                        DataAccess.InsertTicksBulk(flush);
                                        SignalDiagnostics.Info(token, name, DateTime.Now, "DB", $"Inserted {flush.Length} ticks (bulk)");

                                        // metrics
                                        Interlocked.Increment(ref _totalBulkWrites);
                                        Interlocked.Add(ref _totalBulkRows, flush.Length);
                                        Interlocked.Add(ref _totalBulkWriteDurationMs, sw.ElapsedMilliseconds);
                                        Interlocked.Exchange(ref _lastBulkBatchSize, flush.Length);
                                    }
                                    catch (Exception ex)
                                    {
                                        SignalDiagnostics.Reject(token, name, DateTime.Now, $"Bulk insert failed: {ex.Message}");
                                    }
                                    finally
                                    {
                                        _writeSemaphore.Release();
                                    }
                                });

                                _inflightWrites.Add(writeTask);
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
                SignalDiagnostics.Reject(0, "TickWriter", SessionClock.NowIst(), $"WriterLoop failed: {ex.Message}");
            }
            finally
            {
                if (batch.Count > 0)
                {
                    try
                    {
                        await _writeSemaphore.WaitAsync().ConfigureAwait(false);
                        var flush = batch.ToArray();
                        var writeTask = Task.Run(() =>
                        {
                            var sw = Stopwatch.StartNew();
                            try
                            {
                                DataAccess.InsertTicksBulk(flush);
                                Interlocked.Increment(ref _totalBulkWrites);
                                Interlocked.Add(ref _totalBulkRows, flush.Length);
                                Interlocked.Add(ref _totalBulkWriteDurationMs, sw.ElapsedMilliseconds);
                                Interlocked.Exchange(ref _lastBulkBatchSize, flush.Length);
                            }
                            catch { }
                            finally { _writeSemaphore.Release(); }
                        });
                        _inflightWrites.Add(writeTask);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Enqueue a tick for database writing. Returns false if dropped (channel full).
        /// </summary>
        public bool EnqueueTick(TickData tick)
        {
            if (tick == null) return false;
            var idx = (int)(tick.InstrumentToken % (uint)_partitions);
            bool ok = _channels[idx].Writer.TryWrite(tick);
            if (ok) Interlocked.Increment(ref _totalEnqueued);
            else Interlocked.Increment(ref _totalDropped);
            return ok;
        }

        private string ResolveNameSafe(uint token)
        {
            try { return InstrumentCatalog.ResolveName(token) ?? token.ToString(); }
            catch { return token.ToString(); }
        }

        public async Task StopAsync()
        {
            try
            {
                foreach (var ch in _channels) ch.Writer.Complete();
                await Task.WhenAll(_writerTasks).ConfigureAwait(false);
            }
            catch { }

            try
            {
                var writes = _inflightWrites.ToArray();
                await Task.WhenAll(writes).ConfigureAwait(false);
            }
            catch { }
        }
    }
}
