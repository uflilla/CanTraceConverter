using System;
using System.IO;
using NLog;

namespace CanTraceConverter.Services
{
    public static class AppLog
    {
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized)
                return;

            var logsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            Directory.CreateDirectory(logsDir);

            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NLog.config");
            if (File.Exists(configPath))
                LogManager.Setup().LoadConfigurationFromFile(configPath);

            _initialized = true;
            Info("Logging initialized. Log directory: {0}", logsDir);
        }

        public static void Debug(string message) =>
            LogManager.GetCurrentClassLogger().Debug(message);

        public static void Info(string message) =>
            LogManager.GetCurrentClassLogger().Info(message);

        public static void Info(string message, params object[] args) =>
            LogManager.GetCurrentClassLogger().Info(message, args);

        public static void Warn(string message) =>
            LogManager.GetCurrentClassLogger().Warn(message);

        public static void Warn(string message, params object[] args) =>
            LogManager.GetCurrentClassLogger().Warn(message, args);

        public static void Warn(Exception ex, string message) =>
            LogManager.GetCurrentClassLogger().Warn(ex, message);

        public static void Error(string message) =>
            LogManager.GetCurrentClassLogger().Error(message);

        public static void Error(string message, params object[] args) =>
            LogManager.GetCurrentClassLogger().Error(message, args);

        public static void Error(Exception ex, string message) =>
            LogManager.GetCurrentClassLogger().Error(ex, message);

        public static void Error(Exception ex, string message, params object[] args) =>
            LogManager.GetCurrentClassLogger().Error(ex, message, args);

        public static void Error(Exception ex) =>
            LogManager.GetCurrentClassLogger().Error(ex);

        public static void Shutdown()
        {
            if (!_initialized)
                return;

            Info("Application logging shutting down.");
            LogManager.Shutdown();
            _initialized = false;
        }
    }
}
