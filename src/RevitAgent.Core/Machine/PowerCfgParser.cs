namespace RevitAgent.Core.Machine;

/// <summary>
/// Parser for <c>powercfg /query SCHEME_CURRENT SUB_SLEEP</c> output.
/// Extracts AC standby and hibernate idle timeouts in seconds.
/// </summary>
public static class PowerCfgParser
{
    private const string StandbyGuid = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
    private const string HibernateGuid = "9d7815a6-7ee4-497e-8888-515a05f02364";

    public static (int SleepSeconds, int HibernateSeconds) ParseSleepTimeouts(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (0, 0);
        }

        int sleepSeconds = 0;
        int hibernateSeconds = 0;
        int currentSetting = 0; // 0 = none, 1 = standby/sleep, 2 = hibernate

        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Power Setting GUID:", StringComparison.OrdinalIgnoreCase))
            {
                if (trimmed.Contains(StandbyGuid, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains("Sleep after", StringComparison.OrdinalIgnoreCase))
                {
                    currentSetting = 1;
                }
                else if (trimmed.Contains(HibernateGuid, StringComparison.OrdinalIgnoreCase) ||
                         trimmed.Contains("Hibernate after", StringComparison.OrdinalIgnoreCase))
                {
                    currentSetting = 2;
                }
                else
                {
                    currentSetting = 0;
                }

                continue;
            }

            if (trimmed.StartsWith("GUID Alias:", StringComparison.OrdinalIgnoreCase))
            {
                if (trimmed.Contains("STANDBYIDLE", StringComparison.OrdinalIgnoreCase))
                {
                    currentSetting = 1;
                }
                else if (trimmed.Contains("HIBERNATEIDLE", StringComparison.OrdinalIgnoreCase))
                {
                    currentSetting = 2;
                }

                continue;
            }

            if (currentSetting != 0 && trimmed.StartsWith("Current AC Power Setting Index:", StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex >= 0)
                {
                    var valStr = trimmed[(colonIndex + 1)..].Trim();
                    int seconds = ParseIndexValue(valStr);
                    if (currentSetting == 1)
                    {
                        sleepSeconds = seconds;
                    }
                    else if (currentSetting == 2)
                    {
                        hibernateSeconds = seconds;
                    }
                }

                currentSetting = 0;
            }
        }

        return (sleepSeconds, hibernateSeconds);
    }

    private static int ParseIndexValue(string valStr)
    {
        if (valStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(valStr[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hexVal))
            {
                return hexVal;
            }
        }
        else if (int.TryParse(valStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var decVal))
        {
            return decVal;
        }

        return 0;
    }
}
