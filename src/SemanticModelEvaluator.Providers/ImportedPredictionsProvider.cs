using System.Security.Cryptography;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;

namespace SemanticModelEvaluator.Providers;

/// <summary>
/// Treats a predictions/scores file produced elsewhere (any architecture: VLM, generative, safety classifier, reranker…)
/// as an evaluable model. Identity is the file's SHA-256 so a changed file is never silently substituted.
/// </summary>
public sealed class ImportedPredictionsProvider(SettingsStore settings) : IModelProvider
{
    public const string ProviderId = "imported";
    public string Id => ProviderId;
    public string DisplayName => "Imported predictions";
    public bool UsesNetwork => false;
    public string PrivacyNotice => "Reads local prediction files only.";

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProviderHealth.Ok($"{settings.Current.ImportedPredictions.Count} imported file(s)"));

    public ModelDescriptor Register(string path, string name, EvalTask task, string family = "Imported", string notes = "")
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Predictions file not found.", full);
        settings.Update(s =>
        {
            s.ImportedPredictions.RemoveAll(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
            s.ImportedPredictions.Add(new ImportedPredictionsEntry { Name = name, Path = full, Task = task.ToString(), Family = family, Notes = notes });
        });
        return Describe(name, full, task, family, notes);
    }

    public void Unregister(string path) =>
        settings.Update(s => s.ImportedPredictions.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<ModelDescriptor>> DiscoverAsync(ModelQuery query, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<ModelDescriptor>>(() => [.. settings.Current.ImportedPredictions.Select(e =>
            Describe(e.Name, e.Path, Enum.TryParse<EvalTask>(e.Task, out var t) ? t : EvalTask.Retrieval, e.Family, e.Notes))], cancellationToken);

    public static string HashFile(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    public static ModelDescriptor Describe(string name, string path, EvalTask task, string family = "Imported", string notes = "")
    {
        bool exists = File.Exists(path);
        var hash = exists ? HashFile(path) : null;
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var d = new ModelDescriptor
        {
            Identity = new ModelIdentity(ProviderId, slug, hash is null ? "missing" : "sha256-" + hash[..12], hash),
            DisplayName = name,
            ProviderName = "Imported predictions",
            Family = family,
            Tasks = [task],
            InputModality = task == EvalTask.ImageTextRetrieval ? Modality.Multimodal : Modality.Text,
            OutputModality = task is EvalTask.Retrieval or EvalTask.Reranking or EvalTask.ImageTextRetrieval or EvalTask.SemanticSimilarity ? Modality.Scores : Modality.Label,
            RuntimeRequirements = "None (predictions computed elsewhere)",
            License = "n/a (outputs only)",
            IsLocal = true,
            Readiness = exists ? ReadinessState.Ready : ReadinessState.Error,
            ReadinessDetail = exists ? "Import-only · results read from " + Path.GetFileName(path) : "Predictions file missing: " + path,
            Execution = ExecutionKind.ImportedPredictions,
            LocalPath = path
        };
        if (notes.Length > 0) d.Properties["notes"] = notes;
        return d;
    }

    public Task<ValidationReport> ValidateAsync(ModelDescriptor model, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var r = new ValidationReport();
        if (model.LocalPath is null || !File.Exists(model.LocalPath)) return r.Error("Predictions file not found.");
        var hash = HashFile(model.LocalPath);
        if (model.Identity.ContentHash is { } h && h != hash) r.Error("Predictions file content changed since it was imported.");
        else r.Info("Predictions file present and unchanged.");
        r.Warn("Imported predictions: latency/throughput are only available if the file contains latency_ms.");
        return r;
    }, cancellationToken);

    public Task<IModelRunner> LoadAsync(ModelDescriptor model, RuntimeOptions options, CancellationToken cancellationToken) =>
        Task.FromResult<IModelRunner>(new Runner(model));

    private sealed class Runner(ModelDescriptor model) : IImportedPredictionsRunner
    {
        public ModelDescriptor Model { get; } = model;
        public RuntimeInfo Runtime { get; } = new("Imported", "n/a", 0);
        public string PredictionsPath => Model.LocalPath!;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
