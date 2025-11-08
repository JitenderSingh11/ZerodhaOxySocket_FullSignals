using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;

namespace ZerodhaOxySocket
{
    public sealed class TradingSettings
    {
        public int TimeframeMinutes { get; set; } = 5;
        public long? UnderlyingToken { get; set; } = 256265;
        public bool AllowMultipleOpenPositions { get; set; } = false;
        public int DebounceCandles { get; set; } = 2;

        public int AtrPeriod { get; set; } = 14;
        public double AtrStopMult { get; set; } = 1.5;
        public double AtrTrailMult { get; set; } = 2.0;

        public int FastEma { get; set; } = 20;
        public int SlowEma { get; set; } = 50;
        public int RsiPeriod { get; set; } = 14;
        public double RsiBuyBelow { get; set; } = 55;
        public double RsiSellAbove { get; set; } = 45;

        public string EodExit { get; set; } = "15:20:00";
        public double MinBodyPct { get; set; } = 0.15;
        public double MinRangeAtr { get; set; } = 0.6;

        /// <summary>
        /// Minimum historical bars required before evaluating signals.
        /// Used to warm up indicators.
        /// </summary>
        public int SeedBars { get; set; } = 200;
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

        public TradingSettings Trading { get; set; } = new TradingSettings();
    }
    
    public class SubscribedInstrument
    {
        public long Token { get; set; }
        public string Name { get; set; } = "";
        public int TimeframeMinutes { get; set; } = 1;
    }
}
