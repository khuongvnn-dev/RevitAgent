using RevitAgent.Core.Machine;
using RevitAgent.Core.Tools;
using System.Text.Json;

namespace RevitAgent.Tests.Machine;

public class MachineVerdictTests
{
    [Fact]
    public void Verdict_AllPass_IsReady()
    {
        var items = new List<MachineCheckItem>
        {
            new() { Id = "session", Title = "Session", Status = MachineCheckStatus.Pass, Detail = "OK", Fix = null },
            new() { Id = "locked", Title = "Locked", Status = MachineCheckStatus.Pass, Detail = "OK", Fix = null },
        };

        var report = new MachineReadinessReport
        {
            ReadyForUnattended = items.All(i => i.Status != MachineCheckStatus.Fail),
            CheckedAt = DateTimeOffset.UtcNow,
            Year = 2024,
            Items = items
        };

        Assert.True(report.ReadyForUnattended);
    }

    [Fact]
    public void Verdict_PassAndWarn_IsReady()
    {
        var items = new List<MachineCheckItem>
        {
            new() { Id = "session", Title = "Session", Status = MachineCheckStatus.Pass, Detail = "OK", Fix = null },
            new() { Id = "telegram", Title = "Telegram", Status = MachineCheckStatus.Warn, Detail = "Not configured", Fix = "Configure telegram" },
            new() { Id = "elevation", Title = "Elevation", Status = MachineCheckStatus.Warn, Detail = "Elevated", Fix = "Do not elevate" },
        };

        var report = new MachineReadinessReport
        {
            ReadyForUnattended = items.All(i => i.Status != MachineCheckStatus.Fail),
            CheckedAt = DateTimeOffset.UtcNow,
            Year = 2024,
            Items = items
        };

        Assert.True(report.ReadyForUnattended);
    }

    [Fact]
    public void Verdict_AnyFail_IsNotReady()
    {
        var items = new List<MachineCheckItem>
        {
            new() { Id = "session", Title = "Session", Status = MachineCheckStatus.Pass, Detail = "OK", Fix = null },
            new() { Id = "sleep", Title = "Sleep", Status = MachineCheckStatus.Fail, Detail = "Sleep enabled", Fix = "Disable sleep" },
            new() { Id = "telegram", Title = "Telegram", Status = MachineCheckStatus.Warn, Detail = "Not configured", Fix = "Configure telegram" },
        };

        var report = new MachineReadinessReport
        {
            ReadyForUnattended = items.All(i => i.Status != MachineCheckStatus.Fail),
            CheckedAt = DateTimeOffset.UtcNow,
            Year = 2024,
            Items = items
        };

        Assert.False(report.ReadyForUnattended);
    }

    [Fact]
    public void Serialization_SnakeCaseContract_MatchesSpec()
    {
        var report = new MachineReadinessReport
        {
            ReadyForUnattended = false,
            CheckedAt = DateTimeOffset.Parse("2026-11-25T10:00:00+07:00"),
            Year = 2024,
            Items = new List<MachineCheckItem>
            {
                new()
                {
                    Id = "sleep",
                    Title = "Sleep & hibernate",
                    Status = "fail",
                    Detail = "Sleep timeout is 900s",
                    Fix = "Tắt sleep"
                }
            }
        };

        var json = GatewayJson.Serialize(report);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("ready_for_unattended").GetBoolean());
        Assert.Equal(2024, root.GetProperty("year").GetInt32());
        Assert.True(root.TryGetProperty("checked_at", out _));

        var items = root.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());

        var first = items[0];
        Assert.Equal("sleep", first.GetProperty("id").GetString());
        Assert.Equal("Sleep & hibernate", first.GetProperty("title").GetString());
        Assert.Equal("fail", first.GetProperty("status").GetString());
        Assert.Equal("Sleep timeout is 900s", first.GetProperty("detail").GetString());
        Assert.Equal("Tắt sleep", first.GetProperty("fix").GetString());
    }
}
