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
        private readonly List<Candle> _candles = new();
        private DateTime _currentCandleTime = default;
        private double _open, _high, _low, _close, _volume;
        private readonly TimeSpan _timeframe;

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

        public IReadOnlyList<Candle> GetCandles() => _candles.AsReadOnly();


        public Candle ProcessTickWithTime(double ltp, DateTime tickTime)
        {
            // compute bucketStart based on tickTime (convert to IST with SessionClock)
            var bucketStart = Clock.FloorToBucketIst(tickTime, _timeframe); // implement a helper to floor to timeframe based on IST or tickTime timezone
                                                                            // then same logic as ProcessTick but use provided times

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

        public void AddCandle(Candle c)
        {
            _candles.Add(c);
            if (_candles.Count > 1000) _candles.RemoveAt(0);
            _currentCandleTime = c.Time;
        }

        public SignalResult EvaluateSignalsPositionAware_Conservative()
        {
            var last = _candles.Count;

            SignalDiagnostics.Info(0, "InstrumentContext", SessionClock.NowIst(), "EvaluateSignalsPositionAware_Conservative", $"Candle Count: {last}");

            int need = Math.Max(Config.Current.Trading.SlowEma,
                        Math.Max(Config.Current.Trading.RsiPeriod, Config.Current.Trading.AtrPeriod)) + 2;
            if (last < need) return null;

            var c0 = _candles[last - 1]; // just closed
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
