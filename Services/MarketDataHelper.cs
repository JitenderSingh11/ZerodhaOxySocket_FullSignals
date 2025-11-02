using System;
using KiteConnect;

namespace ZerodhaOxySocket
{
    public static class MarketDataHelper
    {
        public static double GetSpotPrice(string apiKey, string accessToken, string tradingsymbol, string exchange = "NSE")
        {
            try
            {
                var kite = new Kite(apiKey, accessToken);
                var quotes = kite.GetQuote(new string[] { $"{exchange}:{tradingsymbol}" });
                if (quotes != null && quotes.TryGetValue($"{exchange}:{tradingsymbol}", out var q))
                    return (double)q.LastPrice;
            }
            catch (Exception ex) { Console.WriteLine("GetSpotPrice failed: " + ex.Message); }
            return 0;
        }
    }
}
