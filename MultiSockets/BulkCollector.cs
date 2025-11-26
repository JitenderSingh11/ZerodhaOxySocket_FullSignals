using KiteConnect;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ZerodhaOxySocket.MultiSockets
{
    public class BulkCollector : IDisposable
    {
        private readonly ZerodhaTickerSocket _socket;
        private readonly TickWriter _tickWriter;
        private readonly HashSet<uint> _subscribedTokens = new();

        /// <summary>
        /// Raised for each tick received. Subscribers can use this (e.g. TradeMonitor).
        /// </summary>
        public event Action<TickData> TickReceived;

        public BulkCollector(string apiKey, string accessToken, TickWriter tickWriter)
        {
            _tickWriter = tickWriter ?? throw new ArgumentNullException(nameof(tickWriter));
            _socket = new ZerodhaTickerSocket();
            _socket.OnTicks += HandleTicks;
            _socket.OnStatus += status => Console.WriteLine($"BulkCollector status: {status}");
            _socket.Connect(apiKey, accessToken);
        }

        /// <summary>
        /// Subscribe to a set of option instrument tokens.
        /// </summary>
        public void Subscribe(IEnumerable<uint> tokens)
        {
            foreach (var token in tokens)
            {
                if (_subscribedTokens.Add(token))
                {
                    _socket.Subscribe(new[] { token }, "full");
                }
            }
        }
        
        private void HandleTicks(object payload)
        {
            // Assume payload is a list or single Tick from KiteConnect
            // Use dynamic to handle IList<Tick> or single Tick
            try
            {
                if (payload is IList<Tick> tickList)
                {
                    foreach (var kt in tickList)
                        EnqueueTick(kt);
                }
                else if (payload is IEnumerable<Tick> tickEnum)
                {
                    foreach (var kt in tickEnum)
                        EnqueueTick(kt);
                }
                else
                {
                    EnqueueTick(payload);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BulkCollector tick processing error: {ex.Message}");
            }
        }

        private void EnqueueTick(dynamic kt)
        {
            uint token = (uint)kt.InstrumentToken;
            double price = (double)kt.LastPrice;
            long qty = (long)(kt.LastQuantity ?? 0);
            long vol = (long)(kt.Volume ?? 0);
            double averagePrice = (double)kt.AveragePrice;
            double openPrice = (double)kt.Open;
            double highPrice = (double)kt.High;
            double lowPrice = (double)kt.Low;
            double closePrice = (double)kt.Close;
            long OI = (long)kt.OI;

            DateTime tickTime = SessionClock.NowIst();
            try
            {
                if (kt.LastTradeTime is DateTime dt)
                    tickTime = dt;
            }
            catch { /* fallback remains */ }

            var tick = new TickData
            {
                InstrumentToken = token,
                LastPrice = price,
                LastQuantity = qty,
                Volume = vol,
                TickTime = tickTime,
                ReceivedAt = SessionClock.NowIst(),
                AveragePrice = averagePrice,
                OpenPrice = openPrice,
                HighPrice = highPrice,
                LowPrice = lowPrice,
                ClosePrice = closePrice,
                OI = OI,
                IsReplay = false
            };

            // Enqueue to DB write channel
            _tickWriter.EnqueueTick(tick);

            // Raise event for real-time consumers (e.g. TradeMonitor)
            TickReceived?.Invoke(tick);
        }

        public void Dispose()
        {
            try
            {
                if (_socket != null)
                {
                    _socket.OnTicks -= HandleTicks;
                    _socket.Dispose();
                }
            }
            catch { }
        }
    }
}
