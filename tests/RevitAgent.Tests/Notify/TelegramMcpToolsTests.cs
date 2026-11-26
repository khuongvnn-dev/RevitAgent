using System.IO;
using System.Text.Json;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Notify.Tools;
using RevitAgent.Core.Tools;
using Xunit;

namespace RevitAgent.Tests.Notify;

public class TelegramMcpToolsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AgentPaths _paths;
    private readonly AuditLog _auditLog;
    private readonly TelegramConfigStore _configStore;

    public TelegramMcpToolsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RevitAgent_McpToolsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new AgentPaths(_tempDir);
        _auditLog = new AuditLog(_paths);
        _configStore = new TelegramConfigStore(_paths, _auditLog);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch { }
        }
    }

    private sealed class FakeHumanEscalation : IHumanEscalation
    {
        public HumanQuestion? LastQuestion { get; private set; }
        public HumanAnswer NextAnswer { get; set; } = new(
            Choice: "Postpone",
            AnsweredBy: "test_user",
            TimedOut: false,
            AskedAt: DateTimeOffset.UtcNow,
            AnsweredAt: DateTimeOffset.UtcNow);

        public Task<HumanAnswer> AskAsync(HumanQuestion q, CancellationToken ct = default)
        {
            LastQuestion = q;
            return Task.FromResult(NextAnswer);
        }
    }

    [Fact]
    public async Task HumanAsk_Validation_SafeOptionNotInOptions_ThrowsValidationFailed()
    {
        var escalation = new FakeHumanEscalation();
        var tool = new HumanAskTool(escalation, _paths);

        var args = JsonSerializer.SerializeToElement(new
        {
            question = "Should we delete elements?",
            options = new[] { "Yes", "No" },
            safe_option = "Cancel" // Not in options!
        });

        var call = new ToolCall(tool.Name, args);

        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            tool.ExecuteAsync(call, CancellationToken.None));

        Assert.Equal(GatewayErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("safe_option", ex.Message);
    }

    [Theory]
    [InlineData(1)] // Too few
    [InlineData(7)] // Too many
    public async Task HumanAsk_Validation_OptionsLength_ThrowsValidationFailed(int optionCount)
    {
        var escalation = new FakeHumanEscalation();
        var tool = new HumanAskTool(escalation, _paths);

        var options = Enumerable.Range(1, optionCount).Select(i => $"Option {i}").ToArray();
        var args = JsonSerializer.SerializeToElement(new
        {
            question = "Pick one",
            options,
            safe_option = options[0]
        });

        var call = new ToolCall(tool.Name, args);

        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            tool.ExecuteAsync(call, CancellationToken.None));

        Assert.Equal(GatewayErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("between 2 and 6", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(241)]
    public async Task HumanAsk_Validation_TimeoutMinutesOutOfRange_ThrowsValidationFailed(int timeout)
    {
        var escalation = new FakeHumanEscalation();
        var tool = new HumanAskTool(escalation, _paths);

        var args = JsonSerializer.SerializeToElement(new
        {
            question = "Pick one",
            options = new[] { "Option A", "Option B" },
            safe_option = "Option A",
            timeout_minutes = timeout
        });

        var call = new ToolCall(tool.Name, args);

        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            tool.ExecuteAsync(call, CancellationToken.None));

        Assert.Equal(GatewayErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("timeout_minutes", ex.Message);
    }

    [Fact]
    public async Task HumanAsk_Validation_InvalidScreenshotPath_ThrowsValidationFailed()
    {
        var escalation = new FakeHumanEscalation();
        var tool = new HumanAskTool(escalation, _paths);

        // Case 1: Non-existent file
        var args1 = JsonSerializer.SerializeToElement(new
        {
            question = "Pick one",
            options = new[] { "A", "B" },
            safe_option = "A",
            screenshot_path = Path.Combine(_paths.Root, "nonexistent.png")
        });

        var ex1 = await Assert.ThrowsAsync<GatewayException>(() =>
            tool.ExecuteAsync(new ToolCall(tool.Name, args1), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, ex1.Code);

        // Case 2: Not a .png
        var textFile = Path.Combine(_paths.Root, "note.txt");
        File.WriteAllText(textFile, "hello");
        var args2 = JsonSerializer.SerializeToElement(new
        {
            question = "Pick one",
            options = new[] { "A", "B" },
            safe_option = "A",
            screenshot_path = textFile
        });

        var ex2 = await Assert.ThrowsAsync<GatewayException>(() =>
            tool.ExecuteAsync(new ToolCall(tool.Name, args2), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, ex2.Code);

        // Case 3: Outside REVITAGENT_HOME
        var outsideDir = Path.Combine(Path.GetTempPath(), "outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsidePng = Path.Combine(outsideDir, "outside.png");
        File.WriteAllBytes(outsidePng, new byte[] { 1, 2, 3 });

        var args3 = JsonSerializer.SerializeToElement(new
        {
            question = "Pick one",
            options = new[] { "A", "B" },
            safe_option = "A",
            screenshot_path = outsidePng
        });

        var ex3 = await Assert.ThrowsAsync<GatewayException>(() =>
            tool.ExecuteAsync(new ToolCall(tool.Name, args3), CancellationToken.None));
        Assert.Equal(GatewayErrorCodes.ValidationFailed, ex3.Code);
        Assert.Contains("REVITAGENT_HOME", ex3.Message);
    }

    [Fact]
    public async Task HumanAsk_Success_InvokesEscalationAndReturnsResult()
    {
        var escalation = new FakeHumanEscalation
        {
            NextAnswer = new HumanAnswer("Option B", "owner_chat", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };
        var tool = new HumanAskTool(escalation, _paths);

        var validPng = Path.Combine(_paths.Root, "dialog.png");
        File.WriteAllBytes(validPng, new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 });

        var args = JsonSerializer.SerializeToElement(new
        {
            question = "Which option should Revit choose?",
            options = new[] { "Option A", "Option B" },
            safe_option = "Option A",
            timeout_minutes = 15,
            screenshot_path = validPng
        });

        var result = await tool.ExecuteAsync(new ToolCall(tool.Name, args), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(result.Structured);
        Assert.Equal("Option B", result.Structured.Value.GetProperty("choice").GetString());
        Assert.Equal("owner_chat", result.Structured.Value.GetProperty("answered_by").GetString());
        Assert.False(result.Structured.Value.GetProperty("timed_out").GetBoolean());
    }

    [Fact]
    public async Task NotifyStatus_ReturnsStatusWithoutToken()
    {
        _configStore.Save(new TelegramOptions
        {
            BotToken = "998877:SECRET_TOKEN_DO_NOT_EXPOSE",
            ChatId = 12345678,
            AskTimeoutMinutes = 45,
            Enabled = true
        });

        var tool = new NotifyStatusTool(_configStore);
        var result = await tool.ExecuteAsync(new ToolCall(tool.Name, null), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.NotNull(result.Structured);

        var json = result.Structured.Value.GetRawText();
        Assert.True(result.Structured.Value.GetProperty("enabled").GetBoolean());
        Assert.True(result.Structured.Value.GetProperty("configured").GetBoolean());
        Assert.Equal(12345678, result.Structured.Value.GetProperty("chat_id").GetInt64());
        Assert.Equal(45, result.Structured.Value.GetProperty("ask_timeout_minutes").GetInt32());

        // CRITICAL: Token must never be present in the returned JSON!
        Assert.DoesNotContain("SECRET_TOKEN_DO_NOT_EXPOSE", json);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }
}
