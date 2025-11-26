using System;

namespace ZerodhaOxySocket.MultiSockets
{
    public class TradeMonitor
    {
        public uint InstrumentToken { get; }
        public double EntryPrice { get; }
        public double TargetPrice { get; }
        public double StopPrice { get; }
        public bool IsActive { get; private set; } = true;

        /// <summary>
        /// Fired when the trade is closed (target or stop hit).
        /// </summary>
        public event Action<TradeMonitor> OnTradeClosed;

        public TradeMonitor(uint token, double entryPrice, double targetPrice, double stopPrice)
        {
            InstrumentToken = token;
            EntryPrice = entryPrice;
            TargetPrice = targetPrice;
            StopPrice = stopPrice;
        }

        /// <summary>
        /// Call this method with each tick to evaluate exit conditions.
        /// </summary>
        public void OnTick(TickData tick)
        {
            if (!IsActive || tick.InstrumentToken != InstrumentToken) return;
            double price = tick.LastPrice;
            if (price >= TargetPrice || price <= StopPrice)
            {
                IsActive = false;
                OnTradeClosed?.Invoke(this);
                // In a real system, place order to exit the trade here
            }
        }
    }
}
