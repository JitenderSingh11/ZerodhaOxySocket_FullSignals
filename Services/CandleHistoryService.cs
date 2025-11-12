using Dapper;
using KiteConnect;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    public class CandleHistoryService
    {
        private static string _connectionString; // Your DB connection string
        private static List<uint> _niftyInstrumentToken; // NIFTY token (update accordingly)
        private static Kite _kiteClient;

        public class ProgressReport
        {
            public int Percent { get; set; }
            public int Processed { get; set; }
            public int Total { get; set; }
            public int CurrentBatch { get; set; }
            public int TotalBatches { get; set; }
            public string Message { get; set; }
        }

        public static Task Initialize(string apiKey, string accessToken, string connString, List<SubscribedInstrument> subscribedInstruments)
        {
            _connectionString = connString;

            _niftyInstrumentToken  = subscribedInstruments
                .Select(i => (uint)i.Token)
                .ToList();

            _kiteClient = new Kite(apiKey);
            _kiteClient.SetAccessToken(accessToken);

            // Start the async method without blocking the UI thread
            return UpsertCandleHistory();
        }

        private static async Task UpsertCandleHistory()
        {
            try
            {
                var lastMonday = GetLastMonday();
                var currentTime = DateTime.Now;

                var candles = new List<CandleData>();
                // Fetch historical candles data (minute-wise)
                foreach (var token in _niftyInstrumentToken)
                {
                    var candleData = GetHistoricalCandles(token, lastMonday, currentTime).Result;
                    candles.AddRange(candleData);
                }

                // Process each fetched candle and insert into the database in batches
                await InsertCandlesInBatches(candles);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during Candle History upsert: {ex.Message}");
                throw ex;
            }
        }

        private static string MapInterval(string interval)
        {
            return interval switch
            {
                "minute" => "1m",
                "day" => "1d",
                "3minute" => "3m",
                "5minute" => "5m",
                "10minute" => "10m",
                "15minute" => "15m",
                "30minute" => "30m",
                "60minute" => "60m",
                _ => interval // fallback to original if not matched
            };
        }

        private static Task<List<CandleData>> GetHistoricalCandles(uint instrumentToken, DateTime from, DateTime to, string interval = "minute")
        {
            var candles = new List<CandleData>();

            try
            {
                var data = _kiteClient.GetHistoricalData(instrumentToken.ToString(), from, to, interval);

                var instrumnentName = ResolveName(instrumentToken);

                // Map the interval to the desired format
                var mappedInterval = MapInterval(interval);

                // Parse the returned data into a list of CandleData
                candles = data.Select(c => new CandleData
                {
                    InstrumentToken = (int)instrumentToken,
                    InstrumentName = instrumnentName, 
                    Interval = mappedInterval,
                    CandleTime = c.TimeStamp,
                    OpenPrice = (double)c.Open,
                    HighPrice = (double)c.High,
                    LowPrice = (double)c.Low,
                    ClosePrice = (double)c.Close,
                    Volume = (long)c.Volume,
                    OI = (long)c.OI
                }).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching historical candles: {ex.Message}");
            }

            return Task.FromResult(candles);
        }

        private static async Task InsertCandlesInBatches(List<CandleData> candles)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var batchSize = 1000; // Define your batch size here (500, 1000, etc.)
                var totalCandles = candles.Count;
                var batches = (int)Math.Ceiling((double)totalCandles / batchSize);

                // Loop through the candles in batches and insert
                for (int batchIndex = 0; batchIndex < batches; batchIndex++)
                {
                    var currentBatch = candles.Skip(batchIndex * batchSize).Take(batchSize).ToList();

                    var sql = @"
                    IF NOT EXISTS(SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'CandlesHistory')
BEGIN

CREATE TABLE CandlesHistory (
    Id BIGINT IDENTITY PRIMARY KEY,
    InstrumentToken BIGINT NOT NULL,
    InstrumentName NVARCHAR(64) NOT NULL,
    Interval VARCHAR(10) NOT NULL, -- e.g., ""1m"" for 1-minute candles
    CandleTime DATETIME2 NOT NULL, -- time of the candle
    OpenPrice DECIMAL(18,2) NOT NULL,
    HighPrice DECIMAL(18,2) NOT NULL,
    LowPrice DECIMAL(18,2) NOT NULL,
    ClosePrice DECIMAL(18,2) NOT NULL,
    Volume BIGINT NULL,
    OI BIGINT NULL,
    DateInserted DATETIME2 DEFAULT GETDATE(),
    UNIQUE (InstrumentToken, CandleTime) -- to prevent duplicate candle inserts
);

END


INSERT INTO CandlesHistory (InstrumentToken, InstrumentName, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume, OI)
                    SELECT @InstrumentToken, @InstrumentName, @Interval, @CandleTime, @OpenPrice, @HighPrice, @LowPrice, @ClosePrice, @Volume, @OI
                    WHERE NOT EXISTS (SELECT 1 FROM CandlesHistory WHERE InstrumentToken = @InstrumentToken AND CandleTime = @CandleTime)";

                    var parameters = currentBatch.Select(c => new
                    {
                        c.InstrumentToken,
                        c.InstrumentName,
                        c.Interval,
                        c.CandleTime,
                        c.OpenPrice,
                        c.HighPrice,
                        c.LowPrice,
                        c.ClosePrice,
                        c.Volume,
                        c.OI
                    });

                    await connection.ExecuteAsync(sql, parameters);
                }
            }
        }

        private static DateTime GetLastMonday()
        {
            var today = DateTime.Now;
            var daysSinceMonday = today.DayOfWeek - DayOfWeek.Monday;
            if (daysSinceMonday < 0)
                daysSinceMonday += 7;
            var lastMonday = today.AddDays(-daysSinceMonday).Date;
            return lastMonday;
        }

        private static string ResolveName(uint token)
        {
            try
            {
                var list = InstrumentHelper.LoadInstrumentsFromCsv(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv"));
                var it = list.FirstOrDefault(x => x.InstrumentToken == token);
                if (it != null) return string.IsNullOrWhiteSpace(it.Tradingsymbol) ? (it.Name ?? token.ToString()) : it.Tradingsymbol;
            }
            catch { }
            return token.ToString();
        }

        /// <summary>
        /// Fetches historical 1-minute candles from Kite and upserts into CandleHistory table
        /// for the specified token between from->to. Batches upserts and reports progress.
        /// </summary>
        public static async Task FetchAndUpsertRangeAsync(uint instrumentToken, DateTime from, DateTime to, string targetTfMinutes,
            IProgress<ProgressReport> progress = null, CancellationToken ct = default)
        {
            // We'll fetch raw 1-minute candles and then (optionally) aggregate to tfMinutes (if needed)
            // For simplicity we use existing GetHistoricalCandles-like logic in the service.
            // If your original service used KiteConnect.KiteConnect instance, reuse it here (ensure tokens set).

            // Example: chunk the fetch into 1-day intervals to avoid huge responses
            var totalList = new List<CandleData>();
            var cursor = from;
            var daySpan = TimeSpan.FromDays(1);

            while (cursor < to)
            {
                ct.ThrowIfCancellationRequested();
                var endChunk = cursor.Add(daySpan);
                if (endChunk > to) endChunk = to;

                progress?.Report(new ProgressReport { Percent = 0, Processed = totalList.Count, Total = -1, Message = $"Fetching {cursor:yyyy-MM-dd} to {endChunk:yyyy-MM-dd}" });

                // You must implement GetHistoricalCandlesFromKite or reuse the existing call in your class
                var chunk = await GetHistoricalCandles((uint)instrumentToken, cursor, endChunk, targetTfMinutes);
                if (chunk != null && chunk.Count > 0) totalList.AddRange(chunk);

                progress?.Report(new ProgressReport { Percent = 0, Processed = totalList.Count, Total = -1, Message = $"Fetched {chunk?.Count ?? 0} candles" });

                cursor = endChunk;
                await Task.Delay(200, ct); // short delay to be nice (adjust or remove based on rate)
            }

            ct.ThrowIfCancellationRequested();

            // If targetTfMinutes > 1, aggregate 1m to targetTf server-side or in-memory. For now assume we store 1m in CandleHistory.
            // We'll upsert the 1m candles into CandleHistory in batches.

            int batchSize = 1000;
            int total = totalList.Count;
            int batches = Math.Max(1, (int)Math.Ceiling(total / (double)batchSize));
            int processed = 0;

            for (int bi = 0; bi < batches; bi++)
            {
                ct.ThrowIfCancellationRequested();
                var batch = totalList.Skip(bi * batchSize).Take(batchSize).ToList();

                // Upsert batch into DB. Implement UpsertCandleHistoryBatch to perform efficient set-based upsert.
                // If you don't have MERGE, you can insert with WHERE NOT EXISTS using table-valued parameter or temporary table.
                InsertCandlesInBatches(batch);

                processed += batch.Count;
                int percent = (int)(processed * 100.0 / Math.Max(1, total));
                progress?.Report(new ProgressReport
                {
                    Percent = percent,
                    Processed = processed,
                    Total = total,
                    CurrentBatch = bi + 1,
                    TotalBatches = batches,
                    Message = $"Upserted batch {bi + 1}/{batches} ({batch.Count} rows)"
                });
                await Task.Delay(50, ct);
            }
        }
    }

    // Helper class to hold Candle data
    public class CandleData
    {
        public int InstrumentToken { get; set; }
        public string InstrumentName { get; set; }
        public string Interval { get; set; }
        public DateTime CandleTime { get; set; }
        public double OpenPrice { get; set; }
        public double HighPrice { get; set; }
        public double LowPrice { get; set; }
        public double ClosePrice { get; set; }
        public long Volume { get; set; }
        public long OI { get; set; }
    }
}
