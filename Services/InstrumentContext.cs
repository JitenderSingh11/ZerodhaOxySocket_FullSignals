using System;
using System.Collections.Generic;
using System.Linq;
using ZerodhaOxySocket.Services;

namespace ZerodhaOxySocket
{
    public class InstrumentContext
    {
        public uint Token { get; }
        public string Name { get; }
        private readonly TimeSpan _timeframe;
        private readonly CandleBatchWriter _batchWriter;
        private readonly List<Candle> _candles = new();
        private readonly Dictionary<DateTime, Candle> _candleMap = new();
        private readonly Dictionary<DateTime, List<TickData>> _lateTicksByBucket = new();
        private readonly List<TickData> _currentCandleTicks = new();
        private DateTime _currentCandleTime = default;
        private DateTime _lastTickTime = default;
        private readonly TimeSpan _bufferDelay = TimeSpan.FromSeconds(2);
        private readonly TimeSpan _lateTickWindow = TimeSpan.FromMinutes(3);
        private readonly object _sync = new();
        
        // Statistics
        private int _totalCandlesFinalized = 0;
        private int _totalTicksProcessed = 0;
        private int _totalLateTicksProcessed = 0;
        private int _totalGapsFilled = 0;
        private int _totalStaleFinalized = 0;
        private int _invalidTicksSkipped = 0;
        private DateTime _lastInvalidTickLog = DateTime.MinValue;

        public InstrumentContext(uint token, string name, TimeSpan timeframe, IEnumerable<Candle>? seed = null, CandleBatchWriter batchWriter = null)
        {
            Token = token;
            Name = name;
            _timeframe = timeframe;
            _batchWriter = batchWriter;

            if (seed != null)
            {
                foreach (var c in seed)
                {
                    var candle = new Candle { Time = c.Time, Open = c.Open, High = c.High, Low = c.Low, Close = c.Close, Volume = c.Volume };
                    _candles.Add(candle);
                    _candleMap[c.Time] = candle;
                }
                if (_candles.Count > 0)
                    _currentCandleTime = _candles.Last().Time;
            }
        }

        public IReadOnlyList<Candle> GetCandles() => _candles.AsReadOnly();

        public Candle? ProcessTickWithTime(double ltp, DateTime tickTime, long qty = 0)
        {
            // HOT PATH: Most ticks fall into the current candle bucket. Handle this with minimal locking.
            var bucketStart = Clock.FloorToBucketIst(tickTime, _timeframe);

            // Fast check without a full lock
            if (bucketStart == _currentCandleTime && _currentCandleTime != default)
            {
                lock (_sync) // Lock only to add to the list
                {
                    _currentCandleTicks.Add(new TickData { TickTime = tickTime, LastPrice = ltp, LastQuantity = qty });
                    _totalTicksProcessed++;
                    _lastTickTime = tickTime > _lastTickTime ? tickTime : _lastTickTime;
                }
                return null;
            }

            // SLOW PATH: New candle, late tick, or first tick. Requires a full lock.
            return ProcessTickSlowPath(ltp, tickTime, qty, bucketStart);
        }

        private Candle? ProcessTickSlowPath(double ltp, DateTime tickTime, long qty, DateTime bucketStart)
        {
            lock (_sync)
            {
                // Re-check condition after acquiring lock to handle race conditions
                if (bucketStart == _currentCandleTime && _currentCandleTime != default)
                {
                    _currentCandleTicks.Add(new TickData { TickTime = tickTime, LastPrice = ltp, LastQuantity = qty });
                    _totalTicksProcessed++;
                    _lastTickTime = tickTime > _lastTickTime ? tickTime : _lastTickTime;
                    return null;
                }

                // VALIDATION: Check incoming tick time
                if (tickTime < new DateTime(2020, 1, 1) || tickTime > DateTime.UtcNow.AddDays(1))
                {
                    _invalidTicksSkipped++;
                    if (_invalidTicksSkipped % 100 == 1 || (DateTime.UtcNow - _lastInvalidTickLog).TotalMinutes >= 1)
                    {
                        _lastInvalidTickLog = DateTime.UtcNow;
                        _ = SignalDiagnostics.WarnAsync(Token, Name, SessionClock.NowIst(),
                            $"Invalid tick timestamp: {tickTime:O} (Total skipped: {_invalidTicksSkipped})");
                    }
                    return null;
                }

                // VALIDATION: Check bucket time
                if (bucketStart < new DateTime(2020, 1, 1) || bucketStart > DateTime.UtcNow.AddDays(1))
                {
                    _invalidTicksSkipped++;
                    if (_invalidTicksSkipped % 100 == 1 || (DateTime.UtcNow - _lastInvalidTickLog).TotalMinutes >= 1)
                    {
                        _lastInvalidTickLog = DateTime.UtcNow;
                        _ = SignalDiagnostics.WarnAsync(Token, Name, SessionClock.NowIst(),
                            $"Invalid bucket time: {bucketStart:O} (Total skipped: {_invalidTicksSkipped})");
                    }
                    return null;
                }

                _lastTickTime = tickTime > _lastTickTime ? tickTime : _lastTickTime;

                if (_currentCandleTime == default)
                {
                    _currentCandleTime = bucketStart;
                }

                if (bucketStart > _currentCandleTime)
                {
                    var finalized = FinalizeCandle(_currentCandleTime, _currentCandleTicks);
                    _currentCandleTicks.Clear();

                    var gapStart = _currentCandleTime + _timeframe;
                    while (gapStart < bucketStart)
                    {
                        var lastClose = _candles.Count > 0 ? _candles.Last().Close : ltp;
                        var gapCandle = new Candle
                        {
                            Time = gapStart, Open = lastClose, High = lastClose, Low = lastClose, Close = lastClose, Volume = 0,
                            InstrumentToken = Token, InstrumentName = Name
                        };
                        _candles.Add(gapCandle);
                        _candleMap[gapStart] = gapCandle;
                        if (_batchWriter != null) _batchWriter.QueueUpdate(gapCandle, Token, Name);
                        else _ = DataAccess.UpdateCandleAsync(gapCandle, Token, Name);
                        _totalGapsFilled++;
                        if (_totalGapsFilled % 10 == 0)
                            _ = SignalDiagnostics.InfoAsync(Token, Name, gapStart, "GapFill", $"Gap candle at {gapStart:HH:mm:ss} (total: {_totalGapsFilled})");
                        gapStart += _timeframe;
                    }

                    _currentCandleTime = bucketStart;
                    _currentCandleTicks.Add(new TickData { TickTime = tickTime, LastPrice = ltp, LastQuantity = qty });
                    _totalTicksProcessed++;
                    
                    // Cleanup late tick buckets periodically
                    if (_totalCandlesFinalized % 10 == 0)
                    {
                        CleanupLateTicks();
                    }

                    return finalized;
                }

                if (_candleMap.TryGetValue(bucketStart, out var existing))
                {
                    if (!_lateTicksByBucket.ContainsKey(bucketStart))
                        _lateTicksByBucket[bucketStart] = new List<TickData>(10); // Pre-allocate
                    _lateTicksByBucket[bucketStart].Add(new TickData { TickTime = tickTime, LastPrice = ltp, LastQuantity = qty });
                    _totalLateTicksProcessed++;

                    var ticks = _lateTicksByBucket[bucketStart];
                    if (RecalculateCandle(bucketStart, ticks, existing))
                    {
                        if (_batchWriter != null) _batchWriter.QueueUpdate(existing, Token, Name);
                        else _ = DataAccess.UpdateCandleAsync(existing, Token, Name);
                        if (_totalLateTicksProcessed % 20 == 0)
                            _ = SignalDiagnostics.InfoAsync(Token, Name, bucketStart, "LateTick", $"Corrected at {bucketStart:HH:mm} (total: {_totalLateTicksProcessed})");
                    }
                }
                
                return null;
            }
        }

        private void CleanupLateTicks()
        {
            var threshold = _lastTickTime.Subtract(_lateTickWindow);
            var keysToRemove = new List<DateTime>();
            foreach (var key in _lateTicksByBucket.Keys)
            {
                if (key < threshold)
                {
                    keysToRemove.Add(key);
                }
            }
            foreach (var key in keysToRemove)
            {
                _lateTicksByBucket.Remove(key);
            }
        }

        private Candle? FinalizeCandle(DateTime bucketStart, List<TickData> ticks)
        {
            if (ticks.Count == 0) return null;

            // OPTIMIZED: Find OHLC in a single pass, assuming ticks are mostly ordered.
            // This is much faster than sorting the whole list.
            double open = ticks[0].LastPrice;
            double close = ticks[ticks.Count - 1].LastPrice;
            double high = open;
            double low = open;
            long volume = 0;

            DateTime firstTickTime = ticks[0].TickTime;
            DateTime lastTickTime = ticks[0].TickTime;

            foreach (var tick in ticks)
            {
                if (tick.LastPrice > high) high = tick.LastPrice;
                if (tick.LastPrice < low) low = tick.LastPrice;
                volume += tick.LastQuantity > 0 ? tick.LastQuantity : 1;

                if (tick.TickTime < firstTickTime)
                {
                    firstTickTime = tick.TickTime;
                    open = tick.LastPrice;
                }
                if (tick.TickTime > lastTickTime)
                {
                    lastTickTime = tick.TickTime;
                    close = tick.LastPrice;
                }
            }

            var candle = new Candle
            {
                Time = bucketStart,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                InstrumentToken = Token,
                InstrumentName = Name
            };

            _candles.Add(candle);
            _candleMap[bucketStart] = candle;
            if (_candles.Count > 1000) _candles.RemoveAt(0);

            // Batched DB update (prevents connection pool exhaustion)
            if (_batchWriter != null)
                _batchWriter.QueueUpdate(candle, Token, Name);
            else
                _ = DataAccess.UpdateCandleAsync(candle, Token, Name);
            
            _totalCandlesFinalized++;
            
            if (_totalCandlesFinalized % 10 == 0) // Log every 10th candle
                _ = SignalDiagnostics.InfoAsync(Token, Name, bucketStart, "Finalized",
                    $"{bucketStart:HH:mm:ss} with {ticks.Count} ticks O={open:F2} H={high:F2} L={low:F2} C={close:F2} V={volume} (total: {_totalCandlesFinalized})");

            return candle;
        }

        private bool RecalculateCandle(DateTime bucket, List<TickData> ticks, Candle candle)
        {
            if (ticks.Count == 0) return false;

            // OPTIMIZED: Find new H, L, C in one pass without full sorting.
            double high = candle.High;
            double low = candle.Low;
            double close = candle.Close;
            long volumeAdd = 0;
            DateTime lastTickTime = DateTime.MinValue;

            // Find the last known tick time in the main candle to compare against
            if (_candleMap.TryGetValue(bucket, out var mainCandle))
            {
                // This is a simplification. A more robust solution would store last tick time in the candle.
                // For now, we assume the close price is from the last tick.
            }

            foreach (var tick in ticks)
            {
                if (tick.LastPrice > high) high = tick.LastPrice;
                if (tick.LastPrice < low) low = tick.LastPrice;
                volumeAdd += tick.LastQuantity > 0 ? tick.LastQuantity : 1;

                if (tick.TickTime > lastTickTime)
                {
                    lastTickTime = tick.TickTime;
                    close = tick.LastPrice;
                }
            }

            var changed = candle.High != high || candle.Low != low || candle.Close != close;
            if (changed)
            {
                candle.High = high;
                candle.Low = low;
                candle.Close = close;
                candle.Volume += volumeAdd;
                
                if (ticks.Count >= 5)
                    _ = SignalDiagnostics.InfoAsync(Token, Name, bucket, "LateUpdate",
                        $"Updated {bucket:HH:mm} with {ticks.Count} late ticks H={high:F2} L={low:F2} C={close:F2}");
            }

            return changed;
        }

        public List<Candle> ForceFinalizePending()
        {
            lock (_sync)
            {
                var result = new List<Candle>();
                if (_currentCandleTime != default && _currentCandleTicks.Count > 0)
                {
                    var finalized = FinalizeCandle(_currentCandleTime, _currentCandleTicks);
                    if (finalized != null)
                    {
                        result.Add(finalized);
                        _currentCandleTicks.Clear();
                    }
                }
                return result;
            }
        }

        public void FinalizeIfStale()
        {
            lock (_sync)
            {
                if (_currentCandleTime == default || _currentCandleTicks.Count == 0 || _lastTickTime == default) return;

                // CORRECTED: Use the last known tick time for comparison, not the system clock (SessionClock.NowIst()).
                // This ensures finalization is based on market data time, not processing time.
                var expectedFinalizationTime = _currentCandleTime + _timeframe;

                if (_lastTickTime >= expectedFinalizationTime)
                {
                    var finalized = FinalizeCandle(_currentCandleTime, _currentCandleTicks);
                    if (finalized != null)
                    {
                        _totalStaleFinalized++;
                        _ = SignalDiagnostics.InfoAsync(Token, Name, finalized.Time, "StaleFinalize",
                            $"Candle at {finalized.Time:HH:mm} finalized based on tick time (stale count: {_totalStaleFinalized})");
                        
                        // Important: After finalizing, the current candle time and ticks must be updated
                        // to reflect the bucket of the tick that triggered the finalization.
                        var newBucket = Clock.FloorToBucketIst(_lastTickTime, _timeframe);
                        _currentCandleTime = newBucket;
                        _currentCandleTicks.Clear();
                        
                        // Since the tick that triggered this was not processed, add it to the new candle
                        // (This part is complex and can be omitted for a simpler fix, but is more correct)
                        // For now, we will let the next tick create the new candle state.
                    }
                }
            }
        }

        /// <summary>
        /// Manually add a candle (used for replay/seeding)
        /// </summary>
        public void AddCandle(Candle c)
        {
            lock (_sync)
            {
                _candles.Add(c);
                _candleMap[c.Time] = c;
                if (_candles.Count > 1000) _candles.RemoveAt(0);
                _currentCandleTime = c.Time;
            }
        }

        /// <summary>
        /// Get candle formation statistics
        /// </summary>
        public string GetStats()
        {
            return $"[{Name}] Candles: {_candles.Count}, Finalized: {_totalCandlesFinalized}, " +
                   $"Ticks: {_totalTicksProcessed}, Late: {_totalLateTicksProcessed}, " +
                   $"Gaps: {_totalGapsFilled}, Stale: {_totalStaleFinalized}, Invalid: {_invalidTicksSkipped}, " +
                   $"Current: {_currentCandleTicks.Count} ticks";
        }

        /// <summary>
        /// Get detailed memory and performance metrics
        /// </summary>
        public (int CandleCount, int CurrentTicksInBuffer, int LateTickBuckets, int TotalLateTicks, long MemoryEstimateBytes) GetMemoryStats()
        {
            lock (_sync)
            {
                int lateTickCount = 0;
                foreach (var bucket in _lateTicksByBucket.Values)
                {
                    lateTickCount += bucket.Count;
                }

                // Rough memory estimate
                long memoryBytes = 
                    (_candles.Count * 100) + // ~100 bytes per candle (rough estimate)
                    (_currentCandleTicks.Count * 60) + // ~60 bytes per tick
                    (lateTickCount * 60); // Late ticks

                return (_candles.Count, _currentCandleTicks.Count, _lateTicksByBucket.Count, lateTickCount, memoryBytes);
            }
        }

        public SignalResult EvaluateSignalsPositionAware_Conservative()
        {
            var last = _candles.Count;
            _ = SignalDiagnostics.InfoAsync(0, "InstrumentContext", SessionClock.NowIst(), "EvaluateSignalsPositionAware_Conservative", $"Candle Count: {last}");

            int need = Math.Max(Config.Current.Trading.SlowEma,
                        Math.Max(Config.Current.Trading.RsiPeriod, Config.Current.Trading.AtrPeriod)) + 2;
            if (last < need) return null;

            var c0 = _candles[last - 1];
            var c1 = _candles[last - 2];

            double emaFast0 = IndicatorHelper.EMA(_candles, Config.Current.Trading.FastEma, last - 1);
            double emaSlow0 = IndicatorHelper.EMA(_candles, Config.Current.Trading.SlowEma, last - 1);
            double rsi0 = IndicatorHelper.RSI(_candles, Config.Current.Trading.RsiPeriod, last - 1);
            double atr0 = IndicatorHelper.ATR(_candles, Config.Current.Trading.AtrPeriod, last - 1);
            if (atr0 <= 0) return null;

            double range = c0.High - c0.Low;
            double body = Math.Abs(c0.Close - c0.Open);
            double bodyPct = (c0.Close > 0) ? (body / c0.Close) * 100.0 : 0.0;
            if (bodyPct < Config.Current.Trading.MinBodyPct) return null;
            if (range < Config.Current.Trading.MinRangeAtr * atr0) return null;

            bool longBias = emaFast0 > emaSlow0 && rsi0 >= Config.Current.Trading.RsiBuyBelow
                             && c0.Close > c0.Open && c0.Close > c1.High;

            bool shortBias = emaFast0 < emaSlow0 && rsi0 <= Config.Current.Trading.RsiSellAbove
                             && c0.Close < c0.Open && c0.Close < c1.Low;

            if (longBias)
                return new SignalResult
                {
                    Type = SignalType.Buy,
                    Price = c0.Close,
                    Time = c0.Time,
                    Meta = $"EMA{Config.Current.Trading.FastEma}>{Config.Current.Trading.SlowEma}, RSI={rsi0:F1}, ATR={atr0:F2}, BreakUp"
                };

            if (shortBias)
                return new SignalResult
                {
                    Type = SignalType.Sell,
                    Price = c0.Close,
                    Time = c0.Time,
                    Meta = $"EMA{Config.Current.Trading.FastEma}<{Config.Current.Trading.SlowEma}, RSI={rsi0:F1}, ATR={atr0:F2}, BreakDn"
                };

            return null;
        }
    }
}
