using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ZerodhaOxySocket.Services;

namespace ZerodhaOxySocket
{
    /// <summary>
    /// Dual-context wrapper that runs both Original and Optimized implementations
    /// in parallel and compares results for validation.
    /// </summary>
    public class InstrumentContextComparison
    {
        private readonly InstrumentContext _original;
        private readonly InstrumentContextOptimized _optimized;
        private readonly uint _token;
        private readonly string _name;

        // Expose Name property for TickHub
        public string Name => _name;

        // Comparison statistics
        private int _totalTicks;
        private int _candleMatches;
        private int _candleMismatches;
        private readonly List<CandleComparisonResult> _differences = new();
        private readonly Stopwatch _originalTimer = new();
        private readonly Stopwatch _optimizedTimer = new();

        public InstrumentContextComparison(uint token, string name, TimeSpan timeframe, IEnumerable<Candle>? seed = null, CandleBatchWriter batchWriter = null)
        {
            _token = token;
            _name = name;
            _original = new InstrumentContext(token, name, timeframe, seed);
            _optimized = new InstrumentContextOptimized(token, name, timeframe, seed);
        }

        public Candle? ProcessTickWithTime(double ltp, DateTime tickTime, long qty = 0)
        {
            // VALIDATION: Check incoming tick time
            if (tickTime < new DateTime(2020, 1, 1) || tickTime > DateTime.UtcNow.AddDays(1))
            {
                _ = SignalDiagnostics.WarnAsync(_token, _name, tickTime, 
                    $"COMPARISON: Invalid tick timestamp: {tickTime:O}. Skipping.");
                return null; // Skip invalid tick
            }

            _totalTicks++;

            // Process with original implementation
            _originalTimer.Start();
            var originalCandle = _original.ProcessTickWithTime(ltp, tickTime, qty);
            _originalTimer.Stop();

            // Process with optimized implementation
            _optimizedTimer.Start();
            var optimizedCandle = _optimized.ProcessTickWithTime(ltp, tickTime, qty);
            _optimizedTimer.Stop();

            // Compare results
            if (originalCandle != null || optimizedCandle != null)
            {
                CompareCandles(originalCandle, optimizedCandle);
            }

            // Return original for now (production uses original)
            return originalCandle;
        }

        private void CompareCandles(Candle? original, Candle? optimized)
        {
            // Both should either be null or non-null
            if ((original == null) != (optimized == null))
            {
                _candleMismatches++;
                _differences.Add(new CandleComparisonResult
                {
                    Time = original?.Time ?? optimized?.Time ?? DateTime.MinValue,
                    OriginalExists = original != null,
                    OptimizedExists = optimized != null,
                    Reason = "One candle is null, the other is not"
                });
                LogMismatch("NULL_MISMATCH", original, optimized);
                return;
            }

            if (original == null) return; // Both are null

            // Compare OHLC values with tolerance
            const double tolerance = 0.01; // 1 paisa tolerance for floating point comparison
            
            bool timeMatch = original.Time == optimized.Time;
            bool openMatch = Math.Abs(original.Open - optimized.Open) < tolerance;
            bool highMatch = Math.Abs(original.High - optimized.High) < tolerance;
            bool lowMatch = Math.Abs(original.Low - optimized.Low) < tolerance;
            bool closeMatch = Math.Abs(original.Close - optimized.Close) < tolerance;
            bool volumeMatch = original.Volume == optimized.Volume;

            if (timeMatch && openMatch && highMatch && lowMatch && closeMatch && volumeMatch)
            {
                _candleMatches++;
                
                // Log every 50th match to reduce noise
                if (_candleMatches % 50 == 0)
                {
                    _ = SignalDiagnostics.InfoAsync(_token, _name, original.Time, "CompareMatch",
                        $"Candle #{_candleMatches} - Both implementations match perfectly");
                }
            }
            else
            {
                _candleMismatches++;
                
                var reasons = new List<string>();
                if (!timeMatch) reasons.Add($"Time: {original.Time:HH:mm:ss} vs {optimized.Time:HH:mm:ss}");
                if (!openMatch) reasons.Add($"Open: {original.Open:F2} vs {optimized.Open:F2}");
                if (!highMatch) reasons.Add($"High: {original.High:F2} vs {optimized.High:F2}");
                if (!lowMatch) reasons.Add($"Low: {original.Low:F2} vs {optimized.Low:F2}");
                if (!closeMatch) reasons.Add($"Close: {original.Close:F2} vs {optimized.Close:F2}");
                if (!volumeMatch) reasons.Add($"Volume: {original.Volume} vs {optimized.Volume}");

                _differences.Add(new CandleComparisonResult
                {
                    Time = original.Time,
                    OriginalExists = true,
                    OptimizedExists = true,
                    OriginalCandle = original,
                    OptimizedCandle = optimized,
                    Reason = string.Join(", ", reasons)
                });

                LogMismatch("VALUE_MISMATCH", original, optimized, string.Join(", ", reasons));
            }
        }

        private void LogMismatch(string type, Candle? original, Candle? optimized, string reason = "")
        {
            var time = original?.Time ?? optimized?.Time ?? SessionClock.NowIst();
            var origStr = original != null 
                ? $"O={original.Open:F2} H={original.High:F2} L={original.Low:F2} C={original.Close:F2} V={original.Volume}"
                : "NULL";
            var optStr = optimized != null
                ? $"O={optimized.Open:F2} H={optimized.High:F2} L={optimized.Low:F2} C={optimized.Close:F2} V={optimized.Volume}"
                : "NULL";
            
            _ = SignalDiagnostics.WarnAsync(_token, _name, time,
                $"Compare{type} Mismatch #{_candleMismatches}: Orig[{origStr}] Opt[{optStr}] Reason: {reason}");
        }

        public IReadOnlyList<Candle> GetCandles() => _original.GetCandles();

        public void FinalizeIfStale()
        {
            _original.FinalizeIfStale();
            _optimized.FinalizeIfStale();
        }

        public void AddCandle(Candle c)
        {
            _original.AddCandle(c);
            _optimized.AddCandle(c);
        }

        public SignalResult EvaluateSignalsPositionAware_Conservative()
        {
            // Use original for signals (production behavior)
            return _original.EvaluateSignalsPositionAware_Conservative();
        }

        /// <summary>
        /// Get comprehensive comparison report
        /// </summary>
        public string GetComparisonReport()
        {
            var avgOriginalUs = _totalTicks > 0 ? _originalTimer.Elapsed.TotalMicroseconds / _totalTicks : 0;
            var avgOptimizedUs = _totalTicks > 0 ? _optimizedTimer.Elapsed.TotalMicroseconds / _totalTicks : 0;
            var speedup = avgOriginalUs > 0 ? avgOriginalUs / avgOptimizedUs : 0;
            var matchRate = (_candleMatches + _candleMismatches) > 0
                ? (_candleMatches * 100.0) / (_candleMatches + _candleMismatches)
                : 100.0;

            var report = $@"
???????????????????????????????????????????????????????????????
  COMPARISON REPORT: {_name}
???????????????????????????????????????????????????????????????

PROCESSING STATISTICS:
  Total Ticks Processed:     {_totalTicks:n0}
  Candles Matches:           {_candleMatches} ({matchRate:F2}%)
  Candles Mismatches:        {_candleMismatches}

PERFORMANCE:
  Original Avg:              {avgOriginalUs:F2} ?s/tick
  Optimized Avg:             {avgOptimizedUs:F2} ?s/tick
  Speedup:                   {speedup:F2}x faster
  Time Saved:                {(_originalTimer.Elapsed - _optimizedTimer.Elapsed).TotalMilliseconds:F2} ms

MEMORY:
  Original:                  {_original.GetStats()}
  Optimized:                 {_optimized.GetStats()}

VERDICT:
  {GetVerdict(matchRate, speedup)}
???????????????????????????????????????????????????????????????
";

            if (_differences.Count > 0 && _differences.Count <= 10)
            {
                report += "\nFIRST DIFFERENCES:\n";
                foreach (var diff in _differences.Take(10))
                {
                    report += $"  {diff.Time:HH:mm:ss} - {diff.Reason}\n";
                }
            }

            return report;
        }

        private string GetVerdict(double matchRate, double speedup)
        {
            if (matchRate >= 99.9 && speedup >= 5)
                return "? EXCELLENT! Optimized version is production-ready.";
            if (matchRate >= 99.0 && speedup >= 3)
                return "? GOOD! Minor differences acceptable, significant speedup.";
            if (matchRate >= 95.0 && speedup >= 2)
                return "??  FAIR! Some differences detected, investigate if acceptable.";
            if (matchRate < 95.0)
                return "? FAILED! Too many differences, do NOT use optimized version.";
            if (speedup < 1.5)
                return "??  WARNING! Minimal speedup, optimization may not be worth it.";
            return "??  REVIEW NEEDED! Check individual differences carefully.";
        }

        public List<CandleComparisonResult> GetDifferences() => _differences;
    }

    public class CandleComparisonResult
    {
        public DateTime Time { get; set; }
        public bool OriginalExists { get; set; }
        public bool OptimizedExists { get; set; }
        public Candle? OriginalCandle { get; set; }
        public Candle? OptimizedCandle { get; set; }
        public string Reason { get; set; }
    }
}
