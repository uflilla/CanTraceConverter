using System;
using System.IO;
using NLog;

namespace CanTraceConverter.Services
{
    public static class AppLog
    {
        private static readonly Logger Log = LogManager.GetLogger("CanTraceConverter");
        private static bool _initialized;

        public static string LogDirectory { get; private set; }

        /// <summary>
        /// Sets up file logging. Never throws: logging must not be able to stop the
        /// application from starting.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;

            try
            {
                // Per-user, always writable — the install directory usually is not.
                LogDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "USB-CAN Studio",
                    "Logs");
                Directory.CreateDirectory(LogDirectory);

                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NLog.config");
                if (File.Exists(configPath))
                    LogManager.Setup().LoadConfigurationFromFile(configPath);

                // Keep the file target pointed at the folder we just proved is writable.
                if (LogManager.Configuration != null)
                {
                    LogManager.Configuration.Variables["logDirectory"] = LogDirectory;
                    LogManager.ReconfigExistingLoggers();
                }

                Info("Logging initialized. Log directory: {0}", LogDirectory);
            }
            catch (Exception ex)
            {
                // Nothing to log to; carry on without a log file.
                System.Diagnostics.Debug.WriteLine("Logging initialization failed: " + ex);
                LogDirectory = null;
            }
        }

        /// <summary>Logger for a specific component, so log lines identify their source.</summary>
        public static Logger For(string name) => LogManager.GetLogger(name);

        public static void Debug(string message) => Log.Debug(message);

        public static void Info(string message) => Log.Info(message);

        public static void Info(string message, params object[] args) => Log.Info(message, args);

        public static void Warn(string message) => Log.Warn(message);

        public static void Warn(string message, params object[] args) => Log.Warn(message, args);

        public static void Warn(Exception ex, string message) => Log.Warn(ex, message);

        public static void Warn(Exception ex, string message, params object[] args) => Log.Warn(ex, message, args);

        public static void Error(string message) => Log.Error(message);

        public static void Error(string message, params object[] args) => Log.Error(message, args);

        public static void Error(Exception ex, string message) => Log.Error(ex, message);

        public static void Error(Exception ex, string message, params object[] args) => Log.Error(ex, message, args);

        public static void Error(Exception ex) => Log.Error(ex);

        public static void Shutdown()
        {
            if (!_initialized)
                return;

            try
            {
                Info("Application logging shutting down.");
                LogManager.Shutdown();
            }
            catch { /* logging may already be down */ }
            finally
            {
                _initialized = false;
            }
        }
    }
}
