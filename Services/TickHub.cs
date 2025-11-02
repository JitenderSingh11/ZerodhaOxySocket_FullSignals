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
        public long InstrumentToken { get; set; }
        public string InstrumentName { get; set; }
        public Candle Candle { get; set; }
    }

    public class SignalEventArgs : EventArgs
    {
        public long InstrumentToken { get; set; }
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

        private static readonly ConcurrentDictionary<uint,(double price,long vol)> _lastSeen = new();
        private static uint _underlyingToken = 256265; // NIFTY

        public static void Init(string apiKey, string accessToken, string connectionString)
        {
            _apiKey = apiKey; _accessToken = accessToken; _cs = connectionString;
            Config.Load(AppDomain.CurrentDomain.BaseDirectory);
            DataAccess.InitDb(connectionString);

            long cfgTok = Config.Current.Trading.UnderlyingToken ?? 256265;
            _underlyingToken = (uint)cfgTok;

            int tf = Config.Current.Trading.TimeframeMinutes;
            int seedBars = Config.Current.Trading.SeedBars;
            var seed = DataAccess.LoadRecentCandlesAggregated(cfgTok, seedBars, tf);
            var name = "NIFTY";
            var ctx = new InstrumentContext(_underlyingToken, name, TimeSpan.FromMinutes(tf), seed);
            _contexts[_underlyingToken] = ctx;

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
                    var tokenU = (uint)t.InstrumentToken;
                    if (!ShouldRecord(tokenU, (double)t.LastPrice, t.Volume, t.LastQuantity))
                        continue;

                    var name = ResolveName(tokenU);

                    var data = new TickData
                    {
                        InstrumentToken = tokenU,
                        InstrumentName = name,
                        LastPrice = (double)t.LastPrice,
                        LastQuantity = t.LastQuantity,
                        Volume = t.Volume,
                        AveragePrice = (double)t.AveragePrice,
                        OpenPrice = (double)t.Open,
                        HighPrice = (double)t.High,
                        LowPrice = (double)t.Low,
                        ClosePrice = (double)t.Close,
                        OI = t.OI,
                        OIChange = 0,
                        BidPrice1 = 0, BidQty1 = 0, AskPrice1 = 0, AskQty1 = 0,
                        TickTime = SessionClock.NowIst()
                    };
                    _tickQueue.Enqueue(data);
                    OnLtp?.Invoke(tokenU, (double)t.LastPrice, t.Volume);

                    var ctx = _contexts.GetOrAdd(tokenU, _ => new InstrumentContext(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes)));
                    if (tokenU == _underlyingToken)
                    {
                        var closed = ctx.ProcessTick((double)t.LastPrice, t.Volume);
                        if (closed != null)
                        {
                            OnCandleClosed?.Invoke(null, new CandleEventArgs { InstrumentToken = (long)tokenU, InstrumentName = ctx.Name, Candle = closed });
                            DataAccess.InsertCandle(closed, tokenU, ctx.Name, true);

                            var sig = ctx.EvaluateSignalsPositionAware();
                            if (sig != null)
                            {
                                var mapped = MapSignalToOption(sig, (double)t.LastPrice);
                                DataAccess.InsertSignal(new Signal { Type = sig.Type, Price = mapped.Price, Note = mapped.Note }, mapped.Token, mapped.Name, true);
                                OnSignal?.Invoke(null, new SignalEventArgs { InstrumentToken = (long)mapped.Token, InstrumentName = mapped.Name, Signal = new Signal { Type = sig.Type, Price = mapped.Price, Note = mapped.Note } });
                            }
                        }
                    }
                }
            };

            _socket.Connect(_apiKey, _accessToken);
        }

        private static (uint Token, string Name, double Price, string Note) MapSignalToOption(Signal s, double underlyingLtp)
        {
            int strike = (int)(System.Math.Round(underlyingLtp / 50.0) * 50);
            string suffix = s.Type == SignalType.Buy ? "CE" : "PE";
            string sym = $"NIFTY{strike}{suffix}";
            var info = InstrumentHelper.FindLatestByTradingsymbol(sym);
            if (info != null) return ((uint)info.InstrumentToken, info.Tradingsymbol, s.Price, $"Mapped->{info.Tradingsymbol}");

            var alt = InstrumentHelper.FindNearestOption("NIFTY", strike, suffix);
            if (alt != null) return ((uint)alt.InstrumentToken, alt.Tradingsymbol, s.Price, $"Mapped->{alt.Tradingsymbol}");

            return (_underlyingToken, "NIFTY", s.Price, "Fallback underlying");
        }

        private static bool ShouldRecord(uint token, double price, long vol, long lastQty)
        {
            if (!SessionClock.IsRegularSessionNow()) return false;
            var last = _lastSeen.GetOrAdd(token, _ => (double.NaN, -1));
            bool unchanged = last.price == price && last.vol == vol;
            if (unchanged && lastQty <= 0) return false;
            _lastSeen[token] = (price, vol);
            return true;
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

        public static void SubscribeAuto(IEnumerable<uint> tokens)
        {
            foreach (var t in tokens)
            {
                var name = ResolveName(t);
                _contexts.GetOrAdd(t, _ => new InstrumentContext(t, name, TimeSpan.FromMinutes(1)));
                _autoTokens.Add(t);
            }
            _socket?.Subscribe(tokens, "full");
        }


        private static string MakeGroupName(string tradingsymbol)
        {
            try
            {
                tradingsymbol ??= "";
                string s = tradingsymbol.ToUpperInvariant();
                // strip CE/PE for grouping
                if (s.EndsWith("CE") || s.EndsWith("PE")) s = s[..^2];
                // split letters/digits: "NIFTY24NOV" -> "NIFTY-24NOV"
                int i = 0; while (i < s.Length && !char.IsDigit(s[i])) i++;
                string underlying = s.Substring(0, i).TrimEnd();
                string expiry = (i < s.Length) ? s.Substring(i) : "";
                return string.IsNullOrWhiteSpace(underlying) ? s : $"{underlying}-{expiry}";
            }
            catch { return tradingsymbol ?? "UNKNOWN"; }
        }

        public static void SubscribeManual(uint token)
        {
            _manualTokens.Add(token);

            var name = ResolveName(token);
            // create a lightweight 1m context for display/buffering of this token (no signal eval for non-underlying)
            _contexts.GetOrAdd(token, _ => new InstrumentContext(token, name, TimeSpan.FromMinutes(1)));
            PortfolioManager.SetGroup(token, MakeGroupName(name));

            _socket?.Subscribe(new[] { token }, "full");
            OnStatus?.Invoke($"Manual subscribed {token} {name}");
        }

        public static void UnsubscribeManual(uint token)
        {
            _manualTokens.Remove(token);
            _socket?.Unsubscribe(new[] { token });
            OnStatus?.Invoke($"Manual unsubscribed {token}");
        }


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
