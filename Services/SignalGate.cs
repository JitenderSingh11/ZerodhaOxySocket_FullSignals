using System;
using System.Collections.Generic;

namespace ZerodhaOxySocket
{
    /// <summary>
    /// Signal gate for debouncing signal emissions.
    /// Converted to instance-based to prevent replay/live signal cross-contamination.
    /// </summary>
    public class SignalGate
    {
        // Singleton instance for live mode
        private static SignalGate _instance;
        public static SignalGate Instance => _instance ??= new SignalGate();

        // Instance state
        private readonly Dictionary<long, (SignalType type, DateTime when)> _last = new();

        // Constructor for replay mode (or live mode via singleton)
        public SignalGate() { }

        public bool ShouldEmitSignal(long underlyingToken, SignalType type, DateTime when, int debounceCandles)
        {
            if (!_last.TryGetValue(underlyingToken, out var prev))
            {
                _last[underlyingToken] = (type, when);
                return true;
            }

            var minGap = TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes * Math.Max(1, debounceCandles));
            if (prev.type == type && (when - prev.when) < minGap) return false;

            _last[underlyingToken] = (type, when);
            return true;
        }

        /// <summary>
        /// Clear all signal history (useful for replay reset)
        /// </summary>
        public void Clear()
        {
            _last.Clear();
        }
    }
}
