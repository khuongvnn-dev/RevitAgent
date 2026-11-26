using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RevitAgent.Contracts.Bridge;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Composition;
using RevitAgent.Core.Install;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Revit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Bridge;

/// <summary>
/// Gateway tool generated from a <see cref="BridgeToolDefinition"/>: validates <c>pid</c>, runs the
/// optional Host-side Prepare hook (e.g. sandbox check for doc_open), checks that the target document of a
/// model-changing tool is a sandbox copy (<see cref="BridgeToolDefinition.SandboxDocument"/>) and forwards the remaining arguments
/// to the Bridge in that Revit. Not auto-discovered (it is registered per catalog entry by <see cref="BridgeModule"/>).
/// </summary>
internal sealed class BridgeForwardingTool : IGatewayTool
{
    private readonly BridgeToolDefinition _definition;
    private readonly BridgeClient _client;
    private readonly BridgeToolServices _services;
    private readonly IRevitReadiness? _readiness;

    public BridgeForwardingTool(BridgeToolDefinition definition, BridgeClient client, BridgeToolServices services, IRevitReadiness? readiness = null)
    {
        _definition = definition;
        _client = client;
        _services = services;
        _readiness = readiness;
        InputSchema = definition.Arguments(
            Schema.Object().Integer("pid", "Revit pid (revit_list_instances).", required: true, minimum: 1)).Build();
    }

    public string Name => _definition.Name;

    public string Description => _definition.Description;

    public ToolPermission Permission => _definition.Permission;

    public JsonElement InputSchema { get; }

    public TimeSpan Timeout => TimeSpan.FromSeconds(_definition.TimeoutSeconds);

    /// <summary>How often <see cref="BridgeToolDefinition.WatchDialogs"/> looks for rule-matched dialogs (tests shorten it).</summary>
    internal TimeSpan DialogWatchInterval { get; init; } = TimeSpan.FromSeconds(1.5);

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var pid = call.RequireInt("pid");
        var args = BridgeToolCatalog.ForwardedArgs(call.Arguments);
        if (_definition.Prepare is not null)
        {
            args = _definition.Prepare(call, args, _services);
        }

        if (_definition.SandboxDocument)
        {
            args = await PinSandboxDocumentAsync(pid, args, cancellationToken).ConfigureAwait(false);
        }

        var forwarded = JsonSerializer.SerializeToElement(args);
        var handled = new List<HandledDialog>();
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcher = _definition.WatchDialogs && _readiness is not null
            ? WatchDialogsAsync(pid, handled, watchCts.Token)
            : Task.CompletedTask;
        JsonElement result;
        try
        {
            result = await _client.CallAsync(pid, Name, forwarded, Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayException ex)
        {
            watchCts.Cancel();
            await watcher.ConfigureAwait(false);
            if (handled.Count == 0)
            {
                throw;
            }

            // The call failed after the gateway answered dialogs (e.g. a safe-dismissed Cancel aborted the open): say which.
            throw new GatewayException(ex.Code, ex.Message, new { error_details = ex.Details, handled_dialogs = handled.ToArray() }, ex);
        }
        finally
        {
            watchCts.Cancel();
            await watcher.ConfigureAwait(false);
        }

        if (_definition.After is not null)
        {
            result = _definition.After(pid, result);
        }

        NoteEvidence(call, pid, args);

        if (handled.Count > 0 && JsonNode.Parse(result.GetRawText()) is JsonObject obj)
        {
            obj["handled_dialogs"] = JsonSerializer.SerializeToNode(handled, GatewayJson.Options);
            result = JsonSerializer.SerializeToElement(obj);
        }

        return ToolResult.Json(result);
    }

    /// <summary>
    /// Model-changing tools work on sandbox copies only. The Bridge does not know the sandbox root, so the gateway asks it
    /// (doc_info) which document the <c>document</c> argument resolves to and checks that file: the document's path, or for a
    /// copy detached from central (no path until saved) the sandbox file doc_open opened it from (<c>opened_from</c>).
    /// <c>document</c> is then pinned to that full path, which the Bridge matches exactly, so the call cannot land on
    /// another document (e.g. the user's original, which has the same title as its copy).
    /// </summary>
    private async Task<JsonObject> PinSandboxDocumentAsync(int pid, JsonObject args, CancellationToken cancellationToken)
    {
        if (!_services.Policy.EnforceSandbox)
        {
            return args;
        }

        var probe = new JsonObject();
        if (args["document"] is JsonNode key)
        {
            probe["document"] = key.DeepClone();
        }

        var info = await _client.CallAsync(pid, BridgeToolNames.DocInfo, JsonSerializer.SerializeToElement(probe), DocumentProbeBudget, cancellationToken).ConfigureAwait(false);
        var title = Text(info, "title") ?? "(untitled)";
        var file = Text(info, "path") ?? Text(info, "opened_from");
        if (file is null)
        {
            throw new GatewayException(
                GatewayErrorCodes.SandboxViolation,
                $"{Name} changes the model and works on sandbox copies only. '{title}' has no file path (never saved, or not opened by doc_open), so it cannot be verified as a sandbox copy. " +
                "Copy the model with sandbox_copy_model, open the copy with doc_open and pass it as document.",
                new { document = title });
        }

        if (!_services.Policy.IsInSandbox(file, _services.Paths))
        {
            throw new GatewayException(
                GatewayErrorCodes.SandboxViolation,
                $"{Name} changes the model and works on sandbox copies only: '{file}' is outside the gateway sandbox. " +
                "Copy the model with sandbox_copy_model, open the copy with doc_open and pass it as document.",
                new { document = title, path = file });
        }

        args["document"] = Path.GetFullPath(file);
        return args;
    }

    /// <summary>
    /// After a successful call: doc_open of a model inside a run makes that run current for the session and the pid;
    /// view_export_image files are added to the evidence index when they landed in a run's evidence folder.
    /// </summary>
    private void NoteEvidence(ToolCall call, int pid, JsonObject args)
    {
        var evidence = _services.Evidence;
        if (evidence is null)
        {
            return;
        }

        if (Name == BridgeToolNames.DocOpen && args["path"]?.GetValue<string>() is { Length: > 0 } model)
        {
            evidence.NoteModelOpened(call.SessionId, pid, model);
        }
        else if (Name == BridgeToolNames.ViewExportImage && args["save_path"]?.GetValue<string>() is { Length: > 0 } shot)
        {
            evidence.Record(shot, "screenshot", Name, call.SessionId, pid, call.CallId);
        }
    }

    /// <summary>Budget of the doc_info call that resolves the target document of a <see cref="BridgeToolDefinition.SandboxDocument"/> tool.</summary>
    internal static readonly TimeSpan DocumentProbeBudget = TimeSpan.FromSeconds(30);

    private static string? Text(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    /// <summary>
    /// While the Bridge call waits on Revit (e.g. a model opening), answers informational dialogs that match a rule — the
    /// same rules as the readiness gate — and, in a Revit this gateway launched, presses the safe-dismiss button (Close /
    /// Cancel / Later, <see cref="SafeDismissPolicy"/>) of a harmless dialog in the way. Forbidden dialogs are never touched.
    /// At most two tries per dialog window.
    /// </summary>
    private async Task WatchDialogsAsync(int pid, List<HandledDialog> handled, CancellationToken ct)
    {
        var attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DialogWatchInterval, ct).ConfigureAwait(false);
                var skip = attempts.Where(a => a.Value >= 2).Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var h in await Task.Run(() => _readiness!.AnswerRuledDialogs(pid, skip), ct).ConfigureAwait(false))
                {
                    attempts[h.Hwnd] = attempts.GetValueOrDefault(h.Hwnd) + 1;
                    handled.Add(h);
                    skip.Add(h.Hwnd); // a rule just answered it: not safe-dismissed in the same pass
                }

                foreach (var h in await Task.Run(() => _readiness!.SafeDismissDialogs(pid, Name, skip), ct).ConfigureAwait(false))
                {
                    attempts[h.Hwnd] = attempts.GetValueOrDefault(h.Hwnd) + 1;
                    handled.Add(h);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort: a failed look never fails the Bridge call; try again on the next tick.
            }
        }
    }
}

/// <summary>Registers the Bridge client, the installer and one forwarding tool per <see cref="BridgeToolCatalog"/> entry.</summary>
public sealed class BridgeModule : IGatewayModule
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<IRevitProcessProbe, SystemRevitProcessProbe>();
        services.TryAddSingleton(new BridgeClientOptions());
        services.TryAddSingleton<BridgeClient>();
        services.TryAddSingleton<Sandbox.EvidenceService>();
        services.TryAddSingleton(sp => new BridgeToolServices(sp.GetRequiredService<GatewayPolicy>(), sp.GetRequiredService<AgentPaths>(), sp.GetRequiredService<Sandbox.EvidenceService>()));
        services.TryAddSingleton(sp => BridgeInstallEnvironment.ForCurrentUser(sp.GetRequiredService<AgentPaths>(), sp.GetRequiredService<IRevitProcessProbe>()));
        services.TryAddSingleton<BridgeInstaller>();

        foreach (var definition in BridgeToolCatalog.All)
        {
            services.AddSingleton<IGatewayTool>(sp => new BridgeForwardingTool(
                definition,
                sp.GetRequiredService<BridgeClient>(),
                sp.GetRequiredService<BridgeToolServices>(),
                sp.GetService<IRevitReadiness>()));
        }
    }
}
