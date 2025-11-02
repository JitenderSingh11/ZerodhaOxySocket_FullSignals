using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaOxySocket
{
    public class InstrumentContext
    {
        public uint Token { get; }
        public string Name { get; }
        private readonly List<Candle> _candles = new();
        private DateTime _currentCandleTime = default;
        private double _open, _high, _low, _close, _volume;
        private readonly TimeSpan _timeframe;

        private int _consecutiveEntry = 0;
        private int _consecutiveExit  = 0;

        private const int DebounceCandles = 2;
        private const int AtrPeriod = 14;
        private const double AtrTrailMult = 2.0;
        private const int RegimeFast = 9, RegimeSlow = 21;

        public InstrumentContext(uint token, string name, TimeSpan timeframe, IEnumerable<Candle>? seed = null)
        {
            Token = token; Name = name; _timeframe = timeframe;
            if (seed != null)
            {
                foreach (var c in seed)
                    _candles.Add(new Candle { Time = c.Time, Open = c.Open, High = c.High, Low = c.Low, Close = c.Close, Volume = c.Volume });
                if (_candles.Count > 0)
                    _currentCandleTime = _candles.Last().Time;
            }
        }

        public Candle ProcessTick(double ltp, long dayVolume)
        {
            var bucketStart = SessionClock.FloorToBucketIst(_timeframe);

            if (_currentCandleTime == default || bucketStart != _currentCandleTime)
            {
                Candle closed = null;
                if (_currentCandleTime != default)
                {
                    closed = new Candle { Time = _currentCandleTime, Open = _open, High = _high, Low = _low, Close = _close, Volume = _volume };
                    _candles.Add(closed);
                    if (_candles.Count > 1000) _candles.RemoveAt(0);
                }
                _currentCandleTime = bucketStart;
                _open = _high = _low = _close = ltp;
                _volume = 0;
                return closed;
            }
            else
            {
                _close = ltp;
                _high = Math.Max(_high, ltp);
                _low = Math.Min(_low, ltp);
                _volume += 0;
                return null;
            }
        }

        private double CalculateATR(int period = AtrPeriod)
        {
            if (_candles.Count < period + 1) return 0;
            double sumTR = 0;
            for (int i = _candles.Count - period; i < _candles.Count; i++)
            {
                var c = _candles[i];
                var prev = _candles[i - 1];
                double tr = Math.Max(c.High - c.Low,
                              Math.Max(Math.Abs(c.High - prev.Close),
                                       Math.Abs(c.Low - prev.Close)));
                sumTR += tr;
            }
            return sumTR / period;
        }

        public Signal EvaluateSignalsPositionAware()
        {
            if (_candles.Count < Math.Max(RegimeSlow, 30)) return null;
            if (!SessionClock.IsRegularSessionNow()) return null;

            var closes = _candles.Select(c => c.Close).ToList();
            var vols   = _candles.Select(c => c.Volume).ToList();

            double smaFast = IndicatorHelper.CalculateSMA(closes, RegimeFast);
            double smaSlow = IndicatorHelper.CalculateSMA(closes, RegimeSlow);
            double rsi     = IndicatorHelper.CalculateRSI(closes, 14);
            double avgVol  = IndicatorHelper.CalculateAverageVolume(vols, 20);
            double last    = closes.Last();
            double lastVol = vols.Last();
            double atr     = CalculateATR(AtrPeriod);

            bool regimeUp = smaFast > smaSlow;
            bool volOk    = avgVol <= 0 ? true : lastVol > avgVol * 1.05;
            bool rsiOk    = rsi > 40 && rsi < 70;

            var pi = PortfolioManager.Get(Token);

            if (pi.State == PositionState.Flat)
            {
                bool entryRaw = regimeUp && volOk && rsiOk;
                _consecutiveEntry = entryRaw ? _consecutiveEntry + 1 : 0;

                if (_consecutiveEntry >= DebounceCandles && PortfolioManager.CanEnter(Token))
                {
                    double trail = last - AtrTrailMult * atr;
                    PortfolioManager.EnterLong(Token, last, trail);
                    _consecutiveEntry = 0;
                    return new Signal { Type = SignalType.Buy, Price = last, Note = "ENTRY Long (debounced)" };
                }
            }
            else
            {
                double newTrail = last - AtrTrailMult * atr;
                if (newTrail > pi.TrailStop) pi.TrailStop = newTrail;

                bool exitRaw = !regimeUp || last <= pi.TrailStop;
                _consecutiveExit = exitRaw ? _consecutiveExit + 1 : 0;

                if (_consecutiveExit >= DebounceCandles)
                {
                    PortfolioManager.ExitToFlat(Token);
                    _consecutiveExit = 0;
                    return new Signal { Type = SignalType.Sell, Price = last, Note = "EXIT (regime/stop)" };
                }
            }
            return null;
        }

        public IReadOnlyList<Candle> GetCandles() => _candles.AsReadOnly();
    }
}
