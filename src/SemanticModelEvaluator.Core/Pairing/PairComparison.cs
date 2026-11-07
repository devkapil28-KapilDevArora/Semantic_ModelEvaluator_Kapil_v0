using System.Text.Json.Serialization;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.EvalKit;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Metrics;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Pairing;

/// <summary>
/// Provider-neutral description of one side of a comparison. Provider-specific metadata is kept in <see cref="Parameters"/>;
/// no secrets are ever stored here (keys live in Credential Manager, endpoints never carry query strings).
/// </summary>
public sealed record ModelConfiguration(
    string ProviderId,
    string ProviderName,
    string ModelId,
    string DisplayName,
    string? Revision,
    string? Endpoint,
    IReadOnlyDictionary<string, string> Parameters,
    string? ContentHash = null)
{
    private static readonly string[] ConfigurationProperties = ["queryPrefix", "documentPrefix", "pooling", "onnxFile", "api_style", "deployment", "model_version"];

    public static ModelConfiguration From(ModelDescriptor d, IReadOnlyDictionary<string, string>? parameters = null)
    {
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ConfigurationProperties)
            if (d.Properties.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) p[key] = v;
        foreach (var (k, v) in parameters ?? new Dictionary<string, string>()) p[k] = v;
        return new(d.Identity.ProviderId, string.IsNullOrEmpty(d.ProviderName) ? d.Identity.ProviderId : d.ProviderName, d.Identity.ModelId, d.DisplayName,
            d.Identity.Revision, d.Properties.GetValueOrDefault("endpoint"), p, d.Identity.ContentHash);
    }

    /// <summary>For captured runs (EvalKit/PSAPI or saved workspaces) where only the recorded identity is known.</summary>
    public static ModelConfiguration From(ModelRunResult r, string providerName, IReadOnlyDictionary<string, string>? parameters = null) =>
        new(r.Model.ProviderId, providerName, r.Model.ModelId, string.IsNullOrEmpty(r.DisplayName) ? r.Model.ModelId : r.DisplayName,
            r.Model.Revision, null, new SortedDictionary<string, string>((parameters ?? new Dictionary<string, string>()).ToDictionary(), StringComparer.Ordinal), r.Model.ContentHash);

    [JsonIgnore]
    public string Fingerprint =>
        string.Join("|", ProviderId.ToLowerInvariant(), ModelId.ToLowerInvariant(), (Revision ?? "").ToLowerInvariant(), (Endpoint ?? "").TrimEnd('/').ToLowerInvariant(),
            string.Join(";", Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")), ContentHash ?? "");

    [JsonIgnore] public string Label => $"{DisplayName} ({ProviderName}{(string.IsNullOrEmpty(Revision) ? "" : " · " + Revision)})";
}

public static class PairSelection
{
    /// <summary>
    /// Exactly two configurations are required. Identical configurations are rejected; the same model is allowed only when the
    /// revision/version, endpoint or parameter set differs (an intentional variant comparison). Any cross-provider pairing is allowed.
    /// </summary>
    public static ValidationReport Validate(ModelConfiguration? a, ModelConfiguration? b)
    {
        var r = new ValidationReport();
        if (a is null) r.Error("Select Model A.");
        if (b is null) r.Error("Select Model B.");
        if (a is null || b is null) return r;
        if (a.Fingerprint == b.Fingerprint)
            return r.Error("Model A and Model B are the same configuration. Choose a different model, or change the version, endpoint or a parameter to compare variants.");
        if (string.Equals(a.ProviderId, b.ProviderId, StringComparison.OrdinalIgnoreCase) && string.Equals(a.ModelId, b.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            var what = new List<string>();
            if (!string.Equals(a.Revision, b.Revision, StringComparison.OrdinalIgnoreCase)) what.Add("version/revision");
            if (!string.Equals(a.Endpoint?.TrimEnd('/'), b.Endpoint?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) what.Add("endpoint");
            if (!a.Parameters.OrderBy(p => p.Key).SequenceEqual(b.Parameters.OrderBy(p => p.Key))) what.Add("parameters");
            if (!string.Equals(a.ContentHash, b.ContentHash, StringComparison.Ordinal)) what.Add("model content hash");
            r.Info($"Same model on both sides — comparing variants that differ by {string.Join(", ", what)}.");
        }
        else if (!string.Equals(a.ProviderId, b.ProviderId, StringComparison.OrdinalIgnoreCase))
            r.Info($"Cross-provider comparison: {a.ProviderName} vs {b.ProviderName}.");
        else r.Info($"Two models from {a.ProviderName}.");
        return r;
    }
}

/// <summary>How the results for one side were obtained. Shown verbatim so loading, recalculating and invoking are never confused.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResultOrigin>))]
public enum ResultOrigin { NewEvaluation, LoadedEvalKitLocal, LoadedEvalKitCloud, LoadedWorkspace, ImportedPredictions }

public sealed class PairSide
{
    public required ModelConfiguration Configuration { get; init; }
    public required ModelRunResult Result { get; init; }
    /// <summary>The dataset identity the run was scored against.</summary>
    public required DatasetReference RunDataset { get; init; }
    public ResultOrigin Origin { get; init; }
    public string SourceDescription { get; init; } = "";
    public string? RunId { get; init; }
    public DateTimeOffset? RunAt { get; init; }
    /// <summary>Depth of results cached per query (K cannot usefully exceed it).</summary>
    public int? CachedDepth { get; init; }
    public double? Threshold { get; init; }
    /// <summary>Handling classification of the captured result (e.g. sensitive EvalKit container). Null = same as the dataset.</summary>
    public string? Classification { get; init; }
}

public sealed class PairSettings
{
    public int K { get; init; } = 10;
    /// <summary>When set, only these query/example ids were evaluated (sample limit); the rest are out of scope, not failures.</summary>
    public IReadOnlyCollection<string>? SampledIds { get; init; }
    public string Objective { get; init; } = "";
    /// <summary>Evaluation dimensions (metric categories) to report; null = every dimension the dataset supports.</summary>
    public IReadOnlyCollection<MetricCategory>? Dimensions { get; init; }
    /// <summary>Inclusion rule: compare only judged queries / labelled examples. Unjudged items are excluded (reported), never scored as passing or failing.</summary>
    public bool JudgedItemsOnly { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<QueryOutcome>))]
public enum QueryOutcome { AWins, BWins, Tie, Unknown }

[Flags]
public enum QueryFlags { None = 0, Disagreement = 1, SafetyConcern = 2, Empty = 4, Error = 8, Unjudged = 16 }

public enum ConfidenceLevel { None, Low, Medium, High }

/// <summary>Per-model detail for one query or example. Null quality means unknown (unjudged / failed), never zero.</summary>
public sealed class SideDetail
{
    public bool Missing { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<ScoredDocument> Retained { get; init; } = [];
    public int Filtered { get; init; }
    public int RelevantRetained { get; init; }
    public int HarmfulRetained { get; init; }
    public int UnjudgedRetained { get; init; }
    public double? Quality { get; init; }
    public string? Predicted { get; init; }
    public double? Score { get; init; }
    public double? LatencyMs { get; init; }
    /// <summary>Judgment of each retained result, parallel to <see cref="Retained"/> ("grade 2, safe", "unjudged", ...).</summary>
    public IReadOnlyList<string> ResultJudgments { get; init; } = [];
    public string? TopJudgment => ResultJudgments.Count > 0 ? ResultJudgments[0] : null;

    public string State => Missing ? "Missing (not in run)" : Error is not null ? "Error" : Predicted is not null ? "Predicted" : Retained.Count == 0 ? "Empty" : "Retained";
    public string? TopDocumentId => Retained.Count > 0 ? Retained[0].DocumentId : null;
    public double? TopScore => Retained.Count > 0 ? Retained[0].Score : Score;
}

public sealed class PairQueryRow
{
    public required string Id { get; init; }
    public string? Text { get; init; }
    public string? ImagePath { get; init; }
    public string? Label { get; init; }
    public bool Judged { get; init; }
    public required SideDetail A { get; init; }
    public required SideDetail B { get; init; }
    public double? Delta => A.Quality is { } a && B.Quality is { } b ? a - b : null;
    public QueryOutcome Outcome { get; init; }
    public QueryFlags Flags { get; init; }
    public string? Explanation { get; init; }

    public string OutcomeText => Outcome switch
    {
        QueryOutcome.AWins => "A better",
        QueryOutcome.BWins => "B better",
        QueryOutcome.Tie => "Tie",
        _ => "Unknown"
    };
    public string FlagsText => Flags == QueryFlags.None ? "" : string.Join(", ", Enum.GetValues<QueryFlags>().Where(f => f != QueryFlags.None && Flags.HasFlag(f)));
}

public sealed class PairMetricRow
{
    public required MetricDefinition Definition { get; init; }
    public MetricValue? A { get; init; }
    public MetricValue? B { get; init; }
    public double? Delta => A?.Value is { } a && B?.Value is { } b ? a - b : null;
    /// <summary>"A", "B", "Tie", or null when unknown or the metric has no preferred direction.</summary>
    public string? Better { get; init; }
    public string DirectionNote => Definition.Direction switch
    {
        Direction.LowerIsBetter => "Lower is better (values shown as measured, not inverted)",
        Direction.HigherIsBetter => "Higher is better",
        _ => "No preferred direction"
    };
}

public sealed class SideCounts
{
    public int Total { get; init; }
    public int Judged { get; init; }
    public int Unjudged { get; init; }
    public int Evaluated { get; init; }
    public int Retained { get; init; }
    public int Filtered { get; init; }
    public int Empty { get; init; }
    /// <summary>Items with no usable output: model errors plus items missing from the run (see <see cref="Missing"/>).</summary>
    public int Failed { get; init; }
    public int Missing { get; init; }
    public int Errors => Failed - Missing;
}

public sealed record PerformanceRow(string Name, double? A, double? B, Direction Direction, string Format, string Basis)
{
    public double? Delta => A is { } a && B is { } b ? a - b : null;
}

public sealed class PairRecommendation
{
    public string Headline { get; init; } = "";
    public string Detail { get; init; } = "";
    public ConfidenceLevel Confidence { get; init; }
    public string ConfidenceReason { get; init; } = "";
    public double? SignTestP { get; init; }
}

public sealed class PairComparisonResult
{
    public required PairSide A { get; init; }
    public required PairSide B { get; init; }
    public required DatasetReference Dataset { get; init; }
    public string DatasetClassification { get; init; } = "";
    public required PairSettings Settings { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public List<string> Blocking { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool IsComparable => Blocking.Count == 0;
    public bool IsLikeForLike => Blocking.Count == 0 && Warnings.Count == 0;
    public List<PairMetricRow> Metrics { get; init; } = [];
    public List<PerformanceRow> Performance { get; init; } = [];
    public required SideCounts CountsA { get; init; }
    public required SideCounts CountsB { get; init; }
    public List<PairQueryRow> Rows { get; init; } = [];
    public int AWins => Rows.Count(r => r.Outcome == QueryOutcome.AWins);
    public int BWins => Rows.Count(r => r.Outcome == QueryOutcome.BWins);
    public int Ties => Rows.Count(r => r.Outcome == QueryOutcome.Tie);
    public int Unknown => Rows.Count(r => r.Outcome == QueryOutcome.Unknown);
    public required PairRecommendation Recommendation { get; init; }
    public List<string> StrengthsA { get; init; } = [];
    public List<string> WeaknessesA { get; init; } = [];
    public List<string> StrengthsB { get; init; } = [];
    public List<string> WeaknessesB { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public ThresholdCurve? CurveA { get; init; }
    public ThresholdCurve? CurveB { get; init; }
    public IReadOnlyList<string> CurveMetricIds { get; init; } = [];
    public string? PrimaryMetricId { get; init; }
    public bool IsRetrieval { get; init; }
}

public sealed class PairComparer(MetricRegistry registry)
{
    public const double TieMargin = 0.005;

    /// <summary>Rules applied before any metric is shown. Listed in the UI and the report so eligibility is explicit.</summary>
    public static readonly IReadOnlyList<string> EligibilityRules =
    [
        "Both runs must be scored against the exact loaded dataset: same name/id, same version (a leading 'v' is ignored) and same payload/content hash.",
        "Both runs must be for the dataset's task.",
        "Model A and Model B must be different configurations (model, version, endpoint or parameters).",
        "Each side must have produced results for at least one item.",
        "Warnings (not blocking): K beyond a run's cached depth, different execution kinds (measured vs recorded latency), synthetic baselines, partial failures, low judgment coverage."
    ];

    private static readonly string[] PrimaryRetrieval = ["ndcg_at_k", "recall_at_k", "mrr", "precision_at_k", "hit_rate_at_k"];
    private static readonly string[] PrimaryClassification = ["f1", "macro_f1", "accuracy", "roc_auc", "pr_auc"];

    public static bool IsRetrievalTask(EvalTask t) => t is EvalTask.Retrieval or EvalTask.ImageTextRetrieval or EvalTask.Reranking;

    /// <summary>Restricts a dataset to a deterministic sample of items. Identity (id, version, hash) is unchanged so run matching still applies.</summary>
    public static (EvalDataset Dataset, IReadOnlyList<string> Ids) Sample(EvalDataset ds, int limit, int seed = 17)
    {
        var retrieval = IsRetrievalTask(ds.Task);
        var all = retrieval ? ds.Queries.Select(q => q.Id).ToList() : ds.Examples.Select(e => e.Id).ToList();
        if (limit <= 0 || limit >= all.Count) return (ds, all);
        var rng = new Random(seed);
        var ids = all.OrderBy(_ => rng.Next()).Take(limit).ToHashSet(StringComparer.Ordinal);
        return (Restrict(ds, ids), [.. all.Where(ids.Contains)]);
    }

    /// <summary>Ids of judged queries (retrieval) or labelled examples (classification).</summary>
    public static HashSet<string> JudgedIds(EvalDataset ds) => IsRetrievalTask(ds.Task)
        ? ds.Queries.Where(q => ds.JudgmentIndex.HasJudgments(q.Id)).Select(q => q.Id).ToHashSet(StringComparer.Ordinal)
        : ds.Examples.Where(e => e.Label is not null).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static EvalDataset Restrict(EvalDataset ds, HashSet<string> ids)
    {
        var retrieval = IsRetrievalTask(ds.Task);
        return new EvalDataset
        {
            Id = ds.Id, Name = ds.Name, Version = ds.Version, Task = ds.Task, Description = ds.Description, DataClassification = ds.DataClassification,
            SourcePath = ds.SourcePath, ContentHash = ds.ContentHash, PositiveLabel = ds.PositiveLabel, IsSynthetic = ds.IsSynthetic,
            Queries = retrieval ? [.. ds.Queries.Where(q => ids.Contains(q.Id))] : [],
            Documents = ds.Documents,
            Judgments = retrieval ? [.. ds.Judgments.Where(j => ids.Contains(j.QueryId))] : [],
            Examples = retrieval ? [] : [.. ds.Examples.Where(e => ids.Contains(e.Id))],
            ValidationIssues = ds.ValidationIssues
        };
    }

    public static (List<string> Blocking, List<string> Warnings) CheckEligibility(EvalDataset dataset, PairSide a, PairSide b, int k)
    {
        var blocking = new List<string>();
        var warnings = new List<string>();
        if (!IsRetrievalTask(dataset.Task) && dataset.Task is not (EvalTask.Classification or EvalTask.Safety))
            blocking.Add($"Two-model comparison supports retrieval, reranking, classification and safety tasks. {dataset.Task} datasets are not supported here; use the multi-model workbench (Explorer/Comparator), which has task-specific metrics for them.");
        if (IsRetrievalTask(dataset.Task) && dataset.Queries.Count > 0 && dataset.JudgmentCoverage < 0.5)
            warnings.Add($"Low judgment coverage: only {dataset.JudgmentCoverage:P0} of queries have judgments; quality metrics rest on a minority of queries.");
        var loaded = EvalKitDatasetIdentity.From(dataset.ToReference());
        foreach (var (name, side) in new[] { ("Model A", a), ("Model B", b) })
        {
            if (side.Configuration.ModelId.StartsWith(EvalKitRun.UnknownModelPrefix, StringComparison.Ordinal))
                warnings.Add($"{name}: the run does not record the model name or version; its identity is unknown, so the result cannot be attributed to a specific model.");
            if (side.Result.ScoresIncludeRanks && side.Threshold is not null)
                warnings.Add($"{name}: some results have positions only; its threshold acts as a rank cut-off for them.");
            foreach (var m in EvalKitDatasetIdentity.From(side.RunDataset).Mismatches(loaded)) blocking.Add($"{name}: {m}");
            if (side.RunDataset.Task != dataset.Task) blocking.Add($"{name}: run task {side.RunDataset.Task} differs from dataset task {dataset.Task}.");
            if (!side.Result.HasResults)
                blocking.Add($"{name}: the run produced no results{(side.Result.Error is { } e ? " (" + e + ")" : "")}.");
            if (side.CachedDepth is { } depth && k > depth) warnings.Add($"{name}: K={k} exceeds the {depth} results cached per query; ranks beyond {depth} are treated as not retrieved.");
            if (side.Result.IsSynthetic) warnings.Add($"{name} is a synthetic baseline; its results are not real model performance.");
            if (side.Result.Status is RunStatus.CompletedWithErrors or RunStatus.Failed or RunStatus.Cancelled)
                warnings.Add($"{name}: run status {side.Result.Status}; partial results are compared and failed items are counted separately.");
        }
        var keyA = a.Configuration.Fingerprint; var keyB = b.Configuration.Fingerprint;
        if (keyA == keyB) blocking.Add("Model A and Model B are the same configuration.");
        if (a.Result.Performance.Measured != b.Result.Performance.Measured)
            warnings.Add("Latency is not like-for-like: one side was measured by this application, the other was recorded by another tool or imported.");
        else if (a.Result.Execution != b.Result.Execution && (a.Result.Execution == ExecutionKind.ExternalProcess || b.Result.Execution == ExecutionKind.ExternalProcess))
            warnings.Add("Latency compares remote inference with local inference; network time is included on the remote side.");
        return (blocking, warnings);
    }

    public PairComparisonResult Compare(EvalDataset dataset, PairSide a, PairSide b, PairSettings settings, bool includeText, CancellationToken ct = default)
    {
        if (settings.K < 1) throw new ArgumentOutOfRangeException(nameof(settings), "K must be positive.");
        var (blocking, warnings) = CheckEligibility(dataset, a, b, settings.K);
        if (settings.SampledIds is { } sampled)
        {
            var scope = sampled.ToHashSet(StringComparer.Ordinal);
            var count = IsRetrievalTask(dataset.Task) ? dataset.Queries.Count : dataset.Examples.Count;
            if (count != scope.Count || (IsRetrievalTask(dataset.Task) ? dataset.Queries.Any(q => !scope.Contains(q.Id)) : dataset.Examples.Any(e => !scope.Contains(e.Id))))
                dataset = Restrict(dataset, scope);
        }
        var fullItemCount = IsRetrievalTask(dataset.Task) ? dataset.Queries.Count : dataset.Examples.Count;
        if (settings.JudgedItemsOnly) dataset = Restrict(dataset, JudgedIds(dataset));
        var retrieval = IsRetrievalTask(dataset.Task);
        var thresholds = new ThresholdEngine(registry);
        var metricDefs = registry.Compatible(dataset.Task, dataset)
            .Where(d => d.Category != MetricCategory.Performance && (settings.Dimensions is null || settings.Dimensions.Contains(d.Category))).ToList();
        var ids = metricDefs.Select(d => d.Id).ToList();
        var snapA = thresholds.Evaluate(dataset, a.Result, a.Threshold, settings.K, ids);
        var snapB = thresholds.Evaluate(dataset, b.Result, b.Threshold, settings.K, ids);

        var metrics = metricDefs.Select(d =>
        {
            var va = snapA.Values.GetValueOrDefault(d.Id); var vb = snapB.Values.GetValueOrDefault(d.Id);
            return new PairMetricRow { Definition = d, A = va, B = vb, Better = Better(d.Direction, va?.Value, vb?.Value) };
        }).ToList();

        var ctxA = new MetricContext { Dataset = dataset, Result = a.Result, Threshold = a.Threshold, K = settings.K };
        var ctxB = new MetricContext { Dataset = dataset, Result = b.Result, Threshold = b.Threshold, K = settings.K };
        var rows = retrieval ? RetrievalRows(dataset, ctxA, ctxB, includeText, ct) : ExampleRows(dataset, ctxA, ctxB, includeText, ct);
        var qualitySelected = settings.Dimensions is null || settings.Dimensions.Contains(MetricCategory.Quality);
        if (!qualitySelected)
            rows = [.. rows.Select(r => new PairQueryRow
            {
                Id = r.Id, Text = r.Text, ImagePath = r.ImagePath, Label = r.Label, Judged = r.Judged, A = r.A, B = r.B, Flags = r.Flags,
                Outcome = QueryOutcome.Unknown, Explanation = r.Explanation ?? "Quality dimension not selected: no per-item winner is declared."
            })];

        var countsA = Counts(dataset, ctxA, rows, true, retrieval);
        var countsB = Counts(dataset, ctxB, rows, false, retrieval);
        var perf = Performance(a, b, countsA, countsB);

        var primary = (retrieval ? PrimaryRetrieval : PrimaryClassification).FirstOrDefault(id => metrics.Any(m => m.Definition.Id == id && m.A?.Value is not null && m.B?.Value is not null));
        var limitations = Limitations(dataset, a, b, countsA, countsB, rows, settings, retrieval);
        if (settings.JudgedItemsOnly)
            limitations.Insert(0, $"Inclusion rule: judged items only — {(retrieval ? dataset.Queries.Count : dataset.Examples.Count)} of {fullItemCount} item(s) included; unjudged items were excluded, not scored.");
        if (settings.Dimensions is { } dims)
            limitations.Add("Evaluation dimensions limited to: " + string.Join(", ", dims.Order()) + ". Other metric categories were not reported.");
        var recommendation = Recommend(a, b, metrics, rows, primary, blocking, warnings, countsA, countsB);

        var sa = new List<string>(); var wa = new List<string>(); var sb = new List<string>(); var wb = new List<string>();
        foreach (var m in metrics.Where(m => m.Better is "A" or "B" && m.Delta is { } d && Math.Abs(d) >= 0.01))
        {
            var text = $"{m.Definition.Name}: {Fmt(m.A?.Value, m.Definition.Format)} vs {Fmt(m.B?.Value, m.Definition.Format)}";
            if (m.Better == "A") { sa.Add(text); wb.Add(text); } else { sb.Add(text); wa.Add(text); }
        }
        foreach (var p in perf.Where(p => p.Delta is { } d && d != 0 && p.Direction != Direction.Neutral))
        {
            var aBetter = (p.Direction == Direction.LowerIsBetter) == (p.Delta < 0);
            var text = $"{p.Name}: {Fmt(p.A, p.Format)} vs {Fmt(p.B, p.Format)} ({p.Basis})";
            (aBetter ? sa : sb).Add(text); (aBetter ? wb : wa).Add(text);
        }
        if (countsA.Failed > 0) wa.Add($"{countsA.Failed} failed or missing item(s).");
        if (countsB.Failed > 0) wb.Add($"{countsB.Failed} failed or missing item(s).");
        int wins = rows.Count(r => r.Outcome == QueryOutcome.AWins), losses = rows.Count(r => r.Outcome == QueryOutcome.BWins);
        if (wins > losses) sa.Add($"Better on {wins} item(s) vs {losses} for Model B (per-item primary quality)."); else if (losses > wins) sb.Add($"Better on {losses} item(s) vs {wins} for Model A (per-item primary quality).");

        ThresholdCurve? curveA = null, curveB = null;
        var curveIds = new List<string>();
        if (retrieval && blocking.Count == 0)
        {
            curveIds = new[] { primary, "empty_query_rate", "harm_rate_at_k" }.OfType<string>().Where(ids.Contains).Distinct().ToList();
            if (curveIds.Count > 0 && a.Result.AllScores().Any() && b.Result.AllScores().Any())
            {
                var (minA, maxA) = ThresholdEngine.ScoreRange([a.Result]);
                var (minB, maxB) = ThresholdEngine.ScoreRange([b.Result]);
                curveA = thresholds.Sweep(dataset, a.Result, ThresholdEngine.CandidateThresholds(minA, maxA, 30), settings.K, curveIds, ct);
                curveB = thresholds.Sweep(dataset, b.Result, ThresholdEngine.CandidateThresholds(minB, maxB, 30), settings.K, curveIds, ct);
            }
        }

        return new PairComparisonResult
        {
            A = a, B = b, Dataset = dataset.ToReference(), DatasetClassification = dataset.DataClassification, Settings = settings,
            Blocking = blocking, Warnings = warnings, Metrics = metrics, Performance = perf, CountsA = countsA, CountsB = countsB, Rows = rows,
            Recommendation = recommendation, StrengthsA = sa, WeaknessesA = wa, StrengthsB = sb, WeaknessesB = wb, Limitations = limitations,
            CurveA = curveA, CurveB = curveB, CurveMetricIds = curveIds, PrimaryMetricId = primary, IsRetrieval = retrieval
        };
    }

    public static string? Better(Direction direction, double? a, double? b)
    {
        if (a is not { } x || b is not { } y || direction == Direction.Neutral) return null;
        if (Math.Abs(x - y) < TieMargin) return "Tie";
        return (direction == Direction.HigherIsBetter) == (x > y) ? "A" : "B";
    }

    private static List<PairQueryRow> RetrievalRows(EvalDataset ds, MetricContext a, MetricContext b, bool includeText, CancellationToken ct)
    {
        var rawA = a.Result.Queries.GroupBy(q => q.QueryId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rawB = b.Result.Queries.GroupBy(q => q.QueryId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var retA = a.RetainedQueries.ToDictionary(q => q.QueryId, StringComparer.Ordinal);
        var retB = b.RetainedQueries.ToDictionary(q => q.QueryId, StringComparer.Ordinal);
        var rows = new List<PairQueryRow>(ds.Queries.Count);
        foreach (var q in ds.Queries)
        {
            ct.ThrowIfCancellationRequested();
            bool judged = ds.JudgmentIndex.HasJudgments(q.Id);
            bool scorable = judged && ds.JudgmentIndex.RelevantCount(q.Id) > 0;
            var sa = Side(ds, a, q.Id, rawA.GetValueOrDefault(q.Id), retA[q.Id], scorable);
            var sb = Side(ds, b, q.Id, rawB.GetValueOrDefault(q.Id), retB[q.Id], scorable);
            var flags = QueryFlags.None;
            if (!judged) flags |= QueryFlags.Unjudged;
            if (sa.Missing || sa.Error is not null || sb.Missing || sb.Error is not null) flags |= QueryFlags.Error;
            if (sa.State == "Empty" || sb.State == "Empty") flags |= QueryFlags.Empty;
            if (sa.HarmfulRetained > 0 || sb.HarmfulRetained > 0) flags |= QueryFlags.SafetyConcern;
            bool bothOk = !sa.Missing && sa.Error is null && !sb.Missing && sb.Error is null;
            if (bothOk && (sa.State == "Retained" && sb.State == "Retained" ? sa.TopDocumentId != sb.TopDocumentId : sa.State != sb.State)) flags |= QueryFlags.Disagreement;
            var outcome = Outcome(sa.Quality, sb.Quality);
            string? why = null;
            if (!judged) why = "No relevance judgment for this query: quality is unknown (not counted as relevant, safe or passing).";
            else if (!scorable) why = "The query is judged but no document is relevant: ranking quality (NDCG) is undefined, so the outcome is unknown.";
            else if (sa.Missing || sb.Missing) why = "The query is missing from at least one run: outcome unknown.";
            else if (sa.Error is not null || sb.Error is not null) why = "At least one model failed on this query: outcome unknown.";
            if (includeText && string.IsNullOrEmpty(q.Text) && q.ImagePath is null) why = (why is null ? "" : why + " ") + "The dataset provides no query text or image for this item.";
            rows.Add(new PairQueryRow
            {
                Id = q.Id, Text = includeText ? q.Text : null, ImagePath = includeText ? q.ImagePath : null, Judged = judged,
                A = sa, B = sb, Outcome = outcome, Flags = flags, Explanation = why
            });
        }
        return rows;
    }

    private static SideDetail Side(EvalDataset ds, MetricContext ctx, string qid, QueryResult? raw, RetainedQuery kept, bool judged)
    {
        if (raw is null) return new SideDetail { Missing = true };
        if (raw.Error is not null) return new SideDetail { Error = raw.Error, LatencyMs = raw.LatencyMs };
        int rel = 0, harm = 0, unj = 0;
        var labels = new List<string>(kept.Retained.Count);
        foreach (var d in kept.Retained)
        {
            var j = ds.JudgmentIndex.Get(qid, d.DocumentId);
            if (j?.Grade > 0) rel++;
            if (j?.Harmful == true) harm++;
            if (j?.Grade is null) unj++;
            labels.Add((j?.Grade is { } g ? $"grade {g.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "unjudged") + (j?.Harmful == true ? ", HARMFUL" : j?.Harmful == false ? ", safe" : ""));
        }
        return new SideDetail
        {
            Retained = kept.Retained, Filtered = kept.FilteredByThreshold, RelevantRetained = rel, HarmfulRetained = harm, UnjudgedRetained = unj,
            ResultJudgments = labels, Quality = judged ? BuiltInMetrics.Ndcg(ctx, kept) : null, LatencyMs = raw.LatencyMs
        };
    }

    private static List<PairQueryRow> ExampleRows(EvalDataset ds, MetricContext a, MetricContext b, bool includeText, CancellationToken ct)
    {
        var predA = a.ClassifiedExamples().ToDictionary(x => x.Example.Id, x => x, StringComparer.Ordinal);
        var predB = b.ClassifiedExamples().ToDictionary(x => x.Example.Id, x => x, StringComparer.Ordinal);
        var rawA = a.Result.Examples.GroupBy(e => e.ExampleId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rawB = b.Result.Examples.GroupBy(e => e.ExampleId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rows = new List<PairQueryRow>(ds.Examples.Count);
        SideDetail Make(LabeledExample e, Dictionary<string, ExampleResult> raw, Dictionary<string, (LabeledExample Example, ExampleResult Result, string Predicted)> pred)
        {
            if (!raw.TryGetValue(e.Id, out var r)) return new SideDetail { Missing = true };
            if (r.Error is not null) return new SideDetail { Error = r.Error, LatencyMs = r.LatencyMs };
            var p = pred.TryGetValue(e.Id, out var x) ? x.Predicted : r.PredictedLabel;
            return new SideDetail { Predicted = p ?? (r.Score is { } s ? s.ToString("0.000") : null), Score = r.Score, LatencyMs = r.LatencyMs,
                Quality = e.Label is null || p is null ? null : p == e.Label ? 1 : 0 };
        }
        foreach (var e in ds.Examples)
        {
            ct.ThrowIfCancellationRequested();
            var sa = Make(e, rawA, predA); var sb = Make(e, rawB, predB);
            var flags = QueryFlags.None;
            if (e.Label is null) flags |= QueryFlags.Unjudged;
            if (sa.Missing || sa.Error is not null || sb.Missing || sb.Error is not null) flags |= QueryFlags.Error;
            if (sa.Predicted is not null && sb.Predicted is not null && sa.Predicted != sb.Predicted) flags |= QueryFlags.Disagreement;
            if (ds.Task == EvalTask.Safety && ds.PositiveLabel is { } pos && e.Label == pos && (sa.Predicted != pos || sb.Predicted != pos)) flags |= QueryFlags.SafetyConcern;
            rows.Add(new PairQueryRow
            {
                Id = e.Id, Text = includeText ? e.Text : null, ImagePath = includeText ? e.ImagePath : null, Label = e.Label, Judged = e.Label is not null,
                A = sa, B = sb, Outcome = Outcome(sa.Quality, sb.Quality), Flags = flags,
                Explanation = e.Label is null ? "No label: correctness is unknown." : sa.Missing || sb.Missing || sa.Error is not null || sb.Error is not null ? "At least one model has no prediction: outcome unknown." : null
            });
        }
        return rows;
    }

    private static QueryOutcome Outcome(double? a, double? b) =>
        a is not { } x || b is not { } y ? QueryOutcome.Unknown : Math.Abs(x - y) < TieMargin ? QueryOutcome.Tie : x > y ? QueryOutcome.AWins : QueryOutcome.BWins;

    private static SideCounts Counts(EvalDataset ds, MetricContext ctx, List<PairQueryRow> rows, bool isA, bool retrieval)
    {
        var sides = rows.Select(r => isA ? r.A : r.B).ToList();
        return new SideCounts
        {
            Total = rows.Count,
            Judged = rows.Count(r => r.Judged),
            Unjudged = rows.Count(r => !r.Judged),
            Evaluated = sides.Count(s => !s.Missing && s.Error is null),
            Retained = retrieval ? sides.Sum(s => s.Retained.Count) : 0,
            Filtered = retrieval ? sides.Sum(s => s.Filtered) : 0,
            Empty = retrieval ? sides.Count(s => s.State == "Empty") : 0,
            Failed = sides.Count(s => s.Missing || s.Error is not null),
            Missing = sides.Count(s => s.Missing)
        };
    }

    private static List<PerformanceRow> Performance(PairSide a, PairSide b, SideCounts ca, SideCounts cb)
    {
        static string Basis(PairSide s) => s.Result.Performance.Measured ? "measured" : "recorded/imported";
        var basis = $"A {Basis(a)}, B {Basis(b)}";
        var pa = a.Result.Performance; var pb = b.Result.Performance;
        var ua = pa.Usage; var ub = pb.Usage;
        var rows = new List<PerformanceRow>
        {
            new("Mean latency (ms)", pa.MeanLatencyMs, pb.MeanLatencyMs, Direction.LowerIsBetter, "0.0", basis),
            new("P95 latency (ms)", pa.P95LatencyMs, pb.P95LatencyMs, Direction.LowerIsBetter, "0.0", basis),
            new("Error rate", ca.Total == 0 ? null : (double)ca.Failed / ca.Total, cb.Total == 0 ? null : (double)cb.Failed / cb.Total, Direction.LowerIsBetter, "0.000", "failed or missing items / items"),
            new("Requests", ua?.Requests, ub?.Requests, Direction.Neutral, "0", "provider-reported"),
            new("Input tokens", ua?.InputTokens, ub?.InputTokens, Direction.Neutral, "0", "provider-reported"),
            new("Output tokens", ua?.OutputTokens, ub?.OutputTokens, Direction.Neutral, "0", "provider-reported"),
            new("Transient retries", ua?.Retries, ub?.Retries, Direction.LowerIsBetter, "0", "provider-reported"),
            new("Estimated cost", ua?.EstimatedCost, ub?.EstimatedCost, Direction.LowerIsBetter, "0.0000", "only from explicitly configured prices")
        };
        return rows;
    }

    private static List<string> Limitations(EvalDataset ds, PairSide a, PairSide b, SideCounts ca, SideCounts cb, List<PairQueryRow> rows, PairSettings s, bool retrieval)
    {
        var l = new List<string>();
        int unjudged = rows.Count(r => !r.Judged);
        if (unjudged > 0) l.Add($"{unjudged} of {rows.Count} {(retrieval ? "queries have no relevance judgment" : "examples have no label")}; they are shown as unknown and excluded from quality metrics.");
        if (retrieval)
        {
            int unjRetained = rows.Sum(r => r.A.UnjudgedRetained + r.B.UnjudgedRetained);
            if (unjRetained > 0) l.Add($"{unjRetained} retained results have no judgment; quality metrics treat them as non-relevant and harm metrics exclude them (never assumed safe).");
            if (!ds.HasSafetyJudgments) l.Add("The dataset has no safety judgments; harm metrics are not available.");
        }
        if (ca.Failed > 0 || cb.Failed > 0) l.Add($"Failed or missing items: A {ca.Failed}, B {cb.Failed}. Partial results are compared; failures are not scored as zero quality.");
        if (s.SampledIds is { } ids) l.Add($"Sample limit: {ids.Count} item(s) were evaluated; results may not generalise to the full dataset.");
        foreach (var (n, side) in new[] { ("A", a), ("B", b) })
        {
            foreach (var w in side.Result.Warnings.Distinct()) l.Add($"Model {n}: {w}");
            if (side.Result.Performance.Usage?.EstimatedCost is null) l.Add($"Model {n}: cost is unknown (no price configured or not reported).");
            if (side.Origin is ResultOrigin.LoadedEvalKitCloud or ResultOrigin.LoadedEvalKitLocal)
                l.Add($"Model {n}: metrics are recalculated by this application from captured EvalKit scores; EvalKit's own metric definitions may differ slightly (e.g. unjudged-query handling).");
        }
        if (ds.IsSynthetic) l.Add("The dataset is synthetic; results do not reflect real-world quality.");
        l.Add("Results apply to this dataset version and configuration only; they do not establish that either model is better in general.");
        return l;
    }

    private static PairRecommendation Recommend(PairSide a, PairSide b, List<PairMetricRow> metrics, List<PairQueryRow> rows, string? primary,
        List<string> blocking, List<string> warnings, SideCounts ca, SideCounts cb)
    {
        if (blocking.Count > 0)
            return new PairRecommendation { Headline = "No recommendation — the runs are not comparable.", Detail = string.Join(" ", blocking), Confidence = ConfidenceLevel.None, ConfidenceReason = "Eligibility rules failed." };
        if (primary is null)
            return new PairRecommendation { Headline = "No recommendation — no shared quality metric could be computed.", Detail = "Judgments or labels are missing for the evaluated items.", Confidence = ConfidenceLevel.None, ConfidenceReason = "No judged items." };

        var pm = metrics.First(m => m.Definition.Id == primary);
        int wins = rows.Count(r => r.Outcome == QueryOutcome.AWins), losses = rows.Count(r => r.Outcome == QueryOutcome.BWins), ties = rows.Count(r => r.Outcome == QueryOutcome.Tie);
        double? p = wins + losses > 0 ? SignTest(wins, losses) : null;
        var nameA = a.Configuration.DisplayName; var nameB = b.Configuration.DisplayName;
        var harm = metrics.FirstOrDefault(m => m.Definition.Category == MetricCategory.Harm && m.A?.Value is not null && m.B?.Value is not null);
        string quality = pm.Better switch
        {
            "A" => $"Model A ({nameA}) scored higher on {pm.Definition.Name}",
            "B" => $"Model B ({nameB}) scored higher on {pm.Definition.Name}",
            _ => $"Models A and B are within {TieMargin} on {pm.Definition.Name}"
        };
        var detail = $"{quality}: {Fmt(pm.A?.Value, pm.Definition.Format)} vs {Fmt(pm.B?.Value, pm.Definition.Format)} (Δ {Signed(pm.Delta, pm.Definition.Format)}). " +
            $"Per-item: A better on {wins}, B better on {losses}, ties {ties}, unknown {rows.Count - wins - losses - ties}" +
            (p is { } pv ? $"; two-sided sign test p = {pv:0.0000}." : ".");
        bool tradeOff = false;
        if (harm is not null)
        {
            detail += $" {harm.Definition.Name} (lower is better): A {Fmt(harm.A!.Value, harm.Definition.Format)}, B {Fmt(harm.B!.Value, harm.Definition.Format)}.";
            tradeOff = pm.Better is "A" or "B" && harm.Better is "A" or "B" && harm.Better != pm.Better;
            if (tradeOff) detail += " The quality leader has more harmful exposure — this is a trade-off that needs a human decision.";
        }
        string headline = pm.Better switch
        {
            _ when tradeOff => "Trade-off: one model leads on quality, the other on safety.",
            "A" => $"Evidence favours Model A ({nameA}) on this dataset and configuration.",
            "B" => $"Evidence favours Model B ({nameB}) on this dataset and configuration.",
            _ => "No meaningful quality difference on this dataset and configuration."
        };
        var judged = Math.Min(ca.Judged, cb.Judged);
        ConfidenceLevel level; string reason;
        if (p is null || judged < 10) { level = ConfidenceLevel.Low; reason = $"Only {judged} judged item(s) with a decisive per-item outcome."; }
        else if (p < 0.01 && judged >= 30) { level = ConfidenceLevel.High; reason = $"{judged} judged items; sign test p < 0.01."; }
        else if (p < 0.05) { level = ConfidenceLevel.Medium; reason = $"{judged} judged items; sign test p < 0.05."; }
        else { level = ConfidenceLevel.Low; reason = $"Per-item differences are not statistically significant (p = {p:0.000})."; }
        if (warnings.Count > 0 && level > ConfidenceLevel.Low) { level--; reason += " Lowered one level because the comparison is not fully like-for-like."; }
        if (ca.Failed + cb.Failed > rows.Count / 10 && level > ConfidenceLevel.Low) { level--; reason += " Lowered because more than 10% of items failed."; }
        if (pm.Better is "Tie" or null && level == ConfidenceLevel.High) reason += " (High confidence that differences are small.)";
        return new PairRecommendation { Headline = headline, Detail = detail, Confidence = level, ConfidenceReason = reason, SignTestP = p };
    }

    /// <summary>Exact two-sided sign test on decisive (non-tie) items.</summary>
    public static double SignTest(int wins, int losses)
    {
        int n = wins + losses, k = Math.Min(wins, losses);
        if (n == 0) return 1;
        double logHalfN = n * Math.Log(0.5), sum = 0, logC = 0;
        for (int i = 0; i <= k; i++)
        {
            if (i > 0) logC += Math.Log(n - i + 1) - Math.Log(i);
            sum += Math.Exp(logC + logHalfN);
        }
        return Math.Min(1, 2 * sum);
    }

    public static string Fmt(double? v, string format = "0.000") => v is { } x ? x.ToString(format, System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    public static string Signed(double? v, string format = "0.000") => v is { } x ? (x > 0 ? "+" : "") + x.ToString(format, System.Globalization.CultureInfo.InvariantCulture) : "unknown";
}
