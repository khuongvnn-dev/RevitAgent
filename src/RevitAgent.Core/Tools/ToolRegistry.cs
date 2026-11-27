using System.Diagnostics;
using System.Text.RegularExpressions;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Policy;

namespace RevitAgent.Core.Tools;

/// <summary>
/// Observes completed tool calls (e.g. the scenario recorder). Registered in DI like tools:
/// any public class implementing it in a gateway assembly is picked up automatically.
/// </summary>
public interface IToolCallObserver
{
    void OnCompleted(ToolCall call, IGatewayTool tool, ToolResult result, TimeSpan duration);
}

/// <summary>
/// The single entry point for running tools: policy check → timeout → execute → error mapping →
/// audit → observers. Used by the MCP server and the scenario runner.
/// </summary>
public sealed class ToolRegistry
{
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]{2,63}$", RegexOptions.Compiled);

    private readonly Dictionary<string, IGatewayTool> _tools;
    private readonly GatewayPolicy _policy;
    private readonly ActionGuard _guard;
    private readonly AuditLog _audit;
    private readonly IReadOnlyList<IToolCallObserver> _observers;

    public ToolRegistry(IEnumerable<IGatewayTool> tools, GatewayPolicy policy, ActionGuard guard, AuditLog audit, IEnumerable<IToolCallObserver> observers)
    {
        _policy = policy;
        _guard = guard;
        _audit = audit;
        _observers = observers.ToArray();
        _tools = new Dictionary<string, IGatewayTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (!NamePattern.IsMatch(tool.Name))
            {
                throw new InvalidOperationException($"Tool name '{tool.Name}' ({tool.GetType().FullName}) must match {NamePattern}.");
            }

            if (!_tools.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException(
                    $"Duplicate tool name '{tool.Name}': {_tools[tool.Name].GetType().FullName} and {tool.GetType().FullName}.");
            }
        }
    }

    public IReadOnlyCollection<IGatewayTool> Tools => _tools.Values;

    public bool TryGet(string name, out IGatewayTool tool) => _tools.TryGetValue(name, out tool!);

    public Task<ToolResult> InvokeAsync(string name, System.Text.Json.JsonElement? arguments, string sessionId, CancellationToken cancellationToken) =>
        InvokeAsync(new ToolCall(name, arguments, sessionId), cancellationToken);

    public async Task<ToolResult> InvokeAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            var unknown = ToolResult.Error(GatewayException.NotFound(
                $"Unknown tool '{call.Name}'. Call tools/list (or gateway_get_info) to see available tools."));
            Audit(call, null, unknown, sw.Elapsed);
            return unknown;
        }

        ToolResult result;
        var grantUsed = false;
        var budget = tool.Timeout;
        Task<ToolResult>? work = null;
        try
        {
            _policy.EnsureAllowed(tool);
            grantUsed = _guard.Check(tool, call);
            budget = tool.TimeoutFor(call);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(budget);
            using (Sandbox.EvidenceService.Enter(call))
            {
                work = tool.ExecuteAsync(call, timeoutCts.Token);
            }

            try
            {
                result = await work.WaitAsync(budget + TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ObserveLate(work, call);
                throw new GatewayException(
                    GatewayErrorCodes.Timeout,
                    $"Tool '{call.Name}' did not finish within {budget.TotalSeconds:0}s. It may still be running in Revit — check Revit with revit_list_instances / revit_list_dialogs before retrying.");
            }
        }
        catch (GatewayException gex)
        {
            result = ToolResult.Error(gex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The tool may still be running: observe its late outcome exactly as for a timeout.
            if (work is not null)
            {
                ObserveLate(work, call);
            }

            result = ToolResult.Error(new GatewayException(GatewayErrorCodes.Timeout, $"Call to '{call.Name}' was cancelled by the client."));
        }
        catch (OperationCanceledException)
        {
            result = ToolResult.Error(new GatewayException(
                GatewayErrorCodes.Timeout,
                $"Tool '{call.Name}' exceeded its {budget.TotalSeconds:0}s budget. It may still be running in Revit — check state before retrying."));
        }
        catch (Exception ex)
        {
            _audit.Diagnostic($"Tool {call.Name} (call {call.CallId}) failed", ex);
            result = ToolResult.Error(new GatewayException(
                GatewayErrorCodes.Internal,
                $"Tool '{call.Name}' failed unexpectedly ({ex.GetType().Name}). Details are in the gateway log (call id {call.CallId})."));
        }

        sw.Stop();
        Audit(call, tool, result, sw.Elapsed, grantUsed);
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnCompleted(call, tool, result, sw.Elapsed);
            }
            catch (Exception ex)
            {
                _audit.Diagnostic($"Observer {observer.GetType().Name} failed", ex);
            }
        }

        return result;
    }

    private void ObserveLate(Task<ToolResult> work, ToolCall call) =>
        work.ContinueWith(
            t => _audit.Diagnostic($"Tool {call.Name} (call {call.CallId}) finished after timeout: {t.Status}", t.Exception),
            TaskScheduler.Default);

    private void Audit(ToolCall call, IGatewayTool? tool, ToolResult result, TimeSpan elapsed, bool grantUsed = false) =>
        _audit.Record(new AuditEntry(
            DateTimeOffset.Now,
            call.CallId,
            call.SessionId,
            call.Name,
            tool?.Permission.ToString() ?? "unknown",
            AuditLog.SummarizeArgs(call.Arguments),
            !result.IsError,
            result.ErrorCode,
            (long)elapsed.TotalMilliseconds,
            GrantUsed: grantUsed));
}
