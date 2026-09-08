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
        private static int _errorDialogOpen;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Handlers first: anything below can fail, and without them a startup fault
            // terminates the process with no message and no log.
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            SessionEnding += OnSessionEnding;

            AppLog.Initialize();
            AppLog.Info("USB-CAN Studio starting.");

            try
            {
                ThemeManager.Initialize();
            }
            catch (Exception ex)
            {
                // A corrupt user.config must not stop the app from opening.
                AppLog.Warn(ex, "Theme initialization failed; continuing with the default theme.");
            }

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
            // Best-effort only: the CLR time-limits ProcessExit handlers, so this cannot be
            // relied on to fully release the hardware. OnExit and per-window teardown do the
            // real work; this just covers abnormal termination.
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
            // Runs on the finalizer thread — logging only, never a modal dialog from here.
            AppLog.Error(e.Exception, "Unobserved background task exception.");
            e.SetObserved();
        }

        /// <summary>
        /// Reports a fault without blocking the thread that raised it. A modal dialog on a
        /// driver callback or finalizer thread would stall that thread indefinitely.
        /// </summary>
        private static void ShowFatalError(string title, Exception ex)
        {
            var message = ex?.GetBaseException()?.Message ?? "Unknown error";

            // One dialog at a time: a fault storm must not produce a wall of message boxes.
            if (Interlocked.Exchange(ref _errorDialogOpen, 1) != 0)
                return;

            var dispatcher = Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                Interlocked.Exchange(ref _errorDialogOpen, 0);
                return;
            }

            try
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        MessageBox.Show(
                            message + Environment.NewLine + Environment.NewLine +
                            "The application will continue when possible.",
                            title,
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                    catch { /* ignore */ }
                    finally
                    {
                        Interlocked.Exchange(ref _errorDialogOpen, 0);
                    }
                }));
            }
            catch
            {
                Interlocked.Exchange(ref _errorDialogOpen, 0);
            }
        }
    }
}
