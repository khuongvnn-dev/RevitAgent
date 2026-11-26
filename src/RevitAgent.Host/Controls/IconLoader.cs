namespace RevitAgent.Host.Controls
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Windows.Media.Imaging;

    /// <summary>
    /// Thread-safe atomic icon loader that loads PNG bitmaps into memory without locking files on disk.
    /// Supports dynamic user overrides in %LOCALAPPDATA%\RevitAgent\Icons\ and hot file replacements.
    /// </summary>
    public static class IconLoader
    {
        private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Clears the in-memory icon cache to force reload from disk.
        /// </summary>
        public static void ClearCache()
        {
            Cache.Clear();
        }

        /// <summary>
        /// Resolves an icon by its contract name (e.g. "launch_revit").
        /// Returns a frozen <see cref="BitmapSource"/> if found, or null to trigger vector/glyph fallback.
        /// </summary>
        public static BitmapSource? GetIcon(string iconName)
        {
            if (string.IsNullOrWhiteSpace(iconName)) return null;

            return Cache.GetOrAdd(iconName, name =>
            {
                string fileName = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.png";

                string[] candidatePaths =
                {
                    // 1. User customization directory
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAgent", "Icons", fileName),
                    // 2. Application Base Directory
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Icons", fileName),
                    // 3. Probing paths from project / current directory
                    Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Icons", fileName),
                    Path.Combine(Directory.GetCurrentDirectory(), "src", "RevitAgent.Host", "Resources", "Icons", fileName),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "RevitAgent.Host", "Resources", "Icons", fileName)
                };

                foreach (var path in candidatePaths)
                {
                    try
                    {
                        var fullPath = Path.GetFullPath(path);
                        if (File.Exists(fullPath))
                        {
                            var bitmap = LoadBitmapWithoutLock(fullPath);
                            if (bitmap != null)
                            {
                                return bitmap;
                            }
                        }
                    }
                    catch
                    {
                        // Continue probing next location
                    }
                }

                return null;
            });
        }

        /// <summary>
        /// Loads a BitmapSource from disk ensuring the underlying file is closed immediately
        /// and no OS file locks remain active.
        /// </summary>
        public static BitmapSource? LoadBitmapWithoutLock(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // Decode into RAM immediately
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze(); // Make cross-thread accessible and immutable
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}
