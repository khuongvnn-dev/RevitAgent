namespace RevitAgent.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Windows.Media.Imaging;
    using System.Xml.Linq;
    using RevitAgent.Host.Config;
    using RevitAgent.Host.Controls;
    using RevitAgent.Host.Services;
    using Xunit;

    public class ThemeAndIconTests
    {
        private static readonly string[] PossibleDarkThemePaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Themes", "DarkTheme.xaml"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "RevitAgent.Host", "Resources", "Themes", "DarkTheme.xaml"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "RevitAgent.Host", "Resources", "Themes", "DarkTheme.xaml"),
            Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Themes", "DarkTheme.xaml"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "RevitAgent.Host", "Resources", "Themes", "DarkTheme.xaml")
        };

        private static readonly string[] PossibleLightThemePaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Themes", "LightTheme.xaml"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "RevitAgent.Host", "Resources", "Themes", "LightTheme.xaml"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "RevitAgent.Host", "Resources", "Themes", "LightTheme.xaml"),
            Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Themes", "LightTheme.xaml"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "RevitAgent.Host", "Resources", "Themes", "LightTheme.xaml")
        };

        private static string ResolvePath(string[] candidates)
        {
            foreach (var path in candidates)
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            throw new FileNotFoundException($"Cannot locate theme file in candidates: {string.Join(", ", candidates)}");
        }

        private static Dictionary<string, string> ParseDictionaryKeys(string filePath)
        {
            var doc = XDocument.Load(filePath);
            var xNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var element in doc.Root!.Elements())
            {
                var keyAttr = element.Attribute(xNamespace + "Key") ?? element.Attribute("Key");
                if (keyAttr != null)
                {
                    var colorAttr = element.Attribute("Color");
                    result[keyAttr.Value] = colorAttr?.Value ?? element.Value;
                }
            }

            return result;
        }

        [Fact]
        public void ThemeService_InitializesWithSystemMode_AndResolvesActiveTheme()
        {
            // Arrange & Act
            using var service = new ThemeService();

            // Assert
            Assert.Equal(ThemeService.ModeSystem, service.ThemeMode);
            Assert.True(service.ActiveTheme == ThemeService.ThemeDark || service.ActiveTheme == ThemeService.ThemeLight);
            Assert.Equal(3, service.SupportedThemeModes.Count);
        }

        [Fact]
        public void ThemeService_SetThemeMode_DarkAndLight_UpdatesActiveThemeAndFiresEvent()
        {
            // Arrange
            using var service = new ThemeService();
            string? capturedTheme = null;
            service.ThemeChanged += t => capturedTheme = t;

            // Act - Set Dark
            service.SetThemeMode(ThemeService.ModeDark);
            Assert.Equal(ThemeService.ModeDark, service.ThemeMode);
            Assert.Equal(ThemeService.ThemeDark, service.ActiveTheme);
            Assert.Equal(ThemeService.ThemeDark, capturedTheme);

            // Act - Set Light
            service.SetThemeMode(ThemeService.ModeLight);
            Assert.Equal(ThemeService.ModeLight, service.ThemeMode);
            Assert.Equal(ThemeService.ThemeLight, service.ActiveTheme);
            Assert.Equal(ThemeService.ThemeLight, capturedTheme);
        }

        [Fact]
        public void ThemeService_ToggleNextThemeMode_CyclesThroughAllModes()
        {
            // Arrange
            using var service = new ThemeService();
            service.SetThemeMode(ThemeService.ModeSystem);

            // Act & Assert Cycle
            Assert.Equal(ThemeService.ModeDark, service.ToggleNextThemeMode());
            Assert.Equal(ThemeService.ModeLight, service.ToggleNextThemeMode());
            Assert.Equal(ThemeService.ModeSystem, service.ToggleNextThemeMode());
        }

        [Fact]
        public void ThemeService_ToggleBinaryTheme_TogglesDirectlyBetweenDarkAndLight()
        {
            // Arrange
            using var service = new ThemeService();
            service.SetThemeMode(ThemeService.ModeDark);
            Assert.Equal(ThemeService.ThemeDark, service.ActiveTheme);

            // Act 1: Toggle from Dark -> should become Light
            string next1 = service.ToggleBinaryTheme();
            Assert.Equal(ThemeService.ModeLight, next1);
            Assert.Equal(ThemeService.ThemeLight, service.ActiveTheme);

            // Act 2: Toggle from Light -> should become Dark
            string next2 = service.ToggleBinaryTheme();
            Assert.Equal(ThemeService.ModeDark, next2);
            Assert.Equal(ThemeService.ThemeDark, service.ActiveTheme);
        }

        [Fact]
        public void ThemeService_NormalizeThemeMode_HandlesNullAndWhitespaceGracefully()
        {
            Assert.Equal(ThemeService.ModeSystem, ThemeService.NormalizeThemeMode(null));
            Assert.Equal(ThemeService.ModeSystem, ThemeService.NormalizeThemeMode("   "));
            Assert.Equal(ThemeService.ModeSystem, ThemeService.NormalizeThemeMode("invalid_mode"));
            Assert.Equal(ThemeService.ModeDark, ThemeService.NormalizeThemeMode("dark"));
            Assert.Equal(ThemeService.ModeLight, ThemeService.NormalizeThemeMode("LIGHT"));
        }

        [Fact]
        public void ThemeDetection_GetIsSystemInLightTheme_RunsSafelyWithoutThrowing()
        {
            // Act & Assert: Must not throw exceptions regardless of environment
            bool isLight = ThemeService.GetIsSystemInLightTheme();
            Assert.True(isLight || !isLight); // Valid bool returned
        }

        [Fact]
        public void ThemeDictionaries_DarkAndLight_Have100PercentSymmetricColorKeys()
        {
            // Arrange
            string darkPath = ResolvePath(PossibleDarkThemePaths);
            string lightPath = ResolvePath(PossibleLightThemePaths);

            var darkDict = ParseDictionaryKeys(darkPath);
            var lightDict = ParseDictionaryKeys(lightPath);

            // Assert: Catalog size
            Assert.True(darkDict.Count >= 40, $"DarkTheme should have >= 40 keys, found {darkDict.Count}");
            Assert.True(lightDict.Count >= 40, $"LightTheme should have >= 40 keys, found {lightDict.Count}");

            // Act: Symmetry check
            var missingInLight = darkDict.Keys.Except(lightDict.Keys).ToList();
            var missingInDark = lightDict.Keys.Except(darkDict.Keys).ToList();

            // Assert: Exact 100% Symmetry
            Assert.True(missingInLight.Count == 0, $"Keys in DarkTheme missing in LightTheme: {string.Join(", ", missingInLight)}");
            Assert.True(missingInDark.Count == 0, $"Keys in LightTheme missing in DarkTheme: {string.Join(", ", missingInDark)}");
            Assert.Equal(darkDict.Count, lightDict.Count);

            // Assert: Essential semantic color tokens are present
            string[] requiredTokens =
            {
                "BrushWindowBg", "BrushCardBg", "BrushCardBorder",
                "BrushInputBg", "BrushInputBorder", "BrushTextPrimary",
                "BrushTextMuted", "BrushAccentPrimary", "BrushSuccess",
                "BrushWarning", "BrushDanger"
            };

            foreach (var token in requiredTokens)
            {
                Assert.True(darkDict.ContainsKey(token), $"DarkTheme missing essential token '{token}'");
                Assert.True(lightDict.ContainsKey(token), $"LightTheme missing essential token '{token}'");
            }
        }

        [Fact]
        public void IconLoader_LoadBitmapWithoutLock_AllowsFileOverwriteWhileOpen()
        {
            // Arrange: Generate 1x1 dummy PNG in temp
            string tempDir = Path.Combine(Path.GetTempPath(), "revitagent_icon_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempPng = Path.Combine(tempDir, "test_icon.png");

            try
            {
                // Write a valid 1x1 PNG bitmap
                var enc = new PngBitmapEncoder();
                var bmp = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 255 }, 4);
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(tempPng))
                {
                    enc.Save(fs);
                }

                // Act: Load bitmap through no-lock loader
                var loaded = IconLoader.LoadBitmapWithoutLock(tempPng);

                // Assert
                Assert.NotNull(loaded);
                Assert.True(loaded.IsFrozen, "Loaded bitmap should be frozen for thread safety.");

                // Act & Assert: Must be able to overwrite the file on disk immediately (No file lock!)
                File.WriteAllBytes(tempPng, new byte[] { 1, 2, 3 });
                Assert.True(File.Exists(tempPng));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void IconLoader_WhenFileNotFound_ReturnsNullGracefully()
        {
            string nonExistent = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N") + ".png");
            var result = IconLoader.LoadBitmapWithoutLock(nonExistent);
            Assert.Null(result);
        }

        [Fact]
        public void AppConfig_ThemeMode_RoundtripPersistence_PreservesValue()
        {
            // Arrange
            string tempFile = Path.Combine(Path.GetTempPath(), "test_theme_config_" + Guid.NewGuid().ToString("N") + ".enc");
            try
            {
                var manager = new EncryptedConfigManager(tempFile);
                var config = new AppConfig
                {
                    ThemeMode = ThemeService.ModeLight,
                    Language = "en-US"
                };

                // Act - Save
                manager.SaveConfig(config);

                // Act - Load
                var loaded = manager.LoadConfig();

                // Assert
                Assert.Equal(ThemeService.ModeLight, loaded.ThemeMode);
                Assert.Equal("en-US", loaded.Language);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        [Fact]
        public void IconPresenter_Instantiates_AndUpdatesVisualsSafely()
        {
            Exception? threadEx = null;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    var presenter = new IconPresenter
                    {
                        IconName = "launch_revit",
                        FallbackGlyph = "🚀",
                        IconSize = 20
                    };

                    Assert.Equal("launch_revit", presenter.IconName);
                    Assert.Equal("🚀", presenter.FallbackGlyph);
                    Assert.Equal(20, presenter.IconSize);
                    Assert.Equal(20, presenter.Width);
                    Assert.Equal(20, presenter.Height);
                    Assert.NotNull(presenter.Content);
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
            });

            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            t.Join();

            if (threadEx != null)
            {
                throw threadEx;
            }
        }
    }
}
