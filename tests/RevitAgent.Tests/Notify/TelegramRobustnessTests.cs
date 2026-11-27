using System.IO;
using System.Net;
using System.Text.Json;
using RevitAgent.Core;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Notify;
using RevitAgent.Core.Policy;
using Xunit;

namespace RevitAgent.Tests.Notify;

public class TelegramRobustnessTests : IDisposable
{
    private const string BotToken = "555123:ROBUST_TOKEN";
    private const long ChatId = 777888999;

    private readonly string _tempDir;
    private readonly AuditLog _audit;
    private readonly TelegramConfigStore _store;

    public TelegramRobustnessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RevitAgent_TgRobust_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = new AgentPaths(_tempDir);
        _audit = new AuditLog(paths);
        _store = new TelegramConfigStore(paths, _audit);
        _store.Save(new TelegramOptions { BotToken = BotToken, ChatId = ChatId, Enabled = true });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static HumanQuestion Question(string title, bool bypass, TimeSpan? timeout = null) =>
        new(title, "text of " + title, null, new[] { "Yes", "No" }, "No", timeout ?? TimeSpan.FromSeconds(10)) { BypassQueue = bypass };

    private static object Callback(int updateId, string data, long fromId = ChatId, long chatId = ChatId) => new
    {
        update_id = updateId,
        callback_query = new
        {
            id = "cb" + updateId,
            from = new { id = fromId, username = "owner" },
            message = new { message_id = 1, chat = new { id = chatId } },
            data,
        },
    };

    [Fact]
    public async Task OwnerApproval_IsNotStarvedByAPendingHumanAsk_AndEachCallbackReachesItsOwnQuestion()
    {
        var sync = new object();
        var idByTitle = new Dictionary<string, string>();
        var delivered = false;
        var handler = new FakeHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("sendMessage"))
            {
                using var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (doc.RootElement.TryGetProperty("reply_markup", out var rm))
                {
                    var data = rm.GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString()!;
                    var text = doc.RootElement.GetProperty("text").GetString()!;
                    lock (sync)
                    {
                        idByTitle[text.Contains("HUMAN") ? "human" : "approval"] = data.Split(':')[1];
                    }
                }

                return FakeHttpMessageHandler.OkJson(new { ok = true, result = new { message_id = 10 } });
            }

            if (uri.Contains("getUpdates"))
            {
                lock (sync)
                {
                    if (!delivered && idByTitle.Count == 2)
                    {
                        delivered = true;
                        return FakeHttpMessageHandler.OkJson(new
                        {
                            ok = true,
                            result = new[]
                            {
                                Callback(1, $"q:{idByTitle["approval"]}:0"), // approval -> "Yes"
                                Callback(2, $"q:{idByTitle["human"]}:1"),    // human_ask -> "No"
                            },
                        });
                    }
                }

                return FakeHttpMessageHandler.OkJson(new { ok = true, result = Array.Empty<object>() });
            }

            return FakeHttpMessageHandler.OkJson(new { ok = true, result = true });
        });
        using var client = new TelegramClient(handler);
        var escalation = new TelegramHumanEscalation(_store, client, _audit);

        var human = escalation.AskAsync(Question("HUMAN", bypass: false));
        while (true)
        {
            lock (sync)
            {
                if (idByTitle.ContainsKey("human"))
                {
                    break;
                }
            }

            await Task.Delay(10);
        }

        // The human_ask is still open; the approval must be sent and answered anyway.
        var approval = escalation.AskAsync(Question("APPROVAL", bypass: true));

        var both = await Task.WhenAll(approval, human).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("Yes", both[0].Choice);
        Assert.False(both[0].TimedOut);
        Assert.Equal("No", both[1].Choice);
        Assert.False(both[1].TimedOut);
    }

    [Fact]
    public async Task Poll_RecoversFromTransientErrors()
    {
        string? id = null;
        var polls = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("sendMessage"))
            {
                using var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (doc.RootElement.TryGetProperty("reply_markup", out var rm))
                {
                    id = rm.GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString()!.Split(':')[1];
                }

                return FakeHttpMessageHandler.OkJson(new { ok = true, result = new { message_id = 10 } });
            }

            if (uri.Contains("getUpdates"))
            {
                return Interlocked.Increment(ref polls) <= 2
                    ? FakeHttpMessageHandler.Error(HttpStatusCode.BadGateway, "bad gateway")
                    : FakeHttpMessageHandler.OkJson(new { ok = true, result = new[] { Callback(1, $"q:{id}:0") } });
            }

            return FakeHttpMessageHandler.OkJson(new { ok = true, result = true });
        });
        using var client = new TelegramClient(handler);
        var escalation = new TelegramHumanEscalation(_store, client, _audit, TimeSpan.FromMilliseconds(1));

        var answer = await escalation.AskAsync(Question("Q", bypass: false)).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.False(answer.TimedOut);
        Assert.Equal("Yes", answer.Choice);
    }

    [Fact]
    public async Task Poll_AfterRepeatedFailures_FallsBackToTheSafeOption()
    {
        var handler = new FakeHttpMessageHandler(req =>
            req.RequestUri!.ToString().Contains("getUpdates")
                ? FakeHttpMessageHandler.Error(HttpStatusCode.BadGateway, "bad gateway")
                : FakeHttpMessageHandler.OkJson(new { ok = true, result = new { message_id = 10 } }));
        using var client = new TelegramClient(handler);
        var escalation = new TelegramHumanEscalation(_store, client, _audit, TimeSpan.FromMilliseconds(1));

        var answer = await escalation.AskAsync(Question("Q", bypass: true)).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.True(answer.TimedOut);
        Assert.Equal("No", answer.Choice);
        Assert.Contains("polling failed", answer.Reason);
    }

    [Fact]
    public async Task PrivateChat_CallbackFromAnotherUserIsRejected()
    {
        string? id = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("sendMessage"))
            {
                using var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result);
                if (doc.RootElement.TryGetProperty("reply_markup", out var rm))
                {
                    id = rm.GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString()!.Split(':')[1];
                }

                return FakeHttpMessageHandler.OkJson(new { ok = true, result = new { message_id = 10 } });
            }

            if (uri.Contains("getUpdates"))
            {
                // Right chat, wrong user.
                return FakeHttpMessageHandler.OkJson(new { ok = true, result = new[] { Callback(1, $"q:{id}:0", fromId: 42) } });
            }

            return FakeHttpMessageHandler.OkJson(new { ok = true, result = true });
        });
        using var client = new TelegramClient(handler);
        var escalation = new TelegramHumanEscalation(_store, client, _audit);

        var answer = await escalation.AskAsync(Question("Q", bypass: false, TimeSpan.FromMilliseconds(300)));

        Assert.True(answer.TimedOut);
        Assert.Equal("No", answer.Choice);
        Assert.Contains(_audit.Recent(), a => a.ErrorCode == "UNAUTHORIZED_CHAT");
    }

    [Fact]
    public async Task OwnerApproval_CancelledByTheClient_IsAudited()
    {
        var channel = new HangingChannel();
        var service = new OwnerApprovalService(new IOwnerApprovalChannel[] { channel }, _audit);
        using var cts = new CancellationTokenSource();
        var task = service.RequestAsync(new OwnerApprovalRequest("bridge_install", "agent-1", new[] { 2024 }, TimeSpan.FromMinutes(5)), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Contains(_audit.Recent(), a => a.Tool == OwnerApprovalService.AuditTool && (a.Note ?? string.Empty).Contains("cancelled"));
    }

    [Fact]
    public void ConfigStore_WritesAtomically_AndLeavesNoTempFiles()
    {
        _store.Save(new TelegramOptions { BotToken = "1:abc", ChatId = 5, Enabled = true });
        _store.Save(new TelegramOptions { BotToken = "1:def", ChatId = 6, Enabled = true });

        Assert.Equal(6, _store.Load().ChatId);
        Assert.Equal("1:def", _store.Load().BotToken);
        Assert.DoesNotContain(Directory.EnumerateFiles(_tempDir, "*.tmp", SearchOption.AllDirectories), _ => true);
    }

    private sealed class HangingChannel : IOwnerApprovalChannel
    {
        public string Name => "hang";

        public bool IsAvailable => true;

        public async Task<OwnerDecision> AskAsync(OwnerApprovalRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return OwnerDecision.Deny(Name, "x", "x");
        }
    }
}
