using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaOxySocket
{
    public static class IndicatorHelper
    {
        public static double CalculateSMA(List<double> prices, int period)
        {
            if (prices.Count < period) return 0;
            return prices.Skip(prices.Count - period).Take(period).Average();
        }

        public static double CalculateRSI(List<double> closes, int period = 14)
        {
            if (closes.Count < period + 1) return 0;
            double gain = 0, loss = 0;
            for (int i = closes.Count - period; i < closes.Count; i++)
            {
                double change = closes[i] - closes[i - 1];
                if (change > 0) gain += change; else loss -= change;
            }
            if (loss == 0) return 100;
            double rs = gain / loss;
            return 100 - (100 / (1 + rs));
        }

        public static double CalculateVWAP(List<(double price, double volume)> pvList)
        {
            double totalPV = pvList.Sum(x => x.price * x.volume);
            double totalVolume = pvList.Sum(x => x.volume);
            return totalVolume == 0 ? 0 : totalPV / totalVolume;
        }

        public static double CalculateAverageVolume(List<double> volumes, int period)
        {
            if (volumes.Count < period) return 0;
            return volumes.Skip(volumes.Count - period).Take(period).Average();
        }

        public static double EMA(IReadOnlyList<Candle> c, int period, int idx)
        {
            if (c == null || c.Count == 0 || idx <= 0 || period <= 1) return 0;
            idx = Math.Min(idx, c.Count - 1);
            double k = 2.0 / (period + 1.0);

            // seed with SMA of first 'period' values ending at idx
            int start = Math.Max(0, idx - period + 1);
            int n = idx - start + 1;
            double seed = 0;
            for (int i = start; i <= idx; i++) seed += c[i].Close;
            seed /= n;

            double ema = seed;
            for (int i = start; i <= idx; i++)
            {
                var price = c[i].Close;
                ema = price * k + ema * (1 - k);
            }
            return ema;
        }

        // RSI at index 'idx' on Close (Wilder)
        public static double RSI(IReadOnlyList<Candle> c, int period, int idx)
        {
            if (c == null || c.Count <= period || idx <= period) return 50;
            idx = Math.Min(idx, c.Count - 1);
            double gain = 0, loss = 0;
            for (int i = idx - period + 1; i <= idx; i++)
            {
                double diff = c[i].Close - c[i - 1].Close;
                if (diff >= 0) gain += diff; else loss -= diff;
            }
            if (loss == 0) return 100;
            double rs = (gain / period) / (loss / period);
            return 100.0 - (100.0 / (1.0 + rs));
        }

        // ATR at index 'idx' (Wilder)
        public static double ATR(IReadOnlyList<Candle> c, int period, int idx)
        {
            if (c == null || c.Count <= period || idx < period) return 0;
            idx = Math.Min(idx, c.Count - 1);
            double atr = 0;
            for (int i = idx - period + 1; i <= idx; i++)
            {
                double h = c[i].High, l = c[i].Low, pc = c[i - 1].Close;
                double tr = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
                atr += tr;
            }
            return atr / period;
        }
    }
}
