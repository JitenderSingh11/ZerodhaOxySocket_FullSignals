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
            if (tick == null || tick.TickTime - order.PlacedAt > MaxFillWait)
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

            double fill = tick.LastPrice;
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

        public static void CloseSimTrade(Guid replayId, long instrumentToken, DateTime afterTime, string reason = null)
        {
            var tick = DataAccess.GetFirstTickAfter(instrumentToken, afterTime);
            if (tick == null) return;
            var last = DataAccess.GetLastOpenSimTrade(replayId, instrumentToken);
            if (last == null) return;
            last.ExitTime = tick.TickTime;
            last.ExitPrice = tick.LastPrice;
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
