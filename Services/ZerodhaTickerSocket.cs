using System;
using System.Collections.Generic;
using System.Linq;
using KiteConnect;

namespace ZerodhaOxySocket
{
    public class ZerodhaTickerSocket : IDisposable
    {
        private Ticker _ticker;
        public event Action<IEnumerable<Tick>> OnTicks;
        public event Action<string> OnStatus;

        public void Connect(string apiKey, string accessToken)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(accessToken))
                throw new Exception("ApiKey or AccessToken missing. Refresh token first.");
            try { _ticker?.Close(); } catch { }
            _ticker = new Ticker(apiKey, accessToken, "wss://ws.kite.trade", true);

            _ticker.OnConnect     += () => OnStatus?.Invoke("🟢 Connected");
            _ticker.OnClose       += () => OnStatus?.Invoke("🔴 Closed");
            _ticker.OnReconnect   += () => OnStatus?.Invoke("🟡 Reconnecting");
            _ticker.OnNoReconnect += () => OnStatus?.Invoke("NoReconnect");
            _ticker.OnError       += (e) => OnStatus?.Invoke("Error: " + e);
            _ticker.OnOrderUpdate += (o) => OnStatus?.Invoke("OrderUpdate: " + (o?.OrderId ?? ""));
            _ticker.OnTick += ticks => { if (ticks != null && ticks.Any()) OnTicks?.Invoke(ticks); };

            _ticker.EnableReconnect(5, 50);
            _ticker.Connect();
        }

        public void Subscribe(IEnumerable<uint> tokens, string mode = "full")
        {
            if (_ticker == null) { OnStatus?.Invoke("Subscribe ignored: not connected."); return; }
            var arr = tokens?.Select(t => t.ToString()).ToArray() ?? Array.Empty<string>();
            if (arr.Length == 0) return;
            _ticker.Subscribe(arr);
            var m = mode.ToLower() switch { "ltp" => Constants.MODE_LTP, "quote" => Constants.MODE_QUOTE, _ => Constants.MODE_FULL };
            _ticker.SetMode(arr, m);
            OnStatus?.Invoke($"Subscribed {arr.Length} in {mode.ToUpper()}");
        }

        public void Unsubscribe(IEnumerable<uint> tokens)
        {
            if (_ticker == null) return;
            var arr = tokens?.Select(t => t.ToString()).ToArray() ?? Array.Empty<string>();
            if (arr.Length == 0) return;
            _ticker.UnSubscribe(arr);
            OnStatus?.Invoke($"Unsubscribed {arr.Length}");
        }

        public void Dispose()
        {
            try { _ticker?.Close(); } catch { }
            _ticker = null;
        }
    }
}
