using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using Dapper;
using System;

namespace ZerodhaOxySocket
{
    public static class DataAccess
    {
        private static string _cs = Config.ConnectionString;

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

            public static void InsertTicksBatch(IEnumerable<TickData> batch)
            {
            try
            {
                using var conn = new SqlConnection(_cs);
                conn.Open();
                using var tran = conn.BeginTransaction();
                var sql = @"
INSERT INTO dbo.Ticks(InstrumentToken,InstrumentName,LastPrice,LastQuantity,Volume,AveragePrice,OpenPrice,HighPrice,LowPrice,ClosePrice,OI,OIChange,BidQty1,BidPrice1,AskPrice1,AskQty1,TickTime)
VALUES(@InstrumentToken,@InstrumentName,@LastPrice,@LastQuantity,@Volume,@AveragePrice,@OpenPrice,@HighPrice,@LowPrice,@ClosePrice,@OI,@OIChange,@BidQty1,@BidPrice1,@AskPrice1,@AskQty1,@TickTime)";
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
                    t.TickTime
                });
                conn.Execute(sql, rows, transaction: tran);
                tran.Commit();
            }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(0, "DataAccess", SessionClock.NowIst(), $"InsertTicksBatch failed: {ex.Message}");
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

            conn.Execute(@"
INSERT INTO dbo.Candles(InstrumentToken, InstrumentName, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume)
VALUES(@token, @name, @interval, @time, @o, @h, @l, @c, @v)",
                    new { token = (long)token, name, Interval = interval, time = c.Time, o = c.Open, h = c.High, l = c.Low, c = c.Close, v = (long)c.Volume });

            }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(0, "DataAccess", SessionClock.NowIst(), $"Insert Candle failed: {ex.Message}");
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
                var rows = conn.Query<Candle>(sql, new { tok = token, bars, tf = tfMinutes, startDate }, commandTimeout: 300).ToList();
                rows.Reverse();
                return rows;
            }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(0, "DataAccess", SessionClock.NowIst(), $"LoadRecentCandlesAggregated failed: {ex.Message}");
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
                var rows = conn.Query<Candle>(sql, new { tok = token, tf = tfMinutes, startDate }, commandTimeout: 300).ToList();
                return rows?.FirstOrDefault();
            }
            catch (Exception ex)
            {
                SignalDiagnostics.Reject(0, "DataAccess", SessionClock.NowIst(), $"LoadInRangeCandlesAggregated failed: {ex.Message}");
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
            return conn.Query<Candle>(sql, new { tok = token, from, to, tf = tfMinutes }).ToList();
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
                    tickSize = i.TickSize,
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
            var r = conn.QueryFirstOrDefault(sql, new { tok = token, t = afterTime });
            if (r == null) return null;
            return new TickData
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
                TickTime = (DateTime)r.TickTime
            };
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
            return conn.Query<TickData>(sql, new { s = start, e = end }).ToList();
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
            return conn.Query<TickData>(sql, new { s = start, e = end, tokens }, commandTimeout: 100).ToList();
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
            dp.Add("@s", start);
            dp.Add("@e", end);

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
                    TickTime = (DateTime)r.TickTime
                };
            }
        }

        public static List<(long Token, string Name)> FindDistinctInstrumentsByStrikeAndType(DateTime day, int strike, string ceOrPe)
        {
            using var conn = new SqlConnection(_cs);
            conn.Open();
            var s = day.Date;
            var e = day.Date.AddDays(1).AddSeconds(-1);
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
