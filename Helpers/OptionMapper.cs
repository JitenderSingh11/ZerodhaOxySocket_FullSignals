using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZerodhaOxySocket
{
    /// <summary>
    /// Dynamic and consistent option selection logic.
    /// Converted to instance-based to support thread-safe replay and custom instrument lists.
    /// </summary>
    public class OptionMapper
    {
        // Singleton instance for live mode
        private static OptionMapper _instance;
        public static OptionMapper Instance => _instance ??= new OptionMapper();

        // Instance state
        private readonly List<InstrumentInfo> _instruments;

        // Constructor for live mode (loads from default CSV)
        public OptionMapper()
        {
            _instruments = LoadInstrumentsFromCsv();
        }

        // Constructor for replay mode (accepts custom instrument list)
        public OptionMapper(List<InstrumentInfo> instruments)
        {
            _instruments = instruments ?? LoadInstrumentsFromCsv();
        }

        public InstrumentInfo GetOption(string symbol, double spotPrice, DateTime expiry, int offsetFromATM, string type)
        {
            int step = symbol.Contains("BANK") ? 100 : 50;
            int atmStrike = (int)(Math.Round(spotPrice / step) * step);
            int strike = atmStrike + (offsetFromATM * step);

            var opt = _instruments.FirstOrDefault(i =>
                i.Name == symbol
                && i.Segment == "NFO-OPT"
                && i.Expiry?.Date == expiry.Date
                && i.Strike == strike
                && string.Equals(i.InstrumentType, type, StringComparison.OrdinalIgnoreCase));

            return opt;
        }

        public InstrumentInfo ChooseATMOption(string symbol, double spotPrice, DateTime expiry, string type)
        {
            return GetOption(symbol, spotPrice, expiry, 0, type);
        }

        public List<DateTime> GetAvailableExpiries(string symbol)
        {
            return _instruments.Where(i => i.Name == symbol && i.Segment == "NFO-OPT" && i.Expiry.HasValue)
                      .Select(i => i.Expiry.Value.Date)
                      .Distinct()
                      .OrderBy(d => d)
                      .ToList();
        }

        public DateTime? GetNearestExpiry(string symbol)
        {
            return GetAvailableExpiries(symbol).FirstOrDefault(d => d >= DateTime.Today);
        }

        private static List<InstrumentInfo> LoadInstrumentsFromCsv()
        {
            var csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv");
            return InstrumentHelper.LoadInstrumentsFromCsv(csvPath);
        }

        /// <summary>
        /// Reload instruments from CSV (useful for refresh)
        /// </summary>
        public void ReloadInstruments()
        {
            var newInstruments = LoadInstrumentsFromCsv();
            _instruments.Clear();
            _instruments.AddRange(newInstruments);
        }
    }
}
