using System;
using System.Linq;
using System.Windows;

namespace UcanView.Services
{
    public static class ThemeManager
    {
        public const string LightTheme = "Light";
        public const string DarkTheme = "Dark";

        private const string LightThemePath = "Themes/Light.xaml";
        private const string DarkThemePath = "Themes/Dark.xaml";

        public static bool IsDarkTheme { get; private set; } = true;

        public static string CurrentTheme => IsDarkTheme ? DarkTheme : LightTheme;

        /// <summary>Raised after the theme changes, so open windows can update any label.</summary>
        public static event Action<string> ThemeChanged;

        public static void Initialize()
        {
            ApplyTheme(ReadSavedTheme());
        }

        /// <summary>Switches theme and remembers the choice for the next launch.</summary>
        public static void ToggleTheme()
        {
            SetTheme(IsDarkTheme ? LightTheme : DarkTheme);
        }

        public static void SetTheme(string themeName)
        {
            ApplyTheme(themeName);

            try
            {
                Properties.Settings.Default.Theme = CurrentTheme;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Could not save the theme preference.");
            }

            try { ThemeChanged?.Invoke(CurrentTheme); } catch { /* ignore subscriber errors */ }
        }

        public static void ApplyTheme(string themeName)
        {
            // An unrecognised value falls back to the named default rather than silently
            // meaning "dark".
            IsDarkTheme = !string.Equals(themeName, LightTheme, StringComparison.OrdinalIgnoreCase);

            var themePath = IsDarkTheme ? DarkThemePath : LightThemePath;
            ReplaceThemeDictionary(themePath);
        }

        private static string ReadSavedTheme()
        {
            try
            {
                var saved = Properties.Settings.Default.Theme;
                return string.IsNullOrWhiteSpace(saved) ? DarkTheme : saved;
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Could not read the saved theme; using the default.");
                return DarkTheme;
            }
        }

        private static void ReplaceThemeDictionary(string themePath)
        {
            var root = Application.Current?.Resources;
            if (root is not ResourceDictionary rootDictionary)
                return;

            var newTheme = new ResourceDictionary
            {
                Source = new Uri(themePath, UriKind.Relative)
            };

            var existing = rootDictionary.MergedDictionaries
                .FirstOrDefault(d =>
                {
                    var source = d.Source?.OriginalString ?? string.Empty;
                    return source.Contains("Themes/Light.xaml") || source.Contains("Themes/Dark.xaml");
                });

            if (existing != null)
            {
                var index = rootDictionary.MergedDictionaries.IndexOf(existing);
                rootDictionary.MergedDictionaries[index] = newTheme;
            }
            else
            {
                rootDictionary.MergedDictionaries.Insert(0, newTheme);
            }
        }
    }
}
