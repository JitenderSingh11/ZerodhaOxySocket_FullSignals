using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace ZerodhaOxySocket.Services
{
    /// <summary>
    /// Background timer service that periodically checks and finalizes stale candles
    /// </summary>
    public class CandleFinalizationTimer : IDisposable
    {
        private static CandleFinalizationTimer _instance;
        public static CandleFinalizationTimer Instance => _instance ??= new CandleFinalizationTimer();

        private Timer _timer;
        private readonly ConcurrentDictionary<uint, InstrumentContext> _contexts = new();
        private bool _isRunning = false;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5); // Check every 5 seconds
        private long _totalFinalized = 0;
        private DateTime _lastCheckTime = DateTime.MinValue;
        private int _timerTickCount = 0;
        private const int PerformanceReportInterval = 12; // Log every 60 seconds (12 * 5s)

        public long TotalFinalized => Interlocked.Read(ref _totalFinalized);

        private CandleFinalizationTimer() { }

        /// <summary>
        /// Start the timer with the given instrument contexts
        /// </summary>
        public void Start(ConcurrentDictionary<uint, InstrumentContext> contexts)
        {
            if (_isRunning)
            {
                System.Diagnostics.Debug.WriteLine("[CandleTimer] Already running");
                return;
            }

            _contexts.Clear();
            foreach (var kvp in contexts)
            {
                _contexts[kvp.Key] = kvp.Value;
            }

            _timer = new Timer(TimerCallback, null, TimeSpan.Zero, _checkInterval);
            _isRunning = true;

            System.Diagnostics.Debug.WriteLine($"[CandleTimer] Started with {_contexts.Count} instruments, check interval: {_checkInterval.TotalSeconds}s");
        }

        /// <summary>
        /// Register a new instrument context for monitoring
        /// </summary>
        public void RegisterContext(uint token, InstrumentContext context)
        {
            _contexts[token] = context;
            System.Diagnostics.Debug.WriteLine($"[CandleTimer] Registered context for token {token} ({context.Name})");
        }

        /// <summary>
        /// Unregister an instrument context
        /// </summary>
        public void UnregisterContext(uint token)
        {
            if (_contexts.TryRemove(token, out var ctx))
            {
                System.Diagnostics.Debug.WriteLine($"[CandleTimer] Unregistered context for token {token} ({ctx.Name})");
            }
        }

        private void TimerCallback(object state)
        {
            if (!_isRunning) return;

            try
            {
                var now = SessionClock.NowIst();
                _lastCheckTime = now;

                // Only check during market hours (optional - you can remove this check if needed)
                // `now` is already IST, SessionClock.IsRegularSessionAt expects any DateTime and normalizes it
                if (!SessionClock.IsRegularSessionAt(now))
                {
                    return;
                }

                int checkedCount = 0;
                int finalizedCount = 0;

                foreach (var kvp in _contexts)
                {
                    try
                    {
                        var ctx = kvp.Value;
                        var candlesBefore = ctx.GetCandles().Count;
                        
                        ctx.FinalizeIfStale();
                        
                        var candlesAfter = ctx.GetCandles().Count;
                        if (candlesAfter > candlesBefore)
                        {
                            finalizedCount++;
                            Interlocked.Increment(ref _totalFinalized);
                        }
                        
                        checkedCount++;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CandleTimer] Error checking token {kvp.Key}: {ex.Message}");
                    }
                }

                if (finalizedCount > 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CandleTimer] Checked {checkedCount} instruments, finalized {finalizedCount} stale candles " +
                        $"(total: {TotalFinalized})");
                }

                // Periodic performance report
                _timerTickCount++;
                if (_timerTickCount >= PerformanceReportInterval)
                {
                    LogPerformanceReport();
                    _timerTickCount = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CandleTimer] Timer callback error: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop the timer
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _timer?.Dispose();
            _timer = null;

            System.Diagnostics.Debug.WriteLine($"[CandleTimer] Stopped. Total candles finalized: {TotalFinalized}");
        }

        /// <summary>
        /// Get statistics about the timer
        /// </summary>
        public string GetStats()
        {
            return $"Running: {_isRunning}, Instruments: {_contexts.Count}, " +
                   $"Total Finalized: {TotalFinalized}, Last Check: {_lastCheckTime:HH:mm:ss}";
        }

        /// <summary>
        /// Get detailed performance metrics across all instruments
        /// </summary>
        public (long TotalMemoryBytes, int TotalCurrentTicks, int TotalLateTickBuckets, int TotalLateTicks) GetPerformanceMetrics()
        {
            long totalMemory = 0;
            int totalCurrentTicks = 0;
            int totalLateTickBuckets = 0;
            int totalLateTicks = 0;

            foreach (var ctx in _contexts.Values)
            {
                try
                {
                    var (_, currentTicks, lateTickBuckets, lateTicks, memoryBytes) = ctx.GetMemoryStats();
                    totalMemory += memoryBytes;
                    totalCurrentTicks += currentTicks;
                    totalLateTickBuckets += lateTickBuckets;
                    totalLateTicks += lateTicks;
                }
                catch { }
            }

            return (totalMemory, totalCurrentTicks, totalLateTickBuckets, totalLateTicks);
        }

        /// <summary>
        /// Log detailed performance report
        /// </summary>
        public void LogPerformanceReport()
        {
            var (totalMem, currentTicks, buckets, lateTicks) = GetPerformanceMetrics();
            var memoryMB = totalMem / (1024.0 * 1024.0);

            System.Diagnostics.Debug.WriteLine(
                $"\n[CandleTimer] Performance Report:\n" +
                $"  Instruments: {_contexts.Count}\n" +
                $"  Memory Usage: {memoryMB:F2} MB\n" +
                $"  Current Ticks in Buffers: {currentTicks}\n" +
                $"  Late Tick Buckets: {buckets}\n" +
                $"  Total Late Ticks: {lateTicks}\n" +
                $"  Total Candles Finalized: {TotalFinalized}\n" +
                $"  Last Check: {_lastCheckTime:HH:mm:ss}");
        }

        public void Dispose()
        {
            Stop();
            _contexts.Clear();
        }
    }
}
