using RevitAgent.Core;
using RevitAgent.Core.Policy;
using RevitAgent.Core.RunStore;
using RevitAgent.Core.Scenario;

namespace RevitAgent.E2E;

/// <summary>
/// Where an E2E invocation writes its evidence. Default: a normal gateway run folder (RunStore, honours
/// REVITAGENT_HOME and config\run-store.json) so the run shows up in runs_list and the dashboard history.
/// An explicit --evidence directory is used as is, with no run record.
/// </summary>
internal sealed class EvidenceRun
{
    private readonly RunStore? _store;

    private EvidenceRun(string evidenceDir, string? runDir, RunStore? store)
    {
        EvidenceDir = evidenceDir;
        RunDir = runDir;
        _store = store;
    }

    public string EvidenceDir { get; }

    /// <summary>Run folder (null when --evidence was given explicitly).</summary>
    public string? RunDir { get; }

    public static EvidenceRun Create(string? explicitDir, string suite, string purpose)
    {
        if (!string.IsNullOrWhiteSpace(explicitDir))
        {
            Directory.CreateDirectory(explicitDir);
            return new EvidenceRun(explicitDir, null, null);
        }

        var store = new RunStore(new AgentPaths(), new GatewayPolicy());
        var run = store.CreateRun(suite, trigger: "cli", purpose: purpose);
        Console.WriteLine($"run folder: {run.FolderPath}");
        return new EvidenceRun(run.EvidenceDir, run.FolderPath, store);
    }

    /// <summary>Completes the run with PASS or FAIL so it appears in runs_list / dashboard history.</summary>
    public void Finish(bool ok)
    {
        if (_store is null || RunDir is null)
        {
            return;
        }

        try
        {
            _store.FinishRun(RunDir, new RunFinishInfo
            {
                EndedAt = DateTimeOffset.Now,
                Verdict = ok ? Verdicts.Pass : Verdicts.Fail,
                ModelUnchanged = true,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not finish run record: {ex.Message}");
        }
    }
}
