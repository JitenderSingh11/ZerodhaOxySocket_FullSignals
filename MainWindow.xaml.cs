using KiteConnect;
using Newtonsoft.Json;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ZerodhaOxySocket.Helpers;
using ZerodhaOxySocket.Services;

namespace ZerodhaOxySocket
{
    public partial class MainWindow : Window
    {
        private AppConfig _config;
        private readonly Dictionary<uint, OxyPlot.Wpf.PlotView> _plots = new();
        private long _tickCount = 0;
        private DateTime _lastTickLocal = DateTime.MinValue;

        private PlotModel _plotModel;
        private CandleStickSeries _candleSeries;

        private bool _autoScroll = true;                       // enable/disable auto scroll
        private TimeSpan _autoScrollWindow = TimeSpan.FromMinutes(60); // visible window size
        private double _autoPaddingMinutes = 1.0;              // small padding on right in minutes

        public MainWindow()
        {
            InitializeComponent();
            Config.Load(AppDomain.CurrentDomain.BaseDirectory);

            _plotModel = new PlotModel { Title = "NIFTY 5m" };

            _plotModel.Axes.Add(new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm",
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                Title = "Time"
            });

            _plotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Dot,
                Title = "Price"
            });

            _candleSeries = new CandleStickSeries
            {
                Title = "Candles",
                CandleWidth = 0.3, // smaller for better fit
                IncreasingColor = OxyColors.Green,
                DecreasingColor = OxyColors.Red
            };

            _plotModel.Series.Add(_candleSeries);
            PlotView.Model = _plotModel;

            LoadConfig();
            UpdateMenuState();

            _ = InstrumentCatalog.EnsureTodayAsync()
    .ContinueWith(t =>
    {
        Dispatcher.Invoke(() =>
        {
            if (t.Exception != null)
                AppendLog($"Instrument snapshot failed: {t.Exception.GetBaseException().Message}");
            else
                AppendLog($"Instrument snapshot OK for {DateTime.Today:yyyy-MM-dd} ({t.Result} rows).");
        });
    });

            CandleHistoryService.Initialize(_config.ApiKey, _config.AccessToken, _config.SqlConnectionString, _config.SubscribedInstruments)
                .ContinueWith(t =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (t.Exception != null)
                            AppendLog($"Candles History snapshot failed: {t.Exception.GetBaseException().Message}");
                        else
                            AppendLog($"Candles History snapshot OK for {DateTime.Today:yyyy-MM-dd}.");
                    });
                });

            // Example: enable file logging to a folder in user's AppData
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZerodhaOxySocket", "logs");
            SignalDiagnostics.StartFileLogging(logDir);

            // optionally show console too while debugging
            SignalDiagnostics.AlsoConsoleWrite = true;


            TickHub.OnStatus += s => Dispatcher.Invoke(() => txtStatus.Text = s);
            TickHub.OnLtp += (token, ltp, vol) => Dispatcher.Invoke(() =>
            {
                _tickCount++;
                _lastTickLocal = Clock.NowIst();
                txtTickStats.Text = $"Ticks: {_tickCount:n0} • Last: {_lastTickLocal:T} • Token: {token} • LTP: {ltp:F2}";
                UpdateChartTitle(token, ltp);
                AppendLog($"Tick {token}: LTP={ltp:F2} Vol={vol}");
            });

            TickHub.OnCandleClosed += (s, e) => Dispatcher.Invoke(() =>
            {
                AppendLog($"Candle {e.InstrumentName} O:{e.Candle.Open:F2} H:{e.Candle.High:F2} L:{e.Candle.Low:F2} C:{e.Candle.Close:F2} V:{e.Candle.Volume}");
                AddCandle(e.Candle);
            });
            TickHub.OnSignal += (s, e) => Dispatcher.Invoke(() =>
            {
                AppendLog($"SIGNAL {e.InstrumentName}: {e.Signal.Type} @ {e.Signal.Price:F2}");
            });


            Task.Run(async () =>
            {
                while (true)
                {
                    var now = DateTime.Now;
                    var eod = now.Date.AddHours(16).AddMinutes(0);
                    if (now >= eod && now < eod.AddMinutes(10)) // Run once during EOD window
                    {
                        await CandleHistoryService.FetchAllDailyTokensCandleHistoryAsync(DateTime.Today);
                        break; // or sleep 1 hour before checking again
                    }

                    await Task.Delay(TimeSpan.FromMinutes(5));
                }
            });

        }

        private void AppendLog(string line)
        {
            txtLog.AppendText($"{Clock.NowIst():HH:mm:ss}  {line}{Environment.NewLine}");
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
            TickPipeline.Start();
            TickHub.Connect();

            _ = InstrumentCatalog.EnsureTodayAsync()
    .ContinueWith(t =>
    {
        Dispatcher.Invoke(() =>
        {
            if (t.Exception != null)
                AppendLog($"Instrument snapshot failed: {t.Exception.GetBaseException().Message}");
            else
                AppendLog($"Instrument snapshot OK for {DateTime.Today:yyyy-MM-dd} ({t.Result} rows).");
        });
    });

            CandleHistoryService.Initialize(_config.ApiKey, _config.AccessToken, _config.SqlConnectionString, _config.SubscribedInstruments)
                .ContinueWith(t =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (t.Exception != null)
                            AppendLog($"Candles History snapshot failed: {t.Exception.GetBaseException().Message}");
                        else
                            AppendLog($"Candles History snapshot OK for {DateTime.Today:yyyy-MM-dd}.");
                    });
                });

                
                var tokens = SubscriptionHelper.GetTokensForAutoSubscribe(_config).ToList();

                if (tokens.Count > 0)
                    TickHub.SubscribeAuto(tokens);
            

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
                    MessageBox.Show($"Refresh failed: {ke.Message}\nException: {ke.InnerException}", "Kite Error");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Refresh failed: " + ex.Message);
                }
            }
        }

        private void OpenReplayWindow_Click(object sender, RoutedEventArgs e)
        {
           var w = new ReplayWindow();
    w.Owner = this;
    w.Show();
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

        public void AddCandle(Candle candle)
        {
            if (candle == null) return;

            // convert time to OxyPlot's double X value
            double x = DateTimeAxis.ToDouble(candle.Time);

            Dispatcher.Invoke(() =>
            {
                // Append candle to CandleStickSeries
                _candleSeries.Items.Add(new HighLowItem(
           x,
           candle.High,
           candle.Low,
           candle.Open,
           candle.Close
       ));


                // keep last N items if you want to limit points (optional)
                const int MAX_ITEMS = 1000;
                if (_candleSeries.Items.Count > MAX_ITEMS)
                    _candleSeries.Items.RemoveAt(0);

                // Auto-scroll/zoom to the latest window
                if (_autoScroll)
                    AutoScrollToLatest(_autoScrollWindow);

                // Redraw chart (false = don't recalc axes; true = recalc)
                _plotModel.InvalidatePlot(false);
            });
        }

        private void AutoScrollToLatest(TimeSpan window)
        {
            // find the DateTimeAxis on the model (bottom axis)
            var xAxis = _plotModel.Axes.OfType<DateTimeAxis>().FirstOrDefault();
            if (xAxis == null) return;

            // if series empty, nothing to do
            if (_candleSeries.Items == null || _candleSeries.Items.Count == 0) return;

            // get last candle X (double)
            var lastIndex = _candleSeries.Items.Count - 1;
            double lastX = _candleSeries.Items[lastIndex].X;

            // convert X back to DateTime and compute window boundaries
            var lastDt = DateTimeAxis.ToDateTime(lastX);

            // compute min/max to show
            var minDt = lastDt.Subtract(window);
            var maxDt = lastDt.AddMinutes(_autoPaddingMinutes); // right padding

            double minX = DateTimeAxis.ToDouble(minDt);
            double maxX = DateTimeAxis.ToDouble(maxDt);

            // If only a few candles exist, you may want to show whole range
            // Optionally ensure minX < maxX
            if (minX >= maxX)
            {
                // small fallback
                minX = lastX - (window.TotalDays * 1e-3);
                maxX = lastX + (window.TotalDays * 1e-3);
            }

            // Zoom the X axis (preserves Y axis scale)
            xAxis.Zoom(minX, maxX);
            AutoZoomYAxisToVisible();

            // You may also want to allow Y auto-zoom to price range of visible candles:
            // _plotModel.Axes.OfType<LinearAxis>().First().Reset()?  (not needed usually)
        }


        private void AutoZoomYAxisToVisible()
        {
            var xAxis = _plotModel.Axes.OfType<DateTimeAxis>().FirstOrDefault();
            var yAxis = _plotModel.Axes.OfType<LinearAxis>().FirstOrDefault();
            if (xAxis == null || yAxis == null) return;

            double minX = xAxis.ActualMinimum;
            double maxX = xAxis.ActualMaximum;
            if (double.IsNaN(minX) || double.IsNaN(maxX)) return;

            // find visible candles
            var visible = _candleSeries.Items.Where(it => it.X >= minX && it.X <= maxX).ToList();
            if (!visible.Any()) return;

            double low = visible.Min(it => it.Low);
            double high = visible.Max(it => it.High);

            // add small padding
            double pad = (high - low) * 0.1;
            yAxis.Zoom(low - pad, high + pad);
        }


        private void ChkAutoScroll_Checked(object sender, RoutedEventArgs e) => _autoScroll = true;
        private void ChkAutoScroll_Unchecked(object sender, RoutedEventArgs e) => _autoScroll = false;


        private void InstrumentsTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        private async void RefreshInstruments_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var listCount = await InstrumentCatalog.EnsureTodayAsync();
                AppendLog($"Instruments refreshed & snapshotted ({listCount} rows).");
                // (optional) if you cache instruments in-memory, refresh that cache here
            }
            catch (Exception ex)
            {
                AppendLog($"Refresh failed: {ex.Message}");
            }
        }

        private void OpenCandleHistoryWindow(object sender, RoutedEventArgs e)
        {
            var w = new CandleHistoryWindow { Owner = this };
            w.ShowDialog();
        }


    }
}
