using System.Text;
using RevitAgent.Core.Mcp;

namespace RevitAgent.Tests.Mcp;

public sealed class PendingRequestTrackerTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Request_is_pending_until_its_response_arrives()
    {
        var t = new PendingRequestTracker();
        t.ObserveUpstream(B("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"tools/call\"}\n"));
        Assert.Equal(2, t.PendingCount);

        t.ObserveDownstream(B("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n"));
        Assert.Equal(1, t.PendingCount);

        t.ObserveDownstream(B("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"result\":{}}\n"));
        Assert.Equal(0, t.PendingCount);
    }

    [Fact]
    public void Lines_split_across_reads_are_reassembled()
    {
        var t = new PendingRequestTracker();
        t.ObserveUpstream(B("{\"jsonrpc\":\"2.0\",\"id\":7,\"met"));
        Assert.Equal(0, t.PendingCount);
        t.ObserveUpstream(B("hod\":\"ping\"}\r"));
        Assert.Equal(0, t.PendingCount);
        t.ObserveUpstream(B("\n"));
        Assert.Equal(1, t.PendingCount);

        t.ObserveDownstream(B("{\"id\":7,\"res"));
        Assert.Equal(1, t.PendingCount);
        t.ObserveDownstream(B("ult\":{}}\n"));
        Assert.Equal(0, t.PendingCount);
    }

    [Fact]
    public void Notifications_and_garbage_are_not_tracked_and_string_1_differs_from_number_1()
    {
        var t = new PendingRequestTracker();
        t.ObserveUpstream(B("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\nnot json\n"));
        Assert.Equal(0, t.PendingCount);

        t.ObserveUpstream(B("{\"id\":1,\"method\":\"x\"}\n"));
        t.ObserveDownstream(B("{\"id\":\"1\",\"result\":{}}\n"));
        Assert.Equal(1, t.PendingCount);

        // A server-to-client request (has a method) is not a response.
        t.ObserveDownstream(B("{\"id\":1,\"method\":\"sampling/createMessage\"}\n"));
        Assert.Equal(1, t.PendingCount);
    }
}
