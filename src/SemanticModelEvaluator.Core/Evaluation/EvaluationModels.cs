using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Evaluation;

[JsonConverter(typeof(JsonStringEnumConverter<RunStatus>))]
public enum RunStatus { Pending, Running, Completed, CompletedWithErrors, Failed, Cancelled }

public sealed record ScoredDocument(string DocumentId, double Score);

/// <summary>Cached retrieval output for one query. Holds only identifiers and scores — never dataset text.</summary>
public sealed class QueryResult
{
    public required string QueryId { get; init; }
    public List<ScoredDocument> Results { get; init; } = [];
    public double? LatencyMs { get; set; }
    public string? Error { get; set; }
}

public sealed class ExampleResult
{
    public required string ExampleId { get; init; }
    /// <summary>Positive-class probability / similarity score.</summary>
    public double? Score { get; set; }
    public string? PredictedLabel { get; set; }
    public string? Output { get; set; }
    public double? LatencyMs { get; set; }
    public string? Error { get; set; }
}

public sealed class PerformanceStats
{
    public double? LoadMs { get; set; }
    public double? MeanLatencyMs { get; set; }
    public double? P50LatencyMs { get; set; }
    public double? P95LatencyMs { get; set; }
    public double? ThroughputPerSecond { get; set; }
    public double? DocumentEncodeMs { get; set; }
    public long? PeakWorkingSetBytes { get; set; }
    public string Device { get; set; } = "";
    public string Runtime { get; set; } = "";
    /// <summary>True when values were measured by this application (false for imported predictions).</summary>
    public bool Measured { get; set; }
    /// <summary>Provider-reported usage; null when the provider does not report it (never assumed zero).</summary>
    public UsageStats? Usage { get; set; }
}

/// <summary>Normalized provider usage. Each field is null when unreported. Cost is only set from explicitly configured prices.</summary>
public sealed class UsageStats
{
    public long? Requests { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? FailedRequests { get; set; }
    public long? Retries { get; set; }
    public double? EstimatedCost { get; set; }
    public string? CostBasis { get; set; }
    /// <summary>Last provider correlation id (request id) — never payloads.</summary>
    public string? LastCorrelationId { get; set; }
}

public sealed class ModelRunResult
{
    public required ModelIdentity Model { get; init; }
    public string DisplayName { get; set; } = "";
    public bool IsSynthetic { get; set; }
    public ExecutionKind Execution { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<QueryResult> Queries { get; set; } = [];
    public List<ExampleResult> Examples { get; set; } = [];
    public PerformanceStats Performance { get; set; } = new();
    public int FailedItems { get; set; }
    /// <summary>True when some or all scores are −rank because the source recorded positions only; thresholds then act as rank cut-offs.</summary>
    public bool ScoresIncludeRanks { get; set; }

    [JsonIgnore] public bool HasResults => Queries.Count > 0 || Examples.Count > 0;

    public IEnumerable<double> AllScores() =>
        Queries.SelectMany(q => q.Results.Select(r => r.Score)).Concat(Examples.Where(e => e.Score.HasValue).Select(e => e.Score!.Value));
}

public sealed class EvaluationConfig
{
    public int K { get; set; } = 10;
    /// <summary>Number of ranked results cached per query; thresholds and K are applied to this cache.</summary>
    public int RetainDepth { get; set; } = 100;
    public int BatchSize { get; set; } = 16;
    public int MaxConcurrency { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 1800;
    public bool UseEmbeddingCache { get; set; } = true;
    public string Device { get; set; } = "CPU";
    public List<string> MetricIds { get; set; } = [];
    public Dictionary<string, string> MetricVersions { get; set; } = [];
    public string? OutputDirectory { get; set; }
}

public sealed record EnvironmentInfo(string Machine, string Os, string Architecture, int LogicalProcessors, string Cpu, string DotNet)
{
    public static EnvironmentInfo Capture() => new(
        Environment.MachineName.Length > 0 ? "local" : "",
        RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.ProcessorCount,
        Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
        RuntimeInformation.FrameworkDescription);

    public override string ToString() => $"{Cpu}; {LogicalProcessors} logical processors; {Architecture}; {Os}; {DotNet}";
}

public sealed class EvaluationRun
{
    public string RunId { get; set; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    public string Name { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public EvalTask Task { get; set; }
    public required DatasetReference Dataset { get; set; }
    public EvaluationConfig Config { get; set; } = new();
    public EnvironmentInfo Environment { get; set; } = EnvironmentInfo.Capture();
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public List<ModelRunResult> Models { get; set; } = [];

    public ModelRunResult? Find(string modelKey) => Models.FirstOrDefault(m => m.Model.Key == modelKey);
}
