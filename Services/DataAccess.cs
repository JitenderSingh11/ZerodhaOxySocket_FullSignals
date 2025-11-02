    using System.Collections.Generic;
    using System.Data.SqlClient;
    using System.Linq;
    using Dapper;
    using System;

    namespace ZerodhaOxySocket
    {
        public static class DataAccess
        {
            private static string _cs = "";

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
    TickTime DATETIME2 NOT NULL DEFAULT SYSDATETIME()
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
    Volume BIGINT NULL
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

            public static void InsertTicksBatch(IEnumerable<TickData> batch)
            {
                using var conn = new SqlConnection(_cs);
                conn.Open();
                using var tran = conn.BeginTransaction();
                var sql = @"
INSERT INTO dbo.Ticks(InstrumentToken,InstrumentName,LastPrice,LastQuantity,Volume,AveragePrice,OpenPrice,HighPrice,LowPrice,ClosePrice,OI,OIChange,BidQty1,BidPrice1,AskPrice1,AskQty1,TickTime)
VALUES(@InstrumentToken,@InstrumentName,@LastPrice,@LastQuantity,@Volume,@AveragePrice,@OpenPrice,@HighPrice,@LowPrice,@ClosePrice,@OI,@OIChange,@BidQty1,@BidPrice1,@AskPrice1,@AskQty1,@TickTime)";
                var rows = batch.Select(t => new {
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
                    t.TickTime
                });
                conn.Execute(sql, rows, transaction: tran);
                tran.Commit();
            }

            public static void InsertCandle(Candle c, uint token, string name, bool isPaper = true)
            {
                using var conn = new SqlConnection(_cs);
                conn.Open();
                conn.Execute(@"
INSERT INTO dbo.Candles(InstrumentToken, InstrumentName, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume)
VALUES(@token, @name, @interval, @time, @o, @h, @l, @c, @v)",
                    new { token = (long)token, name, interval = "1m", time = c.Time, o = c.Open, h = c.High, l = c.Low, c = c.Close, v = (long)c.Volume });
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

            public static System.Collections.Generic.List<Candle> LoadRecentCandlesAggregated(long token, int bars, int tfMinutes)
            {
                const string sql = @"
WITH G AS (
  SELECT
    DATEADD(MINUTE, DATEDIFF(MINUTE, 0, CandleTime)/@tf*@tf, 0) AS BarTime,
    CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume
  FROM dbo.Candles
  WHERE InstrumentToken = @tok AND Interval='1m'
)
SELECT TOP (@bars)
  BarTime AS CandleTime,
  (SELECT TOP 1 OpenPrice  FROM G g2 WHERE g2.BarTime = g.BarTime ORDER BY CandleTime ASC)  AS OpenPrice,
  MAX(HighPrice) AS HighPrice,
  MIN(LowPrice)  AS LowPrice,
  (SELECT TOP 1 ClosePrice FROM G g3 WHERE g3.BarTime = g.BarTime ORDER BY CandleTime DESC) AS ClosePrice,
  SUM(Volume)    AS Volume
FROM G g
GROUP BY BarTime
ORDER BY BarTime DESC;";
                using var conn = new SqlConnection(_cs);
                var rows = conn.Query<Candle>(sql, new { tok = token, bars, tf = tfMinutes }).ToList();
                rows.Reverse();
                return rows;
            }
        }
    }
