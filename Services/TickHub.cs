using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KiteConnect;

namespace ZerodhaOxySocket
{
    public class CandleEventArgs : EventArgs
    {
        public uint InstrumentToken { get; set; }
        public string InstrumentName { get; set; }
        public Candle Candle { get; set; }
    }

    public class SignalEventArgs : EventArgs
    {
        public uint InstrumentToken { get; set; }
        public string InstrumentName { get; set; }
        public Signal Signal { get; set; }
    }

    public static class TickHub
    {
        private static ZerodhaTickerSocket _socket;
        private static readonly ConcurrentQueue<TickData> _tickQueue = new();
        private static readonly CancellationTokenSource _cts = new();
        private static string _apiKey = "", _accessToken = "", _cs = "";
        private static readonly HashSet<uint> _manualTokens = new();
        private static readonly HashSet<uint> _autoTokens = new();
        private static readonly ConcurrentDictionary<uint, InstrumentContext> _contexts = new();

        public static event Action<uint,double,double> OnLtp;
        public static event Action<string> OnStatus;
        public static event EventHandler<CandleEventArgs> OnCandleClosed;
        public static event EventHandler<SignalEventArgs> OnSignal;

        public static void Init(string apiKey, string accessToken, string connectionString)
        {
            _apiKey = apiKey; _accessToken = accessToken; _cs = connectionString;
            DataAccess.InitDb(connectionString);
            StartWriter();
        }

        public static void Connect()
        {
            _socket?.Dispose();
            _socket = new ZerodhaTickerSocket();
            _socket.OnStatus += s => OnStatus?.Invoke(s);
            _socket.OnTicks += ticks =>
            {
                foreach (var t in ticks)
                {
                    var data = new TickData
                    {
                        InstrumentToken = (uint)t.InstrumentToken,
                        LastPrice = t.LastPrice,
                        LastQuantity = t.LastTradedQuantity,
                        Volume = t.Volume,
                        AveragePrice = t.AveragePrice,
                        OpenPrice = t.Open,
                        HighPrice = t.High,
                        LowPrice = t.Low,
                        ClosePrice = t.Close,
                        OI = t.OI,
                        OIChange = 0,
                        BidPrice1 = 0, BidQty1 = 0, AskPrice1 = 0, AskQty1 = 0,
                        TickTime = DateTime.UtcNow
                    };
                    _tickQueue.Enqueue(data);
                    OnLtp?.Invoke((uint)t.InstrumentToken, t.LastPrice, t.Volume);

                    var token = (uint)t.InstrumentToken;
                    var ctx = _contexts.GetOrAdd(token, _ => new InstrumentContext(token, ResolveName(token), TimeSpan.FromMinutes(1)));
                    var closed = ctx.ProcessTick(t.LastPrice, t.LastTradedQuantity);
                    if (closed != null)
                    {
                        OnCandleClosed?.Invoke(null, new CandleEventArgs { InstrumentToken = token, InstrumentName = ctx.Name, Candle = closed });
                        DataAccess.InsertCandle(closed, token, ctx.Name, true);
                        var sig = ctx.EvaluateSignals();
                        if (sig != null)
                        {
                            DataAccess.InsertSignal(sig, token, ctx.Name, true);
                            OnSignal?.Invoke(null, new SignalEventArgs { InstrumentToken = token, InstrumentName = ctx.Name, Signal = sig });
                        }
                    }
                }
            };

            _socket.Connect(_apiKey, _accessToken);
        }

        private static string ResolveName(uint token)
        {
            try
            {
                var list = InstrumentHelper.LoadInstrumentsFromCsv(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv"));
                var it = list.FirstOrDefault(x => x.InstrumentToken == token);
                if (it != null) return string.IsNullOrWhiteSpace(it.Tradingsymbol) ? (it.Name ?? token.ToString()) : it.Tradingsymbol;
            } catch {}
            return token.ToString();
        }

        public static void SubscribeManual(uint token)
        {
            _manualTokens.Add(token);
            _contexts.TryAdd(token, new InstrumentContext(token, ResolveName(token), TimeSpan.FromMinutes(1)));
            _socket?.Subscribe(new []{ token }, "full");
        }

        public static void UnsubscribeManual(uint token)
        {
            _manualTokens.Remove(token);
            _socket?.Unsubscribe(new []{ token });
        }

        public static void SubscribeAuto(IEnumerable<uint> tokens)
        {
            foreach (var t in tokens)
            {
                _autoTokens.Add(t);
                _contexts.TryAdd(t, new InstrumentContext(t, ResolveName(t), TimeSpan.FromMinutes(1)));
            }
            _socket?.Subscribe(tokens, "full");
        }

        public static IEnumerable<uint> CurrentManual() => _manualTokens.ToArray();
        public static IEnumerable<uint> CurrentAuto() => _autoTokens.ToArray();

        private static void StartWriter()
        {
            Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, _cts.Token);
                    Flush();
                }
            }, _cts.Token);
        }

        private static void Flush()
        {
            var batch = new List<TickData>();
            while (_tickQueue.TryDequeue(out var t))
                batch.Add(t);
            if (batch.Count > 0)
                DataAccess.InsertTicksBatch(batch);
        }

        public static void Dispose()
        {
            _cts.Cancel();
            Flush();
            _socket?.Dispose();
        }
    }
}
