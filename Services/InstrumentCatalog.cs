using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    public static class InstrumentCatalog
    {
        private static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;
        private static string DataDir => Path.Combine(BaseDir, "data");
        private static string SnapDir => Path.Combine(BaseDir, "snapshots");
        private static string TodayCsvPath => Path.Combine(DataDir, $"instruments_{DateTime.Today:yyyyMMdd}.csv");

        /// <summary>
        /// Ensure we have today's instruments: download (if missing), parse strictly, and snapshot idempotently.
        /// </summary>
        public static async Task<List<InstrumentInfo>> EnsureTodayAsync()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(SnapDir);

            if (!File.Exists(TodayCsvPath))
            {
                using var http = new HttpClient();
                // Public CSV endpoint (no auth required)
                var csv = await http.GetStringAsync("https://api.kite.trade/instruments");
                File.WriteAllText(TodayCsvPath, csv);
            }

            var list = InstrumentHelper.ParseCsvStrict(TodayCsvPath); // maps 12 Kite columns
            InstrumentSnapshotService.SaveSnapshotIdempotent(list, DateTime.Today, TodayCsvPath);

            var legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv");
            File.Copy(TodayCsvPath, legacyPath, overwrite: true);

            return list;
        }
    }
}
