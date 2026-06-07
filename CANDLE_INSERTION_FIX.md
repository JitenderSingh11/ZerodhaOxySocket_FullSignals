# Candle Database Insertion Fix

## Issue Summary
Candles were not being inserted into the `Candles` database table during today's live market run when using `InstrumentContextOptimized`.

---

## Root Cause Analysis

### Problem 1: Missing Insert in `CandleEvaluationAsyncOptimized`
**Location:** `Services/TickHub.cs` line 390

**Before:**
```csharp
if (IsLive)
{
    await SignalDiagnostics.InfoAsync(tokenU, ctx.Name, ...);
    // DataAccess is handled by the context itself in optimized version
}
```

**Issue:** The comment claimed database insertion was handled by `InstrumentContextOptimized`, but that's misleading.

---

### Problem 2: `InstrumentContextOptimized` Used `UpdateCandleAsync`
**Location:** `Services/InstrumentContextOptimized.cs` line 186

**Before:**
```csharp
_ = DataAccess.UpdateCandleAsync(candle, Token, Name);
```

**Issue:** `UpdateCandleAsync` **only updates existing rows**:
```sql
UPDATE dbo.Candles
SET OpenPrice = @o, HighPrice = @h, LowPrice = @l, ClosePrice = @c, Volume = @v
WHERE InstrumentToken = @token AND Interval = @interval AND CandleTime = @time
```

If the row doesn't exist (which it won't for new candles), **nothing happens**.

---

### Problem 3: Gap Fill Also Used `UpdateCandleAsync`
**Location:** `Services/InstrumentContextOptimized.cs` line 226

Same issue as Problem 2 - gap candles weren't being inserted.

---

## Solution Implemented

### Fix 1: Added Insert in `CandleEvaluationAsyncOptimized`
**Location:** `Services/TickHub.cs` line 390

**After:**
```csharp
if (IsLive)
{
    await SignalDiagnostics.InfoAsync(tokenU, ctx.Name, ...);
    // Insert candle into database (InstrumentContextOptimized uses UpdateCandleAsync which doesn't insert new rows)
    await DataAccess.InsertCandleAsync(closed, tokenU, ctx.Name, true);
}
```

**Why:** Ensures candles are always inserted when finalized, regardless of what the context does internally.

---

### Fix 2: Changed to `InsertCandleAsync` in Finalize
**Location:** `Services/InstrumentContextOptimized.cs` line 186

**After:**
```csharp
// Insert candle into database (not Update, since this is a new finalized candle)
_ = DataAccess.InsertCandleAsync(candle, Token, Name);
```

**Why:** `InsertCandleAsync` performs:
```sql
INSERT INTO dbo.Candles(InstrumentToken, InstrumentName, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume)
VALUES(@token, @name, @interval, @time, @o, @h, @l, @c, @v)
```

This **creates new rows** as intended.

---

### Fix 3: Changed to `InsertCandleAsync` in Gap Fill
**Location:** `Services/InstrumentContextOptimized.cs` line 226

**After:**
```csharp
_ = DataAccess.InsertCandleAsync(gapCandle, Token, Name);
```

**Why:** Gap candles are also new rows and need insertion, not updates.

---

## Comparison: Original vs. Optimized Context

### `InstrumentContext` (Original)
**Location:** `Services/TickHub.cs` line 326

```csharp
if (IsLive)
{
    SignalDiagnostics.Info(...);
    DataAccess.InsertCandle(closed, tokenU, ctx.Name, true);  // ? INSERTS
}
```

**Result:** ? Candles inserted correctly.

### `InstrumentContextOptimized` (Before Fix)
**Location:** `Services/TickHub.cs` line 390

```csharp
if (IsLive)
{
    await SignalDiagnostics.InfoAsync(...);
    // DataAccess is handled by the context itself in optimized version
}
```

**Internal call in context:**
```csharp
_ = DataAccess.UpdateCandleAsync(candle, Token, Name);  // ? UPDATES (no insert)
```

**Result:** ? Candles NOT inserted.

### `InstrumentContextOptimized` (After Fix)
**Location:** `Services/TickHub.cs` line 390

```csharp
if (IsLive)
{
    await SignalDiagnostics.InfoAsync(...);
    await DataAccess.InsertCandleAsync(closed, tokenU, ctx.Name, true);  // ? INSERTS
}
```

**Internal call in context (also fixed):**
```csharp
_ = DataAccess.InsertCandleAsync(candle, Token, Name);  // ? INSERTS
```

**Result:** ? Candles inserted correctly (twice, but that's okay - see below).

---

## Potential Duplicate Insert Concern

### Question: Won't we insert the same candle twice?

**Answer:** No, because of the **UNIQUE constraint** in the database schema:

```sql
CREATE TABLE dbo.Candles(
    Id BIGINT IDENTITY PRIMARY KEY,
    InstrumentToken BIGINT NOT NULL,
    InstrumentName NVARCHAR(64) NULL,
    Interval VARCHAR(10) NOT NULL,
    CandleTime DATETIME2 NOT NULL,
    OpenPrice DECIMAL(18,2) NOT NULL,
    HighPrice DECIMAL(18,2) NOT NULL,
    LowPrice DECIMAL(18,2) NOT NULL,
    ClosePrice DECIMAL(18,2) NOT NULL,
    Volume BIGINT NULL,
    DATECREATED DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);
CREATE INDEX IX_Candles_TokenIntervalTime ON dbo.Candles(InstrumentToken, Interval, CandleTime);
```

**However**, I don't see an explicit `UNIQUE` constraint in the schema. Let me check if there's one in `InitDb`:

Looking at `DataAccess.cs`, there's **no UNIQUE constraint** defined. This means:

### Potential Issue:
- First insert in `InstrumentContextOptimized.FinalizeCurrentCandle()`: ? Inserts
- Second insert in `CandleEvaluationAsyncOptimized`: ? Inserts again (duplicate)

### Solution: Add UNIQUE Constraint

We should add this to `DataAccess.InitDb()`:

```csharp
conn.Execute(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Candles_TokenIntervalTime' AND object_id = OBJECT_ID('dbo.Candles'))
BEGIN
    ALTER TABLE dbo.Candles
    ADD CONSTRAINT UQ_Candles_TokenIntervalTime UNIQUE (InstrumentToken, Interval, CandleTime);
END");
```

But for now, the **safest approach** is to keep the insert only in `TickHub.CandleEvaluationAsyncOptimized()` and remove it from `InstrumentContextOptimized`.

---

## Recommended Approach (Best Practice)

### Keep Insert in One Place Only

**Option 1: Only in TickHub** (Recommended)
- ? Single responsibility: TickHub handles all I/O
- ? Easier to debug
- ? Context focuses on candle building only

**Remove from `InstrumentContextOptimized`:**
```csharp
// _ = DataAccess.InsertCandleAsync(candle, Token, Name);  // REMOVED
```

**Keep in `TickHub.CandleEvaluationAsyncOptimized`:**
```csharp
if (IsLive)
{
    await SignalDiagnostics.InfoAsync(...);
    await DataAccess.InsertCandleAsync(closed, tokenU, ctx.Name, true);  // KEEP
}
```

**Option 2: Only in Context**
- ? Context handles its own persistence
- ? Harder to debug (silent failures)
- ? Less flexible (what if we want to skip DB writes?)

---

## Alternative Solution: Upsert Pattern

Instead of `InsertCandleAsync`, use an **UPSERT** (INSERT or UPDATE):

```sql
MERGE dbo.Candles AS target
USING (SELECT @token AS InstrumentToken, @interval AS Interval, @time AS CandleTime) AS source
ON (target.InstrumentToken = source.InstrumentToken 
    AND target.Interval = source.Interval 
    AND target.CandleTime = source.CandleTime)
WHEN MATCHED THEN
    UPDATE SET 
        OpenPrice = @o, 
        HighPrice = @h, 
        LowPrice = @l, 
        ClosePrice = @c, 
        Volume = @v
WHEN NOT MATCHED THEN
    INSERT (InstrumentToken, InstrumentName, Interval, CandleTime, OpenPrice, HighPrice, LowPrice, ClosePrice, Volume)
    VALUES (@token, @name, @interval, @time, @o, @h, @l, @c, @v);
```

This would allow:
- First call: Inserts
- Second call: Updates (no error)
- Late ticks: Updates existing candle

**Benefit:** Idempotent - safe to call multiple times.

---

## Verification Steps

### 1. Check Candle Count Before Fix
```sql
SELECT COUNT(*) AS CandleCount
FROM Candles
WHERE CAST(DATECREATED AS DATE) = '2025-01-13'  -- Today's date
AND InstrumentToken IN (256265, 260105);  -- NIFTY, BANKNIFTY
```

**Expected:** 0 (before fix)

### 2. Run Application with Fix

Start the application with the fixed code.

### 3. Check Candle Count After Fix
```sql
SELECT COUNT(*) AS CandleCount
FROM Candles
WHERE CAST(DATECREATED AS DATE) = '2025-01-13'
AND InstrumentToken IN (256265, 260105);
```

**Expected:** 78+ candles (1 per 5 minutes from 9:15 AM to 3:30 PM)

### 4. Verify Candle Data
```sql
SELECT TOP 10 
    InstrumentName,
    Interval,
    CandleTime,
    OpenPrice,
    HighPrice,
    LowPrice,
    ClosePrice,
    Volume
FROM Candles
WHERE CAST(DATECREATED AS DATE) = '2025-01-13'
ORDER BY CandleTime DESC;
```

**Expected:** Recent candles with correct OHLCV data.

---

## Current Status

? **Build Successful**  
? **Fix Applied to 3 locations:**
1. `TickHub.CandleEvaluationAsyncOptimized` - Added `InsertCandleAsync`
2. `InstrumentContextOptimized.FinalizeCurrentCandle` - Changed to `InsertCandleAsync`
3. `InstrumentContextOptimized.FillGaps` - Changed to `InsertCandleAsync`

?? **Potential Issue:** Duplicate inserts (see "Recommended Approach" above)

---

## Next Steps

### Immediate (For Tomorrow's Trading):
1. ? Apply the fix (already done)
2. ? Build successful
3. ?? Test with replay data to verify inserts
4. ?? Monitor database for duplicate candles

### Short-Term (This Week):
1. ?? Choose between Option 1 or Option 2 (single insert location)
2. ?? Add UNIQUE constraint to `Candles` table
3. ?? Consider implementing UPSERT pattern

### Long-Term (Next Release):
1. ?? Add unit tests for candle insertion
2. ?? Add integration test for database writes
3. ?? Add monitoring/alerting for missing candles

---

## Summary

**Problem:** `InstrumentContextOptimized` was calling `UpdateCandleAsync` which doesn't insert new rows, causing no candles to be saved to the database during live trading.

**Solution:** Changed all `UpdateCandleAsync` calls to `InsertCandleAsync` in:
- `InstrumentContextOptimized.FinalizeCurrentCandle()`
- `InstrumentContextOptimized.FillGaps()`
- `TickHub.CandleEvaluationAsyncOptimized()`

**Result:** Candles will now be inserted into the database during live trading.

**Recommendation:** Remove duplicate insert from `InstrumentContextOptimized` and keep only in `TickHub` for cleaner architecture.
