using System.Text.Json.Nodes;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Providers.EvalKit;

namespace SemanticModelEvaluator.Providers;

/// <summary>
/// PSAPI adapter boundary.
/// REAL: discovery of PSAPI model/version pairs that were captured by EvalKit (cloud result bundles already downloaded
/// and verified locally). Those runs are compared through the EvalKit result loader, not by calling PSAPI.
/// BLOCKED (undocumented provider contract): live invocation. The authorized ASG-SearchPlatform sources only expose PSAPI
/// as an EvalKit backend/version dimension (Experiments/&lt;exp&gt;/psapi-&lt;ver&gt;/...); no endpoint, request schema or
/// authentication flow is documented, so none is fabricated here. <see cref="LoadAsync"/> always throws.
/// </summary>
public sealed class PsapiProvider : IModelProvider
{
    public const string ProviderId = "psapi";
    public const string BlockedReason =
        "Live PSAPI invocation is blocked: no PSAPI endpoint, request schema or authentication contract is documented in the authorized sources. " +
        "Compare PSAPI models by loading captured EvalKit results (cloud bundle or local run folder).";
    private readonly Func<IReadOnlyList<(string Folder, string Label)>> bundles;

    public PsapiProvider() : this(() => EvalKitCloudService.CachedBundles()) { }
    public PsapiProvider(Func<IReadOnlyList<(string Folder, string Label)>> bundles) => this.bundles = bundles;

    public string Id => ProviderId;
    public string DisplayName => "PSAPI (captured EvalKit runs)";
    public bool UsesNetwork => false;
    public string PrivacyNotice => "Discovery reads locally cached, hash-verified EvalKit result bundles only. Nothing is sent to PSAPI.";

    public static ModelDescriptor Describe(string model, string psapiVersion, string? folder = null) => new()
    {
        Identity = new ModelIdentity(ProviderId, model, "psapi-" + psapiVersion),
        DisplayName = $"{model} (psapi-{psapiVersion})",
        ProviderName = "PSAPI",
        Family = "PSAPI backend captured by EvalKit",
        Tasks = [EvalTask.Retrieval],
        InputModality = Modality.Text,
        OutputModality = Modality.Scores,
        RuntimeRequirements = "Captured EvalKit results",
        Readiness = ReadinessState.ImportOnly,
        ReadinessDetail = "Captured results available. " + BlockedReason,
        Execution = ExecutionKind.None,
        LocalPath = folder,
        Properties = new() { ["psapi_version"] = psapiVersion, ["live_invocation"] = "blocked: undocumented contract" }
    };

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken ct) =>
        Task.FromResult(new ProviderHealth(true, "Captured results only", BlockedReason));

    public Task<IReadOnlyList<ModelDescriptor>> DiscoverAsync(ModelQuery query, CancellationToken ct)
    {
        var found = new Dictionary<string, ModelDescriptor>();
        foreach (var (folder, _) in bundles())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "result-manifest.json")));
                if (o?["model"]?.GetValue<string>() is not { Length: > 0 } model || o["psapi_version"]?.GetValue<string>() is not { Length: > 0 } v) continue;
                var d = Describe(model, v, folder);
                if (string.IsNullOrWhiteSpace(query.Text) || d.DisplayName.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                    found.TryAdd(d.Identity.Key, d);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        return Task.FromResult<IReadOnlyList<ModelDescriptor>>([.. found.Values]);
    }

    public Task<ValidationReport> ValidateAsync(ModelDescriptor model, CancellationToken ct) =>
        Task.FromResult(new ValidationReport().Info("Captured EvalKit results can be compared.").Error(BlockedReason));

    public Task<IModelRunner> LoadAsync(ModelDescriptor model, RuntimeOptions options, CancellationToken ct) =>
        throw new NotSupportedException(BlockedReason);
}
