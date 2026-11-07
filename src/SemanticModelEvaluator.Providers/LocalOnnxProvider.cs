using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Providers.Onnx;

namespace SemanticModelEvaluator.Providers;

/// <summary>User-registered folders containing a BERT-family sentence-embedding ONNX model.</summary>
public sealed class LocalOnnxProvider(SettingsStore settings) : IModelProvider
{
    public const string ProviderId = "local-onnx";
    public string Id => ProviderId;
    public string DisplayName => "Local ONNX";
    public bool UsesNetwork => false;
    public string PrivacyNotice => "Runs on this PC with ONNX Runtime (CPU). No data leaves the machine.";

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProviderHealth.Ok($"{settings.Current.LocalModelFolders.Count} registered folder(s)"));

    public void Register(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        settings.Update(s => { if (!s.LocalModelFolders.Contains(full, StringComparer.OrdinalIgnoreCase)) s.LocalModelFolders.Add(full); });
    }

    public void Unregister(string folder) =>
        settings.Update(s => s.LocalModelFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<ModelDescriptor>> DiscoverAsync(ModelQuery query, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<ModelDescriptor>>(() => [.. settings.Current.LocalModelFolders.Select(f => Describe(f, ProviderId, null))], cancellationToken);

    /// <summary>Builds a descriptor for any ONNX folder (also used by the Hugging Face cache).</summary>
    public static ModelDescriptor Describe(string folder, string providerId, string? modelId)
    {
        if (!Directory.Exists(folder))
        {
            return new ModelDescriptor
            {
                Identity = new ModelIdentity(providerId, modelId ?? Path.GetFileName(folder), "missing"),
                DisplayName = modelId ?? Path.GetFileName(folder),
                ProviderName = providerId == ProviderId ? "Local ONNX" : providerId,
                Readiness = ReadinessState.Error,
                ReadinessDetail = "Folder no longer exists: " + folder,
                Execution = ExecutionKind.None,
                LocalPath = folder
            };
        }
        var info = OnnxModelFolder.Inspect(folder);
        var hash = info.IsSupported ? info.ComputeContentHash() : null;
        var id = modelId ?? info.Manifest?.RepoId ?? Path.GetFileName(folder.TrimEnd('\\', '/'));
        var revision = info.Manifest?.Revision is { Length: > 0 } r && r != "local" ? r : hash is null ? "local" : "sha256-" + hash[..12];
        var d = new ModelDescriptor
        {
            Identity = new ModelIdentity(providerId, id, revision, hash),
            DisplayName = id,
            ProviderName = providerId == ProviderId ? "Local ONNX" : "Hugging Face (cached)",
            Family = info.ModelType ?? "unknown",
            Tasks = [EvalTask.Retrieval, EvalTask.SemanticSimilarity],
            InputModality = Modality.Text,
            OutputModality = Modality.Embedding,
            EmbeddingDimension = info.HiddenSize,
            Quantization = info.Quantization,
            RuntimeRequirements = "ONNX Runtime CPU (bundled)",
            DownloadSizeBytes = info.OnnxPath is null ? null : new FileInfo(info.OnnxPath).Length,
            License = info.Manifest?.License ?? "unknown (check model folder)",
            IsLocal = true,
            Readiness = info.IsSupported ? ReadinessState.Ready : ReadinessState.Unsupported,
            ReadinessDetail = info.IsSupported ? $"Ready · {info.Pooling} pooling · max {info.MaxLength} tokens" : string.Join(" ", info.Problems),
            Execution = info.IsSupported ? ExecutionKind.NativeOnnx : ExecutionKind.None,
            LocalPath = folder
        };
        if (info.Manifest?.QueryPrefix is { Length: > 0 } qp) d.Properties["queryPrefix"] = qp;
        if (info.Manifest?.DocumentPrefix is { Length: > 0 } dp) d.Properties["documentPrefix"] = dp;
        if (info.OnnxPath is not null) d.Properties["onnxFile"] = Path.GetRelativePath(folder, info.OnnxPath);
        d.Properties["pooling"] = info.Pooling.ToString();
        return d;
    }

    public Task<ValidationReport> ValidateAsync(ModelDescriptor model, CancellationToken cancellationToken) => Task.Run(() => ValidateFolder(model), cancellationToken);

    public static ValidationReport ValidateFolder(ModelDescriptor model)
    {
        var r = new ValidationReport();
        if (model.LocalPath is null || !Directory.Exists(model.LocalPath)) return r.Error("Model folder not found.");
        var info = OnnxModelFolder.Inspect(model.LocalPath);
        foreach (var p in info.Problems) r.Error(p);
        if (!info.IsSupported) return r;
        var hash = info.ComputeContentHash();
        if (model.Identity.ContentHash is { } expected && expected != hash)
            r.Error($"Content hash changed (expected {expected[..12]}…, found {hash[..12]}…). The workspace refers to a different model version.");
        try
        {
            var runner = new OnnxEmbeddingRunner(model, info, new RuntimeOptions());
            try
            {
                var v = runner.EmbedAsync(["validation probe"], EmbeddingRole.Query, CancellationToken.None).GetAwaiter().GetResult();
                r.Info($"Loaded in {runner.Runtime.LoadMilliseconds:F0} ms; output dimension {v[0].Length}.");
                if (v[0].Any(float.IsNaN)) r.Error("Model produced NaN values.");
            }
            finally { runner.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        catch (Exception ex) { r.Error("Inference failed: " + ex.Message); }
        if (model.License.StartsWith("unknown")) r.Warn("License unknown — confirm usage rights before sharing results.");
        return r;
    }

    public Task<IModelRunner> LoadAsync(ModelDescriptor model, RuntimeOptions options, CancellationToken cancellationToken) => LoadFolderAsync(model, options, cancellationToken);

    public static Task<IModelRunner> LoadFolderAsync(ModelDescriptor model, RuntimeOptions options, CancellationToken cancellationToken) => Task.Run<IModelRunner>(() =>
    {
        if (model.LocalPath is null) throw new InvalidOperationException("Model has no local folder.");
        var info = OnnxModelFolder.Inspect(model.LocalPath);
        if (model.Identity.ContentHash is { } expected && info.IsSupported && info.ComputeContentHash() != expected)
            throw new InvalidOperationException("Model files changed since this model was registered; refusing to substitute a different version.");
        return new OnnxEmbeddingRunner(model, info, options);
    }, cancellationToken);
}
