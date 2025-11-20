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

        private static long _socketDropCounter = 0;

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
            UnderlyingCandleCache.AddCandleCacheSeeds(cfgTok, seed);
            var name = "NIFTY";
            var ctx = new InstrumentContext(_underlyingToken, name, TimeSpan.FromMinutes(tf), seed);
            _contexts[_underlyingToken] = ctx;
        }

        public static void ReplayInit(ReplayConfig replayConfig)
        {
            Config.Load(AppDomain.CurrentDomain.BaseDirectory);

            long cfgTok = Config.Current.Trading.UnderlyingToken ?? 256265;
            _underlyingToken = (uint)cfgTok;

            int tf = Config.Current.Trading.TimeframeMinutes;
            int seedBars = Config.Current.Trading.SeedBars;
            var seed = DataAccess.LoadRecentCandlesAggregated(cfgTok, seedBars, tf, replayConfig.Start);
            UnderlyingCandleCache.AddCandleCacheSeeds(cfgTok, seed);
            var name = ResolveName(_underlyingToken);
            var ctx = new InstrumentContext(_underlyingToken, name, TimeSpan.FromMinutes(tf), seed);
            _contexts[_underlyingToken] = ctx;
        }


        public static void Connect()
        {
            _socket?.Dispose();
            _socket = new ZerodhaTickerSocket();
            _socket.OnStatus += s => OnStatus?.Invoke(s);
            // Fast path socket handler: tiny, allocation-light, non-blocking
            _socket.OnTicks += payload =>
            {
                try
                {
                    // If SDK returns an IList<T> or array, prefer indexed for-loop (slightly faster than foreach)
                    if (payload is IList<Tick> list)
                    {
                        for (int i = 0, n = list.Count; i < n; ++i)
                        {
                            var kt = list[i];
                            EnqueueFromKt(kt);
                        }
                    }
                    else if (payload is IEnumerable<Tick> batch)
                    {
                        // fallback for IEnumerable (still fast)
                        foreach (var kt in batch)
                            EnqueueFromKt(kt);
                    }
                    else
                    {
                        // single tick payload
                        dynamic single = payload;
                        EnqueueFromKt(single);
                    }
                }
                catch (Exception ex)
                {
                    SignalDiagnostics.Reject(0, "TickHub", DateTime.Now, $"Socket parse/enqueue exception: {ex.Message}");
                }
            };

            // helper (inline for minimal allocations)
            void EnqueueFromKt(dynamic kt)
            {
                // Extract minimal fields only (no heavy method calls)
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

                var t = new TickData
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

                // Fast non-blocking enqueue (very cheap)
                if (!TickPipeline.EnqueueTick(t))
                {
                    SignalDiagnostics.Warn(t.InstrumentToken, t.InstrumentName, DateTime.Now, "Enqueue failed - channel full");
                    // channel full / drop: only increment metric; avoid heavy logging here
                    Interlocked.Increment(ref _socketDropCounter);
                }
            }


            _socket.Connect(_apiKey, _accessToken);
        }

        // One canonical tick pipeline for BOTH live and replay
        private static void HandleTickCore(TickData t, Guid replayId, bool IsLive)
        {
            var tokenU = t.InstrumentToken;

            // gate recording (live uses tick time; replay bypasses when requested)
            if (IsLive)
            {
                if (!ShouldRecord(tokenU, t.LastPrice, t.Volume, t.LastQuantity, t.TickTime))
                    return;
            }

            // diagnostics
            try
            {
                SignalDiagnostics.Info(t.InstrumentToken, t.InstrumentName, t.TickTime, "ENQ", $"Enqueue tick LP={t.LastPrice} Vol={t.Volume} IsLive={IsLive}");
            }
            catch { /* swallow logging errors */ }



            OnLtp?.Invoke(tokenU, t.LastPrice, t.Volume);

            // context
            var name = ResolveName(tokenU);
            var ctx = _contexts.GetOrAdd(tokenU, _ =>
                new InstrumentContext(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes)));

            // IMPORTANT: use the tick's time when updating candles
            var closed = ctx.ProcessTickWithTime(t.LastPrice, t.TickTime);  // add overload if missing (see step 4)

            CandleEvaluation(ctx, closed, tokenU, IsLive, replayId);

            // feed exits for option ticks
            if ((long)tokenU != (_underlyingToken))
                ExitManager.OnOptionTick(tokenU, t.LastPrice, t.TickTime);
        }

        private static void CandleEvaluation(InstrumentContext? ctx,Candle? closed, uint tokenU, bool IsLive, Guid replayId)
        {
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
                    {
                        SignalDiagnostics.Info(tokenU, ctx.Name, closed.Time, "CANDLE", $"Closed candle at {closed.Time:O} O={closed.Open} H={closed.High} L={closed.Low} C={closed.Close} V={closed.Volume}");
                        DataAccess.InsertCandle(closed, tokenU, ctx.Name, true);
                    }
                        

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
                        var expiry = OptionMapper.GetNearestExpiry("NIFTY");
                        var mapped = OptionMapper.ChooseATMOption("NIFTY", sig.Price, expiry.Value, optType);
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
                                //Side = (sig.Type == SignalType.Buy) ? "BUY" : "SELL",
                                Side = "BUY",
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
        }

        /// <summary>
        /// Called by the TickPipeline consumer for each tick (live path).
        /// This runs the in-memory candle update, signal evaluation and exit checks.
        /// It intentionally DOES NOT enqueue tick into old _tickQueue (the pipeline persists ticks).
        /// </summary>
        public static void ProcessTickFromPipeline(TickData t)
        {
            if (t == null) return;

            var tokenU = t.InstrumentToken;

            // gate recording (use provider tick time). Pipeline ensures we call this only for live ticks.
            if (!ShouldRecord(tokenU, t.LastPrice, t.Volume, t.LastQuantity, t.TickTime))
                return;

            // diagnostics
            try
            {
                SignalDiagnostics.Info(t.InstrumentToken, ResolveName(tokenU), t.TickTime, "ENQ",
                    $"Pipeline tick LP={t.LastPrice} Vol={t.Volume}");
            }
            catch { /* swallow logging errors */ }

            // publish LTP to any subscribers
            OnLtp?.Invoke(tokenU, t.LastPrice, t.Volume);

            // get-or-create instrument context
            var name = ResolveName(tokenU);
            var ctx = _contexts.GetOrAdd(tokenU, _ =>
                new InstrumentContext(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes)));

            // IMPORTANT: use the tick's time when updating candles
            var closed = ctx.ProcessTickWithTime(t.LastPrice, t.TickTime);  // ensure this overload exists

            // Evaluate closed candle (this will persist candles when IsLive = true inside CandleEvaluation)
            CandleEvaluation(ctx, closed, tokenU, IsLive: true, replayId: Guid.Empty);

            // feed exits for option ticks (non-underlying tokens)
            if ((long)tokenU != (_underlyingToken))
                ExitManager.OnOptionTick(tokenU, t.LastPrice, t.TickTime);
        }



        private static bool ShouldRecord(
    uint token, double price, long vol, long lastQty,
    DateTime tickIst, bool allowAfterHours = false)
        {
            if (!allowAfterHours && !SessionClock.IsRegularSessionAt(tickIst))
            {
                SignalDiagnostics.Reject(token, ResolveName(token), tickIst, $"SessionClock denied recording (afterHours). tickIst={tickIst:O}");
                return false;
            }

            var last = _lastSeen.GetOrAdd(token, _ => (double.NaN, -1));
            bool unchanged = last.price == price && last.vol == vol;

            if (unchanged && lastQty <= 0)
            {
                SignalDiagnostics.Reject(token, ResolveName(token), tickIst, $"Stale/no-trade tick: unchanged price/vol and lastQty={lastQty}");
                return false;
            }

            _lastSeen[token] = (price, vol);

            SignalDiagnostics.Info(token, ResolveName(token), tickIst, "SHOULDREC", $"Allowed (price={price}, vol={vol}, lastQty={lastQty})");
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

        public static void ProcessReplayTick(TickData t, Guid replayId, bool isActive)
        {
            HandleTickCore(t, replayId, IsLive: false);
        }

        public static void ProcessReplayCandle(uint instrumentToken, Candle closed, Guid replayId)
        {

            var instrumentName = ResolveName(instrumentToken);

            var ctx = _contexts.GetOrAdd(instrumentToken, _ =>
                new InstrumentContext(instrumentToken, instrumentName, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes)));

            ctx.AddCandle(closed); // seed the closed candle    

            CandleEvaluation(ctx, closed, instrumentToken, IsLive: false, replayId);

            var ticks = new List<TickData>();
            var candles = new List<Candle>();

            if (OrderManager.HasOpenPositionForUnderlying(instrumentToken))
            {
                foreach (var o in OrderManager.GetOpenOrdersForUnderlying(instrumentToken))
                {
                    var ticksData = DataAccess.GetTicksRangeForTokens(
                                         [o.InstrumentToken],
                                         closed.Time,
                                         closed.Time.AddMinutes(Config.Current.Trading.TimeframeMinutes));
                    ticks.AddRange(ticksData);

                    if(!ticks.Any())
                    {
                        candles = DataAccess.LoadAggregatedCandles(o.InstrumentToken, closed.Time, closed.Time.AddMinutes(Config.Current.Trading.TimeframeMinutes), 1);
                    }
                }
            }

            foreach(var tick in ticks)
            { 
                if (instrumentToken != (tick.InstrumentToken))
                    ExitManager.OnOptionTick(tick.InstrumentToken, tick.LastPrice, tick.TickTime);
            }

            foreach(var candle in candles)
            {
                if (instrumentToken != (candle.InstrumentToken)) 
                {
                    ExitManager.OnOptionTick(candle.InstrumentToken, candle.Open, candle.Time);
                    ExitManager.OnOptionTick(candle.InstrumentToken, candle.Close, candle.Time);
                }
            }
        }


    }
}