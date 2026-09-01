using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CloudFolderBrowser
{
    static class Program
    {
        /// <summary>
        /// Главная точка входа для приложения.
        /// </summary>
        [STAThread]
        static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += MyHandler;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, args) => ReportUnhandledException(args.Exception);

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        static void MyHandler(object sender, UnhandledExceptionEventArgs args)
        {
            Exception exception = args.ExceptionObject as Exception
                ?? new InvalidOperationException("The application terminated because of an unknown unmanaged error.");
            ReportUnhandledException(exception);
        }

        private static void ReportUnhandledException(Exception exception)
        {
            string message = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;
            try
            {
                string logPath = Path.Combine(Utility.GetApplicationDataDirectory(), "crash.log");
                File.AppendAllText(
                    logPath,
                    $"{DateTime.UtcNow:O} {exception.GetType().FullName}: {message}{Environment.NewLine}");
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to write crash log: {logError.Message}");
            }

            MessageBox.Show(
                "Cloud Folder Browser encountered an unexpected error:\n\n" + message,
                "Unexpected error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
