using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaOxySocket
{
    /// <summary>
    /// Keeps only underlying candles needed for ATR (fast + in-memory).
    /// Converted to instance-based to support separate live and replay sessions.
    /// </summary>
    public class UnderlyingCandleCache
    {
        // Singleton instance for live mode
        private static UnderlyingCandleCache _instance;
        public static UnderlyingCandleCache Instance => _instance ??= new UnderlyingCandleCache();

        // Instance state
        private readonly Dictionary<long, List<Candle>> _byToken = new();

        // Constructor for replay mode (or live mode via singleton)
        public UnderlyingCandleCache() { }

        public void AddCandleCacheSeeds(long underlyingToken, IEnumerable<Candle> candlesList)
        {
            _byToken.Clear();

            foreach (var c in candlesList)
            {
                Put(underlyingToken, c);
            }
        }

        public void Put(long underlyingToken, Candle c)
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

        public double GetAtr(long underlyingToken, int period)
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

        /// <summary>
        /// Clear all cached candles (useful for replay reset)
        /// </summary>
        public void Clear()
        {
            _byToken.Clear();
        }
    }
}
