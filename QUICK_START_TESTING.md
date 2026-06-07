# Quick Start: Side-by-Side Testing Guide

## ?? 5-Minute Setup

### Step 1: Enable Comparison Mode (30 seconds)

Edit `config.json`:
```json
{
  "EnableCandleComparison": true
}
```

### Step 2: Minimal Code Change (2 minutes)

Open `Services/TickHub.cs` and find the `ProcessTickFromPipelineAsync` method.

**Find this code** (around line 450):
```csharp
var ctx = _contexts.GetOrAdd(tokenU, _ =>
    new InstrumentContext(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes)));
```

**Replace with:**
```csharp
var ctx = _contexts.GetOrAdd(tokenU, _ =>
{
    if (Config.Current.EnableCandleComparison)
        return (object)new InstrumentContextComparison(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
    else
        return new InstrumentContext(tokenU, name, TimeSpan.FromMinutes(Config.Current.Trading.TimeframeMinutes));
});
```

**And update the processing** (same method, a few lines down):
```csharp
// OLD:
var closed = ctx.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);

// NEW:
Candle? closed = null;
if (ctx is InstrumentContextComparison comparison)
{
    closed = comparison.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
}
else if (ctx is InstrumentContext original)
{
    closed = original.ProcessTickWithTime(t.LastPrice, t.TickTime, qtyToUse);
}
```

**Update the dictionary type** (top of class):
```csharp
// OLD:
private readonly ConcurrentDictionary<uint, InstrumentContext> _contexts = new();

// NEW:
private readonly ConcurrentDictionary<uint, object> _contexts = new();
```

### Step 3: Build and Run (1 minute)

1. Build the solution (Ctrl+Shift+B)
2. Run the application (F5)
3. Open **Debug Output** window (Ctrl+Alt+O)

### Step 4: Monitor Results (During trading)

Watch for these log messages in Debug Output:

```
[NIFTY 50][MATCH] Candle #10 at 09:16:00 - Both implementations match perfectly
[NIFTY 50][VALUE_MISMATCH] Mismatch #1:
  Original:  09:17:00 O=23450.50 H=23455.75 L=23448.25 C=23453.00 V=15230
  Optimized: 09:17:00 O=23450.50 H=23455.75 L=23448.25 C=23453.00 V=15231
  Reason: Volume: 15230 vs 15231
```

### Step 5: Get Report (End of day)

Add this to your shutdown handler or call manually:

```csharp
// In MainWindow.xaml.cs or wherever you handle EOD
private void GenerateComparisonReport()
{
    foreach (var kvp in TickHub.Instance._contexts)
    {
        if (kvp.Value is InstrumentContextComparison comp)
        {
            var report = comp.GetComparisonReport();
            System.Diagnostics.Debug.WriteLine(report);
            
            // Optionally save to file
            System.IO.File.WriteAllText(
                $"comparison_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt", 
                report);
        }
    }
}
```

## ?? What to Look For

### ? Good Signs:
- Match rate > 99.9%
- Speedup > 5x
- Candles identical except for rare edge cases

### ?? Warning Signs:
- Match rate < 99%
- Frequent volume differences
- OHLC price mismatches

### ? Red Flags:
- Match rate < 95%
- NULL mismatches
- Time mismatches

## ?? Troubleshooting

### Issue: Build Errors

**Error:** `'InstrumentContext' is not compatible with 'object'`

**Fix:** Make sure you cast properly:
```csharp
return (object)new InstrumentContextComparison(...);
```

### Issue: No Logs Appearing

**Fix:** Check Debug Output window source is set to "Debug"

### Issue: Performance Seems Worse

**Cause:** Running both implementations doubles the work!

**Expected:** During comparison, performance will be ~2x slower. This is normal.
The speedup shown in the report is for the optimized version alone.

## ?? Minimal Test (No Code Changes!)

If you want to test without modifying TickHub:

1. Create a simple benchmark:

```csharp
// Add to BenchmarkSuite1/Program.cs
public static void QuickComparisonTest()
{
    var seed = new List<Candle>();
    var comparison = new InstrumentContextComparison(256265, "NIFTY 50", TimeSpan.FromMinutes(1), seed);
    
    // Simulate 1000 ticks
    var random = new Random();
    var basePrice = 23450.0;
    var time = DateTime.Now.Date.AddHours(9).AddMinutes(15);
    
    for (int i = 0; i < 1000; i++)
    {
        var price = basePrice + random.NextDouble() * 100 - 50;
        comparison.ProcessTickWithTime(price, time.AddSeconds(i * 6), 100);
    }
    
    Console.WriteLine(comparison.GetComparisonReport());
}
```

2. Run it: `dotnet run --project BenchmarkSuite1`

## ?? Decision Matrix

| Match Rate | Speedup | Decision |
|-----------|---------|----------|
| > 99.9% | > 5x | ? Deploy optimized version immediately |
| > 99.0% | > 3x | ? Deploy after reviewing differences |
| > 95.0% | > 2x | ?? Investigate differences first |
| < 95.0% | Any | ? Do NOT deploy, fix issues |
| Any | < 1.5x | ?? Optimization not worth complexity |

## ?? Switching Back

If something goes wrong:

1. Set `"EnableCandleComparison": false` in config.json
2. Restart application
3. Everything reverts to original implementation
4. Zero data loss, zero trading interruption

## ?? Next Steps

After 1 day of testing:

1. Review comparison report
2. If match rate > 99.9%, switch to optimized-only mode
3. Update `TickHub.cs` to use `InstrumentContextOptimized` directly
4. Remove comparison code (optional, can keep for future testing)
5. Enjoy 10x faster candle processing! ??
