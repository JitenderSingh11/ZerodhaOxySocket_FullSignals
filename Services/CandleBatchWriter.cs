using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dapper;

namespace ZerodhaOxySocket.Services
{
    /// <summary>
    /// High-performance batched candle database writer to prevent connection pool exhaustion.
    /// Uses Channel&lt;T&gt; for optimal throughput and backpressure handling.
    /// </summary>
    public sealed class CandleBatchWriter : IDisposable
    {
        private readonly Channel<CandleUpdate> _channel;
        private readonly Task _processorTask;
        private readonly CancellationTokenSource _cts = new();
        
        // Configuration (can be moved to config.json)
        private readonly TimeSpan _flushInterval;
        private readonly int _maxBatchSize;
        private readonly int _maxRetries;
        
        // Metrics
        private long _totalQueued;
        private long _totalProcessed;
        private long _totalFailed;
        private long _totalDeduplicated;
        private DateTime _lastFlush = DateTime.UtcNow;
        private readonly object _statsLock = new();

        private sealed class CandleUpdate
        {
            public Candle Candle { get; init; }
            public uint Token { get; init; }
            public string Name { get; init; }
            public DateTime QueuedAt { get; init; }
        }

        public CandleBatchWriter(
            TimeSpan? flushInterval = null, 
            int maxBatchSize = 500,
            int channelCapacity = 10000,
            int maxRetries = 3)
        {
            _flushInterval = flushInterval ?? TimeSpan.FromSeconds(2);
            _maxBatchSize = maxBatchSize;
            _maxRetries = maxRetries;
            
            // Use bounded channel with backpressure handling
            var options = new BoundedChannelOptions(channelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // Drop oldest if channel full
                SingleReader = true,
                SingleWriter = false
            };
            _channel = Channel.CreateBounded<CandleUpdate>(options);
            
            // Start background processor
            _processorTask = Task.Run(ProcessorLoopAsync, _cts.Token);
        }

        /// <summary>
        /// Queue a candle for batch update (non-blocking, thread-safe)
        /// </summary>
        public bool QueueUpdate(Candle candle, uint token, string name)
        {
            if (_cts.IsCancellationRequested) return false;

            var update = new CandleUpdate
            {
                Candle = candle,
                Token = token,
                Name = name,
                QueuedAt = DateTime.UtcNow
            };

            // TryWrite is non-blocking - returns false if channel full
            if (_channel.Writer.TryWrite(update))
            {
                Interlocked.Increment(ref _totalQueued);
                return true;
            }

            // Channel full - item was dropped (DropOldest policy)
            return false;
        }

        private async Task ProcessorLoopAsync()
        {
            var batch = new List<CandleUpdate>(_maxBatchSize);
            var lastFlushTime = DateTime.UtcNow;

            try
            {
                await foreach (var update in _channel.Reader.ReadAllAsync(_cts.Token))
                {
                    batch.Add(update);

                    // Flush conditions:
                    // 1. Batch is full
                    // 2. Flush interval elapsed and batch not empty
                    var timeSinceLastFlush = DateTime.UtcNow - lastFlushTime;
                    var shouldFlush = batch.Count >= _maxBatchSize || 
                                     (timeSinceLastFlush >= _flushInterval && batch.Count > 0);

                    if (shouldFlush)
                    {
                        await FlushBatchAsync(batch);
                        batch.Clear();
                        lastFlushTime = DateTime.UtcNow;
                    }
                }

                // Final flush on shutdown
                if (batch.Count > 0)
                {
                    await FlushBatchAsync(batch);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CandleBatchWriter] Processor loop failed: {ex}");
            }
        }

        private async Task FlushBatchAsync(List<CandleUpdate> batch)
        {
            if (batch.Count == 0) return;

            // Deduplicate: Keep only latest update per (Token, Time)
            var deduplicated = batch
                .GroupBy(u => new { u.Token, u.Candle.Time })
                .Select(g => g.Last())
                .ToList();

            var duplicateCount = batch.Count - deduplicated.Count;
            if (duplicateCount > 0)
            {
                Interlocked.Add(ref _totalDeduplicated, duplicateCount);
            }

            // Retry logic for transient failures
            for (int attempt = 1; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    await ProcessBatchWithTransactionAsync(deduplicated);
                    
                    // Success
                    Interlocked.Add(ref _totalProcessed, deduplicated.Count);
                    lock (_statsLock)
                    {
                        _lastFlush = DateTime.UtcNow;
                    }
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt == _maxRetries)
                    {
                        // All retries exhausted
                        Interlocked.Add(ref _totalFailed, deduplicated.Count);
                        await SignalDiagnostics.RejectAsync(0, "CandleBatchWriter", Clock.NowIst(),
                            $"Batch flush failed after {_maxRetries} attempts: {ex.Message} (Batch size: {deduplicated.Count})");
                    }
                    else
                    {
                        // Exponential backoff
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)), _cts.Token);
                    }
                }
            }
        }

        private async Task ProcessBatchWithTransactionAsync(List<CandleUpdate> batch)
        {
            using var conn = new SqlConnection(Config.ConnectionString);
            await conn.OpenAsync(_cts.Token);

            var interval = $"{Config.Current.Trading.TimeframeMinutes}m";

            using var tran = conn.BeginTransaction();

            // Use MERGE for efficient UPSERT (better than UPDATE + INSERT)
            const string sql = @"
MERGE dbo.Candles AS target
USING (VALUES (@token, @interval, @time, @o, @h, @l, @c, @v, @name)) 
    AS source (InstrumentToken, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume, InstrumentName)
ON (target.InstrumentToken = source.InstrumentToken 
    AND target.Interval = source.Interval 
    AND target.CandleTime = source.CandleTime)
WHEN MATCHED THEN
    UPDATE SET 
        OpenPrice = source.OpenPrice,
        HighPrice = source.HighPrice,
        LowPrice = source.LowPrice,
        ClosePrice = source.ClosePrice,
        Volume = source.Volume
WHEN NOT MATCHED THEN
    INSERT (InstrumentToken, InstrumentName, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume)
    VALUES (source.InstrumentToken, source.InstrumentName, source.Interval, source.CandleTime, 
            source.OpenPrice, source.HighPrice, source.LowPrice, source.ClosePrice, source.Volume);";

            var validUpdates = 0;
            foreach (var update in batch)
            {
                var candleTimeIst = Clock.UtcToIst(update.Candle.Time);

                // VALIDATION: SQL Server datetime2 range
                var minSqlDate = new DateTime(1753, 1, 2);
                var maxReasonableDate = DateTime.UtcNow.AddDays(2);
                
                if (candleTimeIst < minSqlDate || candleTimeIst > maxReasonableDate)
                {
                    continue; // Skip invalid dates
                }

                await conn.ExecuteAsync(sql, new
                {
                    token = (long)update.Token,
                    name = update.Name,
                    interval,
                    time = candleTimeIst,
                    o = update.Candle.Open,
                    h = update.Candle.High,
                    l = update.Candle.Low,
                    c = update.Candle.Close,
                    v = (long)update.Candle.Volume
                }, transaction: tran, commandTimeout: 30);

                validUpdates++;
            }

            await tran.CommitAsync(_cts.Token);
        }

        /// <summary>
        /// Graceful shutdown - flush all pending updates
        /// </summary>
        public async Task ShutdownAsync(TimeSpan timeout)
        {
            // Signal no more writes
            _channel.Writer.Complete();

            // Wait for processor to finish with timeout
            var completedInTime = await Task.WhenAny(_processorTask, Task.Delay(timeout)) == _processorTask;
            
            if (!completedInTime)
            {
                System.Diagnostics.Debug.WriteLine("[CandleBatchWriter] Shutdown timeout - some updates may be lost");
                _cts.Cancel();
            }

            try
            {
                await _processorTask;
            }
            catch
            {
                // Ignore shutdown errors
            }
        }

        public (long Queued, long Processed, long Failed, long Deduplicated, int QueueSize, DateTime LastFlush) GetStats()
        {
            lock (_statsLock)
            {
                return (
                    Interlocked.Read(ref _totalQueued),
                    Interlocked.Read(ref _totalProcessed),
                    Interlocked.Read(ref _totalFailed),
                    Interlocked.Read(ref _totalDeduplicated),
                    _channel.Reader.Count,
                    _lastFlush
                );
            }
        }

        public string GetStatsFormatted()
        {
            var (queued, processed, failed, dedup, queueSize, lastFlush) = GetStats();
            var timeSinceLastFlush = DateTime.UtcNow - lastFlush;
            var successRate = queued > 0 ? (processed * 100.0 / queued) : 100.0;
            
            return $"Queue: {queueSize}, Queued: {queued:N0}, Processed: {processed:N0}, " +
                   $"Failed: {failed:N0}, Deduped: {dedup:N0}, Success: {successRate:F1}%, " +
                   $"Last flush: {timeSinceLastFlush.TotalSeconds:F1}s ago";
        }

        public void Dispose()
        {
            if (_cts.IsCancellationRequested) return;

            // Graceful shutdown with 5 second timeout
            try
            {
                ShutdownAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore dispose errors
            }
            finally
            {
                _cts?.Dispose();
            }
        }
    }
}
