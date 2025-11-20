using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZerodhaOxySocket
{
    // PATCHED: Dynamic and consistent option selection logic
    public static class OptionMapper
    {
        public static InstrumentInfo GetOption(string symbol, double spotPrice, DateTime expiry, int offsetFromATM, string type, List<InstrumentInfo> csv)
        {
            int step = symbol.Contains("BANK") ? 100 : 50;
            int atmStrike = (int)(Math.Round(spotPrice / step) * step);
            int strike = atmStrike + (offsetFromATM * step);

            var opt = csv.FirstOrDefault(i =>
                i.Name == symbol
                && i.Segment == "NFO-OPT"
                && i.Expiry?.Date == expiry.Date
                && i.Strike == strike
                && string.Equals(i.InstrumentType, type, StringComparison.OrdinalIgnoreCase));

            return opt;
        }

        public static InstrumentInfo ChooseATMOption(string symbol, double spotPrice, DateTime expiry, string type)
        {
            return GetOption(symbol, spotPrice, expiry, 0, type, csv);
        }

        public static List<DateTime> GetAvailableExpiries(string symbol, List<InstrumentInfo> csv)
        {
            return csv.Where(i => i.Name == symbol && i.Segment == "NFO-OPT" && i.Expiry.HasValue)
                      .Select(i => i.Expiry.Value.Date)
                      .Distinct()
                      .OrderBy(d => d)
                      .ToList();
        }

        public static DateTime? GetNearestExpiry(string symbol)
        {
            return GetAvailableExpiries(symbol, csv).FirstOrDefault(d => d >= DateTime.Today);
        }

        private static List<InstrumentInfo> csv = LoadInstrumentsFromCsv();

        private static List<InstrumentInfo> LoadInstrumentsFromCsv()
        {
            var csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv");
            return InstrumentHelper.LoadInstrumentsFromCsv(csvPath);
        }
    }

}
