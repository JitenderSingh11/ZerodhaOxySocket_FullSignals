# ? Enhanced Shutdown Diagnostic Logging

## Summary

Added comprehensive diagnostic logging to the `MainWindow_Closing` event to help identify why comparison reports aren't being generated or saved during application shutdown.

## Changes Made

### Enhanced `MainWindow_Closing` Method

The updated shutdown handler now provides step-by-step logging for every operation:

#### 1. **Initial Checks**
```csharp
AppendLog($"Comparison mode enabled: {Config.Current.EnableCandleComparison}");
```
- Logs whether comparison mode is enabled
- Displays warning if disabled

#### 2. **Report Generation**
```csharp
var report = TickHub.Instance.GetComparisonReport();
AppendLog($"Report generated. Length: {report?.Length ?? 0} characters");
```
- Logs report length
- Warns if report is null or empty

#### 3. **Directory Creation**
```csharp
AppendLog($"Reports directory: {reportsDir}");
Directory.CreateDirectory(reportsDir);
```
- Shows exact path where report will be saved
- Logs whether directory already existed or was created

#### 4. **File Writing**
```csharp
File.WriteAllText(reportPath, report ?? "No comparison data available");
AppendLog($"? File size: {new FileInfo(reportPath).Length} bytes");
```
- Logs the exact file path
- Shows file size after writing
- Verifies file exists after write

#### 5. **Summary Display**
```csharp
// Extract key metrics from report
foreach (var line in lines)
{
    if (line.Contains("VERDICT:") || line.Contains("Match") || line.Contains("Speedup:"))
        AppendLog(line.Trim());
}
```
- Displays comparison verdict in UI log
- Shows match rate and speedup statistics

#### 6. **Error Handling**
```csharp
catch (Exception exReport)
{
    AppendLog($"? Comparison report error: {exReport.GetType().Name}");
    AppendLog($"  Message: {exReport.Message}");
    AppendLog($"  Stack: {exReport.StackTrace}");
}
```
- Captures full exception details
- Shows inner exceptions if present
- Doesn't silently swallow errors

## New Diagnostic Files

### 1. **Shutdown Log** (Always Created)
```
Location: C:\Users\vboxuser\AppData\Local\ZerodhaOxySocket\logs\shutdown_yyyyMMdd_HHmmss.log
```

Contains:
```
=== SHUTDOWN LOG 2024-12-09 15:30:45 ===
Shutdown initiated
EnableCandleComparison: true
Starting comparison report generation
Report length: 2543
Reports directory: C:\Users\vboxuser\AppData\Local\ZerodhaOxySocket\reports
Directory already exists
Report path: C:\Users\vboxuser\AppData\Local\ZerodhaOxySocket\reports\comparison_report_20241209_153045.txt
File written. Size: 2543 bytes
File verified to exist
Summary lines: 5
Shutdown complete
```

### 2. **Comparison Report** (If Comparison Mode Enabled)
```
Location: C:\Users\vboxuser\AppData\Local\ZerodhaOxySocket\reports\comparison_report_yyyyMMdd_HHmmss.txt
```

## UI Log Output

During shutdown, you'll see detailed logging in the application's log window:

```
========================================
SHUTDOWN INITIATED
========================================
Comparison mode enabled: true
Generating comparison report...
Calling TickHub.Instance.GetComparisonReport()...
Report generated. Length: 2543 characters
Reports directory: C:\Users\vboxuser\AppData\Local\ZerodhaOxySocket\reports
Reports directory already exists
Writing report to: C:\...\comparison_report_20241209_153045.txt
? Comparison report saved successfully!
? File size: 2543 bytes
? Verified: Report file exists at C:\...\comparison_report_20241209_153045.txt
=== COMPARISON SUMMARY ===
  Total Ticks Processed:     50,342
  Candles Matches:           245 (99.59%)
  Speedup:                   5.23x faster
  ? EXCELLENT! Optimized version is production-ready.
Logged 5 summary lines
========================================
? SHUTDOWN COMPLETE
========================================
Shutdown diagnostic log: C:\...\logs\shutdown_20241209_153045.log
```

## Troubleshooting Guide

### Scenario 1: No Report Generated

**Log shows:**
```
Comparison mode enabled: false
? Comparison mode is DISABLED - no report will be generated
  To enable: Set 'EnableCandleComparison': true in config.json
```

**Solution:**
1. Open `config.json`
2. Add or change: `"EnableCandleComparison": true`
3. Restart application

### Scenario 2: Empty Report

**Log shows:**
```
Report generated. Length: 0 characters
WARNING: Report is empty or null!
```

**Possible causes:**
- No instruments were subscribed during session
- Application was connected but no ticks were received
- Comparison contexts were never created

**Solution:**
1. Check if you clicked "Connect" before closing
2. Verify instruments are configured in `config.json`:
   ```json
   "SubscribedInstruments": [
     { "Token": 256265, "Name": "NIFTY" }
   ]
   ```
3. Ensure some ticks were processed (check tick counter in UI)

### Scenario 3: File Write Error

**Log shows:**
```
? Comparison report error: UnauthorizedAccessException
  Message: Access to the path '...' is denied.
```

**Solution:**
1. Check folder permissions for:
   ```
   C:\Users\vboxuser\AppData\Local\ZerodhaOxySocket\reports
   ```
2. Run as administrator (if needed)
3. Check if antivirus is blocking file writes

### Scenario 4: File Not Found After Write

**Log shows:**
```
? File size: 2543 bytes
? ERROR: Report file does NOT exist after write!
```

**Possible causes:**
- File was written but immediately deleted by antivirus
- Network drive disconnected
- Disk full

**Solution:**
1. Check antivirus exclusions
2. Check available disk space
3. Look in the shutdown diagnostic log for more details

## Diagnostic Checklist

When reporting issues, provide:

1. **Shutdown log file**:
   ```
   C:\Users\vboxuser\AppData\Local\ZerodhaOxySocket\logs\shutdown_yyyyMMdd_HHmmss.log
   ```

2. **Config.json snippet**:
   ```json
   {
     "EnableCandleComparison": true,
     "SubscribedInstruments": [...]
   }
   ```

3. **Screenshots of**:
   - Application log window during shutdown
   - File Explorer showing reports folder contents

4. **Session details**:
   - Was "Connect" clicked?
   - Were ticks received? (check tick counter)
   - How long was app running?
   - Normal close or force-close?

## Testing the Diagnostic Logging

### Test 1: Normal Shutdown
1. Start application
2. Click "Connect"
3. Let it run for a few minutes (receive ticks)
4. Close application normally (X button)
5. Check log window for shutdown sequence
6. Open shutdown log file
7. Verify report file exists

### Test 2: No Connection
1. Start application
2. Do NOT click "Connect"
3. Close application
4. Check if shutdown log explains why no report

### Test 3: Comparison Mode Disabled
1. Edit `config.json`: `"EnableCandleComparison": false`
2. Start and connect
3. Close application
4. Verify log shows comparison mode disabled message

## Performance Impact

- **Minimal**: Logging only occurs during shutdown
- **UI responsiveness**: No impact (logging is synchronous but shutdown already blocks)
- **Disk space**: 
  - Shutdown logs: ~1-2 KB each
  - Comparison reports: ~2-10 KB each
  - Total: <100 KB for 50 shutdowns

## Future Improvements

1. **Auto-cleanup**: Delete shutdown logs older than 7 days
2. **Email reports**: Send comparison reports via email on shutdown
3. **Cloud backup**: Upload reports to cloud storage
4. **Periodic reports**: Generate reports every hour (not just on shutdown)

---

**The enhanced logging is now ready to help diagnose why comparison reports weren't being created!**
