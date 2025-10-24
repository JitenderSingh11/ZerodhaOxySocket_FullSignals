using System;
using System.Data.SqlClient;

namespace ZerodhaOxySocket
{
    public static class CandleBuilder
    {
        public static void BuildCandlesFromTicks(string cs, string interval = "1m", DateTime? fromUtc = null, DateTime? toUtc = null)
        {
            int minutes = interval.ToLower() switch
            {
                "1m" => 1,
                "5m" => 5,
                "15m" => 15,
                _ => 1
            };
            var from = fromUtc ?? DateTime.UtcNow.Date;
            var to = toUtc ?? DateTime.UtcNow;

            using var conn = new SqlConnection(cs);
            conn.Open();

            string sql = $@"
;WITH B AS (
    SELECT InstrumentToken,
           DATEADD(MINUTE, DATEDIFF(MINUTE, 0, TickTime)/{minutes}*{minutes}, 0) AS Bucket,
           LastPrice, Volume, TickTime
    FROM dbo.Ticks
    WHERE TickTime >= @from AND TickTime < @to
),
O AS (
    SELECT InstrumentToken, Bucket,
           FIRST_VALUE(LastPrice) OVER (PARTITION BY InstrumentToken, Bucket ORDER BY TickTime) AS OpenPrice,
           MAX(LastPrice) AS HighPrice,
           MIN(LastPrice) AS LowPrice,
           LAST_VALUE(LastPrice) OVER (PARTITION BY InstrumentToken, Bucket ORDER BY TickTime ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS ClosePrice,
           MAX(Volume) - MIN(Volume) AS VolDelta
    FROM B
    GROUP BY InstrumentToken, Bucket
)
INSERT INTO dbo.Candles(InstrumentToken, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume)
SELECT InstrumentToken, @interval, Bucket, OpenPrice, HighPrice, LowPrice, ClosePrice, VolDelta
FROM O;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@interval", interval);
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);
            cmd.ExecuteNonQuery();
        }
    }
}
