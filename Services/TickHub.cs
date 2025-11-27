using KiteConnect;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

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

    public class OrderCreatedEventArgs : EventArgs
    {
        public OrderRecord Order { get; set; }
    }

    public class TickHub
    {
        private static TickHub _instance;
        public static TickHub Instance => _instance ??= new TickHub();

        private ZerodhaTickerSocket _socket;
        private readonly CancellationTokenSource _cts = new();
        private string _apiKey = "", _accessToken = "", _cs = "";
        private readonly HashSet<uint> _manualTokens = new();
        private readonly HashSet<uint> _autoTokens = new();
        private readonly ConcurrentDictionary<uint, InstrumentContext> _contexts = new();

        // Cached instrument name lookup to avoid CSV reads on hot path
        private readonly ConcurrentDictionary<uint, string> _instrumentNameCache = new();

        // Per-token diagnostic throttle to reduce logging overhead
        private readonly ConcurrentDictionary<uint, DateTime> _lastDiagLog = new();
        private readonly TimeSpan _perTickLogInterval = TimeSpan.FromSeconds(1);

        private bool _enableChartUpdates = false;

        public event Action<uint, double, double> OnLtp;
        public event Action<string> OnStatus;
        public event EventHandler<CandleEventArgs> OnCandleClosed;
        public event EventHandler<SignalEventArgs> OnSignal;
        public event EventHandler<OrderCreatedEventArgs> OnOrderCreated;

        private readonly ConcurrentDictionary<uint, (double price, long vol)> _lastSeen = new();
        private List<uint> _underlyingToken = new List<uint>(); // NIFTY

        private long _socketDropCounter = 0;

        private readonly OrderManager _orderManager = OrderManager.Instance;
        private readonly ExitManager _exitManager = ExitManager.Instance;

        public TickHub() { }

        public void Init(AppConfig _config, string apiKey, string accessToken, string connectionString)
        {
            _apiKey = apiKey;
            _accessToken = accessToken;
            _cs = connectionString;
            _enableChartUpdates = _config.EnableChartUpdates;

            DataAccess.InitDb(connectionString);

            Config.Load(AppDomain.CurrentDomain.BaseDirectory);

            // load instrument name cache once at startup
            LoadInstrumentNames();

            foreach (var inst in _config.SubscribedInstruments)
            {
                uint token = (uint)inst.Token;

                long cfgTok = token;
                _underlyingToken.Add((uint)cfgTok);

                int tf = Config.Current.Trading.TimeframeMinutes;
                int seedBars = Config.Current.Trading.SeedBars;
                var seed = DataAccess.LoadRecentCandlesAggregated(cfgTok, seedBars, tf, ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow));
                UnderlyingCandleCache.AddCandleCacheSeeds(cfgTok, seed);
                var name = inst.Name;
                var ctx = new InstrumentContext(token, name, TimeSpan.FromMinutes(tf), seed);
                _contexts[token] = ctx;
            }
        }

        public void ReplayInit(ReplayConfig replayConfig)
        {
            Config.Load(AppDomain.CurrentDomain.BaseDirectory);

            // load instrument name cache
            LoadInstrumentNames();

            foreach (var inst in replayConfig.SubscribedInstruments)
            {
                uint token = (uint)inst.Token;

                long cfgTok = token;
                _underlyingToken.Add((uint)cfgTok);

                int tf = Config.Current.Trading.TimeframeMinutes;
                int seedBars = Config.Current.Trading.SeedBars;
                var seed = DataAccess.LoadRecentCandlesAggregated(cfgTok, seedBars, tf, ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow));
                UnderlyingCandleCache.AddCandleCacheSeeds(cfgTok, seed);
                var name = inst.Name;
                var ctx = new InstrumentContext(token, name, TimeSpan.FromMinutes(tf), seed);
                _contexts[token] = ctx;
            }
        }

        public void Connect()
        {
            _socket?.Dispose();
            _socket = new ZerodhaTickerSocket();
            _socket.OnStatus += s => OnStatus?.Invoke(s);
            _socket.OnTicks += payload =>
            {
                try
                {
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
                        foreach (var kt in batch)
                            EnqueueFromKt(kt);
                    }
                    else
                    {
                        dynamic single = payload;
                        EnqueueFromKt(single);
                    }
                }
                catch (Exception ex)
                {
                    SignalDiagnostics.Reject(0, "TickHub", ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), $"Socket parse/enqueue exception: {ex.Message}");
                }
            };

            void EnqueueFromKt(dynamic kt)
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

                // Normalize incoming trade time to UTC internal canonical time
                DateTime tickUtc = DateTime.UtcNow;
                try
                {
                    if (kt.LastTradeTime is DateTime dt)
                        tickUtc = ZerodhaOxySocket.Services.Clock.ToUtcFromPossiblyIst(dt);
                }
                catch { }

                var t = new TickData
                {
                    InstrumentToken = token,
                    LastPrice = price,
                    LastQuantity = qty,
                    Volume = vol,
                    TickTime = tickUtc,
                    ReceivedAt = DateTime.UtcNow,
                    AveragePrice = averagePrice,
                    OpenPrice = openPrice,
                    HighPrice = highPrice,
                    LowPrice = lowPrice,
                    ClosePrice = closePrice,
                    OI = OI,
                    IsReplay = false
                };

                if (!TickPipeline.EnqueueTick(t))
                {
                    var nm = ResolveName(token);
                    SignalDiagnostics.Warn(t.InstrumentToken, nm, ZerodhaOxySocket.Services.Clock.UtcToIst(DateTime.UtcNow), "Enqueue failed - channel full");
                    Interlocked.Increment(ref _socketDropCounter);
                }
            }

            _socket.Connect(_apiKey, _accessToken);
        }

        private void HandleTickCore(TickData t, Guid replayId, bool IsLive)
        {
            var tokenU = t.InstrumentToken;

            if (IsLive)
            {
                if (!ShouldRecord(tokenU, t.LastPrice, t.Volume, t.LastQuantity, t.TickTime))
                    return;
            }

            try
            {
                if (ShouldLogPerTick(tokenU))
                    SignalDiagnostics.Info(t.InstrumentToken, t.InstrumentName ?? ResolveName(tokenU), ZerodhaOxySocket.Services.Clock.UtcToIst(t.TickTime), "ENQ", $"Enqueue tick LP={t.LastPrice} Vol={t.Volume} IsLive={IsLive}");
            }
            catch { }

            if (_enableChartUpdates)
                OnLtp?.Invoke(tokenU, t.LastPrice, t.Volume);

            var name = ResolveName(tokenU);
            var ctx = _contexts.GetOrAdd(tokenU, _ =>
                new InstrumentContext(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes)));

            // pass qty (prefer LastQuantity, fallback to Volume if available)
            long qtyToUse = (t.LastQuantity > 0) ? t.LastQuantity : (t.Volume > 0 ? t.Volume : 0);
            var closed = ctx.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);

            CandleEvaluation(ctx, closed, tokenU, IsLive, replayId);

            if (_underlyingToken.Any(ut => ut != tokenU))
                _exitManager.OnOptionTick(tokenU, t.LastPrice, t.TickTime);
        }

        private void CandleEvaluation(InstrumentContext? ctx, Candle? closed, uint tokenU, bool IsLive, Guid replayId)
        {
            if (closed != null)
            {
                if (_underlyingToken.Any(ut => ut == tokenU))
                {
                    if (_enableChartUpdates)
                        OnCandleClosed?.Invoke(this, new CandleEventArgs
                        {
                            InstrumentToken = (long)tokenU,
                            InstrumentName = ctx.Name,
                            Candle = closed
                        });

                    if (IsLive)
                    {
                        SignalDiagnostics.Info(tokenU, ctx.Name, ZerodhaOxySocket.Services.Clock.UtcToIst(closed.Time), "CANDLE", $"Closed candle at {ZerodhaOxySocket.Services.Clock.UtcToIst(closed.Time):O} O={closed.Open} H={closed.High} L={closed.Low} C={closed.Close} V={closed.Volume}");
                        DataAccess.InsertCandle(closed, tokenU, ctx.Name, true);
                    }

                    UnderlyingCandleCache.Put((long)tokenU, closed);

                    var sig = ctx.EvaluateSignalsPositionAware_Conservative();
                    if (sig != null)
                    {
                        if (!SignalGate.ShouldEmitSignal(tokenU, sig.Type, sig.Time, Config.Current.Trading.DebounceCandles))
                            return;

                        if (!Config.Current.Trading.AllowMultipleOpenPositions &&
                            _orderManager.HasOpenPositionForUnderlying(tokenU))
                            return;

                        var optType = (sig.Type == SignalType.Buy) ? "CE" : "PE";
                        var expiry = OptionMapper.GetNearestExpiry(ctx.Name);
                        var mapped = OptionMapper.ChooseATMOption(ctx.Name, sig.Price, expiry.Value, optType);
                        if (mapped != null)
                        {
                            var order = new OrderRecord
                            {
                                OrderId = Guid.NewGuid(),
                                SignalId = Guid.NewGuid(),
                                ReplayId = replayId,
                                InstrumentToken = mapped.InstrumentToken,
                                InstrumentName = mapped.Tradingsymbol,
                                UnderlyingToken = tokenU,
                                UnderlyingPriceAtSignal = sig.Price,
                                Side = "BUY",
                                QuantityLots = 1,
                                Status = OrderStatus.Placed,
                                SignalInfo = sig
                            };
                            _orderManager.CreateOrder(order);

                            // Raise event for order creation
                            OnOrderCreated?.Invoke(this, new OrderCreatedEventArgs { Order = order });
                        }
                    }
                }
            }
        }

        public void ProcessSignalOrder(TickData tick, Guid replayId)
        {
            if (!_orderManager.HasOpenPositionForInstrument(tick.InstrumentToken))
            {
                return;
            }

            var order = _orderManager.GetOpenOrdersForInstrument(tick.InstrumentToken).FirstOrDefault();

            var simOrder = new SimOrder
            {
                ReplayId = replayId,
                InstrumentToken = (uint)order.InstrumentToken,
                InstrumentName = order.InstrumentName,
                UnderlyingToken = order.UnderlyingToken,
                UnderlyingPrice = order.SignalInfo.Price,
                Side = order.Side,
                QuantityLots = order.QuantityLots,
                PlacedAt = order.SignalInfo.Time,
                Reason = "ConservativeA"
            };

            var sim = OrderSimulator.PlaceOrderNextTick(tick, replayId, simOrder);
            _orderManager.AttachFill(order, sim);
            if (order.Status == OrderStatus.Open) _exitManager.Track(order);
        }

        public async Task ProcessSignalOrderAsync(TickData tick, Guid replayId)
        {
            if (!_orderManager.HasOpenPositionForInstrument(tick.InstrumentToken))
            {
                await SignalDiagnostics.RejectAsync(tick.InstrumentToken, ResolveName(tick.InstrumentToken), ZerodhaOxySocket.Services.Clock.UtcToIst(tick.TickTime), "No open position for instrument in ProcessSignalOrderAsync");
                return;
            }
            var order = _orderManager.GetOpenOrdersForInstrument(tick.InstrumentToken).FirstOrDefault();
            var simOrder = new SimOrder
            {
                ReplayId = replayId,
                InstrumentToken = (uint)order.InstrumentToken,
                InstrumentName = order.InstrumentName,
                UnderlyingToken = order.UnderlyingToken,
                UnderlyingPrice = order.SignalInfo.Price,
                Side = order.Side,
                QuantityLots = order.QuantityLots,
                PlacedAt = order.SignalInfo.Time,
                Reason = "ConservativeA"
            };
            var sim = await OrderSimulator.PlaceOrderNextTickAsync(tick, replayId, simOrder);
            await Task.Run(() => _orderManager.AttachFill(order, sim));
            if (order.Status == OrderStatus.Open) await Task.Run(() => _exitManager.Track(order));
            return;
        }

        public void ProcessTickFromPipeline(TickData t)
        {
            if (t == null) return;

            var tokenU = t.InstrumentToken;

            if (!ShouldRecord(tokenU, t.LastPrice, t.Volume, t.LastQuantity, t.TickTime))
                return;

            try
            {
                if (ShouldLogPerTick(tokenU))
                    SignalDiagnostics.Info(t.InstrumentToken, ResolveName(tokenU), ZerodhaOxySocket.Services.Clock.UtcToIst(t.TickTime), "ENQ",
                        $"Pipeline tick LP={t.LastPrice} Vol={t.Volume}");
            }
            catch { }

            if (_enableChartUpdates)
                OnLtp?.Invoke(tokenU, t.LastPrice, t.Volume);

            var name = ResolveName(tokenU);
            var ctx = _contexts.GetOrAdd(tokenU, _ =>
                new InstrumentContext(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes)));

            long qtyToUse = (t.LastQuantity > 0) ? t.LastQuantity : (t.Volume > 0 ? t.Volume : 0);
            var closed = ctx.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);

            CandleEvaluation(ctx, closed, tokenU, IsLive: true, replayId: Guid.Empty);

            if (_underlyingToken.Any(ut => ut != tokenU))
                _exitManager.OnOptionTick(tokenU, t.LastPrice, t.TickTime);
        }

        public async Task ProcessTickFromPipelineAsync(TickData t)
        {
            if (t == null)
            {
                await SignalDiagnostics.RejectAsync(0, "TickHub", DateTime.UtcNow, "Null tick in ProcessTickFromPipelineAsync");
                return;
            }
            var tokenU = t.InstrumentToken;
            if (!ShouldRecord(tokenU, t.LastPrice, t.Volume, t.LastQuantity, t.TickTime))
            {
                await SignalDiagnostics.RejectAsync(tokenU, ResolveName(tokenU), ZerodhaOxySocket.Services.Clock.UtcToIst(t.TickTime), "ShouldRecord returned false in ProcessTickFromPipelineAsync");
                return;
            }
            try
            {
                if (ShouldLogPerTick(tokenU))
                    await SignalDiagnostics.InfoAsync(t.InstrumentToken, ResolveName(tokenU), ZerodhaOxySocket.Services.Clock.UtcToIst(t.TickTime), "ENQ",
                        $"Pipeline tick LP={t.LastPrice} Vol={t.Volume}");
            }
            catch { }
            OnLtp?.Invoke(tokenU, t.LastPrice, t.Volume);
            var name = ResolveName(tokenU);
            var ctx = _contexts.GetOrAdd(tokenU, _ =>
                new InstrumentContext(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes)));
            long qtyToUse = (t.LastQuantity > 0) ? t.LastQuantity : (t.Volume > 0 ? t.Volume : 0);
            var closed = ctx.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
            await CandleEvaluationAsync(ctx, closed, tokenU, IsLive: true, replayId: Guid.Empty);
            if (_underlyingToken.Any(ut => ut != tokenU))
                _exitManager.OnOptionTick(tokenU, t.LastPrice, t.TickTime);
        }

        private async Task CandleEvaluationAsync(InstrumentContext? ctx, Candle? closed, uint tokenU, bool IsLive, Guid replayId)
        {
            if (closed != null)
            {
                if (_underlyingToken.Any(ut => ut == tokenU))
                {
                    if (_enableChartUpdates)
                        OnCandleClosed?.Invoke(this, new CandleEventArgs
                        {
                            InstrumentToken = (long)tokenU,
                            InstrumentName = ctx.Name,
                            Candle = closed
                        });
                    if (IsLive)
                    {
                        await SignalDiagnostics.InfoAsync(tokenU, ctx.Name, ZerodhaOxySocket.Services.Clock.UtcToIst(closed.Time), "CANDLE", $"Closed candle at {ZerodhaOxySocket.Services.Clock.UtcToIst(closed.Time):O} O={closed.Open} H={closed.High} L={closed.Low} C={closed.Close} V={closed.Volume}");
                        await DataAccess.InsertCandleAsync(closed, tokenU, ctx.Name, true);
                    }
                    UnderlyingCandleCache.Put((long)tokenU, closed);
                    var sig = ctx.EvaluateSignalsPositionAware_Conservative();
                    if (sig != null)
                    {
                        if (!SignalGate.ShouldEmitSignal(tokenU, sig.Type, sig.Time, Config.Current.Trading.DebounceCandles))
                        {
                            await SignalDiagnostics.InfoAsync(tokenU, ctx.Name, ZerodhaOxySocket.Services.Clock.UtcToIst(sig.Time), "SIGNALGATE", "SignalGate blocked signal emission");
                            return;
                        }
                        if (!Config.Current.Trading.AllowMultipleOpenPositions &&
                            _orderManager.HasOpenPositionForUnderlying(tokenU))
                        {
                            await SignalDiagnostics.InfoAsync(tokenU, ctx.Name, ZerodhaOxySocket.Services.Clock.UtcToIst(sig.Time), "ORDERGATE", "Multiple open positions not allowed");
                            return;
                        }
                        var optType = (sig.Type == SignalType.Buy) ? "CE" : "PE";
                        var expiry = OptionMapper.GetNearestExpiry(ctx.Name);
                        var mapped = OptionMapper.ChooseATMOption(ctx.Name, sig.Price, expiry.Value, optType);
                        if (mapped != null)
                        {
                            var order = new OrderRecord
                            {
                                OrderId = Guid.NewGuid(),
                                SignalId = Guid.NewGuid(),
                                ReplayId = replayId,
                                InstrumentToken = mapped.InstrumentToken,
                                InstrumentName = mapped.Tradingsymbol,
                                UnderlyingToken = tokenU,
                                UnderlyingPriceAtSignal = sig.Price,
                                Side = "BUY",
                                QuantityLots = 1,
                                Status = OrderStatus.Placed,
                                SignalInfo = sig
                            };
                            _orderManager.CreateOrder(order);
                            OnOrderCreated?.Invoke(this, new OrderCreatedEventArgs { Order = order });
                        }
                    }
                }
            }
        }

        public void ProcessReplayTick(TickData t, Guid replayId, bool isActive)
        {
            HandleTickCore(t, replayId, IsLive: false);
        }

        public void ProcessReplayCandle(uint instrumentToken, Candle closed, Guid replayId)
        {
            var instrumentName = ResolveName(instrumentToken);

            var ctx = _contexts.GetOrAdd(instrumentToken, _ =>
                new InstrumentContext(instrumentToken, instrumentName, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes)));

            ctx.AddCandle(closed);

            CandleEvaluation(ctx, closed, instrumentToken, IsLive: false, replayId);

            var ticks = new List<TickData>();
            var candles = new List<Candle>();

            if (_orderManager.HasOpenPositionForUnderlying(instrumentToken))
            {
                foreach (var o in _orderManager.GetOpenOrdersForUnderlying(instrumentToken))
                {
                    var ticksData = DataAccess.GetTicksRangeForTokens(
                                         new long[] { o.InstrumentToken },
                                         closed.Time,
                                         closed.Time.AddMinutes(Config.Current.Trading.TimeframeMinutes));
                    ticks.AddRange(ticksData);

                    if (!ticks.Any())
                    {
                        candles = DataAccess.LoadAggregatedCandles(o.InstrumentToken, closed.Time, closed.Time.AddMinutes(Config.Current.Trading.TimeframeMinutes), 1);
                    }
                }
            }

            foreach (var tick in ticks)
            {
                if (instrumentToken != (tick.InstrumentToken))
                    _exitManager.OnOptionTick(tick.InstrumentToken, tick.LastPrice, tick.TickTime);
            }

            foreach (var candle in candles)
            {
                if (instrumentToken != (candle.InstrumentToken))
                {
                    _exitManager.OnOptionTick(candle.InstrumentToken, candle.Open, candle.Time);
                    _exitManager.OnOptionTick(candle.InstrumentToken, candle.Close, candle.Time);
                }
            }
        }

        private void LoadInstrumentNames()
        {
            try
            {
                var list = InstrumentHelper.LoadInstrumentsFromCsv(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv"));
                foreach (var it in list)
                {
                    var name = string.IsNullOrWhiteSpace(it.Tradingsymbol)
                        ? (it.Name ?? it.InstrumentToken.ToString())
                        : it.Tradingsymbol;
                    _instrumentNameCache[(uint)it.InstrumentToken] = name;
                }
            }
            catch { }
        }

        private string ResolveName(uint token)
        {
            if (_instrumentNameCache.TryGetValue(token, out var name)) return name;
            try
            {
                var list = InstrumentHelper.LoadInstrumentsFromCsv(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv"));
                var it = list.FirstOrDefault(x => x.InstrumentToken == token);
                if (it != null)
                {
                    var nm = string.IsNullOrWhiteSpace(it.Tradingsymbol) ? (it.Name ?? token.ToString()) : it.Tradingsymbol;
                    _instrumentNameCache[token] = nm;
                    return nm;
                }
            }
            catch { }
            return token.ToString();
        }

        private bool ShouldRecord(
            uint token, double price, long vol, long lastQty,
            DateTime tickUtc, bool allowAfterHours = false)
        {
            if (!allowAfterHours && !SessionClock.IsRegularSessionAt(tickUtc))
            {
                SignalDiagnostics.Reject(token, ResolveName(token), ZerodhaOxySocket.Services.Clock.UtcToIst(tickUtc), $"SessionClock denied recording (afterHours). tickIst={ZerodhaOxySocket.Services.Clock.UtcToIst(tickUtc):O}");
                return false;
            }
            var last = _lastSeen.GetOrAdd(token, _ => (double.NaN, -1));
            bool unchanged = last.price == price && last.vol == vol;
            if (unchanged && lastQty <= 0)
            {
                SignalDiagnostics.Reject(token, ResolveName(token), ZerodhaOxySocket.Services.Clock.UtcToIst(tickUtc), $"Stale/no-trade tick: unchanged price/vol and lastQty={lastQty}");
                return false;
            }
            _lastSeen[token] = (price, vol);
            if (ShouldLogPerTick(token))
                SignalDiagnostics.Info(token, ResolveName(token), ZerodhaOxySocket.Services.Clock.UtcToIst(tickUtc), "SHOULDREC", $"Allowed (price={price}, vol={vol}, lastQty={lastQty})");
            return true;
        }

        private bool ShouldLogPerTick(uint token)
        {
            var now = SessionClock.NowIst();
            var last = _lastDiagLog.GetOrAdd(token, DateTime.MinValue);
            if (now - last < _perTickLogInterval) return false;
            _lastDiagLog[token] = now;
            return true;
        }

        // Restore SubscribeManual and UnsubscribeManual
        public void SubscribeManual(uint token)
        {
            _manualTokens.Add(token);
            var name = ResolveName(token);
            _contexts.GetOrAdd(token, _ => new InstrumentContext(token, name, TimeSpan.FromMinutes(1)));
            PortfolioManager.SetGroup(token, MakeGroupName(name));
            _socket?.Subscribe(new[] { token }, "full");
            OnStatus?.Invoke($"Manual subscribed {token} {name}");
        }

        public void UnsubscribeManual(uint token)
        {
            _manualTokens.Remove(token);
            _socket?.Unsubscribe(new[] { token });
            OnStatus?.Invoke($"Manual unsubscribed {token}");
        }

        // Restore MakeGroupName
        private string MakeGroupName(string tradingsymbol)
        {
            try
            {
                tradingsymbol ??= "";
                string s = tradingsymbol.ToUpperInvariant();
                if (s.EndsWith("CE") || s.EndsWith("PE")) s = s[..^2];
                int i = 0; while (i < s.Length && !char.IsDigit(s[i])) i++;
                string underlying = s.Substring(0, i).TrimEnd();
                string expiry = (i < s.Length) ? s.Substring(i) : "";
                return string.IsNullOrWhiteSpace(underlying) ? s : $"{underlying}-{expiry}";
            }
            catch { return tradingsymbol ?? "UNKNOWN"; }
        }
    }
}