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
    public class AutoSubConfig
    {
        public string Symbol { get; set; } = "NIFTY";
        public int Range { get; set; } = 10;
    }

    public class AppConfig
    {
        public string ApiKey { get; set; } = "";
        public string ApiSecret { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string SqlConnectionString { get; set; } = "Server=localhost;Database=ZerodhaDB;User Id=sa;Password=YourStrong!Pass;TrustServerCertificate=True;";
        public int DefaultTimeframeMinutes { get; set; } = 1;
        public bool PaperTrade { get; set; } = true;
        public bool EnableSimulator { get; set; } = false;
        public List<SubscribedInstrument> SubscribedInstruments { get; set; } = new();
        public List<AutoSubConfig> AutoSubscribe { get; set; } = new() { new AutoSubConfig { Symbol = "NIFTY", Range = 10 } };
        public CandleBuilderConfig CandleBuilder { get; set; } = new();

        [JsonProperty("Trading")]
        public TradingSettings Trading { get; set; } = new();
    }
    
    public class SubscribedInstrument
    {
        public long Token { get; set; }
        public string Name { get; set; } = "";
        public int TimeframeMinutes { get; set; } = 1;
    }
}
