namespace RevitAgent.Host.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Windows;
    using System.Windows.Markup;
    using Microsoft.Win32;

    /// <summary>
    /// Thread-safe runtime dynamic theme service supporting Dark, Light, and Windows System theme detection.
    /// Replaces merged ResourceDictionary in Application.Current on-the-fly.
    /// </summary>
    public sealed class ThemeService : IThemeService, IDisposable
    {
        public const string ModeSystem = "System";
        public const string ModeDark = "Dark";
        public const string ModeLight = "Light";

        public const string ThemeDark = "Dark";
        public const string ThemeLight = "Light";

        public static readonly IReadOnlyList<string> SupportedThemeModesList = new[]
        {
            ModeSystem,
            ModeDark,
            ModeLight
        };

        private readonly object _syncLock = new();
        private string _themeMode = ModeSystem;
        private string _activeTheme = ThemeDark;
        private ResourceDictionary? _currentDictionary;
        private bool _disposed;

        public string ThemeMode => _themeMode;
        public string ActiveTheme => _activeTheme;
        public IReadOnlyList<string> SupportedThemeModes => SupportedThemeModesList;

        public event Action<string>? ThemeChanged;

        public ThemeService()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                }
                catch
                {
                    // Ignore if system events registration fails in sandboxed environments
                }
            }

            // Default initialization
            SetThemeMode(ModeSystem);
        }

        public void SetThemeMode(string? mode)
        {
            lock (_syncLock)
            {
                _themeMode = NormalizeThemeMode(mode);
                string targetTheme = ResolveTargetTheme(_themeMode);
                ApplyTheme(targetTheme);
            }
        }

        public string ToggleNextThemeMode()
        {
            lock (_syncLock)
            {
                string nextMode = _themeMode switch
                {
                    ModeSystem => ModeDark,
                    ModeDark => ModeLight,
                    ModeLight => ModeSystem,
                    _ => ModeSystem
                };

                SetThemeMode(nextMode);
                return nextMode;
            }
        }

        public string ToggleBinaryTheme()
        {
            lock (_syncLock)
            {
                // If active theme is Dark, toggle to Light; otherwise toggle to Dark
                string nextMode = string.Equals(_activeTheme, ThemeDark, StringComparison.OrdinalIgnoreCase)
                    ? ModeLight
                    : ModeDark;

                SetThemeMode(nextMode);
                return nextMode;
            }
        }

        private void ApplyTheme(string targetTheme)
        {
            var newDict = LoadThemeDictionary(targetTheme);
            if (newDict != null)
            {
                _currentDictionary = newDict;
            }

            if (Application.Current != null && newDict != null)
            {
                void Swap()
                {
                    SwapApplicationDictionary(newDict);
                }

                if (Application.Current.Dispatcher.CheckAccess())
                {
                    Swap();
                }
                else
                {
                    Application.Current.Dispatcher.Invoke(Swap);
                }
            }

            _activeTheme = targetTheme;
            ThemeChanged?.Invoke(_activeTheme);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
            {
                lock (_syncLock)
                {
                    if (string.Equals(_themeMode, ModeSystem, StringComparison.OrdinalIgnoreCase))
                    {
                        string targetTheme = ResolveTargetTheme(ModeSystem);
                        if (!string.Equals(targetTheme, _activeTheme, StringComparison.OrdinalIgnoreCase))
                        {
                            if (Application.Current != null)
                            {
                                Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    lock (_syncLock)
                                    {
                                        if (string.Equals(_themeMode, ModeSystem, StringComparison.OrdinalIgnoreCase))
                                        {
                                            ApplyTheme(targetTheme);
                                        }
                                    }
                                });
                            }
                            else
                            {
                                ApplyTheme(targetTheme);
                            }
                        }
                    }
                }
            }
        }

        public static string ResolveTargetTheme(string mode)
        {
            if (string.Equals(mode, ModeLight, StringComparison.OrdinalIgnoreCase))
            {
                return ThemeLight;
            }

            if (string.Equals(mode, ModeDark, StringComparison.OrdinalIgnoreCase))
            {
                return ThemeDark;
            }

            // ModeSystem: Detect from Windows Registry
            return GetIsSystemInLightTheme() ? ThemeLight : ThemeDark;
        }

        public static bool GetIsSystemInLightTheme()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    writable: false);

                if (key?.GetValue("AppsUseLightTheme") is int val)
                {
                    return val != 0;
                }
            }
            catch
            {
                // Safe fallback on any permission or environment restrictions
            }

            return false; // Default to Dark mode for RevitAgent Host
        }

        public static string NormalizeThemeMode(string? mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
                return ModeSystem;

            var trimmed = mode.Trim();
            if (trimmed.Equals(ModeLight, StringComparison.OrdinalIgnoreCase))
                return ModeLight;
            if (trimmed.Equals(ModeDark, StringComparison.OrdinalIgnoreCase))
                return ModeDark;

            return ModeSystem;
        }

        public ResourceDictionary? GetCurrentDictionary()
        {
            lock (_syncLock)
            {
                return _currentDictionary;
            }
        }

        private static void SwapApplicationDictionary(ResourceDictionary newDict)
        {
            var appResources = Application.Current.Resources;
            var merged = appResources.MergedDictionaries;

            ResourceDictionary? existing = null;
            for (int i = 0; i < merged.Count; i++)
            {
                var d = merged[i];
                if (d.Source != null && (d.Source.OriginalString.Contains("Theme.xaml", StringComparison.OrdinalIgnoreCase) ||
                                         d.Source.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase)))
                {
                    existing = d;
                    break;
                }
            }

            if (existing != null)
            {
                int index = merged.IndexOf(existing);
                merged[index] = newDict;
            }
            else
            {
                merged.Add(newDict);
            }
        }

        public static ResourceDictionary? LoadThemeDictionary(string theme)
        {
            string fileName = string.Equals(theme, ThemeLight, StringComparison.OrdinalIgnoreCase)
                ? "LightTheme.xaml"
                : "DarkTheme.xaml";

            // 1. Try pack URI
            try
            {
                var packUri = new Uri($"pack://application:,,,/RevitAgent.Host;component/Resources/Themes/{fileName}", UriKind.Absolute);
                return new ResourceDictionary { Source = packUri };
            }
            catch
            {
                // Fall back to disk probe
            }

            // 2. Try disk probe paths
            string[] probePaths =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Themes", fileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "RevitAgent.Host", "Resources", "Themes", fileName),
                Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Themes", fileName),
                Path.Combine(Directory.GetCurrentDirectory(), "src", "RevitAgent.Host", "Resources", "Themes", fileName)
            };

            foreach (var probe in probePaths)
            {
                try
                {
                    var full = Path.GetFullPath(probe);
                    if (File.Exists(full))
                    {
                        using var stream = File.OpenRead(full);
                        if (XamlReader.Load(stream) is ResourceDictionary dict)
                        {
                            dict.Source = new Uri(full, UriKind.Absolute);
                            return dict;
                        }
                    }
                }
                catch
                {
                    // Continue probing
                }
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                }
                catch
                {
                    // Ignore
                }
            }
        }
    }
}
