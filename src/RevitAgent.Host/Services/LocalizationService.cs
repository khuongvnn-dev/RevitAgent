namespace RevitAgent.Host.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Windows;
    using System.Windows.Markup;

    /// <summary>
    /// Thread-safe runtime dynamic localization service.
    /// Replaces merged ResourceDictionary in Application.Current on-the-fly.
    /// </summary>
    public sealed class LocalizationService : ILocalizationService
    {
        public const string CultureVietnamese = "vi-VN";
        public const string CultureEnglish = "en-US";

        public static readonly IReadOnlyList<string> SupportedLanguagesList = new[]
        {
            CultureVietnamese,
            CultureEnglish
        };

        private readonly object _syncLock = new();
        private string _currentLanguage = CultureVietnamese;
        private ResourceDictionary? _currentDictionary;

        public string CurrentLanguage => _currentLanguage;
        public IReadOnlyList<string> SupportedLanguages => SupportedLanguagesList;

        public event Action<string>? LanguageChanged;

        public LocalizationService()
        {
            // Initial load with default language
            SetLanguage(CultureVietnamese);
        }

        public void SetLanguage(string? cultureCode)
        {
            lock (_syncLock)
            {
                string targetCulture = NormalizeCulture(cultureCode);

                // Update Thread & Process Culture
                try
                {
                    var cultureInfo = new CultureInfo(targetCulture);
                    Thread.CurrentThread.CurrentCulture = cultureInfo;
                    Thread.CurrentThread.CurrentUICulture = cultureInfo;
                    CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
                    CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
                }
                catch
                {
                    // Fallback silently if culture creation is restricted
                }

                // Load new ResourceDictionary
                var newDict = LoadDictionary(targetCulture);
                if (newDict != null)
                {
                    _currentDictionary = newDict;
                }

                // Swap in Application.Current.Resources if available
                if (Application.Current != null && newDict != null)
                {
                    SwapApplicationDictionary(newDict);
                }

                _currentLanguage = targetCulture;
                LanguageChanged?.Invoke(targetCulture);
            }
        }

        public string GetString(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;

            // 1. Try finding in Application.Current resources first
            if (Application.Current != null)
            {
                try
                {
                    if (Application.Current.TryFindResource(key) is string appStr)
                    {
                        return appStr;
                    }
                }
                catch
                {
                    // Ignore and fall back
                }
            }

            // 2. Query cached internal dictionary
            if (_currentDictionary != null && _currentDictionary.Contains(key))
            {
                if (_currentDictionary[key] is string dictStr)
                {
                    return dictStr;
                }
            }

            return key;
        }

        public ResourceDictionary? GetCurrentDictionary()
        {
            lock (_syncLock)
            {
                return _currentDictionary;
            }
        }

        private static string NormalizeCulture(string? cultureCode)
        {
            if (string.IsNullOrWhiteSpace(cultureCode))
                return CultureVietnamese;

            var trimmed = cultureCode.Trim();
            if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return CultureEnglish;

            return CultureVietnamese;
        }

        private void SwapApplicationDictionary(ResourceDictionary newDict)
        {
            var appResources = Application.Current.Resources;
            var merged = appResources.MergedDictionaries;

            ResourceDictionary? existing = null;
            for (int i = 0; i < merged.Count; i++)
            {
                var d = merged[i];
                if (d.Source != null && d.Source.OriginalString.Contains("Strings.", StringComparison.OrdinalIgnoreCase))
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

        public static ResourceDictionary? LoadDictionary(string culture)
        {
            // Method A: Pack URI (WPF App runtime)
            try
            {
                var packUri = new Uri($"/RevitAgent.Host;component/Resources/Localization/Strings.{culture}.xaml", UriKind.RelativeOrAbsolute);
                var dict = new ResourceDictionary { Source = packUri };
                if (dict.Keys.Count > 0)
                {
                    return dict;
                }
            }
            catch
            {
                // Pack URI may fail in unit test runners without an active Application
            }

            // Method B: Direct File Loading via XamlReader
            string[] probePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Localization", $"Strings.{culture}.xaml"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "RevitAgent.Host", "Resources", "Localization", $"Strings.{culture}.xaml"),
                Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Localization", $"Strings.{culture}.xaml"),
                Path.Combine(Directory.GetCurrentDirectory(), "src", "RevitAgent.Host", "Resources", "Localization", $"Strings.{culture}.xaml"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"Strings.{culture}.xaml")
            };

            foreach (var probe in probePaths)
            {
                try
                {
                    string fullPath = Path.GetFullPath(probe);
                    if (File.Exists(fullPath))
                    {
                        using var stream = File.OpenRead(fullPath);
                        if (XamlReader.Load(stream) is ResourceDictionary xamlDict)
                        {
                            return xamlDict;
                        }
                    }
                }
                catch
                {
                    // Continue to next probe path
                }
            }

            return null;
        }
    }
}
