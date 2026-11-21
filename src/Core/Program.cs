using System;
using System.Threading;
using System.Windows.Forms;

namespace WinWipeHelper
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // "--worker job.json sha256": run one job (elevated or as the normal user) with no window.
            if (args.Length >= 2 && args[0].Equals("--worker", StringComparison.OrdinalIgnoreCase))
                return Worker.RunFromFile(args[1], args.Length >= 3 ? args[2] : null);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject as Exception);

            Modules.RegisterWorkers();
            Application.Run(new MainForm());
            return 0;
        }

        static int _crashing;
        static void ShowCrash(Exception ex)
        {
            if (Interlocked.Exchange(ref _crashing, 1) == 1) return;
            try
            {
                using (var log = Log.Start("Error")) log.Error(ex?.ToString() ?? "unknown error");
                MessageBox.Show("Something went wrong:\n\n" + ex?.Message + "\n\nDetails were saved in the logs folder.",
                    Edition.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
            finally { Interlocked.Exchange(ref _crashing, 0); }
        }
    }
}
