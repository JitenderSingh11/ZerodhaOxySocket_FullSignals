using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using Dapper;
using System;
using System.Data;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    public static class DataAccess
    {
        private static string _cs = Config.ConnectionString;

        private static readonly TimeZoneInfo IST =
            TimeZoneInfo.FindSystemTimeZoneById(
                System.Environment.OSVersion.Platform == PlatformID.Win32NT ? "India Standard Time" : "Asia/Kolkata");

        public static void InitDb(string cs)
        {
            _cs = cs;
            using var conn = new SqlConnection(_cs);
            conn.Open();
            conn.Execute(@"
IF OBJECT_ID('dbo.Ticks','U') IS NULL BEGIN
CREATE TABLE dbo.Ticks(
    Id BIGINT IDENTITY PRIMARY KEY,
    InstrumentToken BIGINT NOT NULL,
    InstrumentName NVARCHAR(64) NULL,
    LastPrice DECIMAL(18,2) NOT NULL,
    LastQuantity BIGINT NULL,
    Volume BIGINT NULL,
    AveragePrice DECIMAL(18,2) NULL,
    OpenPrice DECIMAL(18,2) NULL,
    HighPrice DECIMAL(18,2) NULL,
    LowPrice DECIMAL(18,2) NULL,
    ClosePrice DECIMAL(18,2) NULL,
    OI BIGINT NULL,
    OIChange BIGINT NULL,
    BidQty1 BIGINT NULL, BidPrice1 DECIMAL(18,2) NULL,
    AskPrice1 DECIMAL(18,2) NULL, AskQty1 BIGINT NULL,
    TickTime DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    DATECREATED DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);
CREATE INDEX IX_Ticks_TokenTime ON dbo.Ticks(InstrumentToken, TickTime);
END");
            conn.Execute(@"
IF OBJECT_ID('dbo.Candles','U') IS NULL BEGIN
CREATE TABLE dbo.Candles(
    Id BIGINT IDENTITY PRIMARY KEY,
    InstrumentToken BIGINT NOT NULL,
    InstrumentName NVARCHAR(64) NULL,
    Interval VARCHAR(10) NOT NULL,
    CandleTime DATETIME2 NOT NULL,
    OpenPrice DECIMAL(18,2) NOT NULL,
    HighPrice DECIMAL(18,2) NOT NULL,
    LowPrice DECIMAL(18,2) NOT NULL,
    ClosePrice DECIMAL(18,2) NOT NULL,
    Volume BIGINT NULL,
    DATECREATED DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);
CREATE INDEX IX_Candles_TokenIntervalTime ON dbo.Candles(InstrumentToken, Interval, CandleTime);
END");
            conn.Execute(@"
IF OBJECT_ID('dbo.Signals','U') IS NULL BEGIN
CREATE TABLE dbo.Signals(
    Id BIGINT IDENTITY PRIMARY KEY,
    InstrumentToken BIGINT NOT NULL,
    InstrumentName NVARCHAR(64) NULL,
    SignalType NVARCHAR(16) NOT NULL,
    Price DECIMAL(18,2) NOT NULL,
    Note NVARCHAR(256) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);
CREATE INDEX IX_Signals_TokenTime ON dbo.Signals(InstrumentToken, CreatedAt);
END");
        }

        public static async Task InsertTicksBatchAsync(IEnumerable<TickData> batch)
        {
            try
            {
                using var conn = new SqlConnection(_cs);
                await conn.OpenAsync();
                using var tran = conn.BeginTransaction();
                var sql = @"
INSERT INTO dbo.Ticks(InstrumentToken,InstrumentName,LastPrice,LastQuantity,Volume,AveragePrice,OpenPrice,HighPrice,LowPrice,ClosePrice,OI,OIChange,BidQty1,BidPrice1,AskPrice1,AskQty1,TickTime,ReceivedAt)
VALUES(@InstrumentToken,@InstrumentName,@LastPrice,@LastQuantity,@Volume,@AveragePrice,@OpenPrice,@HighPrice,@LowPrice,@ClosePrice,@OI,@OIChange,@BidQty1,@BidPrice1,@AskPrice1,@AskQty1,@TickTime,@ReceivedAt)";
                var rows = batch.Select(t => new
                {
                    InstrumentToken = (long)t.InstrumentToken,
                    t.InstrumentName,
                    t.LastPrice,
                    t.LastQuantity,
                    t.Volume,
                    t.AveragePrice,
                    t.OpenPrice,
                    t.HighPrice,
                    t.LowPrice,
                    t.ClosePrice,
                    t.OI,
                    t.OIChange,
                    t.BidQty1,
                    t.BidPrice1,
                    t.AskPrice1,
                    t.AskQty1,
                    // Convert internal UTC to IST for DB storage
                    TickTime = (DateTime)ZerodhaOxySocket.Services.Clock.UtcToIst(t.TickTime),
                    ReceivedAt = (DateTime)ZerodhaOxySocket.Services.Clock.UtcToIst(t.ReceivedAt ?? DateTime.UtcNow)
                });
                await conn.ExecuteAsync(sql, rows, transaction: tran);
                tran.Commit();
            }
            catch (Exception ex)
            {
                await SignalDiagnostics.RejectAsync(0, "DataAccess", ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"InsertTicksBatchAsync failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Bulk insert using SqlBulkCopy. Uses a DataTable and explicit column mappings.
        /// </summary>
        public static async Task InsertTicksBulkAsync(IEnumerable<TickData> batch)
        {
            try
            {
                var dt = new DataTable();
                dt.Columns.Add("InstrumentToken", typeof(long));
                dt.Columns.Add("LastPrice", typeof(decimal));
                dt.Columns.Add("LastQuantity", typeof(long));
                dt.Columns.Add("Volume", typeof(long));
                dt.Columns.Add("AveragePrice", typeof(decimal));
                dt.Columns.Add("OpenPrice", typeof(decimal));
                dt.Columns.Add("HighPrice", typeof(decimal));
                dt.Columns.Add("LowPrice", typeof(decimal));
                dt.Columns.Add("ClosePrice", typeof(decimal));
                dt.Columns.Add("OI", typeof(long));
                dt.Columns.Add("OIChange", typeof(long));
                dt.Columns.Add("BidQty1", typeof(long));
                dt.Columns.Add("BidPrice1", typeof(decimal));
                dt.Columns.Add("AskPrice1", typeof(decimal));
                dt.Columns.Add("AskQty1", typeof(long));
                dt.Columns.Add("TickTime", typeof(DateTime));
                dt.Columns.Add("InstrumentName", typeof(string));
                dt.Columns.Add("ReceivedAt", typeof(DateTime));

                foreach (var t in batch)
                {
                    var row = dt.NewRow();
                    row["InstrumentToken"] = (long)t.InstrumentToken;
                    row["LastPrice"] = Convert.ToDecimal(t.LastPrice);
                    row["LastQuantity"] = t.LastQuantity;
                    row["Volume"] = t.Volume;
                    row["AveragePrice"] = Convert.ToDecimal(t.AveragePrice);
                    row["OpenPrice"] = Convert.ToDecimal(t.OpenPrice);
                    row["HighPrice"] = Convert.ToDecimal(t.HighPrice);
                    row["LowPrice"] = Convert.ToDecimal(t.LowPrice);
                    row["ClosePrice"] = Convert.ToDecimal(t.ClosePrice);
                    row["OI"] = t.OI;
                    row["OIChange"] = t.OIChange;
                    row["BidQty1"] = t.BidQty1;
                    row["BidPrice1"] = Convert.ToDecimal(t.BidPrice1);
                    row["AskPrice1"] = Convert.ToDecimal(t.AskPrice1);
                    row["AskQty1"] = t.AskQty1;
                    // store IST in DB
                    row["TickTime"] = ZerodhaOxySocket.Services.Clock.UtcToIst(t.TickTime);
                    row["InstrumentName"] = t.InstrumentName ?? string.Empty;
                    row["ReceivedAt"] = ZerodhaOxySocket.Services.Clock.UtcToIst(t.ReceivedAt ?? DateTime.UtcNow);
                    dt.Rows.Add(row);
                }

                using var conn = new SqlConnection(_cs);
                await conn.OpenAsync();
                using var bulk = new SqlBulkCopy(conn)
                {
                    DestinationTableName = "dbo.Ticks",
                    BatchSize = Math.Max(1, dt.Rows.Count),
                    BulkCopyTimeout = 600
                };

                // Column mappings
                bulk.ColumnMappings.Add("InstrumentToken", "InstrumentToken");
                bulk.ColumnMappings.Add("LastPrice", "LastPrice");
                bulk.ColumnMappings.Add("LastQuantity", "LastQuantity");
                bulk.ColumnMappings.Add("Volume", "Volume");
                bulk.ColumnMappings.Add("AveragePrice", "AveragePrice");
                bulk.ColumnMappings.Add("OpenPrice", "OpenPrice");
                bulk.ColumnMappings.Add("HighPrice", "HighPrice");
                bulk.ColumnMappings.Add("LowPrice", "LowPrice");
                bulk.ColumnMappings.Add("ClosePrice", "ClosePrice");
                bulk.ColumnMappings.Add("OI", "OI");
                bulk.ColumnMappings.Add("OIChange", "OIChange");
                bulk.ColumnMappings.Add("BidQty1", "BidQty1");
                bulk.ColumnMappings.Add("BidPrice1", "BidPrice1");
                bulk.ColumnMappings.Add("AskPrice1", "AskPrice1");
                bulk.ColumnMappings.Add("AskQty1", "AskQty1");
                bulk.ColumnMappings.Add("TickTime", "TickTime");
                bulk.ColumnMappings.Add("InstrumentName", "InstrumentName");
                bulk.ColumnMappings.Add("ReceivedAt", "ReceivedAt");

                await Task.Run(() => bulk.WriteToServer(dt));
            }
            catch (Exception ex)
            {
                await SignalDiagnostics.RejectAsync(0, "DataAccess", ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"InsertTicksBulkAsync failed: {ex.Message}");
                throw;
            }
        }

        public static async Task InsertCandleAsync(Candle c, uint token, string name, bool isPaper = true)
        {
            try
            {
                using var conn = new SqlConnection(_cs);
                await conn.OpenAsync();

                var interval = $"{Config.Current.Trading.TimeframeMinutes}m"; // e.g. "5m"

                // Convert candle time (internal UTC) to IST for DB
                var candleTimeIst = ZerodhaOxySocket.Services.Clock.UtcToIst(c.Time);

                await conn.ExecuteAsync(@"
INSERT INTO dbo.Candles(InstrumentToken, InstrumentName, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume)
VALUES(@token, @name, @interval, @time, @o, @h, @l, @c, @v)",
                    new { token = (long)token, name, Interval = interval, time = candleTimeIst, o = c.Open, h = c.High, l = c.Low, c = c.Close, v = (long)c.Volume });

            }
            catch (Exception ex)
            {
                await SignalDiagnostics.RejectAsync(0, "DataAccess", ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"InsertCandleAsync failed: {ex.Message}");
            }
        }

        public static async Task InsertSignalAsync(Signal s, uint token, string name, bool isPaper = true)
        {
            try
            {
                using var conn = new SqlConnection(_cs);
                await conn.OpenAsync();
                await conn.ExecuteAsync(@"
INSERT INTO dbo.Signals(InstrumentToken, InstrumentName, SignalType, Price, Note, CreatedAt)
VALUES(@token, @name, @type, @price, @note, @createdAt)",
                    new { token = (long)token, name, type = s.Type.ToString(), price = s.Price, note = s.Note ?? "", createdAt = SessionClock.NowIst() });
            }
            catch (Exception ex)
            {
                await SignalDiagnostics.RejectAsync(token, name, ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"InsertSignalAsync failed: {ex.Message}");
            }
        }

        public static async Task InsertSimTradeAsync(Guid replayId, SimTrade trade)
        {
            try
            {
                using var conn = new SqlConnection(_cs);
                await conn.OpenAsync();
                await conn.ExecuteAsync(@"
INSERT INTO dbo.SimTrades(ReplayId, InstrumentToken, InstrumentName, UnderlyingToken, UnderlyingPrice, TradeSide, QuantityLots, EntryTime, EntryPrice, ExitTime, ExitPrice, Pnl, Reason)
VALUES(@replay, @token, @name, @utok, @uprice, @side, @lots, @et, @ep, @xt, @xp, @pnl, @reason)",
                    new
                    {
                        replay = replayId,
                        token = (long)trade.InstrumentToken,
                        name = trade.InstrumentName,
                        utok = (long?)trade.UnderlyingToken,
                        uprice = trade.UnderlyingPrice,
                        side = trade.TradeSide,
                        lots = trade.QuantityLots,
                        et = trade.EntryTime,
                        ep = trade.EntryPrice,
                        xt = trade.ExitTime,
                        xp = trade.ExitPrice,
                        pnl = trade.Pnl,
                        reason = trade.Reason ?? ""
                    });
            }
            catch (Exception ex)
            {
                await SignalDiagnostics.RejectAsync(0, "DataAccess", ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"InsertSimTradeAsync failed: {ex.Message}");
            }
        }

        public static void InsertTicksBatch(IEnumerable<TickData> batch)
        {
            try
            {
                using var conn = new SqlConnection(_cs);
                conn.Open();
                using var tran = conn.BeginTransaction();
                var sql = @"
INSERT INTO dbo.Ticks(InstrumentToken,InstrumentName,LastPrice,LastQuantity,Volume,AveragePrice,OpenPrice,HighPrice,LowPrice,ClosePrice,OI,OIChange,BidQty1,BidPrice1,AskPrice1,AskQty1,TickTime,ReceivedAt)
VALUES(@InstrumentToken,@InstrumentName,@LastPrice,@LastQuantity,@Volume,@AveragePrice,@OpenPrice,@HighPrice,@LowPrice,@ClosePrice,@OI,@OIChange,@BidQty1,@BidPrice1,@AskPrice1,@AskQty1,@TickTime,@ReceivedAt)";
                var rows = batch.Select(t => new
                {
                    InstrumentToken = (long)t.InstrumentToken,
                    t.InstrumentName,
                    t.LastPrice,
                    t.LastQuantity,
                    t.Volume,
                    t.AveragePrice,
                    t.OpenPrice,
                    t.HighPrice,
                    t.LowPrice,
                    t.ClosePrice,
                    t.OI,
                    t.OIChange,
                    t.BidQty1,
                    t.BidPrice1,
                    t.AskPrice1,
                    t.AskQty1,
                    // Convert internal UTC to IST for DB storage
                    TickTime = (DateTime)ZerodhaOxySocket.Services.Clock.UtcToIst(t.TickTime),
                    ReceivedAt = (DateTime)ZerodhaOxySocket.Services.Clock.UtcToIst(t.ReceivedAt ?? DateTime.UtcNow)
                });
                conn.Execute(sql, rows, transaction: tran);
                tran.Commit();
            }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(0, "DataAccess", ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"InsertTicksBatch failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Bulk insert using SqlBulkCopy. Uses a DataTable and explicit column mappings.
        /// </summary>
        public static void InsertTicksBulk(IEnumerable<TickData> batch)
        {
            try
            {
                var dt = new DataTable();
                dt.Columns.Add("InstrumentToken", typeof(long));
                dt.Columns.Add("LastPrice", typeof(decimal));
                dt.Columns.Add("LastQuantity", typeof(long));
                dt.Columns.Add("Volume", typeof(long));
                dt.Columns.Add("AveragePrice", typeof(decimal));
                dt.Columns.Add("OpenPrice", typeof(decimal));
                dt.Columns.Add("HighPrice", typeof(decimal));
                dt.Columns.Add("LowPrice", typeof(decimal));
                dt.Columns.Add("ClosePrice", typeof(decimal));
                dt.Columns.Add("OI", typeof(long));
                dt.Columns.Add("OIChange", typeof(long));
                dt.Columns.Add("BidQty1", typeof(long));
                dt.Columns.Add("BidPrice1", typeof(decimal));
                dt.Columns.Add("AskPrice1", typeof(decimal));
                dt.Columns.Add("AskQty1", typeof(long));
                dt.Columns.Add("TickTime", typeof(DateTime));
                dt.Columns.Add("InstrumentName", typeof(string));
                dt.Columns.Add("ReceivedAt", typeof(DateTime));

                foreach (var t in batch)
                {
                    var row = dt.NewRow();
                    row["InstrumentToken"] = (long)t.InstrumentToken;
                    row["LastPrice"] = Convert.ToDecimal(t.LastPrice);
                    row["LastQuantity"] = t.LastQuantity;
                    row["Volume"] = t.Volume;
                    row["AveragePrice"] = Convert.ToDecimal(t.AveragePrice);
                    row["OpenPrice"] = Convert.ToDecimal(t.OpenPrice);
                    row["HighPrice"] = Convert.ToDecimal(t.HighPrice);
                    row["LowPrice"] = Convert.ToDecimal(t.LowPrice);
                    row["ClosePrice"] = Convert.ToDecimal(t.ClosePrice);
                    row["OI"] = t.OI;
                    row["OIChange"] = t.OIChange;
                    row["BidQty1"] = t.BidQty1;
                    row["BidPrice1"] = Convert.ToDecimal(t.BidPrice1);
                    row["AskPrice1"] = Convert.ToDecimal(t.AskPrice1);
                    row["AskQty1"] = t.AskQty1;
                    // store IST in DB
                    row["TickTime"] = ZerodhaOxySocket.Services.Clock.UtcToIst(t.TickTime);
                    row["InstrumentName"] = t.InstrumentName ?? string.Empty;
                    row["ReceivedAt"] = ZerodhaOxySocket.Services.Clock.UtcToIst(t.ReceivedAt ?? DateTime.UtcNow);
                    dt.Rows.Add(row);
                }

                using var conn = new SqlConnection(_cs);
                conn.Open();
                using var bulk = new SqlBulkCopy(conn)
                {
                    DestinationTableName = "dbo.Ticks",
                    BatchSize = Math.Max(1, dt.Rows.Count),
                    BulkCopyTimeout = 600
                };

                // Column mappings
                bulk.ColumnMappings.Add("InstrumentToken", "InstrumentToken");
                bulk.ColumnMappings.Add("LastPrice", "LastPrice");
                bulk.ColumnMappings.Add("LastQuantity", "LastQuantity");
                bulk.ColumnMappings.Add("Volume", "Volume");
                bulk.ColumnMappings.Add("AveragePrice", "AveragePrice");
                bulk.ColumnMappings.Add("OpenPrice", "OpenPrice");
                bulk.ColumnMappings.Add("HighPrice", "HighPrice");
                bulk.ColumnMappings.Add("LowPrice", "LowPrice");
                bulk.ColumnMappings.Add("ClosePrice", "ClosePrice");
                bulk.ColumnMappings.Add("OI", "OI");
                bulk.ColumnMappings.Add("OIChange", "OIChange");
                bulk.ColumnMappings.Add("BidQty1", "BidQty1");
                bulk.ColumnMappings.Add("BidPrice1", "BidPrice1");
                bulk.ColumnMappings.Add("AskPrice1", "AskPrice1");
                bulk.ColumnMappings.Add("AskQty1", "AskQty1");
                bulk.ColumnMappings.Add("TickTime", "TickTime");
                bulk.ColumnMappings.Add("InstrumentName", "InstrumentName");
                bulk.ColumnMappings.Add("ReceivedAt", "ReceivedAt");

                bulk.WriteToServer(dt);
            }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(0, "DataAccess", ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"InsertTicksBulk failed: {ex.Message}");
                throw;
            }
        }

        public static void InsertCandle(Candle c, uint token, string name, bool isPaper = true)
        {
            try
            {
                using var conn = new SqlConnection(_cs);
                conn.Open();

                var interval = $"{Config.Current.Trading.TimeframeMinutes}m"; // e.g. "5m"

                // Convert candle time (internal UTC) to IST for DB
                var candleTimeIst = ZerodhaOxySocket.Services.Clock.UtcToIst(c.Time);

                conn.Execute(@"
INSERT INTO dbo.Candles(InstrumentToken, InstrumentName, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume)
VALUES(@token, @name, @interval, @time, @o, @h, @l, @c, @v)",
                    new { token = (long)token, name, Interval = interval, time = candleTimeIst, o = c.Open, h = c.High, l = c.Low, c = c.Close, v = (long)c.Volume });

            }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(0, "DataAccess", ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"Insert Candle failed: {ex.Message}");
            }
        }

        public static void InsertSignal(Signal s, uint token, string name, bool isPaper = true)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            conn.Execute(@"
INSERT INTO dbo.Signals(InstrumentToken, InstrumentName, SignalType, Price, Note, CreatedAt)
VALUES(@token, @name, @type, @price, @note, @createdAt)",
                new { token = (long)token, name, type = s.Type.ToString(), price = s.Price, note = s.Note ?? "", createdAt = SessionClock.NowIst() });
        }

        public static List<Candle> LoadRecentCandlesAggregated(long token, int bars, int tfMinutes, DateTime startDate)
        {
            try
            {
                const string sql = @"
WITH G AS (
  SELECT InstrumentToken, InstrumentName, Interval,
    DATEADD(MINUTE, DATEDIFF(MINUTE, 0, CandleTime)/@tf*@tf, 0) AS BarTime,
    CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume
  FROM dbo.CandlesHistory with (nolock)
  WHERE InstrumentToken = @tok AND Interval='1m' AND CandleTime >= dateadd(dd,-20, @startDate) and CandleTime < @startDate
)
SELECT TOP (@bars)
InstrumentToken, InstrumentName, Interval,
  BarTime AS [Time],
  (SELECT TOP 1 OpenPrice  FROM G g2 WHERE g2.BarTime = g.BarTime ORDER BY CandleTime ASC)  AS [Open],
  MAX(HighPrice) AS [High],
  MIN(LowPrice)  AS [Low],
  (SELECT TOP 1 ClosePrice FROM G g3 WHERE g3.BarTime = g.BarTime ORDER BY CandleTime DESC) AS [Close],
  SUM(Volume)    AS [Volume]
FROM G g
GROUP BY InstrumentToken, InstrumentName, Interval,BarTime
ORDER BY Time DESC;";
                using var conn = new SqlConnection($"{_cs}");
                // normalize input startDate (accept UTC internal) -> convert to IST for DB
                var startIst = ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(startDate));
                var rows = conn.Query<Candle>(sql, new { tok = token, bars, tf = tfMinutes, startDate = startIst }, commandTimeout: 300).ToList();
                // DB stores IST; convert to UTC for internal use
                foreach (var r in rows)
                {
                    r.Time = TimeZoneInfo.ConvertTimeToUtc(r.Time, IST);
                }
                rows.Reverse();
                return rows;
            }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(0, "DataAccess", ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"LoadRecentCandlesAggregated failed: {ex.Message}");
                return new List<Candle>();
            }
        }

        public static Candle LoadInRangeCandlesAggregated(long token, int tfMinutes, DateTime startDate)
        {
            try
            {
                const string sql = @"
WITH G AS (
  SELECT InstrumentToken, InstrumentName, Interval,
    DATEADD(MINUTE, DATEDIFF(MINUTE, 0, CandleTime)/@tf*@tf, 0) AS BarTime,
    CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume
  FROM dbo.CandlesHistory with (nolock)
  WHERE InstrumentToken = @tok AND Interval='1m' AND CandleTime >= dateadd(dd,-20, @startDate) and CandleTime <= dateadd(dd,20, @startDate)
)
SELECT TOP 1
InstrumentToken, InstrumentName, Interval,
  BarTime AS [Time],
  (SELECT TOP 1 OpenPrice  FROM G g2 WHERE g2.BarTime = g.BarTime ORDER BY CandleTime ASC)  AS [Open],
  MAX(HighPrice) AS [High],
  MIN(LowPrice)  AS [Low],
  (SELECT TOP 1 ClosePrice FROM G g3 WHERE g3.BarTime = g.BarTime ORDER BY CandleTime DESC) AS [Close],
  SUM(Volume)    AS [Volume]
FROM G g
WHERE @startDate BETWEEN BARTime AND DATEADD(MINUTE, @tf, BarTime)
GROUP BY InstrumentToken, InstrumentName, Interval,BarTime
ORDER BY Time DESC;";

                using var conn = new SqlConnection($"{_cs}");
                var startIst = ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(startDate));
                var rows = conn.Query<Candle>(sql, new { tok = token, tf = tfMinutes, startDate = startIst }, commandTimeout: 300).ToList();
                if (rows != null && rows.Count > 0)
                {
                    var first = rows.First();
                    first.Time = TimeZoneInfo.ConvertTimeToUtc(first.Time, IST);
                    return first;
                }
                return null;
            }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(0, "DataAccess", ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"LoadInRangeCandlesAggregated failed: {ex.Message}");
                return null;
            }
        }

        public static List<Candle> LoadAggregatedCandles(long token, DateTime from, DateTime to, int tfMinutes)
        {
            const string sql = @"
WITH G AS (
  SELECT
    InstrumentToken, InstrumentName, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume,
    DATEADD(MINUTE, DATEDIFF(MINUTE, 0, CandleTime)/@tf*@tf, 0) AS BarTime
  FROM dbo.CandlesHistory  with (nolock)
  WHERE InstrumentToken = @tok
    AND Interval='1m'
    AND CandleTime >= @from and CandleTime < @to
)
SELECT InstrumentToken, InstrumentName, Interval,
  BarTime AS [Time],
  (SELECT TOP 1 OpenPrice  FROM G g2 WHERE g2.BarTime = g.BarTime ORDER BY CandleTime ASC)  AS [Open],
  MAX(HighPrice) AS [High],
  MIN(LowPrice) AS [Low],
  (SELECT TOP 1 ClosePrice FROM G g2 WHERE g2.BarTime = g.BarTime ORDER BY CandleTime DESC) AS [Close],
  SUM(Volume) AS [Volume]
FROM G g
GROUP BY InstrumentToken, InstrumentName, Interval,BarTime
ORDER BY [Time];
";

            using var conn = new SqlConnection(_cs);
            conn.Open();
            var fromIst = ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(from));
            var toIst = ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(to));
            var rows = conn.Query<Candle>(sql, new { tok = token, from = fromIst, to = toIst, tf = tfMinutes }).ToList();
            // convert DB IST times to UTC for internal use
            foreach (var r in rows)
                r.Time = TimeZoneInfo.ConvertTimeToUtc(r.Time, IST);
            return rows;
        }

        public static void SaveInstrumentSnapshotToDb(DateTime snapshotDate, IEnumerable<InstrumentInfo> instruments)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            using var tran = conn.BeginTransaction();
            const string insert = @"
INSERT INTO dbo.InstrumentSnapshots(SnapshotDate, InstrumentToken, Tradingsymbol, Name, Expiry, Strike, TickSize, LotSize, InstrumentType, Segment, Exchange, RawLine)
VALUES(@date, @token, @symbol, @name, @expiry, @strike, @tickSize, @lot, @itype, @segment, @exchange, @raw)";
            foreach (var i in instruments)
            {
                conn.Execute(insert, new
                {
                    date = snapshotDate.Date,
                    token = (long)i.InstrumentToken,
                    symbol = i.Tradingsymbol,
                    name = i.Name,
                    expiry = string.IsNullOrWhiteSpace(i.Expiry?.ToString()) ? (DateTime?)null : DateTime.Parse(i.Expiry?.ToString()),
                    strike = i.Strike,
                    tick = i.TickSize,
                    lot = i.LotSize,
                    itype = i.InstrumentType,
                    segment = i.Segment,
                    exchange = i.Exchange,
                    raw = i.RawLine
                }, transaction: tran);
            }
            tran.Commit();
        }

        public static int? LoadSnapshotCountForDate(DateTime date)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            const string sql = @"
SELECT count(1) as InstrumentCount
FROM dbo.InstrumentSnapshots  with (nolock)
WHERE SnapshotDate = @d";

            return (int?)conn.ExecuteScalar(sql, new { d = date.Date });
        }

        public static List<InstrumentInfo> LoadSnapshotForDate(DateTime date)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            const string sql = @"
SELECT InstrumentToken, Tradingsymbol, Name, Expiry, Strike, TickSize, LotSize, InstrumentType, Segment, Exchange, RawLine
FROM dbo.InstrumentSnapshots  with (nolock)
WHERE SnapshotDate = @d";
            var rows = conn.Query(sql, new { d = date.Date }).Select(r => new InstrumentInfo
            {
                InstrumentToken = (long)r.InstrumentToken,
                Tradingsymbol = r.Tradingsymbol,
                Name = r.Name,
                Expiry = (DateTime?)r.Expiry,
                Strike = (double?)r.Strike ?? 0,
                TickSize = (double?)r.TickSize ?? 0,
                LotSize = (int?)r.LotSize ?? 0,
                InstrumentType = r.InstrumentType,
                Segment = r.Segment,
                Exchange = r.Exchange,
                RawLine = r.RawLine
            }).ToList();
            return rows;
        }

        public static TickData GetFirstTickAfter(long token, DateTime afterTime)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            const string sql = @"
SELECT TOP (1) InstrumentToken, InstrumentName, LastPrice, LastQuantity, Volume, AveragePrice, OpenPrice, HighPrice, LowPrice, ClosePrice, OI, OIChange, BidQty1, BidPrice1, AskPrice1, AskQty1, TickTime
FROM dbo.Ticks with (nolock)
WHERE InstrumentToken = @tok AND TickTime > @t
ORDER BY TickTime ASC";
            var afterIst = ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(afterTime));
            var r = conn.QueryFirstOrDefault(sql, new { tok = token, t = afterIst });
            if (r == null) return null;
            var ret = new TickData
            {
                InstrumentToken = (uint)(long)r.InstrumentToken,
                InstrumentName = r.InstrumentName,
                LastPrice = (double)r.LastPrice,
                LastQuantity = (long?)r.LastQuantity ?? 0,
                Volume = (long?)r.Volume ?? 0,
                AveragePrice = (double?)r.AveragePrice ?? 0,
                OpenPrice = (double?)r.OpenPrice ?? 0,
                HighPrice = (double?)r.HighPrice ?? 0,
                LowPrice = (double?)r.LowPrice ?? 0,
                ClosePrice = (double?)r.ClosePrice ?? 0,
                OI = (long?)r.OI ?? 0,
                OIChange = (long?)r.OIChange ?? 0,
                BidPrice1 = (double?)r.BidPrice1 ?? 0,
                BidQty1 = (long?)r.BidQty1 ?? 0,
                AskPrice1 = (double?)r.AskPrice1 ?? 0,
                AskQty1 = (long?)r.AskQty1 ?? 0,
                // DB stores IST, convert to UTC for internal use
                TickTime = TimeZoneInfo.ConvertTimeToUtc((DateTime)r.TickTime, IST)
            };
            return ret;
        }

        public static List<TickData> GetTicksRange(DateTime start, DateTime end)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            var sql = @"
SELECT InstrumentToken, InstrumentName, LastPrice, LastQuantity, Volume, AveragePrice,
       OpenPrice, HighPrice, LowPrice, ClosePrice, OI, OIChange, BidQty1, BidPrice1, AskPrice1, AskQty1, TickTime
FROM dbo.Ticks with (nolock)
WHERE TickTime BETWEEN @s AND @e
ORDER BY TickTime ASC";
            var startIst = ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(start));
            var endIst = ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(end));
            var rows = conn.Query<TickData>(sql, new { s = startIst, e = endIst }).ToList();
            // convert TickTime from DB IST to UTC
            foreach (var t in rows)
            {
                t.TickTime = TimeZoneInfo.ConvertTimeToUtc(t.TickTime, IST);
            }
            return rows;
        }

        public static List<TickData> GetTicksRangeForTokens(long[] tokens, DateTime start, DateTime end)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            var sql = @"
SELECT InstrumentToken, InstrumentName, LastPrice, LastQuantity, Volume, AveragePrice,
       OpenPrice, HighPrice, LowPrice, ClosePrice, OI, OIChange, BidQty1, BidPrice1, AskPrice1, AskQty1, TickTime
FROM dbo.Ticks  with (nolock)
WHERE TickTime >= @s AND TickTime < @e
AND InstrumentToken IN @tokens
ORDER BY TickTime ASC";
            var startIst = ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(start));
            var endIst = ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(end));
            var rows = conn.Query<TickData>(sql, new { s = startIst, e = endIst, tokens }, commandTimeout: 100).ToList();
            foreach (var t in rows)
                t.TickTime = TimeZoneInfo.ConvertTimeToUtc(t.TickTime, IST);
            return rows;
        }

        public static IEnumerable<TickData> StreamTicksRange(long[] tokens, DateTime start, DateTime end)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            var sql = $@"
SELECT InstrumentToken, InstrumentName, LastPrice, LastQuantity, Volume, AveragePrice,
       OpenPrice, HighPrice, LowPrice, ClosePrice, OI, OIChange, BidQty1, BidPrice1, AskPrice1, AskQty1, TickTime
FROM dbo.Ticks  with (nolock)
WHERE TickTime BETWEEN @s AND @e
ORDER BY TickTime ASC";

            var dp = new DynamicParameters();
            dp.Add("@s", ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(start)));
            dp.Add("@e", ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(end)));

            var rdr = conn.Query<dynamic>(sql, dp, buffered: false);
            foreach (var r in rdr)
            {
                yield return new TickData
                {
                    InstrumentToken = (uint)(long)r.InstrumentToken,
                    InstrumentName = r.InstrumentName,
                    LastPrice = (double)r.LastPrice,
                    LastQuantity = (long?)r.LastQuantity ?? 0,
                    Volume = (long?)r.Volume ?? 0,
                    AveragePrice = (double?)r.AveragePrice ?? 0,
                    OpenPrice = (double?)r.OpenPrice ?? 0,
                    HighPrice = (double?)r.HighPrice ?? 0,
                    LowPrice = (double?)r.LowPrice ?? 0,
                    ClosePrice = (double?)r.ClosePrice ?? 0,
                    OI = (long?)r.OI ?? 0,
                    OIChange = (long?)r.OIChange ?? 0,
                    BidPrice1 = (double?)r.BidPrice1 ?? 0,
                    BidQty1 = (long?)r.BidQty1 ?? 0,
                    AskPrice1 = (double?)r.AskPrice1 ?? 0,
                    AskQty1 = (long?)r.AskQty1 ?? 0,
                    TickTime = TimeZoneInfo.ConvertTimeToUtc((DateTime)r.TickTime, IST)
                };
            }
        }

        public static List<(long Token, string Name)> FindDistinctInstrumentsByStrikeAndType(DateTime day, int strike, string ceOrPe)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            var dayIst = ZerodhaOxySocket.Services.Clock.UtcToIst(ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(day));
            var s = dayIst.Date;
            var e = dayIst.Date.AddDays(1).AddSeconds(-1);
            string pat = $"%{strike}%{ceOrPe}%";
            var sql = @"
SELECT DISTINCT InstrumentToken, InstrumentName
FROM dbo.Ticks  with (nolock)
WHERE TickTime BETWEEN @s AND @e
  AND InstrumentName LIKE @pat
ORDER BY InstrumentName";
            var rows = conn.Query(sql, new { s, e, pat }).ToList();
            return rows.Select(r => ((long)r.InstrumentToken, (string)r.InstrumentName)).ToList();
        }

        public static void InsertSimTrade(Guid replayId, SimTrade trade)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            conn.Execute(@"
INSERT INTO dbo.SimTrades(ReplayId, InstrumentToken, InstrumentName, UnderlyingToken, UnderlyingPrice, TradeSide, QuantityLots, EntryTime, EntryPrice, ExitTime, ExitPrice, Pnl, Reason)
VALUES(@replay, @token, @name, @utok, @uprice, @side, @lots, @et, @ep, @xt, @xp, @pnl, @reason)",
                new
                {
                    replay = replayId,
                    token = (long)trade.InstrumentToken,
                    name = trade.InstrumentName,
                    utok = (long?)trade.UnderlyingToken,
                    uprice = trade.UnderlyingPrice,
                    side = trade.TradeSide,
                    lots = trade.QuantityLots,
                    et = trade.EntryTime,
                    ep = trade.EntryPrice,
                    xt = trade.ExitTime,
                    xp = trade.ExitPrice,
                    pnl = trade.Pnl,
                    reason = trade.Reason ?? ""
                });
        }

        public static SimTrade GetLastOpenSimTrade(Guid replayId, long instrumentToken)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            var sql = @"
SELECT TOP 1 * FROM dbo.SimTrades  with (nolock)
WHERE ReplayId = @rid AND InstrumentToken = @tok AND ExitTime IS NULL
AND Reason <> 'Unfilled'
ORDER BY EntryTime DESC";
            return conn.QueryFirstOrDefault<SimTrade>(sql, new { rid = replayId, tok = instrumentToken });
        }

        public static void UpdateSimTradeExit(SimTrade trade)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            var sql = @"
UPDATE dbo.SimTrades
SET ExitTime=@xt, ExitPrice=@xp, Pnl=@pnl, Reason=@reason
WHERE Id=@id";
            conn.Execute(sql, new { xt = trade.ExitTime, xp = trade.ExitPrice, pnl = trade.Pnl, reason = trade.Reason, id = trade.Id });
        }

        public static void UpsertInstrumentSnapshot(DateTime date, IEnumerable<InstrumentInfo> items)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            using var tx = conn.BeginTransaction();

            const string sql = @"
MERGE dbo.InstrumentSnapshots AS tgt
USING (VALUES (@date, @token, @symbol, @name, @expiry, @strike, @tick, @lot, @itype, @segment, @exchange, @raw))
     AS src (SnapshotDate, InstrumentToken, Tradingsymbol, Name, Expiry, Strike, TickSize, LotSize, InstrumentType, Segment, Exchange, RawLine)
ON (tgt.SnapshotDate = src.SnapshotDate AND tgt.InstrumentToken = src.InstrumentToken)
WHEN NOT MATCHED THEN
    INSERT (SnapshotDate, InstrumentToken, Tradingsymbol, Name, Expiry, Strike, TickSize, LotSize, InstrumentType, Segment, Exchange, RawLine)
    VALUES (src.SnapshotDate, src.InstrumentToken, src.Tradingsymbol, src.Name, src.Expiry, src.Strike, src.TickSize, src.LotSize, src.InstrumentType, src.Segment, src.Exchange, src.RawLine)
WHEN MATCHED THEN
    UPDATE SET
        Tradingsymbol  = src.Tradingsymbol,
        Name           = src.Name,
        Expiry         = src.Expiry,
        Strike         = src.Strike,
        TickSize       = src.TickSize,
        LotSize        = src.LotSize,
        InstrumentType = src.InstrumentType,
        Segment        = src.Segment,
        Exchange       = src.Exchange,
        RawLine        = src.RawLine;";

            foreach (var i in items)
            {
                DateTime? exp = null;
                if (i.Expiry.HasValue && DateTime.TryParse(i.Expiry.Value.ToString("s"), out var exd))
                    exp = exd.Date;

                conn.Execute(sql, new
                {
                    date = date.Date,
                    token = (long)i.InstrumentToken,
                    symbol = i.Tradingsymbol,
                    name = i.Name,
                    expiry = exp,
                    strike = i.Strike,
                    tick = i.TickSize,
                    lot = i.LotSize,
                    itype = i.InstrumentType,
                    segment = i.Segment,
                    exchange = i.Exchange,
                    raw = i.RawLine ?? string.Empty
                }, transaction: tx);
            }

            tx.Commit();
        }
    }
}
