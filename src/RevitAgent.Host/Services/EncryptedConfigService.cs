namespace RevitAgent.Host.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using RevitAgent.Host.Config;

    public sealed class EncryptedConfigService : IEncryptedConfigService
    {
        private readonly EncryptedConfigManager _configManager;
        private AppConfig _currentConfig;

        public EncryptedConfigService(EncryptedConfigManager? configManager = null)
        {
            _configManager = configManager ?? new EncryptedConfigManager();
            _currentConfig = new AppConfig();
        }

        public string ConfigFilePath => _configManager.ConfigFilePath;
        public bool ConfigFileExists => _configManager.ConfigFileExists;
        public AppConfig CurrentConfig => _currentConfig;

        public AppConfig LoadConfig()
        {
            _currentConfig = _configManager.LoadConfig();
            return _currentConfig;
        }

        public void SaveConfig(AppConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            _configManager.SaveConfig(config);
            _currentConfig = config;
        }

        public void OpenConfigFileFolder()
        {
            string dir = Path.GetDirectoryName(ConfigFilePath)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = dir,
                UseShellExecute = true
            });
        }
    }
}
