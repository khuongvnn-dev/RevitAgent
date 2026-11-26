namespace RevitAgent.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Windows;
    using System.Windows.Markup;
    using System.Xml.Linq;
    using Microsoft.Extensions.DependencyInjection;
    using RevitAgent.Host;
    using RevitAgent.Host.Config;
    using RevitAgent.Host.Services;
    using RevitAgent.Host.ViewModels;
    using Xunit;

    public class LocalizationTests
    {
        private static readonly string[] PossibleViVnPaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Localization", "Strings.vi-VN.xaml"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "RevitAgent.Host", "Resources", "Localization", "Strings.vi-VN.xaml"),
            Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Localization", "Strings.vi-VN.xaml"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "RevitAgent.Host", "Resources", "Localization", "Strings.vi-VN.xaml")
        };

        private static readonly string[] PossibleEnUsPaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Localization", "Strings.en-US.xaml"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "src", "RevitAgent.Host", "Resources", "Localization", "Strings.en-US.xaml"),
            Path.Combine(Directory.GetCurrentDirectory(), "Resources", "Localization", "Strings.en-US.xaml"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "RevitAgent.Host", "Resources", "Localization", "Strings.en-US.xaml")
        };

        private static string ResolveDictionaryPath(string[] candidates)
        {
            foreach (var path in candidates)
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            throw new FileNotFoundException($"Cannot locate localization dictionary in probe paths: {string.Join(", ", candidates)}");
        }

        private static Dictionary<string, string> ParseDictionaryXml(string filePath)
        {
            var doc = XDocument.Load(filePath);
            var xNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var element in doc.Root!.Elements())
            {
                var keyAttr = element.Attribute(xNamespace + "Key") ?? element.Attribute("Key");
                if (keyAttr != null)
                {
                    result[keyAttr.Value] = element.Value;
                }
            }

            return result;
        }

        [Fact]
        public void DictionarySymmetry_BothDictionaries_Have100PercentIdenticalKeysAndNonEmptyValues()
        {
            // Arrange
            string viPath = ResolveDictionaryPath(PossibleViVnPaths);
            string enPath = ResolveDictionaryPath(PossibleEnUsPaths);

            var viDict = ParseDictionaryXml(viPath);
            var enDict = ParseDictionaryXml(enPath);

            // Assert: Significant number of keys cataloged
            Assert.True(viDict.Count >= 50, $"vi-VN dictionary should contain at least 50 keys, found {viDict.Count}");
            Assert.True(enDict.Count >= 50, $"en-US dictionary should contain at least 50 keys, found {enDict.Count}");

            // Act: Find asymmetric keys
            var missingInEn = viDict.Keys.Except(enDict.Keys).ToList();
            var missingInVi = enDict.Keys.Except(viDict.Keys).ToList();

            // Assert: 100% Symmetry
            Assert.True(missingInEn.Count == 0, $"Keys present in vi-VN but missing in en-US: {string.Join(", ", missingInEn)}");
            Assert.True(missingInVi.Count == 0, $"Keys present in en-US but missing in vi-VN: {string.Join(", ", missingInVi)}");
            Assert.Equal(viDict.Count, enDict.Count);

            // Assert: No empty or whitespace values
            foreach (var (key, value) in viDict)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"vi-VN key '{key}' has an empty or whitespace value.");
                Assert.DoesNotContain("\uFFFD", value); // No unicode replacement characters
                Assert.False(value.Contains("Th?i gian") || value.Contains("Hnh d?ng"), $"vi-VN key '{key}' has corrupted question mark encoding: {value}");
            }

            foreach (var (key, value) in enDict)
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"en-US key '{key}' has an empty or whitespace value.");
                Assert.DoesNotContain("\uFFFD", value);
            }
        }

        [Fact]
        public void LocalizationService_LoadsDefaultAndSwapsLanguage_Successfully()
        {
            // Arrange
            var service = new LocalizationService();
            string? firedLanguage = null;
            service.LanguageChanged += (lang) => firedLanguage = lang;

            // Assert initial state is vi-VN
            Assert.Equal(LocalizationService.CultureVietnamese, service.CurrentLanguage);
            string viTitle = service.GetString("Str_WindowTitle");
            Assert.Contains("Cổng MCP", viTitle);

            // Act - Swap to en-US
            service.SetLanguage(LocalizationService.CultureEnglish);

            // Assert swapped state
            Assert.Equal(LocalizationService.CultureEnglish, service.CurrentLanguage);
            Assert.Equal(LocalizationService.CultureEnglish, firedLanguage);
            string enTitle = service.GetString("Str_WindowTitle");
            Assert.Contains("MCP Gateway", enTitle);
            Assert.NotEqual(viTitle, enTitle);

            // Act - Swap back to vi-VN
            service.SetLanguage(LocalizationService.CultureVietnamese);
            Assert.Equal(LocalizationService.CultureVietnamese, service.CurrentLanguage);
            Assert.Equal(LocalizationService.CultureVietnamese, firedLanguage);
            Assert.Equal(viTitle, service.GetString("Str_WindowTitle"));
        }

        [Theory]
        [InlineData(null, LocalizationService.CultureVietnamese)]
        [InlineData("", LocalizationService.CultureVietnamese)]
        [InlineData("   ", LocalizationService.CultureVietnamese)]
        [InlineData("unknown-XYZ", LocalizationService.CultureVietnamese)]
        [InlineData("vi", LocalizationService.CultureVietnamese)]
        [InlineData("vi-VN", LocalizationService.CultureVietnamese)]
        [InlineData("en", LocalizationService.CultureEnglish)]
        [InlineData("en-US", LocalizationService.CultureEnglish)]
        [InlineData("en-GB", LocalizationService.CultureEnglish)]
        public void LocalizationService_SetLanguage_NormalizesCulturesCorrectly(string? inputCulture, string expectedCulture)
        {
            // Arrange
            var service = new LocalizationService();

            // Act
            service.SetLanguage(inputCulture);

            // Assert
            Assert.Equal(expectedCulture, service.CurrentLanguage);
        }

        [Fact]
        public void LocalizationService_GetString_ReturnsKeyWhenNotFound()
        {
            // Arrange
            var service = new LocalizationService();

            // Act
            string result = service.GetString("Str_NonExistent_Key_99999");

            // Assert
            Assert.Equal("Str_NonExistent_Key_99999", result);
        }

        [Fact]
        public void LocalizationService_GetString_ReturnsEmptyOnNullOrEmptyKey()
        {
            // Arrange
            var service = new LocalizationService();

            // Act & Assert
            Assert.Equal(string.Empty, service.GetString(""));
            Assert.Equal(string.Empty, service.GetString(null!));
        }

        [Fact]
        public void AppConfig_LanguageProperty_DefaultsToViVnAndHandlesNull()
        {
            // Arrange
            var config = new AppConfig();

            // Assert default
            Assert.Equal(LocalizationService.CultureVietnamese, config.Language);

            // Act - Set null or whitespace
            config.Language = null!;
            Assert.Equal(LocalizationService.CultureVietnamese, config.Language);

            config.Language = "   ";
            Assert.Equal(LocalizationService.CultureVietnamese, config.Language);

            // Act - Set en-US
            config.Language = LocalizationService.CultureEnglish;
            Assert.Equal(LocalizationService.CultureEnglish, config.Language);
        }

        [Fact]
        public void AppConfig_LanguagePersistence_SavesAndLoadsThroughDpapi()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "revitagent_dpapi_lang_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var encPath = Path.Combine(tempDir, "appsettings.enc");

            try
            {
                var manager = new EncryptedConfigManager(encPath);

                // Act 1: Save with en-US
                var config = new AppConfig
                {
                    Language = LocalizationService.CultureEnglish,
                    IpcPipeName = "custom-pipe-lang-test"
                };
                manager.SaveConfig(config);

                // Assert 1: Load and verify
                var loaded = manager.LoadConfig();
                Assert.Equal(LocalizationService.CultureEnglish, loaded.Language);
                Assert.Equal("custom-pipe-lang-test", loaded.IpcPipeName);

                // Act 2: Save with vi-VN
                loaded.Language = LocalizationService.CultureVietnamese;
                manager.SaveConfig(loaded);

                // Assert 2: Load and verify
                var reloaded = manager.LoadConfig();
                Assert.Equal(LocalizationService.CultureVietnamese, reloaded.Language);
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
        public void SettingsViewModel_ChangingLanguage_SwapsLocalizationAndSavesToDpapi()
        {
            using var rig = new Host.HostRig();
            var localizationService = new LocalizationService();
            var configService = rig.Config;

            var vm = new SettingsViewModel(configService, new RevitAgent.Core.Policy.GatewayPolicy(), rig.Paths, rig.Log, localizationService);

            // Assert initial
            Assert.Equal(LocalizationService.CultureVietnamese, vm.SelectedLanguage);
            Assert.Equal(LocalizationService.CultureVietnamese, localizationService.CurrentLanguage);

            // Act - Change to English
            vm.SelectedLanguage = LocalizationService.CultureEnglish;

            // Assert
            Assert.Equal(LocalizationService.CultureEnglish, localizationService.CurrentLanguage);
            Assert.Equal(LocalizationService.CultureEnglish, configService.LoadConfig().Language);

            // Act - Change back to Vietnamese
            vm.SelectedLanguage = LocalizationService.CultureVietnamese;

            // Assert
            Assert.Equal(LocalizationService.CultureVietnamese, localizationService.CurrentLanguage);
            Assert.Equal(LocalizationService.CultureVietnamese, configService.LoadConfig().Language);
        }

        [Fact]
        public void MainViewModel_SwitchLanguageCommand_TogglesAndUpdatesProperties()
        {
            using var rig = new Host.HostRig();
            var localization = new LocalizationService();
            var settingsVm = new SettingsViewModel(rig.Config, new RevitAgent.Core.Policy.GatewayPolicy(), rig.Paths, rig.Log, localization);
            var mainVm = new MainViewModel(rig.Dashboard, settingsVm, new AuditLogViewModel(rig.Log, localization), rig.Ipc, rig.Log, localization);

            // Assert initial
            Assert.Equal(LocalizationService.CultureVietnamese, mainVm.CurrentLanguage);

            // Act - Switch to en-US
            mainVm.SwitchLanguageCommand.Execute("en-US");

            // Assert
            Assert.Equal(LocalizationService.CultureEnglish, mainVm.CurrentLanguage);
            Assert.Equal(LocalizationService.CultureEnglish, localization.CurrentLanguage);
            Assert.True(mainVm.DesktopStatusText == "Desktop Interactive" || mainVm.DesktopStatusText == "Desktop Locked / Disconnected");

            // Act - Toggle without parameter
            mainVm.SwitchLanguageCommand.Execute(null);

            // Assert toggled back to vi-VN
            Assert.Equal(LocalizationService.CultureVietnamese, mainVm.CurrentLanguage);
            Assert.Equal(LocalizationService.CultureVietnamese, localization.CurrentLanguage);
            Assert.True(mainVm.DesktopStatusText == "Desktop Interactive" || mainVm.DesktopStatusText == "Desktop Bị khóa / Mất kết nối");
        }

        [Fact]
        public void EveryUserVisibleKeyUsedByViewsAndViewModels_ExistsInBothDictionaries_AndNoV1StringsRemain()
        {
            var viDict = ParseDictionaryXml(ResolveDictionaryPath(PossibleViVnPaths));
            var enDict = ParseDictionaryXml(ResolveDictionaryPath(PossibleEnUsPaths));

            string? hostDir = null;
            for (string? dir = AppDomain.CurrentDomain.BaseDirectory; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                string candidate = Path.Combine(dir, "src", "RevitAgent.Host");
                if (Directory.Exists(candidate))
                {
                    hostDir = candidate;
                    break;
                }
            }

            Assert.NotNull(hostDir);
            var sources = Directory.GetFiles(hostDir, "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".cs") || f.EndsWith("MainWindow.xaml")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Select(File.ReadAllText)
                .ToList();
            Assert.NotEmpty(sources);

            var used = sources.SelectMany(t => System.Text.RegularExpressions.Regex.Matches(t, @"Str_\w+").Select(m => m.Value)).ToHashSet();
            var missingVi = used.Where(k => !viDict.ContainsKey(k)).ToList();
            var missingEn = used.Where(k => !enDict.ContainsKey(k)).ToList();
            Assert.True(missingVi.Count == 0, "Keys used but missing in vi-VN: " + string.Join(", ", missingVi));
            Assert.True(missingEn.Count == 0, "Keys used but missing in en-US: " + string.Join(", ", missingEn));

            foreach (var key in viDict.Keys.Concat(enDict.Keys))
            {
                Assert.DoesNotContain("Telegram", key);
            }
        }

        [Fact]
        public void DependencyInjection_ResolvesLocalizationService_AsSingleton()
        {
            // Arrange
            var services = new ServiceCollection();
            App.ConfigureAppServices(services, new RevitAgent.Core.AgentPaths(TestGateway.NewTempDir("revitagent_di_")));
            var provider = services.BuildServiceProvider();

            // Act
            var instance1 = provider.GetRequiredService<ILocalizationService>();
            var instance2 = provider.GetRequiredService<ILocalizationService>();

            // Assert
            Assert.NotNull(instance1);
            Assert.Same(instance1, instance2);
        }
    }
}
