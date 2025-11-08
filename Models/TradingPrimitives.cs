using System;
using ZerodhaOxySocket.Services;

namespace ZerodhaOxySocket
{
    public enum SignalType { Buy, Sell }

    public class Candle
    {
        public DateTime Time { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low  { get; set; }
        public double Close { get; set; }
        public double Volume { get; set; }
    }

    public class Signal
    {
        public DateTime Time { get; set; } = Clock.NowIst();
        public SignalType Type { get; set; }
        public double Price { get; set; }
        public string Note { get; set; } = "";
    }


    public sealed class SignalResult
    {
        public SignalType Type { get; set; }
        public double Price { get; set; }
        public DateTime Time { get; set; }
        public string Meta { get; set; } = "";
    }

    public enum OrderStatus { Placed, Open, Closed, Unfilled, Cancelled }

    public sealed class OrderRecord
    {
        public Guid OrderId { get; set; }
        public Guid SignalId { get; set; }
        public Guid ReplayId { get; set; } = Guid.Empty;
        public long InstrumentToken { get; set; }
        public string InstrumentName { get; set; } = "";
        public long UnderlyingToken { get; set; }
        public double UnderlyingPriceAtSignal { get; set; }
        public string Side { get; set; } = "BUY";
        public int QuantityLots { get; set; } = 1;
        public int FilledLots { get; set; }
        public double EntryPrice { get; set; }
        public DateTime? EntryTime { get; set; }
        public double? ExitPrice { get; set; }
        public DateTime? ExitTime { get; set; }
        public string ExitReason { get; set; } = "";
        public OrderStatus Status { get; set; } = OrderStatus.Placed;
        public double? Pnl { get; set; }
    }
}
