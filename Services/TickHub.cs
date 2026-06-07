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
        // CHANGED: Support both InstrumentContext and InstrumentContextComparison
        private readonly ConcurrentDictionary<uint, object> _contexts = new();

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

        private OrderManager _orderManager = OrderManager.Instance;
        private ExitManager _exitManager = ExitManager.Instance;
        private readonly Services.CandleFinalizationTimer _candleTimer = Services.CandleFinalizationTimer.Instance;

        // Instance-based services (singleton for live, new instance for replay)
        private UnderlyingCandleCache _candleCache = UnderlyingCandleCache.Instance;
        private SignalGate _signalGate = SignalGate.Instance;
        private OptionMapper _optionMapper = OptionMapper.Instance;

        public TickHub() { }

        public void Init(AppConfig _config, string apiKey, string accessToken, string connectionString)
        {
            _apiKey = apiKey;
            _accessToken = accessToken;
            _cs = connectionString;
            _enableChartUpdates = _config.EnableChartUpdates;

            _orderManager = OrderManager.Instance;
            _exitManager = ExitManager.Instance;

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
                _candleCache.AddCandleCacheSeeds(cfgTok, seed);
                var name = inst.Name;
                
                // CHANGED: Use Optimized by default
                object ctx;
                if (Config.Current.EnableCandleComparison)
                {
                    ctx = new InstrumentContextComparison(token, name, TimeSpan.FromMinutes(tf), seed);
                    OnStatus?.Invoke($"Initialized {name} in COMPARISON mode");
                }
                else
                {
                    ctx = new InstrumentContextOptimized(token, name, TimeSpan.FromMinutes(tf), seed);
                }
                _contexts[token] = ctx;
            }

            // Start the candle finalization timer (only for InstrumentContext)
            var contextList = new ConcurrentDictionary<uint, InstrumentContext>();
            foreach (var kvp in _contexts)
            {
                if (kvp.Value is InstrumentContext ic)
                    contextList[kvp.Key] = ic;
            }
            _candleTimer.Start(contextList);
            OnStatus?.Invoke($"Candle finalization timer started for {_contexts.Count} instruments");
        }

        public void ReplayInit(ReplayConfig replayConfig)
        {
            Config.Load(AppDomain.CurrentDomain.BaseDirectory);

            _orderManager = new OrderManager();
            _exitManager = new ExitManager(_candleCache, _orderManager);

            // NEW: Create replay-specific instances to isolate from live mode
            _candleCache = new UnderlyingCandleCache();
            _signalGate = new SignalGate();
            _optionMapper = new OptionMapper(); // Uses default CSV; could pass custom list if needed

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
                _candleCache.AddCandleCacheSeeds(cfgTok, seed);
                var name = inst.Name;
                
                // CHANGED: Use Optimized by default
                object ctx;
                if (Config.Current.EnableCandleComparison)
                    ctx = new InstrumentContextComparison(token, name, TimeSpan.FromMinutes(tf), seed);
                else
                    ctx = new InstrumentContextOptimized(token, name, TimeSpan.FromMinutes(tf), seed);
                _contexts[token] = ctx;
            }

            // Start the candle finalization timer for replay
            var contextList = new ConcurrentDictionary<uint, InstrumentContext>();
            foreach (var kvp in _contexts)
            {
                if (kvp.Value is InstrumentContext ic)
                    contextList[kvp.Key] = ic;
            }
            _candleTimer.Start(contextList);
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
            
            // CHANGED: Use Optimized by default
            var ctx = _contexts.GetOrAdd(tokenU, _ =>
            {
                if (Config.Current.EnableCandleComparison)
                    return (object)new InstrumentContextComparison(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
                else
                    return new InstrumentContextOptimized(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
            });

            // pass qty (prefer LastQuantity, fallback to Volume if available)
            long qtyToUse = (t.LastQuantity > 0) ? t.LastQuantity : (t.Volume > 0 ? t.Volume : 0);
            
            // CHANGED: Handle all context types
            Candle? closed = null;
            if (ctx is InstrumentContextComparison comparison)
            {
                closed = comparison.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                CandleEvaluation_Comparison(comparison, closed, tokenU, IsLive, replayId);
            }
            else if (ctx is InstrumentContextOptimized optimized)
            {
                closed = optimized.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                // We need a candle evaluation for optimized context
                CandleEvaluationOptimized(optimized, closed, tokenU, IsLive, replayId);
            }
            else if (ctx is InstrumentContext original)
            {
                closed = original.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                CandleEvaluation(original, closed, tokenU, IsLive, replayId);
            }

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

                    _candleCache.Put((long)tokenU, closed);

                    var sig = ctx.EvaluateSignalsPositionAware_Conservative();
                    if (sig != null)
                    {
                        if (!_signalGate.ShouldEmitSignal(tokenU, sig.Type, sig.Time, Config.Current.Trading.DebounceCandles))
                            return;

                        if (!Config.Current.Trading.AllowMultipleOpenPositions &&
                            _orderManager.HasOpenPositionForUnderlying(tokenU))
                            return;

                        var optType = (sig.Type == SignalType.Buy) ? "CE" : "PE";
                        var expiry = _optionMapper.GetNearestExpiry(ctx.Name);
                        var mapped = _optionMapper.ChooseATMOption(ctx.Name, sig.Price, expiry.Value, optType);
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

                            if (IsLive)
                            {
                                // Raise event for order creation
                                OnOrderCreated?.Invoke(this, new OrderCreatedEventArgs { Order = order });
                            }
                        }
                    }
                }
            }
        }

        private void CandleEvaluationOptimized(InstrumentContextOptimized? ctx, Candle? closed, uint tokenU, bool IsLive, Guid replayId)
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
                        SignalDiagnostics.Info(tokenU, ctx.Name, ZerodhaOxySocket.Services.Clock.UtcToIst(closed.Time), "CANDLE_OPT", $"Closed candle at {ZerodhaOxySocket.Services.Clock.UtcToIst(closed.Time):O} O={closed.Open} H={closed.High} L={closed.Low} C={closed.Close} V={closed.Volume}");
                        // DataAccess is handled by the context itself in optimized version
                    }

                    _candleCache.Put((long)tokenU, closed);

                    var sig = ctx.EvaluateSignalsPositionAware_Conservative();
                    if (sig != null)
                    {
                        if (!_signalGate.ShouldEmitSignal(tokenU, sig.Type, sig.Time, Config.Current.Trading.DebounceCandles))
                            return;

                        if (!Config.Current.Trading.AllowMultipleOpenPositions &&
                            _orderManager.HasOpenPositionForUnderlying(tokenU))
                            return;

                        var optType = (sig.Type == SignalType.Buy) ? "CE" : "PE";
                        var expiry = _optionMapper.GetNearestExpiry(ctx.Name);
                        var mapped = _optionMapper.ChooseATMOption(ctx.Name, sig.Price, expiry.Value, optType);
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

                            if (IsLive)
                            {
                                // Raise event for order creation
                                OnOrderCreated?.Invoke(this, new OrderCreatedEventArgs { Order = order });
                            }
                        }
                    }
                }
            }
        }

        public void ProcessSignalOrder(TickData tick, Guid replayId)
        {
            if (!_orderManager.HasPlacedPositionForInstrument(tick.InstrumentToken))
            {
                return;
            }

            var order = _orderManager.GetPlacedOrdersForInstrument(tick.InstrumentToken).FirstOrDefault();

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
            if (!_orderManager.HasPlacedPositionForInstrument(tick.InstrumentToken))
            {
                await SignalDiagnostics.RejectAsync(tick.InstrumentToken, ResolveName(tick.InstrumentToken), ZerodhaOxySocket.Services.Clock.UtcToIst(tick.TickTime), "No open position for instrument in ProcessSignalOrderAsync");
                return;
            }
            var order = _orderManager.GetPlacedOrdersForInstrument(tick.InstrumentToken).FirstOrDefault();
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
            
            // CHANGED: Support comparison mode
            var ctx = _contexts.GetOrAdd(tokenU, _ =>
            {
                if (Config.Current.EnableCandleComparison)
                    return (object)new InstrumentContextComparison(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
                else
                    return new InstrumentContext(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
            });

            long qtyToUse = (t.LastQuantity > 0) ? t.LastQuantity : (t.Volume > 0 ? t.Volume : 0);
            
            // CHANGED: Handle both types
            Candle? closed = null;
            if (ctx is InstrumentContextComparison comparison)
            {
                closed = comparison.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                CandleEvaluation_Comparison(comparison, closed, tokenU, IsLive: true, replayId: Guid.Empty);
            }
            else if (ctx is InstrumentContext original)
            {
                closed = original.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                CandleEvaluation(original, closed, tokenU, IsLive: true, replayId: Guid.Empty);
            }

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

            // CHANGED: Use Optimized by default
            var ctx = _contexts.GetOrAdd(tokenU, _ =>
            {
                if (Config.Current.EnableCandleComparison)
                    return (object)new InstrumentContextComparison(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
                else
                    return new InstrumentContextOptimized(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
            });

            long qtyToUse = (t.LastQuantity > 0) ? t.LastQuantity : (t.Volume > 0 ? t.Volume : 0);

            // CHANGED: Handle all context types
            Candle? closed = null;
            if (ctx is InstrumentContextComparison comparison)
            {
                closed = comparison.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                await CandleEvaluationAsync_Comparison(comparison, closed, tokenU, IsLive: true, replayId: Guid.Empty);
            }
            else if (ctx is InstrumentContextOptimized optimized)
            {
                closed = optimized.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                await CandleEvaluationAsyncOptimized(optimized, closed, tokenU, IsLive: true, replayId: Guid.Empty);
            }
            else if (ctx is InstrumentContext original)
            {
                closed = original.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                await CandleEvaluationAsync(original, closed, tokenU, IsLive: true, replayId: Guid.Empty);
            }

            if (_underlyingToken.Any(ut => ut != tokenU))
                _exitManager.OnOptionTick(tokenU, t.LastPrice, t.TickTime);
        }

        public async Task ProcessTickBatchFromPipelineAsync(List<TickData> batch)
        {
            if (batch == null || batch.Count == 0) return;

            // Group ticks by instrument to process them as a block for each context
            var grouped = batch.GroupBy(t => t.InstrumentToken);

            foreach (var group in grouped)
            {
                var tokenU = group.Key;
                var name = ResolveName(tokenU);
                var ctx = _contexts.GetOrAdd(tokenU, _ =>
                {
                    if (Config.Current.EnableCandleComparison)
                        return new InstrumentContextComparison(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
                    else
                        return new InstrumentContextOptimized(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
                });

                Candle? lastFinalizedCandle = null;

                foreach (var t in group)
                {
                    if (!ShouldRecord(tokenU, t.LastPrice, t.Volume, t.LastQuantity, t.TickTime))
                    {
                        continue;
                    }

                    OnLtp?.Invoke(tokenU, t.LastPrice, t.Volume);
                    long qtyToUse = (t.LastQuantity > 0) ? t.LastQuantity : (t.Volume > 0 ? t.Volume : 0);

                    Candle? closed = null;
                    if (ctx is InstrumentContextComparison comparison)
                    {
                        closed = comparison.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                        if (closed != null) lastFinalizedCandle = closed;
                    }
                    else if (ctx is InstrumentContextOptimized optimized)
                    {
                        closed = optimized.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                        if (closed != null) lastFinalizedCandle = closed;
                    }
                    else if (ctx is InstrumentContext original)
                    {
                        closed = original.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                        if (closed != null) lastFinalizedCandle = closed;
                    }

                    if (_underlyingToken.All(ut => ut != tokenU))
                    {
                        _exitManager.OnOptionTick(tokenU, t.LastPrice, t.TickTime);
                    }
                }

                // Perform candle evaluation only once per batch, with the last finalized candle
                if (lastFinalizedCandle != null)
                {
                    if (ctx is InstrumentContextComparison comparison)
                    {
                        await CandleEvaluationAsync_Comparison(comparison, lastFinalizedCandle, tokenU, IsLive: true, replayId: Guid.Empty);
                    }
                    else if (ctx is InstrumentContextOptimized optimized)
                    {
                        await CandleEvaluationAsyncOptimized(optimized, lastFinalizedCandle, tokenU, IsLive: true, replayId: Guid.Empty);
                    }
                    else if (ctx is InstrumentContext original)
                    {
                        await CandleEvaluationAsync(original, lastFinalizedCandle, tokenU, IsLive: true, replayId: Guid.Empty);
                    }
                }
            }
        }

        /// <summary>
        /// ULTRA-OPTIMIZED: Process a batch of ticks for a single instrument.
        /// Called by TickPipeline per instrument in parallel.
        /// </summary>
        public async Task ProcessTickBatchForInstrumentAsync(uint instrumentToken, List<TickData> ticksForInstrument)
        {
            if (ticksForInstrument == null || ticksForInstrument.Count == 0) return;

            var name = ResolveName(instrumentToken);
            var ctx = _contexts.GetOrAdd(instrumentToken, _ =>
            {
                if (Config.Current.EnableCandleComparison)
                    return (object)new InstrumentContextComparison(instrumentToken, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
                else
                    return new InstrumentContextOptimized(instrumentToken, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
            });

            Candle? lastFinalizedCandle = null;
            int processedCount = 0;

            // Process all ticks for this instrument in one go (minimizes lock contention)
            foreach (var t in ticksForInstrument)
            {
                if (!ShouldRecord(instrumentToken, t.LastPrice, t.Volume, t.LastQuantity, t.TickTime))
                    continue;

                OnLtp?.Invoke(instrumentToken, t.LastPrice, t.Volume);
                long qtyToUse = (t.LastQuantity > 0) ? t.LastQuantity : (t.Volume > 0 ? t.Volume : 0);

                Candle? closed = null;
                if (ctx is InstrumentContextComparison comparison)
                {
                    closed = comparison.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                    if (closed != null) lastFinalizedCandle = closed;
                }
                else if (ctx is InstrumentContextOptimized optimized)
                {
                    closed = optimized.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                    if (closed != null) lastFinalizedCandle = closed;
                }
                else if (ctx is InstrumentContext original)
                {
                    closed = original.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
                    if (closed != null) lastFinalizedCandle = closed;
                }

                if (_underlyingToken.All(ut => ut != instrumentToken))
                {
                    _exitManager.OnOptionTick(instrumentToken, t.LastPrice, t.TickTime);
                }

                processedCount++;
            }

            // Evaluate signals only once per batch (huge savings)
            if (lastFinalizedCandle != null)
            {
                if (ctx is InstrumentContextComparison comparison)
                {
                    await CandleEvaluationAsync_Comparison(comparison, lastFinalizedCandle, instrumentToken, IsLive: true, replayId: Guid.Empty);
                }
                else if (ctx is InstrumentContextOptimized optimized)
                {
                    await CandleEvaluationAsyncOptimized(optimized, lastFinalizedCandle, instrumentToken, IsLive: true, replayId: Guid.Empty);
                }
                else if (ctx is InstrumentContext original)
                {
                    await CandleEvaluationAsync(original, lastFinalizedCandle, instrumentToken, IsLive: true, replayId: Guid.Empty);
                }
            }
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
                    _candleCache.Put((long)tokenU, closed);
                    var sig = ctx.EvaluateSignalsPositionAware_Conservative();
                    if (sig != null)
                    {
                        if (!_signalGate.ShouldEmitSignal(tokenU, sig.Type, sig.Time, Config.Current.Trading.DebounceCandles))
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
                        var expiry = _optionMapper.GetNearestExpiry(ctx.Name);
                        var mapped = _optionMapper.ChooseATMOption(ctx.Name, sig.Price, expiry.Value, optType);
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

        private async Task CandleEvaluationAsyncOptimized(InstrumentContextOptimized? ctx, Candle? closed, uint tokenU, bool IsLive, Guid replayId)
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
                        await SignalDiagnostics.InfoAsync(tokenU, ctx.Name, ZerodhaOxySocket.Services.Clock.UtcToIst(closed.Time), "CANDLE_OPT", $"Closed candle at {ZerodhaOxySocket.Services.Clock.UtcToIst(closed.Time):O} O={closed.Open} H={closed.High} L={closed.Low} C={closed.Close} V={closed.Volume}");
                        // Database insert is handled by InstrumentContextOptimized.UpsertCandleAsync (idempotent)
                    }
                    _candleCache.Put((long)tokenU, closed);
                    var sig = ctx.EvaluateSignalsPositionAware_Conservative();
                    if (sig != null)
                    {
                        if (!_signalGate.ShouldEmitSignal(tokenU, sig.Type, sig.Time, Config.Current.Trading.DebounceCandles))
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
                        var expiry = _optionMapper.GetNearestExpiry(ctx.Name);
                        var mapped = _optionMapper.ChooseATMOption(ctx.Name, sig.Price, expiry.Value, optType);
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

            // CHANGED: Use Optimized by default
            var ctx = _contexts.GetOrAdd(instrumentToken, _ =>
            {
                if (Config.Current.EnableCandleComparison)
                    return (object)new InstrumentContextComparison(instrumentToken, instrumentName, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
                else
                    return new InstrumentContextOptimized(instrumentToken, instrumentName, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
            });

            // CHANGED: Handle all context types
            if (ctx is InstrumentContextComparison comparison)
            {
                comparison.AddCandle(closed);
                CandleEvaluation_Comparison(comparison, closed, instrumentToken, IsLive: false, replayId);
            }
            else if (ctx is InstrumentContextOptimized optimized)
            {
                optimized.AddCandle(closed);
                CandleEvaluationOptimized(optimized, closed, instrumentToken, IsLive: false, replayId);
            }
            else if (ctx is InstrumentContext original)
            {
                original.AddCandle(closed);
                CandleEvaluation(original, closed, instrumentToken, IsLive: false, replayId);
            }

            var ticks = new List<TickData>();
            var candles = new List<Candle>();

            if (_orderManager.HasPlacedPositionForUnderlying(instrumentToken)
                || _orderManager.HasOpenPositionForUnderlying(instrumentToken))
            {
                foreach (var o in _orderManager.GetPlacedOrdersForUnderlying(instrumentToken))
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
                if (instrumentToken != tick.InstrumentToken)
                { 
                    ProcessSignalOrder(tick, replayId);

                    _exitManager.OnOptionTick(tick.InstrumentToken, tick.LastPrice, tick.TickTime);
                }
            }

            foreach (var candle in candles)
            {
                if (instrumentToken != (candle.InstrumentToken))
                {
                    ProcessSignalOrder(new TickData { LastPrice = candle.Open,
                                                      InstrumentName = candle.InstrumentName,
                                                        InstrumentToken = (uint)candle.InstrumentToken,
                                                        TickTime = candle.Time

                    }, replayId);

                    ProcessSignalOrder(new TickData
                    {
                        LastPrice = candle.Close,
                        InstrumentName = candle.InstrumentName,
                        InstrumentToken = (uint)candle.InstrumentToken,
                        TickTime = candle.Time

                    }, replayId);

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
            
            // CHANGED: Use Optimized by default
            var ctx = _contexts.GetOrAdd(token, _ =>
            {
                if (Config.Current.EnableCandleComparison)
                    return (object)new InstrumentContextComparison(token, name, TimeSpan.FromMinutes(1));
                else
                    return new InstrumentContextOptimized(token, name, TimeSpan.FromMinutes(1));
            });

            // Register with timer only if it's original context
            if (ctx is InstrumentContext original)
                _candleTimer.RegisterContext(token, original);
            PortfolioManager.SetGroup(token, MakeGroupName(name));
            _socket?.Subscribe(new[] { token }, "full");
            OnStatus?.Invoke($"Manual subscribed {token} {name}");
        }

        public void UnsubscribeManual(uint token)
        {
            _manualTokens.Remove(token);
            _candleTimer.UnregisterContext(token);
            _socket?.Unsubscribe(new[] { token });
            OnStatus?.Invoke($"Manual unsubscribed {token}");
        }

        /// <summary>
        /// Get performance metrics for monitoring
        /// </summary>
        public string GetPerformanceStats()
        {
            var timerStats = _candleTimer.GetStats();
            var (totalMem, currentTicks, buckets, lateTicks) = _candleTimer.GetPerformanceMetrics();
            var memoryMB = totalMem / (1024.0 * 1024.0);

            return $"Contexts: {_contexts.Count}, Memory: {memoryMB:F2} MB, " +
                   $"BufferedTicks: {currentTicks}, LateTickBuckets: {buckets}, " +
                   $"SocketDrops: {_socketDropCounter}\n{timerStats}";
        }

        /// <summary>
        /// Force a performance report to debug output
        /// </summary>
        public void LogPerformanceReport()
        {
            _candleTimer.LogPerformanceReport();
            System.Diagnostics.Debug.WriteLine($"[TickHub] Socket Drops: {_socketDropCounter}");
        }

        // NEW: Handle comparison wrapper (uses original implementation for signals)
        private void CandleEvaluation_Comparison(InstrumentContextComparison? ctx, Candle? closed, uint tokenU, bool IsLive, Guid replayId)
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

                    _candleCache.Put((long)tokenU, closed);

                    // Use original implementation for signal generation
                    var sig = ctx.EvaluateSignalsPositionAware_Conservative();
                    if (sig != null)
                    {
                        if (!_signalGate.ShouldEmitSignal(tokenU, sig.Type, sig.Time, Config.Current.Trading.DebounceCandles))
                            return;

                        if (!Config.Current.Trading.AllowMultipleOpenPositions &&
                            _orderManager.HasOpenPositionForUnderlying(tokenU))
                            return;

                        var optType = (sig.Type == SignalType.Buy) ? "CE" : "PE";
                        var expiry = _optionMapper.GetNearestExpiry(ctx.Name);
                        var mapped = _optionMapper.ChooseATMOption(ctx.Name, sig.Price, expiry.Value, optType);
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

                            if (IsLive)
                                OnOrderCreated?.Invoke(this, new OrderCreatedEventArgs { Order = order });
                        }
                    }
                }
            }
        }

        // NEW: Async version for comparison wrapper
        private async Task CandleEvaluationAsync_Comparison(InstrumentContextComparison? ctx, Candle? closed, uint tokenU, bool IsLive, Guid replayId)
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
                    _candleCache.Put((long)tokenU, closed);
                    
                    // Use original for signal generation
                    var sig = ctx.EvaluateSignalsPositionAware_Conservative();
                    if (sig != null)
                    {
                        if (!_signalGate.ShouldEmitSignal(tokenU, sig.Type, sig.Time, Config.Current.Trading.DebounceCandles))
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
                        var expiry = _optionMapper.GetNearestExpiry(ctx.Name);
                        var mapped = _optionMapper.ChooseATMOption(ctx.Name, sig.Price, expiry.Value, optType);
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

        /// <summary>
        /// NEW: Get comparison report for all instruments
        /// </summary>
        public string GetComparisonReport()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("---------------------------------------------------------------=");
            report.AppendLine("  PERFORMANCE COMPARISON REPORT");
            report.AppendLine("---------------------------------------------------------------\n");

            int comparisonCount = 0;
            foreach (var kvp in _contexts)
            {
                if (kvp.Value is InstrumentContextComparison comp)
                {
                    report.AppendLine(comp.GetComparisonReport());
                    report.AppendLine();
                    comparisonCount++;
                }
            }

            if (comparisonCount == 0)
            {
                report.AppendLine("No comparison contexts found. Set EnableCandleComparison=true in config.json");
            }

            return report.ToString();
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
