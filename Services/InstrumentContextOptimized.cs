using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ZerodhaOxySocket.Services;

namespace ZerodhaOxySocket
{
    /// <summary>
    /// Ultra-high-performance candle builder with inline OHLC calculation (no sorting).
    /// </summary>
    public class InstrumentContextOptimized
    {
        public uint Token { get; }
        public string Name { get; }
        private readonly TimeSpan _timeframe;
        private readonly List<Candle> _candles = new();
        private readonly Dictionary<DateTime, Candle> _candleMap = new();

        // Current candle state (optimized for fast updates)
        private DateTime _currentCandleTime = default;
        private double _currentOpen;
        private double _currentHigh = double.MinValue;
        private double _currentLow = double.MaxValue;
        private double _currentClose;
        private long _currentVolume;
        private int _currentTickCount;
        
        // Safety thresholds
        private const int MAX_TICKS_PER_CANDLE = 5000;
        private const int ALERT_TICK_THRESHOLD = 2000;

        // Late tick handling
        private readonly Dictionary<DateTime, LateTickBucket> _lateTicksByBucket = new();
        private readonly TimeSpan _lateTickWindow = TimeSpan.FromMinutes(3);
        
        private readonly object _sync = new();
        private DateTime _lastTickTime = default;

        private class LateTickBucket
        {
            public double High = double.MinValue;
            public double Low = double.MaxValue;
            public double LastPrice;
            public long VolumeAdd;
            public int Count;
        }

        public InstrumentContextOptimized(uint token, string name, TimeSpan timeframe, IEnumerable<Candle>? seed = null)
        {
            Token = token;
            Name = name;
            _timeframe = timeframe;

            if (seed != null)
            {
                foreach (var c in seed)
                {
                    var candle = new Candle { Time = c.Time, Open = c.Open, High = c.High, Low = c.Low, Close = c.Close, Volume = c.Volume, InstrumentToken = Token, InstrumentName = Name };
                    _candles.Add(candle);
                    _candleMap[c.Time] = candle;
                }
                if (_candles.Count > 0)
                    _currentCandleTime = _candles[_candles.Count - 1].Time;
            }
        }

        public IReadOnlyList<Candle> GetCandles() => _candles;

        /// <summary>
        /// ZERO-LATENCY tick processing - optimized hot path
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Candle? ProcessTickWithTime(double ltp, DateTime tickTime, long qty = 0)
        {
            var bucketStart = Clock.FloorToBucketIst(tickTime, _timeframe);

            // FIRST TICK: Initialize
            if (_currentCandleTime == default)
            {
                lock (_sync)
                {
                    if (_currentCandleTime == default)
                    {
                        StartNewCandle(bucketStart, ltp, qty);
                        return null;
                    }
                }
            }

            // FAST PATH: Same bucket (99% of ticks) with double-checked locking
            if (bucketStart == _currentCandleTime)
            {
                lock (_sync)
                {
                    // Re-check after lock to prevent race condition
                    if (bucketStart == _currentCandleTime)
                    {
                        UpdateCurrentCandle(ltp, qty);
                        
                        // EMERGENCY: Force finalize if tick count explodes (likely duplicate replay)
                        if (_currentTickCount >= MAX_TICKS_PER_CANDLE)
                        {
                            _ = SignalDiagnostics.WarnAsync(Token, Name, _currentCandleTime,
                                $"[EMERGENCY] Finalizing bucket {_currentCandleTime:HH:mm} with {_currentTickCount} ticks (threshold: {MAX_TICKS_PER_CANDLE})");
                            return FinalizeCurrentCandle();
                        }
                        
                        return null;
                    }
                    // Bucket changed while waiting for lock - fall through to slow path
                }
            }

            // SLOW PATH: New bucket or late tick
            lock (_sync)
            {
                _lastTickTime = SessionClock.NowIst();
                
                // Re-check after acquiring lock
                if (bucketStart == _currentCandleTime)
                {
                    UpdateCurrentCandle(ltp, qty);
                    return null;
                }

                // NEW BUCKET
                if (bucketStart > _currentCandleTime)
                {
                    _ = SignalDiagnostics.InfoAsync(Token, Name, _currentCandleTime, "BUCKET_TRANSITION",
                        $"{_currentCandleTime:HH:mm:ss} ? {bucketStart:HH:mm:ss} (ticks: {_currentTickCount}, tickTime: {tickTime:HH:mm:ss.fff})");
                    
                    var finalized = FinalizeCurrentCandle();
                    FillGaps(bucketStart, ltp);
                    StartNewCandle(bucketStart, ltp, qty);
                    return finalized;
                }

                // LATE TICK
                HandleLateTick(bucketStart, ltp, qty);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateCurrentCandle(double ltp, long qty)
        {
            if (_currentTickCount == 0)
            {
                _currentOpen = ltp;
                _currentHigh = ltp;
                _currentLow = ltp;
            }
            else
            {
                if (ltp > _currentHigh) _currentHigh = ltp;
                if (ltp < _currentLow) _currentLow = ltp;
            }
            _currentClose = ltp;
            _currentVolume += qty > 0 ? qty : 1;
            _currentTickCount++;
            
            // ALERT: Abnormal tick count (likely replay/duplication)
            if (_currentTickCount == ALERT_TICK_THRESHOLD || 
                (_currentTickCount > ALERT_TICK_THRESHOLD && _currentTickCount % 1000 == 0))
            {
                _ = SignalDiagnostics.WarnAsync(Token, Name, _currentCandleTime,
                    $"[ALERT] Bucket {_currentCandleTime:HH:mm} has {_currentTickCount} ticks (threshold: {ALERT_TICK_THRESHOLD})");
            }
        }

        private void StartNewCandle(DateTime bucketStart, double ltp, long qty)
        {
            _currentCandleTime = bucketStart;
            _currentOpen = ltp;
            _currentHigh = ltp;
            _currentLow = ltp;
            _currentClose = ltp;
            _currentVolume = qty > 0 ? qty : 1;
            _currentTickCount = 1;
        }

        private Candle? FinalizeCurrentCandle()
        {
            if (_currentTickCount == 0 || _currentCandleTime == default)
                return null;

            var candle = new Candle
            {
                Time = _currentCandleTime,
                Open = _currentOpen,
                High = _currentHigh,
                Low = _currentLow,
                Close = _currentClose,
                Volume = _currentVolume,
                InstrumentToken = Token,
                InstrumentName = Name
            };

            _candles.Add(candle);
            _candleMap[candle.Time] = candle;
            if (_candles.Count > 1000) _candles.RemoveAt(0);

            // Upsert candle (INSERT if new, UPDATE if exists for late ticks) - idempotent
            _ = DataAccess.UpsertCandleAsync(candle, Token, Name);

            // Log every 50th candle OR any candle with abnormal tick count
            if (_currentTickCount % 50 == 0 || _currentTickCount > ALERT_TICK_THRESHOLD)
            {
                _ = SignalDiagnostics.InfoAsync(Token, Name, _currentCandleTime, "FINALIZED",
                    $"{_currentCandleTime:HH:mm:ss} with {_currentTickCount} ticks O={_currentOpen:F2} H={_currentHigh:F2} L={_currentLow:F2} C={_currentClose:F2} V={_currentVolume}");
            }
            
            // Always log abnormal candles to diagnostics
            if (_currentTickCount > ALERT_TICK_THRESHOLD)
            {
                _ = SignalDiagnostics.WarnAsync(Token, Name, _currentCandleTime,
                    $"[FINALIZED_ABNORMAL] Bucket {_currentCandleTime:HH:mm} finalized with {_currentTickCount} ticks (threshold: {ALERT_TICK_THRESHOLD})");
            }

            // Reset state
            _currentTickCount = 0;
            _currentVolume = 0;
            _currentHigh = double.MinValue;
            _currentLow = double.MaxValue;

            return candle;
        }

        private void FillGaps(DateTime targetBucket, double currentPrice)
        {
            var gapStart = _currentCandleTime + _timeframe;
            var gapCount = 0;
            
            while (gapStart < targetBucket)
            {
                var lastClose = _candles.Count > 0 ? _candles[_candles.Count - 1].Close : currentPrice;
                var gapCandle = new Candle
                {
                    Time = gapStart,
                    Open = lastClose,
                    High = lastClose,
                    Low = lastClose,
                    Close = lastClose,
                    Volume = 0,
                    InstrumentToken = Token,
                    InstrumentName = Name
                };
                _candles.Add(gapCandle);
                _candleMap[gapStart] = gapCandle;
                _ = DataAccess.UpsertCandleAsync(gapCandle, Token, Name);
                gapStart += _timeframe;
                gapCount++;
            }
            
            // Log gap filling activity
            if (gapCount > 0)
            {
                _ = SignalDiagnostics.InfoAsync(Token, Name, targetBucket, "GAP_FILL",
                    $"Filled {gapCount} gap candle(s) from {_currentCandleTime:HH:mm} to {targetBucket:HH:mm}");
            }
        }

        private void HandleLateTick(DateTime bucketStart, double ltp, long qty)
        {
            // VALIDATION: Reject late ticks that are too old (prevent time wraparound issues)
            if ((_currentCandleTime - bucketStart) > _lateTickWindow)
            {
                _ = SignalDiagnostics.WarnAsync(Token, Name, bucketStart,
                    $"[REJECT] Late tick for {bucketStart:HH:mm} is too old (current: {_currentCandleTime:HH:mm}, age: {(_currentCandleTime - bucketStart).TotalMinutes:F1}min)");
                return;
            }
            
            if (!_candleMap.TryGetValue(bucketStart, out var existing))
                return;

            if (!_lateTicksByBucket.TryGetValue(bucketStart, out var bucket))
            {
                bucket = new LateTickBucket();
                _lateTicksByBucket[bucketStart] = bucket;
            }

            if (ltp > bucket.High) bucket.High = ltp;
            if (ltp < bucket.Low) bucket.Low = ltp;
            bucket.LastPrice = ltp;
            bucket.VolumeAdd += qty > 0 ? qty : 1;
            bucket.Count++;

            var changed = false;
            if (bucket.High > existing.High) { existing.High = bucket.High; changed = true; }
            if (bucket.Low < existing.Low) { existing.Low = bucket.Low; changed = true; }
            if (bucket.LastPrice != existing.Close) { existing.Close = bucket.LastPrice; changed = true; }
            existing.Volume += qty > 0 ? qty : 1;

            if (changed)
            {
                _ = DataAccess.UpdateCandleAsync(existing, Token, Name);
                
                // Log late tick correction
                _ = SignalDiagnostics.InfoAsync(Token, Name, bucketStart, "LATE_TICK_CORRECTED",
                    $"Bucket {bucketStart:HH:mm} updated with late tick #{bucket.Count} (H={bucket.High:F2}, L={bucket.Low:F2})");
            }

            // Periodic cleanup
            if (_lateTicksByBucket.Count > 10)
                CleanupLateTicks();
        }

        private void CleanupLateTicks()
        {
            var threshold = _lastTickTime - _lateTickWindow;
            var keysToRemove = new List<DateTime>();
            foreach (var key in _lateTicksByBucket.Keys)
            {
                if (key < threshold)
                    keysToRemove.Add(key);
            }
            foreach (var key in keysToRemove)
                _lateTicksByBucket.Remove(key);
        }

        public void FinalizeIfStale()
        {
            lock (_sync)
            {
                if (_currentTickCount == 0) return;
                var now = SessionClock.NowIst();
                var expectedClose = _currentCandleTime + _timeframe + TimeSpan.FromSeconds(2);
                if (now > expectedClose)
                {
                    _ = SignalDiagnostics.InfoAsync(Token, Name, _currentCandleTime, "STALE_FINALIZE",
                        $"Forcibly finalizing stale bucket {_currentCandleTime:HH:mm} with {_currentTickCount} ticks (now: {now:HH:mm:ss})");
                    FinalizeCurrentCandle();
                }
            }
        }


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

        public List<Candle> ForceFinalizePending()
        {
            lock (_sync)
            {
                var result = new List<Candle>();
                var c = FinalizeCurrentCandle();
                if (c != null)
                    result.Add(c);
                return result;
            }
        }

        public string GetStats()
        {
            return $"[{Name}] Candles: {_candles.Count}, CurrentTicks: {_currentTickCount}, " +
                   $"LateBuckets: {_lateTicksByBucket.Count}";
        }

        public SignalResult EvaluateSignalsPositionAware_Conservative()
        {
            int last = _candles.Count;
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
