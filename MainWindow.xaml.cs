using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using KiteConnect;

namespace ZerodhaOxySocket
{
    public partial class MainWindow : Window
    {
        private AppConfig _config;
        private readonly Dictionary<uint, OxyPlot.Wpf.PlotView> _plots = new();
        private long _tickCount = 0;
        private DateTime _lastTickLocal = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            LoadConfig();
            UpdateMenuState();
            _ = InstrumentDownloader.EnsureInstrumentsCsvAsync();

            TickHub.OnStatus += s => Dispatcher.Invoke(() => txtStatus.Text = s);
            TickHub.OnLtp += (token, ltp, vol) => Dispatcher.Invoke(() =>
            {
                _tickCount++;
                _lastTickLocal = DateTime.Now;
                txtTickStats.Text = $"Ticks: {_tickCount:n0} • Last: {_lastTickLocal:T} • Token: {token} • LTP: {ltp:F2}";
                UpdateChartTitle(token, ltp);
                AppendLog($"Tick {token}: LTP={ltp:F2} Vol={vol}");
            });

            TickHub.OnCandleClosed += (s, e) => Dispatcher.Invoke(() =>
            {
                AppendLog($"Candle {e.InstrumentName} O:{e.Candle.Open:F2} H:{e.Candle.High:F2} L:{e.Candle.Low:F2} C:{e.Candle.Close:F2} V:{e.Candle.Volume}");
            });
            TickHub.OnSignal += (s, e) => Dispatcher.Invoke(() =>
            {
                AppendLog($"SIGNAL {e.InstrumentName}: {e.Signal.Type} @ {e.Signal.Price:F2}");
            });
        }

        private void AppendLog(string line)
        {
            txtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
            txtLog.ScrollToEnd();
        }

        private string GetConfigPath() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        private void LoadConfig()
        {
            var path = GetConfigPath();
            if (!File.Exists(path))
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(new AppConfig(), Formatting.Indented));
                MessageBox.Show("Default config.json created. Please fill ApiKey/ApiSecret.", "Config");
            }
            _config = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }

        private void SaveConfig()
        {
            File.WriteAllText(GetConfigPath(), JsonConvert.SerializeObject(_config, Formatting.Indented));
        }

        private void UpdateMenuState()
        {
            bool hasToken = !string.IsNullOrWhiteSpace(_config?.AccessToken);
            miConnect.IsEnabled = hasToken;
            miSubscribeTabs.IsEnabled = hasToken;
            txtStatus.Text = hasToken ? "Ready to connect." : "⚠️ No access token. Use File → Refresh Access Token first.";
        }

        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_config.AccessToken))
            {
                txtStatus.Text = "⚠️ Cannot connect. Missing access token.";
                MessageBox.Show("AccessToken is empty. Use File → Refresh Access Token first.");
                return;
            }

            TickHub.Init(_config.ApiKey, _config.AccessToken, _config.SqlConnectionString);
            TickHub.Connect();

            var csv = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv");
            foreach (var auto in _config.AutoSubscribe)
            {
                string spotSymbol = auto.Symbol == "NIFTY" ? "NIFTY 50" : auto.Symbol;
                double spot = MarketDataHelper.GetSpotPrice(_config.ApiKey, _config.AccessToken, spotSymbol, "NSE");
                if (spot <= 0) continue;
                int step = auto.Symbol.Contains("BANK") ? 100 : 50;
                int atm = (int)(Math.Round(spot / step) * step);
                var tokens = new List<uint>();
                for (int i = -auto.Range; i <= auto.Range; i++)
                {
                    int strike = atm + i * step;
                    var ce = InstrumentHelper.GetOptionToken(auto.Symbol, strike, "CE", csv);
                    var pe = InstrumentHelper.GetOptionToken(auto.Symbol, strike, "PE", csv);
                    if (ce != 0) tokens.Add(ce);
                    if (pe != 0) tokens.Add(pe);
                }
                if (tokens.Count > 0)
                    TickHub.SubscribeAuto(tokens);
            }

            txtStatus.Text = "Connecting...";
        }

        private void SubscribeTabs_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_config.AccessToken))
            {
                txtStatus.Text = "⚠️ Connect first (requires AccessToken).";
                MessageBox.Show("Connect first (requires AccessToken).");
                return;
            }

            foreach (var sub in _config.SubscribedInstruments)
            {
                AddInstrumentTab((uint)sub.Token, sub.Name);
                TickHub.SubscribeManual((uint)sub.Token);
                AppendLog($"Subscribed (tab): {sub.Name} [{sub.Token}]");
            }
        }

        private void AddInstrumentTab(uint token, string name)
        {
            if (_plots.ContainsKey(token)) return;

            var model = new PlotModel { Title = name };
            var x = new CategoryAxis { Position = AxisPosition.Bottom };
            var y = new LinearAxis { Position = AxisPosition.Left };
            var candles = new CandleStickSeries { };
            model.Axes.Add(x); model.Axes.Add(y);
            model.Series.Add(candles);

            var pv = new OxyPlot.Wpf.PlotView { Model = model, Margin = new Thickness(6) };
            _plots[token] = pv;

            var header = new DockPanel { Tag = token };
            header.Children.Add(new TextBlock { Text = name });
            var btn = new Button { Content = "✖", Width = 22, Height = 22, Margin = new Thickness(6,0,0,0) };
            btn.Click += (s, e) => CloseInstrumentTab(token);
            header.Children.Add(btn);

            var tab = new TabItem { Header = header, Content = pv, Tag = token };
            InstrumentsTab.Items.Add(tab);
        }

        private void CloseInstrumentTab(uint token)
        {
            var tab = InstrumentsTab.Items.OfType<TabItem>().FirstOrDefault(t => (uint)t.Tag == token);
            if (tab != null) InstrumentsTab.Items.Remove(tab);
            if (_plots.ContainsKey(token)) _plots.Remove(token);
            TickHub.UnsubscribeManual(token);
        }

        private void UpdateChartTitle(uint token, double ltp)
        {
            if (_plots.TryGetValue(token, out var pv))
            {
                var baseTitle = pv.Model.Title.Split('[')[0].Trim();
                pv.Model.Title = $"{baseTitle} [{ltp:F2}]";
                pv.Model.InvalidatePlot(false);
            }
        }

        private void RefreshToken_Click(object sender, RoutedEventArgs e)
        {
            var url = $"https://kite.trade/connect/login?api_key={_config.ApiKey}&v=3";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

            var dlg = new InputDialog("Paste request_token from redirect:");
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var kite = new Kite(_config.ApiKey);
                    var req = dlg.Answer?.Trim();
                    if (string.IsNullOrWhiteSpace(_config.ApiSecret))
                        throw new Exception("ApiSecret is empty in config.json.");
                    if (string.IsNullOrWhiteSpace(req))
                        throw new Exception("Empty request_token.");

                    var user = kite.GenerateSession(req, _config.ApiSecret);
                    _config.AccessToken = user.AccessToken;
                    SaveConfig();
                    UpdateMenuState();

                    MessageBox.Show("Access token refreshed.");
                    txtStatus.Text = "Access token refreshed. Ready to connect.";
                }
                catch (KiteException ke)
                {
                    MessageBox.Show($"Refresh failed: {ke.Message}\nCode: {ke.Code}", "Kite Error");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Refresh failed: " + ex.Message);
                }
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
    }
}
