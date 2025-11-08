using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaOxySocket
{
    // Keeps only underlying candles needed for ATR (fast + in-memory)
    public static class UnderlyingCandleCache
    {
        private static readonly Dictionary<long, List<Candle>> _byToken = new();

        public static void Put(long underlyingToken, Candle c)
        {
            if (!_byToken.TryGetValue(underlyingToken, out var list))
            {
                list = new List<Candle>(2048);
                _byToken[underlyingToken] = list;
            }
            list.Add(c);
            // trim to last N to keep memory sane
            if (list.Count > 5000) list.RemoveRange(0, list.Count - 5000);
        }

        public static double GetAtr(long underlyingToken, int period)
        {
            if (!_byToken.TryGetValue(underlyingToken, out var list)) return 0;
            if (list.Count < period + 2) return 0;

            // Wilder ATR on last 'period' bars
            int idx = list.Count - 1;
            double sum = 0;
            for (int i = idx - period + 1; i <= idx; i++)
            {
                var c = list[i];
                var pc = list[i - 1];
                double tr = Math.Max(c.High - c.Low,
                              Math.Max(Math.Abs(c.High - pc.Close), Math.Abs(c.Low - pc.Close)));
                sum += tr;
            }
            return sum / period;
        }
    }
}
