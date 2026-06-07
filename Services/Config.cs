using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;

namespace ZerodhaOxySocket
{
    public sealed class TradingSettings
    {
        public int TimeframeMinutes { get; set; }
        public long? UnderlyingToken { get; set; }
        public bool AllowMultipleOpenPositions { get; set; }
        public int DebounceCandles { get; set; }

        public int AtrPeriod { get; set; }
        public double AtrStopMult { get; set; }
        public double AtrTrailMult { get; set; }

        public int FastEma { get; set; }
        public int SlowEma { get; set; }
        public int RsiPeriod { get; set; }
        public double RsiBuyBelow { get; set; }
        public double RsiSellAbove { get; set; }

        public string EodExit { get; set; }
        public double MinBodyPct { get; set; }
        public double MinRangeAtr { get; set; }

        /// <summary>
        /// Minimum historical bars required before evaluating signals.
        /// Used to warm up indicators.
        /// </summary>
        public int SeedBars { get; set; }
    }

    public static class Config
    {
        private static AppConfig _cfg = new AppConfig();
        public static AppConfig Current => _cfg;

        public static string ConnectionString { get; private set; }

        public static void Load(string baseDir)
        {
            var path = Path.Combine(baseDir, "config.json");
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var cfg = JsonConvert.DeserializeObject<AppConfig>(json);
            if (cfg != null) _cfg = cfg;

            ConnectionString = _cfg.SqlConnectionString;
        }
    }

    public class EmailConfig
    {
        public bool Enabled { get; set; } = false;
        public string SmtpServer { get; set; } = "smtp.gmail.com";
        public int Port { get; set; } = 587;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string To { get; set; } = "";
    }
    public class CandleBuilderConfig
    {
        public bool Enabled { get; set; } = true;
        public string RunAt { get; set; } = "15:40";
        public List<string> Intervals { get; set; } = new() { "1m", "5m", "15m" };
        public EmailConfig EmailNotification { get; set; } = new();
    }

    public class TickWriterConfig
    {
        /// <summary>
        /// Number of partitions for the writer. 0 means auto (ProcessorCount/2).
        /// </summary>
        public int Partitions { get; set; } = 0;

        /// <summary>
        /// Total channel capacity (will be divided among partitions).
        /// </summary>
        public int ChannelCapacity { get; set; } = 300000;

        /// <summary>
        /// Maximum concurrent SqlBulkCopy operations.
        /// </summary>
        public int MaxConcurrentBulkWrites { get; set; } = 3;

        /// <summary>
        /// Batch size for each bulk insert.
        /// </summary>
        public int DbBatchSize { get; set; } = 1000;

        /// <summary>
        /// Flush interval in seconds.
        /// </summary>
        public int DbFlushIntervalSeconds { get; set; } = 2;

        /// <summary>
        /// Maximum allowed tick delay in seconds before dropping the tick.
        /// </summary>
        public int MaxAllowedTickDelaySeconds { get; set; } = 8;

        /// <summary>
        /// Number of writer tasks per partition.
        /// </summary>
        public int WritersPerPartition { get; set; } = 4;
    }


    public class TickPipelineConfig
    {
        /// <summary>
        /// Number of partitions for the pipeline. 0 means auto (ProcessorCount/2).
        /// </summary>
        public int Partitions { get; set; } = 0;

        /// <summary>
        /// Total channel capacity (will be divided among partitions).
        /// </summary>
        public int ChannelCapacity { get; set; } = 300000;

        /// <summary>
        /// Number of consumer tasks per partition.
        /// </summary>
        public int ConsumersPerPartition { get; set; } = 4;
    }

    public class OrderPipelineConfig
    {
        /// <summary>
        /// Number of partitions for order processing. 0 means auto (ProcessorCount/4).
        /// </summary>
        public int Partitions { get; set; } = 0;

        /// <summary>
        /// Channel capacity for order ticks (typically smaller than main pipeline).
        /// </summary>
        public int ChannelCapacity { get; set; } = 50000;

        /// <summary>
        /// Number of consumer tasks per partition for order processing.
        /// </summary>
        public int ConsumersPerPartition { get; set; } = 2;
    }

    public class AppConfig
    {
        public string ApiKey { get; set; } = "";
        public string ApiSecret { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string SqlConnectionString { get; set; } = "Server=localhost;Database=ZerodhaDB;User Id=sa;Password=YourStrong!Pass;TrustServerCertificate=True;";

        // Optional: tick retention window config for candle formation
        public int TickRetentionWindowMinutes { get; set; }
        public int DefaultTimeframeMinutes { get; set; } = 1;
        public bool PaperTrade { get; set; } = true;
        public bool EnableSimulator { get; set; } = false;
        public List<SubscribedInstrument> SubscribedInstruments { get; set; } = new();

        public CandleBuilderConfig CandleBuilder { get; set; } = new();

        [JsonProperty("Trading")]
        public TradingSettings Trading { get; set; } = new();

        // TickWriter settings
        public TickWriterConfig TickWriter { get; set; } = new();

        // TickPipeline settings
        public TickPipelineConfig TickPipeline { get; set; } = new();

        // OrderPipeline settings
        public OrderPipelineConfig OrderPipeline { get; set; } = new();

        public bool EnableChartUpdates { get; set; } = false;

        /// <summary>
        /// Enable real-time candle chart rendering (OxyPlot). 
        /// Disable to save ~100-200 MB RAM and reduce UI thread overhead.
        /// </summary>
        public bool EnableLiveCharting { get; set; } = false;

        /// <summary>
        /// Enable side-by-side comparison of Original vs Optimized candle builder
        /// </summary>
        public bool EnableCandleComparison { get; set; } = false;
    }
    
    public class SubscribedInstrument
    {
        public long Token { get; set; }
        public string Name { get; set; } = "";
        public int TimeframeMinutes { get; set; } = 1;

        public string Symbol { get; set; }


        public string SpotSymbol { get; set; }

        public int Range { get; set; } = 10;
    }
}
