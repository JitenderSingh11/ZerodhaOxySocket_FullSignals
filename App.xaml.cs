using System.Threading.Tasks;
using System.Windows;

namespace ZerodhaOxySocket
{
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                SignalDiagnostics.StopFileLoggingAndFlush();
                TickPipeline.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }
}