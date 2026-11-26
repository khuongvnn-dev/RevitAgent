using RevitAgent.Core.Machine;

namespace RevitAgent.Tests.Machine;

public class PowerCfgParserTests
{
    private const string SampleNoSleep = @"
Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
  GUID Alias: SCHEME_BALANCED
  Subgroup GUID: 238c9fa8-0aad-41ed-83f4-97be242c8f20  (Sleep)
    GUID Alias: SUB_SLEEP
    Power Setting GUID: 29f6c1db-86da-48c5-9fdb-f2b67b1f44da  (Sleep after)
      GUID Alias: STANDBYIDLE
      Minimum Possible Setting: 0x00000000
      Maximum Possible Setting: 0xffffffff
      Possible Settings increment: 0x00000001
      Possible Settings units: Seconds
    Current AC Power Setting Index: 0x00000000
    Current DC Power Setting Index: 0x00000384

    Power Setting GUID: 94ac6d29-73ce-41a6-809f-6363ba21b47e  (Allow hybrid sleep)
      GUID Alias: HYBRIDSLEEP
      Possible Setting Index: 000
      Possible Setting Friendly Name: Off
      Possible Setting Index: 001
      Possible Setting Friendly Name: On
    Current AC Power Setting Index: 0x00000001
    Current DC Power Setting Index: 0x00000001

    Power Setting GUID: 9d7815a6-7ee4-497e-8888-515a05f02364  (Hibernate after)
      GUID Alias: HIBERNATEIDLE
      Minimum Possible Setting: 0x00000000
      Maximum Possible Setting: 0xffffffff
      Possible Settings increment: 0x00000001
      Possible Settings units: Seconds
    Current AC Power Setting Index: 0x00000000
    Current DC Power Setting Index: 0x00000000
";

    private const string SampleWithSleepAndHibernate = @"
Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
  Subgroup GUID: 238c9fa8-0aad-41ed-83f4-97be242c8f20  (Sleep)
    Power Setting GUID: 29f6c1db-86da-48c5-9fdb-f2b67b1f44da  (Sleep after)
      GUID Alias: STANDBYIDLE
    Current AC Power Setting Index: 0x00000384
    Current DC Power Setting Index: 0x00000384

    Power Setting GUID: 9d7815a6-7ee4-497e-8888-515a05f02364  (Hibernate after)
      GUID Alias: HIBERNATEIDLE
    Current AC Power Setting Index: 0x00000708
    Current DC Power Setting Index: 0x00000708
";

    [Fact]
    public void Parse_WhenSleepAndHibernateZero_ReturnsZeros()
    {
        var (sleep, hib) = PowerCfgParser.ParseSleepTimeouts(SampleNoSleep);
        Assert.Equal(0, sleep);
        Assert.Equal(0, hib);
    }

    [Fact]
    public void Parse_WhenSleepAndHibernateConfigured_ReturnsParsedSeconds()
    {
        var (sleep, hib) = PowerCfgParser.ParseSleepTimeouts(SampleWithSleepAndHibernate);
        Assert.Equal(900, sleep);    // 0x384 = 900
        Assert.Equal(1800, hib);   // 0x708 = 1800
    }

    [Fact]
    public void Parse_NullOrEmpty_ReturnsZeros()
    {
        var (s1, h1) = PowerCfgParser.ParseSleepTimeouts(null);
        Assert.Equal(0, s1);
        Assert.Equal(0, h1);

        var (s2, h2) = PowerCfgParser.ParseSleepTimeouts("");
        Assert.Equal(0, s2);
        Assert.Equal(0, h2);
    }

    [Fact]
    public void Parse_DecimalValues_ReturnsParsedSeconds()
    {
        const string text = @"
    Power Setting GUID: 29f6c1db-86da-48c5-9fdb-f2b67b1f44da  (Sleep after)
    Current AC Power Setting Index: 600
    Power Setting GUID: 9d7815a6-7ee4-497e-8888-515a05f02364  (Hibernate after)
    Current AC Power Setting Index: 1200
";
        var (sleep, hib) = PowerCfgParser.ParseSleepTimeouts(text);
        Assert.Equal(600, sleep);
        Assert.Equal(1200, hib);
    }
}
