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
    }
}
