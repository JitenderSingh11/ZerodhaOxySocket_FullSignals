using System;

namespace ZerodhaOxySocket
{
    public class TickData
    {
        public uint InstrumentToken { get; set; }
        public string InstrumentName { get; set; } = "";
        public double LastPrice { get; set; }
        public long LastQuantity { get; set; }
        public long Volume { get; set; }
        public double AveragePrice { get; set; }
        public double OpenPrice { get; set; }
        public double HighPrice { get; set; }
        public double LowPrice { get; set; }
        public double ClosePrice { get; set; }
        public long OI { get; set; }
        public long OIChange { get; set; }
        public double BidPrice1 { get; set; }
        public long BidQty1 { get; set; }
        public double AskPrice1 { get; set; }
        public long AskQty1 { get; set; }
        public DateTime TickTime { get; set; }

        public DateTime? ReceivedAt { get; set; }

        public bool IsReplay { get; set; }
    }
}
