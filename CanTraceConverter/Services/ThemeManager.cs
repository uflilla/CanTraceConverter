using System;
using System.Linq;
using System.Windows;

namespace CanTraceConverter.Services
{
    public static class ThemeManager
    {
        private const string LightThemePath = "Themes/Light.xaml";
        private const string DarkThemePath = "Themes/Dark.xaml";

        public static bool IsDarkTheme { get; private set; }

        public static void Initialize()
        {
            ApplyTheme(Properties.Settings.Default.Theme);
        }

        public static void ApplyTheme(string themeName)
        {
            IsDarkTheme = !string.Equals(themeName, "light", StringComparison.OrdinalIgnoreCase);
            var themePath = IsDarkTheme ? DarkThemePath : LightThemePath;
            ReplaceThemeDictionary(themePath);
        }

        private static void ReplaceThemeDictionary(string themePath)
        {
            var root = Application.Current.Resources;
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
