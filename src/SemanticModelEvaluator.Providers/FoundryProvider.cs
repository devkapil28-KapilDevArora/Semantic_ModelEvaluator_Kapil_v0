using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticModelEvaluator.Core.Evaluation;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Persistence;
using SemanticModelEvaluator.Core.Providers;
using SemanticModelEvaluator.Core.Security;
using SemanticModelEvaluator.Providers.Azure;

namespace SemanticModelEvaluator.Providers;

/// <summary>
/// Microsoft AI Foundry / Azure OpenAI embedding deployments. Deployments are configuration references only
/// (endpoint + deployment name); there is no remote catalog call. Authentication defaults to Entra ID via
/// DefaultAzureCredential (interactive browser excluded). Dataset text IS sent to the configured endpoint during evaluation,
/// so the UI requires explicit confirmation before a run.
/// </summary>
public sealed class FoundryProvider(HttpClient http, NetworkPolicy network, ISecretStore secrets, SettingsStore settings,
    Func<FoundryDeployment, IAzureTokenSource>? tokenSourceFactory = null) : IModelProvider
{
    public const string ProviderId = "foundry";
    private readonly Func<FoundryDeployment, IAzureTokenSource> tokens =
        tokenSourceFactory ?? (d => new AzureTokenSource(AzureIdentityMode.Default, d.TenantId));

    public string Id => ProviderId;
    public string DisplayName => "Microsoft AI Foundry";
    public bool UsesNetwork => true;
    public string PrivacyNotice => "Evaluation sends query and document text to the configured Azure AI endpoint. Discovery lists local configuration only.";

    public static ValidationReport ValidateConfiguration(FoundryDeployment d)
    {
        var r = new ValidationReport();
        if (string.IsNullOrWhiteSpace(d.Name)) r.Error("Name is required.");
        if (!Uri.TryCreate(d.Endpoint?.TrimEnd('/'), UriKind.Absolute, out var uri)) { r.Error("Endpoint must be an absolute https URL."); return r; }
        if (uri.Scheme != Uri.UriSchemeHttps) r.Error("Endpoint must use https.");
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            r.Error("Endpoint must not contain a port, query, fragment or user info (keys belong in Credential Manager, never in URLs).");
        if (!uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
            r.Error("Endpoint host must end with .openai.azure.com or .services.ai.azure.com.");
        if (string.IsNullOrWhiteSpace(d.Deployment)) r.Error("Deployment name is required.");
        if (d.ApiStyle is not ("OpenAIv1" or "AzureOpenAIDeployment")) r.Error("API style must be OpenAIv1 or AzureOpenAIDeployment.");
        if (d.Auth is not ("EntraId" or "ApiKey")) r.Error("Auth must be EntraId or ApiKey.");
        if (d.Auth == "EntraId" && !Uri.TryCreate(d.TokenScope, UriKind.Absolute, out _)) r.Error("Token scope must be an absolute URI ending in /.default.");
        if (string.IsNullOrWhiteSpace(d.ModelVersion)) r.Warn("Model name/version is not declared; the comparison will show the deployment name only.");
        if (d.PricePer1KInputTokens is { } p && (!double.IsFinite(p) || p < 0)) r.Error("Price must be a non-negative number.");
        return r;
    }

    public static ModelDescriptor Describe(FoundryDeployment d)
    {
        var report = ValidateConfiguration(d);
        var revision = string.IsNullOrWhiteSpace(d.ModelVersion) ? "deployment" : d.ModelVersion;
        return new ModelDescriptor
        {
            Identity = new ModelIdentity(ProviderId, $"{d.Endpoint.TrimEnd('/')}/{d.Deployment}", revision),
            DisplayName = d.Name,
            ProviderName = "Microsoft AI Foundry",
            Family = "Azure AI embedding deployment",
            Tasks = [EvalTask.Retrieval, EvalTask.SemanticSimilarity],
            InputModality = Modality.Text,
            OutputModality = Modality.Embedding,
            EmbeddingDimension = d.Dimensions,
            RuntimeRequirements = $"Remote {d.ApiStyle}; {(d.Auth == "EntraId" ? "Entra ID (DefaultAzureCredential)" : "API key from Credential Manager")}",
            License = "Per Azure subscription terms",
            Readiness = report.IsValid ? ReadinessState.Ready : ReadinessState.Error,
            ReadinessDetail = report.IsValid ? "Configured. Use Validate to test sign-in and the endpoint." : report.ToString(),
            Execution = ExecutionKind.ExternalProcess,
            SourceUrl = d.Endpoint,
            Properties = new()
            {
                ["remote_inference"] = "true",
                ["deployment"] = d.Deployment,
                ["endpoint"] = d.Endpoint,
                ["api_style"] = d.ApiStyle,
                ["model_version"] = d.ModelVersion,
                ["auth"] = d.Auth
            }
        };
    }

    private FoundryDeployment Find(ModelDescriptor model) =>
        settings.Current.FoundryDeployments.FirstOrDefault(d => Describe(d).Identity.ModelId == model.Identity.ModelId)
        ?? throw new InvalidOperationException($"Foundry deployment '{model.DisplayName}' is no longer configured.");

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken ct) =>
        Task.FromResult(new ProviderHealth(true, network.OfflineMode ? "Offline (cannot invoke)" : "Configured",
            $"{settings.Current.FoundryDeployments.Count} deployment(s) configured"));

    public Task<IReadOnlyList<ModelDescriptor>> DiscoverAsync(ModelQuery query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelDescriptor>>([.. settings.Current.FoundryDeployments.Select(Describe)
            .Where(m => string.IsNullOrWhiteSpace(query.Text) || m.DisplayName.Contains(query.Text, StringComparison.OrdinalIgnoreCase))]);

    /// <summary>Validates configuration and, when online, sends one fixed non-dataset probe string to confirm sign-in and access.</summary>
    public async Task<ValidationReport> ValidateAsync(ModelDescriptor model, CancellationToken ct)
    {
        FoundryDeployment d;
        try { d = Find(model); } catch (Exception ex) { return new ValidationReport().Error(ex.Message); }
        var report = ValidateConfiguration(d);
        if (!report.IsValid || network.OfflineMode) return network.OfflineMode ? report.Warn("Offline mode: connection not tested.") : report;
        try
        {
            await using var runner = new FoundryRunner(model, d, http, network, tokens(d), secrets);
            var v = await runner.EmbedAsync(["connection test"], EmbeddingRole.Query, ct).ConfigureAwait(false);
            report.Info($"Connected. Embedding dimension {v[0].Length}. Correlation id {runner.Usage.LastCorrelationId ?? "n/a"}.");
            if (d.Dimensions is { } expected && expected != v[0].Length) report.Error($"Configured dimension {expected} differs from returned {v[0].Length}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { report.Error(Redactor.Redact(ex.Message)); }
        return report;
    }

    public Task<IModelRunner> LoadAsync(ModelDescriptor model, RuntimeOptions options, CancellationToken ct)
    {
        var d = Find(model);
        var report = ValidateConfiguration(d);
        if (!report.IsValid) throw new InvalidOperationException(report.ToString());
        return Task.FromResult<IModelRunner>(new FoundryRunner(model, d, http, network, tokens(d), secrets));
    }
}

public sealed class FoundryRunner(ModelDescriptor model, FoundryDeployment d, HttpClient http, NetworkPolicy network,
    IAzureTokenSource tokens, ISecretStore secrets) : ITextEmbeddingRunner, IUsageReportingRunner
{
    private const int MaxAttempts = 4;
    private readonly object gate = new();
    public ModelDescriptor Model { get; } = model;
    public RuntimeInfo Runtime { get; } = new("Azure AI Foundry (" + d.ApiStyle + ")", "Remote", 0, d.Endpoint);
    public UsageStats Usage { get; } = new() { Requests = 0, FailedRequests = 0, Retries = 0 };
    /// <summary>Test hook: replaces delays between retries.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public Uri RequestUri => d.ApiStyle == "AzureOpenAIDeployment"
        ? new($"{d.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(d.Deployment)}/embeddings?api-version={Uri.EscapeDataString(d.ApiVersion)}")
        : new($"{d.Endpoint.TrimEnd('/')}/openai/v1/embeddings");

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
    {
        if (texts.Count == 0) return [];
        var uri = RequestUri;
        network.EnsureAllowed("Foundry embeddings", uri);
        var body = new JsonObject { ["input"] = new JsonArray([.. texts.Select(t => (JsonNode)t)]) };
        if (d.ApiStyle == "OpenAIv1") body["model"] = d.Deployment;
        var payload = body.ToJsonString();
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            var correlation = Guid.NewGuid().ToString();
            request.Headers.Add("x-ms-client-request-id", correlation);
            if (d.Auth == "ApiKey")
                request.Headers.Add("api-key", secrets.Read(d.KeyName) ?? throw new AccessDeniedException($"No API key stored for '{d.Name}'. Add it in Provider settings or switch to Entra ID."));
            else
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(d.TokenScope, ct).ConfigureAwait(false));
            HttpResponseMessage response;
            lock (gate) Usage.Requests++;
            try { response = await http.SendAsync(request, ct).ConfigureAwait(false); }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                lock (gate) Usage.Retries++;
                await Delay(Backoff(attempt, null), ct).ConfigureAwait(false);
                continue;
            }
            using (response)
            {
                var serviceId = response.Headers.TryGetValues("apim-request-id", out var ids) ? ids.FirstOrDefault()
                    : response.Headers.TryGetValues("x-request-id", out var xs) ? xs.FirstOrDefault() : correlation;
                lock (gate) Usage.LastCorrelationId = serviceId;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    lock (gate) Usage.FailedRequests++;
                    throw new AccessDeniedException($"Access denied ({(int)response.StatusCode}) by {uri.Host} for deployment '{d.Deployment}'. " +
                        "Confirm your identity has the 'Cognitive Services OpenAI User' (or Azure AI User) role on the resource. Correlation id " + serviceId + ".");
                }
                if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
                {
                    lock (gate) Usage.Retries++;
                    await Delay(Backoff(attempt, response.Headers.RetryAfter?.Delta), ct).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    lock (gate) Usage.FailedRequests++;
                    throw new HttpRequestException($"{uri.Host} returned {(int)response.StatusCode} {response.ReasonPhrase} for deployment '{d.Deployment}'. Correlation id {serviceId}.", null, response.StatusCode);
                }
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Parse(json, texts.Count);
            }
        }
    }

    private float[][] Parse(string json, int expected)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (JsonException) { throw new InvalidDataException("Endpoint returned invalid JSON."); }
        var data = root?["data"] as JsonArray ?? throw new InvalidDataException("Response has no 'data' array.");
        if (data.Count != expected) throw new InvalidDataException($"Expected {expected} embeddings, received {data.Count}.");
        var output = new float[expected][];
        for (int i = 0; i < data.Count; i++)
        {
            var item = data[i]!;
            int index = item["index"]?.GetValue<int>() ?? i;
            if (index < 0 || index >= expected || output[index] is not null) throw new InvalidDataException("Response embedding indexes are invalid.");
            var vector = (item["embedding"] as JsonArray ?? throw new InvalidDataException("Embedding is not a numeric array (base64 output is not requested).")).Select(x => x!.GetValue<float>()).ToArray();
            if (vector.Length == 0 || vector.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Embedding contains no values or non-finite values.");
            output[index] = vector;
        }
        if (output.Select(v => v.Length).Distinct().Count() != 1) throw new InvalidDataException("Embedding dimensions differ within one response.");
        if (root!["usage"] is JsonObject usage)
        {
            lock (gate)
            {
                if (usage["prompt_tokens"]?.GetValue<long>() is { } p)
                {
                    Usage.InputTokens = (Usage.InputTokens ?? 0) + p;
                    if (d.PricePer1KInputTokens is { } price)
                    {
                        Usage.EstimatedCost = Usage.InputTokens / 1000.0 * price;
                        Usage.CostBasis = $"Configured price {price} per 1K input tokens (not a billing record).";
                    }
                }
            }
        }
        return output;
    }

    public static bool IsTransient(HttpStatusCode code) => code is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static TimeSpan Backoff(int attempt, TimeSpan? retryAfter) =>
        retryAfter is { } r && r > TimeSpan.Zero && r < TimeSpan.FromMinutes(1) ? r : TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
