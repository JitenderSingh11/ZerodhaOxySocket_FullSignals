using KiteConnect;
using Newtonsoft.Json.Linq;
using OxyPlot.Wpf;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Net.WebRequestMethods;

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
            var seed = DataAccess.LoadRecentCandlesAggregated(cfgTok, seedBars, tf, DateTime.Now);
            var name = "NIFTY";
            var ctx = new InstrumentContext(_underlyingToken, name, TimeSpan.FromMinutes(tf), seed);
            _contexts[_underlyingToken] = ctx;

            StartWriter();
        }

        public static void ReplayInit(ReplayConfig replayConfig)
        {
            Config.Load(AppDomain.CurrentDomain.BaseDirectory);

            long cfgTok = Config.Current.Trading.UnderlyingToken ?? 256265;
            _underlyingToken = (uint)cfgTok;

            int tf = Config.Current.Trading.TimeframeMinutes;
            int seedBars = Config.Current.Trading.SeedBars;
            var seed = DataAccess.LoadRecentCandlesAggregated(cfgTok, seedBars, tf, replayConfig.Start);
            var name = ResolveName(_underlyingToken);
            var ctx = new InstrumentContext(_underlyingToken, name, TimeSpan.FromMinutes(tf), seed);
            _contexts[_underlyingToken] = ctx;
        }


        public static void Connect()
        {
            _socket?.Dispose();
            _socket = new ZerodhaTickerSocket();
            _socket.OnStatus += s => OnStatus?.Invoke(s);
            _socket.OnTicks += ticks =>
            {
                foreach (var kt in ticks)
                {
                    var tdata = new TickData
                    {
                        InstrumentToken = (uint)kt.InstrumentToken,
                        InstrumentName = ResolveName((uint)kt.InstrumentToken),
                        LastPrice = (double)kt.LastPrice,
                        LastQuantity = kt.LastQuantity,
                        Volume = kt.Volume,
                        AveragePrice = (double)kt.AveragePrice,
                        OpenPrice = (double)kt.Open,
                        HighPrice = (double)kt.High,
                        LowPrice = (double)kt.Low,
                        ClosePrice = (double)kt.Close,
                        OI = kt.OI,
                        OIChange = 0,
                        BidPrice1 = 0,
                        BidQty1 = 0,
                        AskPrice1 = 0,
                        AskQty1 = 0,
                        TickTime = SessionClock.NowIst()   // LIVE: system IST time
                    };

                    HandleTickCore(tdata, Guid.Empty, IsLive: true);


                }
            };

            _socket.Connect(_apiKey, _accessToken);
        }

        // One canonical tick pipeline for BOTH live and replay
        private static void HandleTickCore(TickData t, Guid replayId, bool IsLive = false)
        {
            var tokenU = t.InstrumentToken;

            // gate recording (live uses tick time; replay bypasses when requested)
            if (IsLive)
            {
                if (!ShouldRecord(tokenU, t.LastPrice, t.Volume, t.LastQuantity, t.TickTime))
                    return;
            }

            if (IsLive)
            {
                // enqueue + event
                _tickQueue.Enqueue(t);
            }

            OnLtp?.Invoke(tokenU, t.LastPrice, t.Volume);

            // context
            var name = ResolveName(tokenU);
            var ctx = _contexts.GetOrAdd(tokenU, _ =>
                new InstrumentContext(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes)));

            // IMPORTANT: use the tick's time when updating candles
            var closed = ctx.ProcessTickWithTime(t.LastPrice, t.TickTime);  // add overload if missing (see step 4)

            // candle closed path
            if (closed != null)
            {
                if (tokenU == _underlyingToken)
                {
                    OnCandleClosed?.Invoke(null, new CandleEventArgs
                    {
                        InstrumentToken = (long)tokenU,
                        InstrumentName = ctx.Name,
                        Candle = closed
                    });

                    if (IsLive)
                        DataAccess.InsertCandle(closed, tokenU, ctx.Name, true);

                    // cache for ATR/exit
                    UnderlyingCandleCache.Put((long)tokenU, closed);

                    // conservative signal logic (same for live & replay)
                    var sig = ctx.EvaluateSignalsPositionAware_Conservative();
                    if (sig != null)
                    {
                        if (!SignalGate.ShouldEmitSignal(tokenU, sig.Type, sig.Time, Config.Current.Trading.DebounceCandles))
                            return;

                        if (!Config.Current.Trading.AllowMultipleOpenPositions &&
                            OrderManager.HasOpenPositionForUnderlying(tokenU))
                            return;

                        var optType = (sig.Type == SignalType.Buy) ? "CE" : "PE";
                        var mapped = OptionMapper.ChooseATMOption(sig.Time, sig.Price, optType);
                        if (mapped != null)
                        {
                            // simulate order (works for live too until you wire real orders)
                            var order = new OrderRecord
                            {
                                OrderId = Guid.NewGuid(),
                                SignalId = Guid.NewGuid(),
                                ReplayId = replayId,
                                InstrumentToken = mapped.InstrumentToken,
                                InstrumentName = mapped.Tradingsymbol,
                                UnderlyingToken = _underlyingToken,
                                UnderlyingPriceAtSignal = sig.Price,
                                Side = (sig.Type == SignalType.Buy) ? "BUY" : "SELL",
                                QuantityLots = 1,
                                Status = OrderStatus.Placed
                            };
                            OrderManager.CreateOrder(order);

                            var simOrder = new SimOrder
                            {
                                ReplayId = replayId,
                                InstrumentToken = (uint)mapped.InstrumentToken,
                                InstrumentName = mapped.Tradingsymbol,
                                UnderlyingToken = order.UnderlyingToken,
                                UnderlyingPrice = sig.Price,
                                Side = order.Side,
                                QuantityLots = order.QuantityLots,
                                PlacedAt = sig.Time,
                                Reason = "ConservativeA"
                            };
                            var sim = OrderSimulator.PlaceOrderNextTick(replayId, simOrder);
                            OrderManager.AttachFill(order, sim);
                            if (order.Status == OrderStatus.Open) ExitManager.Track(order);
                        }
                    }
                }
            }

            // feed exits for option ticks
            if ((long)tokenU != (_underlyingToken))
                ExitManager.OnOptionTick(tokenU, t.LastPrice, t.TickTime);
        }


        private static bool ShouldRecord(
    uint token, double price, long vol, long lastQty,
    DateTime tickIst, bool allowAfterHours = false)
        {
            if (!allowAfterHours && !SessionClock.IsRegularSessionAt(tickIst))
                return false;

            var last = _lastSeen.GetOrAdd(token, _ => (double.NaN, -1));
            bool unchanged = last.price == price && last.vol == vol;

            if (unchanged && lastQty <= 0) return false; // stale/no-trade tick
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

        public static void ProcessReplayTick(TickData t, Guid replayId, bool isActive)
        {
            HandleTickCore(t, replayId, IsLive: false);
        }

    }
}
