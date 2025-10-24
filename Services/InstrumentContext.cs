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

        public InstrumentContext(uint token, string name, TimeSpan timeframe)
        {
            Token = token; Name = name; _timeframe = timeframe;
        }

        public Candle ProcessTick(double ltp, double tickQty)
        {
            DateTime now = DateTime.UtcNow;
            var bucketStart = new DateTime((long)(Math.Floor(now.Ticks / (double)_timeframe.Ticks) * _timeframe.Ticks), DateTimeKind.Utc);

            if (_currentCandleTime == default || bucketStart != _currentCandleTime)
            {
                Candle closed = null;
                if (_currentCandleTime != default)
                {
                    closed = new Candle { Time = _currentCandleTime.ToLocalTime(), Open = _open, High = _high, Low = _low, Close = _close, Volume = _volume };
                    _candles.Add(closed);
                    if (_candles.Count > 500) _candles.RemoveAt(0);
                }
                _currentCandleTime = bucketStart;
                _open = _high = _low = _close = ltp;
                _volume = tickQty;
                return closed;
            }
            else
            {
                _close = ltp;
                _high = Math.Max(_high, ltp);
                _low = Math.Min(_low, ltp);
                _volume += tickQty;
                return null;
            }
        }

        public Signal EvaluateSignals()
        {
            var closes = _candles.Select(c => c.Close).ToList();
            var vols = _candles.Select(c => c.Volume).ToList();
            var pv = _candles.Select(c => (c.Close, c.Volume)).ToList();
            if (closes.Count < 50) return null;

            double sma20 = IndicatorHelper.CalculateSMA(closes, 20);
            double sma50 = IndicatorHelper.CalculateSMA(closes, 50);
            double rsi = IndicatorHelper.CalculateRSI(closes);
            double vwap = IndicatorHelper.CalculateVWAP(pv);
            double avgVol = IndicatorHelper.CalculateAverageVolume(vols, 20);

            double lastPrice = closes.Last();
            double lastVol = vols.Last();

            bool buySignal  = sma20 > sma50 && rsi < 30 && lastPrice > vwap && lastVol > avgVol * 1.5;
            bool sellSignal = rsi > 70 || sma20 < sma50;

            if (buySignal)  return new Signal { Time = DateTime.Now, Type = SignalType.Buy,  Price = lastPrice };
            if (sellSignal) return new Signal { Time = DateTime.Now, Type = SignalType.Sell, Price = lastPrice };
            return null;
        }

        public IReadOnlyList<Candle> GetCandles() => _candles.AsReadOnly();
    }
}
