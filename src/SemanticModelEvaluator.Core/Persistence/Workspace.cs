using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticModelEvaluator.Core.Comparison;
using SemanticModelEvaluator.Core.Datasets;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Persistence;

public sealed class WorkspaceViewState
{
    public string ActiveView { get; set; } = "Explorer";
    public string? FocusModelKey { get; set; }
    public string? FocusScenarioId { get; set; }
    public string? ActiveRunId { get; set; }
    public List<string> SelectedMetricIds { get; set; } = [];
    public string? CurveMetricId { get; set; }
    public int K { get; set; } = 10;
    public bool LinkThresholds { get; set; }
    /// <summary>Per-model threshold (model key → threshold; null = no threshold).</summary>
    public Dictionary<string, double?> Thresholds { get; set; } = [];
    public List<string> VisibleModelKeys { get; set; } = [];
    public string QueryFilter { get; set; } = "";
    public string FailureFilter { get; set; } = "All";
    public string ComparatorTab { get; set; } = "Grid";
    public List<string> ComparedScenarioIds { get; set; } = [];
    public string? RankingPolicyName { get; set; }
    public Dictionary<string, double> RankingWeights { get; set; } = [];
}

public sealed class Workspace
{
    public string Format { get; set; } = "smeval";
    public int FormatVersion { get; set; } = 1;
    public string Application { get; set; } = AppPaths.AppName;
    public string ApplicationVersion { get; set; } = typeof(Workspace).Assembly.GetName().Version?.ToString() ?? "0.1";
    public string Name { get; set; } = "Untitled workspace";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
    public string Notes { get; set; } = "";
    /// <summary>Exact model identities used. Restoring never substitutes a different revision.</summary>
    public List<ModelDescriptor> Models { get; set; } = [];
    public DatasetReference? Dataset { get; set; }
    /// <summary>Cached results (IDs, scores, latencies only). Dataset text is re-read from the dataset source.</summary>
    public List<EvaluationRun> Runs { get; set; } = [];
    public List<Scenario> Scenarios { get; set; } = [];
    public string? ReferenceScenarioId { get; set; }
    public WorkspaceViewState View { get; set; } = new();
    /// <summary>Paths of imported prediction files keyed by model key.</summary>
    public Dictionary<string, string> PredictionFiles { get; set; } = [];
}

public static class WorkspaceSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(Workspace w) => JsonSerializer.Serialize(w, Options);

    public static Workspace Deserialize(string json)
    {
        var w = JsonSerializer.Deserialize<Workspace>(json, Options) ?? throw new InvalidDataException("Empty workspace file.");
        if (w.Format != "smeval") throw new InvalidDataException("Not a Semantic_ModelEvaluator workspace (.smeval).");
        if (w.FormatVersion > 1) throw new InvalidDataException($"Workspace format {w.FormatVersion} is newer than this application supports.");
        return w;
    }
}

public sealed record WorkspaceLoadResult(Workspace Workspace, EvalDataset? Dataset, IReadOnlyList<ValidationIssue> Warnings);

public static class WorkspaceService
{
    public static void Save(Workspace workspace, string path)
    {
        if (!path.EndsWith(AppPaths.WorkspaceExtension, StringComparison.OrdinalIgnoreCase)) path += AppPaths.WorkspaceExtension;
        workspace.SavedAt = DateTimeOffset.Now;
        AtomicFile.WriteAllText(path, WorkspaceSerializer.Serialize(workspace));
    }

    public static Workspace Duplicate(Workspace workspace, string newName)
    {
        var copy = WorkspaceSerializer.Deserialize(WorkspaceSerializer.Serialize(workspace));
        copy.Name = newName;
        copy.CreatedAt = DateTimeOffset.Now;
        return copy;
    }

    /// <summary>Opens a workspace and reports (never silently repairs) missing or changed dependencies.</summary>
    public static WorkspaceLoadResult Open(string path, Func<ModelIdentity, ModelDescriptor?>? resolveModel = null)
    {
        var w = WorkspaceSerializer.Deserialize(File.ReadAllText(path));
        var warnings = new List<ValidationIssue>();
        EvalDataset? dataset = null;
        if (w.Dataset is { } d)
        {
            if (d.SourcePath is null || !File.Exists(d.SourcePath))
                warnings.Add(new(ValidationSeverity.Warning, $"Dataset '{d.Name}' v{d.Version} not found at '{d.SourcePath}'. Cached scores are preserved; metric recalculation requires the original judgments."));
            else
            {
                try
                {
                    var loaded = DatasetLoader.LoadManifest(d.SourcePath);
                    if (loaded.ContentHash != d.ContentHash)
                        warnings.Add(new(ValidationSeverity.Warning, $"Dataset '{d.Name}' has changed since the workspace was saved (content hash differs). Results may not correspond to the current data."));
                    else dataset = loaded;
                }
                catch (Exception ex) { warnings.Add(new(ValidationSeverity.Warning, $"Dataset could not be loaded: {ex.Message}")); }
            }
        }
        foreach (var m in w.Models)
        {
            if (m.Execution == ExecutionKind.ImportedPredictions)
            {
                if (m.LocalPath is not null && !File.Exists(m.LocalPath))
                    warnings.Add(new(ValidationSeverity.Info, $"Predictions file for '{m.DisplayName}' is missing; cached results are still available."));
                continue;
            }
            var current = resolveModel?.Invoke(m.Identity);
            if (current is null)
                warnings.Add(new(ValidationSeverity.Warning, $"Model '{m.DisplayName}' ({m.Identity.Key}) is not available on this machine. Cached results are shown; re-running requires that exact model."));
            else if (current.Identity.ContentHash is not null && m.Identity.ContentHash is not null && current.Identity.ContentHash != m.Identity.ContentHash)
                warnings.Add(new(ValidationSeverity.Warning, $"Model '{m.DisplayName}' files differ from the saved identity. It will not be substituted."));
        }
        foreach (var s in w.Scenarios)
        {
            var run = w.Runs.FirstOrDefault(r => r.RunId == s.RunId);
            if (run is null || run.Find(s.ModelKey) is null)
                warnings.Add(new(ValidationSeverity.Warning, $"Scenario '{s.Name}' refers to results that are not in this workspace."));
        }
        return new WorkspaceLoadResult(w, dataset, warnings);
    }
}
