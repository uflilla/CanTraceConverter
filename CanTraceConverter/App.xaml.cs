using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CanTraceConverter.Services;

namespace CanTraceConverter
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppLog.Initialize();

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            AppLog.Info("USB-CAN Studio starting.");
            ThemeManager.Initialize();

            base.OnStartup(e);

            try
            {
                var launcher = new LauncherWindow();
                MainWindow = launcher;
                launcher.Show();
                AppLog.Info("Launcher window opened.");
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Startup failed.");
                ShowFatalError("Startup failed", ex);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLog.Info("Application exiting with code {0}.", e.ApplicationExitCode);
            AppLog.Shutdown();
            base.OnExit(e);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLog.Error(ex, "Unhandled AppDomain exception. Terminating={0}", e.IsTerminating);
                ShowFatalError("Unexpected error", ex);
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLog.Error(e.Exception, "Unhandled UI thread exception.");
            ShowFatalError("Unexpected error", e.Exception);
            e.Handled = true;
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLog.Error(e.Exception, "Unobserved background task exception.");
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
