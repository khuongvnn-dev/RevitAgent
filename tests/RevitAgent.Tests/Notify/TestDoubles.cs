using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RevitAgent.Core.Tools;

namespace RevitAgent.Tests.Notify;

/// <summary>
/// In-memory fake HTTP message handler capturing all outgoing requests and returning scripted responses.
/// Completely avoids any real network traffic.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
    private readonly List<HttpRequestMessage> _requests = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? handler = null)
    {
        _handler = handler ?? (_ => OkJson(new { ok = true }));
    }

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToArray();
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return Task.FromResult(_handler(request));
    }

    public static HttpResponseMessage OkJson(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, GatewayJson.Options), Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage Error(HttpStatusCode code, string description) =>
        new(code)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                ok = false,
                error_code = (int)code,
                description
            }, GatewayJson.Options), Encoding.UTF8, "application/json")
        };
}

public sealed record NotificationCall(RevitAgent.Core.Notify.NotificationKind Kind, string Text, IReadOnlyList<string>? Images);

public sealed class FakeNotifier : RevitAgent.Core.Notify.INotifier
{
    private readonly List<NotificationCall> _calls = new();
    private readonly object _gate = new();

    public IReadOnlyList<NotificationCall> Calls
    {
        get
        {
            lock (_gate) return _calls.ToArray();
        }
    }

    public Task NotifyAsync(RevitAgent.Core.Notify.NotificationKind kind, string text, IReadOnlyList<string>? imagePaths = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _calls.Add(new NotificationCall(kind, text, imagePaths));
        }
        return Task.CompletedTask;
    }
}
