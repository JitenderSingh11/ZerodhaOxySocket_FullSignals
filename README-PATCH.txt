ZOS Patch v2 (FULL): Underlying-driven signals + Dynamic timeframe seeding + IST CreatedAt
-----------------------------------------------------------------------------------------
Includes ALL required files:
  - Services/SessionClock.cs
  - Services/PortfolioManager.cs
  - Services/Config.cs
  - Services/DataAccess.cs
  - Services/InstrumentContext.cs
  - Services/TickHub.cs
  - Models/TickData.cs
  - config.json (example - merge Trading block into your config)

Key changes:
  * Signals computed only from NIFTY underlying with timeframe from config (default 5m).
  * Seeding: aggregates stored 1m candles into N-minute on startup to avoid long warmup.
  * Mapping: Buy->ATM CE, Sell->ATM PE.
  * Still records ±10 option strikes (unchanged).
  * IST fix: Signals.CreatedAt explicitly written using SessionClock.NowIst().
    If your existing table default uses SYSUTCDATETIME(), run the migration below.

Optional migration (run once if your Signals.CreatedAt default is UTC):
  ALTER TABLE dbo.Signals DROP CONSTRAINT IF EXISTS DF_Signals_CreatedAt;
  ALTER TABLE dbo.Signals ADD CONSTRAINT DF_Signals_CreatedAt DEFAULT SYSDATETIME() FOR CreatedAt;

Index (if not already):
  CREATE INDEX IX_Candles_TokenIntervalTime ON dbo.Candles(InstrumentToken, Interval, CandleTime);
