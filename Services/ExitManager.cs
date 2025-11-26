using System;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaOxySocket
{
    public class ExitManager
    {
        // Singleton instance for migration convenience
        private static ExitManager _instance;
        public static ExitManager Instance => _instance ??= new ExitManager();

        private readonly Dictionary<Guid, OrderRecord> _open = new();
        private readonly OrderManager _orderManager;

        public ExitManager()
        {
            _orderManager = OrderManager.Instance;
        }

        public void Track(OrderRecord order)
        {
            if (order == null) return;
            _open[order.OrderId] = order;
        }

        // Call this for every option tick (see TickHub patch below)
        public void OnOptionTick(long token, double lastPrice, DateTime tickTime)
        {
            foreach (var kv in _open.ToArray())
            {
                var o = kv.Value;
                if (o.Status != OrderStatus.Open || o.InstrumentToken != token) continue;

                // Underlying ATR (5m)
                double atr = UnderlyingCandleCache.GetAtr(o.UnderlyingToken, Config.Current.Trading.AtrPeriod);

                if (atr <= 0) continue;

                // Conservative deltas (can be improved later)
                double delta = (o.Side == "BUY") ? 0.30 : 0.35;

                // Hard stop / trail distances in option points
                double stopDist = atr * Config.Current.Trading.AtrStopMult * delta;
                double trailDist = atr * Config.Current.Trading.AtrTrailMult * delta;

                double stop = (o.Side == "BUY") ? (o.EntryPrice - stopDist) : (o.EntryPrice + stopDist);

                // track most favorable price since entry
                double favorable = _orderManager.GetFavorablePrice(o.OrderId, lastPrice, o.Side);
                double trail = (o.Side == "BUY") ? (favorable - trailDist) : (favorable + trailDist);

                double trigger = (o.Side == "BUY") ? Math.Max(stop, trail) : Math.Min(stop, trail);
                bool hit = (o.Side == "BUY") ? (lastPrice <= trigger) : (lastPrice >= trigger);

                var eod = TimeSpan.Parse(Config.Current.Trading.EodExit);
                bool eodCut = tickTime.TimeOfDay >= eod;

                if (hit || eodCut)
                {
                    string reason = eodCut ? "EOD" :
                        ((o.Side == "BUY" && lastPrice <= stop) || (o.Side == "SELL" && lastPrice >= stop)) ? "ATR Stop" : "ATR Trail";

                    OrderSimulator.CloseSimTrade(o.ReplayId, o.InstrumentToken, lastPrice, tickTime, reason);
                    _orderManager.MarkClosed(o.OrderId, lastPrice, tickTime, reason);
                    _open.Remove(o.OrderId);
                }
                else
                {
                    _orderManager.UpdateFavorablePrice(o.OrderId, favorable);
                }
            }
        }
    }
}
