using System;
using System.Reflection.Metadata;
using System.Windows;
using static System.Net.Mime.MediaTypeNames;

namespace ZerodhaOxySocket
{
    public partial class ReplayWindow : Window
    {
        private ReplayEngine _engine;
        private ReplayConfig _cfg;

        public ReplayWindow()
        {
            InitializeComponent();
        }

        private DateTime CombineDateTime(DateTime date, string time)
        {
            if (!TimeSpan.TryParse(time, out var ts)) ts = new TimeSpan(9, 15, 0);
            return date.Date + ts;
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            var start = CombineDateTime(dpStart.SelectedDate ?? DateTime.Today, tbStartTime.Text);
            var end = CombineDateTime(dpEnd.SelectedDate ?? DateTime.Today, tbEndTime.Text);
            double scale = 10;
            double.TryParse(tbScale.Text, out scale);

            var tokens = new long[] { Config.Current.Trading.UnderlyingToken ?? 256265 }; // default NIFTY
            _cfg = new ReplayConfig { Start = start, End = end, Tokens = tokens, TimeScale = scale };
            _engine = new ReplayEngine(_cfg);

            // optional: subscribe to OnReplayTimeAdvance for UI update
            _engine.OnReplayTimeAdvance += dt => {
                Dispatcher.Invoke(() => tbLog.Text += $"Replaying: {dt:yyyy-MM-dd HH:mm:ss}\\n");
            };

            _engine.Start();
            tbLog.Text = "";
            tbLog.Text += $"Started replay {start:yyyy-MM-dd HH:mm:ss} -> {end:yyyy-MM-dd HH:mm:ss} scale={scale}\\n";
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _engine?.Stop();
            tbLog.Text += "Replay stopped\\n";
        }
    }
}
