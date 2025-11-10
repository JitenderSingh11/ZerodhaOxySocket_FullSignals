using Dapper;
using KiteConnect;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    public class CandleHistoryService
    {
        private static string _connectionString; // Your DB connection string
        private static List<uint> _niftyInstrumentToken; // NIFTY token (update accordingly)
        private static Kite _kiteClient;

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
