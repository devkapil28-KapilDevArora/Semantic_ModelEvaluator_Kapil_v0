using System.Text;
using SemanticModelEvaluator.Core.Models;
using SemanticModelEvaluator.Core.Providers;

namespace SemanticModelEvaluator.Providers;

/// <summary>
/// Deterministic lexical hashing embedders. They are real (if simple) bag-of-features models used as baselines and
/// for repeatable tests. They are always flagged synthetic so their numbers are never presented as neural-model quality.
/// </summary>
public sealed class DeterministicProvider : IModelProvider
{
    public const string ProviderId = "deterministic";
    public string Id => ProviderId;
    public string DisplayName => "Deterministic baselines";
    public bool UsesNetwork => false;
    public string PrivacyNotice => "Runs entirely in-process. No data leaves the machine.";

    public static readonly (string Id, string Name, string Family, Func<string, IEnumerable<string>> Features)[] Variants =
    [
        ("hash-word-256", "Hashed word unigrams (256-d)", "Lexical baseline", Words),
        ("hash-char3-256", "Hashed character trigrams (256-d)", "Lexical baseline", CharTrigrams),
    ];

    public Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderHealth.Ok("Built in"));

    public Task<IReadOnlyList<ModelDescriptor>> DiscoverAsync(ModelQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ModelDescriptor>>([.. Variants.Select(v => Describe(v.Id))]);

    public static ModelDescriptor Describe(string id)
    {
        var v = Variants.First(x => x.Id == id);
        return new ModelDescriptor
        {
            Identity = new ModelIdentity(ProviderId, v.Id, "1", "deterministic-" + v.Id + "-v1"),
            DisplayName = v.Name,
            ProviderName = "Built-in",
            Family = v.Family,
            Tasks = [EvalTask.Retrieval, EvalTask.SemanticSimilarity],
            InputModality = Modality.Text,
            OutputModality = Modality.Embedding,
            EmbeddingDimension = 256,
            RuntimeRequirements = "In-process, CPU, no download",
            License = "Built-in",
            IsLocal = true,
            Readiness = ReadinessState.Ready,
            ReadinessDetail = "Ready (synthetic baseline — not a neural model)",
            Execution = ExecutionKind.Deterministic,
            IsSynthetic = true
        };
    }

    public Task<ValidationReport> ValidateAsync(ModelDescriptor model, CancellationToken cancellationToken) =>
        Task.FromResult(Variants.Any(v => v.Id == model.Identity.ModelId)
            ? new ValidationReport().Info("Deterministic baseline is available.").Warn("Synthetic baseline: not representative of neural model quality.")
            : new ValidationReport().Error("Unknown deterministic model."));

    public Task<IModelRunner> LoadAsync(ModelDescriptor model, RuntimeOptions options, CancellationToken cancellationToken)
    {
        var v = Variants.FirstOrDefault(x => x.Id == model.Identity.ModelId);
        if (v.Id is null) throw new InvalidOperationException("Unknown deterministic model.");
        return Task.FromResult<IModelRunner>(new Runner(model, v.Features));
    }

    private static IEnumerable<string> Words(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static IEnumerable<string> CharTrigrams(string text)
    {
        foreach (var w in Words(text))
        {
            var p = "#" + w + "#";
            for (int i = 0; i + 3 <= p.Length; i++) yield return p.Substring(i, 3);
        }
    }

    /// <summary>FNV-1a — stable across processes and .NET versions (unlike string.GetHashCode).</summary>
    internal static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    public static float[] Embed(string text, Func<string, IEnumerable<string>> features, int dim = 256)
    {
        var v = new float[dim];
        foreach (var f in features(text))
        {
            uint h = Fnv(f);
            v[h % (uint)dim] += (h & 0x80000000) != 0 ? -1f : 1f;
        }
        double n = Math.Sqrt(v.Sum(x => (double)x * x));
        if (n > 0) for (int i = 0; i < dim; i++) v[i] = (float)(v[i] / n);
        return v;
    }

    private sealed class Runner(ModelDescriptor model, Func<string, IEnumerable<string>> features) : ITextEmbeddingRunner
    {
        public ModelDescriptor Model { get; } = model;
        public RuntimeInfo Runtime { get; } = new("Deterministic (in-process)", "CPU", 0);
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(texts.Select(t => Embed(t, features)).ToArray());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
