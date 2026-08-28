using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace CanTraceConverter
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            base.OnStartup(e);

            try
            {
                var launcher = new LauncherWindow();
                MainWindow = launcher;
                launcher.Show();
            }
            catch (Exception ex)
            {
                ShowFatalError("Startup failed", ex);
                Shutdown(1);
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                ShowFatalError("Unexpected error", ex);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ShowFatalError("Unexpected error", e.Exception);
            e.Handled = true;
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            ShowFatalError("Background task error", e.Exception);
        }

        private static void ShowFatalError(string title, Exception ex)
        {
            var message = ex?.GetBaseException()?.Message ?? "Unknown error";
            MessageBox.Show(
                message + Environment.NewLine + Environment.NewLine +
                "The application will continue when possible.",
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
