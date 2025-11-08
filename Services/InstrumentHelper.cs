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


        /// <summary>
        /// Raw CSV line or original raw string used when snapshotting (optional).
        /// Used by snapshot/CSV save routines.
        /// </summary>
        public string RawLine { get; set; }
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

        private static List<InstrumentInfo> LoadAll()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv");
            return LoadInstrumentsFromCsv(path); // your existing method
        }

        private static DateTime ParseExpiry(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return DateTime.MaxValue;
            // Try common formats used in Zerodha dumps
            if (DateTime.TryParse(s, out var d)) return d;
            // Fallbacks (add more if your dump uses specific patterns)
            string[] fmts = { "yyyy-MM-dd", "dd-MMM-yyyy", "dd-MMM-yy" };
            if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out d)) return d;
            return DateTime.MaxValue;
        }

        public static InstrumentInfo FindLatestByTradingsymbol(string tradingSymbol)
        {
            var all = LoadAll();
            var list = all
                .Where(i => string.Equals(i.Tradingsymbol, tradingSymbol, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (list.Count == 0) return null;

            // Prefer latest expiry if there are multiple rows
            return list
                .OrderByDescending(i => ParseExpiry(i.Expiry?.ToString()))
                .First();
        }

        /// <summary>
        /// Find the nearest option by underlying name, target strike and CE/PE.
        /// Prefers closest strike, then earliest future expiry.
        /// </summary>
        public static InstrumentInfo FindNearestOption(string underlying, int targetStrike, string ceOrPe)
        {
            ceOrPe = (ceOrPe ?? "CE").ToUpperInvariant();
            var all = LoadAll();

            var opts = all.Where(i =>
                    (i.InstrumentType?.ToUpperInvariant() == ceOrPe) &&
                    (i.Segment?.StartsWith("NFO", StringComparison.OrdinalIgnoreCase) ?? false) &&
                    // match underlying in Name or Tradingsymbol (loose match is safer across formats)
                    ((i.Name?.IndexOf(underlying, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                     || (i.Tradingsymbol?.IndexOf(underlying, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0) &&
                    i.Strike > 0
                )
                .ToList();

            if (opts.Count == 0) return null;

            var now = DateTime.Today;

            return opts
                .OrderBy(i => Math.Abs(i.Strike - targetStrike))
                .ThenBy(i => ParseExpiry(i.Expiry?.ToString()) < now ? DateTime.MaxValue : ParseExpiry(i.Expiry?.ToString())) // prefer future/nearest expiry
                .First();
        }

        /// <summary>
        /// Load snapshot rows for the specified date from DB (via DataAccess).
        /// Returns null or empty list if none.
        /// </summary>
        public static List<InstrumentInfo> LoadSnapshot(DateTime date)
        {
            try
            {
                // DataAccess.LoadSnapshotForDate was provided in the patch
                var list = DataAccess.LoadSnapshotForDate(date);
                return list ?? new List<InstrumentInfo>();
            }
            catch
            {
                return new List<InstrumentInfo>();
            }
        }

        /// <summary>
        /// Fallback discovery: find an option instrument for given date/strike and type (CE/PE)
        /// by searching tick records. Returns the first matching InstrumentInfo or null.
        /// </summary> 
        public static InstrumentInfo FindOptionTokenFromTicks(DateTime day, int strike, string ceOrPe)
        {
            // DataAccess.FindDistinctInstrumentsByStrikeAndType returns a list of (Token, Name) tuples
            var rows = DataAccess.FindDistinctInstrumentsByStrikeAndType(day, strike, ceOrPe);
            if (rows == null || rows.Count == 0) return null;

            // pick first candidate and construct a minimal InstrumentInfo
            var first = rows.First();
            return new InstrumentInfo
            {
                InstrumentToken = first.Token,
                Tradingsymbol = first.Name,
                Name = first.Name,
                RawLine = $"{first.Token}\t{first.Name}"
                // other fields unknown from tick-only discovery; left blank/zero
            };
        }

        /// <summary>
        /// Strict parser for Kite instruments with exact 12-column order:
        /// instrument_token, exchange_token, tradingsymbol, name, last_price,
        /// expiry, strike, tick_size, lot_size, instrument_type, segment, exchange
        /// Supports comma or tab separators.
        /// </summary>
        public static List<InstrumentInfo> ParseCsvStrict(string path)
        {
            var lines = File.ReadAllLines(path);
            var list = new List<InstrumentInfo>();
            if (lines.Length == 0) return list;

            bool hasHeader = lines[0].IndexOf("instrument_token", StringComparison.OrdinalIgnoreCase) >= 0;
            var start = hasHeader ? 1 : 0;

            for (int i = start; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var cols = line.Split(new[] { '\t', ',' });
                if (cols.Length < 12) continue;

                // helpers
                static long L(string s) => long.TryParse(s, out var v) ? v : 0;
                static int I(string s) => int.TryParse(s, out var v) ? v : 0;
                static double D(string s) => double.TryParse(s, out var v) ? v : 0;

                var info = new InstrumentInfo
                {
                    RawLine = line,
                    InstrumentToken = L(cols[0]),
                    // cols[1] exchange_token (not stored)
                    Tradingsymbol = cols[2],
                    Name = cols[3],
                    // cols[4] last_price (not stored)
                    Expiry = string.IsNullOrWhiteSpace(cols[5]) ? null : (DateTime?)ParseDate(cols[5]),
                    Strike = D(cols[6]),
                    TickSize = D(cols[7]),
                    LotSize = I(cols[8]),
                    InstrumentType = cols[9],   // CE / PE / FUT / EQ
                    Segment = cols[10],
                    Exchange = cols[11]
                };

                list.Add(info);
            }

            return list;
        }
    }
}
