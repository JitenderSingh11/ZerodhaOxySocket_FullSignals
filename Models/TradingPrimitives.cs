using System;

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
        public DateTime Time { get; set; } = DateTime.Now;
        public SignalType Type { get; set; }
        public double Price { get; set; }
        public string Note { get; set; } = "";
    }
}
