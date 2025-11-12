using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ZerodhaOxySocket
{
    public partial class CandleHistoryWindow : Window
    {
        private CancellationTokenSource _cts;
        public CandleHistoryWindow()
        {
            InitializeComponent();

            // initialize defaults
            DpStartDate.SelectedDate = DateTime.Now.AddDays(-7).Date;
            DpEndDate.SelectedDate = DateTime.Now.Date;
            TxtInstrumentToken.Text = Config.Current.Trading.UnderlyingToken.ToString();
            CmbTf.SelectedIndex = 0; // 1 minute default
        }

        private void Log(string text)
        {
            Dispatcher.Invoke(() =>
            {
                TxtLog.AppendText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {text}\r\n");
                TxtLog.ScrollToEnd();
            });
        }

        private void SetStatus(string status)
        {
            Dispatcher.Invoke(() => TxtStatus.Text = status);
        }

        private void SetProgress(double percent)
        {
            Dispatcher.Invoke(() =>
            {
                PgBar.Value = Math.Max(0, Math.Min(100, percent));
            });
        }

        private async void BtnFetch_Click(object sender, RoutedEventArgs e)
        {
            // parse inputs
            if (!DateTime.TryParse($"{DpStartDate.SelectedDate:yyyy-MM-dd} {TxtStartTime.Text}", out var from))
            {
                MessageBox.Show("Invalid start date/time", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!DateTime.TryParse($"{DpEndDate.SelectedDate:yyyy-MM-dd} {TxtEndTime.Text}", out var to))
            {
                MessageBox.Show("Invalid end date/time", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (to <= from)
            {
                MessageBox.Show("End must be after start", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!uint.TryParse(TxtInstrumentToken.Text, out var token))
            {
                MessageBox.Show("Invalid instrument token", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var tfMinutes = (CmbTf.SelectedItem as System.Windows.Controls.ComboBoxItem).Content.ToString();

            BtnFetch.IsEnabled = false;
            BtnCancel.IsEnabled = true;
            _cts = new CancellationTokenSource();
            TxtLog.Clear();
            SetProgress(0);
            SetStatus("Starting...");

            var progress = new Progress<CandleHistoryService.ProgressReport>(report =>
            {
                SetProgress(report.Percent);
                SetStatus($"Processed: {report.Processed} / {report.Total} (batch {report.CurrentBatch}/{report.TotalBatches})");
                if (!string.IsNullOrWhiteSpace(report.Message)) Log(report.Message);
            });

            try
            {
                Log($"Starting fetch: token={token} from={from:O} to={to:O} tf={tfMinutes}m");
                // call the CandleHistoryService method you add below
                await CandleHistoryService.FetchAndUpsertRangeAsync(token, from, to, tfMinutes, progress, _cts.Token);
                SetProgress(100);
                SetStatus("Completed");
                Log("Fetch completed successfully.");
            }
            catch (OperationCanceledException)
            {
                SetStatus("Cancelled");
                Log("Operation cancelled by user.");
            }
            catch (Exception ex)
            {
                SetStatus("Error");
                Log($"Error: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                BtnFetch.IsEnabled = true;
                BtnCancel.IsEnabled = false;
                _cts = null;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            BtnCancel.IsEnabled = false;
            _cts?.Cancel();
            Log("Cancel requested...");
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
