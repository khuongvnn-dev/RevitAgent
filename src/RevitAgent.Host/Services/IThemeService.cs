namespace RevitAgent.Host.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Service for managing runtime dynamic application themes (Dark, Light, System OS).
    /// </summary>
    public interface IThemeService
    {
        /// <summary>
        /// Current configured theme mode ("System", "Dark", "Light").
        /// </summary>
        string ThemeMode { get; }

        /// <summary>
        /// Active visual theme resolved on screen ("Dark" or "Light").
        /// </summary>
        string ActiveTheme { get; }

        /// <summary>
        /// List of supported theme modes.
        /// </summary>
        IReadOnlyList<string> SupportedThemeModes { get; }

        /// <summary>
        /// Event fired when the active theme changes ("Dark" or "Light").
        /// </summary>
        event Action<string>? ThemeChanged;

        /// <summary>
        /// Sets the theme mode ("System", "Dark", or "Light").
        /// If "System", detects Windows OS theme and dynamically reacts to changes.
        /// </summary>
        void SetThemeMode(string? mode);

        /// <summary>
        /// Cycles to the next theme mode (System -> Dark -> Light -> System).
        /// </summary>
        /// <returns>The newly selected theme mode.</returns>
        string ToggleNextThemeMode();

        /// <summary>
        /// Toggles binary theme between Dark and Light immediately (1-Click Toggle).
        /// If currently in System mode, resolves to the opposite of current active theme.
        /// </summary>
        /// <returns>The newly selected theme mode ("Dark" or "Light").</returns>
        string ToggleBinaryTheme();
    }
}
