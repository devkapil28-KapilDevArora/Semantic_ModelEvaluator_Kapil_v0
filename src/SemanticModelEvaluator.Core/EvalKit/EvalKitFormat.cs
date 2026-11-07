using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.EvalKit;

/// <summary>
/// EvalKit dataset identity. Contract source (no setup guide was available): ASG-SearchPlatform
/// EvalKit.Output\EvalInputs.cs (dataset, dataset_version, dataset_payload_hash) and
/// EvalKit.Viewer\Services\EvalKitDatasetCache.cs (match on name + version + payload hash; a leading "v" is ignored).
/// </summary>
public sealed record EvalKitDatasetIdentity(string? Name, string? Version, string? PayloadHash)
{
    public static string? NormalizeVersion(string? v) => v?.Trim().TrimStart('v', 'V');

    /// <summary>Returns the reasons a run cannot be compared against a dataset. Empty = exact match.</summary>
    public IReadOnlyList<string> Mismatches(EvalKitDatasetIdentity dataset)
    {
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Version) || string.IsNullOrWhiteSpace(PayloadHash))
            reasons.Add("The run does not record a complete dataset identity (name, version and payload hash are all required).");
        if (!string.Equals(Name, dataset.Name, StringComparison.Ordinal))
            reasons.Add($"Dataset name differs: run uses '{Name ?? "unknown"}', loaded dataset is '{dataset.Name ?? "unknown"}'.");
        if (!string.Equals(NormalizeVersion(Version), NormalizeVersion(dataset.Version), StringComparison.Ordinal))
            reasons.Add($"Dataset version differs: run uses v{NormalizeVersion(Version) ?? "?"}, loaded dataset is v{NormalizeVersion(dataset.Version) ?? "?"}.");
        if (!string.Equals(PayloadHash, dataset.PayloadHash, StringComparison.OrdinalIgnoreCase))
            reasons.Add("Dataset payload hash differs: the judgments/queries/corpus are not the exact payload this run was scored against.");
        return reasons;
    }

    public static EvalKitDatasetIdentity From(DatasetReference d) => new(d.Id, d.Version, d.ContentHash);
}

/// <summary>A run read from an EvalKit run-output folder or cloud result bundle.</summary>
public sealed class EvalKitRun
{
    public required string MetricsPath { get; init; }
    public string RunId { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public required EvalKitDatasetIdentity Dataset { get; init; }
    public string? Backend { get; init; }
    public string? BackendVersion { get; init; }
    public string? EvalKitVersion { get; init; }
    public int? MaxResultsPerQuery { get; init; }
    public int QueryCount { get; init; }
    public int JudgedCount { get; init; }
    public int ZeroResultCount { get; init; }
    public int FailedQueryCount { get; init; }
    public double? QueryDurationSeconds { get; init; }
    public List<string> MetricNames { get; init; } = [];
    /// <summary>From result-manifest.json when the run came from a cloud bundle.</summary>
    public string? Experiment { get; init; }
    public string? PsApiVersion { get; init; }
    public string? Model { get; init; }
    public string? SourceDescription { get; init; }
    public string Classification { get; init; } = "Unclassified (local EvalKit output)";
    public List<string> Warnings { get; } = [];

    /// <summary>Provider shown in the UI: PSAPI when the bundle records a PSAPI version; otherwise the EvalKit backend.</summary>
    public string ProviderLabel => PsApiVersion is not null ? "PSAPI" : "EvalKit backend";
    /// <summary>Prefix of the model id used when a run records no model name. The run name is shown as provenance only, never as the model.</summary>
    public const string UnknownModelPrefix = "unknown-model";
    /// <summary>Model name recorded by the bundle manifest; otherwise explicitly unknown (never the run name or backend presented as the model).</summary>
    public string ModelLabel => Model ?? $"{UnknownModelPrefix} (run '{Name}')";
    public string RevisionLabel => PsApiVersion is not null ? "psapi-" + PsApiVersion : BackendVersion is not null ? "backend-" + BackendVersion : "unknown";

    /// <summary>Model identity only. The run id is provenance (tracked separately), so two runs of the same model/version are the same configuration.</summary>
    public ModelIdentity Identity => new(PsApiVersion is not null ? "psapi" : "evalkit", ModelLabel, RevisionLabel);

    /// <summary>Batch (experiment) and run metadata recorded by EvalKit, for provenance. Unknown values are labelled, not omitted.</summary>
    public string BatchMetadata =>
        $"batch/experiment {Experiment ?? "not recorded (local output)"} · run {(RunId.Length > 0 ? RunId : "?")} '{Name}'" +
        $" · created {CreatedAt?.ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}" +
        $" · EvalKit {EvalKitVersion ?? "unknown"} · backend {Backend ?? "unknown"} {BackendVersion ?? ""}".TrimEnd() +
        $" · queries {QueryCount}, judged {JudgedCount}, zero-result {ZeroResultCount}, failed {FailedQueryCount}" +
        $" · depth {MaxResultsPerQuery?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}";
}

public static class EvalKitFormat
{
    public const int MinimumMetricsSchema = 3;

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static double? Dbl(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    /// <summary>Finds every *.metrics.json envelope in a folder (non-recursive first, then one level of subfolders).</summary>
    public static IReadOnlyList<string> FindMetricsFiles(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Run-output folder not found.");
        var top = Directory.GetFiles(folder, "*.metrics.json");
        return top.Length > 0 ? [.. top.Order()] : [.. Directory.EnumerateDirectories(folder).SelectMany(d => Directory.GetFiles(d, "*.metrics.json")).Order()];
    }

    /// <summary>Reads a metrics envelope (EvalKit.Output\MetricResults.cs, snake_case, schema_version ≥ 3).</summary>
    public static EvalKitRun ReadRun(string metricsPath, JsonObject? resultManifest = null, string? classification = null)
    {
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(metricsPath)) as JsonObject ?? throw new InvalidDataException("not an object"); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { throw new InvalidDataException($"{Path.GetFileName(metricsPath)} is not a valid EvalKit metrics envelope.", ex); }
        var schema = Int(root["schema_version"]) ?? 0;
        if (schema < MinimumMetricsSchema)
            throw new InvalidDataException($"{Path.GetFileName(metricsPath)} has schema_version {schema}; EvalKit metrics schema {MinimumMetricsSchema}+ is required.");
        var inputs = root["inputs"] as JsonObject;
        var evalkit = root["evalkit"] as JsonObject;
        var config = evalkit?["config"] as JsonObject;
        var description = root["description"] as JsonObject;
        var run = new EvalKitRun
        {
            MetricsPath = Path.GetFullPath(metricsPath),
            RunId = Str(root["run_id"]) ?? Str(evalkit?["run_id"]) ?? Path.GetFileName(metricsPath).Replace(".metrics.json", "", StringComparison.OrdinalIgnoreCase),
            Name = Str(description?["name"]) ?? Path.GetFileName(metricsPath).Replace(".metrics.json", "", StringComparison.OrdinalIgnoreCase),
            CreatedAt = DateTimeOffset.TryParse(Str(description?["created_at"]), out var created) ? created : null,
            Dataset = new(Str(inputs?["dataset"]) ?? Str(resultManifest?["dataset"]),
                Str(inputs?["dataset_version"]) ?? Str(resultManifest?["dataset_version"]),
                Str(inputs?["dataset_payload_hash"])),
            Backend = Str(config?["backend"]),
            BackendVersion = Str(config?["backend_version"]),
            EvalKitVersion = Str(evalkit?["version"]),
            MaxResultsPerQuery = Int(config?["max_results_per_query"]),
            QueryCount = Int(root["query_count"]) ?? 0,
            JudgedCount = Int(root["judged_count"]) ?? 0,
            ZeroResultCount = Int(root["zero_result_count"]) ?? 0,
            FailedQueryCount = Int(root["failed_query_count"]) ?? 0,
            QueryDurationSeconds = Dbl(root["query_duration_seconds"]),
            MetricNames = root["metric_names"] is JsonArray names ? [.. names.Select(Str).OfType<string>()] : [],
            Experiment = Str(resultManifest?["experiment"]),
            PsApiVersion = Str(resultManifest?["psapi_version"]),
            Model = Str(resultManifest?["model"]),
            Classification = classification ?? "Unclassified (local EvalKit output)"
        };
        if (run.Dataset.PayloadHash is null) run.Warnings.Add("Metrics envelope has no inputs.dataset_payload_hash; exact dataset matching is impossible.");
        if (run.Model is null)
            run.Warnings.Add($"No model name is recorded (no result-manifest.json); the run name '{run.Name}' identifies the model and backend '{run.Backend ?? "unknown"}' " +
                $"{run.BackendVersion ?? ""} is the revision. Verify the identity before drawing conclusions.");
        return run;
    }

    /// <summary>Resolves the query-results JSONL referenced by results.path (or &lt;stem&gt;.query-results.jsonl).</summary>
    public static string ResolveQueryResults(string metricsPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(metricsPath))!;
        var root = JsonNode.Parse(File.ReadAllText(metricsPath));
        var declared = Str(root?["results"]?["path"]);
        if (declared is not null)
        {
            var full = SafeChild(dir, Path.GetFileName(declared));
            if (File.Exists(full)) return full;
        }
        var stem = Path.GetFileName(metricsPath).Replace(".metrics.json", "", StringComparison.OrdinalIgnoreCase);
        var fallback = Path.Combine(dir, stem + ".query-results.jsonl");
        if (File.Exists(fallback)) return fallback;
        throw new FileNotFoundException($"Incomplete run: the query-results file for '{stem}' is missing. Add the complete EvalKit run-output folder.");
    }

    private static string SafeChild(string dir, string name)
    {
        var full = Path.GetFullPath(Path.Combine(dir, name));
        if (!full.StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact path escapes the run folder.");
        return full;
    }

    /// <summary>
    /// Streams query-results JSONL rows {query_id, document_id, position (1-based), score?} into cached results.
    /// Rows without a score use −position so ordering is preserved; this is flagged because score thresholds become rank cut-offs.
    /// </summary>
    public static ModelRunResult ReadResults(EvalKitRun run, int retainDepth = 1000, CancellationToken ct = default)
    {
        var path = ResolveQueryResults(run.MetricsPath);
        var byQuery = new Dictionary<string, List<(int Position, ScoredDocument Doc)>>(StringComparer.Ordinal);
        int line = 0, rankOnly = 0, invalid = 0;
        foreach (var text in File.ReadLines(path))
        {
            line++;
            if ((line & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) continue;
            JsonObject? row;
            try { row = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { row = null; }
            var q = Str(row?["query_id"]); var d = Str(row?["document_id"]); var pos = Int(row?["position"]);
            if (row is null || q is null || d is null || pos is null or < 1) { invalid++; continue; }
            double? score = Dbl(row["score"]);
            if (score is { } s && !double.IsFinite(s)) { invalid++; continue; }
            if (score is null) rankOnly++;
            if (!byQuery.TryGetValue(q, out var list)) byQuery[q] = list = [];
            list.Add((pos.Value, new ScoredDocument(d, score ?? -pos.Value)));
        }
        var result = new ModelRunResult
        {
            Model = run.Identity,
            DisplayName = run.ModelLabel,
            Execution = ExecutionKind.ImportedPredictions,
            Status = RunStatus.Completed,
            Performance = new PerformanceStats { Runtime = "EvalKit " + (run.EvalKitVersion ?? "?") + " / " + (run.Backend ?? "backend ?"), Device = "Recorded by EvalKit", Measured = false }
        };
        foreach (var (qid, rows) in byQuery)
        {
            if (rows.GroupBy(r => r.Doc.DocumentId).Any(g => g.Count() > 1)) { result.Warnings.Add($"Query '{qid}' lists a document more than once; duplicates were dropped."); }
            result.Queries.Add(new QueryResult
            {
                QueryId = qid,
                Results = [.. rows.OrderBy(r => r.Position).DistinctBy(r => r.Doc.DocumentId).Take(retainDepth).Select(r => r.Doc)]
            });
        }
        if (invalid > 0) { result.Warnings.Add($"{invalid} of {line} result rows were invalid and skipped."); result.Status = RunStatus.CompletedWithErrors; }
        if (rankOnly > 0) { result.ScoresIncludeRanks = true; result.Warnings.Add($"{rankOnly} result rows had no score; −position is used, so the threshold acts as a rank cut-off for them."); }
        if (run.QueryDurationSeconds is { } secs && run.QueryCount > 0) result.Performance.MeanLatencyMs = secs * 1000 / run.QueryCount;
        result.Warnings.AddRange(run.Warnings);
        return result;
    }

    /// <summary>Builds an evaluation run from EvalKit output. The dataset reference uses the EvalKit identity (name/version/payload hash).</summary>
    public static EvaluationRun ToEvaluationRun(EvalKitRun run, ModelRunResult result) => new()
    {
        RunId = "evalkit-" + run.RunId,
        Name = run.Name,
        StartedAt = run.CreatedAt ?? File.GetLastWriteTimeUtc(run.MetricsPath),
        CompletedAt = run.CreatedAt,
        Task = EvalTask.Retrieval,
        Dataset = new DatasetReference(run.Dataset.Name ?? "unknown", run.Dataset.Name ?? "unknown", run.Dataset.Version ?? "?", EvalTask.Retrieval, null, run.Dataset.PayloadHash ?? ""),
        Config = new EvaluationConfig { K = Math.Min(10, run.MaxResultsPerQuery ?? 10), RetainDepth = run.MaxResultsPerQuery ?? result.Queries.Select(q => q.Results.Count).DefaultIfEmpty(0).Max() },
        Status = result.Status,
        Models = [result]
    };

    // ---- datasets -------------------------------------------------------------------------------------------

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    /// <summary>
    /// Loads an EvalKit dataset manifest (AsgSearchPlatform.Manifest: description{name,version}, corpus[], queries, judgements, payload_hash).
    /// Judgement rows are {query_id, document_id, relevance}; relevance &lt; 0 is harmful (EvalKit HarmCount/HarmExposure),
    /// relevance ≥ 1 is relevant. Unjudged pairs stay unjudged. Per-file "sha256:" hashes are verified when present.
    /// </summary>
    public static EvalDataset LoadDataset(string manifestPath, string? classification = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var root = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject ?? throw new InvalidDataException("Manifest is not a JSON object.");
        var description = root["description"] as JsonObject;
        var name = Str(description?["name"]) ?? Str(root["name"]) ?? throw new InvalidDataException("EvalKit manifest has no description.name.");
        var version = Str(description?["version"]) ?? Str(root["version"]) ?? throw new InvalidDataException("EvalKit manifest has no description.version.");
        var payload = Str(root["payload_hash"]) ?? throw new InvalidDataException("EvalKit manifest has no payload_hash; exact run matching is impossible.");
        var issues = new List<Models.ValidationIssue>();

        string Ref(JsonNode? fileRef, string what)
        {
            var p = Str(fileRef?["path"]) ?? throw new InvalidDataException($"Manifest has no {what} file reference.");
            var full = SafeChild(dir, p.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) throw new FileNotFoundException($"Dataset {what} file '{Path.GetFileName(p)}' is missing; fetch the complete dataset.");
            if (new FileInfo(full).Length > TabularReader.MaxFileBytes)
                throw new InvalidDataException($"Dataset {what} file '{Path.GetFileName(p)}' exceeds the {TabularReader.MaxFileBytes / (1024 * 1024)} MB limit for in-app comparison; use a smaller dataset slice.");
            ct.ThrowIfCancellationRequested();
            if (Str(fileRef?["hash"]) is { } expected && expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                using var s = File.OpenRead(full);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                for (int read; (read = s.Read(buffer, 0, buffer.Length)) > 0;) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
                var actual = "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Dataset {what} file '{Path.GetFileName(p)}' does not match its manifest hash; the cache is corrupt or modified.");
            }
            return full;
        }

        IEnumerable<JsonObject> Rows(string path)
        {
            int n = 0;
            foreach (var line in File.ReadLines(path))
            {
                n++;
                if ((n & 1023) == 0) ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject? o;
                try { o = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { o = null; }
                if (o is null) { issues.Add(new(Models.ValidationSeverity.Warning, $"{Path.GetFileName(path)} line {n} is not a JSON object.")); continue; }
                yield return o;
            }
        }

        var queries = Rows(Ref(root["queries"], "queries")).Select(o => (Id: Str(o["id"]), Text: Str(o["text"])))
            .Where(q => q.Id is not null).Select(q => new QueryItem(q.Id!, q.Text ?? "")).ToList();
        var documents = new List<DocumentItem>();
        var corpus = root["corpus"] as JsonArray ?? (root["corpus"] is JsonObject single ? new JsonArray(single.DeepClone()) : null) ?? throw new InvalidDataException("Manifest has no corpus.");
        foreach (var fileRef in corpus)
        {
            var corpusPath = Ref(fileRef, "corpus");
            var corpusDir = Path.GetDirectoryName(corpusPath)!;
            foreach (var o in Rows(corpusPath))
            {
                if (Str(o["id"]) is not { } id) continue;
                if (Str(o["text"]) is { } text) { documents.Add(new DocumentItem(id, text)); continue; }
                if (Str(o["path"]) is { } rel)
                {
                    var full = SafeChild(corpusDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    documents.Add(ImageExtensions.Contains(Path.GetExtension(full))
                        ? new DocumentItem(id, "", full, new() { ["path"] = rel })
                        : new DocumentItem(id, File.Exists(full) && new FileInfo(full).Length <= 1_000_000 ? File.ReadAllText(full) : "", null, new() { ["path"] = rel }));
                }
            }
        }
        var judgments = new List<Judgment>();
        if (root["judgements"] is JsonObject jref)
            foreach (var o in Rows(Ref(jref, "judgements")))
            {
                var q = Str(o["query_id"]); var d = Str(o["document_id"]); var rel = Dbl(o["relevance"]);
                if (q is null || d is null) continue;
                // EvalKit: relevance < 0 = harmful; missing relevance = unjudged (never safe or relevant).
                judgments.Add(rel is { } r ? new Judgment(q, d, Math.Max(0, r), r < 0) : new Judgment(q, d, null, null));
            }
        else issues.Add(new(Models.ValidationSeverity.Warning, "Manifest has no judgements; quality and harm cannot be computed."));

        var ds = new EvalDataset
        {
            Id = name,
            Name = name,
            Version = version,
            Task = EvalTask.Retrieval,
            Description = Str(description?["short_text"]) ?? Str(description?["text"]) ?? "",
            DataClassification = classification ?? "Unclassified EvalKit dataset — follow the handling rules of its source container",
            SourcePath = Path.GetFullPath(manifestPath),
            ContentHash = payload,
            Queries = queries,
            Documents = documents,
            Judgments = judgments
        };
        DatasetValidator.Validate(ds, issues);
        ds.ValidationIssues.AddRange(issues);
        return ds;
    }

    /// <summary>EvalKit dataset cache root: EVALKIT_CACHE_DIR or %LOCALAPPDATA%\EvalKit\cache (EvalKitDatasetCache.cs).</summary>
    public static string DatasetCacheRoot(string? configured = null) =>
        !string.IsNullOrWhiteSpace(configured) ? configured
        : Environment.GetEnvironmentVariable("EVALKIT_CACHE_DIR") is { Length: > 0 } env ? env
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EvalKit", "cache");

    /// <summary>Finds the exact cached dataset (name, version and payload hash all match) under &lt;cache&gt;\datasets\&lt;name&gt;\v&lt;version&gt;.</summary>
    public static string? FindCachedDataset(EvalKitDatasetIdentity identity, string? cacheRoot = null)
    {
        if (identity.Name is null || identity.Version is null || identity.PayloadHash is null) return null;
        var folder = Path.Combine(DatasetCacheRoot(cacheRoot), "datasets", identity.Name, "v" + EvalKitDatasetIdentity.NormalizeVersion(identity.Version));
        if (!Directory.Exists(folder)) return null;
        foreach (var manifest in Directory.EnumerateFiles(folder, "*.manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(manifest));
                var cached = new EvalKitDatasetIdentity(Str(o?["description"]?["name"]) ?? Str(o?["name"]), Str(o?["description"]?["version"]) ?? Str(o?["version"]), Str(o?["payload_hash"]));
                if (identity.Mismatches(cached).Count == 0) return manifest;
            }
            catch (JsonException) { }
        }
        return null;
    }
}
