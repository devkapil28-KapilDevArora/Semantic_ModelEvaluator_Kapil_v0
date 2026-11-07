using SemanticModelEvaluator.Core.Models;

namespace SemanticModelEvaluator.Core.Providers;

/// <summary>A source of models. Providers are optional: a missing provider reports health instead of throwing.</summary>
public interface IModelProvider
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>True when discovery or acquisition may contact a remote service.</summary>
    bool UsesNetwork { get; }
    /// <summary>Human-readable statement of what leaves the machine when this provider is used.</summary>
    string PrivacyNotice { get; }

    Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ModelDescriptor>> DiscoverAsync(ModelQuery query, CancellationToken cancellationToken);
    Task<ValidationReport> ValidateAsync(ModelDescriptor model, CancellationToken cancellationToken);
    Task<IModelRunner> LoadAsync(ModelDescriptor model, RuntimeOptions options, CancellationToken cancellationToken);
}

/// <summary>Providers that can download/prepare models.</summary>
public interface IAcquiringModelProvider : IModelProvider
{
    Task<ModelDescriptor> AcquireAsync(ModelDescriptor model, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken);
}

public interface IModelRunner : IAsyncDisposable
{
    ModelDescriptor Model { get; }
    RuntimeInfo Runtime { get; }
}

public interface ITextEmbeddingRunner : IModelRunner
{
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken);
}

/// <summary>Thrown for authentication/authorization failures. Never retried as a transient error and never answered by switching identity.</summary>
public sealed class AccessDeniedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Runners that call a remote service and can report normalized usage (requests, tokens, retries, correlation ids).</summary>
public interface IUsageReportingRunner : IModelRunner
{
    Evaluation.UsageStats Usage { get; }
}

/// <summary>Runner that does not execute; evaluation reads predictions that were produced elsewhere.</summary>
public interface IImportedPredictionsRunner : IModelRunner
{
    string PredictionsPath { get; }
}

public sealed class ProviderRegistry(IEnumerable<IModelProvider> providers)
{
    private readonly List<IModelProvider> providers = [.. providers];

    public IReadOnlyList<IModelProvider> Providers => providers;

    public IModelProvider? Find(string id) => providers.FirstOrDefault(p => p.Id == id);

    public IModelProvider Get(string id) => Find(id) ?? throw new MissingProviderException(id);

    public async Task<IReadOnlyList<(IModelProvider Provider, ProviderHealth Health)>> HealthAsync(CancellationToken ct)
    {
        var result = new List<(IModelProvider, ProviderHealth)>();
        foreach (var p in providers)
        {
            ProviderHealth health;
            try { health = await p.CheckHealthAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { health = new(false, "Error", Security.Redactor.Redact(ex.Message)); }
            result.Add((p, health));
        }
        return result;
    }

    /// <summary>Discovers models from every healthy provider; failures in one provider never hide others.</summary>
    public async Task<(IReadOnlyList<ModelDescriptor> Models, IReadOnlyList<string> Errors)> DiscoverAllAsync(ModelQuery query, CancellationToken ct)
    {
        var models = new List<ModelDescriptor>();
        var errors = new List<string>();
        foreach (var p in providers)
        {
            if (p.UsesNetwork && !query.AllowNetwork && p is not ILocalCacheAwareProvider) continue;
            try { models.AddRange(await p.DiscoverAsync(query, ct).ConfigureAwait(false)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add($"{p.DisplayName}: {Security.Redactor.Redact(ex.Message)}"); }
        }
        return (models, errors);
    }
}

/// <summary>Marker for network providers that can still list already-cached models while offline.</summary>
public interface ILocalCacheAwareProvider { }

public sealed class MissingProviderException(string providerId)
    : Exception($"Model provider '{providerId}' is not available in this installation.")
{
    public string ProviderId { get; } = providerId;
}
