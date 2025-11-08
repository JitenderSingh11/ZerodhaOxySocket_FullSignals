using System;
using System.Collections.Generic;

namespace ZerodhaOxySocket
{
    public static class SignalGate
    {
        private static readonly Dictionary<long, (SignalType type, DateTime when)> _last = new();

        public static bool ShouldEmitSignal(long underlyingToken, SignalType type, DateTime when, int debounceCandles)
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
    }
}
