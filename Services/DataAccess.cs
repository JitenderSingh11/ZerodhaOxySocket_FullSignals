using System.Collections.Generic;
using System.Data.SqlClient;
using Dapper;

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
    TickTime DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
) END");
            conn.Execute(@"
IF OBJECT_ID('dbo.Candles','U') IS NULL BEGIN
CREATE TABLE dbo.Candles(
    Id BIGINT IDENTITY PRIMARY KEY,
    InstrumentToken BIGINT NOT NULL,
    Interval VARCHAR(10) NOT NULL,
    CandleTime DATETIME2 NOT NULL,
    OpenPrice DECIMAL(18,2) NOT NULL,
    HighPrice DECIMAL(18,2) NOT NULL,
    LowPrice DECIMAL(18,2) NOT NULL,
    ClosePrice DECIMAL(18,2) NOT NULL,
    Volume BIGINT NULL
) END");
            conn.Execute(@"
IF OBJECT_ID('dbo.Signals','U') IS NULL BEGIN
CREATE TABLE dbo.Signals(
    Id BIGINT IDENTITY PRIMARY KEY,
    InstrumentToken BIGINT NOT NULL,
    InstrumentName NVARCHAR(64) NULL,
    SignalType NVARCHAR(16) NOT NULL,
    Price DECIMAL(18,2) NOT NULL,
    Note NVARCHAR(256) NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
) END");
        }

        public static void InsertTicksBatch(IEnumerable<TickData> batch)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            using var tran = conn.BeginTransaction();
            var sql = @"INSERT INTO dbo.Ticks(InstrumentToken,LastPrice,LastQuantity,Volume,AveragePrice,OpenPrice,HighPrice,LowPrice,ClosePrice,OI,OIChange,BidQty1,BidPrice1,AskPrice1,AskQty1,TickTime)
                        VALUES(@InstrumentToken,@LastPrice,@LastQuantity,@Volume,@AveragePrice,@OpenPrice,@HighPrice,@LowPrice,@ClosePrice,@OI,@OIChange,@BidQty1,@BidPrice1,@AskPrice1,@AskQty1,@TickTime)";
            conn.Execute(sql, batch, transaction: tran);
            tran.Commit();
        }

        public static void InsertCandle(Candle c, uint token, string name, bool isPaper = true)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            conn.Execute(@"INSERT INTO dbo.Candles(InstrumentToken, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume)
                   VALUES(@token, @interval, @time, @o, @h, @l, @c, @v)",
                new { token = (long)token, interval = "1m", time = c.Time, o = c.Open, h = c.High, l = c.Low, c = c.Close, v = (long)c.Volume });
        }

        public static void InsertSignal(Signal s, uint token, string name, bool isPaper = true)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            conn.Execute(@"INSERT INTO dbo.Signals(InstrumentToken, InstrumentName, SignalType, Price, Note)
                   VALUES(@token, @name, @type, @price, @note)",
                new { token = (long)token, name, type = s.Type.ToString(), price = s.Price, note = s.Note ?? "" });
        }
    }
}
