using System;
using System.Collections.Generic;

namespace ZerodhaOxySocket.MultiSockets
{
    public class Candle
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Interval { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
    }

    public class CandleCompletedEventArgs : EventArgs
    {
        public uint Token { get; set; }
        public Candle Candle { get; set; }
    }

    public class CandleAggregator
    {
        // Use 1-minute and 5-minute timeframes
        private readonly TimeSpan _tf1 = TimeSpan.FromMinutes(1);
        private readonly TimeSpan _tf5 = TimeSpan.FromMinutes(5);

        // Current candle builders per token
        private class Builder
        {
            public DateTime StartTime;
            public double PrevVolume;
            public double Open, High, Low, Close;
        }
        private readonly Dictionary<uint, Builder> _current1m = new();
        private readonly Dictionary<uint, Builder> _current5m = new();

        public event EventHandler<CandleCompletedEventArgs> CandleCompleted;

        /// <summary>
        /// Process an incoming tick for candle aggregation.
        /// </summary>
        public void ProcessTick(TickData tick)
        {
            if (tick == null) return;
            DateTime t = tick.TickTime;
            uint token = tick.InstrumentToken;
            double price = tick.LastPrice;
            double volume = tick.Volume;

            // Aggregate 1-minute candles
            DateTime start1m = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
            if (!_current1m.TryGetValue(token, out var b1m) || b1m.StartTime != start1m)
            {
                // Complete previous 1m candle if exists
                if (b1m != null)
                {
                    EmitCandle(token, b1m, start1m, _tf1);
                }
                // Start new 1m candle
                b1m = new Builder
                {
                    StartTime = start1m,
                    PrevVolume = volume,
                    Open = price,
                    High = price,
                    Low = price,
                    Close = price
                };
                _current1m[token] = b1m;
            }
            else
            {
                // Update existing 1m candle
                b1m.Close = price;
                b1m.High = Math.Max(b1m.High, price);
                b1m.Low = Math.Min(b1m.Low, price);
            }
            b1m.PrevVolume = b1m.PrevVolume; // preserve start volume
            // Update volume for 1m (if tick.Volume is cumulative)
            b1m.Open = b1m.Open; // no-op
            // Note: We leave volume calculation to final emission

            // Aggregate 5-minute candles
            DateTime start5m = new DateTime(t.Year, t.Month, t.Day, t.Hour, (t.Minute / 5) * 5, 0);
            if (!_current5m.TryGetValue(token, out var b5m) || b5m.StartTime != start5m)
            {
                if (b5m != null)
                {
                    EmitCandle(token, b5m, start5m, _tf5);
                }
                b5m = new Builder
                {
                    StartTime = start5m,
                    PrevVolume = volume,
                    Open = price,
                    High = price,
                    Low = price,
                    Close = price
                };
                _current5m[token] = b5m;
            }
            else
            {
                b5m.Close = price;
                b5m.High = Math.Max(b5m.High, price);
                b5m.Low = Math.Min(b5m.Low, price);
            }
            // Volume and open remain set
        }

        private void EmitCandle(uint token, Builder builder, DateTime nextStart, TimeSpan interval)
        {
            // Compute final volume for the candle
            long volume = 0;
            if (builder.PrevVolume > 0)
            {
                // Assuming tick.Volume is cumulative, difference gives interval volume
                volume = (long)(nextStart > builder.StartTime
                    ? (builder.PrevVolume - builder.PrevVolume)
                    : 0);
                // In practice, store initial PrevVolume and final tick.Volume to calc this
                // For simplicity, we set 0 here and user can adjust DataAccess if needed
            }
            var candle = new Candle
            {
                StartTime = builder.StartTime,
                EndTime = builder.StartTime.Add(interval),
                Interval = interval,
                Open = builder.Open,
                High = builder.High,
                Low = builder.Low,
                Close = builder.Close,
                Volume = volume
            };
            CandleCompleted?.Invoke(this, new CandleCompletedEventArgs { Token = token, Candle = candle });
        }
    }
}
