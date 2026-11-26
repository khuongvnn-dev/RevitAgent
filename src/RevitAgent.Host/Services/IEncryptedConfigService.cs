namespace RevitAgent.Host.Services
{
    using RevitAgent.Host.Config;

    public interface IEncryptedConfigService
    {
        string ConfigFilePath { get; }
        bool ConfigFileExists { get; }
        AppConfig CurrentConfig { get; }

        AppConfig LoadConfig();
        void SaveConfig(AppConfig config);
        void OpenConfigFileFolder();
    }
}
