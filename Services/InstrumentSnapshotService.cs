using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZerodhaOxySocket
{
    public static class InstrumentSnapshotService
    {
        /// <summary>
        /// Idempotent snapshot: (a) DB upsert (unique by date+token), (b) CSV only once per day.
        /// </summary>
        public static void SaveSnapshotIdempotent(IEnumerable<InstrumentInfo> instruments, DateTime snapshotDate, string sourceCsvPath)
        {
            // 1) DB upsert
            DataAccess.UpsertInstrumentSnapshot(snapshotDate, instruments);

            // 2) CSV file once per day
            var snapDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "snapshots");
            Directory.CreateDirectory(snapDir);
            var dest = Path.Combine(snapDir, $"instruments_{snapshotDate:yyyyMMdd}.csv");

            if (!File.Exists(dest))
            {
                if (File.Exists(sourceCsvPath))
                {
                    File.Copy(sourceCsvPath, dest);
                }
                else
                {
                    // reconstruct from objects if source not available
                    File.WriteAllLines(dest, instruments.Select(i =>
                        i.RawLine ?? $"{i.InstrumentToken}\t{i.Tradingsymbol}\t{i.Name}\t{i.Expiry}\t{i.Strike}\t{i.TickSize}\t{i.LotSize}\t{i.InstrumentType}\t{i.Segment}\t{i.Exchange}"));
                }
            }
        }
    }
}
