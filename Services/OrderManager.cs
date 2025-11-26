using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ZerodhaOxySocket
{
    public class OrderManager
    {
        // Singleton instance for easy migration (optional, can be removed for full DI)
        private static OrderManager _instance;
        public static OrderManager Instance => _instance ??= new OrderManager();

        private readonly ConcurrentDictionary<Guid, OrderRecord> _orders = new();
        private readonly ConcurrentDictionary<Guid, double> _fav = new(); // most favorable price since entry

        public OrderManager() { }

        public void CreateOrder(OrderRecord order)
        {
            _orders[order.OrderId] = order;
            // Optional: persist placed row using your DataAccess if needed
        }

        public void AttachFill(OrderRecord order, SimTrade fill)
        {
            if (fill == null || fill.EntryPrice <= 0) { order.Status = OrderStatus.Unfilled; return; }

            order.EntryTime = fill.EntryTime;
            order.EntryPrice = fill.EntryPrice;
            order.FilledLots = order.QuantityLots;
            order.Status = OrderStatus.Open;

            _orders[order.OrderId] = order;
            _fav[order.OrderId] = order.EntryPrice; // initialize
        }

        public void MarkClosed(Guid orderId, double exitPrice, DateTime exitTime, string reason)
        {
            if (!_orders.TryGetValue(orderId, out var o)) return;
            o.ExitPrice = exitPrice;
            o.ExitTime = exitTime;
            o.ExitReason = reason;
            o.Status = OrderStatus.Closed;

            // compute PnL (per-lot * lots) — adjust if you store lot size multiplier elsewhere
            double pnl = (o.Side == "BUY") ? (exitPrice - o.EntryPrice) : (o.EntryPrice - exitPrice);
            o.Pnl = pnl * o.QuantityLots;
            _orders[o.OrderId] = o;

            // persist to DB (you already have UpdateSimTradeExit — call it as needed)
            // DataAccess.UpdateSimTradeExit(...); // if you prefer syncing here

            _fav.TryRemove(orderId, out _);
        }

        public bool HasOpenPositionForUnderlying(long underlyingToken)
        {
            return _orders.Values.Any(x => x.UnderlyingToken == underlyingToken && x.Status == OrderStatus.Open);
        }

        
        public bool HasOpenPositionForInstrument(long instrumentToken)
        {
            return _orders.Values.Any(x => x.InstrumentToken == instrumentToken && x.Status == OrderStatus.Open);
        }

        public List<OrderRecord> GetOpenOrdersForInstrument(long instrumentToken)
        {
            return _orders.Values
                .Where(x => x.InstrumentToken == instrumentToken && x.Status == OrderStatus.Open)
                .ToList();
        }

        public int GetOpenOrdersCountForUnderlying(long underlyingToken)
        {
            return _orders.Values.Count(x => x.UnderlyingToken == underlyingToken && x.Status == OrderStatus.Open);
        }

        public List<OrderRecord> GetOpenOrdersForUnderlying(long underlyingToken)
        {
            return _orders.Values.Where(x => x.UnderlyingToken == underlyingToken && x.Status == OrderStatus.Open).ToList();
        }

        public double GetFavorablePrice(Guid orderId, double lastPrice, string side)
        {
            if (!_fav.TryGetValue(orderId, out var f)) f = lastPrice;
            if (side == "BUY") f = Math.Max(f, lastPrice);  // higher is favorable
            if (side == "SELL") f = Math.Min(f, lastPrice);  // lower is favorable
            return f;
        }

        public void UpdateFavorablePrice(Guid orderId, double favorable)
        {
            _fav[orderId] = favorable;
        }
    }
}
