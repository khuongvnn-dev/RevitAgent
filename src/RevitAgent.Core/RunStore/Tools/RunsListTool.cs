using System.Globalization;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.RunStore.Tools;

/// <summary>
/// runs_list — list runs from index.jsonl plus folders without index lines (crash-safe).
/// </summary>
public sealed class RunsListTool : GatewayTool
{
    private readonly RunStore _runStore;

    public RunsListTool(RunStore runStore) => _runStore = runStore;

    public override string Name => "runs_list";

    public override string Description =>
        "List runs (newest first) from the run data store index and crash-safe folder scan: run id, suite, verdict, trigger, start/end timestamps, folder paths and report links. " +
        "Supports filtering by suite name (substring), verdict, and date range (from/to). Read-only.";

    public override ToolPermission Permission => ToolPermission.Read;

    protected override Schema BuildSchema() => Schema.Object()
        .String("suite", "Only runs whose suite name contains this text (case-insensitive substring).")
        .String("verdict", "Only runs with this verdict (e.g. PASS, FAIL, SUSPECT, ERROR, RUNNING).")
        .String("from", "Only runs started at or after this ISO-8601 date or timestamp (e.g. 2026-11-20 or 2026-11-20T00:00:00Z).")
        .String("to", "Only runs started at or before this ISO-8601 date or timestamp.")
        .Integer("limit", "Maximum runs to return (default 50, max 200).", defaultValue: 50, minimum: 1, maximum: 200);

    public override Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var limit = call.GetLimit(defaultValue: 50, max: 200);
        var suite = call.GetString("suite");
        var verdict = call.GetString("verdict");
        var fromStr = call.GetString("from");
        var toStr = call.GetString("to");

        DateTimeOffset? from = null;
        if (!string.IsNullOrWhiteSpace(fromStr) && DateTimeOffset.TryParse(fromStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fParsed))
        {
            from = fParsed;
        }

        DateTimeOffset? to = null;
        if (!string.IsNullOrWhiteSpace(toStr) && DateTimeOffset.TryParse(toStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var tParsed))
        {
            // If only a date was passed (time is midnight), include the full day
            if (tParsed.TimeOfDay == TimeSpan.Zero && !toStr!.Contains('T') && !toStr.Contains(':'))
            {
                to = tParsed.AddDays(1).AddTicks(-1);
            }
            else
            {
                to = tParsed;
            }
        }

        var filter = new RunFilter
        {
            Suite = suite,
            Verdict = verdict,
            From = from,
            To = to,
            Limit = limit,
        };

        var runs = _runStore.ListRuns(filter);
        return Task.FromResult(ToolResult.Json(new
        {
            root = _runStore.RootDir,
            total = runs.Count,
            returned = runs.Count,
            runs = runs.Select(r => new
            {
                id = r.Id,
                suite = r.Suite,
                trigger = r.Trigger,
                verdict = r.Verdict,
                started_at = r.StartedAt.ToString("O", CultureInfo.InvariantCulture),
                ended_at = r.EndedAt?.ToString("O", CultureInfo.InvariantCulture),
                run_dir = r.RunDir,
                report_html = r.ReportHtml,
                report_md = r.ReportMd,
                machine = r.Machine,
                user = r.User,
                revit_version = r.RevitVersion,
                counts = r.Counts,
            }).ToArray(),
        }));
    }
}
