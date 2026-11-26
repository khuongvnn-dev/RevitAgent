namespace RevitAgent.Host.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Service for managing runtime dynamic localization and bilingual dictionaries.
    /// </summary>
    public interface ILocalizationService
    {
        /// <summary>
        /// Current active language culture code (e.g. "vi-VN" or "en-US").
        /// </summary>
        string CurrentLanguage { get; }

        /// <summary>
        /// List of supported language culture codes.
        /// </summary>
        IReadOnlyList<string> SupportedLanguages { get; }

        /// <summary>
        /// Event fired when the active language changes.
        /// </summary>
        event Action<string>? LanguageChanged;

        /// <summary>
        /// Dynamically swaps the active ResourceDictionary in Application.Current.Resources.MergedDictionaries
        /// and updates thread culture at runtime without restarting the application.
        /// </summary>
        /// <param name="cultureCode">Culture code ("vi-VN" or "en-US").</param>
        void SetLanguage(string cultureCode);

        /// <summary>
        /// Retrieves a localized string resource by key from the active dictionary.
        /// </summary>
        /// <param name="key">Resource key (e.g. "Str_WindowTitle").</param>
        /// <returns>Localized string or the key if not found.</returns>
        string GetString(string key);
    }
}
