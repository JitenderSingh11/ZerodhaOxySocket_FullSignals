using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace ZerodhaOxySocket
{
    public static class InstrumentDownloader
    {
        private static readonly string Url = "https://api.kite.trade/instruments";
        public static async Task EnsureInstrumentsCsvAsync()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "instruments.csv");
                if (File.Exists(path) && new FileInfo(path).LastWriteTime.Date == DateTime.UtcNow.Date) return;
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(Url);
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex) { Console.WriteLine("Instrument download failed: " + ex.Message); }
        }
    }
}
