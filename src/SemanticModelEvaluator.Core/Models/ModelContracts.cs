using System.Text.Json.Serialization;

namespace SemanticModelEvaluator.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<EvalTask>))]
public enum EvalTask
{
    Retrieval,
    SemanticSimilarity,
    Classification,
    ImageTextRetrieval,
    Reranking,
    Generative,
    Safety
}

[JsonConverter(typeof(JsonStringEnumConverter<Modality>))]
public enum Modality { Text, Image, Multimodal, Embedding, Scores, Label, GeneratedText }

[JsonConverter(typeof(JsonStringEnumConverter<ReadinessState>))]
public enum ReadinessState
{
    Ready,
    NeedsDownload,
    NotInstalled,
    RequiresAuthentication,
    Unsupported,
    ImportOnly,
    Error
}

[JsonConverter(typeof(JsonStringEnumConverter<ExecutionKind>))]
public enum ExecutionKind { NativeOnnx, ExternalProcess, ImportedPredictions, Deterministic, None }

[JsonConverter(typeof(JsonStringEnumConverter<EmbeddingRole>))]
public enum EmbeddingRole { Query, Document }

/// <summary>Exact identity of a model. Workspaces record this and never substitute another revision.</summary>
public sealed record ModelIdentity(string ProviderId, string ModelId, string Revision, string? ContentHash = null)
{
    [JsonIgnore]
    public string Key => $"{ProviderId}:{ModelId}@{Revision}";

    public override string ToString() => Key;
}

public sealed class ModelDescriptor
{
    public required ModelIdentity Identity { get; init; }
    public required string DisplayName { get; set; }
    public string ProviderName { get; set; } = "";
    public string Family { get; set; } = "";
    public List<EvalTask> Tasks { get; set; } = [];
    public Modality InputModality { get; set; } = Modality.Text;
    public Modality OutputModality { get; set; } = Modality.Embedding;
    public int? EmbeddingDimension { get; set; }
    public string? Quantization { get; set; }
    public string RuntimeRequirements { get; set; } = "";
    public long? DownloadSizeBytes { get; set; }
    public string License { get; set; } = "unknown";
    public bool IsLocal { get; set; }
    public ReadinessState Readiness { get; set; }
    public string ReadinessDetail { get; set; } = "";
    public ExecutionKind Execution { get; set; }
    public string? LocalPath { get; set; }
    public string? SourceUrl { get; set; }
    public bool IsGated { get; set; }
    /// <summary>True for synthetic/deterministic baselines whose results must not be presented as real model performance.</summary>
    public bool IsSynthetic { get; set; }
    public Dictionary<string, string> Properties { get; set; } = [];

    [JsonIgnore]
    public bool CanExecute => Readiness == ReadinessState.Ready &&
        Execution is ExecutionKind.NativeOnnx or ExecutionKind.ExternalProcess or ExecutionKind.Deterministic or ExecutionKind.ImportedPredictions;

    public bool Supports(EvalTask task) => Tasks.Contains(task);

    public ModelDescriptor Clone() => new()
    {
        Identity = Identity,
        DisplayName = DisplayName,
        ProviderName = ProviderName,
        Family = Family,
        Tasks = [.. Tasks],
        InputModality = InputModality,
        OutputModality = OutputModality,
        EmbeddingDimension = EmbeddingDimension,
        Quantization = Quantization,
        RuntimeRequirements = RuntimeRequirements,
        DownloadSizeBytes = DownloadSizeBytes,
        License = License,
        IsLocal = IsLocal,
        Readiness = Readiness,
        ReadinessDetail = ReadinessDetail,
        Execution = Execution,
        LocalPath = LocalPath,
        SourceUrl = SourceUrl,
        IsGated = IsGated,
        IsSynthetic = IsSynthetic,
        Properties = new(Properties)
    };
}

public sealed record ProviderHealth(bool Available, string Status, string Detail = "")
{
    public static ProviderHealth Ok(string detail = "") => new(true, "Available", detail);
    public static ProviderHealth Missing(string detail) => new(false, "Not installed", detail);
}

public sealed record ValidationIssue(ValidationSeverity Severity, string Message);

[JsonConverter(typeof(JsonStringEnumConverter<ValidationSeverity>))]
public enum ValidationSeverity { Info, Warning, Error }

public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; } = [];
    public bool IsValid => Issues.All(i => i.Severity != ValidationSeverity.Error);
    public ValidationReport Info(string m) { Issues.Add(new(ValidationSeverity.Info, m)); return this; }
    public ValidationReport Warn(string m) { Issues.Add(new(ValidationSeverity.Warning, m)); return this; }
    public ValidationReport Error(string m) { Issues.Add(new(ValidationSeverity.Error, m)); return this; }
    public override string ToString() => string.Join(Environment.NewLine, Issues.Select(i => $"{i.Severity}: {i.Message}"));
}

public sealed record RuntimeOptions(string Device = "CPU", int Threads = 0, int BatchSize = 16, int MaxSequenceLength = 256);

public sealed record RuntimeInfo(string RuntimeName, string Device, double LoadMilliseconds, string Detail = "");

public sealed record ProgressInfo(string Message, double? Fraction = null);

public sealed record ModelQuery(string? Text = null, EvalTask? Task = null, Modality? InputModality = null, bool AllowNetwork = false);
