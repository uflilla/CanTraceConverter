using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CanTraceConverter.Services;

namespace CanTraceConverter
{
    public partial class App : Application
    {
        private static int _shutdownCleanupDone;

        protected override void OnStartup(StartupEventArgs e)
        {
            AppLog.Initialize();

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            SessionEnding += OnSessionEnding;

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
            ForceReleaseCanResources("OnExit");
            AppLog.Info("Application exiting with code {0}.", e.ApplicationExitCode);
            AppLog.Shutdown();
            base.OnExit(e);
        }

        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            ForceReleaseCanResources("SessionEnding");
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            // Last-chance cleanup if the process is terminating.
            ForceReleaseCanResources("ProcessExit");
        }

        /// <summary>
        /// Stops all cyclic TX and releases USB-CAN hardware so other apps can use the device.
        /// Safe to call multiple times.
        /// </summary>
        internal static void ForceReleaseCanResources(string reason)
        {
            if (Interlocked.Exchange(ref _shutdownCleanupDone, 1) != 0)
                return;

            try
            {
                AppLog.Info("Releasing CAN resources ({0}).", reason);
                UsbCanHardwareManager.ReleaseAll();
            }
            catch (Exception ex)
            {
                try { AppLog.Warn(ex, "CAN resource release failed."); } catch { /* logging may be down */ }
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLog.Error(ex, "Unhandled AppDomain exception. Terminating={0}", e.IsTerminating);
                if (e.IsTerminating)
                    ForceReleaseCanResources("UnhandledException");
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
