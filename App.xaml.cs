using System.Windows; namespace ZerodhaOxySocket { public partial class App : Application {

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                SignalDiagnostics.StopFileLoggingAndFlush();
            }
            finally
            {
                base.OnExit(e);
            }
        }


    }
}