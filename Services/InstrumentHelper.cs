using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ZerodhaOxySocket
{
    public class InstrumentInfo
    {
        public long InstrumentToken { get; set; }
        public long ExchangeToken { get; set; }
        public string Tradingsymbol { get; set; } = "";
        public string Name { get; set; } = "";
        public double LastPrice { get; set; }
        public DateTime? Expiry { get; set; }
        public double Strike { get; set; }
        public double TickSize { get; set; }
        public int LotSize { get; set; }
        public string InstrumentType { get; set; } = "";
        public string Segment { get; set; } = "";
        public string Exchange { get; set; } = "";
    }

    public static class InstrumentHelper
    {
        private static List<InstrumentInfo>? _cache;
        private static DateTime _cacheDateUtc = DateTime.MinValue;

        public static List<InstrumentInfo> LoadInstrumentsFromCsv(string path)
        {
            if (_cache != null && _cacheDateUtc.Date == DateTime.UtcNow.Date) return _cache;
            var list = new List<InstrumentInfo>();
            if (!File.Exists(path)) return list;

            using var sr = new StreamReader(path);
            _ = sr.ReadLine(); // header

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var p = SplitCsv(line);
                if (p.Length < 12) continue;
                try
                {
                    list.Add(new InstrumentInfo
                    {
                        InstrumentToken = ParseLong(p[0]),
                        ExchangeToken   = ParseLong(p[1]),
                        Tradingsymbol   = p[2],
                        Name            = p[3],
                        LastPrice       = ParseDouble(p[4]),
                        Expiry          = ParseDate(p[5]),
                        Strike          = ParseDouble(p[6]),
                        TickSize        = ParseDouble(p[7]),
                        LotSize         = (int)ParseLong(p[8]),
                        InstrumentType  = p[9],
                        Segment         = p[10],
                        Exchange        = p[11]
                    });
                }
                catch { }
            }
            _cache = list;
            _cacheDateUtc = DateTime.UtcNow;
            return list;
        }

        private static long ParseLong(string s) =>
            long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        private static double ParseDouble(string s) =>
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        private static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return d;
            return null;
        }

        private static string[] SplitCsv(string line)
        {
            var parts = new List<string>();
            bool inQuotes = false;
            var cur = new System.Text.StringBuilder();
            foreach (var ch in line)
            {
                if (ch == '"') { inQuotes = !inQuotes; continue; }
                if (ch == ',' && !inQuotes) { parts.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            parts.Add(cur.ToString());
            return parts.ToArray();
        }

        public static uint GetOptionToken(string symbol, int strike, string side, string? csvPath = null)
        {
            var list = LoadInstrumentsFromCsv(csvPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv"));
            var today = DateTime.Today;
            var options = list.Where(i =>
                    i.Exchange == "NFO" &&
                    (i.InstrumentType.Equals("CE", StringComparison.OrdinalIgnoreCase) ||
                     i.InstrumentType.Equals("PE", StringComparison.OrdinalIgnoreCase)) &&
                    i.InstrumentType.Equals(side, StringComparison.OrdinalIgnoreCase) &&
                    i.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(i.Strike - strike) < 0.001 &&
                    i.Expiry.HasValue &&
                    i.Expiry.Value.Date >= today)
                .OrderBy(i => i.Expiry)
                .ToList();

            return options.Count == 0 ? 0 : (uint)options.First().InstrumentToken;
        }
    }
}
