using KiteConnect;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ZerodhaOxySocket.MultiSockets
{
    public class Signal
    {
        public SignalType Type { get; set; }
        public double Price { get; set; }
        public string InstrumentName { get; set; }
    }

    public class SignalEventArgs : EventArgs
    {
        public string InstrumentName { get; set; }
        public Signal Signal { get; set; }
    }

    public class SignalEngine : IDisposable
    {
        private readonly ZerodhaTickerSocket _socket;
        private readonly CandleAggregator _aggregator;
        private readonly Dictionary<uint, string> _tokenNames = new();

        /// <summary>
        /// Fired when a strategy signal is generated.
        /// </summary>
        public event EventHandler<SignalEventArgs> OnSignal;

        public SignalEngine(string apiKey, string accessToken, CandleAggregator aggregator)
        {
            _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
            _aggregator.CandleCompleted += Aggregator_CandleCompleted;

            _socket = new ZerodhaTickerSocket();
            _socket.OnTicks += HandleTicks;
            _socket.OnStatus += status => Console.WriteLine($"SignalEngine status: {status}");
            _socket.Connect(apiKey, accessToken);
        }

        /// <summary>
        /// Subscribe to index tokens by their token IDs (e.g. NIFTY, BANKNIFTY).
        /// </summary>
        public void Subscribe(Dictionary<uint, string> tokenNameMap)
        {
            foreach (var kvp in tokenNameMap)
            {
                var token = kvp.Key;
                var name = kvp.Value;
                if (!_tokenNames.ContainsKey(token))
                {
                    _tokenNames[token] = name;
                    _socket.Subscribe(new[] { token }, "full");
                }
            }
        }

        private void HandleTicks(object payload)
        {
            // Similar to BulkCollector: parse ticks and forward to aggregator
            try
            {
                if (payload is IList<Tick> tickList)
                {
                    foreach (var kt in tickList)
                        FeedToAggregator(kt);
                }
                else if (payload is IEnumerable<Tick> tickEnum)
                {
                    foreach (var kt in tickEnum)
                        FeedToAggregator(kt);
                }
                else
                {
                    FeedToAggregator(payload);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SignalEngine tick processing error: {ex.Message}");
            }
        }

        private void FeedToAggregator(dynamic kt)
        {
            var tick = new TickData
            {
                InstrumentToken = (uint)kt.InstrumentToken,
                LastPrice = (double)kt.LastPrice,
                LastQuantity = (long)(kt.LastQuantity ?? 0),
                Volume = (long)(kt.Volume ?? 0),
                TickTime = (kt.LastTradeTime is DateTime dt) ? dt : SessionClock.NowIst(),
                ReceivedAt = SessionClock.NowIst(),
                AveragePrice = (double)kt.AveragePrice,
                OpenPrice = (double)kt.Open,
                HighPrice = (double)kt.High,
                LowPrice = (double)kt.Low,
                ClosePrice = (double)kt.Close,
                OI = (long)kt.OI,
                IsReplay = false
            };

            if (!TickPipeline.EnqueueTick(tick))
            {
                var nm = InstrumentCatalog.ResolveName(kt.InstrumentToken);
                SignalDiagnostics.Warn(tick.InstrumentToken, nm, DateTime.Now, "Enqueue failed - channel full");
            }
        }

        private void Aggregator_CandleCompleted(object sender, CandleCompletedEventArgs e)
        {
            // Identify instrument name from token
            string name = _tokenNames.TryGetValue(e.Token, out var n) ? n : $"Token{e.Token}";
            double open = e.Candle.Open, close = e.Candle.Close;

            // Simple example strategy: buy on green candle, sell on red
            if (close > open)
            {
                OnSignal?.Invoke(this, new SignalEventArgs
                {
                    InstrumentName = name,
                    Signal = new Signal { Type = SignalType.Buy, Price = close }
                });
            }
            else if (close < open)
            {
                OnSignal?.Invoke(this, new SignalEventArgs
                {
                    InstrumentName = name,
                    Signal = new Signal { Type = SignalType.Sell, Price = close }
                });
            }
        }

        public void Dispose()
        {
            try
            {
                if (_aggregator != null)
                    _aggregator.CandleCompleted -= Aggregator_CandleCompleted;
            }
            catch { }

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
