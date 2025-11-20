using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZerodhaOxySocket.Helpers
{
    // PATCHED: Moved subscription logic into reusable helper for flexibility
    public static class SubscriptionHelper
    {
        public static IEnumerable<uint> GetTokensForAutoSubscribe(AppConfig _config)
        {
            var tokens = new HashSet<uint>();
            int step = 50;

            var csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv");
            var instrumentList = InstrumentHelper.LoadInstrumentsFromCsv(csvPath);

            foreach (var auto in _config.AutoSubscribe)
            {
                string expirySymbol = auto.Symbol; // "NIFTY", "BANKNIFTY"
                string spotSymbol = auto.Symbol == "NIFTY" ? "NIFTY 50" : auto.Symbol; ;
                double spot = MarketDataHelper.GetSpotPrice(_config.ApiKey, _config.AccessToken, spotSymbol, "NSE");
                if (spot <= 0) continue;
                if (auto.Symbol.Contains("BANK")) step = 100;

                var atmStrike = (int)(Math.Round(spot / step) * step);
                var expiries = InstrumentHelper.GetExpiriesFor(expirySymbol, csvPath, DateTime.Today)
                                               .Where(e => e >= DateTime.Today)
                                               .OrderBy(e => e)
                                               .Take(2)
                                               .ToList();

                for (int i = -10; i <= 10; i++)
                {
                    int strike = atmStrike + i * step;
                    foreach (var type in new[] { "CE", "PE" })
                    {
                        foreach (var expiry in expiries)
                        {
                            var opt = InstrumentHelper.GetOptionInstrument(expirySymbol, expiry, strike, type, csvPath);
                            if (opt != null) tokens.Add((uint)opt.InstrumentToken);
                        }
                    }
                }

                var underlying = instrumentList.FirstOrDefault(i => i.Tradingsymbol == spotSymbol && i.Exchange == "NSE");
                if (underlying != null) tokens.Add((uint)underlying.InstrumentToken);
            }

            return tokens;
        }

    }

}
