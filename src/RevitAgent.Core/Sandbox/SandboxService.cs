using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RevitAgent.Contracts.Protocol;
using RevitAgent.Core.Policy;
using RevitAgent.Core.RunStore;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Sandbox;

/// <summary>A run folder under the sandbox (RUN_DIR).</summary>
public sealed record SandboxRun
{
    public string RunId { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string DataDir { get; init; } = string.Empty;

    public string EvidenceDir { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string? Name { get; init; }

    public string? Purpose { get; init; }
}

/// <summary>One verified copy of a file into a run.</summary>
public sealed record SandboxCopy
{
    public string Source { get; init; } = string.Empty;

    public string CopyPath { get; init; } = string.Empty;

    public long Bytes { get; init; }

    public string Sha256SourceBefore { get; init; } = string.Empty;

    public string Sha256SourceAfter { get; init; } = string.Empty;

    public string Sha256Copy { get; init; } = string.Empty;

    /// <summary>The original's hash is identical before and after the copy (proof it was not modified).</summary>
    public bool SourceUnchanged { get; init; }

    public DateTimeOffset CopiedAt { get; init; }
}

public sealed record SandboxCopyResult
{
    public string RunId { get; init; } = string.Empty;

    public string RunDir { get; init; } = string.Empty;

    public bool RunCreated { get; init; }

    /// <summary>The model copy to open with doc_open.</summary>
    public string CopyPath { get; init; } = string.Empty;

    /// <summary>Original model path (full).</summary>
    public string SourcePath => Copies.Count > 0 ? Copies[0].Source : string.Empty;

    /// <summary>SHA-256 of the original model before the copy.</summary>
    public string SourceSha256Before => Copies.Count > 0 ? Copies[0].Sha256SourceBefore : string.Empty;

    /// <summary>SHA-256 of the original model after the copy (equal to before: the original was not modified).</summary>
    public string SourceSha256After => Copies.Count > 0 ? Copies[0].Sha256SourceAfter : string.Empty;

    /// <summary>SHA-256 of the model copy (equal to the source).</summary>
    public string CopySha256 => Copies.Count > 0 ? Copies[0].Sha256Copy : string.Empty;

    /// <summary>The model and every extra file, with their hashes.</summary>
    public IReadOnlyList<SandboxCopy> Copies { get; init; } = Array.Empty<SandboxCopy>();

    public bool OriginalUnchanged { get; init; }

    public string NextStep { get; init; } = string.Empty;
}

public sealed record SandboxRunSummary
{
    public string RunId { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public DateTimeOffset? CreatedAt { get; init; }

    public string? Name { get; init; }

    public string? Purpose { get; init; }

    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();

    public int CopyCount { get; init; }
}

/// <summary>
/// The gateway sandbox: every model an agent opens is a copy inside <c>%REVITAGENT_HOME%\runs\&lt;run_id&gt;\</c>
/// (the original is hashed before and after the run to prove it was not touched). Sources are only read;
/// every write is checked to stay inside the runs folder.
/// </summary>
public sealed class SandboxService
{
    public const string RunInfoFile = "run-info.json";

    public static readonly IReadOnlyList<string> ModelExtensions = new[] { ".rvt", ".rfa", ".rte", ".rft" };

    /// <summary>What extra_files accepts: models/links, shared parameters, data tables, CAD/IFC links, images.</summary>
    public static readonly IReadOnlyList<string> ExtraFileExtensions = new[]
    {
        ".rvt", ".rfa", ".rte", ".rft", ".txt", ".csv", ".tsv", ".xlsx", ".xls", ".json", ".xml", ".yaml", ".yml",
        ".ifc", ".dwg", ".dxf", ".dgn", ".sat", ".skp", ".nwc", ".nwd", ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff",
    };

    private static readonly Regex RunIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly object Gate = new();

    private readonly AgentPaths _paths;
    private readonly GatewayPolicy _policy;
    private readonly RunStore.RunStore? _runStore;

    public SandboxService(AgentPaths paths, GatewayPolicy policy, RunStore.RunStore? runStore = null)
    {
        _paths = paths;
        _policy = policy;
        _runStore = runStore;
    }

    public string RunsDir => _runStore?.RootDir ?? _paths.RunsDir;

    /// <summary>Creates a run folder with <c>data\</c>, <c>evidence\</c>, <c>model\</c>, <c>scenario\</c>, <c>logs\</c> and <c>run-info.json</c>.</summary>
    public SandboxRun CreateRun(string? name, string? purpose = null, string? sessionId = null)
    {
        if (_runStore != null)
        {
            var storeRun = _runStore.CreateRun(name ?? "run", trigger: "agent", purpose: purpose, sessionId: sessionId);
            return new SandboxRun
            {
                RunId = storeRun.Id,
                Path = storeRun.FolderPath,
                DataDir = storeRun.DataDir,
                EvidenceDir = storeRun.EvidenceDir,
                CreatedAt = storeRun.StartedAt,
                Name = storeRun.Suite,
                Purpose = purpose,
            };
        }

        var now = DateTimeOffset.Now;
        var slug = Slug(name);
        lock (Gate)
        {
            var baseId = $"{now:yyyyMMdd-HHmmss}-{slug}";
            var runId = baseId;
            for (var n = 2; Directory.Exists(System.IO.Path.Combine(RunsDir, runId)); n++)
            {
                runId = $"{baseId}-{n}";
            }

            var dir = EnsureUnderRuns(System.IO.Path.Combine(RunsDir, runId), "Run folder");
            Directory.CreateDirectory(dir);
            var data = Directory.CreateDirectory(System.IO.Path.Combine(dir, "data")).FullName;
            var evidence = Directory.CreateDirectory(System.IO.Path.Combine(dir, "evidence")).FullName;
            var info = new JsonObject
            {
                ["run_id"] = runId,
                ["name"] = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                ["purpose"] = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim(),
                ["created_at"] = now.ToString("o", CultureInfo.InvariantCulture),
                ["session_id"] = sessionId,
                ["gateway_version"] = Mcp.McpGatewayServer.Version,
                ["copies"] = new JsonArray(),
            };
            WriteRunInfo(dir, info);
            return new SandboxRun { RunId = runId, Path = dir, DataDir = data, EvidenceDir = evidence, CreatedAt = now, Name = info["name"]?.GetValue<string>(), Purpose = info["purpose"]?.GetValue<string>() };
        }
    }

    /// <summary>Full path of an existing run; VALIDATION_FAILED for ids that are not a single safe folder name.</summary>
    public string ResolveRunDir(string runId)
    {
        if (_runStore != null)
        {
            return _runStore.ResolveRunDir(runId);
        }

        var id = (runId ?? string.Empty).Trim();
        if (!RunIdPattern.IsMatch(id) || id.Contains("..", StringComparison.Ordinal))
        {
            throw GatewayException.Validation($"run_id '{runId}' is not a valid run id (a folder name under the runs folder, e.g. 20261120-031500-my-test). Use sandbox_list_runs.");
        }

        var dir = EnsureUnderRuns(System.IO.Path.Combine(RunsDir, id), "Run folder");
        if (!Directory.Exists(dir))
        {
            throw GatewayException.NotFound($"Run '{id}' does not exist. Create one with sandbox_create_run or list them with sandbox_list_runs.");
        }

        return dir;
    }

    /// <summary>The run's evidence folder (created when missing).</summary>
    public string EvidenceDir(string runId) => Directory.CreateDirectory(System.IO.Path.Combine(ResolveRunDir(runId), "evidence")).FullName;

    /// <summary>
    /// Copies a model (+ optional extra files such as links) into a run, proving with SHA-256 before/after that the
    /// original was not modified and that the copy is identical. The source is only read.
    /// </summary>
    public SandboxCopyResult CopyModel(string sourcePath, string? runId, IReadOnlyList<string>? extraFiles, string? name, string? sessionId, CancellationToken cancellationToken)
    {
        var source = ValidateSource(sourcePath, "source_path", requireModel: true);
        var extras = (extraFiles ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => ValidateSource(f, "extra_files", requireModel: false)).ToList();

        var created = false;
        string dir;
        string id;
        if (!string.IsNullOrWhiteSpace(runId))
        {
            dir = ResolveRunDir(runId);
            id = System.IO.Path.GetFileName(dir);
        }
        else
        {
            var run = CreateRun(name ?? System.IO.Path.GetFileNameWithoutExtension(source), "model copy", sessionId);
            dir = run.Path;
            id = run.RunId;
            created = true;
        }

        var copies = new List<SandboxCopy>();
        foreach (var file in extras.Prepend(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            copies.Add(CopyOne(file, dir, cancellationToken));
        }

        lock (Gate)
        {
            var info = ReadRunInfo(dir) ?? new JsonObject { ["run_id"] = id };
            var arr = info["copies"] as JsonArray ?? new JsonArray();
            foreach (var c in copies)
            {
                arr.Add(JsonSerializer.SerializeToNode(c, GatewayJson.Options));
            }

            info["copies"] = arr;
            WriteRunInfo(dir, info);
        }

        return new SandboxCopyResult
        {
            RunId = id,
            RunDir = dir,
            RunCreated = created,
            CopyPath = copies[0].CopyPath,
            Copies = copies,
            OriginalUnchanged = copies.All(c => c.SourceUnchanged),
            NextStep = $"Open the copy (never the original): doc_open with path '{copies[0].CopyPath}'. Save evidence under '{System.IO.Path.Combine(dir, "evidence")}'.",
        };
    }

    /// <summary>Runs newest first.</summary>
    public IReadOnlyList<SandboxRunSummary> ListRuns(int limit, string? contains = null)
    {
        if (_runStore != null)
        {
            var storeRuns = _runStore.ListRuns(new RunFilter { Suite = contains, Limit = limit });
            var result = new List<SandboxRunSummary>();
            foreach (var r in storeRuns)
            {
                var info = ReadRunInfo(r.RunDir);
                var copies = (info?["copies"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
                result.Add(new SandboxRunSummary
                {
                    RunId = r.Id,
                    Path = r.RunDir,
                    CreatedAt = r.StartedAt,
                    Name = r.Suite,
                    Purpose = info?["purpose"].AsString(),
                    Models = copies.Select(c => c["copy_path"].AsString())
                        .Where(p => p is not null && ModelExtensions.Contains(System.IO.Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                        .Select(p => p!).ToArray(),
                    CopyCount = copies.Count,
                });
            }
            return result;
        }

        var root = new DirectoryInfo(RunsDir);
        var runs = new List<SandboxRunSummary>();
        foreach (var d in root.EnumerateDirectories())
        {
            if (!string.IsNullOrWhiteSpace(contains) && !d.Name.Contains(contains, StringComparison.OrdinalIgnoreCase))
            {
                var nameMatch = ReadRunInfo(d.FullName)?["name"].AsString()?.Contains(contains, StringComparison.OrdinalIgnoreCase) == true;
                if (!nameMatch)
                {
                    continue;
                }
            }

            var info = ReadRunInfo(d.FullName);
            DateTimeOffset? createdAt = null;
            if (info?["created_at"].AsString() is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                createdAt = parsed;
            }

            var copies = (info?["copies"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            runs.Add(new SandboxRunSummary
            {
                RunId = d.Name,
                Path = d.FullName,
                CreatedAt = createdAt ?? new DateTimeOffset(d.CreationTimeUtc, TimeSpan.Zero),
                Name = info?["name"].AsString(),
                Purpose = info?["purpose"].AsString(),
                Models = copies.Select(c => c["copy_path"].AsString())
                    .Where(p => p is not null && ModelExtensions.Contains(System.IO.Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                    .Select(p => p!).ToArray(),
                CopyCount = copies.Count,
            });
        }

        return runs.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.RunId, StringComparer.Ordinal).Take(limit).ToArray();
    }

    public static string Sha256(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>"Wall Check #1" → "wall-check-1" (ASCII, diacritics stripped incl. Vietnamese đ, ≤ 40 chars, default "run").</summary>
    public static string Slug(string? name)
    {
        var s = (name ?? string.Empty).Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : ch is >= 'A' and <= 'Z' ? char.ToLowerInvariant(ch) : '-');
        }

        var slug = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        if (slug.Length > 40)
        {
            slug = slug[..40].Trim('-');
        }

        return slug.Length == 0 ? "run" : slug;
    }

    private SandboxCopy CopyOne(string file, string runDir, CancellationToken cancellationToken)
    {
        var targetDir = Directory.Exists(System.IO.Path.Combine(runDir, "model"))
            ? System.IO.Path.Combine(runDir, "model")
            : runDir;
        var dest = string.Empty;
        string before, copyHash, after;
        var copied = false;
        try
        {
            before = Sha256(file, cancellationToken);
            lock (Gate)
            {
                dest = EnsureUnderRuns(UniqueDestination(targetDir, System.IO.Path.GetFileName(file)), "Copy destination");
                using (new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Reserve the name atomically; File.Copy below overwrites only this placeholder we created.
                }
            }

            copied = true;
            File.Copy(file, dest, overwrite: true);
            var attributes = File.GetAttributes(dest);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(dest, attributes & ~FileAttributes.ReadOnly);
            }

            copyHash = Sha256(dest, cancellationToken);
            after = Sha256(file, cancellationToken);
        }
        catch (IOException ex)
        {
            if (copied)
            {
                TryDelete(dest);
            }

            throw GatewayException.Dependency($"Could not copy '{file}' ({ex.Message.TrimEnd('.')}). If Revit holds it locked, close it there first or copy a saved version.");
        }
        catch (UnauthorizedAccessException)
        {
            if (copied)
            {
                TryDelete(dest);
            }

            throw GatewayException.Denied($"Access to '{file}' was denied.");
        }
        catch (Exception)
        {
            // Cancellation (or anything else) must not leave a partial copy behind.
            if (copied)
            {
                TryDelete(dest);
            }

            throw;
        }

        if (before != after || copyHash != before)
        {
            TryDelete(dest);
            throw new GatewayException(
                GatewayErrorCodes.Conflict,
                $"'{file}' changed while it was being copied (someone is saving it?). The copy was discarded; retry when the file is stable.",
                new { sha256_before = before, sha256_after = after, sha256_copy = copyHash });
        }

        if (targetDir != runDir)
        {
            try
            {
                var shaFile = System.IO.Path.Combine(targetDir, RunStore.RunStore.SourceShaFile);
                var txt = $"# RevitAgent Model Source SHA-256 Verification\n" +
                          $"source: {file}\n" +
                          $"sha256_before: {before}\n" +
                          $"sha256_after: {after}\n" +
                          $"source_unchanged: true\n" +
                          $"verified_at: {DateTimeOffset.Now:O}\n";
                File.WriteAllText(shaFile, txt, new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        return new SandboxCopy
        {
            Source = file,
            CopyPath = dest,
            Bytes = new FileInfo(dest).Length,
            Sha256SourceBefore = before,
            Sha256SourceAfter = after,
            Sha256Copy = copyHash,
            SourceUnchanged = true,
            CopiedAt = DateTimeOffset.Now,
        };
    }

    private string ValidateSource(string path, string argument, bool requireModel)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw GatewayException.Validation($"'{argument}' must be a file path.");
        }

        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception)
        {
            throw GatewayException.Validation($"'{path}' ({argument}) is not a valid path.");
        }

        if (Directory.Exists(full))
        {
            throw GatewayException.Validation($"'{full}' ({argument}) is a folder; pass a file.");
        }

        if (!File.Exists(full))
        {
            throw GatewayException.NotFound($"'{full}' ({argument}) does not exist.");
        }

        var ext = System.IO.Path.GetExtension(full);
        if (requireModel && !ModelExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            throw GatewayException.Validation($"'{full}' is not a Revit file ({string.Join(", ", ModelExtensions)}). Pass other files in extra_files.");
        }

        if (!requireModel && !ExtraFileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            throw GatewayException.Validation($"extra_files only accepts model-related files ({string.Join(" ", ExtraFileExtensions)}); '{full}' is not one.");
        }

        // The gateway's own data (config, audit, logs, launched.json) is never copied out; the runs folder itself is fine.
        var home = System.IO.Path.GetFullPath(_paths.Root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        var runs = System.IO.Path.GetFullPath(RunsDir).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        if (full.StartsWith(home, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(runs, StringComparison.OrdinalIgnoreCase))
        {
            throw GatewayException.Denied($"'{full}' belongs to the gateway's own data folder and cannot be copied.");
        }

        return full;
    }

    private static string UniqueDestination(string dir, string fileName)
    {
        var dest = System.IO.Path.Combine(dir, fileName);
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName);
        for (var n = 2; File.Exists(dest) || Directory.Exists(dest); n++)
        {
            dest = System.IO.Path.Combine(dir, $"{stem}-{n}{ext}");
        }

        return dest;
    }

    /// <summary>Full path when strictly inside the runs folder and accepted by the gateway policy; SANDBOX_VIOLATION otherwise.</summary>
    private string EnsureUnderRuns(string path, string what)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception)
        {
            throw new GatewayException(GatewayErrorCodes.SandboxViolation, $"{what} '{path}' is not a valid path inside the sandbox.");
        }

        var root = System.IO.Path.GetFullPath(RunsDir).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !_policy.IsInSandbox(full, _paths))
        {
            throw new GatewayException(GatewayErrorCodes.SandboxViolation, $"{what} '{full}' is outside the runs folder ({RunsDir}); sandbox tools only write there.");
        }

        // The lexical check above is not enough when a folder inside runs\ is a junction/symlink to elsewhere.
        for (string? dir = full; dir is not null && dir.Length >= root.Length; dir = System.IO.Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            {
                throw new GatewayException(GatewayErrorCodes.SandboxViolation, $"{what} '{full}' goes through a link ('{dir}'); sandbox tools only write to real folders inside the runs folder.");
            }
        }

        return full;
    }

    private static JsonObject? ReadRunInfo(string dir)
    {
        var file = System.IO.Path.Combine(dir, RunInfoFile);
        try
        {
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteRunInfo(string dir, JsonObject info)
    {
        var file = EnsureUnderRuns(System.IO.Path.Combine(dir, RunInfoFile), "run-info.json");
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, info.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(tmp, file, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort cleanup of a partial copy inside the sandbox.
        }
    }
}

internal static class JsonNodeText
{
    /// <summary>The string value of a node, or null when it is missing or not a string (run-info.json may be hand-edited).</summary>
    public static string? AsString(this JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
