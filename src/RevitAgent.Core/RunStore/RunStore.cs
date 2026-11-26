using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Policy;
using RevitAgent.Core.Sandbox;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.RunStore;

/// <summary>
/// Service managing scenario and sandbox run folders, run.json metadata, and index.jsonl.
/// Every run lives in &lt;root&gt;\&lt;yyyy-MM&gt;\&lt;yyyy-MM-dd&gt;\&lt;HHmmss&gt;_&lt;scenario-slug&gt;_&lt;id6&gt;\.
/// Nothing is ever deleted; existing run folders are never overwritten.
/// </summary>
public sealed class RunStore
{
    public const string RunJsonFile = "run.json";
    public const string IndexJsonlFile = "index.jsonl";
    public const string SourceShaFile = "source.sha256.txt";

    private static readonly Regex SafeIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly object Gate = new();

    private readonly AgentPaths _paths;
    private readonly GatewayPolicy _policy;

    public RunStore(AgentPaths paths, GatewayPolicy policy)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public string RootDir => RunStoreOptions.Load(_paths).GetEffectiveRoot(_paths);

    public string IndexFilePath => Path.Combine(RootDir, IndexJsonlFile);

    /// <summary>
    /// Creates a new run folder &lt;root&gt;\&lt;yyyy-MM&gt;\&lt;yyyy-MM-dd&gt;\&lt;HHmmss&gt;_&lt;slug&gt;_&lt;id6&gt;\
    /// with data\, evidence\, scenario\, model\, logs\ and writes initial run.json and run-info.json.
    /// Never reuses or overwrites an existing folder.
    /// </summary>
    public RunFolder CreateRun(
        string? scenario,
        string trigger = "manual",
        string? purpose = null,
        string? sessionId = null,
        int? revitYear = null,
        string? revitBuild = null,
        string? scenarioSha256 = null,
        string? previousRunId = null,
        string? rerunOf = null)
    {
        var now = DateTimeOffset.Now;
        var slug = FormatSlug(scenario);
        var root = RootDir;
        var monthFolder = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var dayFolder = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var timeStr = now.ToString("HHmmss", CultureInfo.InvariantCulture);

        string runId;
        string folderPath;
        string relativePath;

        lock (Gate)
        {
            var dayDir = Path.Combine(root, monthFolder, dayFolder);
            Directory.CreateDirectory(dayDir);

            do
            {
                var id6 = RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
                runId = $"{timeStr}_{slug}_{id6}";
                relativePath = Path.Combine(monthFolder, dayFolder, runId);
                folderPath = Path.Combine(root, relativePath);
            }
            while (Directory.Exists(folderPath));

            Directory.CreateDirectory(folderPath);
        }

        var dataDir = Directory.CreateDirectory(Path.Combine(folderPath, "data")).FullName;
        var evidenceDir = Directory.CreateDirectory(Path.Combine(folderPath, "evidence")).FullName;
        var scenarioDir = Directory.CreateDirectory(Path.Combine(folderPath, "scenario")).FullName;
        var modelDir = Directory.CreateDirectory(Path.Combine(folderPath, "model")).FullName;
        var logsDir = Directory.CreateDirectory(Path.Combine(folderPath, "logs")).FullName;

        // 1. Initial run.json
        var runObj = new JsonObject
        {
            ["id"] = runId,
            ["scenario"] = string.IsNullOrWhiteSpace(scenario) ? "run" : scenario.Trim(),
            ["trigger"] = string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger.Trim(),
            ["machine"] = Environment.MachineName,
            ["user"] = Environment.UserName,
            ["revit"] = new JsonObject
            {
                ["year"] = revitYear,
                ["build"] = revitBuild,
            },
            ["addins"] = new JsonArray(),
            ["gateway_version"] = Mcp.McpGatewayServer.Version,
            ["scenario_sha256"] = scenarioSha256,
            ["started_at"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["ended_at"] = null,
            ["verdict"] = null,
            ["counts"] = null,
        };
        if (!string.IsNullOrWhiteSpace(previousRunId))
        {
            runObj["previous_run_id"] = previousRunId;
        }

        if (!string.IsNullOrWhiteSpace(rerunOf))
        {
            runObj["rerun_of"] = rerunOf;
        }

        WriteJsonFile(Path.Combine(folderPath, RunJsonFile), runObj);

        // 2. Compatibility run-info.json for existing tools/code
        var infoObj = new JsonObject
        {
            ["run_id"] = runId,
            ["name"] = string.IsNullOrWhiteSpace(scenario) ? null : scenario.Trim(),
            ["purpose"] = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim(),
            ["created_at"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["session_id"] = sessionId,
            ["gateway_version"] = Mcp.McpGatewayServer.Version,
            ["copies"] = new JsonArray(),
        };
        if (!string.IsNullOrWhiteSpace(previousRunId))
        {
            infoObj["previous_run_id"] = previousRunId;
        }

        if (!string.IsNullOrWhiteSpace(rerunOf))
        {
            infoObj["rerun_of"] = rerunOf;
        }
        WriteJsonFile(Path.Combine(folderPath, SandboxService.RunInfoFile), infoObj);

        return new RunFolder
        {
            Id = runId,
            FolderPath = folderPath,
            RelativePath = relativePath,
            DataDir = dataDir,
            EvidenceDir = evidenceDir,
            ScenarioDir = scenarioDir,
            ModelDir = modelDir,
            LogsDir = logsDir,
            StartedAt = now,
            Scenario = scenario ?? "run",
            Trigger = trigger,
        };
    }

    /// <summary>
    /// Finalizes a run: updates run.json with final verdict, counts, ended_at,
    /// writes model\source.sha256.txt, extracts logs\audit.jsonl and bridge log,
    /// and appends a single line to index.jsonl with a file lock.
    /// </summary>
    public void FinishRun(string runDir, RunFinishInfo finishInfo)
    {
        if (string.IsNullOrWhiteSpace(runDir) || !Directory.Exists(runDir))
        {
            return;
        }

        var runJsonPath = Path.Combine(runDir, RunJsonFile);
        JsonObject runObj;
        try
        {
            runObj = File.Exists(runJsonPath)
                ? (JsonNode.Parse(File.ReadAllText(runJsonPath)) as JsonObject ?? new JsonObject())
                : new JsonObject();
        }
        catch
        {
            runObj = new JsonObject();
        }

        var runId = runObj["id"]?.GetValue<string>() ?? Path.GetFileName(runDir.TrimEnd('\\', '/'));
        var scenario = runObj["scenario"]?.GetValue<string>() ?? runObj["suite"]?.GetValue<string>() ?? "run"; // "suite" = legacy key
        var trigger = runObj["trigger"]?.GetValue<string>() ?? "manual";
        var startedAtStr = runObj["started_at"]?.GetValue<string>();
        var startedAt = DateTimeOffset.TryParse(startedAtStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var st)
            ? st
            : File.GetCreationTimeUtc(runDir);

        runObj["ended_at"] = finishInfo.EndedAt.ToString("O", CultureInfo.InvariantCulture);
        runObj["verdict"] = finishInfo.Verdict;

        if (finishInfo.Counts != null)
        {
            runObj["counts"] = JsonSerializer.SerializeToNode(finishInfo.Counts, GatewayJson.Options);
        }

        if (finishInfo.RevitYear != null || finishInfo.RevitBuild != null || finishInfo.Pid != null)
        {
            var revitObj = runObj["revit"] as JsonObject ?? new JsonObject();
            if (finishInfo.RevitYear != null) revitObj["year"] = finishInfo.RevitYear;
            if (finishInfo.RevitBuild != null) revitObj["build"] = finishInfo.RevitBuild;
            if (finishInfo.Pid != null) revitObj["pid"] = finishInfo.Pid;
            runObj["revit"] = revitObj;
        }

        if (finishInfo.Addins != null && finishInfo.Addins.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var a in finishInfo.Addins)
            {
                arr.Add(new JsonObject
                {
                    ["name"] = a.Name,
                    ["path"] = a.Path,
                    ["sha256"] = a.Sha256,
                });
            }
            runObj["addins"] = arr;
        }

        if (!string.IsNullOrWhiteSpace(finishInfo.PreviousRunId))
        {
            runObj["previous_run_id"] = finishInfo.PreviousRunId;
        }

        if (!string.IsNullOrWhiteSpace(finishInfo.RerunOf))
        {
            runObj["rerun_of"] = finishInfo.RerunOf;
        }

        WriteJsonFile(runJsonPath, runObj);

        // 1. Model source.sha256.txt verification
        WriteSourceShaVerification(runDir, finishInfo);

        // 2. Extract logs\audit.jsonl
        CollectAuditSlice(runDir, startedAt, finishInfo.EndedAt, finishInfo.SessionId);

        // 3. Collect bridge log if pid available
        if (finishInfo.Pid != null)
        {
            CollectBridgeLog(runDir, finishInfo.Pid.Value);
        }

        // 4. Append to index.jsonl
        var relativeRunDir = GetRelativePathSafe(RootDir, runDir);
        var reportHtmlPath = File.Exists(Path.Combine(runDir, "report.html"))
            ? GetRelativePathSafe(RootDir, Path.Combine(runDir, "report.html"))
            : null;
        var reportMdPath = File.Exists(Path.Combine(runDir, "report.md"))
            ? GetRelativePathSafe(RootDir, Path.Combine(runDir, "report.md"))
            : null;

        var indexEntry = new JsonObject
        {
            ["id"] = runId,
            ["scenario"] = scenario,
            ["trigger"] = trigger,
            ["verdict"] = finishInfo.Verdict,
            ["started_at"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
            ["ended_at"] = finishInfo.EndedAt.ToString("O", CultureInfo.InvariantCulture),
            ["run_dir"] = relativeRunDir,
            ["report_html"] = reportHtmlPath,
            ["report_md"] = reportMdPath,
            ["machine"] = Environment.MachineName,
            ["user"] = Environment.UserName,
            ["revit_version"] = finishInfo.RevitYear,
        };

        if (finishInfo.Counts != null)
        {
            indexEntry["counts"] = JsonSerializer.SerializeToNode(finishInfo.Counts, GatewayJson.Options);
        }

        AppendIndexLine(IndexFilePath, indexEntry.ToJsonString(new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip }));
    }

    /// <summary>
    /// Records a skipped run in index.jsonl (e.g. when nightly schedule is skipped due to failing machine readiness checks).
    /// Does not create or delete any folders on disk.
    /// </summary>
    public void RecordSkippedRun(string scenario, string trigger = "schedule", string? reason = null, int? revitYear = null)
    {
        var now = DateTimeOffset.Now;
        var slug = FormatSlug(scenario);
        var id6 = Guid.NewGuid().ToString("N")[..6];
        var runId = $"{now:HHmmss}_{slug}_{id6}";
        var relativeRunDir = Path.Combine(now.ToString("yyyy-MM", CultureInfo.InvariantCulture), now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), runId);

        var indexEntry = new JsonObject
        {
            ["id"] = runId,
            ["scenario"] = string.IsNullOrWhiteSpace(scenario) ? "run" : scenario.Trim(),
            ["trigger"] = string.IsNullOrWhiteSpace(trigger) ? "schedule" : trigger.Trim(),
            ["verdict"] = "SKIPPED",
            ["started_at"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["ended_at"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["run_dir"] = relativeRunDir,
            ["report_html"] = null,
            ["report_md"] = null,
            ["machine"] = Environment.MachineName,
            ["user"] = Environment.UserName,
            ["revit_version"] = revitYear,
            ["reason"] = reason,
        };

        AppendIndexLine(IndexFilePath, indexEntry.ToJsonString(new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip }));
    }

    /// <summary>
    /// Resolves the full directory path of a run by its id.
    /// Supports new layout (&lt;root&gt;\yyyy-MM\yyyy-MM-dd\&lt;runId&gt;),
    /// old layout (&lt;root&gt;\&lt;runId&gt;), or index-recorded path.
    /// </summary>
    public string ResolveRunDir(string runId)
    {
        var id = (runId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id) || id.Contains("..", StringComparison.Ordinal))
        {
            throw GatewayException.Validation($"run_id '{runId}' is not a valid run id.");
        }

        var root = RootDir;

        // Check if runId is already a relative path under root
        var directPath = Path.GetFullPath(Path.Combine(root, id));
        if (Directory.Exists(directPath) && directPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return directPath;
        }

        // Check old-layout directly under root: <root>\<runId>
        var oldLayout = Path.Combine(root, id);
        if (Directory.Exists(oldLayout))
        {
            return Path.GetFullPath(oldLayout);
        }

        // Check new layout for today: <root>\<yyyy-MM>\<yyyy-MM-dd>\<runId>
        var today = DateTimeOffset.Now;
        var todayNewLayout = Path.Combine(root, today.ToString("yyyy-MM", CultureInfo.InvariantCulture), today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), id);
        if (Directory.Exists(todayNewLayout))
        {
            return Path.GetFullPath(todayNewLayout);
        }

        // Check index.jsonl for recorded run_dir
        if (File.Exists(IndexFilePath))
        {
            try
            {
                foreach (var line in File.ReadLines(IndexFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var idProp) && string.Equals(idProp.GetString(), id, StringComparison.OrdinalIgnoreCase))
                    {
                        if (doc.RootElement.TryGetProperty("run_dir", out var dirProp) && dirProp.GetString() is { Length: > 0 } rel)
                        {
                            var resolved = Path.GetFullPath(Path.Combine(root, rel));
                            if (Directory.Exists(resolved))
                            {
                                return resolved;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fall through to directory search
            }
        }

        // Wildcard search: <root>\*\*\<runId>
        try
        {
            if (Directory.Exists(root))
            {
                foreach (var monthDir in Directory.EnumerateDirectories(root, "????-??"))
                {
                    foreach (var dayDir in Directory.EnumerateDirectories(monthDir, "????-??-??"))
                    {
                        var candidate = Path.Combine(dayDir, id);
                        if (Directory.Exists(candidate))
                        {
                            return Path.GetFullPath(candidate);
                        }
                    }
                }
            }
        }
        catch
        {
            // Fall through
        }

        // Fallback: check default paths.RunsDir if RootDir was changed
        if (!string.Equals(root, _paths.RunsDir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_paths.RunsDir))
        {
            var fallbackPath = Path.Combine(_paths.RunsDir, id);
            if (Directory.Exists(fallbackPath))
            {
                return Path.GetFullPath(fallbackPath);
            }
        }

        throw GatewayException.NotFound($"Run '{id}' does not exist in the run store ({root}).");
    }

    /// <summary>
    /// Lists runs newest first, reading index.jsonl plus any unindexed run folders (crash-safe).
    /// Supports old-layout run folders directly under root.
    /// </summary>
    public IReadOnlyList<RunSummary> ListRuns(RunFilter filter)
    {
        var runs = new List<RunSummary>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = RootDir;

        // 1. Read index.jsonl
        if (File.Exists(IndexFilePath))
        {
            try
            {
                foreach (var line in File.ReadLines(IndexFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var rootElem = doc.RootElement.Clone();
                        var id = rootElem.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
                        if (string.IsNullOrWhiteSpace(id)) continue;

                        var scenario = ReadScenarioName(rootElem) ?? string.Empty;
                        var trigger = rootElem.TryGetProperty("trigger", out var tProp) ? tProp.GetString() ?? "manual" : "manual";
                        var verdict = rootElem.TryGetProperty("verdict", out var vProp) ? vProp.GetString() ?? "UNKNOWN" : "UNKNOWN";
                        var startedStr = rootElem.TryGetProperty("started_at", out var stProp) ? stProp.GetString() : null;
                        var endedStr = rootElem.TryGetProperty("ended_at", out var endProp) ? endProp.GetString() : null;
                        var relDir = rootElem.TryGetProperty("run_dir", out var rdProp) ? rdProp.GetString() ?? id : id;
                        var fullDir = Path.GetFullPath(Path.Combine(root, relDir));
                        var rpHtml = rootElem.TryGetProperty("report_html", out var rphProp) ? rphProp.GetString() : null;
                        var rpMd = rootElem.TryGetProperty("report_md", out var rpmProp) ? rpmProp.GetString() : null;
                        var machine = rootElem.TryGetProperty("machine", out var mProp) ? mProp.GetString() : null;
                        var user = rootElem.TryGetProperty("user", out var uProp) ? uProp.GetString() : null;
                        var revitVer = rootElem.TryGetProperty("revit_version", out var rvProp) && rvProp.TryGetInt32(out var yr) ? yr : (int?)null;
                        var counts = rootElem.TryGetProperty("counts", out var cProp) ? cProp.Clone() : (JsonElement?)null;

                        var started = DateTimeOffset.TryParse(startedStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var sParsed)
                            ? sParsed
                            : File.GetCreationTimeUtc(fullDir);
                        var ended = DateTimeOffset.TryParse(endedStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var eParsed)
                            ? (DateTimeOffset?)eParsed
                            : null;

                        seenIds.Add(id);
                        seenDirs.Add(fullDir);

                        runs.Add(new RunSummary
                        {
                            Id = id,
                            Scenario = scenario,
                            Trigger = trigger,
                            Verdict = verdict,
                            StartedAt = started,
                            EndedAt = ended,
                            RunDir = fullDir,
                            ReportHtml = rpHtml != null ? Path.GetFullPath(Path.Combine(root, rpHtml)) : null,
                            ReportMd = rpMd != null ? Path.GetFullPath(Path.Combine(root, rpMd)) : null,
                            Machine = machine,
                            User = user,
                            RevitVersion = revitVer,
                            Counts = counts,
                        });
                    }
                    catch
                    {
                        // Ignore corrupted line
                    }
                }
            }
            catch
            {
                // Index read error
            }
        }

        // 2. Scan folders without index lines (in-flight runs, crashed runs, or old-layout folders)
        if (Directory.Exists(root))
        {
            // A) Old-layout folders directly under root
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.StartsWith("20", StringComparison.Ordinal) && dirName.Length == 7 && dirName.Contains('-'))
                    {
                        // This is a yyyy-MM folder for new layout; skip here
                        continue;
                    }

                    if (seenDirs.Contains(dir) || seenIds.Contains(dirName))
                    {
                        continue;
                    }

                    TryAddUnindexedFolder(dir, dirName, root, runs, seenIds, seenDirs);
                }
            }
            catch
            {
            }

            // B) New-layout folders: <root>\<yyyy-MM>\<yyyy-MM-dd>\<runId>
            try
            {
                foreach (var monthDir in Directory.EnumerateDirectories(root, "????-??"))
                {
                    foreach (var dayDir in Directory.EnumerateDirectories(monthDir, "????-??-??"))
                    {
                        foreach (var runFolder in Directory.EnumerateDirectories(dayDir))
                        {
                            var folderName = Path.GetFileName(runFolder);
                            if (seenDirs.Contains(runFolder) || seenIds.Contains(folderName))
                            {
                                continue;
                            }

                            TryAddUnindexedFolder(runFolder, folderName, root, runs, seenIds, seenDirs);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        // 3. Apply filters
        var query = runs.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.Scenario))
        {
            query = query.Where(r => r.Scenario.Contains(filter.Scenario, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.Verdict))
        {
            query = query.Where(r => string.Equals(r.Verdict, filter.Verdict, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.From.HasValue)
        {
            query = query.Where(r => r.StartedAt >= filter.From.Value);
        }

        if (filter.To.HasValue)
        {
            query = query.Where(r => r.StartedAt <= filter.To.Value);
        }

        return query
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id, StringComparer.Ordinal)
            .Take(filter.Limit > 0 ? filter.Limit : 50)
            .ToArray();
    }

    /// <summary>
    /// Gets full metadata (run.json) and a list of all files with sizes for a run.
    /// </summary>
    public RunDetails GetRun(string runId)
    {
        var runDir = ResolveRunDir(runId);
        var runJsonPath = Path.Combine(runDir, RunJsonFile);
        var runInfoPath = Path.Combine(runDir, SandboxService.RunInfoFile);

        JsonElement runData;
        if (File.Exists(runJsonPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(runJsonPath));
                runData = doc.RootElement.Clone();
            }
            catch
            {
                runData = JsonSerializer.SerializeToElement(new { id = runId, error = "Corrupt run.json" });
            }
        }
        else if (File.Exists(runInfoPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(runInfoPath));
                runData = doc.RootElement.Clone();
            }
            catch
            {
                runData = JsonSerializer.SerializeToElement(new { id = runId, error = "Corrupt run-info.json" });
            }
        }
        else
        {
            runData = JsonSerializer.SerializeToElement(new { id = runId });
        }

        var files = new List<RunFileInfo>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(runDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(runDir, f);
                var size = new FileInfo(f).Length;
                files.Add(new RunFileInfo(rel, size));
            }
        }
        catch
        {
            // Partial file listing on permission issue
        }

        return new RunDetails
        {
            RunId = runId,
            RunDir = runDir,
            Run = runData,
            Files = files.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    public static string FormatSlug(string? name) => SandboxService.Slug(name);

    /// <summary>Reads the scenario name from a run.json / index.jsonl record; accepts the legacy "suite" key written by older runs.</summary>
    private static string? ReadScenarioName(JsonElement root)
    {
        foreach (var key in new[] { "scenario", "suite" })
        {
            if (root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString();
        }
        return null;
    }

    private static void TryAddUnindexedFolder(string dir, string folderName, string root, List<RunSummary> runs, HashSet<string> seenIds, HashSet<string> seenDirs)
    {
        var runJson = Path.Combine(dir, RunJsonFile);
        var runInfo = Path.Combine(dir, SandboxService.RunInfoFile);

        if (!File.Exists(runJson) && !File.Exists(runInfo))
        {
            return;
        }

        seenIds.Add(folderName);
        seenDirs.Add(dir);

        string scenario = folderName;
        string trigger = "manual";
        string verdict = "RUNNING";
        DateTimeOffset started = File.GetCreationTimeUtc(dir);
        DateTimeOffset? ended = null;
        string? machine = null;
        string? user = null;
        int? revitVer = null;
        JsonElement? counts = null;

        if (File.Exists(runJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(runJson));
                var rootElem = doc.RootElement;
                scenario = ReadScenarioName(rootElem) ?? scenario;
                trigger = rootElem.TryGetProperty("trigger", out var tp) ? tp.GetString() ?? trigger : trigger;
                verdict = rootElem.TryGetProperty("verdict", out var vp) && vp.ValueKind == JsonValueKind.String ? vp.GetString() ?? "RUNNING" : "RUNNING";
                if (rootElem.TryGetProperty("started_at", out var stp) && DateTimeOffset.TryParse(stp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var st))
                {
                    started = st;
                }
                if (rootElem.TryGetProperty("ended_at", out var ep) && DateTimeOffset.TryParse(ep.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var et))
                {
                    ended = et;
                }
                machine = rootElem.TryGetProperty("machine", out var mp) ? mp.GetString() : null;
                user = rootElem.TryGetProperty("user", out var up) ? up.GetString() : null;
                if (rootElem.TryGetProperty("revit", out var rp) && rp.TryGetProperty("year", out var yp) && yp.TryGetInt32(out var y))
                {
                    revitVer = y;
                }
                if (rootElem.TryGetProperty("counts", out var cp))
                {
                    counts = cp.Clone();
                }
            }
            catch
            {
            }
        }
        else if (File.Exists(runInfo))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(runInfo));
                var rootElem = doc.RootElement;
                scenario = ReadScenarioName(rootElem) ?? (rootElem.TryGetProperty("name", out var np) ? np.GetString() ?? scenario : scenario);
                verdict = rootElem.TryGetProperty("verdict", out var vp) ? vp.GetString() ?? "UNKNOWN" : "UNKNOWN";
                var startedStr = rootElem.TryGetProperty("started", out var stp) ? stp.GetString() : rootElem.TryGetProperty("created_at", out var cp) ? cp.GetString() : null;
                if (startedStr != null && DateTimeOffset.TryParse(startedStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var st))
                {
                    started = st;
                }
                if (rootElem.TryGetProperty("ended", out var ep) && DateTimeOffset.TryParse(ep.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var et))
                {
                    ended = et;
                }
                machine = rootElem.TryGetProperty("machine", out var mp) ? mp.GetString() : null;
            }
            catch
            {
            }
        }

        var reportHtml = File.Exists(Path.Combine(dir, "report.html")) ? Path.Combine(dir, "report.html") : null;
        var reportMd = File.Exists(Path.Combine(dir, "report.md")) ? Path.Combine(dir, "report.md") : null;

        runs.Add(new RunSummary
        {
            Id = folderName,
            Scenario = scenario,
            Trigger = trigger,
            Verdict = verdict,
            StartedAt = started,
            EndedAt = ended,
            RunDir = Path.GetFullPath(dir),
            ReportHtml = reportHtml,
            ReportMd = reportMd,
            Machine = machine,
            User = user,
            RevitVersion = revitVer,
            Counts = counts,
        });
    }

    private static void AppendIndexLine(string indexPath, string jsonLine)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonLine.TrimEnd('\r', '\n') + "\n");
        for (var i = 0; i < 15; i++)
        {
            try
            {
                using var fs = new FileStream(
                    indexPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
                return;
            }
            catch (IOException) when (i < 14)
            {
                Thread.Sleep(20 + Random.Shared.Next(30));
            }
        }
    }

    private static void WriteJsonFile(string path, JsonObject obj)
    {
        var tmp = path + ".tmp";
        var json = obj.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private static string GetRelativePathSafe(string relativeTo, string path)
    {
        try
        {
            return Path.GetRelativePath(relativeTo, path);
        }
        catch
        {
            return path;
        }
    }

    private static void WriteSourceShaVerification(string runDir, RunFinishInfo finishInfo)
    {
        if (finishInfo.ModelSource == null && finishInfo.ModelShaBefore == null)
        {
            return;
        }

        try
        {
            var modelDir = Directory.CreateDirectory(Path.Combine(runDir, "model")).FullName;
            var shaFile = Path.Combine(modelDir, SourceShaFile);
            var sb = new StringBuilder();
            sb.AppendLine($"# RevitAgent Model Source SHA-256 Verification");
            sb.AppendLine($"source: {finishInfo.ModelSource ?? "(unknown)"}");
            sb.AppendLine($"sha256_before: {finishInfo.ModelShaBefore ?? "(none)"}");
            sb.AppendLine($"sha256_after: {finishInfo.ModelShaAfter ?? finishInfo.ModelShaBefore ?? "(none)"}");
            sb.AppendLine($"source_unchanged: {(finishInfo.ModelUnchanged ? "true" : "false")}");
            sb.AppendLine($"verified_at: {finishInfo.EndedAt:O}");
            File.WriteAllText(shaFile, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // Best effort
        }
    }

    private void CollectAuditSlice(string runDir, DateTimeOffset started, DateTimeOffset ended, string? sessionId)
    {
        try
        {
            var auditDir = _paths.AuditDir;
            if (!Directory.Exists(auditDir)) return;

            var logsDir = Directory.CreateDirectory(Path.Combine(runDir, "logs")).FullName;
            var destAudit = Path.Combine(logsDir, "audit.jsonl");

            var matchedLines = new List<string>();
            var dayFiles = Directory.EnumerateFiles(auditDir, "??????.jsonl").Concat(Directory.EnumerateFiles(auditDir, "????????.jsonl"));
            foreach (var f in dayFiles)
            {
                foreach (var line in File.ReadLines(f))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var elem = doc.RootElement;
                        var atStr = elem.TryGetProperty("At", out var atp) ? atp.GetString() : elem.TryGetProperty("at", out var atp2) ? atp2.GetString() : null;
                        var sess = elem.TryGetProperty("SessionId", out var sp) ? sp.GetString() : elem.TryGetProperty("session_id", out var sp2) ? sp2.GetString() : null;

                        var matchSession = sessionId != null && sess != null && (sess.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase) || sessionId.StartsWith(sess, StringComparison.OrdinalIgnoreCase));
                        var matchTime = false;
                        if (atStr != null && DateTimeOffset.TryParse(atStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                        {
                            // Include a small 5-second buffer on either side
                            matchTime = at >= started.AddSeconds(-5) && at <= ended.AddSeconds(5);
                        }

                        if (matchSession || matchTime)
                        {
                            matchedLines.Add(line);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (matchedLines.Count > 0)
            {
                File.WriteAllLines(destAudit, matchedLines, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Best effort
        }
    }

    private void CollectBridgeLog(string runDir, int pid)
    {
        try
        {
            var srcBridgeLog = Path.Combine(_paths.LogDir, $"bridge-{pid}.log");
            if (File.Exists(srcBridgeLog))
            {
                var logsDir = Directory.CreateDirectory(Path.Combine(runDir, "logs")).FullName;
                var dst = Path.Combine(logsDir, $"bridge-{pid}.log");
                File.Copy(srcBridgeLog, dst, overwrite: true);
            }
        }
        catch
        {
            // Best effort
        }
    }
}
