using System;

namespace ZerodhaOxySocket
{
    public class SimTrade
    {
        public long Id { get; set; }
        public Guid ReplayId { get; set; }
        public long InstrumentToken { get; set; }
        public string InstrumentName { get; set; }
        public long? UnderlyingToken { get; set; }
        public double UnderlyingPrice { get; set; }
        public string TradeSide { get; set; } // BUY/SELL
        public int QuantityLots { get; set; }
        public DateTime EntryTime { get; set; }
        public double EntryPrice { get; set; }
        public DateTime? ExitTime { get; set; }
        public double? ExitPrice { get; set; }
        public double? Pnl { get; set; }
        public string Reason { get; set; }
    }

    public class SimOrder
    {
        public Guid ReplayId { get; set; }
        public uint InstrumentToken { get; set; }
        public string InstrumentName { get; set; }
        public long? UnderlyingToken { get; set; }
        public double UnderlyingPrice { get; set; }
        public string Side { get; set; } = "BUY";
        public int QuantityLots { get; set; } = 1;
        public DateTime PlacedAt { get; set; }
        public string Reason { get; set; }
    }

    public static class OrderSimulator
    {
        public static TimeSpan MaxFillWait = TimeSpan.FromMinutes(5);

        public static SimTrade PlaceOrderNextTick(Guid replayId, SimOrder order)
        {
            var tick = DataAccess.GetFirstTickAfter(order.InstrumentToken, order.PlacedAt);
            Candle candle = null;

            if (replayId != Guid.Empty && tick == null)
            {
                candle = DataAccess.LoadInRangeCandlesAggregated(order.InstrumentToken, Config.Current.Trading.TimeframeMinutes, order.PlacedAt);
            }

            if ((tick == null && candle == null) || (tick?.TickTime - order.PlacedAt > MaxFillWait || candle?.Time - order.PlacedAt > MaxFillWait))
            {
                var unfilled = new SimTrade
                {
                    ReplayId = replayId,
                    InstrumentToken = order.InstrumentToken,
                    InstrumentName = order.InstrumentName,
                    UnderlyingToken = order.UnderlyingToken,
                    UnderlyingPrice = order.UnderlyingPrice,
                    TradeSide = order.Side,
                    QuantityLots = order.QuantityLots,
                    EntryTime = order.PlacedAt,
                    EntryPrice = 0,
                    Reason = "Unfilled"
                };
                DataAccess.InsertSimTrade(replayId, unfilled);
                return unfilled;
            }

            double fill = tick?.LastPrice ?? default;

            
            if (fill == default && candle != null)
            {
                var orderCloserToCandleOpen = Math.Abs((candle.Time - order.PlacedAt).TotalSeconds);
                var orderCloserToCandleClose = Math.Abs((candle.Time.AddMinutes(Config.Current.Trading.TimeframeMinutes) - order.PlacedAt).TotalSeconds);

                fill = orderCloserToCandleOpen < orderCloserToCandleClose ? candle.Open : candle.Close;
            }

            var sim = new SimTrade
            {
                ReplayId = replayId,
                InstrumentToken = order.InstrumentToken,
                InstrumentName = order.InstrumentName,
                UnderlyingToken = order.UnderlyingToken,
                UnderlyingPrice = order.UnderlyingPrice,
                TradeSide = order.Side,
                QuantityLots = order.QuantityLots,
                EntryTime = tick.TickTime,
                EntryPrice = fill,
                Reason = order.Reason
            };
            DataAccess.InsertSimTrade(replayId, sim);
            return sim;
        }

        public static void CloseSimTrade(Guid replayId, long instrumentToken, double lastPrice, DateTime tickTime, string reason = null)
        {
            var last = DataAccess.GetLastOpenSimTrade(replayId, instrumentToken);
            if (last == null) return;
            last.ExitTime = tickTime;
            last.ExitPrice = lastPrice;
            double pnl = 0;
            if (string.Equals(last.TradeSide, "BUY", StringComparison.OrdinalIgnoreCase))
                pnl = (last.ExitPrice.Value - last.EntryPrice) * last.QuantityLots;
            else
                pnl = (last.EntryPrice - last.ExitPrice.Value) * last.QuantityLots;
            last.Pnl = pnl;
            last.Reason = reason ?? last.Reason;
            DataAccess.UpdateSimTradeExit(last);
        }
    }
}
